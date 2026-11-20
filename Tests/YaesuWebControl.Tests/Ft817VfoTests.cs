using System.Collections.Concurrent;
using System.Text;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// The FT-817 and FT-818 keep the VFO they're on (A or B) in their EEPROM, and
// Ft847CatTransport reads it there (BB 0055, bit 0), swaps with 81, and
// answers VS / FA / FB / MD0 / MD1 like the FTdx10. These run against
// Ft847Simulator, so they prove the translation; Felix DF3FX's FT-817 proves
// the radio.
public class Ft817VfoTests
{
    private static (Ft847CatTransport Link, Ft847Simulator Radio) Open(string model = "FT-817", int pollMs = 5000)
    {
        var radio = new Ft847Simulator(model: model)
        {
            FrequencyHz = 14_074_000, Mode = 0x01,          // VFO A: 20 m USB
            OtherFrequencyHz = 7_074_000, OtherMode = 0x00, // VFO B: 40 m LSB
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
            if (commands.Length > 0)
            {
                var bytes = Encoding.ASCII.GetBytes(commands);
                link.Write(bytes, 0, bytes.Length);
            }
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

    private static List<byte[]> Snapshot(Ft847Simulator radio)
    {
        while (true)
        {
            try { return radio.Received.ToList(); }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }
    }

    private static int Count(Ft847Simulator radio, byte opcode) => Snapshot(radio).Count(b => b[4] == opcode);

    [Theory]
    [InlineData("FT-817")]
    [InlineData("FT-818")]
    public async Task Vs_ReadsTheVfoFromTheEeprom(string model)
    {
        var (link, radio) = Open(model);
        Assert.Equal(new[] { "VS0;" }, await SendAsync(link, "VS;", 1));

        radio.OnVfoB = true;   // the operator pressed A/B on the radio
        await Task.Delay(150); // past the 100 ms cache
        Assert.Equal(new[] { "VS1;" }, await SendAsync(link, "VS;", 1));

        var bb = Snapshot(radio).First(b => b[4] == Ft847Cat.ReadEeprom);
        Assert.Equal(new byte[] { 0x00, 0x55, 0, 0, 0xBB }, bb);
        link.Dispose();
    }

    [Fact]
    public void VfoFromEeprom_UsesBitZeroOfTheFirstByteOnly()
    {
        Assert.Equal(0, Ft847Cat.VfoFromEeprom(new byte[] { 0xA4, 0xFF }));
        Assert.Equal(1, Ft847Cat.VfoFromEeprom(new byte[] { 0xA5, 0x00 }));
    }

    [Fact]
    public async Task OnVfoA_FaIsLive_AndFbIsUnknownUntilSeen()
    {
        var (link, radio) = Open();
        Assert.Equal(new[] { "FA014074000;" }, await SendAsync(link, "FA;", 1));
        Assert.Empty(await SendAsync(link, "FB;", 1));
        Assert.Equal(0, Count(radio, Ft847Cat.ToggleVfo));
        link.Dispose();
    }

    [Fact]
    public async Task Vs1_SwapsOnce_ThenFbIsLive_AndFaIsRemembered()
    {
        var (link, radio) = Open();
        await SendAsync(link, "FA;MD0;", 2);

        await SendAsync(link, "VS1;", 0);
        Assert.True(radio.OnVfoB);
        Assert.Equal(1, Count(radio, Ft847Cat.ToggleVfo));

        Assert.Equal(new[] { "FB007074000;" }, await SendAsync(link, "FB;", 1));
        Assert.Equal(new[] { "MD11;" }, await SendAsync(link, "MD1;", 1));
        Assert.Equal(new[] { "FA014074000;" }, await SendAsync(link, "FA;", 1));
        Assert.Equal(new[] { "MD02;" }, await SendAsync(link, "MD0;", 1));
        Assert.Equal(new[] { "VS1;" }, await SendAsync(link, "VS;", 1));

        // Back to A: one more swap.
        await SendAsync(link, "VS0;", 0);
        Assert.False(radio.OnVfoB);
        Assert.Equal(2, Count(radio, Ft847Cat.ToggleVfo));
        link.Dispose();
    }

    [Fact]
    public async Task Vs_ToTheVfoAlreadyOn_SendsNoSwap()
    {
        var (link, radio) = Open();
        await SendAsync(link, "VS0;", 0);
        Assert.Equal(0, Count(radio, Ft847Cat.ToggleVfo));
        link.Dispose();
    }

    [Fact]
    public async Task Vs_NeverSwapsWhileTransmitting()
    {
        var (link, radio) = Open();
        radio.Transmitting = true;
        await SendAsync(link, "VS1;", 0);
        Assert.False(radio.OnVfoB);
        Assert.Equal(0, Count(radio, Ft847Cat.ToggleVfo));
        link.Dispose();
    }

    [Fact]
    public async Task ASet_ForTheVfoTheRadioIsNotOn_IsDropped()
    {
        var (link, radio) = Open();
        radio.OnVfoB = true;
        await SendAsync(link, "FA021074000;MD01;", 0);
        Assert.Equal(0, Count(radio, Ft847Cat.SetMainFrequency));
        Assert.Equal(0, Count(radio, Ft847Cat.SetMainMode));

        await SendAsync(link, "FB010136000;", 0);
        Assert.Equal(10_136_000, radio.FrequencyHz);
        link.Dispose();
    }

    [Fact]
    public async Task Poll_ReportsAVfoChangeMadeOnTheRadio()
    {
        var (link, radio) = Open(pollMs: 50);
        var first = await SendAsync(link, "AI1;", 2);
        Assert.Contains("VS0;", first);

        // The operator presses A/B on the radio.
        (radio.FrequencyHz, radio.OtherFrequencyHz) = (radio.OtherFrequencyHz, radio.FrequencyHz);
        (radio.Mode, radio.OtherMode) = (radio.OtherMode, radio.Mode);
        radio.OnVfoB = true;

        var seen = new List<string>();
        for (int i = 0; i < 20 && !(seen.Contains("VS1;") && seen.Contains("FB007074000;")); i++)
            seen.AddRange(await SendAsync(link, "", 0));
        Assert.Contains("VS1;", seen);
        Assert.Contains("FB007074000;", seen);
        Assert.Contains("MD11;", seen);
        Assert.DoesNotContain(seen, s => s.StartsWith("FA"));
        link.Dispose();
    }

    // YWC's start-up ping-pong (VS1; ... VS0;) fills VFO B's slot, and the
    // next poll reports it although the radio is back on A.
    [Fact]
    public async Task Poll_ReportsTheOtherVfo_AfterAPingPong()
    {
        var (link, radio) = Open(pollMs: 50);
        await SendAsync(link, "VS1;", 0);
        await SendAsync(link, "VS0;", 0);
        Assert.False(radio.OnVfoB);

        var seen = await SendAsync(link, "AI1;", 4);
        for (int i = 0; i < 10 && !seen.Contains("FB007074000;"); i++)
            seen.AddRange(await SendAsync(link, "", 0));
        Assert.Contains("VS0;", seen);
        Assert.Contains("FA014074000;", seen);
        Assert.Contains("FB007074000;", seen);
        Assert.Contains("MD11;", seen);
        link.Dispose();
    }

    [Fact]
    public async Task ARadioThatWontReadItsEeprom_FallsBackToOneVfo()
    {
        var (link, radio) = Open();
        radio.AnswersEeprom = false;
        for (int i = 0; i < 4; i++)
        {
            await SendAsync(link, "FA;", 1);
            await Task.Delay(120);
        }
        Assert.Equal(new[] { "VS0;" }, await SendAsync(link, "VS;", 1));

        await SendAsync(link, "VS1;", 0);
        Assert.Equal(0, Count(radio, Ft847Cat.ToggleVfo));

        await SendAsync(link, "FA021074000;", 0);
        Assert.Equal(21_074_000, radio.FrequencyHz);
        link.Dispose();
    }

    [Theory]
    [InlineData("FT-857")]
    [InlineData("FT-897")]
    [InlineData("FT-847")]
    public async Task OtherRadios_NeverReadTheEeprom_OrSwap(string model)
    {
        var (link, radio) = Open(model);
        await SendAsync(link, "FA;FB;VS;VS1;MD0;MD1;", 3);
        Assert.Equal(0, Count(radio, Ft847Cat.ReadEeprom));
        Assert.Equal(0, Count(radio, Ft847Cat.ToggleVfo));
        link.Dispose();
    }

    [Theory]
    [InlineData("FT-817")]
    [InlineData("FT-818")]
    public async Task EepromWrite_IsNeverSent(string model)
    {
        var (link, radio) = Open(model, pollMs: 50);
        await SendAsync(link, "AI1;VS;VS1;FA;FB;MD0;MD1;FB014074000;MD02;VS0;FA007000000;", 4);
        await Task.Delay(300);
        Assert.False(radio.EepromWritten);
        Assert.DoesNotContain(Snapshot(radio), b => b[4] == Ft847Cat.WriteEeprom);
        link.Dispose();
    }
}
