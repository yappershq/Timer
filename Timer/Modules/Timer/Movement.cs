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
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameObjects;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Source2Surf.Timer.Modules.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

internal partial class TimerModule
{
    private unsafe void OnPlayerProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;

        if (!client.IsValid || client.IsFakeClient)
        {
            return;
        }

        var pawn = arg.Pawn;

        if (!pawn.IsAlive)
        {
            return;
        }

        var service = arg.Service;

        service.Stamina   = 0f;
        service.DuckSpeed = 7.0f;

        if (_timerInfo[client.Slot] is not { } timerInfo || _stageTimerInfo[client.Slot] is not { } stageTimer)
        {
            return;
        }

        var onGround = pawn.GroundEntityHandle.IsValid();

        var info = arg.Info;

        if (pawn.ActualMoveType == MoveType.NoClip)
        {
            timerInfo.StopTimer();
            stageTimer.StopTimer();

            timerInfo.OnGroundTick  = 0;
            stageTimer.OnGroundTick = 0;

            return;
        }

        var forwardmove = info->ForwardMove;
        var sidemove    = info->SideMove;

        var inMainStartZone  = timerInfo.InZone  == EZoneType.Start;
        var inStageStartZone = stageTimer.InZone == EZoneType.Stage;

        if (!onGround && (inMainStartZone || inStageStartZone))
        {
            var maxJumps    = _mapInfoModule.GetMaxPrejumps(timerInfo.Track);
            var shouldBlock = false;

            // Check each timer independently based on which zone the player is in
            if (inMainStartZone
                && timerInfo.WasOnGround
                && timerInfo.OnGroundTick <= PrejumpGraceTicks
                && timerInfo.Jumps        >= maxJumps)
            {
                timerInfo.Jumps = 0;
                shouldBlock     = true;
            }

            if (inStageStartZone
                && stageTimer.WasOnGround
                && stageTimer.OnGroundTick <= PrejumpGraceTicks
                && stageTimer.Jumps        >= maxJumps)
            {
                stageTimer.Jumps = 0;
                shouldBlock      = true;
            }

            if (shouldBlock)
            {
                arg.Velocity      = new ();
                info->ForwardMove = 0;
                info->SideMove    = 0;
            }
        }

        UpdateTimerState(timerInfo);
        UpdateTimerState(stageTimer);

        return;

        void UpdateTimerState(TimerInfo timer)
        {
            if (onGround)
            {
                timer.OnGroundTick++;
            }
            else
            {
                timer.OnGroundTick = 0;
            }

            timer.WasOnGround     = onGround;
            timer.LastForwardMove = forwardmove;
            timer.LastLeftMove    = sidemove;
        }
    }

    private void OnPlayerRunCommandPost(IPlayerRunCommandHookParams arg, HookReturnValue<EmptyHookReturn> hook)
    {
        var client = arg.Client;

        if (client.IsFakeClient || arg.Pawn.AsPlayer() is not { IsAlive: true } pawn)
        {
            return;
        }

        var slot = client.Slot;

        if (_timerInfo[slot] is not { } timerInfo || _stageTimerInfo[slot] is not { } stageTimer)
        {
            return;
        }

        var mainRunning  = timerInfo.IsTimerRunning();
        var stageRunning = stageTimer.IsTimerRunning();

        if (!mainRunning && !stageRunning)
        {
            return;
        }

        var service  = arg.Service;
        var origin   = pawn.GetAbsOrigin();
        var angles   = pawn.GetEyeAngles();
        var velocity = pawn.GetAbsVelocity();

        var onGround = pawn.GroundEntityHandle.IsValid();

        var isSurfing = false;

        if (!onGround)
        {
            var hull = service.GetNetVar<bool>("m_bDucked") ? DuckedHull : StandingHull;

            var collision = pawn.GetCollisionProperty()!;

            var end = origin;
            end.Z -= SurfTraceDepth;

            var attribute = RnQueryShapeAttr.PlayerMovement(collision.CollisionAttribute.InteractsWith);
            attribute.SetEntityToIgnore(pawn, 0);

            var result = _bridge.PhysicsQueryManager.TraceShapePlayerMovement(new (hull),
                                                                              origin,
                                                                              end,
                                                                              attribute);

            isSurfing = result.DidHit() && Math.Abs(result.PlaneNormal.Z) < sv_standable_normal.GetFloat();
        }

        var leftmove = service.GetNetVar<float>("m_flLeftMove");

        if (mainRunning)
        {
            timerInfo.TimerTick++;

            UpdatePlayerStats(pawn,
                              service,
                              timerInfo,
                              angles,
                              velocity,
                              isSurfing,
                              leftmove,
                              timerInfo.LastYaw);

            if (timerInfo.CurrentCheckpointInfo is { } currentCp)
            {
                currentCp.AverageVelocity
                    += (velocity - currentCp.AverageVelocity) / (timerInfo.TimerTick - currentCp.TimerTick);

                if (velocity.LengthSqr() > currentCp.MaxVelocity.LengthSqr())
                {
                    currentCp.MaxVelocity = velocity;
                }
            }

            timerInfo.LastYaw = angles.Y;
        }

        if (stageRunning)
        {
            stageTimer.TimerTick++;

            UpdatePlayerStats(pawn,
                              service,
                              stageTimer,
                              angles,
                              velocity,
                              isSurfing,
                              leftmove,
                              stageTimer.LastYaw);

            stageTimer.LastYaw = angles.Y;
        }
    }

    private void OnPlayerJump(IGameEvent e)
    {
        if (e.GetPlayerController("userid") is not { IsValidEntity: true } controller
            || _timerInfo[controller.PlayerSlot] is not { } timerInfo)
        {
            return;
        }

        timerInfo.Jumps++;

        if (_stageTimerInfo[controller.PlayerSlot] is { } stageTimer)
        {
            stageTimer.Jumps++;
        }
    }

    private static void UpdatePlayerStats(IPlayerPawn      pawn,
                                          IMovementService service,
                                          TimerInfo        timerInfo,
                                          Vector           angle,
                                          Vector           velocity,
                                          bool             isSurfing,
                                          float            sidemove,
                                          float            lastYaw)
    {
        var onGround = pawn.GroundEntityHandle.IsValid();

        if (!onGround)
        {
            var yawDiff = angle.Y - lastYaw;

            if (timerInfo.LastLeftMove != 0 && sidemove is > 0 or < 0)
            {
                timerInfo.Strafes++;
            }

            var buttons = service.KeyButtons;

            var isPressingLeft  = (buttons & UserCommandButtons.MoveLeft)  != 0;
            var isPressingRight = (buttons & UserCommandButtons.MoveRight) != 0;

            if (!isSurfing && (isPressingLeft || isPressingRight) && MathF.Abs(yawDiff) > 0.01)
            {
                timerInfo.TotalMeasures++;

                if ((yawDiff    > 0.0f && isPressingLeft  && !isPressingRight)
                    || (yawDiff < 0.0f && !isPressingLeft && isPressingRight))
                {
                    timerInfo.GoodSync++;
                }
            }
        }

        if (velocity.LengthSqr() > timerInfo.MaxVelocity.LengthSqr())
        {
            timerInfo.MaxVelocity = velocity;
        }

        timerInfo.AvgVelocity += (velocity - timerInfo.AvgVelocity) / timerInfo.TimerTick;
    }
}
