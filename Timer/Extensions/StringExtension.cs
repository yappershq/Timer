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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Text;
using Sharp.Shared.Definition;

namespace Source2Surf.Timer.Extensions;

internal static class StringExtension
{
    private static readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>>
        ColorCodeAlternateLookup;
    private static readonly char[] Braces = ['{', '}'];

    static StringExtension()
    {
        var tempMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in typeof(ChatColor).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(string))
            {
                continue;
            }

            var fieldName        = field.Name;
            var patternToReplace = $"{{{fieldName}}}";
            var colorCode        = field.GetValue(null)?.ToString() ?? string.Empty;

            tempMappings[patternToReplace] = colorCode;
        }

        var frozenMapping = tempMappings.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        ColorCodeAlternateLookup = frozenMapping.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    extension(string value)
    {
        public string RemoveColorPlaceholder()
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var valueSpan = value.AsSpan();

            if (valueSpan.IndexOfAny(Braces) == -1)
            {
                return value;
            }

            using var sb     = ZString.CreateStringBuilder(true);
            var       cursor = 0;

            while (cursor < valueSpan.Length)
            {
                var braceIndex = valueSpan[cursor..]
                    .IndexOfAny(Braces);

                if (braceIndex == -1)
                {
                    sb.Append(valueSpan[cursor..]);

                    break;
                }

                // Adjust braceIndex to be relative to the full string span
                braceIndex += cursor;

                // Append the text segment from the cursor to the brace
                sb.Append(valueSpan.Slice(cursor, braceIndex - cursor));

                // Handle escaped braces: {{ or }}
                if (braceIndex + 1 < valueSpan.Length && valueSpan[braceIndex + 1] == valueSpan[braceIndex])
                {
                    sb.Append(valueSpan[braceIndex]);
                    cursor = braceIndex + 2; // Move cursor past both characters of the escape sequence

                    continue;
                }

                // Handle a potential placeholder starting with '{'
                if (valueSpan[braceIndex] == '{')
                {
                    var endIndex = valueSpan[(braceIndex + 1)..]
                        .IndexOf('}');

                    // Check if a valid placeholder was found
                    if (endIndex == -1)
                    {
                        // Unterminated placeholder; treat '{' as a literal
                        sb.Append('{');
                        cursor = braceIndex + 1;

                        continue;
                    }

                    endIndex += braceIndex + 1;
                    var placeholderSpan = valueSpan.Slice(braceIndex, (endIndex - braceIndex) + 1);

                    if (ColorCodeAlternateLookup.TryGetValue(placeholderSpan, out var replacement))
                    {
                        sb.Append(replacement);
                        cursor = endIndex + 1;
                    }
                    else
                    {
                        // Invalid placeholder; treat '{' as a literal
                        sb.Append('{');
                        cursor = braceIndex + 1;
                    }
                }
                else
                {
                    // Handle an unmatched '}' as a literal character
                    sb.Append('}');
                    cursor = braceIndex + 1;
                }
            }

            return sb.ToString();
        }
    }
}