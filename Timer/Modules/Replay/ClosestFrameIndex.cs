/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using BitOperations = System.Numerics.BitOperations;
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Modules.Replay;

/// <summary>
///     Spatial index over replay frame positions for "closest frame to player" lookups
///     (used for live time-difference HUDs).
///     Bucketed k-d tree with AVX2 leaf evaluation: O(log n) traversal, SIMD distance
///     computation in leaf buckets. See Timer.Benchmarks results — KdTreeSimd dominates
///     the realistic surf workload while keeping memory low (~16 bytes/frame).
/// </summary>
internal sealed class ClosestFrameIndex
{
    // Sized to fit one Vector512<float> exactly; AVX2 hardware folds the leaf
    // into two 8-wide passes, which is still cheaper than a third tree level.
    private static readonly int BucketSize = Vector512<float>.Count;

    // Top recursion levels are dispatched to Parallel.Invoke. After NthElement
    // partitions a range, the two child ranges read/write disjoint slices of the
    // arrays so they can build concurrently. Past depth 3 (= up to 8 leaves) the
    // remaining subtrees are too small for the per-task overhead to pay off.
    private const int ParallelDepth = 3;
    private const int ParallelMin   = 2048;

    private readonly float[] _xs;
    private readonly float[] _ys;
    private readonly float[] _zs;
    private readonly int[]   _orig;

    public int FrameCount => _xs.Length;

    public ClosestFrameIndex(IReadOnlyList<ReplayFrameData> frames)
    {
        var n = frames.Count;
        _xs   = new float[n];
        _ys   = new float[n];
        _zs   = new float[n];
        _orig = new int[n];

        for (var i = 0; i < n; i++)
        {
            var o = frames[i].Origin;
            _xs[i]   = o.X;
            _ys[i]   = o.Y;
            _zs[i]   = o.Z;
            _orig[i] = i;
        }

        BuildRec(0, n, 0, 0);
    }

