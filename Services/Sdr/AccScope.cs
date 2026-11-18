// Yaesu Web Control — the FTdx101's own scope, from its ACC socket.
//
// The FTdx101 sends its Main and Sub scope rows out of the rear ACC socket as
// SPI. The ACC board (an ESP32-S3, github.com/mm5agm/Yaesu_ACC_Spectrum)
// passes them to the PC over USB, and the YaesuAccScope library decodes them.
// Each frame carries both receivers' rows AND where they sit: the VFO, the
// span and Center/Cursor/Fix, so nothing is polled over CAT to place them.
//
// This file is the pure half: device keys, which radios have the socket, and
// one receiver's row as a spectrum frame. AccScopeService owns the board's
// port. Unlike the FT-710's scope ("yaesu-scope:" keys) none of this runs in
// the SDR worker: one board feeds both panels, so the reader lives in the main
// process and the worker never sees an "acc-scope:" key.

using YaesuAccScope;

namespace Yaesu_Web_Control.Services.Sdr;

public static class AccScope
{
    /// <summary>Device key prefix for the ACC board in the SDR device dropdowns.</summary>
    public const string KeyPrefix = "acc-scope:";

    public const string MainKey = KeyPrefix + "main";
    public const string SubKey  = KeyPrefix + "sub";

    public const string MainLabel = "ACC scope, Main (FTdx101 ACC socket)";
    public const string SubLabel  = "ACC scope, Sub (FTdx101 ACC socket)";

    public static bool IsAccKey(string? key) =>
        key is not null && key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the Sub receiver's key, false for Main (and anything else).</summary>
    public static bool IsSub(string? key) =>
        string.Equals(key, SubKey, StringComparison.OrdinalIgnoreCase);

    public static string LabelFor(string key) => IsSub(key) ? SubLabel : MainLabel;

