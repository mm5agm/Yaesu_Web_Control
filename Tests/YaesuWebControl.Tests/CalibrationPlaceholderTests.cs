using System.Text.RegularExpressions;
using Yaesu_Web_Control.Models.Calibration;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

/// <summary>
/// Felix DF3FX's FT-817 power meter sat at full scale at its lowest power
/// (D#379): his calibration.user.json was made before the FT-817 table shipped
/// and kept the FTdx101's 200 W PWR table. Untouched placeholders now follow
/// the radio model; meters the user calibrated stay. And the Settings page's
/// selects must not hard-code a selected option, or a save writes it back
/// (his 9600 baud kept turning into 38400).
/// </summary>
public class CalibrationPlaceholderTests
{
    private static MeterCalibration Meter(string name, params (double raw, string radio)[] pts) => new()
    {
        Name = name,
        Points = pts.Select(p => new CalibrationPoint { Raw = p.raw, Radio = p.radio }).ToList(),
    };

    private static readonly MeterCalibration BigPwr = Meter("PWR", (30, "5"), (76, "25"), (112, "50"), (222, "200"));
    private static readonly MeterCalibration QrpPwr = Meter("PWR", (0, "0"), (17, "0.5"), (51, "1"), (136, "5"));

    [Fact]
    public void An_untouched_placeholder_from_another_model_is_replaced()
    {
        var user = new CalibrationFile { Meters = { Meter("PWR", (30, "5.0"), (76, "25"), (112, "50"), (222, "200")) } };
        var ft817 = new CalibrationFile { Meters = { QrpPwr } };
        var generic = new CalibrationFile { Meters = { BigPwr } };

        bool changed = CalibrationStorage.ReplaceShippedPlaceholders(user, ft817, new[] { generic, ft817 });

        Assert.True(changed);
        Assert.Equal(new[] { 0.0, 17, 51, 136 }, user.Meters[0].Points.Select(p => p.Raw));
    }

    [Fact]
    public void A_meter_the_user_calibrated_is_kept()
    {
        var mine = Meter("PWR", (20, "5"), (80, "25"), (115, "50"), (225, "200"));
        var user = new CalibrationFile { Meters = { mine } };
        var ft817 = new CalibrationFile { Meters = { QrpPwr } };

        bool changed = CalibrationStorage.ReplaceShippedPlaceholders(user, ft817, new[] { new CalibrationFile { Meters = { BigPwr } }, ft817 });

        Assert.False(changed);
        Assert.Same(mine, user.Meters[0]);
    }

    [Fact]
    public void A_meter_already_on_this_models_table_is_left_alone()
    {
        var user = new CalibrationFile { Meters = { Meter("PWR", (0, "0"), (17, "0.5"), (51, "1"), (136, "5")) } };
        var ft817 = new CalibrationFile { Meters = { QrpPwr } };

        Assert.False(CalibrationStorage.ReplaceShippedPlaceholders(user, ft817, new[] { ft817 }));
    }

    [Fact]
    public void The_shipped_FT817_table_differs_from_the_generic_one()
    {
        // The case Felix hit: if these were the same, nothing would be swapped.
        string Pwr(string file) => Regex.Match(File.ReadAllText(Path.Combine(RepoRoot(), "wwwroot", file)),
            "\"PWR\".*?\\]", RegexOptions.Singleline).Value;
        Assert.NotEqual(Pwr("calibration.default.json"), Pwr("calibration.default.FT-817.json"));
    }

    [Theory]
    [InlineData("Settings.BaudRate")]
    [InlineData("Settings.WebAddress")]
    public void Settings_selects_do_not_hard_code_a_selected_option(string field)
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "Pages/Settings.cshtml"));
        var select = Regex.Match(page, $"<select[^>]*asp-for=\"{Regex.Escape(field)}\"[^>]*>(.*?)</select>", RegexOptions.Singleline);
        Assert.True(select.Success, $"No select for {field}.");
        Assert.DoesNotMatch(new Regex(@"<option[^>]*\sselected[\s>]"), select.Groups[1].Value);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Yaesu_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
