using Yaesu_Web_Control.Models;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// The FTdx101's two IF OUT sockets are 105 kHz apart (MAIN 9.005 MHz, SUB
// 8.900 MHz), and until 2026-09-11 one shared SdrIfFrequencyHz tuned both
// SDRs, so the VFO B panel showed a slice of band 100 kHz above the SUB dial.
// A wrong answer here is silent in exactly the same way: the SUB trace looks
// like a perfectly good spectrum, just not of the frequency the dial says.
// These pin down the split and the migration that puts existing users right
// without touching a value they set deliberately.
public sealed class SdrCentreFrequencyMigrationTests
{
    [Theory]
    [InlineData("FTdx101MP", "A", 9_000_000)]
    [InlineData("FTdx101MP", "B", 8_895_000)]
    [InlineData("FTdx101D",  "A", 9_000_000)]
    [InlineData("FTdx101D",  "B", 8_895_000)]
    [InlineData("FTDX3000",  "A", 9_000_000)]
    [InlineData("FTDX3000",  "B", 9_000_000)]
    [InlineData("FTdx10",    "B", 9_000_000)]
    public void DefaultSdrCentreHz_FollowsTheRadiosIfOut(string model, string vfo, long expected)
        => Assert.Equal(expected, RadioCapabilities.DefaultSdrCentreHz(model, vfo));

    [Fact]
    public void DefaultSdrCentreHz_IsCaseInsensitiveOnVfo()
        => Assert.Equal(8_895_000, RadioCapabilities.DefaultSdrCentreHz("FTdx101MP", "b"));

    // The common case: an FTdx101 owner who never touched the field. Their
    // file holds the old stock 9,000,000; A keeps it and B gets the SUB value.
    [Fact]
    public void Migrate_StockLegacyOnAnFtdx101_GivesSubItsOwnCentre()
    {
        var s = new ApplicationSettings { RadioModel = "FTdx101MP", SdrIfFrequencyHz = 9_000_000 };
        SettingsService.MigrateSdrIfFrequency(s);
        Assert.Equal(9_000_000, s.SdrIfFrequencyHzA);
        Assert.Equal(8_895_000, s.SdrIfFrequencyHzB);
        Assert.Equal(0, s.SdrIfFrequencyHz);
    }

    // A deliberate non-stock value (ELAD FDM-DUO, some other IF tap) is kept
    // on BOTH sides - the migration must not guess at what the user meant.
    [Fact]
    public void Migrate_CustomLegacy_IsKeptOnBothVfos()
    {
        var s = new ApplicationSettings { RadioModel = "FTdx101MP", SdrIfFrequencyHz = 12_345_000 };
        SettingsService.MigrateSdrIfFrequency(s);
        Assert.Equal(12_345_000, s.SdrIfFrequencyHzA);
        Assert.Equal(12_345_000, s.SdrIfFrequencyHzB);
    }

    // A file that already has the split fields is left alone, legacy or not.
    [Fact]
    public void Migrate_ExistingPerVfoValues_AreNotOverwritten()
    {
        var s = new ApplicationSettings
        {
            RadioModel        = "FTdx101MP",
            SdrIfFrequencyHz  = 9_000_000,
            SdrIfFrequencyHzA = 9_001_000,
            SdrIfFrequencyHzB = 8_900_000,
        };
        SettingsService.MigrateSdrIfFrequency(s);
        Assert.Equal(9_001_000, s.SdrIfFrequencyHzA);
        Assert.Equal(8_900_000, s.SdrIfFrequencyHzB);
        Assert.Equal(0, s.SdrIfFrequencyHz);
    }

    // Brand-new file: nothing set anywhere, so both sides take the radio's
    // defaults - and on a single-IF radio that is 9 MHz for both.
    [Theory]
    [InlineData("FTdx101MP", 9_000_000, 8_895_000)]
    [InlineData("FTDX3000",  9_000_000, 9_000_000)]
    public void Migrate_NothingSet_FallsBackToTheRadioDefaults(string model, long expectedA, long expectedB)
    {
        var s = new ApplicationSettings { RadioModel = model };
        SettingsService.MigrateSdrIfFrequency(s);
        Assert.Equal(expectedA, s.SdrIfFrequencyHzA);
        Assert.Equal(expectedB, s.SdrIfFrequencyHzB);
    }
}