    /// <summary>
    /// Where a panel's spectrum comes from, for the views to say on it. With
    /// both scopes on screen an ACC panel looks just like an SDR's, so each
    /// one says which it is (Colin, 2026-11-13). "ACC scope" when the panel
    /// shows its own receiver's scope (Main on A, Sub on B), "ACC scope, Sub"
    /// or "ACC scope, Main" when it shows the other one; "FT-710 scope" for
    /// the FT-710's internal one; "SDR" for any other device; null when the
    /// panel has none.
    /// </summary>
    public static string? PanelSource(string? key, string target)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (Ft710ScopeFrame.IsScopeKey(key)) return "FT-710 scope";
        if (!IsAccKey(key)) return "SDR";
        bool own = IsSub(key) == string.Equals(target, "B", StringComparison.OrdinalIgnoreCase);
        return own ? "ACC scope" : IsSub(key) ? "ACC scope, Sub" : "ACC scope, Main";
    }

    /// <summary>
    /// Radios whose ACC socket sends the scope: the FTdx101MP and FTdx101D,
    /// the only ones the ACC board has been built for. The FTdx10 may put
    /// similar data on its ACC socket, but nobody has looked.
    /// </summary>
    public static bool ModelHasAcc(string? model) =>
        string.Equals(model, "FTdx101MP", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(model, "FTdx101D", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The audio scope (Main and Sub audio) comes from the ACC board, so the
    /// pages offer it only when a spectrum's source is the ACC scope, on a
    /// radio that has one (Colin, 2026-11-18). With an SDR picked for both
    /// spectrums it isn't shown, and the board's port is never opened for it.
    /// </summary>
    public static bool AudioScopeWanted(string? model, string? keyA, string? keyB) =>
        ModelHasAcc(model) && (IsAccKey(keyA) || IsAccKey(keyB));

    public const string AudioScopeNotPicked =
        "The audio scope needs the ACC scope picked as a spectrum source in Settings.";

    /// <summary>
    /// The row's points that fall inside the span. The radio sends 850 points
    /// with about 30 beyond each edge of the span; the middle 791, centred on
    /// point 425, are the span itself (ReceiverScope.SpanPoints).
    /// </summary>
    public const int FirstSpanPoint = ReceiverScope.CentrePoint - ReceiverScope.SpanPoints / 2;   // 30

    /// <summary>
    /// dB per step of the level byte: about 0.42, measured 2026-11-14 on an
    /// FTdx101MP by switching the radio's attenuator (0.46 from its scope
    /// LEVEL, within the error). See docs/comparison.md in the
    /// Yaesu_ACC_Spectrum repository. The panel scales vertically for itself,
    /// so this only sets how many dB the trace's steps are labelled as.
    /// </summary>
    public const float DbPerStep = 0.42f;

    /// <summary>The level a byte of 0 (nothing at all) maps to.</summary>
    public const float DbAtZero = -150f;

    public static float LevelToDb(byte level) => DbAtZero + level * DbPerStep;

    /// <summary>A refined level, which falls between the radio's steps, in dB.</summary>
    public static float LevelToDb(float level) => DbAtZero + level * DbPerStep;

    /// <summary>
    /// One receiver's row as a frame for the spectrum panel: the 791 points
    /// inside the span, low to high frequency, in dB; centred where the radio
    /// centres its scope (the VFO in Center, the window's middle in Cursor
    /// and Fix); exactly the radio's span wide. Null when the frame doesn't
    /// make sense (ReceiverScope.IsValid), so a row is never drawn at a
    /// guessed frequency.
    ///
    /// The levels are the refined trace when one is given (ScopeRefiner's 850
    /// points for this row), else the raw row. The panels draw the refined
    /// one: each raw row is a separate snapshot with a rough floor, where the
    /// SDRs' frames come already averaged by the worker (SpectrumProcessor).
    /// </summary>
    public static SpectrumFrameMsg? ToSpectrum(ReceiverScope scope, ulong sequence, float[]? refined = null)
    {
        if (!scope.IsValid) return null;
        if (refined is not null && refined.Length != AccFrame.Points)
            throw new ArgumentException($"A refined trace has {AccFrame.Points} points, not {refined.Length}.", nameof(refined));

        var bins = new float[ReceiverScope.SpanPoints];
        if (refined is not null)
        {
            for (int i = 0; i < bins.Length; i++)
                bins[i] = LevelToDb(refined[FirstSpanPoint + i]);
        }
        else
        {
            var levels = scope.Levels;
            for (int i = 0; i < bins.Length; i++)
                bins[i] = LevelToDb(levels[FirstSpanPoint + i]);
        }

        return new SpectrumFrameMsg(sequence, scope.CentreHz, scope.SpanHz, bins);
    }

    /// <summary>
    /// The Refine slider's steps as ScopeRefiner weights: step 0 is the raw
    /// rows, each step after is smoother and slower to follow. At 25 frames a
    /// second a weight w takes about 1/(25·w) s to follow a change and smooths
    /// the floor about as much as averaging 2/w − 1 frames. Step 3 is the
    /// library's default, measured level with an RSP on 20m (ScopeRefiner).
    /// </summary>
    public static readonly double[] RefineWeights = { 1.0, 0.6, 0.45, ScopeRefiner.DefaultWeight, 0.2, 0.12 };

    public const int DefaultRefineLevel = 3;
    public static int MaxRefineLevel => RefineWeights.Length - 1;

    public static bool IsRefineLevel(int level) => level >= 0 && level <= MaxRefineLevel;

    /// <summary>The weight for a slider step; a step out of range (an old or hand-edited file) gets the default.</summary>
    public static double RefineWeight(int level) =>
        RefineWeights[IsRefineLevel(level) ? level : DefaultRefineLevel];

    /// <summary>Shown when frames arrive but don't say where the row sits.</summary>
    public const string UnplacedProblem =
        "Frames are coming from the ACC board, but they don't say a VFO and span that make sense, " +
        "so the scope isn't drawn rather than drawn in the wrong place.";

    /// <summary>
    /// What a panel shows when no frame has come for a while: the status for
    /// the panel and the reason, from what the board's reader is doing.
    /// </summary>
    public static (string Status, string? Detail) WhenNoFrames(AccScopeState state) => state.Status switch
    {
        AccScopeStatus.Reading => ("disconnected",
            $"The ACC board on {state.Port} is connected, but nothing is coming from the radio. Check the radio " +
            "is switched on, that ACC pins 11 and 12 are linked so it streams, and that ground (ACC pin 2) is " +
            "connected. ACC Spectrum Viewer's wiring check shows which wire is missing."),
        AccScopeStatus.NoBoard or AccScopeStatus.Lost => ("disconnected", state.Problem),
        _ => ("connecting", null),
    };

    /// <summary>
    /// The SS span digit for a span in Hz (0 = 1 kHz … 9 = 1 MHz), or null
    /// when the radio's scope has no such span (the SDR-only 15 kHz, 30 kHz, 40 kHz
    /// and 2 MHz).
    /// </summary>
    public static char? SpanDigit(double spanHz)
    {
        for (int i = 0; i < ReceiverScope.Spans.Length; i++)
            if (ReceiverScope.Spans[i] == spanHz) return (char)('0' + i);
        return null;
    }

    /// <summary>
    /// What a panel's label says while the radio's scope LEVEL is cutting the
    /// trace off: a short word for the label, and the full reason for its
    /// tooltip and the log. Null when the level is fine.
    /// </summary>
    public static (string Short, string Detail)? LevelWarning(AccLevelProblem problem) => problem switch
    {
        AccLevelProblem.TooLow => ("LEVEL too low",
            "The radio's scope Level is too low for the ACC scope: the floor is sitting on the bottom of the " +
            "radio's scale, so weak signals there are lost. Raise Level on the radio, or in YWC's Radio Scope " +
            "panel (§5.20), until this goes. +20 or more is usually right. The radio keeps Level for each band, so " +
            "set it on every band you use."),
        AccLevelProblem.TooHigh => ("LEVEL too high",
            "The radio's scope Level is too high for the ACC scope: strong signals are reaching the top of the " +
            "radio's scale and being cut off. Lower Level on the radio, or in YWC's Radio Scope panel (§5.20)."),
        _ => null,
    };
}

public enum AccLevelProblem { None, TooLow, TooHigh }

/// <summary>
/// Watches one receiver's raw ACC rows for a scope LEVEL that cuts the trace
/// off. The rows are the radio's display data, after its LEVEL: too low and
/// the floor sits on step 0, where weak signals vanish; too high and strong
/// signals sit on step 255. Measured 2026-11-14 on an FTdx101MP (see
/// docs/comparison.md in Yaesu_ACC_Spectrum): at LEVEL 0 at least one floor
/// point in twenty was on step 0, from +10 up none were.
/// </summary>
/// <remarks>
/// Judged over two seconds of rows (50 at 25 a second) so a single odd row
/// doesn't flicker the warning, and with a lower bar for clearing than for
/// raising it so a level right on the edge doesn't flicker either.
/// </remarks>
public sealed class AccLevelWatch
{
    public const int    WindowRows       = 50;
    /// <summary>
    /// The radio's floor clips on step 1, not 0: at LEVEL -10 on 20 m, 36 % of
    /// points sat on step 1 while step 0 was as rare as steps 2-7.
    /// </summary>
    public const byte   BottomStep       = 1;
    /// <summary>
    /// Raise "too low" when this share of the span's points is on or below
    /// BottomStep. Measured on an FTdx101MP: LEVEL +05 gave 10.7 % on 160 m,
    /// 15.1 % on 10 m and 1.5 % on 20 m; LEVEL 0 on 20 m gave 5.3 %.
    /// </summary>
    public const double LowRaiseShare    = 0.05;
    /// <summary>
    /// Clear below this. At LEVEL +23.5 the 1 and 2 kHz spans on 10 m reached
    /// 2.2 % in a window (narrow spans have a lower floor), so 1 % could latch.
    /// </summary>
    public const double LowClearShare    = 0.025;
    /// <summary>Raise "too high" when this share of rows has a point on step 255.</summary>
    public const double HighRaiseShare   = 0.2;
    public const double HighClearShare   = 0.05;

    private int  _rows;
    private long _bottom;
    private int  _topRows;

    public AccLevelProblem Problem { get; private set; }

    /// <summary>
    /// Adds one raw row (all 850 points; only those inside the span count).
    /// Returns true when Problem has just changed.
    /// </summary>
    public bool Add(ReadOnlySpan<byte> row)
    {
        var span = row.Slice(AccScope.FirstSpanPoint, ReceiverScope.SpanPoints);
        bool top = false;
        foreach (byte b in span)
        {
            if (b <= BottomStep) _bottom++;
            else if (b == byte.MaxValue) top = true;
        }
        if (top) _topRows++;
        if (++_rows < WindowRows) return false;

        double bottomShare = (double)_bottom / ((long)WindowRows * ReceiverScope.SpanPoints);
        double topShare    = (double)_topRows / WindowRows;
        _rows = 0; _bottom = 0; _topRows = 0;

        var next = Problem switch
        {
            AccLevelProblem.TooLow  when bottomShare >= LowClearShare  => AccLevelProblem.TooLow,
            AccLevelProblem.TooHigh when topShare    >= HighClearShare => AccLevelProblem.TooHigh,
            _ when bottomShare >= LowRaiseShare => AccLevelProblem.TooLow,
            _ when topShare    >= HighRaiseShare => AccLevelProblem.TooHigh,
            _ => AccLevelProblem.None,
        };
        if (next == Problem) return false;
        Problem = next;
        return true;
    }
}
