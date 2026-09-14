namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// #132. Catches a capture device that has started packing several frame
    /// periods into one picture. Such a frame is entirely well formed - right
    /// dimensions, single image, nothing after the EOI - so neither the merged
    /// sample check nor the frame-rate floor can see it. Only its size gives it
    /// away, because four quadrants of screen carry roughly twice the detail of
    /// one.
    ///
    /// Measured on the one occurrence I have caught: 330 KB tiled against 143 KB
    /// healthy, a ratio of 2.3, while the delivered rate halved from 15 to 7.5.
    /// The rate alone was no use as a signal - the collapse detector trips below
    /// a quarter of target and this sat at half - so size it is.
    ///
    /// Compared against a rolling median rather than a byte count, so the same
    /// threshold holds across every capture size, JPEG quality and backend. A
    /// median, not a mean, so a run of tiled frames cannot drag the baseline up
    /// behind itself and hide the fault.
    /// </summary>
    public sealed class VideoTiledFrameDetector
    {
        private readonly int[] _sizes;
        private readonly double _ratio;
        private readonly int _streakNeeded;
        private int _count;
        private int _next;
        private int _streak;

        public VideoTiledFrameDetector(int window = 30, double ratio = 2.0, int streakNeeded = 3)
        {
            if (window < 1)
                throw new ArgumentOutOfRangeException(nameof(window));
            if (streakNeeded < 1)
                throw new ArgumentOutOfRangeException(nameof(streakNeeded));

            _sizes = new int[window];
            _ratio = ratio;
            _streakNeeded = streakNeeded;
        }

        /// <summary>True once enough frames have been seen to trust the median.</summary>
        public bool Ready => _count >= _sizes.Length;

        /// <summary>
        /// Median frame size over the window, or 0 before <see cref="Ready"/>.
        /// Reported so the log line can show what the outlier was measured
        /// against - the ratio is the whole argument, and a bare byte count
        /// would not let anyone check it afterwards.
        /// </summary>
        public int Median
        {
            get
            {
                if (!Ready)
                    return 0;
                var copy = (int[])_sizes.Clone();
                Array.Sort(copy);
                return copy[copy.Length / 2];
            }
        }

        /// <summary>
        /// Records one published frame. Returns true when this frame completes a
        /// run of oversize ones long enough to act on; the run resets when it
        /// does, so a caller that ignores the result is told again rather than
        /// held at true forever. A single large frame never trips it - ordinary
        /// screen content swings enough for that on its own.
        /// </summary>
        public bool Observe(int frameBytes)
        {
            var median = Median;
            if (median > 0 && frameBytes > median * _ratio)
                _streak++;
            else
                _streak = 0;

            _sizes[_next] = frameBytes;
            _next = (_next + 1) % _sizes.Length;
            if (_count < _sizes.Length)
                _count++;

            if (_streak < _streakNeeded)
                return false;

            _streak = 0;
            return true;
        }
    }
}
