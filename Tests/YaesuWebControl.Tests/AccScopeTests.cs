using Yaesu_Web_Control.Services.Sdr;
using YaesuAccScope;

namespace YaesuWebControl.Tests;

/// <summary>
/// The FTdx101's own scope from its ACC socket, as the SDR panels get it:
/// device keys, which radios are offered it, one receiver's row as a spectrum
/// frame, and the span buttons' SS digits. The frames are built here byte by
/// byte at the offsets YaesuAccScope reads (docs/protocol.md in
/// mm5agm/Yaesu_ACC_Spectrum), so these tests check YWC's use of the library,
/// not the library itself, which has its own tests against real captures.
/// </summary>
public class AccScopeTests
{
    // Byte offsets of Main's and Sub's fields in a frame (AccFrame.cs).
    private const int MainVfoAt = 3068, MainLeftAt = 3076, MainSpanAt = 3062;
    private const int SubVfoAt  = 3094, SubLeftAt  = 3098, SubSpanAt  = 3084;

    private static void PutUInt32(byte[] b, int at, long v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    /// <summary>A frame with a level ramp (or one level) on Main and a flat row on Sub.</summary>
    private static AccFrame Frame(
        long mainVfo = 14_074_000, int mainSpanIndex = 4, ScopePlacement mainPlacement = ScopePlacement.Center, long mainLeft = 0,
        long subVfo  = 7_074_000,  int subSpanIndex  = 5, ScopePlacement subPlacement  = ScopePlacement.Center,
        byte? mainLevel = null)
    {
        var raw = new byte[4096];
        for (int i = 0; i < AccFrame.Points; i++)
        {
            raw[i] = (byte)(255 - (mainLevel ?? i % 256)); // Main: level i % 256 (or flat), sent inverted
            raw[AccFrame.Points + i] = 255 - 60;          // Sub: level 60 everywhere
        }
        PutUInt32(raw, MainVfoAt, mainVfo);
        PutUInt32(raw, MainLeftAt, mainLeft);
        raw[MainSpanAt] = (byte)(((int)mainPlacement << 6) | mainSpanIndex);
        PutUInt32(raw, SubVfoAt, subVfo);
        PutUInt32(raw, SubLeftAt, 0);
        raw[SubSpanAt] = (byte)(((int)subPlacement << 6) | subSpanIndex);
        raw[2917] = 3;   // waterfall, not 3DSS
        raw[2918] = 3;
        return AccFrame.Parse(raw);
    }

    [Theory]
    [InlineData("acc-scope:main", true, false)]
    [InlineData("acc-scope:sub", true, true)]
    [InlineData("ACC-SCOPE:SUB", true, true)]
    [InlineData("yaesu-scope:ft710", false, false)]
    [InlineData("sdrplay:hw6-2405242660", false, false)]
    [InlineData(null, false, false)]
    public void Keys(string? key, bool isAcc, bool isSub)
    {
        Assert.Equal(isAcc, AccScope.IsAccKey(key));
        Assert.Equal(isSub, AccScope.IsSub(key));
    }

    [Theory]
    [InlineData("acc-scope:main", "A", "ACC scope")]
    [InlineData("acc-scope:sub", "B", "ACC scope")]
    [InlineData("acc-scope:sub", "A", "ACC scope, Sub")]
    [InlineData("acc-scope:main", "B", "ACC scope, Main")]
    [InlineData("sdrplay:hw6-2405242660", "A", "SDR")]
    [InlineData("yaesu-scope:ft710", "A", "FT-710 scope")]
    [InlineData("", "A", null)]
    [InlineData(null, "B", null)]
    public void Each_panel_says_where_its_spectrum_comes_from(string? key, string target, string? expected)
        => Assert.Equal(expected, AccScope.PanelSource(key, target));

    [Fact]
    public void Acc_keys_are_not_the_ft710_scope_keys_the_worker_takes()
    {
        // The worker hands every "yaesu-scope:" key to the FT4222 code, so an
        // ACC key must never look like one.
        Assert.False(Ft710ScopeFrame.IsScopeKey(AccScope.MainKey));
        Assert.False(Ft710ScopeFrame.IsScopeKey(AccScope.SubKey));
    }

    [Theory]
    [InlineData("FTdx101MP", true)]
    [InlineData("FTdx101D", true)]
    [InlineData("FT-710", false)]
    [InlineData("FTdx10", false)]
    [InlineData("FTDX5000MP", false)]
    [InlineData(null, false)]
    public void Only_the_ftdx101_has_it(string? model, bool expected) =>
        Assert.Equal(expected, AccScope.ModelHasAcc(model));

    [Fact]
    public void Main_row_is_the_span_points_in_rf_order_centred_on_the_vfo()
    {
        var f = AccScope.ToSpectrum(Frame().Main, 7)!;

        Assert.Equal(7UL, f.Sequence);
        Assert.Equal(14_074_000, f.CentreHz);
        Assert.Equal(20_000, f.SpanHz);
        Assert.Equal(ReceiverScope.SpanPoints, f.Bins.Length);
        // Point 30 of the ramp is the first bin, 820 the last: low frequency first.
        Assert.Equal(AccScope.LevelToDb(30), f.Bins[0]);
        Assert.Equal(AccScope.LevelToDb(820 % 256), f.Bins[^1]);
        // Point 425, the VFO in Center, is the middle bin.
        Assert.Equal(AccScope.LevelToDb(425 % 256), f.Bins[ReceiverScope.SpanPoints / 2]);
    }

    [Fact]
    public void A_refined_trace_is_drawn_in_place_of_the_raw_row()
    {
        var scope = Frame().Main;
        var refined = new float[AccFrame.Points];
        for (int i = 0; i < refined.Length; i++) refined[i] = 100.5f;

        var f = AccScope.ToSpectrum(scope, 1, refined)!;

        Assert.Equal(ReceiverScope.SpanPoints, f.Bins.Length);
        Assert.All(f.Bins, b => Assert.Equal(AccScope.LevelToDb(100.5f), b));
        Assert.Equal(14_074_000, f.CentreHz);
    }

    [Fact]
    public void The_refiner_gives_the_panel_a_steadier_floor()
    {
        // Main's level at every point flips between 80 and 120 each frame, as a
        // rough floor does. Raw, a bin jumps 20 dB a frame; refined, far less.
        var refiner = new ScopeRefiner();
        float[]? lastRaw = null, lastRefined = null;
        float rawJump = 0, refinedJump = 0;
        for (int n = 0; n < 40; n++)
        {
            var scope = Frame(mainLevel: n % 2 == 0 ? (byte)80 : (byte)120).Main;
            var raw = AccScope.ToSpectrum(scope, (ulong)n)!.Bins;
            var refined = AccScope.ToSpectrum(scope, (ulong)n, refiner.Add(scope))!.Bins;
            if (lastRaw is not null)
            {
                rawJump = Math.Abs(raw[400] - lastRaw[400]);
                refinedJump = Math.Abs(refined[400] - lastRefined![400]);
            }
            lastRaw = raw;
            lastRefined = refined;
        }
        Assert.Equal(20f, rawJump);
        Assert.True(refinedJump < rawJump / 2, $"refined moved {refinedJump} dB a frame");
    }

    [Fact]
    public void Refine_step_0_is_raw_and_each_step_after_is_smoother()
    {
        Assert.Equal(1.0, AccScope.RefineWeight(0));
        for (int n = 1; n <= AccScope.MaxRefineLevel; n++)
            Assert.True(AccScope.RefineWeight(n) < AccScope.RefineWeight(n - 1), $"step {n}");
        // Every step is a weight ScopeRefiner takes.
        foreach (var w in AccScope.RefineWeights) _ = new ScopeRefiner(w);
    }

    [Fact]
    public void The_default_refine_step_is_the_librarys_measured_default()
    {
        Assert.Equal(ScopeRefiner.DefaultWeight, AccScope.RefineWeight(AccScope.DefaultRefineLevel));
        // A fresh settings file starts both panels on it.
        var s = new Yaesu_Web_Control.Models.ApplicationSettings();
        Assert.Equal(AccScope.DefaultRefineLevel, s.AccRefineLevelA);
        Assert.Equal(AccScope.DefaultRefineLevel, s.AccRefineLevelB);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(99)]
    public void A_refine_step_out_of_range_gets_the_default(int level)
    {
        Assert.False(AccScope.IsRefineLevel(level));
        Assert.Equal(ScopeRefiner.DefaultWeight, AccScope.RefineWeight(level));
    }

    [Fact]
    public void Refine_raw_draws_the_radios_rows_as_they_come()
    {
        var refiner = new ScopeRefiner(AccScope.RefineWeight(0));
        AccScope.ToSpectrum(Frame(mainLevel: 80).Main, 1, refiner.Add(Frame(mainLevel: 80).Main));
        var scope = Frame(mainLevel: 120).Main;
        var f = AccScope.ToSpectrum(scope, 2, refiner.Add(scope))!;
        Assert.All(f.Bins, b => Assert.Equal(AccScope.LevelToDb(120), b));
    }

    [Fact]
    public void Sub_row_comes_from_sub()
    {
        var f = AccScope.ToSpectrum(Frame().Sub, 1)!;

        Assert.Equal(7_074_000, f.CentreHz);
        Assert.Equal(50_000, f.SpanHz);
        Assert.All(f.Bins, b => Assert.Equal(AccScope.LevelToDb(60), b));
    }

    [Fact]
    public void In_fix_the_window_is_placed_from_its_left_edge_not_the_vfo()
    {
        var f = AccScope.ToSpectrum(Frame(mainVfo: 14_074_000, mainSpanIndex: 6, mainPlacement: ScopePlacement.Fix,
                                          mainLeft: 14_000_000).Main, 1)!;

        Assert.Equal(14_050_000, f.CentreHz);
        Assert.Equal(100_000, f.SpanHz);
    }

    [Fact]
    public void A_frame_that_makes_no_sense_is_not_drawn()
    {
        Assert.Null(AccScope.ToSpectrum(Frame(mainVfo: 0).Main, 1));
        Assert.Null(AccScope.ToSpectrum(Frame(mainSpanIndex: 12).Main, 1));
    }

    [Fact]
    public void A_stronger_level_is_a_higher_db()
    {
        Assert.True(AccScope.LevelToDb(200) > AccScope.LevelToDb(100));
        Assert.Equal(AccScope.DbAtZero, AccScope.LevelToDb(0));
    }

    [Theory]
    [InlineData(1_000, '0')]
    [InlineData(20_000, '4')]
    [InlineData(50_000, '5')]
    [InlineData(1_000_000, '9')]
    public void Span_buttons_map_to_the_radios_ss_digit(double hz, char digit) =>
        Assert.Equal(digit, AccScope.SpanDigit(hz));

    [Theory]
    [InlineData(15_000)]
    [InlineData(30_000)]
    [InlineData(40_000)]
    [InlineData(2_000_000)]
    public void Sdr_only_spans_have_no_digit(double hz) => Assert.Null(AccScope.SpanDigit(hz));

    [Fact]
    public void Every_radio_span_is_one_the_panels_offer()
    {
        foreach (var hz in ReceiverScope.Spans)
            Assert.True(SpectrumSpanPlan.IsValid(hz), $"{hz} Hz");
    }

    [Fact]
    public void No_frames_says_why()
    {
        var (s1, d1) = AccScope.WhenNoFrames(new AccScopeState(AccScopeStatus.Searching, null, null));
        Assert.Equal("connecting", s1);
        Assert.Null(d1);

        var (s2, d2) = AccScope.WhenNoFrames(new AccScopeState(AccScopeStatus.NoBoard, null, "No ACC board found."));
        Assert.Equal("disconnected", s2);
        Assert.Equal("No ACC board found.", d2);

        var (s3, d3) = AccScope.WhenNoFrames(new AccScopeState(AccScopeStatus.Reading, "COM6", null));
        Assert.Equal("disconnected", s3);
        Assert.Contains("COM6", d3);
        Assert.Contains("pins 11 and 12", d3);
    }
}
