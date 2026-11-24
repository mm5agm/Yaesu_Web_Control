using System.Buffers.Binary;
using System.Text;
using Yaesu_Web_Control.Services.Wsjtx;

namespace YaesuWebControl.Tests;

// #332: WSJT-Z's Log QSO window can't be clicked from the FT8 Tiles, so the
// operator unticks "Prompt me to log QSO" and the tiles show a "Logged"
// banner from WSJT-Z's QSO Logged (type 5) message instead.
public sealed class WsjtxQsoLoggedTests
{
    private sealed class Bytes
    {
        private readonly List<byte> _b = new();
        public Bytes U8(byte v) { _b.Add(v); return this; }
        public Bytes U32(uint v) { var x = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(x, v); _b.AddRange(x); return this; }
        public Bytes I64(long v) { var x = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(x, v); _b.AddRange(x); return this; }
        public Bytes U64(ulong v) { var x = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(x, v); _b.AddRange(x); return this; }
        public Bytes S(string s) { var x = Encoding.UTF8.GetBytes(s); U32((uint)x.Length); _b.AddRange(x); return this; }
        public Bytes Null() => U32(uint.MaxValue);
        public Bytes Header(uint type, string id) => U32(0xADBCCBDA).U32(2).U32(type).S(id);
        // QDateTime: Julian day, ms since midnight, time spec (+ offset for spec 2).
        public Bytes DateTime(byte spec, int offsetSeconds = 0)
        {
            I64(2_461_368).U32(45_015_000).U8(spec);
            if (spec == 2) U32(unchecked((uint)offsetSeconds));
            return this;
        }
        public byte[] ToArray() => _b.ToArray();
    }

    // Everything WSJT-X 2.x sends after the fields we read.
    private static Bytes Tail(Bytes b) => b
        .S("5W").S("").S("").DateTime(1).S("").S("MM5AGM").S("IO86").S("").S("").S("");

    private static byte[] Logged(byte spec = 1, string call = "G4ABC") =>
        Tail(new Bytes().Header(5, "WSJT-X - Z").DateTime(spec, 3600)
            .S(call).S("IO91").U64(14_075_512).S("FT8").S("-10").S("-12")).ToArray();

    [Fact]
    public void Parses_the_fields_the_banner_shows()
    {
        var q = WsjtxProtocol.TryParseQsoLogged(Logged());
        Assert.NotNull(q);
        Assert.Equal("WSJT-X - Z", q!.Id);
        Assert.Equal("G4ABC", q.DxCall);
        Assert.Equal("IO91", q.DxGrid);
        Assert.Equal(14_075_512, q.TxFrequencyHz);
        Assert.Equal("FT8", q.Mode);
        Assert.Equal("-10", q.ReportSent);
        Assert.Equal("-12", q.ReportReceived);
    }

    [Fact]
    public void A_time_with_a_UTC_offset_is_skipped_correctly()
    {
        var q = WsjtxProtocol.TryParseQsoLogged(Logged(spec: 2));
        Assert.Equal("G4ABC", q?.DxCall);
        Assert.Equal("FT8", q?.Mode);
    }

    [Fact]
    public void Null_strings_read_as_empty()
    {
        var bytes = new Bytes().Header(5, "WSJT-X").DateTime(1)
            .S("K1ABC").Null().U64(7_074_000).S("FT4").Null().Null().ToArray();
        var q = WsjtxProtocol.TryParseQsoLogged(bytes);
        Assert.NotNull(q);
        Assert.Equal("", q!.DxGrid);
        Assert.Equal("", q.ReportSent);
    }

    [Fact]
    public void Time_zone_spec_no_call_short_or_other_types_give_null()
    {
        Assert.Null(WsjtxProtocol.TryParseQsoLogged(Logged(spec: 3)));
        Assert.Null(WsjtxProtocol.TryParseQsoLogged(Logged(call: "")));
        var full = Logged();
        Assert.Null(WsjtxProtocol.TryParseQsoLogged(full[..40]));
        var status = new Bytes().Header(1, "WSJT-X").U64(14_074_000).S("FT8").ToArray();
        Assert.Null(WsjtxProtocol.TryParseQsoLogged(status));
    }

    [Fact]
    public void Type_number_matches_NetworkMessage_hpp() =>
        Assert.Equal(5u, WsjtxProtocol.TypeQsoLogged);
}
