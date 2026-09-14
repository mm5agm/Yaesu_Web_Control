namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Radio Display capture size selector. "" means auto — let
    /// <see cref="VideoCapturePinRank"/> rank the pins, which is what every
    /// release before this one did and is still the right answer for almost
    /// everyone. An explicit "WxH" pins the capture to that MJPEG mode.
    /// </summary>
    /// <remarks>
    /// Bigger is not better here. Measured on a VIXLW HDMI dongle against an
    /// FTDX101MP set to its 800x600 PIXEL mode (issue #132): the dongle's
    /// larger modes are a pure upscale of the same 800x600 source — an FFT of
    /// the captured frame is flat to Nyquist at 800x600 but rolls off ~27 dB
    /// at 1920x1080 — so they carry no extra detail, look measurably softer,
    /// and cost roughly twice the bytes. The selector exists because dongles
    /// vary and some radios drive a different panel size, not because a wider
    /// mode is an upgrade.
    /// </remarks>
    internal static class VideoSizeOptions
    {
        /// <summary>Auto — rank the pins as before.</summary>
        public const string Auto = "";

        public static string Format(int width, int height) => $"{width}x{height}";

        public static bool TryParse(string? spec, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrWhiteSpace(spec))
                return false;

            var parts = spec.Trim().Split('x', 'X');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0].Trim(), out var w) ||
                !int.TryParse(parts[1].Trim(), out var h) ||
                w < 2 || h < 2 || w > 8192 || h > 8192)
            {
                return false;
            }

            width = w;
            height = h;
            return true;
        }

        /// <summary>
        /// Distinct MJPEG sizes the device advertises, smallest first. Only
        /// compressed pins: an uncompressed mode at the same size is the USB2
        /// ~22 fps trap and must never be offered as a choice.
        /// </summary>
        public static string[] FromPins(IEnumerable<VideoCapturePinRank.Pin> pins)
        {
            var seen = new HashSet<(int W, int H)>();
            var list = new List<(int W, int H)>();
            foreach (var p in pins)
            {
                if (!p.Jpeg || p.Width < 2 || p.Height < 2)
                    continue;
                if (seen.Add((p.Width, p.Height)))
                    list.Add((p.Width, p.Height));
            }

            return list
                .OrderBy(s => (long)s.W * s.H)
                .ThenBy(s => s.W)
                .Select(s => Format(s.W, s.H))
                .ToArray();
        }

        /// <summary>
        /// The stored value if it parses and the device still offers it,
        /// otherwise <see cref="Auto"/>. Falling back to auto rather than
        /// pinning an impossible mode is deliberate: a dongle swap must not
        /// leave the panel unable to open.
        /// </summary>
        public static string Normalize(string? requested, IReadOnlyList<string>? available)
        {
            if (!TryParse(requested, out var w, out var h))
                return Auto;

            var value = Format(w, h);
            if (available is { Count: > 0 } &&
                !available.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return Auto;
            }

            return value;
        }

        /// <summary>
        /// Encode width to use with a chosen capture size: the capture width
        /// itself, so an explicitly chosen mode is not silently downscaled by
        /// <c>VideoMaxWidth</c> on the way out. Auto keeps the old behaviour.
        /// </summary>
        public static int EncodeWidthFor(string? requested, int maxWidth) =>
            TryParse(requested, out var w, out _) ? w : maxWidth;
    }
}
