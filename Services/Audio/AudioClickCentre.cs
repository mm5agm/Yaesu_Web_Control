using RadioWebControl.Core.Services.Cw;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>
    /// Click-to-centre on the radio audio waterfall: a signal heard at one audio
    /// tone, moved by retuning the dial so it is heard at another.
    ///
    /// <para>The sign is the only part that is easy to get wrong, so it comes
    /// from <see cref="CwZeroIn"/>, the same sum CW zero-in uses. In the
    /// upper-sideband modes audio = RF - dial, so moving the dial up moves the
    /// tone down; in the lower ones the tone rises with the dial.</para>
    ///
    /// <para>Only the modes whose audio is a straight frequency shift of the
    /// band. AM and FM have no such relationship, and PSK, C4FM and the DATA-FM
    /// modes are left out because nobody has checked what the radios do with
    /// their audio: a dial moved the wrong way is worse than one not moved.</para>
    /// </summary>
    public static class AudioClickCentre
    {
        /// <summary>The waterfall's top edge: nothing above this is a tone in it.</summary>
        public const double MaxAudioHz = 4000;

        /// <summary>Moves smaller than this are not worth a CAT write.</summary>
        public const long MinMoveHz = 5;

        /// <summary>
        /// True when the tone rises as the dial rises (LSB, CW-L, RTTY-L,
        /// DATA-L), false when it falls (USB, CW-U, RTTY-U, DATA-U), null when
        /// this mode is not one to move the dial in.
        /// </summary>
        public static bool? ToneRisesWithDial(string? mode) => mode?.Trim().ToUpperInvariant() switch
        {
            "LSB" or "CW-L" or "RTTY-L" or "RTTY" or "DATA-L" => true,
            "USB" or "CW-U" or "RTTY-U" or "DATA-U"           => false,
            _ => null,
        };

        /// <summary>True for the CW modes, where the target is the CW pitch.</summary>
        public static bool IsCw(string? mode) =>
            mode is not null && mode.Trim().StartsWith("CW", StringComparison.OrdinalIgnoreCase);

        /// <summary>The CW pitch in Hz from its KP code: 0-75 is 300-1050 Hz in 10 Hz steps.</summary>
        public static int CwPitchHz(int code) => 300 + 10 * Math.Clamp(code, 0, 75);

        /// <summary>
        /// Hz to add to the dial so a signal heard at <paramref name="fromHz"/>
        /// is heard at <paramref name="toHz"/>; null when either tone is outside
        /// the waterfall.
        /// </summary>
        public static long? DialMove(double fromHz, double toHz, bool toneRisesWithDial)
        {
            if (!InRange(fromHz) || !InRange(toHz)) return null;
            return CwZeroIn.ComputeOffsetWholeHz(fromHz, toHz, toneRisesWithDial, maxOffsetHz: MaxAudioHz);
        }

        private static bool InRange(double hz) => double.IsFinite(hz) && hz > 0 && hz <= MaxAudioHz;
    }
}
