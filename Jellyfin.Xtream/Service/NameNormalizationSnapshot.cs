// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// An immutable, precompiled view of name-normalization rules.
/// </summary>
public sealed partial class NameNormalizationSnapshot
{
    private const int MaxTagLength = 32;
    private readonly CompiledNameRule[] _rules;
    private readonly ILogger<NameNormalizationService> _logger;
    private readonly ConcurrentDictionary<int, byte> _disabledRules = new();

    internal NameNormalizationSnapshot(
        long version,
        CompiledNameRule[] rules,
        ILogger<NameNormalizationService> logger)
    {
        Version = version;
        _rules = rules;
        _logger = logger;
    }

    /// <summary>
    /// Gets the snapshot version, which changes whenever configuration is activated.
    /// </summary>
    public long Version { get; }

    /// <summary>
    /// Normalizes a provider name using only this snapshot.
    /// </summary>
    /// <param name="name">The provider-supplied name.</param>
    /// <param name="scope">The name scope.</param>
    /// <returns>The normalized title and extracted prefix tags.</returns>
    public ParsedName Normalize(string? name, NameScope scope)
    {
        string title = (name ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        List<string> tags = [];
        ExtractPrefixTags(ref title, tags);

        foreach (CompiledNameRule rule in _rules)
        {
            if ((rule.Scopes & scope) == 0 || _disabledRules.ContainsKey(rule.LineNumber))
            {
                continue;
            }

            try
            {
                title = rule.Regex.Replace(title, rule.Replacement);
            }
            catch (RegexMatchTimeoutException ex)
            {
                if (_disabledRules.TryAdd(rule.LineNumber, 0))
                {
                    _logger.LogWarning(
                        ex,
                        "Name cleanup rule on line {LineNumber} timed out and is disabled until configuration changes.",
                        rule.LineNumber);
                }
            }
            catch (ArgumentException ex)
            {
                if (_disabledRules.TryAdd(rule.LineNumber, 0))
                {
                    _logger.LogWarning(
                        ex,
                        "Name cleanup rule on line {LineNumber} failed and is disabled until configuration changes.",
                        rule.LineNumber);
                }
            }
        }

        title = WhitespaceRegex().Replace(title, " ").Trim();
        return new ParsedName(title, [.. tags]);
    }

    private static void AddTag(List<string> tags, string candidate)
    {
        string tag = candidate.Trim();
        if (tag.Length == 0)
        {
            return;
        }

        foreach (string existing in tags)
        {
            if (string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        tags.Add(tag);
    }

    private static void ExtractPrefixTags(ref string title, List<string> tags)
    {
        while (TryStripWrappedPrefix(ref title, out string? wrappedTag)
               || TryStripBlockPrefix(ref title, out wrappedTag))
        {
            AddTag(tags, wrappedTag);
        }
    }

    private static bool IsBlockElement(char value)
    {
        return value is >= '\u2580' and <= '\u259F';
    }

    private static bool IsPipe(char value)
    {
        return value is '|' or '│' or '┃' or '｜';
    }

    private static bool IsPlausibleTag(string candidate)
    {
        string tag = candidate.Trim();
        if (tag.Length == 0 || tag.Length > MaxTagLength)
        {
            return false;
        }

        bool hasLetterOrDigit = false;
        foreach (char value in tag)
        {
            if (char.IsLetter(value))
            {
                if (char.IsLower(value))
                {
                    return false;
                }

                hasLetterOrDigit = true;
            }
            else if (char.IsDigit(value))
            {
                hasLetterOrDigit = true;
            }
            else if (!char.IsWhiteSpace(value) && value is not ('+' or '-' or '_' or '.' or '/' or '&'))
            {
                return false;
            }
        }

        return hasLetterOrDigit;
    }

    private static string TrimAfterPrefix(string value)
    {
        string result = value.TrimStart();
        if (result.Length > 1
            && result[0] is '-' or ':'
            && char.IsWhiteSpace(result[1]))
        {
            return result[1..].TrimStart();
        }

        return result;
    }

    private static bool TryStripBlockPrefix(ref string title, out string tag)
    {
        for (int i = 1; i < title.Length; i++)
        {
            if (!IsBlockElement(title[i]))
            {
                continue;
            }

            string candidate = title[..i];
            if (!IsPlausibleTag(candidate))
            {
                break;
            }

            tag = candidate.Trim();
            title = TrimAfterPrefix(title[(i + 1)..]);
            return true;
        }

        tag = string.Empty;
        return false;
    }

    private static bool TryStripWrappedPrefix(ref string title, out string tag)
    {
        if (title.Length < 3)
        {
            tag = string.Empty;
            return false;
        }

        int closingIndex;
        if (title[0] == '[')
        {
            closingIndex = title.IndexOf(']', StringComparison.Ordinal);
        }
        else if (IsPipe(title[0]))
        {
            closingIndex = -1;
            for (int i = 1; i < title.Length; i++)
            {
                if (IsPipe(title[i]))
                {
                    closingIndex = i;
                    break;
                }
            }
        }
        else
        {
            tag = string.Empty;
            return false;
        }

        if (closingIndex <= 1)
        {
            tag = string.Empty;
            return false;
        }

        string candidate = title[1..closingIndex];
        if (!IsPlausibleTag(candidate))
        {
            tag = string.Empty;
            return false;
        }

        tag = candidate.Trim();
        title = TrimAfterPrefix(title[(closingIndex + 1)..]);
        return true;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    internal sealed record CompiledNameRule(
        int LineNumber,
        NameScope Scopes,
        Regex Regex,
        string Replacement);
}
