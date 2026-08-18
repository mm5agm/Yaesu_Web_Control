namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Radio Display JPEG quality presets (panel selector).
        /// Low 40 / Medium 65 / Max 85 (default). The 85 cap is intentional.
    /// Unknown values snap to the nearest preset so a hand-tuned 40 stays Low
    /// instead of jumping to Medium.
    /// </summary>
    public static class VideoJpegQualityOptions
    {
        public const int Low = 40;
        public const int Medium = 65;
        public const int Max = 85;

        public static readonly int[] Allowed = { Low, Medium, Max };

        public static int Normalize(int quality)
        {
            if (Allowed.Contains(quality))
                return quality;

            var best = Medium;
            var bestDist = int.MaxValue;
            foreach (var allowed in Allowed)
            {
                var dist = Math.Abs(allowed - quality);
                if (dist < bestDist)
                {
                    best = allowed;
                    bestDist = dist;
                }
            }

            return best;
        }
    }
}
