using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Emby.FastIptv.LiveTv.Probe;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;

namespace Emby.FastIptv.LiveTv
{
    public class FastIptvTuner : ITunerHost
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Per-tuner channel cache: key = TunerHostInfo.Id
        private static readonly Dictionary<string, (List<M3uChannel> Channels, DateTime Fetched)> Cache
            = new Dictionary<string, (List<M3uChannel>, DateTime)>();

        // Per-tuner XMLTV EPG cache: key = TunerHostInfo.Id
        private static readonly Dictionary<string, (XDocument Doc, DateTime Fetched)> EpgCache
            = new Dictionary<string, (XDocument, DateTime)>();

        private static readonly object CacheLock = new object();

        public string Name => "Fast IPTV M3U";
        public string Type => "FastIptv";
        public string SetupUrl => "configurationpage?name=FastIptvTunerSetup";
        public bool IsSupported => true;

        public TunerHostInfo GetDefaultConfiguration() => new TunerHostInfo
        {
            Type = Type,
            FriendlyName = "Fast IPTV M3U",
            ImportGuideData = false
        };

        public string GetChannelIdPrefix(TunerHostInfo info) => "fastiptv_";

        // Returns true only when the user configured an XMLTV EPG URL for this tuner.
        public bool SupportsGuideData(TunerHostInfo info)
            => !string.IsNullOrEmpty(GetTunerSettings(info?.Id)?.EpgUrl);

        // Always true so Emby can also remap guide data from separately-added listing providers.
        public bool SupportsRemappingGuideData(TunerHostInfo info) => true;

        public Task ValdidateOptions(TunerHostInfo info, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(info?.Url))
                throw new ArgumentException("M3U playlist URL is required.");

            if (IsLocalPath(info.Url))
                return Task.CompletedTask;

            if (!Uri.TryCreate(info.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException($"'{info.Url}' is not a valid HTTP/HTTPS URL or local file path.");

            return Task.CompletedTask;
        }

        public Task OnSaved(TunerHostInfo info, bool isNew, CancellationToken ct)
        {
            lock (CacheLock)
            {
                Cache.Remove(info.Id ?? string.Empty);
                EpgCache.Remove(info.Id ?? string.Empty);
            }

            // Saving a tuner doubles as the manual reset for probe results, which is what a user
            // needs when a provider swaps a channel's codec inside the probe cache window.
            StreamProber.ClearCache(BuildProbeOptions(Plugin.Instance?.Configuration));
            return Task.CompletedTask;
        }

        public Task OnDeleted(TunerHostInfo info, CancellationToken ct)
        {
            lock (CacheLock)
            {
                Cache.Remove(info.Id ?? string.Empty);
                EpgCache.Remove(info.Id ?? string.Empty);
            }
            RemoveTunerSettings(info.Id);
            return Task.CompletedTask;
        }

        public Task<List<TunerHostInfo>> DiscoverDevices(int ms, CancellationToken ct)
            => Task.FromResult(new List<TunerHostInfo>());

        public async Task<List<ChannelInfo>> GetChannels(TunerHostInfo info, CancellationToken ct)
        {
            var channels = await GetCachedChannelsAsync(info, ct).ConfigureAwait(false);
            var prefix = GetChannelIdPrefix(info);
            return channels.Select(c => new ChannelInfo
            {
                Id = prefix + c.Id,
                Name = c.Name ?? c.Id,
                Number = c.Number.ToString(),
                ChannelType = c.IsRadio ? ChannelType.Radio : ChannelType.TV,
                TunerHostId = info.Id,
                ImageUrl = c.TvgLogo,
                Tags = string.IsNullOrEmpty(c.Group) ? new string[0] : new[] { c.Group }
            }).ToList();
        }

        public Task<List<ChannelInfo>> RefreshChannels(TunerHostInfo info, CancellationToken ct)
        {
            lock (CacheLock) { Cache.Remove(info.Id ?? string.Empty); }
            return GetChannels(info, ct);
        }

        public async Task<List<ProgramInfo>> GetProgramsAsync(
            TunerHostInfo info, ChannelInfo channel,
            DateTimeOffset startDate, DateTimeOffset endDate,
            CancellationToken ct)
        {
            var epgUrl = GetTunerSettings(info?.Id)?.EpgUrl;
            if (string.IsNullOrEmpty(epgUrl))
            {
                // No EPG URL configured — return a single live-stream placeholder.
                return new List<ProgramInfo>
                {
                    new ProgramInfo
                    {
                        Id        = "prog_" + channel.Id,
                        ChannelId = channel.Id,
                        Name      = channel.Name,
                        StartDate = startDate.UtcDateTime,
                        EndDate   = endDate.UtcDateTime,
                        IsLive    = true,
                        IsNew     = false
                    }
                };
            }

            var doc = await GetCachedEpgAsync(info, epgUrl, ct).ConfigureAwait(false);

            // Strip tuner prefix to get the raw tvg-id used as the XMLTV channel id.
            var prefix       = GetChannelIdPrefix(info);
            var xmlTvChId    = channel.Id.StartsWith(prefix, StringComparison.Ordinal)
                ? channel.Id.Substring(prefix.Length)
                : channel.Id;

            return XmlTvParser.ParsePrograms(doc, channel.Id, xmlTvChId, startDate, endDate);
        }

        public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
            TunerHostInfo info, BaseItem item, string channelId, CancellationToken ct)
        {
            var channel = await ResolveChannelAsync(info, channelId, ct).ConfigureAwait(false);
            if (channel == null) return new List<MediaSourceInfo>();

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var userAgent = GetEffectiveUserAgent(info.Id, config);
            var headers = ParseCustomHeaders(GetTunerSettings(info.Id)?.CustomHeaders);
            var source = await BuildMediaSourceAsync(channelId, channel.Url, config, userAgent, headers, ct)
                .ConfigureAwait(false);
            return new List<MediaSourceInfo> { source };
        }

