namespace RadioWebControl.Core.Services.Cw
{
    /// <summary>
    /// The arithmetic behind the CW reader's Auto button: once the decoder has
    /// a readable station locked, how far to move the dial and how narrow a
    /// filter it will stand.
    ///
    /// Pure and radio-agnostic. Which CAT command moves the VFO and which
    /// filter code means which width are the application's business; the
    /// numbers are the same on any radio.
    /// </summary>
    public static class CwAutoTune
    {
        /// <summary>How many agreeing readings Auto wants before it acts.</summary>
        public const int SamplesWanted = 5;

        /// <summary>
        /// Below this the zero-in reading is tracker jitter, not mistuning - the
        /// tone wanders about that much on a perfectly centred signal - so the
        /// dial is left alone rather than nudged back and forth.
        /// </summary>
        public const double DeadbandHz = 10.0;

        /// <summary>
        /// One offset from several readings: the median, so one bad frame -
        /// the tracker briefly on the neighbour, or on a key click - cannot
        /// decide where the dial goes. Null for no readings.
        /// </summary>
        public static double? SettledOffsetHz(IReadOnlyList<double> offsetsHz)
        {
            if (offsetsHz is null || offsetsHz.Count == 0) return null;
            var sorted = offsetsHz.Where(o => !double.IsNaN(o)).OrderBy(o => o).ToArray();
            if (sorted.Length == 0) return null;
            int mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        /// <summary>
        /// Widest the tones of a "steady" station may spread, coarse stage.
        /// The tracker wanders 20 or 30 Hz on a single keyed station; more than
        /// this means it is hopping between stations, and moving onto the
        /// average of two of them would put the dial on neither.
        /// </summary>
        public const double SteadySpreadHz = 40.0;

        /// <summary>
        /// The coarse filter: wide enough that a station moved on a tracker
        /// reading still lands inside it, narrow enough to shut out most of a
        /// crowded 2 kHz passband so the decoder can get a readable lock.
        /// </summary>
        public const int CoarseWidthHz = 500;

        /// <summary>
        /// The tone a run of tracker readings agree on, or null if they do not.
        ///
        /// This is the coarse stage, for a station the decoder can hear but
        /// cannot yet read - typically several hundred hertz off pitch in a wide
        /// filter, with neighbours either side. It needs no readable lock, only
        /// a tracker that keeps coming back to the same place.
        /// </summary>
        public static double? SteadyToneHz(IReadOnlyList<double> tonesHz, double maxSpreadHz = SteadySpreadHz)
        {
            if (tonesHz is null || tonesHz.Count == 0) return null;
            var ok = tonesHz.Where(t => !double.IsNaN(t) && t > 0).ToArray();
            if (ok.Length < tonesHz.Count) return null;
            if (ok.Max() - ok.Min() > maxSpreadHz) return null;
            return SettledOffsetHz(ok);
        }

        /// <summary>
        /// Hz to move the dial, rounded to whole Hz, or null to leave it where
        /// it is because the station is already on pitch.
        /// </summary>
        public static long? DialMoveHz(double? settledOffsetHz)
        {
            if (settledOffsetHz is not { } o || double.IsNaN(o)) return null;
            if (Math.Abs(o) < DeadbandHz) return null;
            return (long)Math.Round(o);
        }

        /// <summary>
        /// The filter width to read a station keying at <paramref name="wpm"/>.
        ///
        /// Ten hertz per word per minute. A dot at W wpm lasts 1.2/W seconds,
        /// so the keying sidebands that carry the element edges reach about
        /// 1.25 W Hz either side of the tone by the third harmonic - 2.5 W
        /// across. Ten times leaves room for that, for the zero-in being a few
        /// tens of hertz out, and for drift, while still shutting out the
        /// station next door. At 20 wpm that asks for 200 Hz and gets the
        /// floor; at 40 it asks for 400.
        ///
        /// The floor is the operator's own Reader Mode width, so Auto never
        /// goes narrower than they have said they want to read at. The cap
        /// stops a speed estimate railing high - which it does on chatter -
        /// from opening the filter back up to SSB width.
        /// </summary>
        /// <param name="wpm">Measured speed; zero or less means unknown.</param>
        /// <param name="floorHz">Narrowest width to choose.</param>
        /// <param name="capHz">Widest width to choose.</param>
        public static int WidthForSpeedHz(double wpm, int floorHz, int capHz = 500)
        {
            if (capHz < floorHz) capHz = floorHz;
            if (double.IsNaN(wpm) || wpm <= 0) return floorHz;
            int wanted = (int)Math.Round(wpm * 10.0);
            return Math.Clamp(wanted, floorHz, capHz);
        }
    }
}
