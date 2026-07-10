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
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Compiles configured name-cleanup rules and atomically publishes immutable normalization snapshots.
/// </summary>
/// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
public sealed class NameNormalizationService(ILogger<NameNormalizationService> logger)
{
    private const int MaxRuleCount = 128;
    private static readonly TimeSpan _regexTimeout = TimeSpan.FromMilliseconds(100);
    private NameNormalizationSnapshot _snapshot = new(0, [], logger);
    private long _version;

    /// <summary>
    /// Gets the current immutable normalization snapshot.
    /// </summary>
    /// <returns>A snapshot which remains unchanged when plugin configuration is updated.</returns>
    public NameNormalizationSnapshot CreateSnapshot()
    {
        return Volatile.Read(ref _snapshot);
    }

    /// <summary>
    /// Normalizes a name using the current rule snapshot.
    /// </summary>
    /// <param name="name">The provider-supplied name.</param>
    /// <param name="scope">The name scope.</param>
    /// <returns>The normalized title and extracted prefix tags.</returns>
    public ParsedName Normalize(string? name, NameScope scope)
    {
        return CreateSnapshot().Normalize(name, scope);
    }

    /// <summary>
    /// Compiles and atomically activates cleanup rules.
    /// Invalid rules are omitted while the remaining valid rules stay active.
    /// </summary>
    /// <param name="rules">The legacy line-oriented cleanup-rule configuration.</param>
    /// <returns>Validation messages for rules that could not be activated.</returns>
    public IReadOnlyList<string> UpdateRules(string? rules)
    {
        List<string> errors = [];
        List<NameNormalizationSnapshot.CompiledNameRule> compiledRules = [];

        if (!string.IsNullOrWhiteSpace(rules))
        {
            string[] lines = rules.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (compiledRules.Count >= MaxRuleCount)
                {
                    errors.Add($"Only the first {MaxRuleCount} valid cleanup rules are activated.");
                    break;
                }

                int lineNumber = i + 1;
                if (!TryParseRule(line, lineNumber, out NameRuleDefinition definition, out string? parseError))
                {
                    errors.Add(parseError ?? $"Line {lineNumber}: invalid cleanup rule.");
                    continue;
                }

                try
                {
                    Regex regex = new(
                        definition.Pattern,
                        RegexOptions.Compiled | RegexOptions.CultureInvariant,
                        _regexTimeout);

                    // Force replacement-token validation while configuration is updated.
                    _ = regex.Replace(string.Empty, definition.Replacement);
                    compiledRules.Add(new(
                        lineNumber,
                        definition.Scopes,
                        regex,
                        definition.Replacement));
                }
                catch (ArgumentException ex)
                {
                    errors.Add($"Line {lineNumber}: {ex.Message}");
                }
            }
        }

        long version = Interlocked.Increment(ref _version);
        NameNormalizationSnapshot snapshot = new(version, [.. compiledRules], logger);
        Volatile.Write(ref _snapshot, snapshot);

        foreach (string error in errors)
        {
            logger.LogWarning("Name cleanup rule validation: {ValidationError}", error);
        }

        return errors.AsReadOnly();
    }

    private static string DecodeReplacement(string replacement)
    {
        return replacement
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal);
    }

    private static bool TryParseRule(
        string line,
        int lineNumber,
        out NameRuleDefinition definition,
        out string? error)
    {
        NameScope scopes = NameScope.All;
        string body = line;
        if (!TryReadScopePrefix(line, out NameScope scopedRules, out int prefixLength, out error))
        {
            definition = default;
            return false;
        }

        if (prefixLength > 0)
        {
            scopes = scopedRules;
            body = line[prefixLength..].TrimStart();
        }

        int separator = body.IndexOf("=>", StringComparison.Ordinal);
        string pattern = (separator < 0 ? body : body[..separator]).Trim();
        string replacement = separator < 0 ? string.Empty : DecodeReplacement(body[(separator + 2)..].Trim());
        if (pattern.Length == 0)
        {
            definition = default;
            error = $"Line {lineNumber}: cleanup pattern is empty.";
            return false;
        }

        definition = new(scopes, pattern, replacement);
        error = null;
        return true;
    }

    private static bool TryReadScopePrefix(
        string line,
        out NameScope scopes,
        out int prefixLength,
        out string? error)
    {
        scopes = NameScope.All;
        prefixLength = 0;
        error = null;
        if (line.Length == 0 || line[0] != '[')
        {
            return true;
        }

        int closingBracket = line.IndexOf(']', StringComparison.Ordinal);
        if (closingBracket <= 1
            || closingBracket + 1 >= line.Length
            || !char.IsWhiteSpace(line[closingBracket + 1]))
        {
            return true;
        }

        string[] scopeNames = line[1..closingBracket]
            .Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        NameScope parsedScopes = NameScope.None;
        List<string> unknownScopes = [];
        foreach (string scopeName in scopeNames)
        {
            if (TryParseScope(scopeName, out NameScope parsedScope))
            {
                parsedScopes |= parsedScope;
            }
            else
            {
                unknownScopes.Add(scopeName);
            }
        }

        // A normal regular expression may begin with a character class such as [A-Z].
        if (parsedScopes == NameScope.None)
        {
            return true;
        }

        if (unknownScopes.Count > 0)
        {
            error = $"Unknown cleanup scope(s): {string.Join(", ", unknownScopes)}.";
            return false;
        }

        scopes = parsedScopes;
        prefixLength = closingBracket + 1;
        return true;
    }

    private static bool TryParseScope(string value, out NameScope scope)
    {
        switch (value.ToUpperInvariant())
        {
            case "ALL":
                scope = NameScope.All;
                return true;
            case "LIVE":
            case "LIVETV":
            case "LIVECHANNEL":
                scope = NameScope.LiveChannel;
                return true;
            case "PROGRAM":
            case "PROGRAMME":
            case "LIVEPROGRAM":
                scope = NameScope.LiveProgram;
                return true;
            case "CATEGORY":
                scope = NameScope.Category;
                return true;
            case "MOVIE":
            case "VOD":
                scope = NameScope.Vod;
                return true;
            case "SHOW":
            case "SERIES":
                scope = NameScope.Series;
                return true;
            case "SEASON":
                scope = NameScope.Season;
                return true;
            case "EPISODE":
                scope = NameScope.Episode;
                return true;
            case "FILE":
            case "FILESYSTEM":
                scope = NameScope.Filesystem;
                return true;
            default:
                scope = NameScope.None;
                return false;
        }
    }

    private readonly record struct NameRuleDefinition(NameScope Scopes, string Pattern, string Replacement);
}
