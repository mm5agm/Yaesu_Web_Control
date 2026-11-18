using Yaesu_Web_Control.Services.Audio;

namespace YaesuWebControl.Tests;

/// <summary>
/// The audio waterfall's mode guesser, fed synthetic radio audio: each mode
/// built from its published tones and speed, with noise, through the same
/// analyser the service uses. These check the rules hold for clean signals;
/// real off-air audio is the test that matters.
/// </summary>
public class AudioModeClassifierTests
{
    private const int Rate = 48_000;
    private static readonly DateTime Start = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    // ── building audio ────────────────────────────────────────────────────────

    private static float[] Noise(double seconds, int seed = 1, double sigma = 0.01)
    {
        var rng = new Random(seed);
        var a = new float[(int)(seconds * Rate)];
        for (int i = 0; i < a.Length; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            a[i] = (float)(sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        return a;
    }

    private static void Mix(float[] into, float[] add)
    {
        for (int i = 0; i < into.Length && i < add.Length; i++) into[i] += add[i];
    }

    /// <summary>
    /// Continuous-phase FSK: random symbols from tones f0, f0+spacing, ...,
    /// at baud symbols a second, sent only while on(t) says so.
    /// </summary>
    private static float[] Fsk(double seconds, double f0, double spacing, int tones, double baud,
        Func<double, bool>? on = null, int seed = 7, double amp = 0.003)
    {
        var rng = new Random(seed);
        var a = new float[(int)(seconds * Rate)];
        double phase = 0;
        int symbol = -1, tone = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double t = (double)i / Rate;
            int s = (int)(t * baud);
            if (s != symbol) { symbol = s; tone = rng.Next(tones); }
            phase += 2 * Math.PI * (f0 + tone * spacing) / Rate;
            if (on is null || on(t)) a[i] = (float)(amp * Math.Sin(phase));
        }
        return a;
    }

    private static float[] Psk31(double seconds, double f, int seed = 3, double amp = 0.003)
    {
        var rng = new Random(seed);
        const double T = 1 / 31.25;
        var a = new float[(int)(seconds * Rate)];
        double prev = 1, cur = 1;
        int symbol = -1;
        for (int i = 0; i < a.Length; i++)
        {
            double t = (double)i / Rate;
            int s = (int)(t / T);
            if (s != symbol) { symbol = s; prev = cur; if (rng.Next(2) == 0) cur = -cur; }
            double x = (t - s * T) / T;
            double env = prev * (1 + Math.Cos(Math.PI * x)) / 2 + cur * (1 - Math.Cos(Math.PI * x)) / 2;
            a[i] = (float)(amp * env * Math.Sin(2 * Math.PI * f * t));
        }
        return a;
    }

    /// <summary>Morse at 20 wpm (60 ms dit) with 5 ms edges, over and over.</summary>
    private static float[] Cw(double seconds, double f, double amp = 0.003)
    {
        const string text = "CQ CQ DE MM5AGM MM5AGM K ";
        var code = new Dictionary<char, string>
        {
            ['C'] = "-.-.", ['Q'] = "--.-", ['D'] = "-..", ['E'] = ".", ['M'] = "--",
            ['5'] = ".....", ['A'] = ".-", ['G'] = "--.", ['K'] = "-.-",
        };
        var keyed = new List<bool>();   // one entry per dit
        foreach (char ch in text)
        {
            if (ch == ' ') { keyed.AddRange(new[] { false, false, false, false }); continue; }
            foreach (char e in code[ch])
            {
                keyed.AddRange(Enumerable.Repeat(true, e == '-' ? 3 : 1));
                keyed.Add(false);
            }
            keyed.AddRange(new[] { false, false });
        }
        const double dit = 0.06, edge = 0.005;
        var a = new float[(int)(seconds * Rate)];
        double env = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double t = (double)i / Rate;
            bool down = keyed[(int)(t / dit) % keyed.Count];
            env = Math.Clamp(env + (down ? 1 : -1) / (edge * Rate), 0, 1);
            a[i] = (float)(amp * env * Math.Sin(2 * Math.PI * f * t));
        }
        return a;
    }

    private static IReadOnlyList<AudioModeGuess> Classify(float[] audio, IReadOnlyList<AudioDecodeHint>? hints = null)
    {
        var analyser = new AudioWaterfallAnalyser();
        var guesser = new AudioModeClassifier(analyser.BinHz, analyser.FrameSeconds, analyser.BinCount);
        long fed = 0;
        const int chunk = 480;
        for (int i = 0; i < audio.Length; i += chunk)
        {
            int n = Math.Min(chunk, audio.Length - i);
            fed += n;
            var at = Start.AddSeconds((double)fed / Rate);
            analyser.Push(audio.AsSpan(i, n), levels => guesser.Add(levels, at));
        }
        return guesser.Classify(hints);
    }

    private static AudioModeGuess Only(IReadOnlyList<AudioModeGuess> guesses)
    {
        Assert.True(guesses.Count == 1,
            "Expected one signal, got: " + string.Join("; ", guesses.Select(g => $"{g.Mode} {g.LowHz:0}-{g.HighHz:0} ({g.Detail})")));
        return guesses[0];
    }

