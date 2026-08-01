using System.Speech.Synthesis;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Speaks short confirmation phrases ("Move to fourteen point zero seven
    /// four megahertz, successful") via Windows' built-in TTS engine after
    /// every voice command completes. Same SAPI 5 stack the recogniser uses.
    ///
    /// <para>Output routing: by default the audio goes to whichever device
    /// Windows considers the default playback target. The operator can instead
    /// pick a specific speaker/headset in Settings → Voice Control (so
    /// confirmations reach them even when the Windows default is claimed by
    /// WSJT-X, rig audio, etc.). Because System.Speech can't target an output
    /// device by name, the chosen-device path renders the phrase to a WAV in
    /// memory and plays it to that WaveOut device via NAudio — mirroring how the
    /// input side captures a chosen mic (see <see cref="MicrophoneCapture"/>).</para>
    ///
    /// Construction is best-effort -- if SAPI's synthesiser can't be created
    /// (corrupt install, no voices, etc.), the service silently no-ops via
    /// <see cref="Speak"/> rather than throwing.
    ///
    /// Singleton -- holds a long-lived SpeechSynthesizer that the system
    /// caches voice/audio resources against.
    /// </summary>
    public sealed class VoiceTtsService : IDisposable
    {
        private readonly ILogger<VoiceTtsService> _logger;
        private SpeechSynthesizer? _synth;

        // Synth isn't thread-safe and its output binding is shared state, so all
        // use of it is serialised through this lock.
        private readonly object _synthLock = new();

        // Chosen playback device: -1 = Windows default (WAVE_MAPPER), else a
        // WaveOut device index resolved from the saved name. `volatile` so the
        // Speak path sees an ApplyOutputDevice change without taking the lock.
        private volatile int _deviceNumber = -1;
        private string? _deviceName;

        // Live playback for the chosen-device path; Stop()/Dispose()d before
        // each new phrase so rapid successive commands don't overlap.
        private readonly object _playLock = new();
        private WaveOutEvent? _waveOut;
        private WaveFileReader? _reader;

        public VoiceTtsService(ILogger<VoiceTtsService> logger)
        {
            _logger = logger;
            try
            {
                _synth = new SpeechSynthesizer();
                _synth.SetOutputToDefaultAudioDevice();
                _logger.LogInformation(
                    "[VoiceTts] Synthesiser ready (voice={Voice})",
                    _synth.Voice?.Name ?? "(default)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceTts] Failed to initialise speech synthesiser; confirmation announcements disabled");
                _synth = null;
            }
        }

        /// <summary>
        /// Choose which playback device confirmations use. Pass null/empty for
        /// the Windows default. Resolved to a live WaveOut index now; if the
        /// named device isn't present we fall back to the default and log it.
        /// Called at startup (from VoiceControlService) and whenever the Settings
        /// picker changes — no restart needed.
        /// </summary>
        public void ApplyOutputDevice(string? name)
        {
            _deviceName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            int index = AudioOutput.FindDeviceIndex(_deviceName);
            _deviceNumber = index;
            if (_deviceName == null)
                _logger.LogInformation("[VoiceTts] Output device: Windows default");
            else if (index < 0)
                _logger.LogWarning("[VoiceTts] Output device '{Name}' not found; using Windows default", _deviceName);
            else
                _logger.LogInformation("[VoiceTts] Output device: '{Name}' (WaveOut #{Index})", _deviceName, index);
        }

        /// <summary>
        /// Speak the given text asynchronously. Cancels any in-flight speech
        /// (rapid successive PTT presses don't queue up several seconds of
        /// stale confirmations). No-op if the synthesiser failed to init
        /// or the text is empty.
        /// </summary>
        public void Speak(string text)
        {
            if (_synth == null) return;
            if (string.IsNullOrWhiteSpace(text)) return;

            int device = _deviceNumber;
            if (device < 0)
            {
                SpeakToDefault(text);
            }
            else
            {
                // Render + play on a background thread so intent dispatch isn't
                // blocked by the (brief) synthesis of the phrase.
                Task.Run(() => SpeakToDevice(text, device));
            }
        }

        // Windows-default path: unchanged from the original, proven behaviour.
        private void SpeakToDefault(string text)
        {
            try
            {
                lock (_synthLock)
                {
                    _synth!.SpeakAsyncCancelAll();
                    _synth.SetOutputToDefaultAudioDevice();
                    _synth.SpeakAsync(text);
                }
                _logger.LogInformation("[VoiceTts] Speak (default device): '{Text}'", text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceTts] Speak failed for: '{Text}'", text);
            }
        }

        // Chosen-device path: render the phrase to an in-memory WAV, then play
        // it to the selected WaveOut device via NAudio.
        private void SpeakToDevice(string text, int device)
        {
            byte[] wav;
            try
            {
                using var ms = new MemoryStream();
                lock (_synthLock)
                {
                    _synth!.SetOutputToWaveStream(ms);
                    _synth.Speak(text);            // blocking render (short phrase)
                    _synth.SetOutputToNull();      // release the stream binding
                }
                wav = ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceTts] Render failed for: '{Text}'", text);
                return;
            }

            try
            {
                lock (_playLock)
                {
                    StopPlaybackLocked();
                    _reader = new WaveFileReader(new MemoryStream(wav));
                    _waveOut = new WaveOutEvent { DeviceNumber = device };
                    _waveOut.PlaybackStopped += (_, _) =>
                    {
                        lock (_playLock)
                        {
                            try { _reader?.Dispose(); } catch { /* best-effort */ }
                            _reader = null;
                        }
                    };
                    _waveOut.Init(_reader);
                    _waveOut.Play();
                }
                _logger.LogInformation("[VoiceTts] Speak (WaveOut #{Device}): '{Text}'", device, text);
            }
            catch (Exception ex)
            {
                // If the chosen device can't be opened (unplugged mid-session,
                // exclusive-mode conflict, etc.), fall back to the default so the
                // operator still hears the confirmation.
                _logger.LogWarning(ex, "[VoiceTts] Playback on WaveOut #{Device} failed; falling back to default device", device);
                SpeakToDefault(text);
            }
        }

        private void StopPlaybackLocked()
        {
            try { _waveOut?.Stop(); } catch { /* best-effort */ }
            try { _waveOut?.Dispose(); } catch { /* best-effort */ }
            _waveOut = null;
            try { _reader?.Dispose(); } catch { /* best-effort */ }
            _reader = null;
        }

        public void Dispose()
        {
            lock (_playLock) { StopPlaybackLocked(); }
            try { _synth?.Dispose(); } catch { /* ignore */ }
            _synth = null;
        }
    }
}
