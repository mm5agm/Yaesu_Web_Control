using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using RadioWebControl.Core.Services.Rtty;

namespace RadioWebControl.Core.Tools.RttyBench;

/// <summary>
/// Decode a recorded RTTY signal offline, over and over, to find the settings
/// that copy it best.
///
/// <para><b>Why this exists.</b> Tuning a decoder against a live signal does
/// not work, and the reason is measurable rather than theoretical. On
/// 2026-10-10 the same configuration - same dial, same mark, same shift, same
/// baud, same IF width, nothing changed - was scored twice on a real contest
/// station seventy-five seconds apart and came back 80% then 62%, with
/// framing errors of 18.6% then 31.9%. Every "improvement" measured that
/// afternoon was smaller than that. The signal fades, the sending station
/// starts a QSO, the band changes: an A/B test across two different pieces of
/// time is not an A/B test.</para>
///
/// <para>So the audio is recorded once and decoded many times. Everything
/// that is not the setting under test is then genuinely identical, and a
/// difference of two percent means something.</para>
///
/// <para><b>What it cannot answer.</b> Anything in front of the recording -
/// the IF filter width, the attenuator, AGC, the dial. Those are the radio's,
/// they are baked into the samples, and comparing them needs one recording
/// each and enough repeats to beat the fading. The bench is for everything
/// after the audio: mark, shift, baud, squelch, the figures table, USOS and
/// polarity.</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                RttyBench <file.wav> [options]

                  --mark <hz>        mark tone, default 2125
                  --shift <hz>       mark to space, default 170
                  --baud <n>         default 45.45
                  --reverse          put space below mark instead of above
                  --usos on|off      unshift on space, default on
                  --figures ita2|ustty   default ustty (what amateurs send)
                  --squelch <n>      default 0.45
                  --text             print the decoded text as well as the score
                  --sweep <axis>     mark | shift | baud | squelch | flags | all
                  --analyse          run the Auto analyser over 4 s blocks instead
                  --low <hz> --high <hz>   the analyser's scan window; the default is the
                                     app's own, not a copy of it

                The score counts only what noise does not produce: contest
                vocabulary, four-character grid squares, and tokens that appear
                twice. Callsign-shaped tokens seen once are NOT counted - 28%
                of random five-character groups look like a callsign.
                """);
            return args.Length == 0 ? 1 : 0;
        }

        string path = args[0];
        if (!File.Exists(path)) { Console.Error.WriteLine($"no such file: {path}"); return 1; }

        var o = Options.Parse(args);
        float[] audio = ReadWavMono(path, out int sampleRate);
        Console.WriteLine($"{Path.GetFileName(path)}: {audio.Length / (double)sampleRate:N1} s @ {sampleRate} Hz");
        Console.WriteLine();

        if (o.Analyse)
        {
            // Four-second blocks, because that is what the live Auto button
            // records. Every block is reported, agreed or refused: a refusal
            // that prints nothing is how a sweep of sixty-three stops produced
            // no evidence at all about any of them.
            int block = sampleRate * 4;
            Console.WriteLine("  block     outcome       | early:  mark/space  shift   baud conf marg | late:   mark/space  shift   baud conf marg");
            for (int i = 0, n = 0; i + block <= audio.Length; i += block, n++)
            {
                var a = RttySignalAnalyser.AnalyseAgreed(audio.AsSpan(i, block), sampleRate,
                                                         o.LowHz, o.HighHz);
                // Both halves, always, and not just the one that happened to
                // survive. A refusal IS a disagreement between them, so printing
                // one of them says nothing about why it was refused - which is
                // how a scan-window theory survived an afternoon before being
                // tested and found wrong in one command.
                static string Fmt(RttySignalEstimate? e) => e is null
                    ? "            -"
                    : string.Format(CultureInfo.InvariantCulture,
                        " {0,6:F0}/{1,-6:F0} {2,5:F0} {3,6:F2} {4,4:F2} {5,4:F2}",
                        e.MarkHz, e.SpaceHz, e.ShiftHz, e.Baud, e.Confidence,
                        // The polarity margin, which is the only thing that
                        // distinguishes a block measured right from one measured
                        // with mark and space the wrong way round. Both come out
                        // of the same keying fit with the same confidence, so
                        // confidence cannot tell them apart and this can.
                        e.ToneMargin);

                // The whole-block estimate, even when the halves were refused.
                // AnalyseAgreed computes it first and then discards it, and it
                // is measured over four seconds rather than two - so whether it
                // is right on a refused block is exactly the question a fix to
                // the speed gate turns on.
                var whole = RttySignalAnalyser.Analyse(audio.AsSpan(i, block), sampleRate, o.LowHz, o.HighHz);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,5:F0} s  {1,-18} |{2} |{3} ||{4}",
                    n * 4, a.Outcome, Fmt(a.Early), Fmt(a.Late), Fmt(whole)));
            }
            return 0;
        }

        if (o.Sweep is null)
        {
            Result r = Decode(audio, sampleRate, o);
            Console.WriteLine(Header);
            Console.WriteLine(r.Row(o.Describe()));
            if (o.ShowText)
            {
                Console.WriteLine();
                Console.WriteLine(r.Text.Trim());
            }
            return 0;
        }

        var runs = new List<(string Label, Result R)>();
        foreach (Options v in o.Variants(o.Sweep))
            runs.Add((v.Describe(), Decode(audio, sampleRate, v)));

        Console.WriteLine(Header);
        foreach (var (label, r) in runs.OrderByDescending(x => x.R.Score).ThenBy(x => x.R.FramingPercent))
            Console.WriteLine(r.Row(label));

        var best = runs.OrderByDescending(x => x.R.Score).ThenBy(x => x.R.FramingPercent).First();
        Console.WriteLine();
        Console.WriteLine($"best: {best.Label}");
        if (o.ShowText)
        {
            Console.WriteLine();
            Console.WriteLine(best.R.Text.Trim());
        }
        return 0;
    }

    private const string Header =
        "  score   known/tok   chars  framing   config";

    private sealed record Result(string Text, long Framing, int Chars, int Tokens, int Known)
    {
        public double Score => Tokens == 0 ? 0 : 100.0 * Known / Tokens;
        public double FramingPercent => Chars == 0 ? 0 : 100.0 * Framing / Chars;

        public string Row(string label) =>
            string.Format(CultureInfo.InvariantCulture,
                "{0,6:N1}%  {1,4}/{2,-4}  {3,6}  {4,5:N1}%   {5}",
                Score, Known, Tokens, Chars, FramingPercent, label);
    }

    private static Result Decode(float[] audio, int sampleRate, Options o)
    {
        double space = o.Reverse ? o.MarkHz - o.ShiftHz : o.MarkHz + o.ShiftHz;
        var d = new RttyDemodulator(sampleRate, o.MarkHz, space, o.Baud, o.Figures, o.Usos)
        {
            Squelch = o.Squelch,
        };

        // Feed in blocks rather than one call, so the bench exercises the same
        // streaming path the live reader does. A single giant Feed would hide
        // any state that only goes wrong across a block boundary.
        var sb = new StringBuilder();
        const int block = 4800;
        for (int i = 0; i < audio.Length; i += block)
            sb.Append(d.Feed(audio.AsSpan(i, Math.Min(block, audio.Length - i))));

        string text = sb.ToString();
        (int tokens, int known) = ScoreText(text);
        return new Result(text, d.FramingErrors, text.Length, tokens, known);
    }

    // ---- the score -------------------------------------------------------

    private static readonly HashSet<string> Vocabulary = new(StringComparer.Ordinal)
    {
        "CQ", "TEST", "DE", "TU", "RY", "QRZ", "UP", "AGN", "NR", "599", "5NN",
        "73", "BK", "K", "TNX", "GL", "QSL", "R", "RST", "NW", "PSE",
    };

    private static readonly Regex TokenRe = new("[A-Z0-9/]{2,}", RegexOptions.Compiled);
    private static readonly Regex GridRe = new("^[A-R]{2}[0-9]{2}$", RegexOptions.Compiled);

    /// <summary>
    /// A callsign has a digit separating a prefix from a suffix of letters.
    /// Used only to decide whether a repeat is worth counting - never on its
    /// own, because a quarter of pure hash matches this.
    /// </summary>
    private static readonly Regex CallRe =
        new("^(?:[A-Z]{1,2}|[0-9][A-Z]{1,2})[0-9]{1,2}[A-Z]{1,3}(?:/[A-Z0-9]{1,3})?$", RegexOptions.Compiled);

    private static (int Tokens, int Known) ScoreText(string text)
    {
        string up = text.ToUpperInvariant();
        var tokens = TokenRe.Matches(up).Select(m => m.Value).ToList();
        if (tokens.Count == 0) return (0, 0);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string t in tokens) counts[t] = counts.TryGetValue(t, out int n) ? n + 1 : 1;

        int known = 0;
        foreach (string t in tokens)
        {
            bool vocab = Vocabulary.Contains(t);
            bool grid = GridRe.IsMatch(t);
            // A token that appears twice and could be a callsign, a grid or a
            // contest word. Contest RTTY sends the callsign and the exchange
            // twice on purpose; noise does not repeat a five-character group -
            // the chance is about one in sixty million.
            bool repeat = counts[t] >= 2 && (vocab || grid || CallRe.IsMatch(t));
            if (vocab || grid || repeat) known++;
        }
        return (tokens.Count, known);
    }

    // ---- options ---------------------------------------------------------

    private sealed record Options
    {
        public double MarkHz { get; init; } = 2125;
        public double ShiftHz { get; init; } = 170;
        public double Baud { get; init; } = 45.45;
        public bool Reverse { get; init; }
        public bool Usos { get; init; } = true;
        public RttyFigureSet Figures { get; init; } = RttyFigureSet.UsTty;
        public double Squelch { get; init; } = 0.45;
        public bool ShowText { get; init; }
        public string? Sweep { get; init; }
        public bool Analyse { get; init; }
        // The production defaults, not copies of them: a bench whose scan range
        // differs from the app's measures a decoder nobody is running.
        public double LowHz { get; init; } = RttySignalAnalyser.DefaultLowHz;
        public double HighHz { get; init; } = RttySignalAnalyser.DefaultHighHz;

        public string Describe() => string.Format(CultureInfo.InvariantCulture,
            "mark {0:N0} shift {1:N0} baud {2:N2} {3} usos {4} {5} sq {6:N2}",
            MarkHz, ShiftHz, Baud, Reverse ? "rev" : "nor", Usos ? "on " : "off",
            Figures == RttyFigureSet.UsTty ? "ustty" : "ita2 ", Squelch);

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{a} needs a value");
                double Num() => double.Parse(Next(), CultureInfo.InvariantCulture);
                o = a switch
                {
                    "--mark" => o with { MarkHz = Num() },
                    "--shift" => o with { ShiftHz = Num() },
                    "--baud" => o with { Baud = Num() },
                    "--reverse" => o with { Reverse = true },
                    "--squelch" => o with { Squelch = Num() },
                    "--usos" => o with { Usos = Next() is not "off" },
                    "--figures" => o with { Figures = Next() is "ita2" ? RttyFigureSet.Ita2 : RttyFigureSet.UsTty },
                    "--text" => o with { ShowText = true },
                    "--sweep" => o with { Sweep = Next() },
                    "--analyse" => o with { Analyse = true },
                    "--low" => o with { LowHz = Num() },
                    "--high" => o with { HighHz = Num() },
                    _ => throw new ArgumentException($"unknown option {a}"),
                };
            }
            return o;
        }

        /// <summary>One axis at a time, because a grid over everything reports
        /// a winner without saying which change earned it.</summary>
        public IEnumerable<Options> Variants(string axis)
        {
            switch (axis)
            {
                case "mark":
                    for (int d = -80; d <= 80; d += 10) yield return this with { MarkHz = MarkHz + d };
                    break;
                case "shift":
                    foreach (int s in new[] { 170, 180, 200, 220, 425, 450, 850 })
                        yield return this with { ShiftHz = s };
                    break;
                case "baud":
                    foreach (double b in new[] { 45.0, 45.45, 45.9, 50, 56, 75 })
                        yield return this with { Baud = b };
                    break;
                case "squelch":
                    foreach (double s in new[] { 0.10, 0.20, 0.30, 0.45, 0.60, 0.80 })
                        yield return this with { Squelch = s };
                    break;
                case "flags":
                    foreach (bool rev in new[] { false, true })
                        foreach (bool us in new[] { true, false })
                            foreach (var f in new[] { RttyFigureSet.UsTty, RttyFigureSet.Ita2 })
                                yield return this with { Reverse = rev, Usos = us, Figures = f };
                    break;
                case "all":
                    foreach (string ax in new[] { "flags", "mark", "baud", "squelch" })
                        foreach (Options v in Variants(ax)) yield return v;
                    break;
                default:
                    throw new ArgumentException($"unknown sweep axis '{axis}'");
            }
        }
    }

    // ---- WAV -------------------------------------------------------------

    /// <summary>
    /// Enough of RIFF/WAVE to read what the app records: 16-bit PCM, any rate,
    /// mono or stereo. Chunks are walked rather than assumed, because a 'LIST'
    /// chunk before 'data' is common and a fixed 44-byte header reads it as
    /// samples.
    /// </summary>
    private static float[] ReadWavMono(string path, out int sampleRate)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);

        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a RIFF file");
        r.ReadUInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAVE file");

        int channels = 0, bits = 0;
        sampleRate = 0;
        byte[]? data = null;

        while (fs.Position + 8 <= fs.Length)
        {
            string id = new(r.ReadChars(4));
            uint size = r.ReadUInt32();
            long next = fs.Position + size + (size & 1);   // chunks are word-aligned

            if (id == "fmt ")
            {
                r.ReadUInt16();                 // format tag
                channels = r.ReadUInt16();
                sampleRate = (int)r.ReadUInt32();
                r.ReadUInt32();                 // byte rate
                r.ReadUInt16();                 // block align
                bits = r.ReadUInt16();
            }
            else if (id == "data")
            {
                data = r.ReadBytes((int)size);
            }

            fs.Position = next;
        }

        if (data is null || channels == 0) throw new InvalidDataException("no fmt/data chunk");
        if (bits != 16) throw new InvalidDataException($"expected 16-bit PCM, got {bits}-bit");

        int frames = data.Length / 2 / channels;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            int sum = 0;
            for (int c = 0; c < channels; c++)
                sum += BitConverter.ToInt16(data, (i * channels + c) * 2);
            mono[i] = sum / (float)(channels * 32768);
        }
        return mono;
    }
}
