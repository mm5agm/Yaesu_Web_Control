namespace Yaesu_Web_Control.Services;

/// <summary>
/// What YWC knows of the radio's AF-FFT attenuator, oscilloscope attenuator
/// and oscilloscope time (SS P2=7) when it can't read them from the radio.
///
/// The three share one CAT field, so changing one means writing all three.
/// If the current values aren't known, the other two get written as 0 and
/// the operator's settings are lost (#396, on an FTDX10). So ScopeController
/// asks the radio first, then here: the ACC board's frames carry the settings
/// live, and failing that, the values YWC last read or wrote itself.
///
/// The FTDX10 never answers an SS read, so without an ACC board the values
/// YWC wrote are all it has. They're saved to disk, so the first press after
/// a restart uses the last values rather than 0. They can still be out of
/// date if someone changed them on the radio while YWC wasn't running.
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

    /// <summary>Where the app saves the values between runs.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MM5AGM", "Yaesu Web Control", "scope-af-fft.txt");

    private readonly object _gate = new();
    private readonly string? _path;
    private (char FftAtt, char OscAtt, char OscTime)? _acc;
    private DateTime _accAt;
    private readonly (char FftAtt, char OscAtt, char OscTime)?[] _known = new (char, char, char)?[2];
    private readonly DateTime[] _knownAt = new DateTime[2];
    private readonly bool[] _fromDisk = new bool[2];

    /// <summary>
    /// <paramref name="path"/> is the file the values are kept in between
    /// runs; null keeps them in memory only.
    /// </summary>
    public ScopeAfFftMemory(string? path = null)
    {
        _path = path;
        Load();
    }

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
        bool changed;
        lock (_gate)
        {
            var i = Index(band);
            changed = _known[i] != values;
            _known[i] = values;
            _knownAt[i] = nowUtc ?? DateTime.UtcNow;
            _fromDisk[i] = false;
        }
        if (changed) Save();
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
            if (known is not null)
                return (known.Value, _fromDisk[i]
                    ? "the values YWC last set, saved from an earlier run"
                    : "the values YWC last read or wrote");
            return null;
        }
    }

    // The file is one line per band: "0 213" is Main with FFT ATT 2, OSC ATT
    // 1 and OSC time 3. Anything unreadable is ignored.
    private void Load()
    {
        if (_path is null) return;
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadAllLines(_path))
            {
                var t = line.Trim();
                if (t.Length != 5 || t[0] is not ('0' or '1') || t[1] != ' ') continue;
                if (!char.IsAsciiDigit(t[2]) || !char.IsAsciiDigit(t[3]) || !char.IsAsciiDigit(t[4])) continue;
                var i = Index(t[0]);
                _known[i] = (t[2], t[3], t[4]);
                _knownAt[i] = DateTime.MinValue;   // older than any ACC frame
                _fromDisk[i] = true;
            }
        }
        catch (Exception) { /* a missing or damaged file just means nothing saved */ }
    }

    private void Save()
    {
        if (_path is null) return;
        string text;
        lock (_gate)
        {
            var lines = new List<string>();
            for (int i = 0; i < 2; i++)
                if (_known[i] is { } v) lines.Add($"{i} {v.FftAtt}{v.OscAtt}{v.OscTime}");
            text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            lock (_gate) File.WriteAllText(_path, text);
        }
        catch (Exception) { /* not being able to save only loses the memory at the next restart */ }
    }

    private static int Index(char band) => band == '1' ? 1 : 0;

    private static char Digit(int v) => (char)('0' + Math.Clamp(v, 0, 9));
}
