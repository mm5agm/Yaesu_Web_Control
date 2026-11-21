using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Yaesu_Web_Control.Controllers;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

/// <summary>
/// #396: on an FTDX10 the radio didn't answer the SS P2=7 read, so pressing
/// AF-FFT ATT, OSC ATT or OSC Time wrote the other two as 0. The current
/// values now come from the radio, else the ACC board, else what YWC last
/// read or wrote.
/// </summary>
public class ScopeAfFftFallbackTests
{
    [Fact]
    public async Task The_radio_answering_the_read_is_used_as_before()
    {
        var (controller, cat, _) = New(ss07Answer: "SS0721300");

        await controller.Set("main", "affft", new ScopeSetRequest { Value = "a0" });

        Assert.Contains("SS0720300;", cat.Sent);
    }

    [Fact]
    public async Task No_answer_takes_the_others_from_the_acc_board()
    {
        var (controller, cat, memory) = New(ss07Answer: null);
        memory.FromAcc(fftAtt: 2, oscAtt: 1, oscTime: 3);

        await controller.Set("main", "affft", new ScopeSetRequest { Value = "a0" });

        // OSC ATT changes; AF-FFT 20 dB and 30 ms stay.
        Assert.Contains("SS0720300;", cat.Sent);
    }

    [Fact]
    public async Task No_answer_and_no_acc_keeps_what_YWC_wrote_last()
    {
        var (controller, cat, _) = New(ss07Answer: null);

        await controller.Set("main", "affft", new ScopeSetRequest { Value = "1" });    // AF-FFT 10 dB
        await controller.Set("main", "affft", new ScopeSetRequest { Value = "t3" });   // then 30 ms

        // Eugene's log had SS0700300 here, the AF-FFT lost.
        Assert.Equal(new[] { "SS0710000;", "SS0710300;" }, cat.Sent.Where(s => s.Length == 10 && s.StartsWith("SS07")));
    }

    [Fact]
    public async Task The_state_shows_the_acc_settings_when_the_read_goes_unanswered()
    {
        var (controller, _, memory) = New(ss07Answer: null);
        memory.FromAcc(fftAtt: 1, oscAtt: 2, oscTime: 5);

        var result = Assert.IsType<OkObjectResult>(await controller.GetState("main"));
        var state = result.Value!;
        string? Prop(string name) => (string?)state.GetType().GetProperty(name)!.GetValue(state);

        Assert.Equal(("1", "2", "5"), (Prop("fftAtt"), Prop("oscAtt"), Prop("oscTime")));
    }

    [Fact]
    public void A_frame_taken_before_YWC_wrote_does_not_undo_the_write()
    {
        var m = new ScopeAfFftMemory();
        var t = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        m.Remember('0', ('1', '0', '0'), t);
        m.FromAcc(0, 0, 0, t.AddMilliseconds(200));   // the radio hasn't redrawn yet

        Assert.Equal(('1', '0', '0'), m.Current('0', t.AddMilliseconds(300))!.Value.Values);

        m.FromAcc(2, 0, 0, t.AddSeconds(2));          // changed on the radio's own screen later
        Assert.Equal(('2', '0', '0'), m.Current('0', t.AddSeconds(2.1))!.Value.Values);
    }

    [Fact]
    public void An_old_acc_frame_is_not_current()
    {
        var m = new ScopeAfFftMemory();
        var t = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        m.FromAcc(2, 1, 3, t);

        Assert.NotNull(m.Current('0', t.AddSeconds(1)));
        Assert.Null(m.Current('0', t.AddSeconds(5)));
    }

    private static (ScopeController, FakeCat, ScopeAfFftMemory) New(string? ss07Answer)
    {
        var (state, _) = ModeVfoRoutingTests.NewDispatcher(singleReceiver: true, activeVfo: 0);
        state.RadioModel = "FTdx10";
        var cat = FakeCat.Create(ss07Answer);
        var memory = new ScopeAfFftMemory();
        var controller = new ScopeController(
            (ICatClient)cat, null!, state, NullLogger<ScopeController>.Instance, memory);
        return (controller, cat, memory);
    }

    /// <summary>
    /// An ICatClient that is always connected, records what it's sent, and
    /// answers "SS07;" with <see cref="Ss07Answer"/> (null = no answer).
    /// </summary>
    public class FakeCat : DispatchProxy
    {
        public List<string> Sent { get; } = new();
        public string? Ss07Answer { get; set; }

        public static FakeCat Create(string? ss07Answer)
        {
            var proxy = (FakeCat)(object)Create<ICatClient, FakeCat>();
            proxy.Ss07Answer = ss07Answer;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_IsConnected":
                    return true;
                case "SendCommandAsync":
                    var command = (string)args![0]!;
                    Sent.Add(command);
                    return Task.FromResult(command == "SS07;" ? Ss07Answer ?? "" : "");
                default:
                    var ret = method.ReturnType;
                    if (ret == typeof(Task)) return Task.CompletedTask;
                    if (ret.IsGenericType && ret.GetGenericTypeDefinition() == typeof(Task<>))
                    {
                        var inner = ret.GetGenericArguments()[0];
                        var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                        return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner)
                            .Invoke(null, new[] { value });
                    }
                    return ret.IsValueType ? Activator.CreateInstance(ret) : null;
            }
        }
    }
}
