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
        var filterHz  = 0;      // --filter: derive the search window from the IF filter
        var track     = true;
        var telemetry = 5.0;
        var raw       = false;
        var spectrum  = false;
        var wpm       = 0.0;                   // 0 = leave the decoder's own default
        var pinWpm    = false;
        var timeline  = false;
        var marks     = false;
        var markList  = 0;
        var traceFrom = double.MaxValue;
        var traceTo   = double.MinValue;
        var minElement = -1.0;
        var charGapDits = -1.0;  // --char-gap: character-gap boundary, in dits
        var debounce   = -1.0;
        var resync    = true;
        var warmup     = -1.0;                 // -1 = leave the decoder's own default
        var trainNoise = -1.0;
        var timelineHz = 0.0;
        string? path  = null;
        string? selftest = null;
        string? expectPath = null;             // --expect: score against known text
        var addNoiseDb = double.NaN;           // --noise: SNR to degrade a clean file to
        var fadeDepthDb = double.NaN;          // --fade: QSB depth in dB
        var fadeHz      = 0.05;                // --fade-hz: QSB rate, 20 s period

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--expect":    expectPath = args[++i]; break;
                case "--noise":     addNoiseDb = Arg(args, ++i); break;
                case "--fade":      fadeDepthDb = Arg(args, ++i); break;
                case "--fade-hz":   fadeHz      = Arg(args, ++i); break;
                case "--pitch":     pitch     = Arg(args, ++i); break;
                case "--search":    search    = Arg(args, ++i); break;
                case "--filter":    filterHz  = (int)Arg(args, ++i); break;
                case "--telemetry": telemetry = Arg(args, ++i); break;
                case "--no-track":  track     = false; break;
                case "--wpm":       wpm       = Arg(args, ++i); break;
                case "--pin-wpm":   wpm       = Arg(args, ++i); pinWpm = true; break;
                case "--marks":     marks     = true;
                                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-')
                                        && int.TryParse(args[i + 1], out var ml)) { markList = ml; i++; }
                                    break;
                case "--trace":     traceFrom = Arg(args, ++i);
                                    traceTo   = Arg(args, ++i); break;
                case "--min-element": minElement = Arg(args, ++i); break;
                case "--char-gap":    charGapDits = Arg(args, ++i); break;
                case "--debounce":    debounce   = Arg(args, ++i); break;
                case "--no-resync": resync    = false; break;
                case "--train-noise": trainNoise = Arg(args, ++i); break;
                case "--warmup":    warmup   = Arg(args, ++i); break;
                case "--raw":       raw       = true;  break;
                case "--spectrum":  spectrum  = true;  break;
                case "--timeline":  timeline  = true;
                                    if (i + 1 < args.Length && double.TryParse(args[i + 1],
                                        NumberStyles.Float, CultureInfo.InvariantCulture, out var thz))
                                    { timelineHz = thz; i++; }
                                    break;
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

        // Fade first, then noise: the path fades the signal, the receiver adds
        // the band noise afterwards. See ApplyFade.
        if (!double.IsNaN(fadeDepthDb))
        {
            ApplyFade(samples, rate, fadeDepthDb, fadeHz);
            Console.WriteLine($"fade      {fadeDepthDb:F0} dB QSB at {fadeHz:F3} Hz " +
                              $"({1.0 / Math.Max(fadeHz, 1e-9):F0} s period)");
        }

        if (!double.IsNaN(addNoiseDb))
        {
            double sigRms = AddNoise(samples, addNoiseDb, rate);
            Console.WriteLine($"noise     added to {addNoiseDb:F0} dB SNR in 500 Hz " +
                              $"(mark level {20.0 * Math.Log10(Math.Max(sigRms, 1e-9)):F1} dBFS)");
        }

        // The decoder wants a whole multiple of 8 kHz. Recorders default to
        // 44.1 kHz often enough that refusing outright would just waste a trip
        // to the rig, so resample rather than complain.
        if (rate % 8000 != 0)
        {
            samples = Resample(samples, rate, 48000);
            Console.WriteLine($"note: resampled {rate} Hz -> 48000 Hz (the decoder needs a multiple of 8 kHz)");
            rate = 48000;
        }

        // A signal outside the IF filter is one the operator cannot hear;
        // the passband is the only place worth hunting. See
        // CwDecoderOptions.SearchWindowForFilterWidth.
        if (filterHz > 0) search = CwDecoderOptions.SearchWindowForFilterWidth(filterHz);

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
        if (filterHz > 0)
            Console.WriteLine($"filter    {filterHz} Hz IF filter, so the tone is hunted across the passband and no further");
        if (wpm > 0)
            Console.WriteLine($"speed     {(pinWpm ? "pinned at" : "starting from")} {wpm:F0} wpm");
        Console.WriteLine($"train     speed trains on marks at or over "
                        + $"{(trainNoise >= 0.0 ? trainNoise : new CwElementDecoderOptions().MinTrainNoiseMultiple):F1}x the noise floor");
        Console.WriteLine();

        if (marks) MarksReport(samples, rate, pitch, search, track, markList,
                               warmup >= 0.0 ? warmup : new CwDecoderOptions().WarmupSeconds, traceFrom, traceTo,
                               debounce >= 0.0 ? debounce : new CwDecoderOptions().KeyDebounceMs);
        if (spectrum) Spectrum.Report(samples, rate);
        if (timeline) Spectrum.Timeline(samples, rate, timelineHz);

        var engine = new CwDecoderEngine(new CwDecoderOptions
        {
            InputSampleRate = rate,
            PitchHz         = pitch,
            SearchWindowHz  = search,
            TrackPitch      = track,
            WarmupSeconds   = warmup >= 0.0 ? warmup : new CwDecoderOptions().WarmupSeconds,
            KeyDebounceMs   = debounce >= 0.0 ? debounce : new CwDecoderOptions().KeyDebounceMs,
            // The speed tracker is the one located defect (plan §4.4), and until
            // now the bench could only watch it move. --wpm seeds it; --pin-wpm
            // clamps Min and Max onto the same value so it cannot move at all,
            // which is what separates "the tracker is wrong" from "the elements
            // are wrong" on a recording where both look the same.
            Element         = new CwElementDecoderOptions
            {
                InitialWpm          = wpm > 0 ? wpm : new CwElementDecoderOptions().InitialWpm,
                MinWpm              = wpm > 0 && pinWpm ? wpm : new CwElementDecoderOptions().MinWpm,
                MaxWpm              = wpm > 0 && pinWpm ? wpm : new CwElementDecoderOptions().MaxWpm,
                EnableResync        = resync,
                MinElementMs        = minElement >= 0.0
                                    ? minElement : new CwElementDecoderOptions().MinElementMs,
                CharacterGapDits    = charGapDits > 0.0
                                    ? charGapDits : new CwElementDecoderOptions().CharacterGapDits,
                MinTrainNoiseMultiple = trainNoise >= 0.0 ? trainNoise : new CwElementDecoderOptions().MinTrainNoiseMultiple,
            },
        });

        var frame      = rate / 100;            // 10 ms, the frame size the apps produce
        var transcript = new StringBuilder();
        var line       = new StringBuilder();
        var nextTele   = telemetry;
        var lastWpm    = 0.0;
        var lastTone   = 0.0;

        var readTally = new int[4];
        for (var offset = 0; offset < samples.Length; offset += frame)
        {
            var n    = Math.Min(frame, samples.Length - offset);
            var text = engine.ProcessFrame(samples.AsSpan(offset, n));
            readTally[(int)engine.Readability]++;
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
                                + $"{engine.SnrDb,5:F1} dB  {engine.Confidence,4:F2} cf  "
                                + $"{(engine.SignalPresent ? "signal" : "quiet ")}"
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
        var (runs, longestRun) = CountElementRuns(transcript.ToString());

        Console.WriteLine($"characters   {transcript.Length}");
        Console.WriteLine($"runs >=4     {runs}{(runs > 0 ? $" (longest {longestRun})" : "")}");
        Console.WriteLine($"speed        {lastWpm:F1} wpm (last tracked)");
        Console.WriteLine($"tone         {lastTone:F1} Hz  (configured pitch {pitch:F0} Hz)");
        Console.WriteLine($"zero-in      {(zero is null ? "not offered" : $"{zero:+#;-#;0} Hz")}");
        Console.WriteLine($"confidence   {engine.Confidence:F2}");
        {
            double tot = Math.Max(1, readTally.Sum());
            Console.WriteLine($"readability  Readable {readTally[(int)CwReadability.Readable] / tot * 100.0:F0}%  "
                            + $"Chatter {readTally[(int)CwReadability.Chatter] / tot * 100.0:F0}%  "
                            + $"Jumbled {readTally[(int)CwReadability.Jumbled] / tot * 100.0:F0}%  "
                            + $"Unknown {readTally[(int)CwReadability.Unknown] / tot * 100.0:F0}%");
        }
        Console.WriteLine();
        if (expectPath != null)
        {
            if (!File.Exists(expectPath)) throw new FileNotFoundException($"no such file: {expectPath}");
            var (acc, sent, got) = Score(File.ReadAllText(expectPath), transcript.ToString());
            Console.WriteLine();
            Console.WriteLine("--- against the known text ----------------------------------");
            Console.WriteLine($"sent         {sent} characters");
            Console.WriteLine($"copied       {got} characters");
            Console.WriteLine($"accuracy     {acc * 100.0:F1}%");
        }

        Console.WriteLine();
        Console.WriteLine("--- transcript ----------------------------------------------");
        Console.WriteLine(transcript.ToString());
        return 0;
    }

    /// <summary>
    /// Mix white noise in at a stated SNR, in the 500 Hz reference bandwidth a
    /// CW operator would quote.
    ///
    /// Two things here are easy to get wrong and were, first time round.
    ///
    /// The signal reference is the marks, not the file. A Farnsworth practice
    /// recording is mostly silence - the 5 WPM file keys down about a tenth of
    /// the time - so whole-file RMS understates the tone by around 10 dB and
    /// every quoted SNR is that much too pessimistic, by a margin that changes
    /// with the sending speed. That makes the ten ARRL files incomparable to
    /// each other, which is the one thing a sweep across them needs. The level
    /// taken here is the 90th percentile of a 10 ms sliding RMS: high enough to
    /// sit inside the marks, low enough not to chase a single sample.
    ///
    /// The bandwidth is stated. White noise has no SNR until you say over what
    /// bandwidth, and the honest number for a receiver is its filter - the
    /// detector's own bin is only about 8 Hz wide, so it enjoys roughly 27 dB
    /// of processing gain over a 4 kHz Nyquist and "0 dB" broadband is still a
    /// perfectly solid signal. That is why the first sweep here was inert from
    /// clean all the way to 0 dB. Quoting in 500 Hz matches both the receiver
    /// the recordings came off and CwSignalGenerator.AddNoise in the core
    /// tests, so a number here means the same as a number there.
    /// </summary>
    private static double AddNoise(float[] samples, double snrDb, int rate)
    {
        double markRms = MarkLevel(samples, rate);

        const double RefBw = 500.0;
        double fullBw   = rate / 2.0;
        double sigma    = markRms / Math.Pow(10.0, snrDb / 20.0) * Math.Sqrt(fullBw / RefBw);

        // Fixed seed: a sweep that moves because the noise moved is not a sweep.
        var rnd = new Random(20260827);
        for (int i = 0; i < samples.Length; i++)
        {
            // Box-Muller, so the noise is Gaussian rather than uniform - a
            // uniform "hiss" has the wrong peak statistics and the detector's
            // presence gate is built on exactly those.
            double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble();
            double g  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            samples[i] = (float)Math.Clamp(samples[i] + g * sigma, -1.0, 1.0);
        }
        return markRms;
    }

    /// <summary>
    /// The level of the tone while the key is down, as the 90th percentile of a
    /// 10 ms sliding RMS. Ten milliseconds is under a dit at any speed these
    /// files reach, so a window sits wholly inside a mark rather than straddling
    /// its edges.
    /// </summary>
    private static double MarkLevel(float[] samples, int rate)
    {
        int win = Math.Max(1, rate / 100);
        if (samples.Length < win) return 0.0;

        var levels = new List<double>(samples.Length / win + 1);
        for (int start = 0; start + win <= samples.Length; start += win)
        {
            double sum = 0;
            for (int i = start; i < start + win; i++) sum += (double)samples[i] * samples[i];
            levels.Add(Math.Sqrt(sum / win));
        }
        if (levels.Count == 0) return 0.0;

        levels.Sort();
        return levels[(int)(0.90 * (levels.Count - 1))];
    }

    /// <summary>
    /// Sinusoidal QSB, as a depth in dB and a fade rate in Hz.
    ///
    /// Applied before the noise, because that is the order the ionosphere and
    /// the receiver work in: the path fades the signal, and the band noise the
    /// receiver adds afterwards stays where it is. Fading the sum instead would
    /// fade the noise too, which no operator has ever heard, and would leave the
    /// SNR constant through the fade - hiding the very effect being tested.
    ///
    /// The envelope matches CwSignalGenerator.ApplyQsb: a raised cosine that
    /// touches 0 dB at the peak and -depth at the trough, so "20 dB QSB" means
    /// the signal is full strength at its best and 20 dB down at its worst.
    /// </summary>
    private static void ApplyFade(float[] samples, int rate, double depthDb, double fadeHz)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double t  = (double)i / rate;
            double dB = -0.5 * depthDb * (1.0 - Math.Cos(2.0 * Math.PI * fadeHz * t));
            samples[i] = (float)(samples[i] * Math.Pow(10.0, dB / 20.0));
        }
    }

    /// <summary>
    /// Score a transcript against the text that was actually sent.
    ///
    /// Levenshtein over a normalised form, reported as 1 - distance/length, so
    /// an insertion of rubbish costs as much as a dropped character. Both sides
    /// are upper-cased and run-length collapsed on whitespace; the ARRL files
    /// carry a "= NOW 20 WPM =" header the decoder never hears, so anything
    /// before the last '=' on the first line is dropped.
    /// </summary>
    private static (double accuracy, int sent, int got) Score(string expected, string actual)
    {
        expected = StripHeader(expected);
        string a = Normalise(expected), b = Normalise(actual);
        if (a.Length == 0) return (0.0, 0, b.Length);

        int d = Levenshtein(a, b);
        return (Math.Max(0.0, 1.0 - (double)d / a.Length), a.Length, b.Length);
    }

    private static readonly char[] NewlineChars = { (char)13, (char)10 };

    private static string StripHeader(string text)
    {
        int nl = text.IndexOfAny(NewlineChars);
        if (nl < 0) return text;
        string first = text[..nl];
        return first.Contains("WPM") ? text[(nl + 1)..] : text;
    }

    private static string Normalise(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = true;
        foreach (char c in s.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(c)) { if (!space) { sb.Append(' '); space = true; } continue; }
            sb.Append(c);
            space = false;
        }
        return sb.ToString().Trim();
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur  = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// What the element decoder is actually handed, run by run.
    ///
    /// Section 4.11d.1 prescribed holding the speed while SignalPresent is
    /// false, on the reasoning that noise trains the tracker through the quiet.
    /// That turns out to be inert, and this report is why: the detector already
    /// forces key-up when the gate says absent, so a mark can only exist inside
    /// a presence excursion and the gate reads true at almost every edge the
    /// decoder sees. The excursions themselves are the noise.
    /// </summary>
    private static void MarksReport(float[] samples, int rate, double pitch, double search, bool track, int listCount, double warmupSeconds,
                                    double traceFrom = double.MaxValue, double traceTo = double.MinValue,
                                    double keyDebounceMs = 0.0)
    {
        var detector = new CwToneDetector(new CwToneDetectorOptions
        {
            InputSampleRate = rate,
            PitchHz         = pitch,
            SearchWindowHz  = search,
            TrackPitch      = track,
            WarmupSeconds   = warmupSeconds,
            KeyDebounceMs   = keyDebounceMs,
        });

        var buffer = new List<CwToneSample>();
        var frame  = rate / 100;

        bool   keyDown = false, started = false, runAllPresent = true;
        double edge = 0.0;

        var markAt      = new List<double>();
        var markMs      = new List<double>();
        var markSnr     = new List<double>();   // mean over the mark
        var markMag     = new List<double>();   // peak over the mark
        var markOverNoise = new List<double>();  // mark peak as a multiple of the noise floor
        double runSnr = 0.0, runMag = 0.0, runNoise = 0.0; int runN = 0;
        var markPresent = new List<bool>();     // presence at the closing edge
        var markSolid   = new List<bool>();     // presence held for the whole mark
        var excursions  = 0;
        var lastPresent = false;
        var presentSecs = 0.0;
        var sampleSecs  = 0.0;

        for (var offset = 0; offset < samples.Length; offset += frame)
        {
            var n = Math.Min(frame, samples.Length - offset);
            buffer.Clear();
            detector.Process(samples.AsSpan(offset, n), buffer);

            foreach (var s in buffer)
            {
                if (sampleSecs == 0.0) sampleSecs = 0.005;      // one envelope hop
                if (s.SignalPresent) presentSecs += 0.005;
                if (s.SignalPresent && !lastPresent) excursions++;
                lastPresent = s.SignalPresent;

                double tMs = s.TimeSeconds * 1000.0;

                if (s.TimeSeconds >= traceFrom && s.TimeSeconds <= traceTo)
                {
                    // on at half way from the noise up to the mark reference,
                    // off at 35% - the same shaping the detector applies.
                    double span   = s.MarkLevel - s.NoiseLevel;
                    double onThr  = s.NoiseLevel + 0.50 * span;
                    double offThr = s.NoiseLevel + 0.35 * span;
                    Console.WriteLine(
                        $"{s.TimeSeconds,8:F3}  mag {Db(s.Magnitude),7:F1}  noise {Db(s.NoiseLevel),7:F1}" +
                        $"  mark {Db(s.MarkLevel),7:F1}  on {Db(onThr),7:F1}  off {Db(offThr),7:F1}" +
                        $"  {(s.KeyDown ? "DOWN" : "  up")}  {(s.SignalPresent ? "P" : " ")}" +
                        $"  {new string('#', (int)Math.Clamp((Db(s.Magnitude) - Db(onThr)) * 2.0 + 20, 0, 60))}");
                }

                if (!started)
                {
                    started = true; keyDown = s.KeyDown; edge = tMs;
                    runAllPresent = s.SignalPresent;
                    continue;
                }

                if (s.KeyDown == keyDown)
                {
                    if (keyDown && !s.SignalPresent) runAllPresent = false;
                    if (keyDown)
                    {
                        runSnr += s.SnrDb;
                        runMag = Math.Max(runMag, s.Magnitude);
                        runNoise += s.NoiseLevel;
                        runN++;
                    }
                    continue;
                }

                double durationMs = tMs - edge;
                if (keyDown && durationMs >= 12.0)   // the decoder's MinElementMs
                {
                    markAt.Add(tMs / 1000.0);
                    markMs.Add(durationMs);
                    markPresent.Add(s.SignalPresent);
                    markSolid.Add(runAllPresent);
                    markSnr.Add(runN > 0 ? runSnr / runN : 0.0);
                    markMag.Add(runMag);
                    double noise = runN > 0 ? runNoise / runN : 0.0;
                    markOverNoise.Add(noise > 1e-12 ? runMag / noise : 0.0);
                }

                runSnr = 0.0; runMag = 0.0; runNoise = 0.0; runN = 0;

                edge = tMs;
                keyDown = s.KeyDown;
                runAllPresent = s.SignalPresent;
            }
        }

        int endPresent = markPresent.Count(p => p);
        int solid      = markSolid.Count(p => p);
        var sorted     = markMs.OrderBy(m => m).ToList();
        double median  = sorted.Count > 0 ? sorted[sorted.Count / 2] : 0.0;

        Console.WriteLine("--- marks ---------------------------------------------------");
        Console.WriteLine($"presence     on for {presentSecs:F1} s of {samples.Length / (double)rate:F1} s, "
                        + $"in {excursions} excursions");
        Console.WriteLine($"marks        {markMs.Count} at or over 12 ms, median {median:F0} ms");
        Console.WriteLine($"             gate true at the closing edge: {endPresent}, false: {markMs.Count - endPresent}");
        Console.WriteLine($"             gate true for the whole mark:  {solid}, patchy: {markMs.Count - solid}");
        // Shape of the mark population. Real Morse has two lengths in a 3:1
        // ratio, so p90/p10 sits near 3; a detector chattering on a
        // near-threshold tone produces one length and a spread under 2.
        if (sorted.Count >= 10)
        {
            double Pct(double q) => sorted[Math.Clamp((int)(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
            double p10 = Pct(0.10), p90 = Pct(0.90);
            double markSecs = markMs.Sum() / 1000.0;
            Console.WriteLine($"             p10 {p10,4:F0} ms   p50 {median,4:F0} ms   p90 {p90,4:F0} ms   "
                            + $"spread {(p10 > 0 ? p90 / p10 : 0),4:F2}");
            Console.WriteLine($"             key-down {markSecs / (samples.Length / (double)rate) * 100.0,4:F1}% of the file");
        }

        if (listCount > 0)
        {
            Console.WriteLine("             at        ms     SNR      peak   x noise");
            for (int i = 0; i < Math.Min(listCount, markMs.Count); i++)
                Console.WriteLine($"             {markAt[i],6:F2} {markMs[i],6:F0} {markSnr[i],7:F1} {markMag[i],9:F4} "
                                + $"{markOverNoise[i],9:F1}");
            Console.WriteLine();
        }

        Console.WriteLine("             bucket      n   median SNR   median peak   median x noise");
        foreach (var (label, lo, hi) in new[]
                 {
                     ("<20 ms", 0.0, 20.0), ("20-40 ms", 20.0, 40.0), ("40-80 ms", 40.0, 80.0),
                     ("80-160 ms", 80.0, 160.0), (">160 ms", 160.0, double.MaxValue),
                 })
        {
            var idx = Enumerable.Range(0, markMs.Count).Where(i => markMs[i] >= lo && markMs[i] < hi).ToList();
            if (idx.Count == 0) { Console.WriteLine($"             {label,-10}  0"); continue; }
            var snr = idx.Select(i => markSnr[i]).OrderBy(v => v).ToList();
            var mag = idx.Select(i => markMag[i]).OrderBy(v => v).ToList();
            var xn  = idx.Select(i => markOverNoise[i]).OrderBy(v => v).ToList();
            Console.WriteLine($"             {label,-10} {idx.Count,3}   {snr[snr.Count / 2],8:F1} dB   {mag[mag.Count / 2],11:F4}   {xn[xn.Count / 2],14:F1}");
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Runs of four or more identical single-element characters - the
    /// TTTTTTTTT and EEEEE strings the plan's 4.11d table counts. Only
    /// characters whose Morse symbol is all dits or all dahs qualify, and a
    /// space breaks a run.
    /// </summary>
    private static (int Runs, int Longest) CountElementRuns(string text)
    {
        const string singleElement = "ETIMSOH50";
        int runs = 0, longest = 0, i = 0;

        while (i < text.Length)
        {
            char c = text[i];
            int j = i;
            while (j < text.Length && text[j] == c) j++;

            int len = j - i;
            if (len >= 4 && singleElement.IndexOf(c) >= 0)
            {
                runs++;
                if (len > longest) longest = len;
            }

            i = j;
        }

        return (runs, longest);
    }

    private static string Stamp(double seconds)
        => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");

    private static string Linear(float v)
        => v <= 0 ? "-inf dBFS" : $"{20 * Math.Log10(v):F1} dBFS";

    private static double Db(double v) => 20.0 * Math.Log10(Math.Max(v, 1e-12));

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
              --filter <Hz>     the radio's IF filter width; sets the search window
                                to the passband instead of a fixed 250 Hz
              --no-track        pin the detector to --pitch instead of hunting
              --wpm <n>         start the speed tracker at n wpm instead of 20
              --no-resync       stop the hard re-seed firing, leaving only the
                                EMA, to tell the two apart on a runaway
              --marks           report the runs the element decoder is handed,
                                against what the presence gate said at the time
              --warmup <s>      detector settling time before anything counts as
                                keyed; 0 is the pre-fix behaviour
              --train-noise <x> a mark must peak at x times the noise floor
                                before it may train the speed
              --pin-wpm <n>     hold the speed at n wpm so it cannot track at
                                all — tells a tracker fault apart from an
                                element-timing fault on the same recording
              --telemetry <s>   seconds between telemetry lines    (default 5, 0 = off)
              --raw             transcript only, no timestamps or telemetry
              --spectrum        say what is in the recording before decoding it,
                                so a transcript of noise is not mistaken for a
                                transcript of a signal
              --timeline [Hz]   the same, but a second at a time, so a fading
                                signal is not averaged away and the fades can be
                                lined up against --telemetry 1

            Record mono, 48 kHz, from the radio's USB CODEC — see
            docs/design/cw-bench-procedure.md.
            """);
    }
}