    /// <summary>
    ///     Returns the index of the replay frame whose Origin is closest to <paramref name="position" />,
    ///     or -1 if the index is empty.
    /// </summary>
    public int FindClosest(in Vector position, out float distSq)
    {
        var n = _xs.Length;
        if (n == 0)
        {
            distSq = float.PositiveInfinity;
            return -1;
        }

        var bestSq = float.PositiveInfinity;
        var bestI  = -1;
        SearchRec(0, n, 0, position.X, position.Y, position.Z, ref bestSq, ref bestI);

        distSq = bestSq;
        return bestI < 0 ? -1 : _orig[bestI];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NextAxis(int axis) => axis == 2 ? 0 : axis + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float[] AxisArray(int axis) => axis == 0 ? _xs : axis == 1 ? _ys : _zs;

    private void BuildRec(int lo, int hi, int axis, int depth)
    {
        var count = hi - lo;
        if (count <= BucketSize)
        {
            return;
        }

        var leftSize = LeftSubtreeSize(count);
        var mid      = lo + leftSize;

        // Pass the axis array directly so partition's hot loop reduces to arr[i],
        // eliminating the per-element axis dispatch in GetAxis.
        NthElement(lo, hi, mid, AxisArray(axis));

        var next = NextAxis(axis);
        if (depth < ParallelDepth && count > ParallelMin)
        {
            // Subtree ranges are disjoint after partition, so they can build concurrently.
            // Long replays (e.g. 1-hour runs ~= 230k frames) see this scale near-linearly
            // up to the parallel-depth cap.
            Parallel.Invoke(
                () => BuildRec(lo,      mid, next, depth + 1),
                () => BuildRec(mid + 1, hi,  next, depth + 1));
        }
        else
        {
            BuildRec(lo,      mid, next, depth + 1);
            BuildRec(mid + 1, hi,  next, depth + 1);
        }
    }

    private void SearchRec(int lo, int hi, int axis, float qx, float qy, float qz, ref float bestSq, ref int bestIdx)
    {
        var count = hi - lo;
        if (count <= 0)
        {
            return;
        }

        if (count <= BucketSize)
        {
            ScanLeaf(lo, hi, qx, qy, qz, ref bestSq, ref bestIdx);
            return;
        }

        var leftSize = LeftSubtreeSize(count);
        var mid      = lo + leftSize;

        var px = _xs[mid];
        var py = _ys[mid];
        var pz = _zs[mid];
        var dx = px - qx;
        var dy = py - qy;
        var dz = pz - qz;
        var d  = (dx * dx) + (dy * dy) + (dz * dz);
        if (d < bestSq)
        {
            bestSq  = d;
            bestIdx = mid;
        }

        var diff = axis == 0 ? (qx - px) : axis == 1 ? (qy - py) : (qz - pz);

        int nearLo, nearHi, farLo, farHi;
        if (diff < 0f)
        {
            nearLo = lo;
            nearHi = mid;
            farLo  = mid + 1;
            farHi  = hi;
        }
        else
        {
            nearLo = mid + 1;
            nearHi = hi;
            farLo  = lo;
            farHi  = mid;
        }

        var next = NextAxis(axis);
        SearchRec(nearLo, nearHi, next, qx, qy, qz, ref bestSq, ref bestIdx);

        if (diff * diff < bestSq)
        {
            SearchRec(farLo, farHi, next, qx, qy, qz, ref bestSq, ref bestIdx);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ScanLeaf(int lo, int hi, float qx, float qy, float qz, ref float bestSq, ref int bestIdx)
    {
        // Take refs to array data once; SIMD LoadUnsafe + Unsafe.Add avoids the
        // bounds check the JIT cannot eliminate from indexed access on the AVX-512
        // path, which is the hot path for full BucketSize=16 leaves.
        ref var xRef = ref MemoryMarshal.GetArrayDataReference(_xs);
        ref var yRef = ref MemoryMarshal.GetArrayDataReference(_ys);
        ref var zRef = ref MemoryMarshal.GetArrayDataReference(_zs);
        var i = lo;

        // AVX-512 path (Vector512<float>.Count = 16 floats / iter). One full BucketSize leaf
        // in a single iteration. Runtime gate; on hardware without AVX-512 this branch is
        // cold and not taken.
        if (Vector512.IsHardwareAccelerated && hi - i >= Vector512<float>.Count)
        {
            var qxv = Vector512.Create(qx);
            var qyv = Vector512.Create(qy);
            var qzv = Vector512.Create(qz);

            Span<float> dists = stackalloc float[Vector512<float>.Count];

            while (i + Vector512<float>.Count <= hi)
            {
                var x  = Vector512.LoadUnsafe(ref Unsafe.Add(ref xRef, (nint) i));
                var y  = Vector512.LoadUnsafe(ref Unsafe.Add(ref yRef, (nint) i));
                var z  = Vector512.LoadUnsafe(ref Unsafe.Add(ref zRef, (nint) i));
                var dx = Vector512.Subtract(x, qxv);
                var dy = Vector512.Subtract(y, qyv);
                var dz = Vector512.Subtract(z, qzv);
                var dv = Vector512.Add(
                    Vector512.Add(
                        Vector512.Multiply(dx, dx),
                        Vector512.Multiply(dy, dy)),
                    Vector512.Multiply(dz, dz));

                // Skip the per-lane scan entirely when no lane can improve the current best.
                // bestSq decreases monotonically as the search progresses, so most leaves
                // visited deep in the tree never beat it.
                var bestVec = Vector512.Create(bestSq);
                if (Vector512.LessThanAny(dv, bestVec))
                {
                    dv.StoreUnsafe(ref dists[0]);
                    for (var j = 0; j < Vector512<float>.Count; j++)
                    {
                        var dvj = dists[j];
                        if (dvj < bestSq)
                        {
                            bestSq  = dvj;
                            bestIdx = i + j;
                        }
                    }
                }

                i += Vector512<float>.Count;
            }
        }

        // AVX2 path (Vector256<float>.Count = 8 floats / iter). Covers either the full leaf
        // on AVX2-only hardware, or the remainder left over by the AVX-512 pass.
        if (Vector256.IsHardwareAccelerated && hi - i >= Vector256<float>.Count)
        {
            var qxv = Vector256.Create(qx);
            var qyv = Vector256.Create(qy);
            var qzv = Vector256.Create(qz);

            Span<float> dists = stackalloc float[Vector256<float>.Count];

            while (i + Vector256<float>.Count <= hi)
            {
                var x  = Vector256.LoadUnsafe(ref Unsafe.Add(ref xRef, (nint) i));
                var y  = Vector256.LoadUnsafe(ref Unsafe.Add(ref yRef, (nint) i));
                var z  = Vector256.LoadUnsafe(ref Unsafe.Add(ref zRef, (nint) i));
                var dx = Vector256.Subtract(x, qxv);
                var dy = Vector256.Subtract(y, qyv);
                var dz = Vector256.Subtract(z, qzv);
                var dv = Vector256.Add(
                    Vector256.Add(
                        Vector256.Multiply(dx, dx),
                        Vector256.Multiply(dy, dy)),
                    Vector256.Multiply(dz, dz));

                var bestVec = Vector256.Create(bestSq);
                if (Vector256.LessThanAny(dv, bestVec))
                {
                    dv.StoreUnsafe(ref dists[0]);
                    for (var j = 0; j < Vector256<float>.Count; j++)
                    {
                        var dvj = dists[j];
                        if (dvj < bestSq)
                        {
                            bestSq  = dvj;
                            bestIdx = i + j;
                        }
                    }
                }

                i += Vector256<float>.Count;
            }
        }

        // Scalar tail: < Vector256<float>.Count elements, or no SIMD on ancient hardware.
        for (; i < hi; i++)
        {
            var ex = Unsafe.Add(ref xRef, (nint) i) - qx;
            var ey = Unsafe.Add(ref yRef, (nint) i) - qy;
            var ez = Unsafe.Add(ref zRef, (nint) i) - qz;
            var ds = (ex * ex) + (ey * ey) + (ez * ez);
            if (ds < bestSq)
            {
                bestSq  = ds;
                bestIdx = i;
            }
        }
    }

    private static int LeftSubtreeSize(int n)
    {
        if (n <= 1)
        {
            return 0;
        }

        var h           = Log2Floor(n);
        var leftLastMax = 1 << (h - 1);
        var last        = n - ((1 << h) - 1) - 1;
        var leftLast    = Math.Min(last, leftLastMax);

        return ((1 << (h - 1)) - 1) + leftLast;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Log2Floor(int n) => BitOperations.Log2((uint) n);

    /// <summary>
    ///     Hoare-partition quickselect. Hoare touches only the elements on the "wrong"
    ///     side of the pivot (roughly half), and both inner do/while loops are monotonic,
    ///     so the branch predictor sees almost no mispredicts — markedly faster to build
    ///     than Lomuto while leaving the array in the same partitioned state SearchRec
    ///     relies on. NthElement/SwapAll take cached <c>ref T</c> via
    ///     GetArrayDataReference + Unsafe.Add to elide the partition loop's bounds checks
    ///     (same trick as the leaf scan). All four arrays move in lockstep.
    ///
    ///     Postcondition: arr[k] holds the k-th smallest value on this axis within
    ///     [lo,hi), with [lo,k) &lt;= arr[k] &lt;= (k,hi) — equal-key ordering within a side
    ///     may differ from Lomuto, which does not affect the split index (mid) or query
    ///     results.
    /// </summary>
    private void NthElement(int lo, int hi, int k, float[] arr)
    {
        ref var xs   = ref MemoryMarshal.GetArrayDataReference(_xs);
        ref var ys   = ref MemoryMarshal.GetArrayDataReference(_ys);
        ref var zs   = ref MemoryMarshal.GetArrayDataReference(_zs);
        ref var orig = ref MemoryMarshal.GetArrayDataReference(_orig);
        ref var ar   = ref MemoryMarshal.GetArrayDataReference(arr);

        while (true)
        {
            var len = hi - lo;
            if (len <= 1)
            {
                return;
            }

            // Hoare degenerates on len = 2; handle it directly.
            if (len == 2)
            {
                if (Unsafe.Add(ref ar, (nint) (lo + 1)) < Unsafe.Add(ref ar, (nint) lo))
                {
                    SwapAll(ref xs, ref ys, ref zs, ref orig, lo, lo + 1);
                }

                return;
            }

            // Median-of-three pivot: sort arr[lo], arr[mid], arr[hi-1] so the median
            // sits at mid, then use its value as the pivot (Hoare keys off the value,
            // not a fixed pivot slot).
            var mid = lo + (len >> 1);
            MedianOfThreeAt(ref xs, ref ys, ref zs, ref orig, ref ar, lo, mid, hi - 1);
            var pivot = Unsafe.Add(ref ar, (nint) mid);

            // Hoare partition. After exit: arr[lo..j] <= pivot, arr[j+1..hi-1] >= pivot.
            int i = lo - 1, j = hi;
            while (true)
            {
                do
                {
                    i++;
                } while (Unsafe.Add(ref ar, (nint) i) < pivot);

                do
                {
                    j--;
                } while (Unsafe.Add(ref ar, (nint) j) > pivot);

                if (i >= j)
                {
                    break;
                }

                SwapAll(ref xs, ref ys, ref zs, ref orig, i, j);
            }

            if (k <= j)
            {
                hi = j + 1;
            }
            else
            {
                lo = j + 1;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MedianOfThreeAt(
        ref float xs, ref float ys, ref float zs, ref int orig,
        ref float ar, int a, int b, int c)
    {
        ref var va = ref Unsafe.Add(ref ar, (nint) a);
        ref var vb = ref Unsafe.Add(ref ar, (nint) b);
        ref var vc = ref Unsafe.Add(ref ar, (nint) c);

        if (vb < va)
        {
            SwapAll(ref xs, ref ys, ref zs, ref orig, a, b);
        }

        if (vc < va)
        {
            SwapAll(ref xs, ref ys, ref zs, ref orig, a, c);
        }

        if (vc < vb)
        {
            SwapAll(ref xs, ref ys, ref zs, ref orig, b, c);
        }

        // After: arr[a] <= arr[b] <= arr[c]; median sits at b.
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapAll(
        ref float xs, ref float ys, ref float zs, ref int orig,
        int i, int j)
    {
        ref var xi = ref Unsafe.Add(ref xs,   (nint) i);
        ref var xj = ref Unsafe.Add(ref xs,   (nint) j);
        ref var yi = ref Unsafe.Add(ref ys,   (nint) i);
        ref var yj = ref Unsafe.Add(ref ys,   (nint) j);
        ref var zi = ref Unsafe.Add(ref zs,   (nint) i);
        ref var zj = ref Unsafe.Add(ref zs,   (nint) j);
        ref var oi = ref Unsafe.Add(ref orig, (nint) i);
        ref var oj = ref Unsafe.Add(ref orig, (nint) j);

        (xi, xj) = (xj, xi);
        (yi, yj) = (yj, yi);
        (zi, zj) = (zj, zi);
        (oi, oj) = (oj, oi);
    }
}
