using System.Threading.Channels;
using RadioWebControl.Core.Services.Cw;
using Yaesu_Web_Control.Services.Audio;

namespace Yaesu_Web_Control.Services.Cw
{
    /// <summary>
    /// Core's ICwAudioSource over the audio bridge's RX capture.
    ///
    /// The bridge already opens the radio's USB codec and produces mono float
    /// frames at exactly the rate and length Core asks for, so this opens no
    /// device of its own. That is deliberate: a second PortAudio stream on the
    /// same codec either fails outright or fights the first one for buffers,
    /// and the operator listening to the radio would be the one who noticed.
    ///
    /// The consequence is that the decoder only hears anything while an audio
    /// session is live, because that is when the bridge opens the devices.
    /// CwReaderService reports that rather than looking silently broken.
    ///
    /// The bridge raises frames on the PortAudio callback thread, where the
    /// decoder's tone analysis has no business running. So frames are copied
    /// into a bounded channel there - cheap, non-blocking, never waits - and a
    /// background task does the decoding. If the decoder ever falls behind, the
    /// channel drops the oldest frame rather than stalling the audio thread:
    /// dropped CW audio is a few lost characters, whereas a blocked audio
    /// callback is a click in the operator's headphones.
    /// </summary>
    public sealed class BridgeCwAudioSource : ICwAudioSource, IDisposable
    {
        // A second of audio. Long enough to ride out a GC pause or a slow
        // frame, short enough that a real backlog is discarded rather than
        // decoded minutes late.
        private const int QueueCapacity = 100;

        private readonly RadioAudioBridgeService _bridge;
        private readonly ILogger<BridgeCwAudioSource> _logger;
        private readonly object _gate = new();

        private Channel<ReadOnlyMemory<float>>? _queue;
        private CancellationTokenSource? _cts;
        private Task? _pump;
        private long _dropped;
        private bool _holdsCapture;

        public BridgeCwAudioSource(RadioAudioBridgeService bridge, ILogger<BridgeCwAudioSource> logger)
        {
            _bridge = bridge;
            _logger = logger;
        }

        public int SampleRate => AudioConstants.SampleRate;

        public bool IsRunning { get; private set; }

        public event Action<ReadOnlyMemory<float>>? FrameAvailable;

        /// <summary>Frames discarded because the decoder could not keep up.</summary>
        public long DroppedFrames => Interlocked.Read(ref _dropped);

        /// <summary>
        /// True when the bridge has the capture device open. False means there
        /// is no audio to decode however healthy everything else looks.
        /// </summary>
        public bool AudioDevicesOpen => _bridge.DevicesOpen;

        /// <summary>
        /// Last error from asking the bridge to open capture, or null. Surfaced
        /// so the reader can say "the RX device is not set" rather than sitting
        /// silently on an audio stream that was never opened.
        /// </summary>
        public string? CaptureError { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (IsRunning) return;

                _queue = Channel.CreateBounded<ReadOnlyMemory<float>>(
                    new BoundedChannelOptions(QueueCapacity)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                        SingleWriter = true,
                    });

                Interlocked.Exchange(ref _dropped, 0);
                _cts = new CancellationTokenSource();
                _pump = Task.Run(() => PumpAsync(_queue.Reader, _cts.Token));

                _bridge.RxFrameCaptured += OnBridgeFrame;
                IsRunning = true;
            }

            // Ask the bridge for received audio directly rather than waiting for
            // somebody to connect the Remote Audio bar. The decoder only ever
            // consumes RX, so this opens the capture endpoint alone and leaves
            // the radio's playback endpoint free for WSJT-X and friends.
            CaptureError = await _bridge.AcquireCaptureAsync();
            _holdsCapture = CaptureError == null;

            if (CaptureError != null)
                _logger.LogWarning("CW audio source started but capture could not open: {Error}", CaptureError);
            else
                _logger.LogInformation("CW audio source started (devices open: {Open}, RX-only: {RxOnly})",
                    _bridge.DevicesOpen, _bridge.CaptureOnly);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Task? pump;
            CancellationTokenSource? cts;

            lock (_gate)
            {
                if (!IsRunning) return;

                _bridge.RxFrameCaptured -= OnBridgeFrame;
                IsRunning = false;

                _queue?.Writer.TryComplete();
                cts = _cts;
                pump = _pump;
                _cts = null;
                _pump = null;
                _queue = null;
            }

            if (_holdsCapture)
            {
                _holdsCapture = false;
                _bridge.ReleaseCapture();
            }
            CaptureError = null;

            cts?.Cancel();

            if (pump is not null)
            {
                try
                {
                    await pump.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
                {
                    // Stopping; a pump that will not wind up in two seconds is
                    // not worth blocking a shutdown for.
                }
            }

            cts?.Dispose();

            long dropped = DroppedFrames;
            if (dropped > 0)
                _logger.LogWarning("CW audio source stopped; {Dropped} frames were dropped", dropped);
            else
                _logger.LogInformation("CW audio source stopped");
        }

        /// <summary>
        /// On the PortAudio callback thread. Does the least possible: the frame
        /// is already a private copy from the bridge, so this only hands it to
        /// the channel, which never blocks because the channel drops instead.
        /// </summary>
        private void OnBridgeFrame(ReadOnlyMemory<float> frame)
        {
            var queue = _queue;
            if (queue is null) return;

            if (!queue.Writer.TryWrite(frame))
                Interlocked.Increment(ref _dropped);
        }

        private async Task PumpAsync(ChannelReader<ReadOnlyMemory<float>> reader, CancellationToken ct)
        {
            try
            {
                await foreach (var frame in reader.ReadAllAsync(ct))
                {
                    try
                    {
                        FrameAvailable?.Invoke(frame);
                    }
                    catch (Exception ex)
                    {
                        // One bad frame must not end the pump: the next one may
                        // be fine, and a dead pump is a reader that silently
                        // stops decoding.
                        _logger.LogDebug(ex, "CW decode of a frame threw - continuing");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal stop.
            }
        }

        public void Dispose()
        {
            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CW audio source disposal");
            }
        }
    }
}
