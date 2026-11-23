using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// Felix DF3FX's FT-817 (Discussion #379): 432.039 MHz showed as 4.320390,
// modes and VFO changes made on the radio never reached YWC, and PO stayed
// near zero. The causes, all from Hamlib's ft817.c:
//  - the FT-817/818/857/897 answer every set block with a one-byte 00 ack,
//    which YWC didn't read, so a late one landed in front of the next 03 reply;
//  - the 200 ms meter poll kept the command queue busy, so the radio poll
//    (which reports MD/VS changes) never ran;
//  - the FT-817/818 give power through BD, not the F7 meter bits.
// Timing-sensitive (an emulated radio answering over a fake serial port):
// skipped in CI, where the shared runners stall long enough to fail it
// now and then. Run locally with the full suite.
[Trait("Category", "SerialTiming")]
public class Ft817AckTests
{
    private static (Ft847CatTransport Link, Ft847Simulator Radio) Open(string model = "FT-817", int pollMs = 5000, int ackDelayMs = 5)
    {
        var radio = new Ft847Simulator(model: model)
        {
            FrequencyHz = 14_074_000, Mode = 0x01,
            OtherFrequencyHz = 7_074_000, OtherMode = 0x00,
            AckDelayMs = ackDelayMs,
        };
        var link = new Ft847CatTransport(radio, replyTimeoutMs: 200, pollMs: pollMs, model: model);
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
            var bytes = Encoding.ASCII.GetBytes(commands);
            link.Write(bytes, 0, bytes.Length);
            for (int i = 0; i < 300 && replies.Count < expected; i++)
                await Task.Delay(10);
            await Task.Delay(80);
            return replies.ToList();
        }
        finally
        {
            link.DataReceived -= OnData;
        }
    }

    [Theory]
    [InlineData("FT-817")]
    [InlineData("FT-818")]
    [InlineData("FT-857")]
    [InlineData("FT-897")]
    public async Task ALateAck_DoesNotShiftTheNextFrequencyRead(string model)
    {
        // The ack arrives 60 ms after the set: well after the next read
        // would have gone out if YWC didn't wait for it.
        var (link, radio) = Open(model, ackDelayMs: 60);
        var replies = await SendAsync(link, "FA432039000;FA;MD0;", 2);
        Assert.Equal(new[] { "FA432039000;", "MD02;" }, replies);
        Assert.Equal(432_039_000, radio.FrequencyHz);
        link.Dispose();
    }

    [Fact]
    public async Task ModeSet_ThenRead_GetsTheModeNotTheAck()
    {
        var (link, radio) = Open(ackDelayMs: 60);
        var replies = await SendAsync(link, "MD03;MD0;FA;", 2);
        Assert.Equal(new[] { "MD03;", "FA014074000;" }, replies);
        Assert.Equal(0x02, radio.Mode);   // CW
        link.Dispose();
    }

    [Fact]
    public async Task ThePollRuns_WhileTheMeterPollKeepsTheQueueBusy()
    {
        var (link, radio) = Open(pollMs: 300);
        await SendAsync(link, "AI1;", 0);

        var reports = new ConcurrentQueue<string>();
        void OnData(string text) => reports.Enqueue(text);
        link.DataReceived += OnData;
        try
        {
            await Task.Delay(400);       // the first poll reports what's there
            radio.Mode = 0x00;           // LSB, changed on the radio
            radio.OnVfoB = true;         // and A/B pressed

            // Meter reads every 100 ms, faster than the poll interval.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                var bytes = Encoding.ASCII.GetBytes("RM5;SM0;");
                link.Write(bytes, 0, bytes.Length);
                await Task.Delay(100);
            }
        }
        finally
        {
            link.DataReceived -= OnData;
        }

        Assert.Contains("VS1;", reports);
        link.Dispose();
    }

    [Theory]
    [InlineData(0, "RM5000000;")]
    [InlineData(4, "RM5068000;")]
    [InlineData(8, "RM5136000;")]
    public async Task Ft817_PowerComesFromBd_WhenTransmitting(int bd, string expected)
    {
        var (link, radio) = Open();
        radio.Transmitting = true;
        radio.PowerMeter = 15;   // the F7 bits say full scale: not what the FT-817 means
        radio.PowerBd = bd;
        Assert.Equal(new[] { expected }, await SendAsync(link, "RM5;", 1));
        Assert.Contains(radio.Received, b => b[4] == Ft847Cat.ReadTxMetering);
        link.Dispose();
    }

    [Fact]
    public async Task Ft817_Receiving_SendsNoBd()
    {
        var (link, radio) = Open();
        radio.PowerBd = 8;
        Assert.Equal(new[] { "RM5000000;" }, await SendAsync(link, "RM5;", 1));
        Assert.DoesNotContain(radio.Received.ToList(), b => b[4] == Ft847Cat.ReadTxMetering);
        link.Dispose();
    }

    [Fact]
    public async Task Ft857_PowerStillComesFromF7()
    {
        var (link, radio) = Open("FT-857");
        radio.Transmitting = true;
        radio.PowerMeter = 15;
        var reply = Assert.Single(await SendAsync(link, "RM5;", 1));
        Assert.NotEqual("RM5000000;", reply);
        Assert.DoesNotContain(radio.Received.ToList(), b => b[4] == Ft847Cat.ReadTxMetering);
        link.Dispose();
    }

    [Fact]
    public void PowerFromBd_IsTheTopNibble()
    {
        Assert.Equal(0, Ft847Cat.PowerFromBd(new byte[] { 0x0F, 0xFF }));
        Assert.Equal(136, Ft847Cat.PowerFromBd(new byte[] { 0x80, 0x00 }));
    }

    [Fact]
    public async Task Ft847_DoesNotWaitForAnAck()
    {
        // The FT-847 sends no ack. Waiting for one would cost the 200 ms
        // reply timeout on each of these five sets.
        var (link, radio) = Open("FT-847");
        await SendAsync(link, "FA;", 1);
        var sw = Stopwatch.StartNew();
        var replies = await SendAsync(link, "FA014075000;FA014076000;FA014077000;FA014078000;FA014079000;FA;", 1);
        Assert.Equal(new[] { "FA014079000;" }, replies);
        Assert.True(sw.ElapsedMilliseconds < 700, $"took {sw.ElapsedMilliseconds} ms");
        link.Dispose();
    }

    [Theory]
    [InlineData("FT-817", true)]
    [InlineData("FT-818", true)]
    [InlineData("FT-857", true)]
    [InlineData("FT-897", true)]
    [InlineData("FT-847", false)]
    public void AcksSets_OnlyOnTheFt8x7(string model, bool acks) => Assert.Equal(acks, Ft847Cat.AcksSets(model));
}
