using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using PortAudioSharp;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>
    /// Bridges radio USB audio (PortAudio) to a single browser WebSocket client
    /// using Opus or PCM16 frames. RX packets are sent as soon as a frame is
    /// ready (not on a timer) to keep LAN latency low.
    /// </summary>
    public sealed class RadioAudioBridgeService : IHostedService, IDisposable
    {
        private readonly ILogger<RadioAudioBridgeService> _logger;
        private readonly ISettingsService _settings;
        private readonly AudioSessionManager _sessions;

        private readonly object _deviceLock = new();

        private PortAudioSharp.Stream? _captureStream;
        private PortAudioSharp.Stream? _playbackStream;
        private OpusCodec? _codec;
        private string _codecName = AudioConstants.CodecOpus;
        private float _rxGain = 1f;
        private float _txGain = 1f;
        private int _rxSeq;
        private CancellationTokenSource? _pumpCts;
        private Task? _sendTask;
        private Task? _levelsTask;
        private WebSocket? _activeSocket;
        private Channel<byte[]>? _outChannel;
        private readonly float[] _captureAccum = new float[AudioConstants.FrameSamples * 4];
        private int _captureAccumLen;
        private readonly float[] _playbackRing = new float[AudioConstants.PlaybackRingMaxSamples];
        private int _playbackWrite;
        private int _playbackRead;
        private int _playbackCount;
        private readonly object _playbackLock = new();
        private readonly object _graceLock = new();
        private bool _devicesOpen;
        private float _rxLevel;
        private float _txLevel;
        private string? _openRxDeviceKey;
        private string? _openTxDeviceKey;
        private CancellationTokenSource? _deviceCloseCts;
        private float[] _cbCapRaw = Array.Empty<float>();
        private float[] _cbCapMono = Array.Empty<float>();
        private float[] _cbCapResampled = Array.Empty<float>();
        private float[] _cbPlayBridge = Array.Empty<float>();
        private float[] _cbPlayMono = Array.Empty<float>();
        private float[] _cbPlayInterleaved = Array.Empty<float>();
        private readonly SemaphoreSlim _wsSendLock = new(1, 1);

        /// <summary>
        /// Pop-out handoff closes the Index WebSocket then immediately opens
        /// /RemoteAudio. Without a grace window, WASAPI teardown + reopen on
        /// the same USB codec native-crashes the host (no managed exception).
        /// </summary>
        private static readonly TimeSpan DeviceCloseGrace = TimeSpan.FromSeconds(3);

        public RadioAudioBridgeService(
            ILogger<RadioAudioBridgeService> logger,
            ISettingsService settings,
            AudioSessionManager sessions)
        {
            _logger = logger;
            _settings = settings;
            _sessions = sessions;
        }

        /// <summary>
        /// Raised for each frame of RX audio, mono float at
        /// AudioConstants.SampleRate and AudioConstants.FrameSamples long,
        /// after RX gain has been applied.
        ///
        /// This exists so the CW decoder can listen to the radio without
        /// opening the capture device a second time. Two PortAudio streams on
        /// one USB codec is a fight nobody wins, and the bridge already
        /// produces exactly the frames Core's ICwAudioSource documents.
        ///
        /// Raised ON THE PORTAUDIO CALLBACK THREAD. Handlers must return
        /// promptly - copy the frame and get off this thread. Anything that
        /// blocks here is an audio dropout for the listening operator.
        /// </summary>
        public event Action<ReadOnlyMemory<float>>? RxFrameCaptured;

        public float RxLevel => _rxLevel;
        public float TxLevel => _txLevel;
        public bool DevicesOpen => _devicesOpen;
        public string ActiveCodec => _codecName;
        public float RxGain => _rxGain;
        public float TxGain => _txGain;

        /// <summary>Update live gains (session or idle). Values are clamped.</summary>
        public void SetGains(float? rx = null, float? tx = null)
        {
            if (rx.HasValue) _rxGain = Math.Clamp(rx.Value, 0.05f, 4f);
            if (tx.HasValue) _txGain = Math.Clamp(tx.Value, 0.05f, 4f);
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            CancelScheduledDeviceClose();
            await StopSessionAsync(forceCloseDevices: true);
        }

        public async Task HandleWebSocketAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var settings = await _settings.GetSettingsAsync();
            if (!settings.AudioStreamingEnabled)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Remote audio is disabled in Settings.");
                return;
            }

            var socket = await context.WebSockets.AcceptWebSocketAsync();
            var connectionId = context.Connection.Id;

            if (!_sessions.TryAcquire(connectionId, socket))
            {
                var busy = AudioWireProtocol.FrameControl(0, new { cmd = "busy", message = "Another audio session is already active." });
                await socket.SendAsync(busy, WebSocketMessageType.Binary, true, CancellationToken.None);
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "busy", CancellationToken.None);
                return;
            }

            _activeSocket = socket;
            _logger.LogInformation("Audio WebSocket connected ({Id})", connectionId);

            try
            {
                await RunSessionAsync(socket, settings, context.RequestAborted);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audio session failed");
            }
            finally
            {
                await StopSessionAsync(socket);
                _sessions.Release(connectionId);
                if (socket.State == WebSocketState.Open)
                {
                    try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                    catch { /* ignore */ }
                }
                socket.Dispose();
                _logger.LogInformation("Audio WebSocket disconnected ({Id})", connectionId);
            }
        }

        private async Task RunSessionAsync(WebSocket socket, ApplicationSettings settings, CancellationToken ct)
        {
            CancelScheduledDeviceClose();

            _rxGain = Math.Clamp(settings.AudioRxGain, 0.05f, 4f);
            _txGain = Math.Clamp(settings.AudioTxGain, 0.05f, 4f);
            _codecName = AudioConstants.CodecOpus;

            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            helloCts.CancelAfter(TimeSpan.FromSeconds(10));
            var hello = await ReceiveOneMessageAsync(socket, helloCts.Token);
            if (hello == null)
            {
                await SendControlAsync(socket, new { cmd = "error", message = "Expected hello control message." });
                return;
            }

            if (!AudioWireProtocol.TryParse(hello, out var type, out _, out var payload) || type != AudioConstants.MsgControl)
            {
                await SendControlAsync(socket, new { cmd = "error", message = "First message must be control hello." });
                return;
            }

            using var doc = JsonDocument.Parse(payload.ToArray());
            var root = doc.RootElement;
            var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;
            if (!string.Equals(cmd, "hello", StringComparison.OrdinalIgnoreCase))
            {
                await SendControlAsync(socket, new { cmd = "error", message = "Expected cmd=hello." });
                return;
            }

            // Client sends codecs in preference order; pick the first we support.
            // Opus is preferred for bandwidth; PCM16 remains the fallback.
            _codecName = AudioConstants.CodecPcm16;
            if (root.TryGetProperty("codecs", out var codecs) && codecs.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in codecs.EnumerateArray())
                {
                    var s = el.GetString();
                    if (s == AudioConstants.CodecOpus)
                    {
                        _codecName = AudioConstants.CodecOpus;
                        break;
                    }
                    if (s == AudioConstants.CodecPcm16)
                    {
                        _codecName = AudioConstants.CodecPcm16;
                        break;
                    }
                }
            }

            EnsureCodec();

            bool reused = AudioDeviceEnumerator.Invoke(() =>
            {
                if (CanReuseDevices(settings)) return true;
                if (_devicesOpen)
                {
                    _logger.LogInformation("Audio device selection changed — reopening PortAudio streams");
                    CloseDevicesAndCodecOnAudioThread();
                }
                return false;
            });
            if (reused)
            {
                _logger.LogInformation("Reusing open audio devices (session handoff within grace window)");
            }
            else
            {
                EnsureCodec();
                string? openError = OpenDevices(settings);
                if (openError != null)
                {
                    await SendControlAsync(socket, new { cmd = "error", message = openError });
                    return;
                }
            }

            await SendControlAsync(socket, new
            {
                cmd = "ready",
                codec = _codecName,
                sampleRate = AudioConstants.SampleRate,
                frameSamples = AudioConstants.FrameSamples
            });

            _outChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(48)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _sendTask = Task.Factory.StartNew(
                () => SendPumpAsync(socket, _pumpCts.Token),
                _pumpCts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            _levelsTask = Task.Run(() => LevelsPumpAsync(socket, _pumpCts.Token), _pumpCts.Token);

            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var msg = ms.ToArray();
                if (msg.Length < 4) continue;
                uint bodyLen = BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(0, 4));
                if (msg.Length < 4 + bodyLen) continue;
                var body = msg.AsSpan(4, (int)bodyLen);
                if (!AudioWireProtocol.TryParse(body, out var msgType, out _, out var msgPayload)) continue;

                if (msgType == AudioConstants.MsgOpusTx || msgType == AudioConstants.MsgPcmTx)
                    HandleTxAudio(msgType, msgPayload);
                else if (msgType == AudioConstants.MsgControl)
                    HandleControl(msgPayload);
            }
        }

        private void HandleControl(ReadOnlySpan<byte> payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload.ToArray());
                var root = doc.RootElement;
                var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;
                if (cmd == "setGain")
                {
                    if (root.TryGetProperty("rx", out var rx) && rx.TryGetSingle(out var rxf))
                        _rxGain = Math.Clamp(rxf, 0.05f, 4f);
                    if (root.TryGetProperty("tx", out var tx) && tx.TryGetSingle(out var txf))
                        _txGain = Math.Clamp(txf, 0.05f, 4f);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring bad audio control message");
            }
        }

        private void HandleTxAudio(byte msgType, ReadOnlySpan<byte> payload)
        {
            // Opus decode needs room for WebCodecs' default 20 ms packets (or longer).
            Span<float> pcm = stackalloc float[AudioConstants.OpusDecodeMaxSamples];
            int n;
            try
            {
                if (msgType == AudioConstants.MsgOpusTx)
                {
                    if (_codec == null) return;
                    n = _codec.Decode(payload, pcm);
                }
                else
                {
                    n = Math.Min(AudioConstants.FrameSamples, payload.Length / 2);
                    for (int i = 0; i < n; i++)
                    {
                        short s = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(i * 2, 2));
                        pcm[i] = s / 32768f;
                    }
                }
            }
            catch (Exception ex)
            {
                // A single bad Opus packet must not tear down the WebSocket session.
                _logger.LogDebug(ex, "Dropping bad TX audio frame (type={Type}, {Bytes} bytes)", msgType, payload.Length);
                return;
            }

            if (n <= 0) return;

            float peak = 0;
            for (int i = 0; i < n; i++)
            {
                float v = pcm[i] * _txGain;
                pcm[i] = Math.Clamp(v, -1f, 1f);
                float a = Math.Abs(pcm[i]);
                if (a > peak) peak = a;
            }
            _txLevel = peak;
            PushPlayback(pcm[..n]);
        }

        private void PushPlayback(ReadOnlySpan<float> samples)
        {
            lock (_playbackLock)
            {
                foreach (var s in samples)
                {
                    if (_playbackCount >= _playbackRing.Length)
                    {
                        _playbackRead = (_playbackRead + 1) % _playbackRing.Length;
                        _playbackCount--;
                    }
                    _playbackRing[_playbackWrite] = s;
                    _playbackWrite = (_playbackWrite + 1) % _playbackRing.Length;
                    _playbackCount++;
                }
            }
        }

        private int PullPlayback(Span<float> dest)
        {
            lock (_playbackLock)
            {
                int n = Math.Min(dest.Length, _playbackCount);
                for (int i = 0; i < n; i++)
                {
                    dest[i] = _playbackRing[_playbackRead];
                    _playbackRead = (_playbackRead + 1) % _playbackRing.Length;
                }
                for (int i = n; i < dest.Length; i++) dest[i] = 0;
                _playbackCount -= n;
                return n;
            }
        }

        private bool CanReuseDevices(ApplicationSettings settings) =>
            _devicesOpen
            && string.Equals(_openRxDeviceKey, settings.AudioRadioRxDevice, StringComparison.Ordinal)
            && string.Equals(_openTxDeviceKey, settings.AudioRadioTxDevice, StringComparison.Ordinal);

        private void EnsureCodec()
        {
            if (_codecName == AudioConstants.CodecOpus)
                _codec ??= new OpusCodec();
            else
            {
                _codec?.Dispose();
                _codec = null;
            }
        }

        private void CancelScheduledDeviceClose()
        {
            lock (_graceLock)
            {
                if (_deviceCloseCts == null) return;
                try { _deviceCloseCts.Cancel(); } catch { /* ignore */ }
                _deviceCloseCts.Dispose();
                _deviceCloseCts = null;
            }
        }

        private void ScheduleDeviceClose()
        {
            CancelScheduledDeviceClose();
            var cts = new CancellationTokenSource();
            lock (_graceLock) _deviceCloseCts = cts;
            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DeviceCloseGrace, token);
                    AudioDeviceEnumerator.Invoke(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        _logger.LogInformation(
                            "Audio device grace ({Seconds}s) elapsed — closing PortAudio streams",
                            DeviceCloseGrace.TotalSeconds);
                        CloseDevicesAndCodecOnAudioThread();
                    });
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        private void CloseDevicesAndCodecOnAudioThread()
        {
            CloseDevicesOnAudioThread();
            var codec = _codec;
            _codec = null;
            try { codec?.Dispose(); } catch { /* ignore */ }
            _openRxDeviceKey = null;
            _openTxDeviceKey = null;
        }

        private string? OpenDevices(ApplicationSettings settings) =>
            AudioDeviceEnumerator.Invoke(() => OpenDevicesOnAudioThread(settings));

        private string? OpenDevicesOnAudioThread(ApplicationSettings settings)
        {
            lock (_deviceLock)
            {
                try
                {
                    AudioDeviceEnumerator.EnsureInitialized();

                    // Never fall back to the OS default I/O devices. Blank TX in
                    // particular opens the PC speakers and loops the browser mic
                    // into the room (and never reaches the radio USB codec).
                    if (string.IsNullOrWhiteSpace(settings.AudioRadioRxDevice))
                        return "Radio RX (capture) device is not set. Pick the radio USB recording endpoint in Settings → Remote Audio.";
                    if (string.IsNullOrWhiteSpace(settings.AudioRadioTxDevice))
                        return "Radio TX (playback) device is not set. Pick the radio USB Speakers/playback endpoint in Settings → Remote Audio (blank would use PC speakers and cause mic feedback).";

                    int rxIndex = AudioDeviceEnumerator.FindDeviceIndex(settings.AudioRadioRxDevice, requireInput: true, requireOutput: false);
                    int txIndex = AudioDeviceEnumerator.FindDeviceIndex(settings.AudioRadioTxDevice, requireInput: false, requireOutput: true);

                    if (rxIndex < 0)
                        return "Radio RX (capture) device not found. Pick it in Settings → Remote Audio.";
                    if (txIndex < 0)
                        return "Radio TX (playback) device not found. Pick it in Settings → Remote Audio.";

                    var rxInfo = PortAudio.GetDeviceInfo(rxIndex);
                    var txInfo = PortAudio.GetDeviceInfo(txIndex);

                    // WASAPI shared mode usually requires the device mix format
                    // (typically stereo). Opening mono against a 2-ch USB CODEC
                    // fails with InvalidChannelCount — open stereo and mix to mono.
                    int inCh = rxInfo.maxInputChannels >= 2 ? 2 : Math.Max(1, rxInfo.maxInputChannels);
                    int outCh = txInfo.maxOutputChannels >= 2 ? 2 : Math.Max(1, txInfo.maxOutputChannels);

                    // Same for sample rate: WASAPI rejects a rate that isn't the
                    // shared-mode mix format. Open at each device's default and
                    // resample to/from the 48 kHz bridge.
                    int rxRate = PickDeviceSampleRate(rxInfo.defaultSampleRate);
                    int txRate = PickDeviceSampleRate(txInfo.defaultSampleRate);
                    uint rxFrames = DeviceFramesPerBuffer(rxRate);
                    uint txFrames = DeviceFramesPerBuffer(txRate);

                    const double targetLatency = 0.01;
                    double inLat = Math.Min(rxInfo.defaultLowInputLatency > 0 ? rxInfo.defaultLowInputLatency : targetLatency, 0.02);
                    double outLat = Math.Min(txInfo.defaultLowOutputLatency > 0 ? txInfo.defaultLowOutputLatency : targetLatency, 0.02);
                    inLat = Math.Max(inLat, 0.005);
                    outLat = Math.Max(outLat, 0.005);

                    var inParams = new StreamParameters
                    {
                        device = rxIndex,
                        channelCount = inCh,
                        sampleFormat = SampleFormat.Float32,
                        suggestedLatency = inLat,
                        hostApiSpecificStreamInfo = IntPtr.Zero
                    };

                    var outParams = new StreamParameters
                    {
                        device = txIndex,
                        channelCount = outCh,
                        sampleFormat = SampleFormat.Float32,
                        suggestedLatency = outLat,
                        hostApiSpecificStreamInfo = IntPtr.Zero
                    };

                    PortAudioSharp.Stream.Callback captureCb = (IntPtr input, IntPtr output, uint frameCount,
                        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData) =>
                    {
                        // An exception escaping into portaudio.dll kills the process
                        // with no managed log — wrap the whole callback.
                        try
                        {
                            if (!_devicesOpen || input == IntPtr.Zero) return StreamCallbackResult.Continue;
                            int interleaved = (int)frameCount * inCh;
                            EnsureCallbackBuffer(ref _cbCapRaw, interleaved);
                            Marshal.Copy(input, _cbCapRaw, 0, interleaved);
                            EnsureCallbackBuffer(ref _cbCapMono, (int)frameCount);
                            if (inCh == 1)
                            {
                                Array.Copy(_cbCapRaw, _cbCapMono, (int)frameCount);
                            }
                            else
                            {
                                for (int i = 0; i < (int)frameCount; i++)
                                    _cbCapMono[i] = 0.5f * (_cbCapRaw[i * inCh] + _cbCapRaw[i * inCh + 1]);
                            }
                            int resampledLen = ResampleInto(
                                _cbCapMono.AsSpan(0, (int)frameCount),
                                rxRate,
                                AudioConstants.SampleRate,
                                ref _cbCapResampled);
                            OnCaptureSamples(_cbCapResampled.AsSpan(0, resampledLen));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Audio capture callback dropped a buffer");
                        }
                        return StreamCallbackResult.Continue;
                    };

                    PortAudioSharp.Stream.Callback playbackCb = (IntPtr input, IntPtr output, uint frameCount,
                        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData) =>
                    {
                        try
                        {
                            if (output == IntPtr.Zero) return StreamCallbackResult.Continue;
                            int bridgeCount = Math.Max(1,
                                (int)Math.Round(frameCount * (double)AudioConstants.SampleRate / txRate));
                            EnsureCallbackBuffer(ref _cbPlayBridge, bridgeCount);
                            PullPlayback(_cbPlayBridge.AsSpan(0, bridgeCount));
                            int monoLen = ResampleInto(
                                _cbPlayBridge.AsSpan(0, bridgeCount),
                                AudioConstants.SampleRate,
                                txRate,
                                ref _cbPlayMono);
                            if (monoLen != frameCount)
                            {
                                EnsureCallbackBuffer(ref _cbPlayMono, (int)frameCount);
                                if (monoLen < frameCount)
                                    _cbPlayMono.AsSpan(monoLen, (int)frameCount - monoLen).Clear();
                                monoLen = (int)frameCount;
                            }
                            if (outCh == 1)
                            {
                                Marshal.Copy(_cbPlayMono, 0, output, (int)frameCount);
                            }
                            else
                            {
                                int interleaved = (int)frameCount * outCh;
                                EnsureCallbackBuffer(ref _cbPlayInterleaved, interleaved);
                                for (int i = 0; i < (int)frameCount; i++)
                                {
                                    float s = _cbPlayMono[i];
                                    _cbPlayInterleaved[i * outCh] = s;
                                    _cbPlayInterleaved[i * outCh + 1] = s;
                                }
                                Marshal.Copy(_cbPlayInterleaved, 0, output, interleaved);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Audio playback callback dropped a buffer");
                        }
                        return StreamCallbackResult.Continue;
                    };

                    _captureStream = new PortAudioSharp.Stream(
                        inParams: inParams,
                        outParams: null,
                        sampleRate: rxRate,
                        framesPerBuffer: rxFrames,
                        streamFlags: StreamFlags.ClipOff,
                        callback: captureCb,
                        userData: IntPtr.Zero);

                    _playbackStream = new PortAudioSharp.Stream(
                        inParams: null,
                        outParams: outParams,
                        sampleRate: txRate,
                        framesPerBuffer: txFrames,
                        streamFlags: StreamFlags.ClipOff,
                        callback: playbackCb,
                        userData: IntPtr.Zero);

                    // Claim the stream slot before Start so a concurrent Settings
                    // /api/audio/devices call cannot GetDeviceInfo while WASAPI
                    // callbacks are already running.
                    AudioDeviceEnumerator.AddOpenStream();
                    _devicesOpen = true;
                    _captureStream.Start();
                    _playbackStream.Start();
                    _openRxDeviceKey = settings.AudioRadioRxDevice;
                    _openTxDeviceKey = settings.AudioRadioTxDevice;
                    _logger.LogInformation(
                        "Audio devices open — RX '{Rx}' (#{RxI}, {InCh}ch @{RxRate} Hz), TX '{Tx}' (#{TxI}, {OutCh}ch @{TxRate} Hz), codec={Codec}, bridge={Bridge} Hz, frame={Frame} samples",
                        rxInfo.name, rxIndex, inCh, rxRate, txInfo.name, txIndex, outCh, txRate, _codecName, AudioConstants.SampleRate, AudioConstants.FrameSamples);
                    return null;
                }
                catch (PortAudioException ex)
                {
                    string detail = PortAudio.GetErrorText(ex.ErrorCode);
                    _logger.LogError(ex, "Failed to open audio devices ({Code}: {Detail})", ex.ErrorCode, detail);
                    CloseDevices();
                    return $"Failed to open audio devices: {ex.ErrorCode} — {detail}";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to open audio devices");
                    CloseDevices();
                    return $"Failed to open audio devices: {ex.Message}";
                }
            }
        }

        private static int PickDeviceSampleRate(double defaultRate)
        {
            int rate = (int)Math.Round(defaultRate);
            if (rate >= 8_000 && rate <= 192_000) return rate;
            return AudioConstants.SampleRate;
        }

        /// <summary>~10 ms worth of frames at the device rate (matches bridge frame duration).</summary>
        private static uint DeviceFramesPerBuffer(int deviceRate) =>
            (uint)Math.Max(64, (int)Math.Round(deviceRate * AudioConstants.FrameSamples / (double)AudioConstants.SampleRate));

        private static void EnsureCallbackBuffer(ref float[] buffer, int length)
        {
            if (buffer.Length >= length) return;
            var next = new float[length];
            if (buffer.Length > 0)
                Array.Copy(buffer, next, buffer.Length);
            buffer = next;
        }

        /// <summary>Linear resample into a reusable scratch buffer; returns output length.</summary>
        private static int ResampleInto(ReadOnlySpan<float> input, int inRate, int outRate, ref float[] scratch)
        {
            if (input.Length == 0) return 0;
            if (inRate == outRate)
            {
                EnsureCallbackBuffer(ref scratch, input.Length);
                input.CopyTo(scratch);
                return input.Length;
            }

            int outLen = Math.Max(1, (int)Math.Round(input.Length * (double)outRate / inRate));
            EnsureCallbackBuffer(ref scratch, outLen);
            double ratio = (double)inRate / outRate;
            int last = input.Length - 1;
            for (int i = 0; i < outLen; i++)
            {
                double src = i * ratio;
                int i0 = (int)src;
                if (i0 >= last)
                {
                    scratch[i] = input[last];
                    continue;
                }
                double frac = src - i0;
                scratch[i] = (float)(input[i0] + (input[i0 + 1] - input[i0]) * frac);
            }
            return outLen;
        }

        private static float[] Resample(ReadOnlySpan<float> input, int inRate, int outRate)
        {
            if (inRate == outRate || input.Length == 0)
                return input.ToArray();

            var scratch = Array.Empty<float>();
            int len = ResampleInto(input, inRate, outRate, ref scratch);
            var output = new float[len];
            scratch.AsSpan(0, len).CopyTo(output);
            return output;
        }

        private void OnCaptureSamples(ReadOnlySpan<float> samples)
        {
            Span<float> frame = stackalloc float[AudioConstants.FrameSamples];
            int offset = 0;
            while (offset < samples.Length)
            {
                int space = _captureAccum.Length - _captureAccumLen;
                int take = Math.Min(space, samples.Length - offset);
                samples.Slice(offset, take).CopyTo(_captureAccum.AsSpan(_captureAccumLen, take));
                _captureAccumLen += take;
                offset += take;

                while (_captureAccumLen >= AudioConstants.FrameSamples)
                {
                    _captureAccum.AsSpan(0, AudioConstants.FrameSamples).CopyTo(frame);
                    float peak = 0;
                    for (int i = 0; i < frame.Length; i++)
                    {
                        float v = Math.Clamp(frame[i] * _rxGain, -1f, 1f);
                        frame[i] = v;
                        float a = Math.Abs(v);
                        if (a > peak) peak = a;
                    }
                    _rxLevel = peak;

                    // Fan the frame out to the CW decoder, if anything is
                    // listening. One array copy on the audio thread; the
                    // listener is responsible for getting off it.
                    var rxListeners = RxFrameCaptured;
                    if (rxListeners is not null)
                    {
                        var copy = new float[AudioConstants.FrameSamples];
                        frame.CopyTo(copy);
                        try
                        {
                            rxListeners(copy);
                        }
                        catch (Exception ex)
                        {
                            // A broken listener must never take the audio
                            // bridge down with it.
                            _logger.LogDebug(ex, "RxFrameCaptured listener threw - ignoring");
                        }
                    }

                    byte[] packet;
                    byte msgType;
                    if (_codecName == AudioConstants.CodecOpus && _codec != null)
                    {
                        try
                        {
                            packet = _codec.Encode(frame);
                            msgType = AudioConstants.MsgOpusRx;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Opus RX encode failed — dropping frame");
                            int remainErr = _captureAccumLen - AudioConstants.FrameSamples;
                            if (remainErr > 0)
                                Array.Copy(_captureAccum, AudioConstants.FrameSamples, _captureAccum, 0, remainErr);
                            _captureAccumLen = remainErr;
                            continue;
                        }
                    }
                    else
                    {
                        packet = new byte[AudioConstants.FrameSamples * 2];
                        for (int i = 0; i < AudioConstants.FrameSamples; i++)
                        {
                            short s = (short)Math.Clamp((int)(frame[i] * 32767f), short.MinValue, short.MaxValue);
                            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(i * 2, 2), s);
                        }
                        msgType = AudioConstants.MsgPcmRx;
                    }

                    uint seq = (uint)Interlocked.Increment(ref _rxSeq);
                    var framed = AudioWireProtocol.Frame(msgType, seq, packet);
                    _outChannel?.Writer.TryWrite(framed);

                    int remain = _captureAccumLen - AudioConstants.FrameSamples;
                    if (remain > 0)
                        Array.Copy(_captureAccum, AudioConstants.FrameSamples, _captureAccum, 0, remain);
                    _captureAccumLen = remain;
                }
            }
        }

        private async Task SendPumpAsync(WebSocket socket, CancellationToken ct)
        {
            var channel = _outChannel;
            if (channel == null) return;

            try
            {
                await foreach (var frame in channel.Reader.ReadAllAsync(ct))
                {
                    if (socket.State != WebSocketState.Open) break;
                    await SendWebSocketAsync(socket, frame, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Audio send pump failed");
            }
        }

        private async Task LevelsPumpAsync(WebSocket socket, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(200, ct);
                    if (socket.State != WebSocketState.Open) break;
                    var levels = AudioWireProtocol.FrameControl(
                        (uint)Interlocked.Increment(ref _rxSeq),
                        new { cmd = "levels", rx = _rxLevel, tx = _txLevel });
                    await SendWebSocketAsync(socket, levels, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Audio levels pump failed");
                    break;
                }
            }
        }

        private async Task StopSessionAsync(WebSocket? sessionSocket = null, bool forceCloseDevices = false)
        {
            // A pop-out reconnect can finish RunSessionAsync on connection B while
            // connection A is still in this finally block. Only tear down shared
            // bridge state when this socket still owns it (or on host shutdown).
            bool ownsBridge = forceCloseDevices
                || sessionSocket == null
                || ReferenceEquals(_activeSocket, sessionSocket);

            if (!ownsBridge)
                return;

            try { _outChannel?.Writer.TryComplete(); } catch { /* ignore */ }
            try { _pumpCts?.Cancel(); } catch { /* ignore */ }
            if (ReferenceEquals(_activeSocket, sessionSocket))
            {
                try { sessionSocket?.Abort(); } catch { /* ignore */ }
            }
            if (_sendTask != null)
            {
                try { await _sendTask; } catch { /* ignore */ }
                _sendTask = null;
            }
            if (_levelsTask != null)
            {
                try { await _levelsTask; } catch { /* ignore */ }
                _levelsTask = null;
            }
            _pumpCts?.Dispose();
            _pumpCts = null;
            _outChannel = null;
            _activeSocket = null;
            lock (_playbackLock)
            {
                _playbackCount = 0;
                _playbackRead = 0;
                _playbackWrite = 0;
            }
            _captureAccumLen = 0;
            _rxLevel = 0;
            _txLevel = 0;

            if (forceCloseDevices)
            {
                CancelScheduledDeviceClose();
                AudioDeviceEnumerator.Invoke(CloseDevicesAndCodecOnAudioThread);
            }
            else if (_devicesOpen)
            {
                ScheduleDeviceClose();
            }
        }

        private void CloseDevices() =>
            AudioDeviceEnumerator.Invoke(CloseDevicesOnAudioThread);

        private void CloseDevicesOnAudioThread()
        {
            lock (_deviceLock)
            {
                bool wasOpen = _devicesOpen;
                _devicesOpen = false;
                try { _captureStream?.Stop(); } catch { /* ignore */ }
                try { _captureStream?.Dispose(); } catch { /* ignore */ }
                _captureStream = null;
                try { _playbackStream?.Stop(); } catch { /* ignore */ }
                try { _playbackStream?.Dispose(); } catch { /* ignore */ }
                _playbackStream = null;
                if (wasOpen)
                    AudioDeviceEnumerator.ReleaseOpenStream();
            }
        }

        private async Task SendWebSocketAsync(WebSocket socket, byte[] frame, CancellationToken ct)
        {
            await _wsSendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            }
            finally
            {
                _wsSendLock.Release();
            }
        }

        private static async Task SendControlAsync(WebSocket socket, object payload)
        {
            var frame = AudioWireProtocol.FrameControl(0, payload);
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        private static async Task<byte[]?> ReceiveOneMessageAsync(WebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var msg = ms.ToArray();
            if (msg.Length < 4) return null;
            uint bodyLen = BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(0, 4));
            if (msg.Length < 4 + bodyLen) return null;
            return msg.AsSpan(4, (int)bodyLen).ToArray();
        }

        public void Dispose()
        {
            CancelScheduledDeviceClose();
            StopSessionAsync(forceCloseDevices: true).GetAwaiter().GetResult();
            _wsSendLock.Dispose();
        }
    }
}