    private const double Seconds = 32;

    // ── the analyser ──────────────────────────────────────────────────────────

    [Fact]
    public void AToneLandsInItsBin()
    {
        var analyser = new AudioWaterfallAnalyser();
        Assert.Equal(48_000.0 / 8192, analyser.BinHz, 6);
        Assert.Equal(683, analyser.BinCount);
        var tone = new float[8192 * 2];
        for (int i = 0; i < tone.Length; i++) tone[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / Rate);
        float[]? last = null;
        int frames = 0;
        analyser.Push(tone, l => { last = l.ToArray(); frames++; });
        Assert.Equal(1 + 8192 / 2048, frames);
        int peak = Array.IndexOf(last!, last!.Max());
        Assert.Equal((int)Math.Round(1000 / analyser.BinHz), peak);
        Assert.InRange(last![peak], -3f, 0.5f);   // a full-scale sine is about 0 dBFS
    }

    [Fact]
    public void BytesSpanMinus140ToMinus20()
    {
        Assert.Equal(0, AudioWaterfallAnalyser.ToByte(-150));
        Assert.Equal(255, AudioWaterfallAnalyser.ToByte(-10));
        Assert.Equal(128, AudioWaterfallAnalyser.ToByte(-80));
    }

    // ── the guesses ───────────────────────────────────────────────────────────

    [Fact]
    public void NoiseAloneIsNothing()
    {
        Assert.Empty(Classify(Noise(Seconds)));
    }

    [Fact]
    public void Ft8()
    {
        var a = Noise(Seconds);
        // 79 symbols of 0.16 s, starting half a second into each 15 s period.
        Mix(a, Fsk(Seconds, 1500, 6.25, 8, 6.25, t => t % 15 is >= 0.5 and < 0.5 + 12.64));
        var g = Only(Classify(a));
        Assert.Equal("FT8", g.Mode);
        Assert.False(g.FromWsjtx);
        Assert.InRange(g.LowHz, 1480, 1505);
        Assert.InRange(g.HighHz, 1540, 1570);
    }

    [Fact]
    public void ABusyFt8BandIsNamedByTheGapAtTheEndOfEachCycle()
    {
        // 28 stations 52 Hz apart, touching into one wide signal, each a
        // little late or early, plus one that never stops and one with its
        // clock 13 s out: as on 20 m on a weekday lunchtime.
        var a = Noise(40);
        var rng = new Random(11);
        for (int i = 0; i < 28; i++)
        {
            double dt = -0.3 + 1.3 * rng.NextDouble();
            double amp = 0.001 + 0.004 * rng.NextDouble();
            double odd = rng.Next(2) * 15;   // even or odd cycles, or both
            Func<double, bool> on = i % 3 == 0
                ? t => t % 15 is var p && p >= 0.5 + dt && p < 0.5 + dt + 12.64
                : t => (t + odd) % 30 is var p && p >= 0.5 + dt && p < 0.5 + dt + 12.64;
            Mix(a, Fsk(40, 450 + 52 * i, 6.25, 8, 6.25, on, seed: 100 + i, amp: amp));
        }
        Mix(a, Fsk(40, 1150, 6.25, 8, 6.25, null, seed: 99, amp: 0.004));
        Mix(a, Fsk(40, 1360, 6.25, 8, 6.25, t => t % 15 is >= 13.0 or < 0.6, seed: 98, amp: 0.004));

        var guesses = Classify(a);
        string all = string.Join("; ", guesses.Select(g => $"{g.Mode} {g.LowHz:0}-{g.HighHz:0} ({g.Detail})"));
        Assert.Contains(guesses, g => g.Detail.StartsWith("many FT8") && g.HighHz - g.LowHz > 700);
        double ft8 = guesses.Where(g => g.Mode == "FT8").Sum(g => g.HighHz - g.LowHz);
        double other = guesses.Where(g => g.Mode != "FT8").Sum(g => g.HighHz - g.LowHz);
        Assert.True(ft8 > 4 * other, all);
    }

    [Fact]
    public void OneFt8SlotUsedInBothHalvesIsFt8NotPsk()
    {
        var a = Noise(40);
        Mix(a, Fsk(40, 1500, 6.25, 8, 6.25, t => t % 30 is >= 0.5 and < 13.14, seed: 21));
        Mix(a, Fsk(40, 1500, 6.25, 8, 6.25, t => t % 30 is >= 15.5 and < 28.14, seed: 22));
        Assert.Equal("FT8", Only(Classify(a)).Mode);
    }

    [Fact]
    public void TwoFt8StationsARttyShiftApartAreNotRtty()
    {
        var a = Noise(40);
        Func<double, bool> both = t => t % 15 is >= 0.5 and < 13.14;
        Mix(a, Fsk(40, 1000, 6.25, 8, 6.25, both, seed: 31));
        Mix(a, Fsk(40, 1223, 6.25, 8, 6.25, both, seed: 32));
        var guesses = Classify(a);
        Assert.DoesNotContain(guesses, g => g.Mode == "RTTY");
        Assert.All(guesses, g => Assert.Equal("FT8", g.Mode));
    }

