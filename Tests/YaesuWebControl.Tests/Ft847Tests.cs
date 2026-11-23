using System.Collections.Concurrent;
using System.Text;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// The FT-847 speaks the old five-byte binary CAT. Ft847CatTransport has to turn
// YWC's text commands into the right blocks and the radio's replies back into
// text. These run against Ft847Simulator, which is built from the manual and
// Hamlib, so they prove the translation; only a real FT-847 proves the radio.
// Timing-sensitive (an emulated radio answering over a fake serial port):
// skipped in CI, where the shared runners stall long enough to fail it
// now and then. Run locally with the full suite.
[Trait("Category", "SerialTiming")]
public class Ft847Tests
{
    private static (Ft847CatTransport Link, Ft847Simulator Radio) Open(int pollMs = 5000)
    {
        var radio = new Ft847Simulator();
        var link = new Ft847CatTransport(radio, replyTimeoutMs: 200, pollMs: pollMs);
        link.Open();
        return (link, radio);
    }

    private static async Task<List<string>> SendAsync(Ft847CatTransport link, string commands, int expected)
    {
        var replies = new ConcurrentQueue<string>();
        void OnData(string text) => replies.Enqueue(text);
        link.DataReceived += OnData;
        try
        {
            if (commands.Length > 0)
            {
                var bytes = Encoding.ASCII.GetBytes(commands);
                link.Write(bytes, 0, bytes.Length);
            }
            for (int i = 0; i < 200 && replies.Count < expected; i++)
                await Task.Delay(10);
            await Task.Delay(60);
            return replies.ToList();
        }
        finally
        {
            link.DataReceived -= OnData;
        }
    }

    // ── The protocol helpers, against the manual's own examples ──

    [Fact]
    public void Frequency_IsBcdInTenHertzUnits_MostSignificantFirst()
    {
        // Manual: "43, 21, 00, 00 = 432.1000 MHz".
        Assert.Equal(new byte[] { 0x43, 0x21, 0x00, 0x00 }, Ft847Cat.FrequencyBcd(432_100_000));
        Assert.Equal(new byte[] { 0x01, 0x40, 0x74, 0x00 }, Ft847Cat.FrequencyBcd(14_074_000));
        Assert.Equal(new byte[] { 0x43, 0x97, 0x00, 0x00, 0x01 }, Ft847Cat.SetFrequencyBlock(439_700_000));
        Assert.Equal(432_100_000, Ft847Cat.FrequencyFromBcd(new byte[] { 0x43, 0x21, 0x00, 0x00 }));
    }

    [Fact]
    public void Frequency_IsRoundedToTenHertz()
    {
        Assert.Equal(new byte[] { 0x01, 0x40, 0x74, 0x01 }, Ft847Cat.FrequencyBcd(14_074_006));
        Assert.Equal(new byte[] { 0x01, 0x40, 0x74, 0x00 }, Ft847Cat.FrequencyBcd(14_074_004));
    }

    [Fact]
    public void ANibbleThatIsNotADigit_IsNotAFrequency()
    {
        Assert.Null(Ft847Cat.FrequencyFromBcd(new byte[] { 0x1A, 0x00, 0x00, 0x00 }));
    }

    [Theory]
    [InlineData('1', 0x00)] [InlineData('2', 0x01)] [InlineData('3', 0x02)] [InlineData('7', 0x03)]
    [InlineData('5', 0x04)] [InlineData('4', 0x08)] [InlineData('D', 0x84)] [InlineData('B', 0x88)]
    public void Modes_RoundTrip(char md, byte ft847)
    {
        Assert.Equal(ft847, Ft847Cat.ModeFromMd(md));
        Assert.Equal(md, Ft847Cat.MdFromMode(ft847));
    }

    [Theory]
    [InlineData('C', 0x01)] [InlineData('9', 0x01)] [InlineData('8', 0x00)] [InlineData('6', 0x00)] [InlineData('A', 0x08)]
    public void DataAndRttyModes_GoOutAsTheirBaseMode(char md, byte ft847) =>
        Assert.Equal(ft847, Ft847Cat.ModeFromMd(md));

    [Fact]
    public void NarrowCw_ReadsAsCw() => Assert.Equal('3', Ft847Cat.MdFromMode(0x82));

    // ── The transport ──

    [Fact]
    public void Open_SendsCatOn_AndClose_SendsCatOff()
    {
        var (link, radio) = Open();
        Assert.True(radio.CatOn);
        link.Close();
        Assert.False(radio.CatOn);
        Assert.Equal(0x80, radio.Received[^1][4]);
    }

    [Fact]
    public async Task FrequencyAndMode_ReadAsModernReplies()
    {
        var (link, radio) = Open();
        radio.FrequencyHz = 432_100_000;
        radio.Mode = 0x08;
        Assert.Equal(new[] { "FA432100000;", "MD04;" }, await SendAsync(link, "FA;MD0;", 2));
        link.Dispose();
    }

    [Fact]
    public async Task OneRead_AnswersBothFrequencyAndMode()
    {
        var (link, radio) = Open();
        await SendAsync(link, "FA;MD0;", 2);
        Assert.Equal(1, radio.Received.Count(b => b[4] == Ft847Cat.ReadMainFrequencyAndMode));
        link.Dispose();
    }

    [Fact]
    public async Task Sets_ReachTheRadio()
    {
        var (link, radio) = Open();
        await SendAsync(link, "FA007074000;MD01;", 0);
        Assert.Equal(7_074_000, radio.FrequencyHz);
        Assert.Equal(0x00, radio.Mode);
        Assert.Equal(new[] { "FA007074000;", "MD01;" }, await SendAsync(link, "FA;MD0;", 2));
        link.Dispose();
    }

