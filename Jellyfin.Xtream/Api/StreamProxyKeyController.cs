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

using Jellyfin.Xtream.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// Provides elevated revocation operations for proxy grants.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/JellyfinXtream/v1/ProxyKeys")]
public sealed class StreamProxyKeyController(
    StreamProxyTokenService tokenService,
    TaskService taskService) : ControllerBase
{
    /// <summary>
    /// Revokes all outstanding short-lived playback grants.
    /// </summary>
    /// <returns>An empty success response.</returns>
    [HttpPost("Playback/Rotate")]
    public IActionResult RotatePlayback()
    {
        tokenService.RotatePlaybackKey();
        return NoContent();
    }

    /// <summary>
    /// Revokes all durable STRM grants and schedules enabled exports for regeneration.
    /// </summary>
    /// <returns>An empty success response.</returns>
    [HttpPost("PersistentStrm/Rotate")]
    public IActionResult RotatePersistentStrm()
    {
        tokenService.RotatePersistentStrmKey();
        if (Plugin.Instance.Configuration.IsVodStrmExportEnabled
            || Plugin.Instance.Configuration.IsSeriesStrmExportEnabled)
        {
            taskService.CancelIfRunningAndQueue(
                "Jellyfin.Xtream",
                "Jellyfin.Xtream.Tasks.StrmExportScheduledTask");
        }

        return NoContent();
    }
}