    [Fact]
    public void NothingIsFoundPastTheReceiveFilter()
    {
        // Noise through a 3 kHz filter, and a trace of leakage at 3.6 kHz:
        // 40 dB under the noise in the passband, but well clear of the
        // nothing past the filter edge.
        var white = Noise(Seconds);
        const int taps = 401;
        const double cut = 3000.0 / Rate;
        var h = new double[taps];
        for (int i = 0; i < taps; i++)
        {
            double m = i - (taps - 1) / 2.0;
            double sinc = m == 0 ? 2 * cut : Math.Sin(2 * Math.PI * cut * m) / (Math.PI * m);
            h[i] = sinc * (0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1)));
        }
        var a = new float[white.Length];
        for (int n = taps; n < white.Length; n++)
        {
            double y = 0;
            for (int i = 0; i < taps; i++) y += h[i] * white[n - i];
            a[n] = (float)y;
        }
        for (int n = 0; n < a.Length; n++) a[n] += (float)(0.00005 * Math.Sin(2 * Math.PI * 3600 * n / Rate));

        Assert.Empty(Classify(a));
    }

    [Fact]
    public void Ft4()
    {
        var a = Noise(Seconds);
        Mix(a, Fsk(Seconds, 1000, 20.833, 4, 20.833, t => t % 7.5 is >= 0.5 and < 0.5 + 4.94));
        Assert.Equal("FT4", Only(Classify(a)).Mode);
    }

    [Fact]
    public void CwIsNamed()
    {
        var a = Noise(Seconds);
        Mix(a, Cw(Seconds, 700));
        var g = Only(Classify(a));
        Assert.Equal("CW", g.Mode);
        Assert.InRange(g.LowHz, 670, 700);
        Assert.InRange(g.HighHz, 700, 730);
    }

    [Fact]
    public void SteadyCarrier()
    {
        var a = Noise(Seconds);
        Mix(a, Fsk(Seconds, 1200, 0, 1, 1));
        Assert.Equal("Carrier", Only(Classify(a)).Mode);
    }

    [Fact]
    public void Rtty170()
    {
        var a = Noise(Seconds);
        Mix(a, Fsk(Seconds, 2125, 170, 2, 45.45));
        var g = Only(Classify(a));
        Assert.Equal("RTTY", g.Mode);
        Assert.Contains("170 Hz shift", g.Detail);
    }

    [Fact]
    public void Psk31IsNamed()
    {
        var a = Noise(Seconds);
        Mix(a, Psk31(Seconds, 1000));
        Assert.Equal("PSK31", Only(Classify(a)).Mode);
    }

    [Fact]
    public void Piccolo6()
    {
        var a = Noise(Seconds);
        Mix(a, Fsk(Seconds, 1800, 20, 6, 20));
        var g = Only(Classify(a));
        Assert.Equal("Piccolo", g.Mode);
        Assert.StartsWith("6 tones 20 Hz apart", g.Detail);
    }

    [Fact]
    public void Olivia8_250()
    {
        var a = Noise(Seconds);
        Mix(a, Fsk(Seconds, 1200, 31.25, 8, 31.25));
        Assert.Equal("Olivia 8/250", Only(Classify(a)).Mode);
    }

    [Fact]
    public void Mfsk16()
    {
        var a = Noise(Seconds);
        Mix(a, Fsk(Seconds, 1500, 15.625, 16, 15.625));
        Assert.Equal("MFSK16", Only(Classify(a)).Mode);
    }

    [Fact]
    public void SeveralAtOnceAreEachNamed()
    {
        var a = Noise(Seconds);
        Mix(a, Cw(Seconds, 600));
        Mix(a, Fsk(Seconds, 1200, 6.25, 8, 6.25, t => t % 15 is >= 0.5 and < 0.5 + 12.64));
        Mix(a, Fsk(Seconds, 2125, 170, 2, 45.45));
        var modes = Classify(a).Select(g => g.Mode).ToList();
        Assert.Equal(new[] { "CW", "FT8", "RTTY" }, modes);
    }

    [Fact]
    public void AWsjtxDecodeBeatsTheGuess()
    {
        var a = Noise(Seconds);
        Mix(a, Fsk(Seconds, 1500, 6.25, 8, 6.25, t => t % 15 is >= 0.5 and < 0.5 + 12.64));
        var g = Only(Classify(a, new[] { new AudioDecodeHint(1500, "FT8") }));
        Assert.Equal("FT8", g.Mode);
        Assert.True(g.FromWsjtx);
    }

    [Fact]
    public void ADecodeElsewhereChangesNothing()
    {
        var a = Noise(Seconds);
        Mix(a, Cw(Seconds, 700));
        var g = Only(Classify(a, new[] { new AudioDecodeHint(2000, "FT8") }));
        Assert.Equal("CW", g.Mode);
        Assert.False(g.FromWsjtx);
    }
}
