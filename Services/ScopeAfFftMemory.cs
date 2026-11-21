namespace Yaesu_Web_Control.Services;

/// <summary>
/// What YWC knows of the radio's AF-FFT attenuator, oscilloscope attenuator
/// and oscilloscope time (SS P2=7) when the radio doesn't answer the read.
///
/// The three share one CAT field, so changing one means writing all three.
/// If the current values aren't known, the other two get written as 0 and
/// the operator's settings are lost (#396, on an FTDX10). So ScopeController
/// asks the radio first, then here: the ACC board's frames carry the settings
/// live, and failing that, the values YWC last read or wrote itself.
/// </summary>
public sealed class ScopeAfFftMemory
{
    /// <summary>An ACC frame older than this isn't current.</summary>
    public static readonly TimeSpan AccFresh = TimeSpan.FromSeconds(2);

    /// <summary>
    /// After YWC writes, ACC frames for this long may still show the old
    /// values: the radio takes a moment to change and redraw.
    /// </summary>
    public static readonly TimeSpan SettleAfterWrite = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private (char FftAtt, char OscAtt, char OscTime)? _acc;
    private DateTime _accAt;
    private readonly (char FftAtt, char OscAtt, char OscTime)?[] _known = new (char, char, char)?[2];
    private readonly DateTime[] _knownAt = new DateTime[2];

    /// <summary>
    /// Settings from an ACC frame. The radio has one set for both receivers,
    /// so they stand for Main and Sub.
    /// </summary>
    public void FromAcc(int fftAtt, int oscAtt, int oscTime, DateTime? nowUtc = null)
    {
        lock (_gate)
        {
            _acc = (Digit(fftAtt), Digit(oscAtt), Digit(oscTime));
            _accAt = nowUtc ?? DateTime.UtcNow;
        }
    }

    /// <summary>Values YWC read from the radio or wrote to it, for band '0' (Main) or '1' (Sub).</summary>
    public void Remember(char band, (char FftAtt, char OscAtt, char OscTime) values, DateTime? nowUtc = null)
    {
        lock (_gate)
        {
            _known[Index(band)] = values;
            _knownAt[Index(band)] = nowUtc ?? DateTime.UtcNow;
        }
    }

    /// <summary>
    /// The best guess at the radio's current settings, and where it came from,
    /// or null when YWC has nothing to go on.
    /// </summary>
    public ((char FftAtt, char OscAtt, char OscTime) Values, string Source)? Current(char band, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_gate)
        {
            var i = Index(band);
            var known = _known[i];
            bool accFresh = _acc is not null && now - _accAt <= AccFresh;
            // A fresh frame wins, unless YWC has written since it was taken.
            bool accAfterWrite = known is null || _accAt >= _knownAt[i] + SettleAfterWrite;
            if (accFresh && accAfterWrite) return (_acc!.Value, "the ACC board");
            if (known is not null) return (known.Value, "the values YWC last read or wrote");
            return null;
        }
    }

    private static int Index(char band) => band == '1' ? 1 : 0;

    private static char Digit(int v) => (char)('0' + Math.Clamp(v, 0, 9));
}
