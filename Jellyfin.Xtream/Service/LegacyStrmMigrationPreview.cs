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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A non-mutating legacy STRM scan result.
/// </summary>
/// <param name="Kind">Export layout that was scanned.</param>
/// <param name="RootPath">Configured export root.</param>
/// <param name="Candidates">High-confidence plugin-owned candidates.</param>
/// <param name="Truncated">Whether the safety scan limit was reached.</param>
public sealed record LegacyStrmMigrationPreview(
    LegacyStrmExportKind Kind,
    string RootPath,
    IReadOnlyList<LegacyStrmMigrationCandidate> Candidates,
    bool Truncated)
{
    /// <summary>
    /// Gets the one-time identifier required to quarantine this exact reviewed preview.
    /// </summary>
    public Guid PreviewId { get; init; }

    /// <summary>
    /// Gets the time after which this preview can no longer be applied.
    /// </summary>
    public DateTimeOffset? ExpiresUtc { get; init; }

    /// <summary>
    /// Gets a value indicating whether one or more paths could not be inspected.
    /// </summary>
    public bool Incomplete { get; init; }

    /// <summary>
    /// Gets the number of paths that could not be inspected.
    /// </summary>
    public int SkippedPathCount { get; init; }

    internal IReadOnlyDictionary<string, byte[]> CandidateHashes { get; init; } =
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
}
