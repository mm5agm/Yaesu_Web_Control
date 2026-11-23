using System.Collections.Concurrent;
using System.Text;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// The FT-817, FT-818, FT-857 and FT-897 use the FT-847's five-byte CAT, with
// three differences that matter (Ft847Cat): 00 and 80 are LOCK ON and OFF, not
// CAT ON and OFF; the meters are four bits; and data goes out in DIG mode.
// These run against Ft847Simulator acting as each radio, so they prove the
// translation; only a real radio proves the radio.
public class Ft8x7Tests
{
    private static (Ft847CatTransport Link, Ft847Simulator Radio) Open(string model, int pollMs = 5000)
    {
        var radio = new Ft847Simulator(model: model);
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

    // The simulator adds to Received on the link's thread; copy it before reading.
    private static List<byte[]> ReceivedSnapshot(Ft847Simulator radio)
    {
        while (true)
        {
            try { return radio.Received.ToList(); }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }
    }

    // A set gets no reply, so SendAsync can't know when the radio has it. On a
    // slow CI runner the link can still be busy with its first poll, so wait
    // (up to 2 s) for the radio to show the change instead of a fixed time.
    private static async Task WaitUntilAsync(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++)
            await Task.Delay(10);
    }

    public static TheoryData<string> Models => new() { "FT-817", "FT-818", "FT-857", "FT-897" };

