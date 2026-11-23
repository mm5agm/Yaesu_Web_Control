using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// #394 (Rick W2JAZ, N1MM through YWC): a read that timed out removed a newer
// read's slot, and YWC's meter loop and N1MM both sending "TX;" took each
// other's answers. These pin how replies are paired with reads now.
public sealed class PendingRepliesTests
{
    [Fact]
    public async Task A_read_gets_its_answer()
    {
        var replies = new PendingReplies();
        var ask = replies.AskAsync("FA", "FA;", () => Task.CompletedTask, 1000, CancellationToken.None);
        Assert.True(replies.TryAnswer("FA", "FA014074000"));
        Assert.Equal("FA014074000", await ask);
    }

    [Fact]
    public async Task A_read_with_no_answer_returns_null_and_frees_its_slot()
    {
        var replies = new PendingReplies();
        Assert.Null(await replies.AskAsync("IF", "IF;", () => Task.CompletedTask, 20, CancellationToken.None));
        Assert.False(replies.TryAnswer("IF", "IF001014074000"));
    }

    [Fact]
    public async Task The_same_plain_read_is_sent_once_and_both_askers_get_the_answer()
    {
        var replies = new PendingReplies();
        int sends = 0;
        Task Send() { sends++; return Task.CompletedTask; }
        var meter = replies.AskAsync("TX", "TX;", Send, 1000, CancellationToken.None);
        var n1mm = replies.AskAsync("TX", "TX;", Send, 1000, CancellationToken.None);
        Assert.True(replies.TryAnswer("TX", "TX0"));
        Assert.Equal("TX0", await meter);
        Assert.Equal("TX0", await n1mm);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task A_read_that_times_out_leaves_a_newer_read_waiting()
    {
        var replies = new PendingReplies();
        int sends = 0;
        Task Send() { sends++; return Task.CompletedTask; }
        // A set with the same letters takes the slot from an older read ...
        var older = replies.AskAsync("TX", "TX;", Send, 30, CancellationToken.None);
        var newer = replies.AskAsync("TX", "TX0;", Send, 2000, CancellationToken.None);
        // ... and the older read timing out must not take the newer one's slot.
        Assert.Null(await older);
        Assert.True(replies.TryAnswer("TX", "TX0"));
        Assert.Equal("TX0", await newer);
        Assert.Equal(2, sends);
    }

    [Theory]
    [InlineData("TX0;")]
    [InlineData("FA014074000;")]
    public async Task A_set_is_always_sent_even_if_the_same_one_is_waiting(string set)
    {
        var replies = new PendingReplies();
        int sends = 0;
        Task Send() { sends++; return Task.CompletedTask; }
        var key = set[..2];
        var first = replies.AskAsync(key, set, Send, 30, CancellationToken.None);
        var second = replies.AskAsync(key, set, Send, 30, CancellationToken.None);
        await Task.WhenAll(first, second);
        Assert.Equal(2, sends);
    }

    [Theory]
    [InlineData("TX", "TX;", true)]
    [InlineData("SM0", "SM0;", true)]
    [InlineData("MD", "MD0;", false)]
    [InlineData("TX", "TX1;", false)]
    public void Only_the_key_and_a_semicolon_is_a_plain_read(string key, string command, bool plain) =>
        Assert.Equal(plain, PendingReplies.IsPlainRead(key, command));
}
