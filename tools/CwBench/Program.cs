using System.Globalization;
using System.Text;
using RadioWebControl.Core.Services.Cw;

namespace CwBench;

/// <summary>
/// Runs the Core CW decoder over a recording, so it can be scored against a
/// radio's own decoder on the same off-air signal.
///
/// Phase 1 measured the decoder against synthetic audio (see
/// docs/design/cw-reader-plan.md §4.1). The plan then asks for a bench
/// comparison against the IC-7300 MkII before any app wiring is built, and the
/// app wiring is exactly what does not exist yet — so this harness stands in
/// for it: record the radio's audio to a .wav, run it through here, compare
/// with what the radio put on its own screen.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cwbench: {ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }

        var pitch     = 600.0;
        var search    = 250.0;
        var track     = true;
        var telemetry = 5.0;
        var raw       = false;
        var spectrum  = false;
        string? path  = null;
        string? selftest = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pitch":     pitch     = Arg(args, ++i); break;
                case "--search":    search    = Arg(args, ++i); break;
                case "--telemetry": telemetry = Arg(args, ++i); break;
                case "--no-track":  track     = false; break;
                case "--raw":       raw       = true;  break;
                case "--spectrum":  spectrum  = true;  break;
                case "--selftest":  selftest  = i + 1 < args.Length && !args[i + 1].StartsWith('-')
                                                ? args[++i] : "selftest.wav"; break;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException($"unknown option {args[i]}");
                    path = args[i];
                    break;
            }
        }

        if (selftest != null)
        {
            WriteSelfTest(selftest);
            Console.WriteLine($"Wrote {selftest} — synthetic CW, known text, for proving this harness.");
            Console.WriteLine();
            path ??= selftest;
        }

        if (path == null) { Usage(); return 1; }
        if (!File.Exists(path)) throw new FileNotFoundException($"no such file: {path}");

        var wav     = WavFile.Read(path);
        var samples = wav.Mono;
        var rate    = wav.SampleRate;

        // The decoder wants a whole multiple of 8 kHz. Recorders default to
        // 44.1 kHz often enough that refusing outright would just waste a trip
        // to the rig, so resample rather than complain.
        if (rate % 8000 != 0)
        {
            samples = Resample(samples, rate, 48000);
            Console.WriteLine($"note: resampled {rate} Hz -> 48000 Hz (the decoder needs a multiple of 8 kHz)");
            rate = 48000;
        }

        var peak = 0f;
        var sumSq = 0.0;
        foreach (var s in samples) { var a = Math.Abs(s); if (a > peak) peak = a; sumSq += s * (double)s; }
        var rms = Math.Sqrt(sumSq / Math.Max(1, samples.Length));

        Console.WriteLine($"file      {Path.GetFullPath(path)}");
        Console.WriteLine($"format    {rate} Hz, {wav.Channels} ch, {wav.BitsPerSample}-bit, {wav.DurationSeconds:F1} s");
        Console.WriteLine($"level     peak {Linear(peak)}, rms {Linear((float)rms)}");
        if (peak >= 0.999f)  Console.WriteLine("WARNING   the recording clips — turn the capture level down and record again");
        if (peak <  0.02f)   Console.WriteLine("WARNING   the recording is very quiet — check the capture device and level");
        Console.WriteLine($"decoder   pitch {pitch:F0} Hz ±{search:F0} Hz, tracking {(track ? "on" : "off")}");
        Console.WriteLine();

        if (spectrum) Spectrum.Report(samples, rate);

        var engine = new CwDecoderEngine(new CwDecoderOptions
        {
            InputSampleRate = rate,
            PitchHz         = pitch,
            SearchWindowHz  = search,
            TrackPitch      = track,
        });

        var frame      = rate / 100;            // 10 ms, the frame size the apps produce
        var transcript = new StringBuilder();
        var line       = new StringBuilder();
        var nextTele   = telemetry;
        var lastWpm    = 0.0;
        var lastTone   = 0.0;

        for (var offset = 0; offset < samples.Length; offset += frame)
        {
            var n    = Math.Min(frame, samples.Length - offset);
            var text = engine.ProcessFrame(samples.AsSpan(offset, n));
            var t    = (offset + n) / (double)rate;

            if (text.Length > 0)
            {
                transcript.Append(text);
                if (raw) { Console.Write(text); }
                else
                {
                    if (line.Length == 0) line.Append($"[{Stamp(t)}] ");
                    line.Append(text);
                    if (line.Length > 78) { Console.WriteLine(line.ToString().TrimEnd()); line.Clear(); }
                }
            }

            if (engine.WordsPerMinute > 0) lastWpm  = engine.WordsPerMinute;
            if (engine.ToneHz         > 0) lastTone = engine.ToneHz;

            if (!raw && telemetry > 0 && t >= nextTele)
            {
                nextTele += telemetry;
                if (line.Length > 0) { Console.WriteLine(line.ToString().TrimEnd()); line.Clear(); }
                Console.WriteLine($"           . {Stamp(t)}  {lastWpm,4:F1} wpm  {lastTone,6:F1} Hz  "
                                + $"{engine.SnrDb,5:F1} dB  {(engine.SignalPresent ? "signal" : "quiet ")}"
                                + $"  {(engine.IsLocked ? "locked" : "      ")}");
            }
        }

        var tail = engine.Flush();
        if (tail.Length > 0) { transcript.Append(tail); if (raw) Console.Write(tail); else line.Append(tail); }
        if (!raw && line.Length > 0) Console.WriteLine(line.ToString().TrimEnd());
        if (raw) { Console.WriteLine(); return 0; }

        var zero = engine.ZeroInOffsetHz();

        Console.WriteLine();
        Console.WriteLine("--- summary -------------------------------------------------");
        Console.WriteLine($"characters   {transcript.Length}");
        Console.WriteLine($"speed        {lastWpm:F1} wpm (last tracked)");
        Console.WriteLine($"tone         {lastTone:F1} Hz  (configured pitch {pitch:F0} Hz)");
        Console.WriteLine($"zero-in      {(zero is null ? "not offered" : $"{zero:+#;-#;0} Hz")}");
        Console.WriteLine($"confidence   {engine.Confidence:F2}");
        Console.WriteLine();
        Console.WriteLine("--- transcript ----------------------------------------------");
        Console.WriteLine(transcript.ToString());
        return 0;
    }

    private static string Stamp(double seconds)
        => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");

    private static string Linear(float v)
        => v <= 0 ? "-inf dBFS" : $"{20 * Math.Log10(v):F1} dBFS";

    private static double Arg(string[] args, int i)
        => i < args.Length
            ? double.Parse(args[i], CultureInfo.InvariantCulture)
            : throw new ArgumentException("missing value");

    /// <summary>Linear interpolation. Good enough for a single audio tone.</summary>
    private static float[] Resample(float[] input, int from, int to)
    {
        var ratio = to / (double)from;
        var outLen = (int)(input.Length * ratio);
        var output = new float[outLen];
        for (var i = 0; i < outLen; i++)
        {
            var src = i / ratio;
            var i0  = (int)src;
            var i1  = Math.Min(i0 + 1, input.Length - 1);
            var f   = src - i0;
            output[i] = (float)(input[i0] * (1 - f) + input[i1] * f);
        }
        return output;
    }

    /// <summary>
    /// Writes a short recording of known text at two speeds, so the harness can
    /// be proved before a radio is involved. Deliberately mirrors the
    /// mixed-speed case from §4.1 — the one the FTdx101 cannot follow.
    /// </summary>
    private static void WriteSelfTest(string path)
    {
        const int rate = 48000;
        var parts = new List<float[]>
        {
            Silence(rate, 1.0),
            Tone(rate, "CQ CQ DE MM5AGM MM5AGM K", 27, 600),
            Silence(rate, 1.5),
            Tone(rate, "MM5AGM DE OZ1JTE GM OM UR RST 599 599", 16, 600),
            Silence(rate, 1.0),
        };

        var total = parts.Sum(p => p.Length);
        var all   = new float[total];
        var at    = 0;
        foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }

        // A little noise, so the presence gate and the threshold tracker are
        // actually exercised rather than handed a clean tone.
        var rng = new Random(20260825);
        for (var i = 0; i < all.Length; i++)
            all[i] += (float)((rng.NextDouble() * 2 - 1) * 0.01);

        WriteWav(path, all, rate);
    }

    private static float[] Silence(int rate, double seconds) => new float[(int)(rate * seconds)];

    private static float[] Tone(int rate, string text, double wpm, double hz)
    {
        var dit  = 1.2 / wpm;                     // PARIS timing
        var buf  = new List<float>();
        var rise = (int)(rate * 0.005);           // 5 ms raised-cosine edges

        void Key(double seconds, bool on)
        {
            var n = (int)(rate * seconds);
            // Snapshot the write position: buf.Count advances as samples are
            // added, so using it inside the loop advances the phase twice per
            // sample and puts the tone an octave up.
            var start = buf.Count;
            for (var i = 0; i < n; i++)
            {
                var env = !on ? 0.0
                    : i < rise            ? 0.5 * (1 - Math.Cos(Math.PI * i / rise))
                    : i > n - rise        ? 0.5 * (1 - Math.Cos(Math.PI * (n - i) / rise))
                    : 1.0;
                var phase = 2 * Math.PI * hz * (start + i) / rate;
                buf.Add((float)(0.4 * env * Math.Sin(phase)));
            }
        }

        foreach (var ch in text.ToUpperInvariant())
        {
            if (ch == ' ') { Key(dit * 4, false); continue; }   // 7 total with the 3 after the last element
            var code = MorseTable.Encode(ch);
            if (string.IsNullOrEmpty(code)) continue;
            foreach (var el in code)
            {
                Key(el == '-' ? dit * 3 : dit, true);
                Key(dit, false);
            }
            Key(dit * 2, false);                                 // 3 dits between characters
        }

        return buf.ToArray();
    }

    private static void WriteWav(string path, float[] mono, int rate)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        var bytes = mono.Length * 2;

        bw.Write("RIFF"u8); bw.Write(36 + bytes); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((ushort)1); bw.Write((ushort)1);
        bw.Write(rate); bw.Write(rate * 2); bw.Write((ushort)2); bw.Write((ushort)16);
        bw.Write("data"u8); bw.Write(bytes);
        foreach (var s in mono)
            bw.Write((short)(Math.Clamp(s, -1f, 1f) * 32767));
    }

    private static void Usage()
    {
        Console.WriteLine("""
            cwbench — run the Core CW decoder over a recording

              CwBench <file.wav> [options]
              CwBench --selftest [out.wav]        write and decode synthetic CW

            options
              --pitch <Hz>      CW pitch, centre of the tone search (default 600)
              --search <Hz>     half-width of the tone search      (default 250)
              --no-track        pin the detector to --pitch instead of hunting
              --telemetry <s>   seconds between telemetry lines    (default 5, 0 = off)
              --raw             transcript only, no timestamps or telemetry
              --spectrum        say what is in the recording before decoding it,
                                so a transcript of noise is not mistaken for a
                                transcript of a signal

            Record mono, 48 kHz, from the radio's USB CODEC — see
            docs/design/cw-bench-procedure.md.
            """);
    }
}
