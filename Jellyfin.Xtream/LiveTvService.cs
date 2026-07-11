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
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream;

/// <summary>
/// Class LiveTvService.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="LiveTvService"/> class.
/// </remarks>
/// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
/// <param name="providerHttpClient">The credential-safe provider HTTP client.</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
/// <param name="memoryCache">Instance of the <see cref="IMemoryCache"/> interface.</param>
/// <param name="xtreamClient">Instance of the <see cref="IXtreamClient"/> interface.</param>
/// <param name="nameNormalizer">Instance of the <see cref="NameNormalizationService"/> class.</param>
public class LiveTvService(
    IServerApplicationHost appHost,
    ProviderHttpClient providerHttpClient,
    ILogger<LiveTvService> logger,
    IMemoryCache memoryCache,
    IXtreamClient xtreamClient,
    NameNormalizationService nameNormalizer) : ILiveTvService, ISupportsDirectStreamProvider
{
    /// <inheritdoc />
    public string Name => "Xtream Live";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<ChannelInfo> items = [];
        NameNormalizationSnapshot names = nameNormalizer.CreateSnapshot();
        foreach (StreamInfo channel in await plugin.StreamService.GetLiveStreams(cancellationToken).ConfigureAwait(false))
        {
            ParsedName parsed = plugin.StreamService.ApplyLiveStreamOverrides(channel, names);
            items.Add(new ChannelInfo()
            {
                Id = StreamService.ToGuid(StreamService.LiveTvPrefix, channel.StreamId, 0, 0).ToString(),
                Number = channel.Num.ToString(CultureInfo.InvariantCulture),
                ImageUrl = channel.StreamIcon,
                Name = parsed.Title,
                Tags = parsed.Tags,
            });
        }

        return items;
    }

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<TimerInfo>>(new List<TimerInfo>());
    }

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<SeriesTimerInfo>>(new List<SeriesTimerInfo>());
    }

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        MediaSourceInfo source = await GetChannelStream(channelId, string.Empty, cancellationToken).ConfigureAwait(false);
        return [RequireOpening(source)];
    }

    /// <inheritdoc />
    public async Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        int channel = DecodeChannelId(channelId);
        await Plugin.Instance.StreamService.EnsureLiveStreamAuthorizedAsync(channel, cancellationToken).ConfigureAwait(false);

        return Plugin.Instance.StreamService.GetMediaSourceInfo(StreamType.Live, channel);
    }

    /// <inheritdoc />
    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
    {
        logger.LogInformation("Closing livestream {ChannelId}", id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
    {
        return Task.FromResult(new SeriesTimerInfo
        {
            PostPaddingSeconds = 120,
            PrePaddingSeconds = 120,
            RecordAnyChannel = false,
            RecordAnyTime = true,
            RecordNewOnly = false
        });
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        int streamId = DecodeChannelId(channelId);
        await Plugin.Instance.StreamService.EnsureLiveStreamAuthorizedAsync(streamId, cancellationToken).ConfigureAwait(false);

        NameNormalizationSnapshot names = nameNormalizer.CreateSnapshot();
        string key = $"xtream-epg-{channelId}-names-{names.Version.ToString(CultureInfo.InvariantCulture)}";
        ICollection<ProgramInfo>? items = null;
        if (memoryCache.TryGetValue(key, out ICollection<ProgramInfo>? o))
        {
            items = o;
        }
        else
        {
            items = new List<ProgramInfo>();
            Plugin plugin = Plugin.Instance;
            {
                EpgListings epgs = await xtreamClient.GetEpgInfoAsync(plugin.Creds, streamId, cancellationToken).ConfigureAwait(false);
                foreach (EpgInfo epg in epgs.Listings)
                {
                    ParsedName parsedName = names.Normalize(epg.Title, NameScope.LiveProgram);
                    string programName = string.IsNullOrWhiteSpace(parsedName.Title)
                        ? "Untitled program"
                        : parsedName.Title;
                    items.Add(new()
                    {
                        Id = StreamService.ToGuid(StreamService.EpgPrefix, streamId, epg.Id, 0).ToString(),
                        ChannelId = channelId,
                        StartDate = epg.Start,
                        EndDate = epg.End,
                        Name = programName,
                        Overview = epg.Description,
                    });
                }
            }

            memoryCache.Set(key, items, DateTimeOffset.Now.AddMinutes(10));
        }

        return from epg in items
               where epg.EndDate >= startDateUtc && epg.StartDate < endDateUtc
               select epg;
    }

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(string channelId, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        int channel = DecodeChannelId(channelId);
        await Plugin.Instance.StreamService.EnsureLiveStreamAuthorizedAsync(channel, cancellationToken).ConfigureAwait(false);

        Plugin plugin = Plugin.Instance;
        MediaSourceInfo mediaSourceInfo = plugin.StreamService.GetMediaSourceInfo(StreamType.Live, channel, restream: true);
        ILiveStream? stream = currentLiveStreams.Find(stream =>
            stream.TunerHostId == Restream.TunerHost
            && stream.MediaSource.Id == mediaSourceInfo.Id
            && stream.EnableStreamSharing);

        if (stream == null)
        {
            stream = new Restream(appHost, providerHttpClient, logger, mediaSourceInfo);
            await stream.Open(cancellationToken).ConfigureAwait(false);
        }

        stream.ConsumerCount++;
        return stream;
    }

    internal static MediaSourceInfo RequireOpening(MediaSourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.RequiresOpening = true;
        source.RequiresClosing = true;
        return source;
    }

    private static int DecodeChannelId(string channelId)
    {
        if (!Guid.TryParse(channelId, out Guid guid))
        {
            throw new ArgumentException("Unsupported channel", nameof(channelId));
        }

        StreamService.FromGuid(guid, out int prefix, out int channel, out int reserved1, out int reserved2);
        if (prefix != StreamService.LiveTvPrefix || channel <= 0 || reserved1 != 0 || reserved2 != 0)
        {
            throw new ArgumentException("Unsupported channel", nameof(channelId));
        }

        return channel;
    }
}
