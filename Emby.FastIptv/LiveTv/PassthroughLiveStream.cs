using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Emby.FastIptv.LiveTv
{
    // A live stream that holds no connection and buffers nothing. Its media source still points
    // at the provider URL, so ffmpeg (playback) and the recorder fetch the stream themselves,
    // exactly as with RequiresOpening = false.
    //
    // It exists because Emby's recorder needs a non-null ILiveStream: EmbyTV only opens one when
    // the media source says RequiresOpening, and RecordingRequiresEncoding / DirectRecorder.Record
    // then read liveStream.SupportsCopyTo without a null check. With RequiresOpening = false every
    // recording failed with a NullReferenceException (identical code in Emby 4.9.5 and 4.10.1).
    //
    // Emby's own stream (ILiveTvManager.CreateLiveStream) is deliberately not used: on open it
    // waits 3.5-8 s for a buffer and then runs ffprobe, discarding the plugin's fast probe.
    //
    // SupportsCopyTo is false, so the recorder reads mediaSource.Path itself: a direct HTTP copy
    // for MPEG-TS, ffmpeg for HLS. Both send the media source's RequiredHttpHeaders.
    public class PassthroughLiveStream : ILiveStream
    {
        public PassthroughLiveStream(MediaSourceInfo mediaSource, string tunerHostId)
        {
            UniqueId = Guid.NewGuid().ToString("N");
            OriginalStreamId = UniqueId;
            TunerHostId = tunerHostId;
            DateOpened = DateTimeOffset.UtcNow;

            // Emby derives LiveStreamId from MediaSource.Id and tracks open streams by it. These
            // streams are not shared, so each one needs its own id, or one viewer closing a
            // channel would close another viewer's (or a recording's) stream entry.
            mediaSource.Id = mediaSource.Id + "_" + UniqueId;
            MediaSource = mediaSource;
        }

        // Emby uses ConsumerCount to decide when the stream can be closed. Up to 4.9 the server
        // sets it directly; 4.10 removed the setter and calls AddConsumer/RemoveConsumer with a
        // consumer id instead. Both paths are implemented so one DLL loads on either server; they
        // are virtual because the CLR only maps virtual methods onto interface members, and a
        // member absent from the compile-time interface would otherwise be emitted non-virtual.
        private readonly HashSet<string> _consumers = new HashSet<string>();
        private readonly object _consumerLock = new object();
        private int _consumerCount;

        public virtual int ConsumerCount
        {
            get { lock (_consumerLock) { return _consumerCount; } }
            set { lock (_consumerLock) { _consumerCount = value; } }
        }

        public virtual void AddConsumer(string id)
        {
            lock (_consumerLock)
            {
                if (_consumers.Add(id ?? string.Empty)) _consumerCount++;
            }
        }

        public virtual void RemoveConsumer(string id)
        {
            lock (_consumerLock)
            {
                if (_consumers.Remove(id ?? string.Empty) && _consumerCount > 0) _consumerCount--;
            }
        }
        public string OriginalStreamId { get; set; }
        public string TunerHostId { get; }
        public bool EnableStreamSharing => false;
        public MediaSourceInfo MediaSource { get; set; }
        public string UniqueId { get; }
        public DateTimeOffset DateOpened { get; }
        public bool SupportsCopyTo => false;

        public Task Open(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Close() => Task.CompletedTask;

        public Task CopyToAsync(
            Stream outputStream,
            DateTimeOffset? startAt,
            Action<SegmentedStreamSegmentInfo> segmentAction,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("Passthrough stream: read MediaSource.Path instead.");

        public Task CopyToAsync(PipeWriter writer, CancellationToken cancellationToken)
            => throw new NotSupportedException("Passthrough stream: read MediaSource.Path instead.");
    }
}
