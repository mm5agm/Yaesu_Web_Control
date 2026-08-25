namespace CwBench;

/// <summary>
/// Answers the first question every bench recording raises: is there a signal in
/// here at all, and is anything being keyed?
///
/// Off the air that is not obvious. A transcript of "EEEIS EEE" could be a weak
/// signal being half-copied or it could be hiss, and those want opposite
/// responses - one is a decoder to improve, the other is a recording to throw
/// away. Rather than guess from the transcript, measure the audio directly.
///
/// A bank of Goertzel filters, one per bin, run over every frame. Two figures
/// come out of each bin:
///
///   level   the mean magnitude, which says where the energy is;
///   keying  the ratio of the loud frames to the typical frame, which says
///           whether that energy is being switched on and off.
///
/// Steady energy - carrier, hum, hiss - has a keying ratio near 1. A keyed CW
/// tone spends more time off than on, so its median frame is silence and its
/// loud frames are full scale, which gives a large ratio. That difference is the
/// whole point: it separates "there was CW here" from "there was noise here"
/// without any reference to whether the decoder managed to read it.
/// </summary>
internal static class Spectrum
{
    internal readonly record struct Bin(double Hz, double Level, double Keying);

    public static IReadOnlyList<Bin> Analyse(float[] samples, int rate,
                                             double fromHz = 100, double toHz = 3000,
                                             double stepHz = 25)
    {
        var frame  = rate / 100;                       // 10 ms, as the decoder uses
        var frames = samples.Length / frame;
        if (frames < 10) return Array.Empty<Bin>();

        var bins = new List<Bin>();
        for (var hz = fromHz; hz <= toHz; hz += stepHz)
        {
            var mags = new double[frames];
            var k    = 2 * Math.Cos(2 * Math.PI * hz / rate);

            for (var f = 0; f < frames; f++)
            {
                // Goertzel: a two-tap recurrence whose final state holds the
                // magnitude at this one frequency. Cheaper than an FFT when the
                // bins wanted are few and need not be evenly spaced on a radix.
                double s1 = 0, s2 = 0;
                var    at = f * frame;
                for (var i = 0; i < frame; i++)
                {
                    var s = samples[at + i] + k * s1 - s2;
                    s2 = s1;
                    s1 = s;
                }
                mags[f] = Math.Sqrt(s1 * s1 + s2 * s2 - k * s1 * s2) / frame;
            }

            var sorted = (double[])mags.Clone();
            Array.Sort(sorted);
            var median = sorted[sorted.Length / 2];
            var loud   = sorted[(int)(sorted.Length * 0.95)];
            var mean   = mags.Average();

            // A silent median would divide by zero on a perfectly clean file;
            // floor it at something below any real recording's noise.
            bins.Add(new Bin(hz, mean, loud / Math.Max(median, 1e-9)));
        }

        return bins;
    }

    public static void Report(float[] samples, int rate)
    {
        var bins = Analyse(samples, rate);
        if (bins.Count == 0) { Console.WriteLine("spectrum  recording too short to analyse"); return; }

        var peak   = bins.MaxBy(b => b.Level);
        var keyed  = bins.MaxBy(b => b.Keying);
        var levels = bins.Select(b => b.Level).ToArray();
        Array.Sort(levels);
        var flat = levels[levels.Length / 2] / Math.Max(peak.Level, 1e-12);

        Console.WriteLine("--- spectrum ------------------------------------------------");
        Console.WriteLine($"strongest    {peak.Hz:F0} Hz   {Db(peak.Level)}");
        Console.WriteLine($"most keyed   {keyed.Hz:F0} Hz   keying ratio {keyed.Keying:F1}");
        Console.WriteLine($"flatness     {flat:F2}   (median bin / strongest bin)");
        Console.WriteLine();

        // Only the bins worth looking at. Everything within 20 dB of the peak,
        // which on a quiet band is most of them and on a real signal is few.
        Console.WriteLine("  Hz    level      keying");
        foreach (var b in bins.Where(b => b.Level > peak.Level / 10).OrderByDescending(b => b.Level).Take(12))
            Console.WriteLine($"  {b.Hz,4:F0}  {Db(b.Level),9}  {b.Keying,6:F1}  {Bar(b.Level / peak.Level)}");

        Console.WriteLine();
        if (keyed.Keying < 3)
            Console.WriteLine("VERDICT      nothing is being keyed - this is noise, not CW.");
        else if (keyed.Keying < 8)
            Console.WriteLine($"VERDICT      something is keyed around {keyed.Hz:F0} Hz, but weakly.");
        else
            Console.WriteLine($"VERDICT      a keyed tone at {keyed.Hz:F0} Hz - decode this against the radio.");
        Console.WriteLine();
    }

    private static string Db(double v) => v <= 0 ? "-inf dB" : $"{20 * Math.Log10(v):F1} dB";

    private static string Bar(double fraction)
        => new('#', Math.Max(1, (int)(fraction * 30)));
}
