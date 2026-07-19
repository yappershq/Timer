using System;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Extensions;

namespace Source2Surf.Timer.Modules;

internal partial class MiscModule
{
    private unsafe void PatchTheNavCheck()
    {
        var server = _bridge.Modules.Server;

        // 1. Resolve CCSGameConfiguration::AddModGameSystem address and extract the absolute address of g_fGameOver

        var g_fGameOverAddress = nint.Zero;
        var end                = nint.Zero;

        try
        {
            var CCSGameConfiguration_AddModGameSystem
                = server.FindFunction(["CSGOVScriptGameSystem", "NavGameSystem", "BotGameSystem"]);

            if (CCSGameConfiguration_AddModGameSystem == nint.Zero)
            {
                _logger.LogWarning("Failed to get address for CCSGameConfiguration::AddModGameSystem");

                return;
            }

            if (!server.GetFunctionRange(CCSGameConfiguration_AddModGameSystem, out _, out end))
            {
                _logger.LogWarning("Failed to get function range for CCSGameConfiguration::AddModGameSystem");

                return;
            }

            if (end <= CCSGameConfiguration_AddModGameSystem)
            {
                _logger.LogWarning("Invalid function range for CCSGameConfiguration::AddModGameSystem (end <= start)");

                return;
            }

            var addModFuncLength = (uint) (end - CCSGameConfiguration_AddModGameSystem);
            var addModReader     = new UnsafeCodeReader(CCSGameConfiguration_AddModGameSystem, addModFuncLength);

            var addModDecoder
                = Decoder.Create(64, addModReader, (ulong) CCSGameConfiguration_AddModGameSystem, DecoderOptions.AMD);

            while (addModReader.CanReadByte)
            {
                var instr = addModDecoder.Decode();

                if (instr.IsInvalid)
                {
                    continue;
                }

                // mov byte ptr [rip+disp], 0
                if (instr.Code               == Code.Mov_rm8_imm8
                    && instr.Op0Kind         == OpKind.Memory
                    && instr.MemoryBase      == Register.RIP
                    && instr.GetImmediate(1) == 0)
                {
                    g_fGameOverAddress = (nint) instr.IPRelativeMemoryAddress;

                    break;
                }
            }

            if (g_fGameOverAddress == nint.Zero)
            {
                _logger.LogWarning("Failed to find g_fGameOver address in CCSGameConfiguration::AddModGameSystem, trying method #2");
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Error when trying to get address for g_fGameOver, trying method #2");
        }

        // resolve CCSGameRules::GoToIntermission
        if (g_fGameOverAddress == nint.Zero)
        {
            try
            {
                var mp_chattime = _bridge.ConVarManager.FindConVar("mp_chattime", true)
                                  ?? throw new Exception("Failed to find cvar mp_chattime");

                var mp_chattimePtr = server.FindPtr(mp_chattime.GetAbsPtr());

                if (mp_chattimePtr == nint.Zero)
                    throw new Exception("No pointer(mp_chattime) was found");

                var referencedFunction = server.FindFunction(mp_chattimePtr);

                if (referencedFunction == nint.Zero)
                    throw new Exception("Failed to find any function that references mp_chattime");

                if (!server.GetFunctionRange(referencedFunction, out var funcStart, out var funcEnd))
                    throw new Exception("Failed to get function range");

                if (funcEnd <= funcStart)
                    throw new Exception("Invalid function range (end <= start)");

                var length     = (uint) (funcEnd - funcStart);
                var codeReader = new UnsafeCodeReader(funcStart, length);

                var intermissionDecoder = Decoder.Create(64, codeReader, (ulong) funcStart, DecoderOptions.AMD);

                while (codeReader.CanReadByte)
                {
                    var instr = intermissionDecoder.Decode();

                    if (instr.IsInvalid)
                    {
                        continue;
                    }

                    if (instr.Code == Code.Cmp_rm8_imm8
                        && instr.IsIPRelativeMemoryOperand
                        && instr.GetImmediate(1) == 0)
                    {
                        g_fGameOverAddress = (nint) instr.IPRelativeMemoryAddress;

                        break;
                    }

                    // mov byte ptr [rip+disp], 1
                    if (instr.Code == Code.Mov_rm8_imm8
                        && instr.IsIPRelativeMemoryOperand
                        && instr.GetImmediate(1) == 1)
                    {
                        g_fGameOverAddress = (nint) instr.IPRelativeMemoryAddress;

                        break;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Method #2 failed, stop patching NavCheck in CCSBotManager::BotAddCommand");

                return;
            }
        }

        if (g_fGameOverAddress == nint.Zero)
        {
            _logger.LogError("Tried two methods to get address for g_fGameOver but all failed, stop patching NavCheck in CCSBotManager::BotAddCommand");

            return;
        }

        _logger.LogInformation("Found g_fGameOver at 0x{Address:X}", g_fGameOverAddress);

        // 2. Decode CCSBotManager::BotAddCommand

        var gamedata                           = _bridge.ModSharp.GetGameData();
        var CCSBotManager_BotAddCommandAddress = gamedata.GetAddress("CCSBotManager::BotAddCommand");

        if (CCSBotManager_BotAddCommandAddress == nint.Zero)
        {
            _logger.LogWarning("Failed to get address for CCSBotManager::BotAddCommand");

            return;
        }

        if (!server.GetFunctionRange(CCSBotManager_BotAddCommandAddress, out _, out end))
        {
            _logger.LogWarning("Failed to get function range for CCSBotManager::BotAddCommand");

            return;
        }

        if (end <= CCSBotManager_BotAddCommandAddress)
        {
            _logger.LogWarning("Invalid function range for CCSBotManager::BotAddCommand (end <= start)");

            return;
        }

        var funcLength = (uint) (end - CCSBotManager_BotAddCommandAddress);
        var reader     = new UnsafeCodeReader(CCSBotManager_BotAddCommandAddress, funcLength);
        var decoder    = Decoder.Create(64, reader, (ulong) CCSBotManager_BotAddCommandAddress, DecoderOptions.AMD);

        Instruction? firstJz     = null;
        ulong        patchTarget = 0;
        Instruction  prev        = default;

        while (reader.CanReadByte)
        {
            var instr = decoder.Decode();

            if (instr.IsInvalid)
            {
                continue;
            }

            // Step 1: Find "test REG, REG" followed by "je/jz"
            if (firstJz is null)
            {
                // 64-bit register self-test (e.g. test rax, rax / test r14, r14)
                var isTestRegReg = prev.Code           == Code.Test_rm64_r64
                                   && prev.Op0Kind     == OpKind.Register
                                   && prev.Op1Kind     == OpKind.Register
                                   && prev.Op0Register == prev.Op1Register;

                // Also handle unoptimized "cmp REG, 0" null-pointer checks, just in case the compiler isn't that smart
                var isCmpRegZero = prev.Code is Code.Cmp_rm64_imm8 or Code.Cmp_rm64_imm32
                                   && prev.GetImmediate(1) == 0;

                if ((isTestRegReg || isCmpRegZero) && instr.Code is Code.Je_rel8_64 or Code.Je_rel32_64)
                {
                    firstJz = instr;
                }
            }

            // Step 2: Find the instruction referencing g_fGameOver as the landing point
            else if (instr.IsIPRelativeMemoryOperand
                     && (nint) instr.IPRelativeMemoryAddress == g_fGameOverAddress)
            {
                patchTarget = instr.IP;
            }

            prev = instr;

            if (patchTarget != 0)
            {
                break;
            }
        }

        if (firstJz is not { } jzInstr)
        {
            _logger.LogError("Failed to find first jz instruction that matches our requirement, stop patching NavCheck in CCSBotManager::BotAddCommand");

            return;
        }

        if (patchTarget == 0)
        {
            _logger.LogWarning("Failed to find patch target in CCSBotManager::BotAddCommand, stop patching NavCheck in CCSBotManager::BotAddCommand");

            return;
        }

        var jzAddress = (byte*) jzInstr.IP;
        var jzLength  = jzInstr.Length;

        var codeWriter = new ByteArrayCodeWriter();
        var encoder    = Encoder.Create(64, codeWriter);

        Instruction jmpInstr;

        // Check if short jump (2 bytes) can reach the target
        var diff = (long) patchTarget - (long) (jzInstr.IP + 2);

        if (diff is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            jmpInstr = Instruction.CreateBranch(Code.Jmp_rel8_64, patchTarget);
        }
        else
        {
            if (jzLength < 5)
            {
                _logger.LogWarning("Not enough space to patch jz instruction ({Length} bytes, need 5 for near JMP)", jzLength);

                return;
            }

            jmpInstr = Instruction.CreateBranch(Code.Jmp_rel32_64, patchTarget);
        }

        try
        {
            encoder.Encode(jmpInstr, jzInstr.IP);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encode JMP instruction");

            return;
        }

        var encodedBytes = codeWriter.ToArray();

        // Build final patchBytes, pad remaining space with NOPs
        var patchBytes = new byte[jzLength];

        for (var i = 0; i < jzLength; i++)
        {
            patchBytes[i] = i < encodedBytes.Length ? encodedBytes[i] : (byte) 0x90;
        }

        _patchManager.Apply(jzAddress,
                            patchBytes,
                            $"BotAddCommand nav check: JMP 0x{jzInstr.IP:X} -> 0x{patchTarget:X} (was {jzInstr.Code})");
    }
}
