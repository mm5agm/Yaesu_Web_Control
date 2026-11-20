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
}
