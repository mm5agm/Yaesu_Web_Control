using System.Buffers.Binary;
using System.Text;
using Yaesu_Web_Control.Services.Wsjtx;

namespace YaesuWebControl.Tests;

// Issue #332: the big-tile page calls stations by sending WSJT-Z a Reply that
// echoes one of its decodes, stops transmitting with HaltTx, and switches Auto
// Call with a schema-3 Configure. The byte layouts here are built by hand from
// WSJT-Z's NetworkMessage.hpp, not by the code under test, so a mistake in the
// writer can't hide behind a matching mistake in the reader. These are still
// only unit tests: whether WSJT-Z acts on the messages needs a real WSJT-Z.
public sealed class WsjtxBigTileTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    // --- A tiny independent QDataStream writer for the expected bytes ---

    private sealed class Bytes
    {
        private readonly List<byte> _b = new();
        public Bytes U32(uint v) { var x = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(x, v); _b.AddRange(x); return this; }
        public Bytes U64(ulong v) { var x = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(x, v); _b.AddRange(x); return this; }
        public Bytes F64(double v) { var x = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(x, v); _b.AddRange(x); return this; }
        public Bytes B(bool v) { _b.Add(v ? (byte)1 : (byte)0); return this; }
        public Bytes U8(byte v) { _b.Add(v); return this; }
        public Bytes S(string s) { var x = Encoding.UTF8.GetBytes(s); U32((uint)x.Length); _b.AddRange(x); return this; }
        public Bytes Header(uint schema, uint type, string id) => U32(0xADBCCBDA).U32(schema).U32(type).S(id);
        public byte[] ToArray() => _b.ToArray();
    }

    private static byte[] DecodeBytes(string message, int snr = -12, double dt = 0.3, uint df = 1234, uint timeMs = 43_215_000)
        => new Bytes().Header(2, 2, "WSJT-X - Z")
            .B(true).U32(timeMs).U32((uint)snr).F64(dt).U32(df).S("~").S(message).B(false).B(false)
            .ToArray();

    // --- Decode ---

    [Fact]
    public void Decode_ParsesEveryField()
    {
        var d = WsjtxProtocol.TryParseDecode(DecodeBytes("CQ K1ABC FN42", snr: -19, dt: -0.4, df: 875));
        Assert.NotNull(d);
        Assert.Equal("WSJT-X - Z", d!.Id);
        Assert.True(d.IsNew);
        Assert.Equal(43_215_000u, d.TimeMs);
        Assert.Equal(-19, d.Snr);
        Assert.Equal(-0.4, d.DeltaTime);
        Assert.Equal(875u, d.DeltaFrequency);
        Assert.Equal("~", d.Mode);
        Assert.Equal("CQ K1ABC FN42", d.Message);
        Assert.False(d.LowConfidence);
    }

    [Fact]
    public void Decode_WithoutTheLaterFlags_StillParses()
    {
        var bytes = new Bytes().Header(2, 2, "WSJT-X")
            .B(true).U32(1000).U32(unchecked((uint)-5)).F64(0.1).U32(500).S("~").S("CQ DL2XY JO62")
            .ToArray();
        var d = WsjtxProtocol.TryParseDecode(bytes);
        Assert.NotNull(d);
        Assert.Equal("CQ DL2XY JO62", d!.Message);
        Assert.Equal(-5, d.Snr);
    }

    [Fact]
    public void Decode_Truncated_IsRejected()
    {
        var bytes = DecodeBytes("CQ K1ABC FN42");
        Assert.Null(WsjtxProtocol.TryParseDecode(bytes[..30]));
    }

    [Fact]
    public void Status_ReadsTxEnabledAndMyCall()
    {
        var bytes = new Bytes().Header(2, 1, "WSJT-X - Z")
            .U64(14_074_000).S("FT8").S("K1ABC").S("-10").S("FT8")
            .B(true).B(false).B(true).U32(1500).U32(1200).S("MM5AGM").S("IO85").S("FN42")
            .ToArray();
        var s = WsjtxProtocol.TryParseStatus(bytes);
        Assert.NotNull(s);
        Assert.Equal(14_074_000, s!.DialFrequency);
        Assert.Equal("FT8", s.Mode);
        Assert.Equal("K1ABC", s.DxCall);
        Assert.True(s.TxEnabled);
        Assert.False(s.Transmitting);
        Assert.True(s.Decoding);
        Assert.Equal("MM5AGM", s.DeCall);
    }

    [Fact]
    public void Status_Full_ReadsTheTxMessage()
    {
        var bytes = new Bytes().Header(3, 1, "WSJT-X - Z")
            .U64(14_074_000).S("FT8").S("K1ABC").S("-10").S("FT8")
            .B(true).B(true).B(false).U32(1500).U32(1200).S("MM5AGM").S("IO85").S("FN42")
            .B(false).S("").B(false).U8(0).U32(0).U32(15).S("Default").S("K1ABC MM5AGM R-10  ")
            .ToArray();
        var s = WsjtxProtocol.TryParseStatus(bytes);
        Assert.NotNull(s);
        Assert.Equal("MM5AGM", s!.DeCall);
        Assert.Equal("K1ABC MM5AGM R-10", s.TxMessage);
        Assert.Equal("IO85", s.DeGrid);
    }

    [Fact]
    public void Status_WithoutTxMessage_LeavesItEmpty()
    {
        var bytes = new Bytes().Header(2, 1, "WSJT-X")
            .U64(14_074_000).S("FT8").S("K1ABC").S("-10").S("FT8")
            .B(true).B(false).B(true).U32(1500).U32(1200).S("MM5AGM").S("IO85").S("FN42")
            .ToArray();
        var s = WsjtxProtocol.TryParseStatus(bytes);
        Assert.NotNull(s);
        Assert.Equal("MM5AGM", s!.DeCall);
        Assert.Equal("", s.TxMessage);
    }

    [Fact]
    public void Status_Short_StillGivesTheEarlyFields()
    {
        var bytes = new Bytes().Header(2, 1, "WSJT-X")
            .U64(7_074_000).S("FT8").S("").S("").S("FT8").B(false).B(true)
            .ToArray();
        var s = WsjtxProtocol.TryParseStatus(bytes);
        Assert.NotNull(s);
        Assert.Equal(7_074_000, s!.DialFrequency);
        Assert.True(s.Transmitting);
        Assert.Equal("", s.DeCall);
        Assert.Equal("", s.DeGrid);
    }

    // --- Outgoing messages ---

    [Fact]
    public void Reply_EchoesTheDecodeExactly()
    {
        var d = WsjtxProtocol.TryParseDecode(DecodeBytes("W1XYZ K1ABC RR73", snr: -7, dt: 0.2, df: 1610, timeMs: 50_000_000))!;
        var expected = new Bytes().Header(3, 4, "WSJT-X - Z")
            .U32(50_000_000).U32(unchecked((uint)-7)).F64(0.2).U32(1610).S("~").S("W1XYZ K1ABC RR73").B(false).U8(0)
            .ToArray();
        Assert.Equal(expected, WsjtxProtocol.BuildReply(d, "WSJT-X - Z"));
    }

    [Fact]
    public void HaltTx_Now()
    {
        var expected = new Bytes().Header(3, 8, "WSJT-X - Z").B(false).ToArray();
        Assert.Equal(expected, WsjtxProtocol.BuildHaltTx("WSJT-X - Z", autoTxOnly: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Configure_ChangesOnlyAutoCall_AndSaysSchema3(bool on)
    {
        var expected = new Bytes().Header(3, 15, "WSJT-X - Z")
            .S("").U32(uint.MaxValue).S("").B(false).U32(uint.MaxValue).U32(uint.MaxValue)
            .S("").S("").B(false)
            .B(false)   // Auto CQ always off
            .B(on)      // Auto Call
            .ToArray();
        Assert.Equal(expected, WsjtxProtocol.BuildAutoConfigure("WSJT-X - Z", autoCq: false, autoCall: on));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Configure_AutoCq_TurnsAutoCallOff(bool on)
    {
        var expected = new Bytes().Header(3, 15, "WSJT-X - Z")
            .S("").U32(uint.MaxValue).S("").B(false).U32(uint.MaxValue).U32(uint.MaxValue)
            .S("").S("").B(false)
            .B(on)      // Auto CQ
            .B(false)   // Auto Call always off
            .ToArray();
        Assert.Equal(expected, WsjtxProtocol.BuildAutoConfigure("WSJT-X - Z", autoCq: on, autoCall: false));
    }

    // The Call CQ tile is there and Stop transmit turns Auto CQ off as well as halting.
    [Fact]
    public void Ft8Tiles_CallCqTile_AndStopTurnsAutoOff()
    {
        string root = FindRepoRoot();
        string page = File.ReadAllText(Path.Combine(root, "Pages", "Ft8Tiles.cshtml"));
        Assert.Contains("id=\"ftCallCq\" aria-label=\"Call CQ\"", page);
        string js = File.ReadAllText(Path.Combine(root, "wwwroot", "js", "ft8-tiles", "ft8-tiles-page.js"));
        int stop = js.IndexOf("el(\"ftStop\")", StringComparison.Ordinal);
        int next = js.IndexOf("el(\"ftAutoOn\")", stop, StringComparison.Ordinal);
        string stopBody = js.Substring(stop, next - stop);
        Assert.Contains("/api/wsjtx/halt", stopBody);
        Assert.Contains("\"/api/wsjtx/autocq\", { on: false }", stopBody);
    }

    // Call CQ waits until just after the next period starts, so the first CQ
    // goes out in full. T0 is 12:00:00 UTC, a period start for FT8 and FT4.
    [Theory]
    [InlineData(0.0, 15, 0.3)]     // right at the start: 0.3 s in, this period
    [InlineData(0.2, 15, 0.1)]     // still before 0.3 s in
    [InlineData(0.3, 15, 15.0)]    // exactly 0.3 s in: next period
    [InlineData(4.0, 15, 11.3)]    // part-way through: next period
    [InlineData(14.9, 15, 0.4)]
    [InlineData(4.0, 7.5, 3.8)]    // FT4
    public void DelayToPeriodStart_WaitsForTheNextPeriod(double secondsIn, double period, double expected)
    {
        var delay = WsjtxCallList.DelayToPeriodStart(T0.AddSeconds(secondsIn), period, 0.3);
        Assert.Equal(expected, delay.TotalSeconds, 3);
    }

    // A Call CQ still waiting for its period is cancelled by Stop transmit,
    // Auto Call and Auto CQ off, so it can't start after the operator stopped it.
    [Fact]
    public void WaitingCallCq_IsCancelledByStopAndAutoCall()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "Services", "WsjtxUdpService.cs"));
        foreach (string method in new[] { "SendHaltTxAsync(", "SendAutoCallAsync(", "SendAutoCqAsync(" })
        {
            int at = src.IndexOf("public " , src.IndexOf(method, StringComparison.Ordinal) - 60, StringComparison.Ordinal);
            int body = src.IndexOf('{', at);
            int firstStatement = src.IndexOf(';', body);
            Assert.Contains("CancelPendingCq()", src.Substring(body, firstStatement - body + 1));
        }
    }

    // --- Who can be called ---

    [Theory]
    [InlineData("CQ K1ABC FN42", "K1ABC", CallKind.Cq, "FN42")]
    [InlineData("CQ DX VK3ABC QF22", "VK3ABC", CallKind.Cq, "QF22")]
    [InlineData("CQ POTA W5DD EM12", "W5DD", CallKind.Cq, "EM12")]
    [InlineData("CQ 290 K1ABC FN42", "K1ABC", CallKind.Cq, "FN42")]
    [InlineData("CQ JA1AA", "JA1AA", CallKind.Cq, "")]
    [InlineData("QRZ EA7QQ IM76", "EA7QQ", CallKind.Cq, "IM76")]
    [InlineData("CQ <PJ4/K1ABC>", "PJ4/K1ABC", CallKind.Cq, "")]
    [InlineData("W1XYZ PY2XYZ RR73", "PY2XYZ", CallKind.Finished, "")]
    [InlineData("W1XYZ DL2XY 73", "DL2XY", CallKind.Finished, "")]
    [InlineData("W1XYZ VE3ZZZ RRR", "VE3ZZZ", CallKind.Finished, "")]
    [InlineData("MM5AGM OH6AA KP20", "OH6AA", CallKind.CallingYou, "KP20")]
    [InlineData("MM5AGM SP5CCC -15", "SP5CCC", CallKind.CallingYou, "")]
    public void Classify_FindsTheStation(string message, string call, CallKind kind, string grid)
    {
        var r = WsjtxCallList.Classify(message, "MM5AGM");
        Assert.NotNull(r);
        Assert.Equal(call, r!.Value.Call);
        Assert.Equal(kind, r.Value.Kind);
        Assert.Equal(grid, r.Value.Grid);
        Assert.False(r.Value.Busy);
    }

    [Theory]
    [InlineData("W1XYZ K1ABC FN42")]
    [InlineData("W1XYZ K1ABC -10")]
    [InlineData("W1XYZ K1ABC R-10")]
    public void Classify_StationInAQso_IsBusy(string message)
    {
        var r = WsjtxCallList.Classify(message, "MM5AGM");
        Assert.NotNull(r);
        Assert.Equal("K1ABC", r!.Value.Call);
        Assert.True(r.Value.Busy);
    }

    [Theory]
    [InlineData("CQ MM5AGM IO85")]      // my own CQ
    [InlineData("K1ABC MM5AGM RR73")]   // me finishing
    [InlineData("TNX 73 GL")]           // free text
    [InlineData("CQ")]
    public void Classify_Ignores(string message)
    {
        var r = WsjtxCallList.Classify(message, "MM5AGM");
        Assert.True(r is null || r.Value.Busy);
    }

    [Fact]
    public void CallList_KeepsTheLatestDecode_AndItsPlace()
    {
        var list = new WsjtxCallList { MyCall = "MM5AGM" };
        list.Add(Decode("CQ K1ABC FN42", -10), T0);
        list.Add(Decode("CQ DL2XY JO62", -5), T0);
        list.Add(Decode("W1XYZ K1ABC RR73", -8), T0.AddSeconds(15));

        var snap = list.Snapshot(T0.AddSeconds(16), TimeSpan.FromSeconds(33));
        Assert.Equal(new[] { "K1ABC", "DL2XY" }, snap.Select(e => e.Call));
        Assert.Equal(CallKind.Finished, snap[0].Kind);
        Assert.Equal("W1XYZ K1ABC RR73", snap[0].Decode.Message);   // the Reply echoes this one
        Assert.Equal("FN42", snap[0].Grid);                          // grid kept from the CQ
    }

    [Fact]
    public void CallList_DropsBusyAndQuietStations_PutsCallersFirst()
    {
        var list = new WsjtxCallList { MyCall = "MM5AGM" };
        list.Add(Decode("CQ K1ABC FN42", -10), T0);
        list.Add(Decode("CQ DL2XY JO62", -5), T0);
        list.Add(Decode("CQ EA7QQ IM76", -12), T0.AddSeconds(30));
        list.Add(Decode("W1XYZ K1ABC -10", -9), T0.AddSeconds(30));   // K1ABC busy
        list.Add(Decode("MM5AGM OH6AA KP20", -14), T0.AddSeconds(30));

        var snap = list.Snapshot(T0.AddSeconds(40), TimeSpan.FromSeconds(33));
        // DL2XY last heard 40 s ago: gone. K1ABC busy: gone.
        Assert.Equal(new[] { "OH6AA", "EA7QQ" }, snap.Select(e => e.Call));
        Assert.False(list.TryGet("K1ABC", out _));
    }

    [Fact]
    public void Bands_FollowTheMode()
    {
        var b20 = WsjtxBands.All.Single(b => b.Name == "20 m");
        Assert.Equal(14_074_000, WsjtxBands.DialFor(b20, "FT8"));
        Assert.Equal(14_080_000, WsjtxBands.DialFor(b20, "FT4"));
        Assert.Equal("40 m", WsjtxBands.BandOf(7_074_000)!.Name);
        Assert.Null(WsjtxBands.BandOf(144_174_000));
    }

    // The page asks WSJT-Z to transmit, so nobody gets it without turning it on.
    [Fact]
    public void Ft8TilesIsOffByDefault()
    {
        Assert.False(new Yaesu_Web_Control.Models.ApplicationSettings().Ft8TilesEnabled);
    }

    // While off, every action but Stop is refused, and the menu item is hidden.
    [Fact]
    public void Ft8TilesOffIsEnforcedByTheServerAndTheMenu()
    {
        string root = FindRepoRoot();
        string controller = File.ReadAllText(Path.Combine(root, "Controllers", "WsjtxController.cs"));
        foreach (string action in new[] { "Reply(", "AutoCall(", "AutoCq(", "Band(" })
        {
            int at = controller.IndexOf("public async Task<IActionResult> " + action, StringComparison.Ordinal);
            Assert.True(at >= 0, action);
            int body = controller.IndexOf('{', at);
            string firstLine = controller.Substring(body, 120);
            Assert.Contains("if (!TilesEnabled) return Conflict", firstLine);
        }
        string layout = File.ReadAllText(Path.Combine(root, "Pages", "Shared", "_Layout.cshtml"));
        int link = layout.IndexOf("asp-page=\"/Ft8Tiles\"", StringComparison.Ordinal);
        int gate = layout.LastIndexOf("@if (layoutFt8Tiles)", link, StringComparison.Ordinal);
        Assert.True(link > 0 && gate > 0 && link - gate < 200);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    private static WsjtxDecode Decode(string message, int snr) =>
        new("WSJT-X - Z", true, 1000, snr, 0.1, 1500, "~", message, false, false);
}
