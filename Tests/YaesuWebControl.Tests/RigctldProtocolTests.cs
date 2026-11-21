using System.Text;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// The rigctl conversation as Hamlib's NET rigctl backend (netrigctl.c) sees
// it. Found in IWC (D#51), where fldigi kept hanging up after 4.5 s.
public sealed class RigctldProtocolTests
{
    // Reads a protocol-0 dump_state the way netrigctl_open does, line by line.
    // Throws if it runs out of lines; fails if any are left over.
    private static (int RxRanges, int TxRanges) ParseLikeHamlib(string dump)
    {
        var lines = new Queue<string>(dump.Split('\n'));
        string Next() => lines.Count > 0 ? lines.Dequeue() : throw new InvalidOperationException("ran out of lines");

        Assert.Equal("0", Next());          // protocol version
        Assert.Equal("2", Next());          // rig model
        int.Parse(Next());                  // ITU region: a plain number

        int Ranges()
        {
            var n = 0;
            while (true)
            {
                var f = Next().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(7, f.Length);
                if (double.Parse(f[0]) == 0 && double.Parse(f[1]) == 0) return n;
                n++;
            }
        }
        var rx = Ranges();
        var tx = Ranges();

        void Pairs()
        {
            while (true)
            {
                var f = Next().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(2, f.Length);
                if (f[0] == "0" && f[1] == "0") return;
            }
        }
        Pairs();                            // tuning steps
        Pairs();                            // filters
        for (int i = 0; i < 12; i++) Next(); // max_rit .. has_set_parm

        Assert.Empty(lines);
        return (rx, tx);
    }

    [Fact]
    public void DumpState_ParsesLineForLineLikeHamlib_KeepingTheHfRange()
    {
        var dump = RigctldServer.GetDumpState();
        Assert.Equal((2, 2), ParseLikeHamlib(dump));
        Assert.StartsWith("1800000.000000 30000000.000000 ", dump.Split('\n')[3]);
    }

    [Fact]
    public void TakeLines_KeepsACommandSplitAcrossTwoReads()
    {
        var pending = new StringBuilder();
        Assert.Equal(new[] { "f" }, RigctldServer.TakeLines(pending, "f\nF 1407"));
        Assert.Equal(new[] { "F 14074000", "m" }, RigctldServer.TakeLines(pending, "4000\nm\n"));
        Assert.Equal(0, pending.Length);
    }

    [Fact]
    public void TakeLines_DropsAnEndlessLineInsteadOfGrowingForever()
    {
        var pending = new StringBuilder();
        for (int i = 0; i < 10; i++)
            Assert.Empty(RigctldServer.TakeLines(pending, new string('x', 1000)));
        Assert.True(pending.Length <= 5000);
    }

    [Theory]
    [InlineData("ID;")]
    [InlineData("FA;")]
    [InlineData("FA;MD0;")]
    [InlineData("FA014074000;")]
    public void LooksLikeYaesuCat_SpotsAProgramSetToTheRadiosOwnModel(string sent)
        => Assert.True(RigctldServer.LooksLikeYaesuCat(sent));

    [Theory]
    [InlineData("\\get_powerstat\n\\chk_vfo\n\\dump_state\n")]
    [InlineData("f\nm\n")]
    [InlineData("F 14074000.000000\n")]
    [InlineData("\\get_fr")]   // a rigctl command split across two reads
    [InlineData("M PKTUSB")]
    public void LooksLikeYaesuCat_IgnoresRigctlText(string sent)
        => Assert.False(RigctldServer.LooksLikeYaesuCat(sent));

    // #387: Log4OM, set to Hamlib NET rigctl with VFO MODE ticked, sends
    // ";V ?" and then polls "+\\get_vfo_info VFOA" and "VFOB". The expected
    // replies are what Hamlib 4.6.2's own rigctld (Log4OM's bundled copy,
    // dummy rig) sent back on 2026-11-21.

    // Answers like a rig on 7.074 MHz USB, VFO B on 14.074, recording calls.
    private static (Func<string, string[], string?, Task<string>> Plain, List<string> Calls) FakeRig()
    {
        var calls = new List<string>();
        Task<string> Plain(string name, string[] args, string? vfo)
        {
            calls.Add($"{name}|{string.Join(' ', args)}|{vfo}");
            var onB = RigctldServer.IsVfoB(vfo ?? (name == "get_vfo_info" ? args.FirstOrDefault() : null));
            return Task.FromResult(name switch
            {
                "get_freq"     => onB ? "14074000" : "7074000",
                "get_mode"     => "USB\n0",
                "set_vfo"      => args.FirstOrDefault() == "?" ? "VFOA VFOB \nRPRT 0" : "RPRT 0",
                "get_vfo_info" => RigctldServer.VfoInfo(onB ? "14074000" : "7074000", "USB", false),
                "get_split_vfo"=> "0\nVFOA",
                "get_level"    => "-54",
                "set_freq"     => onB ? "RPRT -1" : "RPRT 0",
                _              => "RPRT -1",
            });
        }
        return (Plain, calls);
    }

