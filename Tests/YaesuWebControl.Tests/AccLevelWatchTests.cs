using Yaesu_Web_Control.Services.Sdr;
using YaesuAccScope;

namespace YaesuWebControl.Tests;

/// <summary>
/// The scope LEVEL warning on an ACC panel. The ACC rows are the radio's
/// display data, after its LEVEL, so too low a LEVEL puts the floor on step 1
/// and loses weak signals in a way that looks just like a quiet band. These
/// pin when the warning is raised and when it clears.
/// </summary>
public class AccLevelWatchTests
{
    /// <summary>A raw 850-point row: floor at 40, `zeros` span points on `step` (the clip is 1), optionally one on 255.</summary>
    private static byte[] Row(int zeros, bool top = false, byte step = AccLevelWatch.BottomStep)
    {
        var row = new byte[AccFrame.Points];
        Array.Fill(row, (byte)40);
        for (int i = 0; i < zeros; i++) row[AccScope.FirstSpanPoint + i * 7 % ReceiverScope.SpanPoints] = step;
        if (top) row[ReceiverScope.CentrePoint] = 255;
        return row;
    }

    /// <summary>Feeds a window of rows; returns whether the last one changed the problem.</summary>
    private static bool Window(AccLevelWatch w, Func<int, byte[]> row)
    {
        bool changed = false;
        for (int i = 0; i < AccLevelWatch.WindowRows; i++) changed = w.Add(row(i));
        return changed;
    }

    private static int Share(double share) => (int)Math.Ceiling(share * ReceiverScope.SpanPoints);

    [Fact]
    public void AClearFloor_RaisesNothing()
    {
        var w = new AccLevelWatch();
        Assert.False(Window(w, _ => Row(0)));
        Assert.Equal(AccLevelProblem.None, w.Problem);
    }

    [Fact]
    public void FloorOnTheBottomStep_IsTooLow_AndClearsOnlyWellBelowTheBar()
    {
        var w = new AccLevelWatch();
        Assert.True(Window(w, _ => Row(Share(0.06))));
        Assert.Equal(AccLevelProblem.TooLow, w.Problem);

        // Between the clearing and raising bars: it stays, so it doesn't flicker.
        Assert.False(Window(w, _ => Row(Share(0.03))));
        Assert.Equal(AccLevelProblem.TooLow, w.Problem);

        Assert.True(Window(w, _ => Row(0)));
        Assert.Equal(AccLevelProblem.None, w.Problem);
    }

    [Fact]
    public void StepZero_CountsToo_AndStepTwoDoesNot()
    {
        var w = new AccLevelWatch();
        Window(w, _ => Row(Share(0.10), step: 2));
        Assert.Equal(AccLevelProblem.None, w.Problem);
        Assert.True(Window(w, _ => Row(Share(0.10), step: 0)));
        Assert.Equal(AccLevelProblem.TooLow, w.Problem);
    }

    [Fact]
    public void AGoodLevelOnANarrowSpan_ClearsTheWarning()
    {
        // LEVEL +23.5 on 10 m at the 1 kHz span: up to 2.2 % of points on the clip.
        var w = new AccLevelWatch();
        Window(w, _ => Row(Share(0.11)));                     // LEVEL +05 on 160 m
        Assert.Equal(AccLevelProblem.TooLow, w.Problem);
        Assert.True(Window(w, _ => Row(Share(0.022))));
        Assert.Equal(AccLevelProblem.None, w.Problem);
    }

    [Fact]
    public void AFewDipsToTheBottom_AreNotAWarning()
    {
        var w = new AccLevelWatch();
        Window(w, _ => Row(Share(0.03)));
        Assert.Equal(AccLevelProblem.None, w.Problem);
    }

    [Fact]
    public void SignalsOnTheTopStep_InManyRows_AreTooHigh()
    {
        var w = new AccLevelWatch();
        Assert.True(Window(w, i => Row(0, top: i % 4 == 0)));   // a quarter of rows
        Assert.Equal(AccLevelProblem.TooHigh, w.Problem);
        Assert.True(Window(w, _ => Row(0)));
        Assert.Equal(AccLevelProblem.None, w.Problem);
    }

    [Fact]
    public void AnOccasionalTopStep_IsNotAWarning()
    {
        var w = new AccLevelWatch();
        Window(w, i => Row(0, top: i == 0));
        Assert.Equal(AccLevelProblem.None, w.Problem);
    }

    [Fact]
    public void PointsOutsideTheSpan_DontCount()
    {
        var w = new AccLevelWatch();
        Window(w, _ =>
        {
            var row = Row(0);
            Array.Clear(row, 0, AccScope.FirstSpanPoint);   // the 30 beyond the left edge
            return row;
        });
        Assert.Equal(AccLevelProblem.None, w.Problem);
    }

    [Fact]
    public void EachProblem_HasAWarning_AndNoneHasNone()
    {
        Assert.Null(AccScope.LevelWarning(AccLevelProblem.None));
        Assert.Equal("LEVEL too low", AccScope.LevelWarning(AccLevelProblem.TooLow)!.Value.Short);
        Assert.Contains("Raise Level", AccScope.LevelWarning(AccLevelProblem.TooLow)!.Value.Detail);
        Assert.Contains("Lower Level", AccScope.LevelWarning(AccLevelProblem.TooHigh)!.Value.Detail);
    }
}
