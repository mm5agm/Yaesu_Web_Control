using System.Globalization;
using System.Text.RegularExpressions;

namespace YaesuWebControl.Tests;

// The FTdx101 tiles on the Radio Display (radio-display-hotspots.js). The TFT
// shows VFO A and VFO B side by side, each with its own frequency and its own
// ANT ATT IPO R.FIL AGC cells. Colin 2026-11-23: only VFO A's digits had
// tiles, the readout tiles were five cells across both VFOs, and the meter
// tile stopped short of the SWR meter's right-hand scales.
public class RadioDisplayFtdx101TilesTests
{
    private static string Script()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Program.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "wwwroot", "js", "video", "radio-display-hotspots.js"));
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static List<(string Id, double L, double R, long Step)> Digits(string js, string table)
    {
        var start = js.IndexOf($"const {table} = [", StringComparison.Ordinal);
        Assert.True(start >= 0, table);
        var body = js[start..js.IndexOf("];", start, StringComparison.Ordinal)];
        return Regex.Matches(body, @"\['(\w+)',\s*([\d.]+),\s*([\d.]+),\s*([\d_]+)\]")
            .Select(m => (m.Groups[1].Value, D(m.Groups[2].Value), D(m.Groups[3].Value),
                          long.Parse(m.Groups[4].Value.Replace("_", ""), CultureInfo.InvariantCulture)))
            .ToList();
    }

    private static string Ftdx101Zones(string js)
    {
        var start = js.IndexOf("    FTdx101: {", StringComparison.Ordinal);
        var end = js.IndexOf("    FTdx10: {", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var block = js[start..end];
        return block[block.IndexOf("        zones: {", StringComparison.Ordinal)..];
    }

    [Theory]
    [InlineData("FTDX101_DIGITS", "FTDX101_DIGITS_B")]
    [InlineData("FTDX101_DIGITS_A_SMALL", "FTDX101_DIGITS_B_LARGE")]
    public void VfoB_HasADigitTileForEachDigit_RightOfVfoA(string aTable, string bTable)
    {
        var js = Script();
        var a = Digits(js, aTable);
        var b = Digits(js, bTable);
        Assert.Equal(8, a.Count);
        for (int i = 1; i < a.Count; i++) Assert.Equal(a[i - 1].R, a[i].L);

        Assert.Equal(a.Select(d => d.Step), b.Select(d => d.Step));
        Assert.True(b[0].L > a[^1].R, "VFO B's digits start right of VFO A's");
        Assert.True(b[^1].R <= 1.0);
        for (int i = 0; i < b.Count; i++)
        {
            Assert.True(b[i].R > b[i].L, b[i].Id);
            if (i > 0) Assert.Equal(b[i - 1].R, b[i].L);   // cells touch, no gaps
        }
    }

    // The radio draws the active VFO large and the other small (Colin
    // 2026-11-23, SUB active), so each table hides while it isn't drawn.
    [Fact]
    public void LargeAndSmallDigits_ShowForTheActiveVfo()
    {
        var js = Script();
        var zones = Ftdx101Zones(js);
        Assert.Contains("digitZones(FTDX101_DIGITS, 0.325, 0.400, 'A', ['active:B'])", zones);
        Assert.Contains("digitZones(FTDX101_DIGITS_A_SMALL, 0.325, 0.400, 'A', ['active:A'])", zones);
        Assert.Contains("digitZones(FTDX101_DIGITS_B, 0.325, 0.400, 'B', ['active:B'])", zones);
        Assert.Contains("digitZones(FTDX101_DIGITS_B_LARGE, 0.325, 0.400, 'B', ['active:A'])", zones);
        Assert.Contains("'active:A': vfo => vfo === 'A'", js);
        Assert.Contains("'active:B': vfo => vfo === 'B'", js);
        Assert.Contains("if (conds.some(c => VFO_CONDITIONS[c]?.(this._vfo()))) return true;", js);
    }

    [Fact]
    public void Readouts_AreTenCells_FiveForEachVfo()
    {
        var zones = Ftdx101Zones(Script());
        var cells = Regex.Matches(zones,
                @"(\w+):\s*\{ rect: \[([\d.]+), [\d.]+, ([\d.]+), [\d.]+\], action: 'readout\.(\w+)',\s*vfo: '([AB])' \}")
            .Select(m => (Id: m.Groups[1].Value, L: D(m.Groups[2].Value), R: D(m.Groups[3].Value),
                          Key: m.Groups[4].Value, Vfo: m.Groups[5].Value))
            .ToList();

        var keys = new[] { "ant", "att", "ipo", "rfil", "agc" };
        Assert.Equal(keys, cells.Where(c => c.Vfo == "A").Select(c => c.Key));
        Assert.Equal(keys, cells.Where(c => c.Vfo == "B").Select(c => c.Key));
        Assert.True(cells.Where(c => c.Vfo == "A").Max(c => c.R) <= 0.5, "VFO A's cells are the left half");
        Assert.True(cells.Where(c => c.Vfo == "B").Min(c => c.L) >= 0.5, "VFO B's cells are the right half");
        for (int i = 1; i < cells.Count; i++)
            Assert.True(cells[i].L >= cells[i - 1].R, $"{cells[i].Id} overlaps {cells[i - 1].Id}");
    }

    [Fact]
    public void MeterTile_ReachesTheSwrMetersRightHandScales()
    {
        var m = Regex.Match(Ftdx101Zones(Script()), @"meter:\s*\{ rect: \[[\d.]+, [\d.]+, ([\d.]+), [\d.]+\]");
        Assert.True(m.Success);
        var right = D(m.Groups[1].Value);
        Assert.InRange(right, 0.84, 0.855);   // past the dB / VDD / A scales, short of VFO B's filter display
    }

    [Fact]
    public void ZoneVfo_IsUsedForDigitsAndReadouts()
    {
        var js = Script();
        Assert.Contains("const vfo = this._zoneVfo(zone);", js);
        Assert.Contains("this._cycleReadout(action.key, this._zoneVfo(zone))", js);
        Assert.Contains("if (!zone.vfo && base?.vfo) zone.vfo = base.vfo;", js);
    }
}
