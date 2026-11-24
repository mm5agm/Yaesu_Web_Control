using System.Text;
using Yaesu_Web_Control.Services.N1mm;

namespace YaesuWebControl.Tests;

// YWC #394: N1MM Logger+ connects to Yaesu Web Control's TCP CAT port as if
// it were the radio. These pin the rules for what is passed on, what is
// answered here, and when the radio is known to be keyed.
public sealed class N1mmCatServerTests
{
    private sealed class FakeRadio : ICatPassThrough
    {
        public bool IsConnected { get; set; } = true;
        public List<string> Reads { get; } = new();
        public List<string> Writes { get; } = new();
        public Dictionary<string, string> Answers { get; } = new();

        public Task<string?> ReadAsync(string command, CancellationToken cancellationToken)
        {
            Reads.Add(command);
            return Task.FromResult(Answers.TryGetValue(command, out var a) ? a : null);
        }

        public Task WriteAsync(string command, CancellationToken cancellationToken)
        {
            Writes.Add(command);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("FA;", N1mmCatKind.Read)]
    [InlineData("IF;", N1mmCatKind.Read)]
    [InlineData("TX;", N1mmCatKind.Read)]
    [InlineData("MD0;", N1mmCatKind.Read)]
    [InlineData("SM0;", N1mmCatKind.Read)]
    [InlineData("RM5;", N1mmCatKind.Read)]
    [InlineData("EX030101;", N1mmCatKind.Read)]
    [InlineData("FA014074000;", N1mmCatKind.Set)]
    [InlineData("MD02;", N1mmCatKind.Set)]
    [InlineData("TX0;", N1mmCatKind.Set)]
    [InlineData("TX1;", N1mmCatKind.Set)]
    [InlineData("FT1;", N1mmCatKind.Set)]
    [InlineData("BU0;", N1mmCatKind.Set)]
    [InlineData("KY CQ TEST;", N1mmCatKind.Set)]
    [InlineData("AI;", N1mmCatKind.AutoInfo)]
    [InlineData("AI0;", N1mmCatKind.AutoInfo)]
    [InlineData("AI1;", N1mmCatKind.AutoInfo)]
    [InlineData("fa;", N1mmCatKind.Invalid)]
    [InlineData("F;", N1mmCatKind.Invalid)]
    [InlineData("<xml>;", N1mmCatKind.Invalid)]
    public void Classify_sorts_reads_sets_and_AI(string command, N1mmCatKind kind) =>
        Assert.Equal(kind, N1mmCatProtocol.Classify(command));

    [Fact]
    public void Classify_rejects_overlong_commands() =>
        Assert.Equal(N1mmCatKind.Invalid, N1mmCatProtocol.Classify("FA" + new string('1', 70) + ";"));

    [Fact]
    public void TakeCommands_splits_and_keeps_a_partial_command()
    {
        var pending = new StringBuilder("FA;\r\nMD0;FB01");
        Assert.Equal(new[] { "FA;", "MD0;" }, N1mmCatProtocol.TakeCommands(pending));
        Assert.Equal("FB01", pending.ToString());
        pending.Append("4074000;");
        Assert.Equal(new[] { "FB014074000;" }, N1mmCatProtocol.TakeCommands(pending));
        Assert.Equal("", pending.ToString());
    }

    [Fact]
    public void TakeCommands_throws_away_endless_text_without_a_semicolon()
    {
        var pending = new StringBuilder(new string('x', N1mmCatProtocol.MaxPendingLength + 1));
        Assert.Empty(N1mmCatProtocol.TakeCommands(pending));
        Assert.Equal(0, pending.Length);
    }

    [Fact]
    public async Task Reads_return_the_radios_answers_in_order()
    {
        var radio = new FakeRadio();
        radio.Answers["FA;"] = "FA014074000";
        radio.Answers["MD0;"] = "MD02";
        var session = new N1mmCatSession(radio);

        var reply = await session.ProcessAsync("FA;MD0;FB;", CancellationToken.None);

        Assert.Equal("FA014074000;MD02;", reply); // FB gave no answer: nothing for it
        Assert.Equal(new[] { "FA;", "MD0;", "FB;" }, radio.Reads);
        Assert.Empty(radio.Writes);
    }

    [Fact]
    public async Task Sets_are_written_and_not_answered()
    {
        var radio = new FakeRadio();
        var session = new N1mmCatSession(radio);

        var reply = await session.ProcessAsync("FA007030000;MD03;", CancellationToken.None);

        Assert.Equal("", reply);
        Assert.Equal(new[] { "FA007030000;", "MD03;" }, radio.Writes);
    }

