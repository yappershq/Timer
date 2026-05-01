using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;

namespace Source2Surf.Timer.Managers.Patch;

internal readonly record struct MemoryPatch(nint Address, byte[] OriginalBytes);

internal interface IPatchManager
{
    unsafe void Apply(byte* address, ReadOnlySpan<byte> patchBytes, string description);
}

internal class PatchManager : IManager, IPatchManager
{
    private readonly InterfaceBridge        _bridge;
    private readonly ILogger<PatchManager>  _logger;
    private readonly List<MemoryPatch>      _patches = [];

    public PatchManager(InterfaceBridge bridge, ILogger<PatchManager> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public bool Init() => true;

    public unsafe void Apply(byte* address, ReadOnlySpan<byte> patchBytes, string description)
    {
        var nativeAddress = (nint)address;
        var size          = patchBytes.Length;
        var originalBytes = new ReadOnlySpan<byte>(address, size).ToArray();

        _patches.Add(new MemoryPatch(nativeAddress, originalBytes));

        _bridge.ModSharp.SetMemoryAccess(nativeAddress,
                                         size,
                                         MemoryAccess.Read | MemoryAccess.Write | MemoryAccess.Execute,
                                         out var originalAccess);

        patchBytes.CopyTo(new Span<byte>(address, size));

        _bridge.ModSharp.SetMemoryAccess(nativeAddress, size, originalAccess);

        _logger.LogInformation("Applied patch: {Description} at 0x{Address:X} ({Size} bytes)", description, (ulong)nativeAddress, size);
    }

    public unsafe void Shutdown()
    {
        for (var i = _patches.Count - 1; i >= 0; i--)
        {
            var patch   = _patches[i];
            var address = (byte*) patch.Address;
            var size    = patch.OriginalBytes.Length;

            _bridge.ModSharp.SetMemoryAccess(patch.Address,
                                             size,
                                             MemoryAccess.Read | MemoryAccess.Write | MemoryAccess.Execute);

            patch.OriginalBytes.CopyTo(new Span<byte>(address, size));

            _bridge.ModSharp.SetMemoryAccess(patch.Address, size, MemoryAccess.Read | MemoryAccess.Execute);

            _logger.LogInformation("Restored patch at 0x{Address:X} ({Size} bytes)", (ulong) patch.Address, size);
        }

        _patches.Clear();
    }
}
