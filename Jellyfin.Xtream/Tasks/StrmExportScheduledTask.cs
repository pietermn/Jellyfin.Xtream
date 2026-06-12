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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Xtream.Tasks;

/// <summary>
/// Scheduled task that exports Xtream VOD and series items as STRM files.
/// </summary>
/// <param name="strmExportService">Instance of the <see cref="StrmExportService"/> class.</param>
public class StrmExportScheduledTask(StrmExportService strmExportService) : IScheduledTask, IConfigurableScheduledTask
{
    /// <inheritdoc />
    public string Name => "Export Xtream STRM files";

    /// <inheritdoc />
    public string Key => "XtreamStrmExport";

    /// <inheritdoc />
    public string Description => "Exports selected Xtream VOD and Series entries to STRM files for normal Jellyfin libraries.";

    /// <inheritdoc />
    public string Category => "Xtream";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return strmExportService.ExportAsync(progress, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new()
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
            }
        ];
    }
}
