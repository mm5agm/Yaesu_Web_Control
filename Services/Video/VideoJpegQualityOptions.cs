namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Radio Display JPEG quality presets (panel selector).
    /// Low 50 / Medium 65 (default) / Max 85. The 85 cap is intentional.
    /// </summary>
    public static class VideoJpegQualityOptions
    {
        public const int Low = 50;
        public const int Medium = 65;
        public const int Max = 85;

        public static readonly int[] Allowed = { Low, Medium, Max };

        public static int Normalize(int quality) =>
            Allowed.Contains(quality) ? quality : Medium;
    }
}
