using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RadioWebControl.Core.Services.Spectrum;
using Yaesu_Web_Control.Hubs;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>
    /// Feeds the Filter Function Display with what the receiver is actually
    /// passing: the radio's RX audio, analysed on the host and pushed to every
    /// browser that asked for it.
    ///
    /// The panel used to fill its passband with random numbers when no Remote
    /// Audio session was running (#161). The honest picture is the one the
    /// radio's own filter display draws -- the spectrum of the audio after the
    /// filter -- and the host already has that audio: the bridge can open the
    /// radio's USB capture endpoint without streaming anything to a browser
    /// (<see cref="RadioAudioBridgeService.AcquireCaptureAsync"/>, which is how
    /// the CW reader listens). So this takes a capture hold while at least one
    /// page is subscribed, runs the audio through
    /// <see cref="AudioSpectrumAnalyser"/>, and sends the bins to the
    /// "filterSpectrum" SignalR group at a modest rate.
    ///
    /// The frame is shaped like the browser's own AnalyserNode output so the
    /// panel draws it with the code it already had for Remote Audio.
    ///
    /// Nothing is opened until a page subscribes, and the hold is released
    /// when the last one goes, so a host with no RX device configured -- or
    /// the setting switched off -- costs nothing and the panel stays empty.
    /// </summary>
    public sealed class FilterSpectrumService : IDisposable
    {
        public const string GroupName = "filterSpectrum";

        // 2048 @ 48 kHz: 23.4 Hz per bin, a frame every 43 ms. Every other
        // frame is sent, so ~12 fps: enough that noise looks alive and a
        // signal appears the moment it does, cheap enough to run on every
        // open tab all day. 256 bins reach 6 kHz, past any audio passband.
        private const int FftSize   = 2048;
        private const int MaxHz     = 6000;
        private const int SendEvery = 2;

        private readonly RadioAudioBridgeService _bridge;
        private readonly IHubContext<RadioHub> _hub;
        private readonly ISettingsService _settings;
        private readonly ILogger<FilterSpectrumService> _logger;

        private readonly object _gate = new();
        private readonly HashSet<string> _subscribers = new(StringComparer.Ordinal);
        private readonly AudioSpectrumAnalyser _analyser =
            new(AudioConstants.SampleRate, FftSize, MaxHz);

        private bool _holdingCapture;
        private bool _listening;
        private int  _frameCounter;
        private int  _sendInFlight;
        private string? _lastError;

        public FilterSpectrumService(
            RadioAudioBridgeService bridge,
            IHubContext<RadioHub> hub,
            ISettingsService settings,
            ILogger<FilterSpectrumService> logger)
        {
            _bridge   = bridge;
            _hub      = hub;
            _settings = settings;
            _logger   = logger;
        }

        /// <summary>
        /// A page wants frames. Returns null when frames will follow, or a
        /// human-readable reason they will not (setting off, no RX device
        /// configured, device not found). The reason is the same string the
        /// Remote Audio bar would show, so the fix is the same too.
        /// </summary>
        public async Task<string?> SubscribeAsync(string connectionId)
        {
            var settings = await _settings.GetSettingsAsync();
            if (!settings.FilterScopeAudioEnabled)
                return "Filter display audio is switched off in Settings > Remote Audio.";

            bool first;
            lock (_gate)
            {
                first = _subscribers.Count == 0;
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
                    // The bridge undoes its own hold count on failure. Drop
                    // the subscriber too, so the next page to arrive is a
                    // "first" again and retries after the operator has fixed
                    // the setting -- there is nothing to retry for otherwise.
                    _subscribers.Remove(connectionId);
                    _logger.LogInformation("Filter display audio not started: {Error}", error);
                    return error;
                }

                _holdingCapture = true;
                if (!_listening)
                {
                    _analyser.Reset();
                    _frameCounter = 0;
                    _bridge.RxFrameCaptured += OnFrame;
                    _listening = true;
                }
            }

            _logger.LogInformation("Filter display audio started (RX-only capture, {Bins} bins to {MaxHz} Hz)",
                _analyser.BinCount, MaxHz);
            return null;
        }

        /// <summary>A page has gone. Releases the capture when it was the last.</summary>
        public void Unsubscribe(string connectionId)
        {
            bool last;
            lock (_gate)
            {
                if (!_subscribers.Remove(connectionId))
                    return;
                last = _subscribers.Count == 0;
                if (last && _listening)
                {
                    _bridge.RxFrameCaptured -= OnFrame;
                    _listening = false;
                }
            }

            if (!last || !_holdingCapture)
                return;

            _holdingCapture = false;
            _bridge.ReleaseCapture();
            _logger.LogInformation("Filter display audio stopped (last page left)");
        }

        // PortAudio callback thread. The analyser copies into its own buffer
        // and a 2048-point FFT is tens of microseconds; the send is handed
        // off, never awaited here.
        private void OnFrame(ReadOnlyMemory<float> frame)
        {
            if (!_analyser.Push(frame.Span))
                return;

            if (++_frameCounter % SendEvery != 0)
                return;

            // Drop a frame rather than queue behind a slow client: the next
            // one is 86 ms away and carries the same smoothing history.
            if (Interlocked.CompareExchange(ref _sendInFlight, 1, 0) != 0)
                return;

            var bins = _analyser.Bins.ToArray();
            var payload = new
            {
                property = "FilterSpectrum",
                value = new
                {
                    bins,
                    binHz      = _analyser.BinHz,
                    sampleRate = _analyser.SampleRate,
                    fftSize    = _analyser.FftSize,
                },
            };

            _ = _hub.Clients.Group(GroupName)
                .SendAsync("RadioStateUpdate", payload)
                .ContinueWith(t =>
                {
                    Interlocked.Exchange(ref _sendInFlight, 0);
                    if (t.IsFaulted)
                        _logger.LogDebug(t.Exception, "Filter spectrum send failed");
                }, TaskScheduler.Default);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_listening)
                {
                    _bridge.RxFrameCaptured -= OnFrame;
                    _listening = false;
                }
                _subscribers.Clear();
            }
            if (_holdingCapture)
            {
                _holdingCapture = false;
                _bridge.ReleaseCapture();
            }
        }
    }
}
