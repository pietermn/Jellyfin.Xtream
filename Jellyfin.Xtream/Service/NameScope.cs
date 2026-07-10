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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Identifies the subject or output for which a media name is normalized.
/// </summary>
[Flags]
public enum NameScope
{
    /// <summary>
    /// No name scope.
    /// </summary>
    None = 0,

    /// <summary>
    /// A Live TV channel name.
    /// </summary>
    LiveChannel = 1 << 0,

    /// <summary>
    /// A Live TV or catch-up programme name.
    /// </summary>
    LiveProgram = 1 << 1,

    /// <summary>
    /// A provider category name.
    /// </summary>
    Category = 1 << 2,

    /// <summary>
    /// A video-on-demand movie name.
    /// </summary>
    Vod = 1 << 3,

    /// <summary>
    /// A series name.
    /// </summary>
    Series = 1 << 4,

    /// <summary>
    /// A season name.
    /// </summary>
    Season = 1 << 5,

    /// <summary>
    /// An episode name.
    /// </summary>
    Episode = 1 << 6,

    /// <summary>
    /// A name used to construct filesystem output.
    /// </summary>
    Filesystem = 1 << 7,

    /// <summary>
    /// Every supported name scope.
    /// </summary>
    All = LiveChannel | LiveProgram | Category | Vod | Series | Season | Episode | Filesystem,
}