    [Theory]
    [InlineData(";V ?",                 "set_vfo: ?;VFOA VFOB \nRPRT 0")]
    [InlineData("+\\get_vfo_info VFOA", "get_vfo_info: VFOA\nFreq: 7074000\nMode: USB\nWidth: 0\nSplit: 0\nSatMode: 0\nRPRT 0")]
    [InlineData("+\\get_vfo_info VFOB", "get_vfo_info: VFOB\nFreq: 14074000\nMode: USB\nWidth: 0\nSplit: 0\nSatMode: 0\nRPRT 0")]
    [InlineData(";f",                   "get_freq:;Frequency: 7074000;RPRT 0")]
    [InlineData(";\\get_freq",          "get_freq:;Frequency: 7074000;RPRT 0")]
    [InlineData("+f",                   "get_freq:\nFrequency: 7074000\nRPRT 0")]
    [InlineData("|f",                   "get_freq:|Frequency: 7074000|RPRT 0")]
    [InlineData(",f",                   "get_freq:,Frequency: 7074000,RPRT 0")]
    [InlineData(";m",                   "get_mode:;Mode: USB;Passband: 0;RPRT 0")]
    [InlineData(";s",                   "get_split_vfo:;Split: 0;TX VFO: VFOA;RPRT 0")]
    [InlineData(";l STRENGTH",          "get_level: STRENGTH;-54;RPRT 0")]
    [InlineData(";F 14074000",          "set_freq: 14074000;RPRT 0")]
    [InlineData(";t",                   "get_ptt:;RPRT -1")]
    [InlineData(";q",                   "RPRT 0")]
    public async Task ExtendedCommands_AreAnsweredInHamlibsFormat(string line, string expected)
        => Assert.Equal(expected, await RigctldServer.AnswerLineAsync(line, FakeRig().Plain));

    [Theory]
    [InlineData("f", "7074000")]
    [InlineData("\\get_freq", "7074000")]
    [InlineData("V ?", "VFOA VFOB \nRPRT 0")]
    [InlineData("\\get_vfo_info VFOA", "7074000\nUSB\n0\n0\n0")]
    [InlineData("s", "0\nVFOA")]
    public async Task PlainCommands_AreUnchanged(string line, string expected)
        => Assert.Equal(expected, await RigctldServer.AnswerLineAsync(line, FakeRig().Plain));

    // Log4OM tunes with "F VFOA 8074000": the VFO comes first in VFO mode.
    [Theory]
    [InlineData("F VFOA 8074000", "set_freq|8074000|VFOA")]
    [InlineData("\\set_freq VFOA 8074000", "set_freq|8074000|VFOA")]
    [InlineData("f VFOA", "get_freq||VFOA")]
    [InlineData("M VFOA USB 2400", "set_mode|USB 2400|VFOA")]
    [InlineData("F 14074000", "set_freq|14074000|")]
    [InlineData("S 1 VFOB", "set_split_vfo|1 VFOB|")]
    [InlineData("V VFOB", "set_vfo|VFOB|")]
    [InlineData("\\get_vfo_info VFOB", "get_vfo_info|VFOB|")]
    public async Task TheVfoArgument_IsTakenOffBeforeTheValue(string line, string call)
    {
        var (plain, calls) = FakeRig();
        await RigctldServer.AnswerLineAsync(line, plain);
        Assert.Equal(call, Assert.Single(calls));
    }

    [Fact]
    public async Task SettingVfoB_IsRefused_NotAppliedToA()
        => Assert.Equal("RPRT -1", await RigctldServer.AnswerLineAsync("F VFOB 14074000", FakeRig().Plain));

    // Hamlib's one-letter names, which netrigctl (WSJT-X, fldigi) sends.
    [Theory]
    [InlineData("s", "get_split_vfo")] [InlineData("S 1 VFOB", "set_split_vfo")]
    [InlineData("i", "get_split_freq")] [InlineData("I 14076000", "set_split_freq")]
    [InlineData("x", "get_split_mode")] [InlineData("X USB 2400", "set_split_mode")]
    [InlineData("j", "get_rit")] [InlineData("J 100", "set_rit")]
    [InlineData("z", "get_xit")] [InlineData("Z 100", "set_xit")]
    [InlineData("_", "get_info")]
    public async Task ShortLetters_MeanWhatTheyMeanInHamlib(string line, string name)
    {
        var (plain, calls) = FakeRig();
        await RigctldServer.AnswerLineAsync(line, plain);
        Assert.StartsWith(name + "|", Assert.Single(calls));
    }

    [Theory]
    [InlineData("r")] [InlineData("c")] [InlineData("y")]
    public async Task LettersWithNoCommandHere_GetRprtMinus1(string line)
        => Assert.Equal("RPRT -1", await RigctldServer.AnswerLineAsync(line, FakeRig().Plain));
}
