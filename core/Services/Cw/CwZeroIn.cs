namespace RadioWebControl.Core.Services.Cw
{
    /// <summary>
    /// Zero-in arithmetic: given the tone we are actually hearing and the pitch
    /// the operator wants to hear, how far does the VFO have to move?
    ///
    /// Pure and radio-agnostic on purpose. The FTdx101 does not need this, since
    /// it has a ZI CAT command and nudges its own VFO, but the IC-7300 MkII has
    /// no equivalent and Icom Web Control has to work the offset out and then set
    /// the frequency itself. Keeping the sum here means both applications agree
    /// about the sign, which is the only part of this that is easy to get wrong.
    ///
    /// Sign convention. In CW-U the receiver puts the audio above the carrier,
    /// so audio = RF - VFO, and moving the VFO up lowers the tone. In CW-L it is
    /// the other way round. Hence lowerSideband.
    /// </summary>
    public static class CwZeroIn
    {
        /// <summary>
        /// Hz to add to the current VFO frequency to bring measuredToneHz onto
        /// targetPitchHz. Null when the measurement is not trustworthy enough to
        /// act on, which the caller should treat as "do nothing" rather than
        /// "move by zero": the two look identical on the radio but only one of
        /// them should light a button up.
        /// </summary>
        /// <param name="measuredToneHz">Tone the detector is currently tracking.</param>
        /// <param name="targetPitchHz">The operator's configured CW pitch.</param>
        /// <param name="lowerSideband">True for CW-L (or CW-R, on radios that call it that).</param>
        /// <param name="maxOffsetHz">
        /// Refuse to move further than this in one go. A large offset almost
        /// always means the detector locked onto the wrong signal, and a reader
        /// that yanks the VFO half a kilohertz because of one bad FFT frame is
        /// worse than one that does nothing.
        /// </param>
        /// <param name="confidence">0..1 from the tone detector.</param>
        /// <param name="minConfidence">Confidence floor, 0..1.</param>
        public static double? ComputeOffsetHz(
            double measuredToneHz,
            double targetPitchHz,
            bool   lowerSideband = false,
            double maxOffsetHz   = 500.0,
            double confidence    = 1.0,
            double minConfidence = 0.5)
        {
            if (double.IsNaN(measuredToneHz) || measuredToneHz <= 0) return null;
            if (double.IsNaN(targetPitchHz)  || targetPitchHz  <= 0) return null;
            if (confidence < minConfidence) return null;

            double delta = measuredToneHz - targetPitchHz;
            if (lowerSideband) delta = -delta;

            if (Math.Abs(delta) > maxOffsetHz) return null;
            return delta;
        }

        /// <summary>
        /// The same answer rounded to whole Hz, which is the resolution every CAT
        /// and CI-V set-frequency command actually takes.
        /// </summary>
        public static long? ComputeOffsetWholeHz(
            double measuredToneHz,
            double targetPitchHz,
            bool   lowerSideband = false,
            double maxOffsetHz   = 500.0,
            double confidence    = 1.0,
            double minConfidence = 0.5)
        {
            var delta = ComputeOffsetHz(measuredToneHz, targetPitchHz, lowerSideband,
                                        maxOffsetHz, confidence, minConfidence);
            return delta is null ? null : (long)Math.Round(delta.Value);
        }
    }
}