        public async Task<ILiveStream> GetChannelStream(
            TunerHostInfo info, BaseItem item, string channelId, string streamId,
            List<ILiveStream> currentStreams, CancellationToken ct)
        {
            var channel = await ResolveChannelAsync(info, channelId, ct).ConfigureAwait(false);
            if (channel == null)
                throw new InvalidOperationException($"Channel '{channelId}' not found.");

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var userAgent = GetEffectiveUserAgent(info.Id, config);
            var headers = ParseCustomHeaders(GetTunerSettings(info.Id)?.CustomHeaders);
            var source = await BuildMediaSourceAsync(channelId, channel.Url, config, userAgent, headers, ct)
                .ConfigureAwait(false);

            if (GetTunerSettings(info.Id)?.EnableHealthProbe == true)
                await ProbeStreamAsync(channel.Url, userAgent, headers, ct).ConfigureAwait(false);

            return new PassthroughLiveStream(source, info.Id);
        }

        // ── helpers ────────────────────────────────────────────────────────────

        private static async Task<MediaSourceInfo> BuildMediaSourceAsync(
            string channelId,
            string url,
            PluginConfiguration config,
            string userAgent,
            Dictionary<string, string> customHeaders,
            CancellationToken ct)
        {
            // HLS streams (.m3u8) must be declared as "hls" so Emby doesn't pass
            // -f mpegts to ffmpeg and clients check HLS capability rather than TS.
            var container = StreamProber.IsPlaylistUrl(url) ? "hls" : config.DefaultContainer;

            var source = new MediaSourceInfo
            {
                Id = channelId,
                Path = url,
                Protocol = MediaProtocol.Http,
                Container = container,
                IsInfiniteStream = true,
                // Required for recording: Emby's recorder only gets a live stream object when the
                // source requires opening, and crashes without one. Opening is free — see
                // PassthroughLiveStream — and the URL is still read directly by ffmpeg.
                RequiresOpening = true,
                RequiresClosing = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                BufferMs = 3000,
                RequiredHttpHeaders = BuildRequiredHeaders(userAgent, customHeaders),

                // Empty means "no idea" and makes Emby probe the stream itself, which is correct
                // but costs seconds on every tune. Advertising guesses is not an option: an HEVC
                // 4K channel offered to clients as h264/1080p plays as audio with black video.
                MediaStreams = new List<MediaStream>()
            };

            if (config.AdvertiseStreamMetadata)
            {
                source.MediaStreams = BuildAdvertisedStreams(config);
                return source;
            }

            source.MediaStreams = await GetProbedStreamsAsync(url, userAgent, customHeaders, config, ct)
                .ConfigureAwait(false);

            return source;
        }

        // Fast in-process probe of the real stream. Falls back to an empty list — i.e. to Emby's
        // own probe — for anything it cannot determine with certainty, and never throws: a failed
        // probe must cost latency at worst, never playback.
        private static async Task<List<MediaStream>> GetProbedStreamsAsync(
            string url,
            string userAgent,
            Dictionary<string, string> customHeaders,
            PluginConfiguration config,
            CancellationToken ct)
        {
            try
            {
                var result = await StreamProber
                    .ProbeAsync(url, userAgent, customHeaders, BuildProbeOptions(config), ct)
                    .ConfigureAwait(false);

                return result.IsUsable ? ToMediaStreams(result.Streams) : new List<MediaStream>();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return new List<MediaStream>();
            }
        }

