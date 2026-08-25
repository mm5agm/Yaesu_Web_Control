namespace CwBench;

/// <summary>
/// Minimal RIFF/WAVE reader, enough for whatever a sound recorder produces off
/// a radio's USB CODEC. Hand-rolled rather than pulled from a package because
/// this harness sits next to Core, and Core's whole point is having no
/// dependencies; a bench tool that needed NuGet to read a .wav would be an odd
/// exception to that.
///
/// Handles PCM 8/16/24/32-bit and IEEE float 32/64-bit, any channel count
/// (mixed down to mono by averaging), and skips unknown chunks rather than
/// assuming 'fmt ' and 'data' are the only two — WAVs written by ffmpeg carry
/// a LIST/INFO chunk, and ones written by Windows carry 'fact'.
/// </summary>
public sealed class WavFile
{
    public required int     SampleRate { get; init; }
    public required int     Channels   { get; init; }
    public required int     BitsPerSample { get; init; }
    public required float[] Mono       { get; init; }

    public double DurationSeconds => Mono.Length / (double)SampleRate;

    public static WavFile Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
        br.ReadUInt32(); // riff size, unused - we trust the chunk headers
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

        int    channels = 0, sampleRate = 0, bits = 0;
        ushort format   = 0;
        byte[]? data    = null;

        while (fs.Position + 8 <= fs.Length)
        {
            var id   = new string(br.ReadChars(4));
            var size = br.ReadUInt32();
            var next = fs.Position + size + (size % 2); // chunks are word-aligned

            if (id == "fmt ")
            {
                format     = br.ReadUInt16();
                channels   = br.ReadUInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32();  // byte rate
                br.ReadUInt16(); // block align
                bits       = br.ReadUInt16();
                // WAVE_FORMAT_EXTENSIBLE hides the real format in a GUID whose
                // first two bytes are the format tag it stands in for.
                if (format == 0xFFFE && size >= 40)
                {
                    br.ReadUInt16();          // cbSize
                    br.ReadUInt16();          // valid bits
                    br.ReadUInt32();          // channel mask
                    format = br.ReadUInt16(); // first field of the SubFormat GUID
                }
            }
            else if (id == "data")
            {
                data = br.ReadBytes((int)size);
            }

            if (fs.Position != next)
            {
                if (next > fs.Length) break;
                fs.Position = next;
            }
        }

        if (data == null)   throw new InvalidDataException("No 'data' chunk.");
        if (channels == 0)  throw new InvalidDataException("No 'fmt ' chunk.");

        var mono = Decode(data, format, bits, channels);
        return new WavFile
        {
            SampleRate    = sampleRate,
            Channels      = channels,
            BitsPerSample = bits,
            Mono          = mono,
        };
    }

    private static float[] Decode(byte[] data, ushort format, int bits, int channels)
    {
        var bytesPerSample = bits / 8;
        if (bytesPerSample == 0) throw new InvalidDataException($"Unsupported bit depth {bits}.");

        var frames = data.Length / (bytesPerSample * channels);
        var mono   = new float[frames];

        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = (f * channels + c) * bytesPerSample;
                sum += format switch
                {
                    // WAVE_FORMAT_PCM: 8-bit is unsigned, everything wider is signed.
                    1 when bits == 8  => (data[o] - 128) / 128.0,
                    1 when bits == 16 => BitConverter.ToInt16(data, o) / 32768.0,
                    1 when bits == 24 => ((data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16))) / 8388608.0,
                    1 when bits == 32 => BitConverter.ToInt32(data, o) / 2147483648.0,
                    // WAVE_FORMAT_IEEE_FLOAT
                    3 when bits == 32 => BitConverter.ToSingle(data, o),
                    3 when bits == 64 => BitConverter.ToDouble(data, o),
                    _ => throw new InvalidDataException($"Unsupported WAV format {format} at {bits} bits."),
                };
            }
            mono[f] = (float)(sum / channels);
        }

        return mono;
    }
}