    [Theory]
    [MemberData(nameof(Models))]
    public void OpenAndClose_NeverSendLockOnOrOff(string model)
    {
        var (link, radio) = Open(model);
        link.Close();
        Assert.False(radio.Locked);
        Assert.DoesNotContain(radio.Received, b => b[4] == 0x00 || b[4] == 0x80);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public async Task FrequencyAndMode_ReadAndSet(string model)
    {
        var (link, radio) = Open(model);
        radio.FrequencyHz = 145_500_000;
        radio.Mode = 0x08;
        Assert.Equal(new[] { "FA145500000;", "MD04;" }, await SendAsync(link, "FA;MD0;", 2));

        await SendAsync(link, "FA007074000;MD01;", 0);
        await WaitUntilAsync(() => radio.FrequencyHz == 7_074_000 && radio.Mode == 0x00);
        Assert.Equal(7_074_000, radio.FrequencyHz);
        Assert.Equal(0x00, radio.Mode);
        Assert.False(radio.Locked);
        link.Dispose();
    }

    [Theory]
    [MemberData(nameof(Models))]
    public async Task IdReply_NamesTheRadio(string model)
    {
        var (link, _) = Open(model);
        var replies = await SendAsync(link, "ID;PS;", 2);
        Assert.Equal("ID0" + model[3..] + ";", replies[0]);
        Assert.Equal("PS1;", replies[1]);
        link.Dispose();
    }

    [Theory]
    [MemberData(nameof(Models))]
    public async Task Meters_AreFourBits(string model)
    {
        var (link, radio) = Open(model);
        radio.SMeter = 15;
        Assert.Equal(new[] { "SM0255;" }, await SendAsync(link, "SM0;", 1));
        radio.SMeter = 0;
        Assert.Equal(new[] { "SM0000;" }, await SendAsync(link, "SM0;", 1));

        // Full scale: the FT-857/897 from the F7 bits, the FT-817/818 from BD
        // (Ft817AckTests), as Hamlib reads them.
        radio.Transmitting = true;
        radio.PowerMeter = 15;
        radio.PowerBd = 15;
        Assert.Equal(new[] { "TX2;", "RM5255000;" }, await SendAsync(link, "TX;RM5;", 2));
        link.Dispose();
    }

    [Fact]
    public async Task Receiving_ThePowerMeterReadsZero()
    {
        var (link, radio) = Open("FT-817");
        radio.PowerMeter = 15;   // bits set while receiving are not a power reading
        Assert.Equal(new[] { "RM5000000;" }, await SendAsync(link, "RM5;", 1));
        link.Dispose();
    }

    [Theory]
    [InlineData('C')] [InlineData('8')] [InlineData('9')] [InlineData('6')] [InlineData('E')]
    public async Task DataModes_GoOutAsDig_AndReadBackAsAsked(char md)
    {
        var (link, radio) = Open("FT-857");
        await SendAsync(link, $"MD0{md};", 0);
        await WaitUntilAsync(() => radio.Mode == Ft847Cat.DigMode);
        Assert.Equal(Ft847Cat.DigMode, radio.Mode);
        Assert.Equal(new[] { $"MD0{md};" }, await SendAsync(link, "MD0;", 1));
        link.Dispose();
    }

    [Fact]
    public void DigFromTheFrontPanel_ReadsAsDataU() =>
        Assert.Equal('C', Ft847Cat.MdFromMode(Ft847Cat.DigMode, "FT-817"));

    [Fact]
    public void DataFm_GoesOutAsPacket() =>
        Assert.Equal(Ft847Cat.PktMode, Ft847Cat.ModeFromMd('A', "FT-897"));

    [Fact]
    public void FmNarrow_IsOnlyOnThe857And897()
    {
        Assert.Equal((byte)0x88, Ft847Cat.ModeFromMd('B', "FT-857"));
        Assert.Equal((byte)0x88, Ft847Cat.ModeFromMd('B', "FT-897"));
        Assert.Equal((byte)0x08, Ft847Cat.ModeFromMd('B', "FT-817"));
        Assert.Equal((byte)0x08, Ft847Cat.ModeFromMd('B', "FT-818"));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public async Task PttOn_IsNeverSent_AndPttOffIs(string model)
    {
        var (link, radio) = Open(model);
        await SendAsync(link, "TX1;", 0);
        Assert.DoesNotContain(radio.Received, b => b[4] == 0x08);
        await SendAsync(link, "TX0;", 0);
        await WaitUntilAsync(() => ReceivedSnapshot(radio).Any(b => b[4] == Ft847Cat.PttOff));
        Assert.Contains(ReceivedSnapshot(radio), b => b[4] == Ft847Cat.PttOff);
        link.Dispose();
    }

    [Fact]
    public async Task AboveSeventyCm_IsNotSent()
    {
        var (link, radio) = Open("FT-817");
        await SendAsync(link, "FA500000000;", 0);
        Assert.DoesNotContain(radio.Received, b => b[4] == Ft847Cat.SetMainFrequency);
        link.Dispose();
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Capabilities_TreatThemAsLimitedCatVhfUhfRadios(string model)
    {
        Assert.True(RadioCapabilities.UsesFiveByteCat(model));
        Assert.True(RadioCapabilities.HasLimitedCat(model));
        Assert.True(RadioCapabilities.HasTwoMetresAndSeventyCm(model));
        Assert.True(RadioCapabilities.IsTunableFrequency(model, 432_100_000));
        Assert.False(RadioCapabilities.IsTunableFrequency(model, 500_000_000));
        Assert.True(RadioCapabilities.IsSingleReceiver(model));
        Assert.False(RadioCapabilities.HasAntennaSelector(model));
        // The FT-817 and FT-818 read their VFO from EEPROM; the FT-857 and FT-897 don't yet.
        Assert.Equal(model is "FT-817" or "FT-818", RadioCapabilities.CanSelectRxVfo(model));
        Assert.False(RadioCapabilities.PollsTemperature(model));
        Assert.False(RadioCapabilities.HasRoofingFilterCat(model));
    }

    [Fact]
    public void Power_FollowsEachRadio()
    {
        Assert.Equal(5, RadioCapabilities.MaxPowerWattsAt("FT-817", 432_100_000));
        Assert.Equal(6, RadioCapabilities.MaxPowerWattsAt("FT-818", 14_074_000));
        Assert.Equal(100, RadioCapabilities.MaxPowerWattsAt("FT-857", 14_074_000));
        Assert.Equal(50, RadioCapabilities.MaxPowerWattsAt("FT-897", 144_300_000));
        Assert.Equal(20, RadioCapabilities.MaxPowerWattsAt("FT-857", 432_100_000));
        Assert.Equal(50, RadioCapabilities.MaxPowerWattsAt("FT-847", 432_100_000));
    }
}