    [Fact]
    public async Task ADataModeSet_ReadsBackAsTheDataMode_WhileTheRadioStaysInItsBase()
    {
        var (link, radio) = Open();
        await SendAsync(link, "MD0C;", 0);
        Assert.Equal(0x01, radio.Mode);
        Assert.Equal(new[] { "MD0C;" }, await SendAsync(link, "MD0;", 1));

        radio.Mode = 0x02;   // the operator turns the mode knob to CW
        await Task.Delay(150);
        Assert.Equal(new[] { "MD03;" }, await SendAsync(link, "MD0;", 1));
        link.Dispose();
    }

    [Fact]
    public async Task OutOfRangeFrequency_IsNotSent()
    {
        var (link, radio) = Open();
        await SendAsync(link, "FA000050000;", 0);
        Assert.DoesNotContain(radio.Received, b => b[4] == Ft847Cat.SetMainFrequency);
        link.Dispose();
    }

    [Fact]
    public async Task SMeter_IsScaledTo255()
    {
        var (link, radio) = Open();
        radio.SMeter = 31;
        Assert.Equal(new[] { "SM0255;" }, await SendAsync(link, "SM0;", 1));
        radio.SMeter = 0;
        Assert.Equal(new[] { "SM0000;" }, await SendAsync(link, "SM0;", 1));
        link.Dispose();
    }

    [Fact]
    public async Task TxStatus_BitSevenClear_MeansTransmitting()
    {
        var (link, radio) = Open();
        Assert.Equal(new[] { "TX0;" }, await SendAsync(link, "TX;", 1));
        radio.Transmitting = true;
        radio.PowerMeter = 31;
        Assert.Equal(new[] { "TX2;", "RM5255000;" }, await SendAsync(link, "TX;RM5;", 2));
        link.Dispose();
    }

    [Fact]
    public async Task PttOn_IsNeverSent()
    {
        var (link, radio) = Open();
        await SendAsync(link, "TX1;", 0);
        Assert.DoesNotContain(radio.Received, b => b[4] == 0x08);
        link.Dispose();
    }

    [Fact]
    public async Task PttOff_IsSent()
    {
        var (link, radio) = Open();
        await SendAsync(link, "TX0;", 0);
        Assert.Contains(radio.Received, b => b[4] == Ft847Cat.PttOff);
        link.Dispose();
    }

    [Fact]
    public async Task InitAnswers_AreMadeUpLocally()
    {
        var (link, _) = Open();
        var replies = await SendAsync(link, "ID;PS;DT0;", 3);
        Assert.Equal("ID0847;", replies[0]);
        Assert.Equal("PS1;", replies[1]);
        Assert.StartsWith("DT0" + DateTime.Now.ToString("yyyy"), replies[2]);
        link.Dispose();
    }

    [Fact]
    public async Task ACommandTheRadioDoesNotHave_ReadsAsQuestionMark_AndSendsNothing()
    {
        var (link, radio) = Open();
        var before = radio.Received.Count;
        Assert.Equal(new[] { "?;", "?;" }, await SendAsync(link, "NB0;AG0;NB01;", 2));
        Assert.Equal(before, radio.Received.Count);
        link.Dispose();
    }

    [Fact]
    public async Task VfoB_IsKeptLocally()
    {
        var (link, radio) = Open();
        Assert.Equal(new[] { "FB014074000;" }, await SendAsync(link, "FB;", 1));
        await SendAsync(link, "FB021074000;MD12;", 0);
        Assert.Equal(new[] { "FB021074000;", "MD12;" }, await SendAsync(link, "FB;MD1;", 2));
        Assert.Equal(14_074_000, radio.FrequencyHz);
        link.Dispose();
    }

    [Fact]
    public async Task WithAutoInformation_AKnobTurnIsReported()
    {
        var (link, radio) = Open(pollMs: 40);
        await SendAsync(link, "AI1;", 0);
        await Task.Delay(150);
        radio.FrequencyHz = 14_075_500;
        var replies = await SendAsync(link, "", 1);
        Assert.Contains("FA014075500;", replies);
        link.Dispose();
    }

    [Fact]
    public async Task AnEarlyRadioThatDoesNotAnswerReads_GetsNoMadeUpFrequency()
    {
        var (link, radio) = Open();
        radio.AnswersReads = false;
        Assert.Empty(await SendAsync(link, "FA;", 0));
        link.Dispose();
    }

    [Fact]
    public void Capabilities_TreatItAsAVhfUhfSingleReceiver()
    {
        Assert.True(RadioCapabilities.HasTwoMetresAndSeventyCm("FT-847"));
        Assert.True(RadioCapabilities.IsTunableFrequency("FT-847", 432_100_000));
        Assert.False(RadioCapabilities.IsTunableFrequency("FT-847", 600_000_000));
        Assert.Equal(100, RadioCapabilities.MaxPowerWattsAt("FT-847", 14_074_000));
        Assert.Equal(50, RadioCapabilities.MaxPowerWattsAt("FT-847", 144_300_000));
        Assert.True(RadioCapabilities.IsSingleReceiver("FT-847"));
        Assert.False(RadioCapabilities.HasAntennaSelector("FT-847"));
        Assert.False(RadioCapabilities.CanSelectRxVfo("FT-847"));
    }
}
