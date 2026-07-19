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
using System.Text.Json;
using Cysharp.Text;
using Sharp.Shared.Definition;
using Source2Surf.Timer.Shared;

namespace Source2Surf.Timer;

internal static class Utils
{
    public static readonly JsonSerializerOptions SerializerOptions = new () { WriteIndented = true, IndentSize = 4 };

    public static readonly JsonSerializerOptions DeserializerOptions = new ()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string FormatTime(float totalSeconds, bool precise = false)
    {
        var sb = ZString.CreateStringBuilder(true);

        try
        {
            FormatTime(ref sb, totalSeconds, precise);

            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
    }

    public static void FormatTime(ref Utf16ValueStringBuilder sb, float totalSeconds, bool precise = false)
    {
        var negative = totalSeconds < 0;
        var total    = Math.Abs(totalSeconds);

        var totalSecondsInt = (int) total;
        var hours           = totalSecondsInt / 3600;
        var minutes         = (totalSecondsInt / 60) % 60;
        var seconds         = totalSecondsInt % 60;

        var fractional = total - totalSecondsInt;
        var ms         = precise ? (int) (fractional * 1000) : (int) (fractional * 10);

        if (negative) sb.Append('-');

        if (hours > 0)
        {
            sb.Append(hours);
            sb.Append(':');
        }

        AppendPadded2(ref sb, minutes);

        sb.Append(':');
        AppendPadded2(ref sb, seconds);
        sb.Append('.');

        if (precise)
            AppendPadded3(ref sb, ms);
        else
            sb.Append((char) ('0' + ms));
    }

    /// <summary>
    ///     Appends a chat-colored (green) formatted time: <c>{green}MM:SS.mmm{white}</c>.
    /// </summary>
    public static void AppendColoredTime(ref Utf16ValueStringBuilder sb, float time, bool precise = true)
    {
        sb.Append(ChatColor.LightGreen);
        FormatTime(ref sb, time, precise);
        sb.Append(ChatColor.White);
    }

    /// <summary>
    ///     Appends a signed chat-colored time delta: red <c>+</c> when losing time,
    ///     green <c>-</c> when ahead, followed by |delta| and a reset to white.
    /// </summary>
    public static void AppendSignedDelta(ref Utf16ValueStringBuilder sb, float delta, bool precise = true)
    {
        if (delta >= 0f)
        {
            sb.Append(ChatColor.Red);
            sb.Append('+');
        }
        else
        {
            sb.Append(ChatColor.LightGreen);
            sb.Append('-');
        }

        FormatTime(ref sb, MathF.Abs(delta), precise);
        sb.Append(ChatColor.White);
    }

    private static void AppendPadded2(ref Utf16ValueStringBuilder sb, int value)
    {
        sb.Append((char) ('0' + (value / 10)));
        sb.Append((char) ('0' + (value % 10)));
    }

    private static void AppendPadded3(ref Utf16ValueStringBuilder sb, int value)
    {
        sb.Append((char) ('0' + (value / 100)));
        sb.Append((char) ('0' + ((value / 10) % 10)));
        sb.Append((char) ('0' + (value % 10)));
    }

    public static string GetTrackName(int track, bool ignoreNumber = false)
    {
        return track switch
        {
            < 0 or >= TimerConstants.MAX_TRACK =>
                throw new IndexOutOfRangeException($"Track out of range. [0, {TimerConstants.MAX_TRACK})"),
            0                     => "Main",
            > 0 when ignoreNumber => "Bonus",
            > 0                   => $"Bonus {track}",
        };
    }
}