    [Fact]
    public async Task AI_never_reaches_the_radio_and_reads_as_off()
    {
        var radio = new FakeRadio();
        var session = new N1mmCatSession(radio);

        Assert.Equal("AI0;", await session.ProcessAsync("AI;", CancellationToken.None));
        Assert.Equal("", await session.ProcessAsync("AI0;AI1;", CancellationToken.None));
        Assert.Empty(radio.Reads);
        Assert.Empty(radio.Writes);
    }

    [Fact]
    public async Task Invalid_text_is_dropped_and_remembered_once()
    {
        var radio = new FakeRadio();
        var session = new N1mmCatSession(radio);

        await session.ProcessAsync("hello;FA;", CancellationToken.None);
        await session.ProcessAsync("world;", CancellationToken.None);

        Assert.Equal("hello;", session.FirstInvalid);
        Assert.Equal(new[] { "FA;" }, radio.Reads);
    }

    [Fact]
    public async Task Nothing_is_sent_while_the_radio_is_not_connected()
    {
        var radio = new FakeRadio { IsConnected = false };
        var session = new N1mmCatSession(radio);

        Assert.Equal("", await session.ProcessAsync("FA;FA014074000;TX1;", CancellationToken.None));
        Assert.Empty(radio.Reads);
        Assert.Empty(radio.Writes);
        Assert.False(session.Keyed); // so no TX0 is owed when it disconnects
    }

    // The server sends TX0 when a keyed connection drops or holds TX1 too
    // long; this is the state it goes by. Commands only; nothing transmits.
    [Fact]
    public async Task Keyed_follows_TX1_TX2_and_TX0()
    {
        var radio = new FakeRadio();
        var session = new N1mmCatSession(radio);

        await session.ProcessAsync("TX1;", CancellationToken.None);
        Assert.True(session.Keyed);
        await session.ProcessAsync("TX0;", CancellationToken.None);
        Assert.False(session.Keyed);
        await session.ProcessAsync("TX2;", CancellationToken.None);
        Assert.True(session.Keyed);
        session.ClearKeyed();
        Assert.False(session.Keyed);
        await session.ProcessAsync("TX;", CancellationToken.None); // a read changes nothing
        Assert.False(session.Keyed);
    }

    [Theory]
    [InlineData(4535, 4535)]
    [InlineData(1024, 1024)]
    [InlineData(65535, 65535)]
    [InlineData(80, N1mmCatServer.DefaultPort)]
    [InlineData(0, N1mmCatServer.DefaultPort)]
    [InlineData(70000, N1mmCatServer.DefaultPort)]
    public void ClampPort_keeps_ports_from_1024_up(int port, int expected) =>
        Assert.Equal(expected, N1mmCatServer.ClampPort(port));

    // #394: N1MM waited for every answer in a packet before it got any.
    [Fact]
    public async Task Each_answer_is_sent_as_soon_as_the_radio_gives_it()
    {
        var radio = new FakeRadio();
        radio.Answers["FA;"] = "FA014074000";
        radio.Answers["MD0;"] = "MD02";
        var session = new N1mmCatSession(radio);
        var sent = new List<string>();
        await session.ProcessAsync("FA;MD0;AI;", (a, _) => { sent.Add(a); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(new[] { "FA014074000;", "MD02;", "AI0;" }, sent);
    }

    [Fact]
    public async Task Reads_the_radio_does_not_answer_are_counted_for_the_log()
    {
        var radio = new FakeRadio();
        radio.Answers["FA;"] = "FA014074000";
        var session = new N1mmCatSession(radio);
        var missed = new List<string>();
        session.OnUnanswered = missed.Add;
        var reply = await session.ProcessAsync("FA;IF;FA014070000;TX;", CancellationToken.None);
        Assert.Equal("FA014074000;", reply);
        Assert.Equal(2, session.Unanswered);
        Assert.Equal(new[] { "IF;", "TX;" }, missed);
    }

    [Fact]
    public async Task Sets_are_counted_and_reported_for_the_log()
    {
        var radio = new FakeRadio();
        var session = new N1mmCatSession(radio);
        var sent = new List<string>();
        session.OnSet = sent.Add;
        await session.ProcessAsync("FA;BS05;FA007025000;MD03;TX;", CancellationToken.None);
        Assert.Equal(3, session.Sets);
        Assert.Equal(new[] { "BS05;", "FA007025000;", "MD03;" }, sent);
    }
}
