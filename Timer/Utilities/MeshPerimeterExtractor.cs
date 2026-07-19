using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Source2Surf.Timer.Types;

namespace Source2Surf.Timer.Utilities;

internal class MeshPerimeterExtractor
{
    // Vertex snap grid: 0.1 world units. Tight enough to preserve geometry, loose enough
    // to weld vertices that the compiler emitted with slight float drift between parts.
    private const float SnapGridScale = 10f;

#region Main Entry Point

    // Caller retains ownership of `kv` and is responsible for disposing it.
    public static List<Edge> ExtractPerimeterEdges(IKeyValues3 kv, bool includeTop = false)
    {
        var finalEdges = new List<Edge>();

        var m_parts = kv.FindMember("m_parts");

        if (m_parts == null)
            return finalEdges;

        var partCount   = m_parts.GetArrayElementCount();
        var edgeNormals = new Dictionary<Edge, List<Vector>>();
        var ceilZ       = float.MinValue;

        // Shared vertex pool so vertices from different parts that coincide snap to one entry.
        var vertexGrid = new Dictionary<(int, int, int), Vector>();

        Vector Snap(Vector v)
        {
            var key = ((int) MathF.Round(v.X * SnapGridScale),
                       (int) MathF.Round(v.Y * SnapGridScale),
                       (int) MathF.Round(v.Z * SnapGridScale));

            if (vertexGrid.TryGetValue(key, out var existing))
                return existing;

            vertexGrid[key] = v;

            return v;
        }

        for (var p = 0; p < partCount; p++)
        {
            var part      = m_parts.GetArrayElement(p);
            var m_rnShape = part?.FindMember("m_rnShape");

            if (m_rnShape == null)
                continue;

            var m_hulls  = m_rnShape.FindMember("m_hulls");
            var m_meshes = m_rnShape.FindMember("m_meshes");

            var hullCount = m_hulls?.GetArrayElementCount()  ?? 0;
            var meshCount = m_meshes?.GetArrayElementCount() ?? 0;

            for (var i = 0; i < hullCount; i++)
            {
                var hullKv = m_hulls?.GetArrayElement(i)?.FindMember("m_Hull");

                if (hullKv == null)
                    continue;

                if (hullKv.FindMember("m_Bounds")?.FindMember("m_vMaxBounds") is { } maxArr)
                    ceilZ = Math.Max(ceilZ, maxArr.GetArrayElement(2)?.GetFloat() ?? float.MinValue);

                ParseHull(hullKv, edgeNormals, Snap);
            }

            for (var i = 0; i < meshCount; i++)
            {
                var meshKv = m_meshes?.GetArrayElement(i)?.FindMember("m_Mesh");

                if (meshKv == null)
                    continue;

                if (meshKv.FindMember("m_vMax") is { } maxArr)
                    ceilZ = Math.Max(ceilZ, maxArr.GetArrayElement(2)?.GetFloat() ?? float.MinValue);

                ParseMesh(meshKv, edgeNormals, Snap);
            }
        }

        foreach (var (edge, normals) in edgeNormals)
        {
            if (!IsSharpEdge(edge, normals))
                continue;

            if (!includeTop && IsFlatOnCeiling(edge, ceilZ))
                continue;

            finalEdges.Add(edge);
        }

        return finalEdges;
    }

#endregion

#region Parsers

