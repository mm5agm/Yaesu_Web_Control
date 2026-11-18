using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yaesu_Web_Control.Hubs;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>
    /// The radio audio waterfall: the same USB RX audio WSJT-X decodes, 0 to
    /// 4 kHz at 5.86 Hz a bin, sent to the pages showing it, with a guess at
    /// the mode of each signal in it.
    ///
    /// Works for every radio with a USB audio codec, and needs no ACC board.
    /// Like <see cref="FilterSpectrumService"/>, it holds the bridge's RX-only
    /// capture while at least one page is subscribed and lets it go 2 s after
    /// the last one leaves.
    ///
    /// Frames go to the "audioWaterfall" group on RadioStateUpdate as property
    /// "AudioWaterfall" (bytes, -140 to -20 dBFS as 0 to 255). Every 2 s the
    /// mode guesses go as "AudioWaterfallModes". When WSJT-X has decoded
    /// something at a signal's offset in the last 30 s, that wins over the
    /// guess.
    /// </summary>
    public sealed class AudioWaterfallService : IDisposable
    {
        public const string GroupName = "audioWaterfall";
        public const string FrameProperty = "AudioWaterfall";
        public const string ModesProperty = "AudioWaterfallModes";

        // 23.4 frames a second from the analyser; every other one is sent.
        private const int SendEvery = 2;
        private static readonly TimeSpan ClassifyEvery = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan HintAge = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ReleaseDebounce = TimeSpan.FromSeconds(2);

        private readonly RadioAudioBridgeService _bridge;
        private readonly IHubContext<RadioHub> _hub;
        private readonly IServiceProvider _services;
        private readonly ILogger<AudioWaterfallService> _logger;

        private readonly object _gate = new();
        private readonly HashSet<string> _subscribers = new(StringComparer.Ordinal);
        private readonly AudioWaterfallAnalyser _analyser = new();
        private readonly AudioModeClassifier _classifier;

        private bool _holdingCapture;
        private bool _listening;
        private int _frameCounter;
        // Environment.TickCount64 when the frame send in flight began; 0 when
        // none is. A send that never finished (or threw before it started)
        // used to hold a flag forever, and the page waited for audio for good.
        private long _sendStartedMs;
        private static readonly long StuckSendMs = 2000;
        private int _classifying;
        private string? _lastError;
        private CancellationTokenSource? _releaseCts;
        private System.Threading.Timer? _classifyTimer;

        public AudioWaterfallService(
            RadioAudioBridgeService bridge,
            IHubContext<RadioHub> hub,
            IServiceProvider services,
            ILogger<AudioWaterfallService> logger)
        {
            _bridge = bridge;
            _hub = hub;
            _services = services;
            _logger = logger;
            _classifier = new AudioModeClassifier(_analyser.BinHz, _analyser.FrameSeconds, _analyser.BinCount);
        }

        /// <summary>
        /// A page wants the waterfall. Null when frames will follow, or the
        /// reason they won't (no RX device set, device not found): the same
        /// words the Remote Audio bar would show.
        /// </summary>
        public async Task<string?> SubscribeAsync(string connectionId)
        {
            bool first;
            lock (_gate)
            {
                CancelReleaseLocked();
                first = _subscribers.Count == 0 && !_holdingCapture;
                _subscribers.Add(connectionId);
            }
            if (!first)
                return _lastError;

            var error = await _bridge.AcquireCaptureAsync();
            lock (_gate)
            {
                _lastError = error;
                if (error != null)
                {
                    _subscribers.Remove(connectionId);
                    _logger.LogInformation("Audio waterfall not started: {Error}", error);
                    return error;
                }

                _holdingCapture = true;
                if (!_listening)
                {
                    _analyser.Reset();
                    _classifier.Clear();
                    _frameCounter = 0;
                    _bridge.RxFrameCaptured += OnFrame;
                    _classifyTimer = new System.Threading.Timer(_ => ClassifyAndSend(), null, ClassifyEvery, ClassifyEvery);
                    _listening = true;
                }
            }

            _logger.LogInformation("Audio waterfall started (RX-only capture, {Bins} bins of {BinHz:0.00} Hz)",
                _analyser.BinCount, _analyser.BinHz);
            return null;
        }

        /// <summary>A page has gone. Lets the capture go when it was the last.</summary>
        public void Unsubscribe(string connectionId)
        {
            lock (_gate)
            {
                if (!_subscribers.Remove(connectionId) || _subscribers.Count > 0)
                    return;
                if (!_holdingCapture && !_listening)
                    return;
                CancelReleaseLocked();
                var cts = new CancellationTokenSource();
                _releaseCts = cts;
                _ = ReleaseAfterDebounceAsync(cts.Token);
            }
        }

        private void CancelReleaseLocked()
        {
            if (_releaseCts is null)
                return;
            try { _releaseCts.Cancel(); } catch { /* ignore */ }
            _releaseCts.Dispose();
            _releaseCts = null;
        }

        private async Task ReleaseAfterDebounceAsync(CancellationToken token)
        {
            try { await Task.Delay(ReleaseDebounce, token); }
            catch (OperationCanceledException) { return; }

            lock (_gate)
            {
                if (token.IsCancellationRequested || _subscribers.Count > 0)
                    return;
                StopListeningLocked();
                if (!_holdingCapture)
                    return;
                _holdingCapture = false;
                _bridge.ReleaseCapture();
                CancelReleaseLocked();
            }
            _logger.LogInformation("Audio waterfall stopped (last page left)");
        }

        private void StopListeningLocked()
        {
            if (!_listening)
                return;
            _bridge.RxFrameCaptured -= OnFrame;
            _classifyTimer?.Dispose();
            _classifyTimer = null;
            _listening = false;
        }

        // PortAudio callback thread: an 8192-point FFT every 2048 samples is
        // well under a millisecond, and the send is never awaited here.
        private void OnFrame(ReadOnlyMemory<float> frame) =>
            _analyser.Push(frame.Span, OnLevels);

        private void OnLevels(ReadOnlySpan<float> levels)
        {
            _classifier.Add(levels, DateTime.UtcNow);

            if (++_frameCounter % SendEvery != 0)
                return;
            long now = Environment.TickCount64;
            long started = Interlocked.Read(ref _sendStartedMs);
            if (started != 0)
            {
                if (now - started < StuckSendMs)
                    return;
                if (Interlocked.CompareExchange(ref _sendStartedMs, now, started) != started)
                    return;
                _logger.LogWarning("Audio waterfall: a frame send has not finished after {Ms} ms; sending again", now - started);
            }
            else if (Interlocked.CompareExchange(ref _sendStartedMs, now, 0) != 0)
            {
                return;
            }

            var bins = new byte[levels.Length];
            for (int i = 0; i < levels.Length; i++)
                bins[i] = AudioWaterfallAnalyser.ToByte(levels[i]);
            var payload = new
            {
                property = FrameProperty,
                value = new
                {
                    bins,
                    binHz = _analyser.BinHz,
                    frameSeconds = _analyser.FrameSeconds * SendEvery,
                    filterHz = UsbAudioFilterHz(_services.GetService<RadioStateService>()),
                },
            };

            Task send;
            try
            {
                send = _hub.Clients.Group(GroupName).SendAsync("RadioStateUpdate", payload);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _sendStartedMs, 0, now);
                _logger.LogWarning(ex, "Audio waterfall send threw");
                return;
            }
            _ = send.ContinueWith(t =>
            {
                Interlocked.CompareExchange(ref _sendStartedMs, 0, now);
                if (t.IsFaulted)
                    _logger.LogWarning(t.Exception, "Audio waterfall send failed");
            }, TaskScheduler.Default);
        }

        // Timer thread. A classification looks back 32 s and takes a few tens
        // of milliseconds; if one is still running the next is skipped.
        private void ClassifyAndSend()
        {
            if (Interlocked.CompareExchange(ref _classifying, 1, 0) != 0)
                return;
            try
            {
                var hints = _services.GetService<WsjtxUdpService>()?.RecentDecodeHints(HintAge);
                var guesses = _classifier.Classify(hints);
                var payload = new
                {
                    property = ModesProperty,
                    value = guesses.Select(ToWire).ToArray(),
                };
                _ = _hub.Clients.Group(GroupName).SendAsync("RadioStateUpdate", payload);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Audio mode guess failed");
            }
            finally
            {
                Interlocked.Exchange(ref _classifying, 0);
            }
        }

        /// <summary>
        /// The IF width, in Hz, of the receiver the radio sends to its USB
        /// audio, or null when it is not known. In RTTY-L at 300 Hz the
        /// waterfall shows one 300 Hz slice of hiss whatever the band is doing
        /// (Colin, 2026-11-18), so the page says so when it is narrow.
        /// Single-receiver radios like the FTdx10 send whichever VFO is in
        /// use; the dual-receiver ones send MAIN, as in click-to-centre.
        /// </summary>
        public static int? UsbAudioFilterHz(RadioStateService? state)
        {
            if (state is null) return null;
            bool useB = state.IsSingleReceiver && state.ActiveVfo == 1;
            return YaesuIfWidth.HzForCode(state.RadioModel,
                                          useB ? state.ModeB : state.ModeA,
                                          useB ? state.IfWidthB : state.IfWidthA);
        }

        /// <summary>What the page gets for each signal.</summary>
        public static object ToWire(AudioModeGuess g) => new
        {
            lowHz = Math.Round(g.LowHz),
            highHz = Math.Round(g.HighHz),
            mode = g.Mode,
            detail = g.Detail,
            fromWsjtx = g.FromWsjtx,
        };

        public void Dispose()
        {
            lock (_gate)
            {
                CancelReleaseLocked();
                StopListeningLocked();
                _subscribers.Clear();
                if (_holdingCapture)
                {
                    _holdingCapture = false;
                    _bridge.ReleaseCapture();
                }
            }
        }
    }
}
