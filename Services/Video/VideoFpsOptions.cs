namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Radio Display FPS selector: 15 / 30 / 60. Device max hides presets the
    /// capture pin cannot do. The chosen value is a target — USB, encode, and
    /// CPU can still deliver less.
    /// </summary>
    public static class VideoFpsOptions
    {
        public static readonly int[] Allowed = { 15, 30, 60 };

        public static int[] UniqueSorted(IEnumerable<double> raw)
        {
            var set = new SortedSet<int>();
            foreach (var d in raw)
            {
                if (double.IsNaN(d) || double.IsInfinity(d) || d < 1)
                    continue;
                var n = (int)Math.Round(d, MidpointRounding.AwayFromZero);
                if (n is >= 1 and <= 120)
                    set.Add(n);
            }

            return set.ToArray();
        }

        /// <summary>
        /// Presets that fall inside a driver-reported min/max interval range.
        /// Used only when collecting the device's maximum advertised rate.
        /// </summary>
        public static void AddPresetsInRange(ICollection<double> dest, double minFps, double maxFps)
        {
            if (maxFps < minFps)
                (minFps, maxFps) = (maxFps, minFps);
            foreach (var p in Allowed)
            {
                if (p >= minFps - 0.5 && p <= maxFps + 0.5)
                    dest.Add(p);
            }
        }

        public static int[] ForDevice(int[]? advertised)
        {
            var max = Max(advertised);
            if (max <= 0)
                return Allowed;

            var capped = Allowed.Where(a => a <= max).ToArray();
            return capped.Length > 0 ? capped : [Allowed[0]];
        }

        public static int Normalize(int fps, int[]? advertised = null)
        {
            var allowed = ForDevice(advertised);
            if (allowed.Contains(fps))
                return fps;

            var best = allowed[0];
            var bestDist = int.MaxValue;
            foreach (var a in allowed)
            {
                var dist = Math.Abs(a - fps);
                if (dist < bestDist)
                {
                    best = a;
                    bestDist = dist;
                }
            }

            return best;
        }

        public static int Max(int[]? advertised) =>
            advertised is { Length: > 0 } ? advertised[^1] : 0;
    }
}
