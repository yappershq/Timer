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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Modules.Zone;
using ZLinq;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

internal partial class ZoneModule
{
    private HookReturnValue<EmptyHookReturn> OnPlayerRunCommandPre(IPlayerRunCommandHookParams      arg1,
                                                                   HookReturnValue<EmptyHookReturn> arg2)
    {
        var client = arg1.Client;

        if (client.IsFakeClient)
        {
            return new ();
        }

        var pawn = arg1.Pawn;

        if (!pawn.IsAlive || _buildZoneInfo[client.Slot] is not { } buildInfo)
        {
            return new ();
        }

        var eyepos = pawn.GetEyePosition();
        eyepos.Z -= 2f;

        pawn.GetEyeAngles().AnglesToVectorSource2(out var direction, out _, out _);

        var end = eyepos + (direction * 1024.0f);

        var attribute = RnQueryShapeAttr.Bullets();
        attribute.HitTrigger = false;
        attribute.SetEntityToIgnore(pawn, 0);

        var result = _bridge.PhysicsQueryManager.TraceLineNoPlayers(eyepos, end, attribute);

        const int snapGrid = 2;

        var snapped = SnapToGrid(result.EndPosition + new Vector(0, 0, 3), snapGrid);

        // TODO: find a better way to visualize the zone, beams are fucked

        RenderDirectionBeam(buildInfo, eyepos, snapped);
        RenderSnapBeams(buildInfo, snapped, snapGrid);

        buildInfo.RenderPreviewBeams(buildInfo.Points[0],
                                     buildInfo.Step == 1 ? snapped + new Vector(0, 0, 128) : buildInfo.Points[1]);

        if ((arg1.KeyButtons & UserCommandButtons.Use) == 0 || (arg1.ChangedButtons & UserCommandButtons.Use) == 0)
        {
            return new ();
        }

        if (buildInfo.Step == 0)
        {
            buildInfo.Points[0] = snapped;

            if (buildInfo.RenderBeams[0] == null)
            {
                var kv = new Dictionary<string, KeyValuesVariantValueItem>
                {
                    { "rendercolor", "255 255 255" },
                    { "BoltWidth", "6" },
                };

                for (var i = 0; i < buildInfo.RenderBeams.Length; i++)
                {
                    if (_bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv) is not
                    {
                        IsValidEntity: true,
                    } beam)
                    {
                        return new ();
                    }

                    buildInfo.RenderBeams[i] = beam;
                }
            }

            buildInfo.Step++;
        }
        else if (buildInfo.Step == 1)
        {
            buildInfo.Points[1] = snapped + new Vector(0, 0, 128);

            AddZone(new ()
            {
                Track    = buildInfo.Track,
                ZoneType = buildInfo.Zone,
                Prebuilt = false,
                Corner1  = buildInfo.Points[0],
                Corner2  = buildInfo.Points[1],
            });

            buildInfo.KillBeams();

            _buildZoneInfo[client.Slot] = null;

            var mapName = _bridge.CurrentMapName;
            var zoneSnapshot = _zones.AsValueEnumerable()
                                     .Where(i => i.Value.Prebuilt == false)
                                     .Select(i => ZoneMapper.ToZoneData(i.Value))
                                     .ToArray();

            Task.Run(async () =>
            {
                try
                {
                    await RetryHelper.RetryAsync(
                        () => _requestManager.SaveZonesAsync(mapName, zoneSnapshot),
                        RetryHelper.IsTransient, _logger, "SaveZonesAsync"
                    );
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to save custom zones to database");
                }
            }, _bridge.CancellationToken);
        }

        return new ();
    }

    private static void RenderSnapBeams(BuildZoneInfo buildInfo, Vector snapped, int snapGrid)
    {
        var snapBeams = buildInfo.SnapBeams;

        // forward <-> backwards
        snapBeams[0].SetAbsOrigin(snapped             + new Vector(1, 0, 0)  * snapGrid / 2);
        snapBeams[0].SetNetVar("m_vecEndPos", snapped + new Vector(-1, 0, 0) * snapGrid / 2);

        // left <-> right
        snapBeams[1].SetAbsOrigin(snapped             + new Vector(0, -1, 0) * snapGrid / 2);
        snapBeams[1].SetNetVar("m_vecEndPos", snapped + new Vector(0, 1, 0)  * snapGrid / 2);
    }

    private void RenderDirectionBeam(BuildZoneInfo buildInfo, Vector eyepos, Vector snapped)
    {
        if (buildInfo.DirectionBeam is not { } directionBeam)
        {
            var kv = new Dictionary<string, KeyValuesVariantValueItem>
            {
                { "rendercolor", "255 255 255" },
                { "BoltWidth", "6" },
            };

            if (_bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv) is not { IsValidEntity: true } beam)
            {
                return;
            }

            beam.SetAbsOrigin(eyepos);
            beam.SetNetVar("m_vecEndPos", snapped);
            buildInfo.DirectionBeam = beam;
        }
        else
        {
            directionBeam.SetAbsOrigin(eyepos);
            directionBeam.SetNetVar("m_vecEndPos", snapped);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector SnapToGrid(in Vector pos, int grid)
    {
        if (grid <= 1)
        {
            return pos;
        }

        var gridF = (float) grid;

        var snappedX = (float) Math.Round(pos.X / gridF, MidpointRounding.AwayFromZero) * gridF;
        var snappedY = (float) Math.Round(pos.Y / gridF, MidpointRounding.AwayFromZero) * gridF;

        return new (snappedX, snappedY, pos.Z);
    }
}
