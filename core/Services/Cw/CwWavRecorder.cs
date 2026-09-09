using System.Text;

namespace RadioWebControl.Core.Services.Cw
{
    /// <summary>
    /// Writes a stream of mono float frames to a 16-bit PCM WAV as they arrive.
    ///
    /// This exists so the bench corpus can be recorded from the same frames the
    /// decoder is actually being fed, rather than from a second capture of the
    /// radio. A separate recorder would have its own device, its own clock and
    /// its own resampling, and every difference between it and the live path is
    /// a difference the bench can never account for.
    ///
    /// 16-bit PCM rather than float, because that is what the rest of the
    /// corpus is and what CwBench's reader was written against. The decoder
    /// works from a normalised envelope, so the quantisation is far below
    /// anything it can see.
    ///
    /// The header's two length fields are written as zero up front and patched
    /// on Dispose. A reader that opens the file mid-capture therefore sees a
    /// zero-length data chunk, which is the documented signal to take whatever
    /// bytes are actually present - so a capture can be scored while it is
    /// still running, and a capture lost to a crash is still a usable file
    /// rather than a corrupt one.
    /// </summary>
    public sealed class CwWavRecorder : IDisposable
    {
        // Roughly a second of audio at 48 kHz. Flushing on that cadence keeps
        // the file readable as it grows without turning every 10 ms frame into
        // a disk write.
        private const int FlushBytes = 96_000;

        private readonly object _gate = new();
        private readonly FileStream _fs;
        private readonly BinaryWriter _bw;

        private long _samples;
        private int _sinceFlush;
        private bool _closed;

        public CwWavRecorder(string path, int sampleRate, int channels = 1)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

            Path = path;
            SampleRate = sampleRate;
            Channels = channels;

            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // FileShare.Read so CwBench can be pointed at the file while the
            // capture is still running.
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _bw = new BinaryWriter(_fs, Encoding.ASCII, leaveOpen: true);

            int blockAlign = 2 * channels;
            _bw.Write("RIFF"u8); _bw.Write(0); _bw.Write("WAVE"u8);
            _bw.Write("fmt "u8); _bw.Write(16);
            _bw.Write((ushort)1);              // PCM
            _bw.Write((ushort)channels);
            _bw.Write(sampleRate);
            _bw.Write(sampleRate * blockAlign); // byte rate
            _bw.Write((ushort)blockAlign);
            _bw.Write((ushort)16);
            _bw.Write("data"u8); _bw.Write(0);  // patched on Dispose
            _bw.Flush();
        }

        public string Path { get; }
        public int SampleRate { get; }
        public int Channels { get; }

        /// <summary>Samples written per channel.</summary>
        public long SampleCount { get { lock (_gate) return _samples; } }

        public double DurationSeconds => SampleCount / (double)(SampleRate * Channels);

        /// <summary>
        /// Append a frame. Called from the decoder's pump thread, never from
        /// the audio callback, so a synchronous write is safe here - and it is
        /// far cheaper than the tone analysis already running on that thread.
        /// </summary>
        public void Write(ReadOnlySpan<float> samples)
        {
            lock (_gate)
            {
                if (_closed) return;

                foreach (var s in samples)
                {
                    var clamped = s > 1f ? 1f : (s < -1f ? -1f : s);
                    _bw.Write((short)(clamped * 32767f));
                }

                _samples += samples.Length;
                _sinceFlush += samples.Length * 2;

                if (_sinceFlush >= FlushBytes)
                {
                    _bw.Flush();
                    _fs.Flush(flushToDisk: false);
                    _sinceFlush = 0;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;

                try
                {
                    _bw.Flush();

                    // Patch the two lengths now the total is known.
                    int dataBytes = checked((int)Math.Min(_samples * 2, int.MaxValue - 44));
                    _fs.Position = 4;  _bw.Write(36 + dataBytes);
                    _fs.Position = 40; _bw.Write(dataBytes);
                    _bw.Flush();
                }
                catch (IOException)
                {
                    // A file that could not be finalised is still readable -
                    // the zero-length data chunk tells the reader to take what
                    // is there. Losing the capture entirely would be worse.
                }
                finally
                {
                    _bw.Dispose();
                    _fs.Dispose();
                }
            }
        }
    }
}
