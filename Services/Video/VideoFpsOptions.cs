namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>Allowed Radio Display encode rates (panel selector).</summary>
    public static class VideoFpsOptions
    {
        public static readonly int[] Allowed = { 15, 30, 40, 60 };

        public static int Normalize(int fps) =>
            Allowed.Contains(fps) ? fps : 15;
    }
}
