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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Result of an explicit legacy STRM quarantine operation.
/// </summary>
/// <param name="Kind">Export layout that was quarantined.</param>
/// <param name="QuarantinedCount">Number of files moved.</param>
/// <param name="QuarantinePath">Batch folder containing the files and report.</param>
/// <param name="SkippedCount">Candidates that changed or became unsafe after preview.</param>
public sealed record LegacyStrmMigrationResult(
    LegacyStrmExportKind Kind,
    int QuarantinedCount,
    string? QuarantinePath,
    int SkippedCount);
