using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Microphone selection helper for voice control.
    ///
    /// <para>
    /// <see cref="System.Speech.Recognition.SpeechRecognitionEngine"/> can only
    /// take audio from the Windows <em>default</em> recording device
    /// (<c>SetInputToDefaultAudioDevice</c>) or from a raw PCM
    /// <see cref="Stream"/> (<c>SetInputToAudioStream</c>) — it has no API to
    /// target a device by name or index. To let the user pick a specific mic in
    /// Settings (so partially-sighted operators don't have to fight Windows'
    /// default-device selection), we enumerate and capture the chosen device
    /// ourselves via WaveIn/MME and feed the recogniser a <see cref="MicrophoneStream"/>.
    /// </para>
    ///
    /// <para>
    /// Capture is done at 48 kHz, 16-bit, mono PCM — the mic's native rate — and
    /// SAPI is handed a matching 48 kHz format so its own resampler produces the
    /// 16 kHz its acoustic model uses. Capturing at 16 kHz instead would force
    /// WinMM's low-quality downsample and measurably hurt confidence.
    /// </para>
    /// </summary>
    public static class MicrophoneCapture
    {
        /// <summary>
        /// Capture rate. We deliberately capture at 48 kHz (the native rate of
        /// virtually all USB/PC mics) rather than the 16 kHz SAPI's acoustic
        /// model ultimately uses, and hand SAPI a 48 kHz format. That way SAPI's
        /// own high-quality resampler produces the 16 kHz, instead of WinMM's
        /// cheap linear downsample doing it on the way in — which measurably
        /// starved recognition confidence on a quiet mic.
        /// </summary>
        public const int SampleRate = 48_000;
        public const int Bits = 16;
        public const int Channels = 1;

        public sealed record InputDevice(int Index, string Name);

        /// <summary>
        /// Enumerate the WaveIn (recording) devices Windows exposes right now.
        /// Names are the MME product names — truncated to 31 chars by the OS,
        /// which is why Device Manager shows e.g. "Microphone (Marantz Umpire
        /// Mic)" the same way. That truncation is consistent, so it's a stable
        /// key to match on later (see <see cref="FindDeviceIndex"/>).
        /// </summary>
        public static IReadOnlyList<InputDevice> ListInputDevices()
        {
            var list = new List<InputDevice>();
            int count = WaveInEvent.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var caps = WaveInEvent.GetCapabilities(i);
                    if (!string.IsNullOrWhiteSpace(caps.ProductName))
                        list.Add(new InputDevice(i, Normalize(caps.ProductName)));
                }
                catch
                {
                    // A device that won't describe itself is skipped rather
                    // than aborting the whole enumeration.
                }
            }
            return list;
        }

        /// <summary>
        /// Resolve a saved device <em>name</em> to its current WaveIn index.
        /// Names are used as the persisted key rather than indices because a
        /// device's index shifts as other devices are plugged/unplugged,
        /// whereas the name the user picked is stable. Returns -1 when the name
        /// is empty or the device is no longer present — the caller then falls
        /// back to the Windows default device.
        /// </summary>
        public static int FindDeviceIndex(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            var target = Normalize(name);
            int count = WaveInEvent.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    if (string.Equals(Normalize(WaveInEvent.GetCapabilities(i).ProductName), target, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
                catch
                {
                    // Ignore an un-describable device and keep looking.
                }
            }
            return -1;
        }

        // MME product names come from a fixed 32-char buffer, so long names
        // arrive truncated at 31 chars and can end on a space. That trailing
        // space survives enumeration but is stripped somewhere in the
        // browser → JSON → settings round-trip, breaking a later exact match.
        // Trimming trailing whitespace on both sides keeps the key stable.
        private static string Normalize(string name) => name.TrimEnd();
    }

    /// <summary>
    /// A read-only, blocking <see cref="Stream"/> of 48 kHz/16-bit/mono PCM that
    /// SAPI's <c>SetInputToAudioStream</c> can pull from. A
    /// <see cref="WaveInEvent"/> delivers captured buffers on a background
    /// thread into a bounded queue; <see cref="Read"/> (called by SAPI on its
    /// own thread) blocks until data is available and only returns 0 once the
    /// stream is disposed. Returning 0 from a still-live stream would signal
    /// end-of-input and halt recognition, so a running capture never does.
    /// </summary>
    public sealed class MicrophoneStream : Stream
    {
        private readonly WaveInEvent _waveIn;
        private readonly object _lock = new();
        private readonly Queue<byte[]> _chunks = new();
        private byte[]? _current;
        private int _currentPos;
        private long _queuedBytes;
        private readonly long _capacityBytes;
        private volatile bool _stopped;
        private long _position; // total bytes handed to SAPI so far

        // Diagnostics: peak level + byte totals, logged ~once/second so we can
        // see from the log whether the chosen mic is actually producing audio.
        private readonly ILogger? _logger;
        private long _totalCaptured;
        private long _totalRead;
        private int _peakSinceLog;
        private DateTime _nextLevelLog = DateTime.MinValue;

        // Adaptive gain (AGC). Many mics deliver speech at only ~8% of full
        // scale, which starves SAPI and drops its confidence so correctly-heard
        // commands fall under the accept threshold. We lift quiet speech toward
        // ~TargetPeak with a slow AGC: back off fast when it gets loud (avoid
        // clipping), ramp up slowly, and hold steady during near-silence so the
        // noise floor isn't amplified. Starts pre-boosted so the very first
        // utterance already benefits.
        //
        // The AGC always aims for the same TargetPeak (~30% FS); MaxGain is only
        // the ceiling for how hard it may push a genuinely starved mic to get
        // there. A healthy mic reaches target at 1–2x and never touches the cap,
        // so a high ceiling costs it nothing — it only rescues very quiet mics.
        // (An earlier build capped at 4x, tuned on a bench mic already at a
        // healthy level; a much quieter device — e.g. a "Marantz Umpire Mic"
        // peaking at raw ~12–56/32767 — was left pinned at 4x and still starved,
        // its speech under-driven ~50x. Hence the far higher ceiling here.)
        //
        // SilenceFloor must sit *below* real speech, not above it: on that same
        // quiet mic, spoken commands peaked at raw ~50 while the old floor of 150
        // classified them as noise and refused to adapt. Keep it low enough that
        // genuine speech drives the gain; the fast down-ramp still pulls back
        // instantly if a loud sound arrives, so brief noise-driven ramps on a
        // normal mic self-correct.
        private const int TargetPeak = 10_000; // ~30% FS
        private const int SilenceFloor = 40;   // below this = don't adapt (noise)
        // 120x: even at 60x the Umpire Mic reached only ~9% FS on speech (raw
        // ~50 -> 3000), leaving random marginal utterances under SAPI's own
        // rejection threshold. Recognition is push-to-talk and the buffer is
        // discarded before each press, so lifting the noise floor between words
        // is low-risk — noise only matters inside the utterance window.
        private const float MaxGain = 120f;
        private float _gain = 3f;

        public MicrophoneStream(int deviceNumber, ILogger? logger = null)
        {
            _logger = logger;
            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(MicrophoneCapture.SampleRate, MicrophoneCapture.Bits, MicrophoneCapture.Channels),
                // 50 ms buffers keep push-to-talk latency low without flooding
                // the queue with tiny allocations.
                BufferMilliseconds = 50,
            };
            // Cap the backlog at ~2 s of audio. When SAPI isn't reading (between
            // PTT presses) the queue would otherwise grow unbounded; instead we
            // drop the oldest audio, so a resumed recognition only ever sees at
            // most a couple of seconds of stale sound — and StartListening calls
            // DiscardBuffered() before each session anyway.
            _capacityBytes = MicrophoneCapture.SampleRate * (MicrophoneCapture.Bits / 8) * MicrophoneCapture.Channels * 2;

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += (_, _) =>
            {
                lock (_lock) { _stopped = true; Monitor.PulseAll(_lock); }
            };
            _waveIn.StartRecording();
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;

            // Raw (pre-gain) peak: shows whether the mic is actually picking up
            // sound (near-0 => muted/wrong device/access denied) and drives the
            // AGC below. Diagnostic peak stays on the raw signal so the log
            // reflects true input level, with the applied gain logged alongside.
            int bufPeak = 0;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short s = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                int abs = s == short.MinValue ? short.MaxValue : Math.Abs(s);
                if (abs > bufPeak) bufPeak = abs;
            }
            if (bufPeak > _peakSinceLog) _peakSinceLog = bufPeak;

            // Adapt the gain toward TargetPeak (only when there's real signal).
            if (bufPeak > SilenceFloor)
            {
                float desired = (float)TargetPeak / bufPeak;
                float rate = desired < _gain ? 0.5f : 0.05f; // fast down, slow up
                _gain += (desired - _gain) * rate;
                if (_gain < 1f) _gain = 1f;
                else if (_gain > MaxGain) _gain = MaxGain;
            }

            _totalCaptured += e.BytesRecorded;
            if (_logger != null && DateTime.UtcNow >= _nextLevelLog)
            {
                _nextLevelLog = DateTime.UtcNow.AddSeconds(1);
                _logger.LogInformation("[Voice] Mic capture: peak={Peak}/32767, gain={Gain:F1}x, captured={Cap}B read={Read}B",
                    _peakSinceLog, _gain, _totalCaptured, _totalRead);
                _peakSinceLog = 0;
            }

            // Apply gain into a fresh chunk (hard-clipped to 16-bit).
            var chunk = new byte[e.BytesRecorded];
            if (_gain > 1.01f)
            {
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    short s = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                    int v = (int)(s * _gain);
                    if (v > short.MaxValue) v = short.MaxValue;
                    else if (v < short.MinValue) v = short.MinValue;
                    chunk[i] = (byte)(v & 0xFF);
                    chunk[i + 1] = (byte)((v >> 8) & 0xFF);
                }
            }
            else
            {
                Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
            }
            lock (_lock)
            {
                _chunks.Enqueue(chunk);
                _queuedBytes += chunk.Length;
                while (_queuedBytes > _capacityBytes && _chunks.Count > 1)
                {
                    var dropped = _chunks.Dequeue();
                    _queuedBytes -= dropped.Length;
                }
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>
        /// Drop everything currently queued. Called just before a recognition
        /// session starts so ambient noise (or the tail of a previous
        /// utterance) captured while idle isn't replayed as the first match.
        /// </summary>
        public void DiscardBuffered()
        {
            lock (_lock)
            {
                _chunks.Clear();
                _queuedBytes = 0;
                _current = null;
                _currentPos = 0;
            }
        }

        private bool _firstReadLogged;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_firstReadLogged)
            {
                _firstReadLogged = true;
                _logger?.LogInformation("[Voice] Mic stream: SAPI's first Read() call (count={Count}) — reader pump is running", count);
            }
            // Fully satisfy the requested count before returning. SAPI's stream
            // wrapper treats a short read (fewer bytes than asked for) as
            // end-of-stream and stops the recogniser — that's why an earlier
            // build read exactly one 1600-byte chunk against a count=3040
            // request and then never read again. So we block, accumulating
            // captured chunks, until the caller's buffer is full (or the stream
            // is stopped, in which case we return whatever we managed — a
            // genuine short/zero read that legitimately signals the end).
            int written = 0;
            lock (_lock)
            {
                while (written < count)
                {
                    if (_current != null && _currentPos < _current.Length)
                    {
                        int available = _current.Length - _currentPos;
                        int n = Math.Min(available, count - written);
                        Buffer.BlockCopy(_current, _currentPos, buffer, offset + written, n);
                        _currentPos += n;
                        written += n;
                        if (_currentPos >= _current.Length) _current = null;
                        continue;
                    }

                    if (_chunks.Count > 0)
                    {
                        _current = _chunks.Dequeue();
                        _queuedBytes -= _current.Length;
                        _currentPos = 0;
                        continue;
                    }

                    if (_stopped) break; // end-of-stream -> return what we have
                    Monitor.Wait(_lock);
                }

                _position += written;
                _totalRead += written;
                return written;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _waveIn.StopRecording(); } catch { /* best-effort */ }
                lock (_lock) { _stopped = true; Monitor.PulseAll(_lock); }
                try { _waveIn.Dispose(); } catch { /* best-effort */ }
            }
            base.Dispose(disposing);
        }

        // ── read-only, non-seekable stream boilerplate ──────────────────────
        // System.Speech.SetInputToAudioStream reads Length and Position even
        // though it only ever reads forward, so these must return values rather
        // than throw (a throw here was why SetInputToAudioStream failed). Length
        // reports "effectively infinite" for a live capture; Position tracks how
        // much has been read so far. The stream is genuinely non-seekable, so
        // the setter is a no-op.
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => long.MaxValue;
        public override long Position
        {
            get => _position;
            set { /* non-seekable live stream: ignore */ }
        }
        public override void Flush() { }
        // System.Speech's stream wrapper queries the current position with
        // Seek(0, SeekOrigin.Current) on its reader thread rather than reading
        // the Position property. Throwing NotSupportedException here silently
        // kills the recogniser's read pump — the stream is captured fine but
        // SAPI never reads a single byte (read=0B forever). This is a
        // forward-only live capture, so there's nothing to seek to: treat any
        // seek as a position query and return where we are, never throwing.
        public override long Seek(long offset, SeekOrigin origin) => _position;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
