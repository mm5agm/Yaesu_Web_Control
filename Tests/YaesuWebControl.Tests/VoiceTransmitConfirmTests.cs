using Yaesu_Web_Control.Services.Voice;

namespace YaesuWebControl.Tests;

/// <summary>
/// Nothing here can reach a radio: the class only decides, and the voice service acts
/// on what it says.
/// </summary>
public class VoiceTransmitConfirmTests
{
    private const float Clear = 0.95f;
    private static readonly DateTime Start = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private static VoiceRecognisedCommand Command(string intent, bool dryRun = false, string? macroCat = null, string vfo = "A")
    {
        var args = new Dictionary<string, object>();
        if (macroCat != null) args["macroCat"] = macroCat;
        return new VoiceRecognisedCommand(intent, args, vfo, dryRun);
    }

    private static VoiceRecognisedCommand Confirm(bool dryRun = false) => Command(VoiceTransmitConfirm.ConfirmIntent, dryRun);

    private static VoiceTransmitDecision Decide(
        VoiceTransmitConfirm confirm, VoiceRecognisedCommand command, double seconds = 0, float confidence = Clear)
        => confirm.Decide(command, confidence, Start.AddSeconds(seconds));

    [Theory]
    [InlineData("TxOn", null)]
    [InlineData("AtuTune", null)]
    [InlineData("Macro", "TX1;")]
    public void AnythingThatGoesOnTheAirWaitsToBeConfirmed(string intent, string? macroCat)
    {
        var confirm = new VoiceTransmitConfirm();
        var command = Command(intent, macroCat: macroCat);

        Assert.Equal(VoiceTransmitStep.AskToConfirm, Decide(confirm, command).Step);

        var confirmed = Decide(confirm, Confirm(), seconds: 2);
        Assert.Equal(VoiceTransmitStep.Run, confirmed.Step);
        Assert.Same(command, confirmed.Command);
    }

    [Theory]
    [InlineData("TxOff")]
    [InlineData("AtuOff")]
    [InlineData("BandUp")]
    public void EverythingElseRunsStraightAway(string intent)
    {
        var decision = Decide(new VoiceTransmitConfirm(), Command(intent), confidence: 0.6f);

        Assert.Equal(VoiceTransmitStep.Run, decision.Step);
    }

    [Fact]
    public void AMacroThatStaysOnReceiveRunsStraightAway()
    {
        Assert.Equal(VoiceTransmitStep.Run, Decide(new VoiceTransmitConfirm(), Command("Macro", macroCat: "TX0;")).Step);
    }

    [Fact]
    public void ConfirmOnItsOwnDoesNothing()
    {
        Assert.Equal(VoiceTransmitStep.NothingToConfirm, Decide(new VoiceTransmitConfirm(), Confirm()).Step);
    }

    [Fact]
    public void ConfirmWorksOnlyOnce()
    {
        var confirm = new VoiceTransmitConfirm();
        Decide(confirm, Command("TxOn"));
        Decide(confirm, Confirm());

        Assert.Equal(VoiceTransmitStep.NothingToConfirm, Decide(confirm, Confirm()).Step);
    }

    [Fact]
    public void ConfirmTooLateDoesNothing()
    {
        var confirm = new VoiceTransmitConfirm();
        Decide(confirm, Command("TxOn"));

        Assert.Equal(VoiceTransmitStep.NothingToConfirm,
            Decide(confirm, Confirm(), seconds: VoiceTransmitConfirm.Window.TotalSeconds + 0.1).Step);
    }

    [Fact]
    public void ConfirmAtTheEndOfTheWindowStillCounts()
    {
        var confirm = new VoiceTransmitConfirm();
        Decide(confirm, Command("TxOn"));

        Assert.Equal(VoiceTransmitStep.Run, Decide(confirm, Confirm(), seconds: VoiceTransmitConfirm.Window.TotalSeconds).Step);
    }

    [Fact]
    public void AnotherCommandDropsTheOneWaiting()
    {
        var confirm = new VoiceTransmitConfirm();
        Decide(confirm, Command("TxOn"));

        Assert.Equal(VoiceTransmitStep.Run, Decide(confirm, Command("BandUp"), seconds: 1).Step);
        Assert.Equal(VoiceTransmitStep.NothingToConfirm, Decide(confirm, Confirm(), seconds: 2).Step);
    }

    [Fact]
    public void ForgetDropsTheOneWaiting()
    {
        var confirm = new VoiceTransmitConfirm();
        Decide(confirm, Command("TxOn"));
        confirm.Forget();

        Assert.Equal(VoiceTransmitStep.NothingToConfirm, Decide(confirm, Confirm(), seconds: 1).Step);
    }

    [Fact]
    public void AMisheardTransmitIsRefusedAndNotHeld()
    {
        var confirm = new VoiceTransmitConfirm();

        Assert.Equal(VoiceTransmitStep.NotClear, Decide(confirm, Command("TxOn"), confidence: VoiceTransmitGate.MinConfidence - 0.01f).Step);
        Assert.Equal(VoiceTransmitStep.NothingToConfirm, Decide(confirm, Confirm(), seconds: 1).Step);
    }

    [Fact]
    public void AMisheardConfirmIsRefusedAndDropsTheOneWaiting()
    {
        var confirm = new VoiceTransmitConfirm();
        Decide(confirm, Command("TxOn"));

        Assert.Equal(VoiceTransmitStep.NotClear, Decide(confirm, Confirm(), seconds: 1, confidence: VoiceTransmitGate.MinConfidence - 0.01f).Step);
        Assert.Equal(VoiceTransmitStep.NothingToConfirm, Decide(confirm, Confirm(), seconds: 2).Step);
    }

    [Fact]
    public void ADryRunStaysDryWhenConfirmed()
    {
        var confirm = new VoiceTransmitConfirm();
        Decide(confirm, Command("TxOn", dryRun: true));

        var confirmed = Decide(confirm, Confirm(dryRun: true), seconds: 1);

        Assert.Equal(VoiceTransmitStep.Run, confirmed.Step);
        Assert.True(confirmed.Command.DryRun);
    }

    // The dangerous way round: a live command must not be released from a dry run.
    [Fact]
    public void ADryRunCannotConfirmALiveCommand()
    {
        var confirm = new VoiceTransmitConfirm();
        Decide(confirm, Command("TxOn", dryRun: false));

        Assert.Equal(VoiceTransmitStep.NothingToConfirm, Decide(confirm, Confirm(dryRun: true), seconds: 1).Step);
    }

    [Fact]
    public void ALiveSessionCannotConfirmADryRunCommand()
    {
        var confirm = new VoiceTransmitConfirm();
        Decide(confirm, Command("TxOn", dryRun: true));

        Assert.Equal(VoiceTransmitStep.NothingToConfirm, Decide(confirm, Confirm(dryRun: false), seconds: 1).Step);
    }

    [Fact]
    public void AConfirmedCommandGoesToTheVfoItWasHeardFor()
    {
        var confirm = new VoiceTransmitConfirm();
        Decide(confirm, Command("TxOn", vfo: "B"));

        Assert.Equal("B", Decide(confirm, Confirm(), seconds: 1).Command.Vfo);
    }
}