        private static ProbeOptions BuildProbeOptions(PluginConfiguration config)
        {
            var timeout = config?.ProbeTimeoutSeconds ?? 4;
            var kilobytes = config?.ProbeMaxKilobytes ?? 768;
            var cacheHours = config?.ProbeCacheHours ?? 24;

            return new ProbeOptions
            {
                TimeoutSeconds = timeout > 0 ? timeout : 4,
                MaxBytes = (kilobytes > 0 ? kilobytes : 768) * 1024,
                CacheTtl = TimeSpan.FromHours(cacheHours > 0 ? cacheHours : 24),
                CacheFilePath = GetProbeCachePath()
            };
        }

        private static string GetProbeCachePath()
        {
            var folder = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "probe-cache.json");
        }

        // The Index values come straight from the PMT order, which is the order ffmpeg assigns,
        // because Emby turns them into "-map 0:<index>" on the transcode command line.
        private static List<MediaStream> ToMediaStreams(List<ProbedStream> probed)
        {
            var streams = new List<MediaStream>(probed.Count);
            var videoSeen = false;
            var audioSeen = false;
            var subtitleSeen = false;

            foreach (var p in probed)
            {
                var stream = new MediaStream
                {
                    Index = p.Index,
                    Codec = p.Codec,
                    Language = p.Language,
                    Profile = p.Profile,
                    Level = p.Level
                };

                switch (p.Kind)
                {
                    case ProbedStreamKind.Video:
                        stream.Type = MediaStreamType.Video;
                        stream.Width = p.Width;
                        stream.Height = p.Height;
                        stream.BitDepth = p.BitDepth;
                        stream.IsInterlaced = p.IsInterlaced;
                        stream.IsDefault = !videoSeen;
                        videoSeen = true;
                        break;

                    case ProbedStreamKind.Audio:
                        stream.Type = MediaStreamType.Audio;
                        stream.Channels = p.Channels;
                        stream.SampleRate = p.SampleRate;
                        stream.ChannelLayout = ChannelLayoutFor(p.Channels);
                        stream.IsDefault = !audioSeen;
                        audioSeen = true;
                        break;

                    default:
                        stream.Type = MediaStreamType.Subtitle;
                        stream.IsDefault = !subtitleSeen;
                        subtitleSeen = true;
                        break;
                }

                streams.Add(stream);
            }

            return streams;
        }

        private static string ChannelLayoutFor(int? channels)
        {
            switch (channels)
            {
                case 1: return "mono";
                case 2: return "stereo";
                case 3: return "2.1";
                case 6: return "5.1";
                case 8: return "7.1";
                default: return null;
            }
        }

        // ffmpeg, the recorder and direct-playing clients fetch the URL themselves (the live
        // stream is a PassthroughLiveStream), so the tuner's User-Agent and custom headers have to
        // travel with the media source or the provider answers 403.
        private static Dictionary<string, string> BuildRequiredHeaders(
            string userAgent, Dictionary<string, string> customHeaders)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (customHeaders != null)
                foreach (var kv in customHeaders)
                    headers[kv.Key] = kv.Value;

            // The dedicated User-Agent setting is more specific, so it wins.
            if (!string.IsNullOrEmpty(userAgent))
                headers["User-Agent"] = userAgent;

            return headers;
        }

        // Opt-in legacy behaviour: advertise the configured codecs instead of probing.
        // Width/Height are intentionally left unset so the stream's native resolution applies.
        private static List<MediaStream> BuildAdvertisedStreams(PluginConfiguration config) =>
            new List<MediaStream>
            {
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Codec = config.DefaultVideoCodec,
                    Index = 0,
                    IsDefault = true
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Codec = config.DefaultAudioCodec,
                    Index = 1,
                    Channels = 2,
                    IsDefault = true
                }
            };

        private static bool IsLocalPath(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeFile)
                return true;
            return Path.IsPathRooted(url);
        }

        private static string ResolveLocalPath(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeFile)
                return uri.LocalPath;
            return url;
        }

        private async Task<M3uChannel> ResolveChannelAsync(
            TunerHostInfo info, string channelId, CancellationToken ct)
        {
            var channels = await GetCachedChannelsAsync(info, ct).ConfigureAwait(false);
            var prefix = GetChannelIdPrefix(info);
            var rawId = channelId.StartsWith(prefix, StringComparison.Ordinal)
                ? channelId.Substring(prefix.Length)
                : channelId;
            return channels.FirstOrDefault(c => c.Id == rawId);
        }

        private async Task<List<M3uChannel>> GetCachedChannelsAsync(TunerHostInfo info, CancellationToken ct)
        {
            var key = info.Id ?? string.Empty;
            var ttl = GetEffectiveCacheTtl(info.Id);

            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out var hit) &&
                    DateTime.UtcNow - hit.Fetched < ttl)
                    return hit.Channels;
            }

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var userAgent = GetEffectiveUserAgent(info.Id, config);
            var headers = ParseCustomHeaders(GetTunerSettings(info.Id)?.CustomHeaders);
            var channels = await FetchM3uAsync(info.Url, userAgent, headers, ct).ConfigureAwait(false);

            lock (CacheLock)
            {
                Cache[key] = (channels, DateTime.UtcNow);
            }

            return channels;
        }

        private static async Task<List<M3uChannel>> FetchM3uAsync(
            string url, string userAgent, Dictionary<string, string> headers, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
                return new List<M3uChannel>();

            if (IsLocalPath(url))
            {
                var content = await File.ReadAllTextAsync(ResolveLocalPath(url), ct).ConfigureAwait(false);
                return M3uParser.Parse(content);
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            if (!string.IsNullOrEmpty(userAgent))
                req.Headers.TryAddWithoutValidation("User-Agent", userAgent);

            foreach (var kv in headers)
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var content2 = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return M3uParser.Parse(content2);
        }

        private async Task<XDocument> GetCachedEpgAsync(
            TunerHostInfo info, string epgUrl, CancellationToken ct)
        {
            var key = info?.Id ?? string.Empty;
            var ttl = GetEffectiveCacheTtl(info?.Id);

            lock (CacheLock)
            {
                if (EpgCache.TryGetValue(key, out var hit) &&
                    DateTime.UtcNow - hit.Fetched < ttl)
                    return hit.Doc;
            }

            var config    = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var userAgent = GetEffectiveUserAgent(info?.Id, config);
            var headers   = ParseCustomHeaders(GetTunerSettings(info?.Id)?.CustomHeaders);
            var doc       = await FetchXmlTvAsync(epgUrl, userAgent, headers, ct).ConfigureAwait(false);

            lock (CacheLock)
            {
                EpgCache[key] = (doc, DateTime.UtcNow);
            }

            return doc;
        }

        private static async Task<XDocument> FetchXmlTvAsync(
            string url, string userAgent, Dictionary<string, string> headers, CancellationToken ct)
        {
            if (IsLocalPath(url))
            {
                try { return XDocument.Load(ResolveLocalPath(url)); }
                catch { return new XDocument(); }
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            if (!string.IsNullOrEmpty(userAgent))
                req.Headers.TryAddWithoutValidation("User-Agent", userAgent);

            foreach (var kv in headers)
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            try { return XDocument.Parse(content); }
            catch { return new XDocument(); }
        }

        private static string GetEffectiveUserAgent(string tunerId, PluginConfiguration config)
        {
            var perTuner = GetTunerSettings(tunerId)?.UserAgent;
            if (!string.IsNullOrWhiteSpace(perTuner))
                return perTuner;
            return config?.UserAgent ?? string.Empty;
        }

        private static TimeSpan GetEffectiveCacheTtl(string tunerId)
        {
            var perTuner = GetTunerSettings(tunerId)?.CacheTtlHours ?? 0;
            if (perTuner > 0)
                return TimeSpan.FromHours(perTuner);

            var global = Plugin.Instance?.Configuration?.CacheTtlHours ?? 6;
            return TimeSpan.FromHours(global > 0 ? global : 6);
        }

        // Sends a HEAD request to verify the URL is reachable before committing to a full GET.
        // Any HTTP response (including 405 Method Not Allowed) is treated as success — only
        // network-level failures (timeout, DNS, connection refused) throw.
        private static async Task ProbeStreamAsync(
            string url, string userAgent, Dictionary<string, string> headers, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            if (!string.IsNullOrEmpty(userAgent))
                req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            foreach (var kv in headers)
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
        }

        private static TunerSettingsEntry GetTunerSettings(string tunerId)
        {
            if (string.IsNullOrEmpty(tunerId)) return null;
            var entries = Plugin.Instance?.Configuration?.TunerSettings;
            if (entries == null) return null;
            return entries.FirstOrDefault(e => e.TunerId == tunerId);
        }

        private static void RemoveTunerSettings(string tunerId)
        {
            if (string.IsNullOrEmpty(tunerId)) return;
            var config = Plugin.Instance?.Configuration;
            if (config?.TunerSettings == null) return;

            var updated = config.TunerSettings.Where(e => e.TunerId != tunerId).ToArray();
            if (updated.Length == config.TunerSettings.Length) return;

            config.TunerSettings = updated;
            Plugin.Instance.SaveConfiguration();
        }

        // Parses "Name: Value" lines into a header dictionary. Invalid lines are skipped.
        private static Dictionary<string, string> ParseCustomHeaders(string headersText)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(headersText)) return dict;

            foreach (var raw in headersText.Split('\n'))
            {
                var line = raw.Trim();
                var sep = line.IndexOf(':');
                if (sep <= 0) continue;

                var name = line.Substring(0, sep).Trim();
                var value = line.Substring(sep + 1).Trim();
                if (!string.IsNullOrEmpty(name))
                    dict[name] = value;
            }

            return dict;
        }
    }
}