    private static void ParseHull(IKeyValues3 hullKv, Dictionary<Edge, List<Vector>> edgeNormals, Func<Vector, Vector> snap)
    {
        if (hullKv.FindMember("m_VertexPositions") is not { } m_VertexPositions
            || hullKv.FindMember("m_Edges") is not { } m_Edges
            || hullKv.FindMember("m_Faces") is not { } m_Faces)
            return;

        var vBytes = m_VertexPositions.GetBinaryBlob();
        var eBytes = m_Edges.GetBinaryBlob();
        var fBytes = m_Faces.GetBinaryBlob();

        if (vBytes.IsEmpty || eBytes.IsEmpty || fBytes.IsEmpty)
            return;

        var positions = MemoryMarshal.Cast<byte, Vector>(vBytes);
        var edges     = MemoryMarshal.Cast<byte, RnHalfEdge>(eBytes);

        // m_Faces is a flat byte array where each byte is a starting half-edge index for one face.
        foreach (var startEdgeIndex in fBytes)
        {
            if (startEdgeIndex >= edges.Length)
                continue;

            var currentEdgeIndex = startEdgeIndex;
            var faceVertices     = new List<Vector>();

            // First pass: Collect vertices and SNAP them (guard caps iteration so a corrupt chain can't spin forever)
            for (var guard = 0; guard < edges.Length; guard++)
            {
                var currentEdge = edges[currentEdgeIndex];

                if (currentEdge.OriginVertex >= positions.Length)
                    break;

                faceVertices.Add(snap(positions[currentEdge.OriginVertex]));
                currentEdgeIndex = currentEdge.Next;

                if (currentEdgeIndex >= edges.Length || currentEdgeIndex == startEdgeIndex)
                    break;
            }

            if (faceVertices.Count < 3)
                continue;

            var faceNormal = CalculateSurfaceNormal(faceVertices[0], faceVertices[1], faceVertices[2]);

            // Second pass: Map the normal to every edge on this face using snapped vertices
            for (var j = 0; j < faceVertices.Count; j++)
            {
                var v1   = faceVertices[j];
                var v2   = faceVertices[(j + 1) % faceVertices.Count];
                var edge = new Edge(v1, v2);

                if (!edgeNormals.TryGetValue(edge, out var normalsList))
                {
                    normalsList       = new List<Vector>(2);
                    edgeNormals[edge] = normalsList;
                }

                normalsList.Add(faceNormal);
            }
        }
    }

    private static void ParseMesh(IKeyValues3 meshKv, Dictionary<Edge, List<Vector>> edgeNormals, Func<Vector, Vector> snap)
    {
        if (meshKv.FindMember("m_Vertices") is not { } m_Vertices
            || meshKv.FindMember("m_Triangles") is not { } m_Triangles)
            return;

        var vBytes = m_Vertices.GetBinaryBlob();
        var iBytes = m_Triangles.GetBinaryBlob();

        if (vBytes.IsEmpty || iBytes.IsEmpty)
            return;

        var positions = MemoryMarshal.Cast<byte, Vector>(vBytes);
        var indices   = MemoryMarshal.Cast<byte, int>(iBytes);

        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];

            if ((uint) i0 >= (uint) positions.Length
                || (uint) i1 >= (uint) positions.Length
                || (uint) i2 >= (uint) positions.Length)
                continue;

            var v0 = snap(positions[i0]);
            var v1 = snap(positions[i1]);
            var v2 = snap(positions[i2]);

            var faceNormal = CalculateSurfaceNormal(v0, v1, v2);
            var edges      = new[] { new Edge(v0, v1), new Edge(v1, v2), new Edge(v2, v0) };

            foreach (var edge in edges)
            {
                if (!edgeNormals.TryGetValue(edge, out var normalsList))
                {
                    normalsList       = new List<Vector>(2);
                    edgeNormals[edge] = normalsList;
                }

                normalsList.Add(faceNormal);
            }
        }
    }

#endregion

