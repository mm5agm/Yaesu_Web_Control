using System.Collections.Concurrent;
using System.Text;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// The simulated radio behind YWC_USE_STUB_RADIO=1. It has to answer reads in
// the radio's own format, keep what it is set to, and report sets back under
// AI1, or the pages it exists to exercise show nothing useful.
public class StubRadioTests
{
    private static async Task<List<string>> SendAsync(StubRadio radio, string commands, int expected)
    {
        var replies = new ConcurrentQueue<string>();
        void OnData(string text) => replies.Enqueue(text);
        radio.DataReceived += OnData;
        try
        {
            var bytes = Encoding.ASCII.GetBytes(commands);
            radio.Write(bytes, 0, bytes.Length);
            for (int i = 0; i < 100 && replies.Count < expected; i++)
                await Task.Delay(10);
            await Task.Delay(30);   // and nothing more after that
            return replies.ToList();
        }
        finally
        {
            radio.DataReceived -= OnData;
        }
    }

    private static StubRadio Open(string model = "FTdx101MP")
    {
        var radio = new StubRadio(model);
        radio.Open();
        return radio;
    }

    [Fact]
    public async Task Reads_AreAnsweredInOrder_InTheRadiosFormat()
    {
        using var radio = Open();
        var replies = await SendAsync(radio, "FA;MD0;SH0;ID;", 4);
        Assert.Equal(new[] { "FA014074000;", "MD0C;", "SH0020;", "ID0682;" }, replies);
    }

    [Fact]
    public async Task ASet_IsKept_AndReadsBack()
    {
        using var radio = Open();
        Assert.Empty(await SendAsync(radio, "FA007074000;GT02;", 0));
        Assert.Equal(new[] { "FA007074000;", "GT02;", "GT13;" }, await SendAsync(radio, "FA;GT0;GT1;", 3));
    }

    [Fact]
    public async Task WithAutoInformationOn_ASetIsReportedBack()
    {
        using var radio = Open();
        Assert.Equal(new[] { "NB01;" }, await SendAsync(radio, "AI1;NB01;", 1));
    }

    [Fact]
    public async Task AnUnknownRead_GetsAQuestionMark()
    {
        using var radio = Open();
        Assert.Equal(new[] { "?;" }, await SendAsync(radio, "ZZ;", 1));
    }

    [Fact]
    public async Task SwitchedOff_ItAnswersNothing_UntilPs1()
    {
        using var radio = Open();
        Assert.Empty(await SendAsync(radio, "PS0;FA;", 1));
        Assert.False(radio.PoweredOn);
        Assert.Equal(new[] { "FA014074000;" }, await SendAsync(radio, "PS1;FA;", 1));
    }

    [Fact]
    public async Task Dt0_AnswersWithTheDate()
    {
        using var radio = Open();
        var replies = await SendAsync(radio, "DT0;", 1);
        Assert.Equal("DT0" + DateTime.Now.ToString("yyyyMMdd") + ";", Assert.Single(replies));
    }

    [Fact]
    public async Task MeterReads_HaveTheirMeterNumberAndThreeDigits()
    {
        using var radio = Open();
        var replies = await SendAsync(radio, "SM0;RM8;", 2);
        Assert.Matches(@"^SM0\d{3};$", replies[0]);
        Assert.Equal("RM8190000;", replies[1]);
    }

    [Fact]
    public async Task Ftdx3000_UsesEightDigitFrequencies()
    {
        using var radio = Open("FTDX3000");
        Assert.Equal(new[] { "FA14074000;" }, await SendAsync(radio, "FA;", 1));
    }

    [Fact]
    public async Task Sv_SwapsTheVfos()
    {
        using var radio = Open();
        Assert.Equal(new[] { "FA007074000;", "FB014074000;" }, await SendAsync(radio, "SV;FA;FB;", 2));
    }
}
