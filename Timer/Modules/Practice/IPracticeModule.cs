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

using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace Source2Surf.Timer.Modules.Practice;

internal interface IPracticeModule
{
    /// <summary>
    /// Whether the player is currently flagged as "in practice".
    /// </summary>
    bool IsInPractice(IGameClient client);

    /// <inheritdoc cref="IsInPractice(IGameClient)"/>
    bool IsInPractice(PlayerSlot slot);

    /// <summary>
    /// Whether the player is currently running a segmented attempt — i.e. they
    /// have used saveloc on a style/loc that allows segmented runs and the
    /// resulting finish is still eligible for records.
    /// </summary>
    bool IsInSegmented(IGameClient client);

    /// <inheritdoc cref="IsInSegmented(IGameClient)"/>
    bool IsInSegmented(PlayerSlot slot);

    /// <summary>
    /// Capture the player's current pose into the location stack and set the
    /// practice flag. Returns false when the player is in a state that doesn't
    /// allow saving (dead, observer, paused, noclip, etc.).
    /// </summary>
    bool SaveLoc(IGameClient client);

    /// <summary>
    /// Teleport back to a stored location. <paramref name="index"/> defaults to
    /// the most recent location; pass -1 to use the current cursor (controlled
    /// by <see cref="TeleportNext"/> / <see cref="TeleportPrev"/>).
    /// </summary>
    bool TeleportToLoc(IGameClient client, int index = -1);

    /// <summary>
    /// Move the cursor to the next location and teleport there.
    /// </summary>
    bool TeleportNext(IGameClient client);

    /// <summary>
    /// Move the cursor to the previous location and teleport there.
    /// </summary>
    bool TeleportPrev(IGameClient client);

    /// <summary>Number of saved locations for the player.</summary>
    int GetLocCount(IGameClient client);

    /// <inheritdoc cref="GetLocCount(IGameClient)"/>
    int GetLocCount(PlayerSlot slot);

    /// <summary>Index of the location the player will teleport to with <c>!loc</c>.</summary>
    int GetCurrentLoc(IGameClient client);

    /// <summary>Drop the entire location stack for the player.</summary>
    void ClearLocs(IGameClient client);
}