#region Topology Math

    // Opposing-normal cancellation: dot < this means the faces are ~180° apart and form an
    // internal boolean cut introduced by the compiler. Both get removed.
    private const float OpposingNormalDot = -0.98f;

    // Nearly-coplanar threshold. Above this, two faces sharing an edge are considered the
    // same surface — the edge is either a triangulation diagonal or a fine curve slice.
    private const float CoplanarDot = 0.99f;

    // Fully-coplanar threshold. Above this, the surface is truly flat (not curved), so
    // the shared edge is purely a triangulation artifact and gets dropped.
    private const float FlatCoplanarDot = 0.9999f;

    // World-axis alignment tolerance for the "melt diagonals" rule. A flat-surface
    // triangulation diagonal moves significantly along all three axes; a real edge
    // hugs at least one axis within this many units.
    private const float AxisAlignedTolerance = 2.0f;

    // Floor detection: |normal.Z| above this on both faces means we're on a near-horizontal
    // surface, where we aggressively melt zigzag triangulation seams that would otherwise
    // clutter the wireframe.
    private const float FloorNormalZ = 0.95f;

    // Hard-corner threshold on floors — more permissive because floor triangulation tends
    // to create many shallow zigzags that we want to suppress.
    private const float FloorCornerDot = 0.85f;

    // Hard-corner threshold elsewhere — anything not nearly-coplanar is a real corner.
    private const float WallCornerDot = 0.99f;

    private static bool IsSharpEdge(Edge edge, List<Vector> normals)
    {
        if (normals.Count == 0)
            return false;

        if (normals.Count == 1)
            return true; // Exterior boundary

        // Phase 1: cancel out opposing pairs left behind by compiler boolean cuts.
        var  activeNormals = new List<Vector>(normals);
        bool removed;

        do
        {
            removed = false;

            for (var i = 0; i < activeNormals.Count; i++)
            {
                for (var j = i + 1; j < activeNormals.Count; j++)
                {
                    var dot = Dot(activeNormals[i], activeNormals[j]);

                    if (dot < OpposingNormalDot)
                    {
                        activeNormals.RemoveAt(j);
                        activeNormals.RemoveAt(i);
                        removed = true;

                        break;
                    }
                }

                if (removed)
                    break;
            }
        }
        while (removed);

        if (activeNormals.Count == 0)
            return false;

        if (activeNormals.Count == 1)
            return true;

        // Phase 2: filter triangulation artifacts on curved/flat surfaces.
        for (var n1 = 0; n1 < activeNormals.Count; n1++)
        {
            for (var n2 = n1 + 1; n2 < activeNormals.Count; n2++)
            {
                var dot = Dot(activeNormals[n1], activeNormals[n2]);

                if (dot >= CoplanarDot)
                {
                    var dx = MathF.Abs(edge.V2.X - edge.V1.X);
                    var dy = MathF.Abs(edge.V2.Y - edge.V1.Y);
                    var dz = MathF.Abs(edge.V2.Z - edge.V1.Z);

                    // A real edge on a curved surface hugs at least one world axis;
                    // a triangulation diagonal drifts on all three.
                    var isAxisAligned = dx < AxisAlignedTolerance
                                        || dy < AxisAlignedTolerance
                                        || dz < AxisAlignedTolerance;

                    if (!isAxisAligned)
                        continue;

                    // Keep curve slices (in the CoplanarDot..FlatCoplanarDot band);
                    // drop the rest as seams on a flat surface.
                    if (dot < FlatCoplanarDot)
                        return true;
                }
                else
                {
                    var isFloor   = MathF.Abs(activeNormals[n1].Z) > FloorNormalZ
                                    && MathF.Abs(activeNormals[n2].Z) > FloorNormalZ;
                    var threshold = isFloor ? FloorCornerDot : WallCornerDot;

                    if (dot < threshold)
                        return true;
                }
            }
        }

        return false;
    }

    private static float Dot(Vector a, Vector b)
        => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Vector CalculateSurfaceNormal(Vector v0, Vector v1, Vector v2)
    {
        float ux = v1.X - v0.X,
              uy = v1.Y - v0.Y,
              uz = v1.Z - v0.Z;

        float vx = v2.X - v0.X,
              vy = v2.Y - v0.Y,
              vz = v2.Z - v0.Z;

        var nx  = uy * vz - uz * vy;
        var ny  = uz * vx - ux * vz;
        var nz  = ux * vy - uy * vx;
        var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);

        return len > 0.0001f ? new Vector(nx / len, ny / len, nz / len) : new Vector(0, 0, 0);
    }

    private static bool IsFlatOnCeiling(Edge edge, float ceilZ)
        => MathF.Abs(edge.V1.Z - ceilZ) < 0.1f && MathF.Abs(edge.V2.Z - ceilZ) < 0.1f;

#endregion

#region Structs

    // Source 2 RnHull half-edge record. Byte-wide indices cap a single hull at 256 vertices/edges/faces.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RnHalfEdge
    {
        public byte Next;
        public byte Twin;
        public byte OriginVertex;
        public byte Face;
    }

#endregion
}