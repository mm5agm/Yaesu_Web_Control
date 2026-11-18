namespace Yaesu_Web_Control.Services.Wsjtx
{
    /// <summary>
    /// The usual FT8 and FT4 dial frequencies, the same ones WSJT-X lists by
    /// default, for the big-tile page's band tiles.
    /// </summary>
    public static class WsjtxBands
    {
        public sealed record Band(string Name, long Ft8Hz, long Ft4Hz);

        public static readonly IReadOnlyList<Band> All = new[]
        {
            new Band("160 m",  1_840_000,  1_840_000),
            new Band("80 m",   3_573_000,  3_575_500),
            new Band("60 m",   5_357_000,  5_357_000),
            new Band("40 m",   7_074_000,  7_047_500),
            new Band("30 m",  10_136_000, 10_140_000),
            new Band("20 m",  14_074_000, 14_080_000),
            new Band("17 m",  18_100_000, 18_104_000),
            new Band("15 m",  21_074_000, 21_140_000),
            new Band("12 m",  24_915_000, 24_919_000),
            new Band("10 m",  28_074_000, 28_180_000),
            new Band("6 m",   50_313_000, 50_318_000),
        };

        public static long DialFor(Band band, string mode) =>
            string.Equals(mode, "FT4", StringComparison.OrdinalIgnoreCase) ? band.Ft4Hz : band.Ft8Hz;

        /// <summary>The band a dial frequency is in, or null (within 500 kHz of a band's FT8 frequency).</summary>
        public static Band? BandOf(long hz) =>
            All.FirstOrDefault(b => Math.Abs(hz - b.Ft8Hz) < 500_000);
    }
}
