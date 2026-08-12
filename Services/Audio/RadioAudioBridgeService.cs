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
        private bool _devicesOpen;
        private float _rxLevel;
        private float _txLevel;

        public RadioAudioBridgeService(
            ILogger<RadioAudioBridgeService> logger,
            ISettingsService settings,
            AudioSessionManager sessions)
        {
            _logger = logger;
            _settings = settings;
            _sessions = sessions;
        }

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
            await StopSessionAsync();
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
                await StopSessionAsync();
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
            _rxGain = Math.Clamp(settings.AudioRxGain, 0.05f, 4f);
            _txGain = Math.Clamp(settings.AudioTxGain, 0.05f, 4f);
            _codecName = AudioConstants.CodecOpus;
            _codec = new OpusCodec();

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

            string? openError = OpenDevices(settings);
            if (openError != null)
            {
                await SendControlAsync(socket, new { cmd = "error", message = openError });
                return;
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
            _sendTask = Task.Run(() => SendPumpAsync(socket, _pumpCts.Token), _pumpCts.Token);
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

        private string? OpenDevices(ApplicationSettings settings)
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
                        if (input == IntPtr.Zero) return StreamCallbackResult.Continue;
                        int interleaved = (int)frameCount * inCh;
                        var raw = new float[interleaved];
                        Marshal.Copy(input, raw, 0, interleaved);
                        var mono = new float[frameCount];
                        if (inCh == 1)
                        {
                            Array.Copy(raw, mono, (int)frameCount);
                        }
                        else
                        {
                            for (int i = 0; i < (int)frameCount; i++)
                                mono[i] = 0.5f * (raw[i * inCh] + raw[i * inCh + 1]);
                        }
                        OnCaptureSamples(Resample(mono, rxRate, AudioConstants.SampleRate));
                        return StreamCallbackResult.Continue;
                    };

                    PortAudioSharp.Stream.Callback playbackCb = (IntPtr input, IntPtr output, uint frameCount,
                        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData) =>
                    {
                        if (output == IntPtr.Zero) return StreamCallbackResult.Continue;
                        int bridgeCount = Math.Max(1,
                            (int)Math.Round(frameCount * (double)AudioConstants.SampleRate / txRate));
                        var bridge = new float[bridgeCount];
                        PullPlayback(bridge);
                        var mono = Resample(bridge, AudioConstants.SampleRate, txRate);
                        // Callback asked for frameCount samples; pad/truncate if rounding drifted.
                        if (mono.Length != frameCount)
                        {
                            var adjusted = new float[frameCount];
                            Array.Copy(mono, adjusted, Math.Min(mono.Length, (int)frameCount));
                            mono = adjusted;
                        }
                        if (outCh == 1)
                        {
                            Marshal.Copy(mono, 0, output, (int)frameCount);
                        }
                        else
                        {
                            var interleaved = new float[frameCount * outCh];
                            for (int i = 0; i < (int)frameCount; i++)
                            {
                                float s = mono[i];
                                interleaved[i * outCh] = s;
                                interleaved[i * outCh + 1] = s;
                            }
                            Marshal.Copy(interleaved, 0, output, interleaved.Length);
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

                    _captureStream.Start();
                    _playbackStream.Start();
                    _devicesOpen = true;
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

        private static float[] Resample(ReadOnlySpan<float> input, int inRate, int outRate)
        {
            if (inRate == outRate || input.Length == 0)
                return input.ToArray();

            int outLen = Math.Max(1, (int)Math.Round(input.Length * (double)outRate / inRate));
            var output = new float[outLen];
            double ratio = (double)inRate / outRate;
            int last = input.Length - 1;
            for (int i = 0; i < outLen; i++)
            {
                double src = i * ratio;
                int i0 = (int)src;
                if (i0 >= last)
                {
                    output[i] = input[last];
                    continue;
                }
                double frac = src - i0;
                output[i] = (float)(input[i0] + (input[i0 + 1] - input[i0]) * frac);
            }
            return output;
        }

        private void OnCaptureSamples(float[] samples)
        {
            Span<float> frame = stackalloc float[AudioConstants.FrameSamples];
            int offset = 0;
            while (offset < samples.Length)
            {
                int space = _captureAccum.Length - _captureAccumLen;
                int take = Math.Min(space, samples.Length - offset);
                Array.Copy(samples, offset, _captureAccum, _captureAccumLen, take);
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
                    await socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
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
                    await Task.Delay(100, ct);
                    if (socket.State != WebSocketState.Open) break;
                    var levels = AudioWireProtocol.FrameControl(
                        (uint)Interlocked.Increment(ref _rxSeq),
                        new { cmd = "levels", rx = _rxLevel, tx = _txLevel });
                    await socket.SendAsync(levels, WebSocketMessageType.Binary, true, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Audio levels pump failed");
                    break;
                }
            }
        }

        private async Task StopSessionAsync()
        {
            try { _outChannel?.Writer.TryComplete(); } catch { /* ignore */ }
            try { _pumpCts?.Cancel(); } catch { /* ignore */ }
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
            CloseDevices();
            _codec?.Dispose();
            _codec = null;
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
        }

        private void CloseDevices()
        {
            lock (_deviceLock)
            {
                try { _captureStream?.Stop(); } catch { /* ignore */ }
                try { _captureStream?.Dispose(); } catch { /* ignore */ }
                _captureStream = null;
                try { _playbackStream?.Stop(); } catch { /* ignore */ }
                try { _playbackStream?.Dispose(); } catch { /* ignore */ }
                _playbackStream = null;
                _devicesOpen = false;
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
            StopSessionAsync().GetAwaiter().GetResult();
        }
    }
}
