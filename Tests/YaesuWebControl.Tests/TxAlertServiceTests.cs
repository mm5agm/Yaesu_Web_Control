using Yaesu_Web_Control.Models.Calibration;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// Discussion #257: the high SWR alert reads SWR through the same calibration
// table as the gauges, so 3.2 on the pop-up is 3.2 on the meter.
public sealed class TxAlertServiceTests
{
    private static CalibrationFile File(params (double Raw, string? Radio)[] points)
    {
        var meter = new MeterCalibration { Name = "SWR" };
        foreach (var (raw, radio) in points) meter.Points.Add(new CalibrationPoint { Raw = raw, Radio = radio });
        var f = new CalibrationFile();
        f.Meters.Add(new MeterCalibration { Name = "ALC" });
        f.Meters.Add(meter);
        return f;
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(51, 1.5)]
    [InlineData(64, 1.75)]
    [InlineData(77, 2.0)]
    [InlineData(102.5, 2.5)]
    [InlineData(128, 3.0)]
    [InlineData(242, 9.9)]
    [InlineData(255, 9.9)]
    [InlineData(-5, 1.0)]
    public void Interpolate_follows_the_FTdx101_table(double raw, double swr)
    {
        Assert.Equal(swr, TxAlertService.Interpolate(TxAlertService.DefaultSwrTable, raw), 3);
    }

    [Fact]
    public void ReadSwrTable_takes_the_SWR_meter_sorted_by_raw()
    {
        var t = TxAlertService.ReadSwrTable(() => File((128, "3.0"), (0, "1.0"), (77, "2.0")));
        Assert.NotNull(t);
        Assert.Equal(new[] { 0.0, 77.0, 128.0 }, t!.Select(r => r.Raw));
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, t.Select(r => r.Value));
    }

    [Fact]
    public void ReadSwrTable_skips_rows_without_a_number()
    {
        var t = TxAlertService.ReadSwrTable(() => File((0, "1.0"), (50, ""), (60, "inf"), (100, "3")));
        Assert.Equal(new[] { 0.0, 100.0 }, t!.Select(r => r.Raw));
    }

    [Fact]
    public void ReadSwrTable_is_null_with_fewer_than_two_rows()
    {
        Assert.Null(TxAlertService.ReadSwrTable(() => File((0, "1.0"))));
        Assert.Null(TxAlertService.ReadSwrTable(() => new CalibrationFile()));
    }

    [Fact]
    public void ReadSwrTable_is_null_when_the_file_cannot_be_read()
    {
        Assert.Null(TxAlertService.ReadSwrTable(() => throw new IOException("locked")));
    }
}
