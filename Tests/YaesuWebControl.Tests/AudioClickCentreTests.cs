using System.Text.RegularExpressions;
using Yaesu_Web_Control.Services.Audio;

namespace YaesuWebControl.Tests;

/// <summary>
/// Click-to-centre on the radio audio waterfall: the sign of the dial move in
/// each mode, the modes it refuses, the page's click and target line, and the
/// standalone page every radio gets.
/// </summary>
public class AudioClickCentreTests
{
    // In USB the tone is RF - dial: a signal heard 290 Hz too high needs the
    // dial 290 Hz higher. In LSB the tone is dial - RF, so the dial goes down.
    [Theory]
    [InlineData("USB", 2500, 2210, 290)]
    [InlineData("USB", 1900, 2210, -310)]
    [InlineData("DATA-U", 2500, 2210, 290)]
    [InlineData("RTTY-U", 2500, 2210, 290)]
    [InlineData("CW-U", 900, 600, 300)]
    [InlineData("LSB", 2500, 2210, -290)]
    [InlineData("DATA-L", 2500, 2210, -290)]
    [InlineData("RTTY-L", 1900, 2210, 310)]
    [InlineData("CW-L", 900, 600, -300)]
    public void The_dial_moves_the_right_way_in_each_mode(string mode, double fromHz, double toHz, long expected)
    {
        var rises = AudioClickCentre.ToneRisesWithDial(mode);
        Assert.NotNull(rises);
        Assert.Equal(expected, AudioClickCentre.DialMove(fromHz, toHz, rises!.Value));
    }

    // Agrees with RTTY Auto's own table for the modes both handle.
    [Theory]
    [InlineData("RTTY-L")]
    [InlineData("RTTY-U")]
    [InlineData("DATA-L")]
    [InlineData("DATA-U")]
    public void It_agrees_with_rtty_auto(string mode) =>
        Assert.Equal(RadioWebControl.Core.Services.Rtty.RttyMarkCentre.SidebandForMode(mode),
                     AudioClickCentre.ToneRisesWithDial(mode));

    [Theory]
    [InlineData("AM")]
    [InlineData("AM-N")]
    [InlineData("FM")]
    [InlineData("FM-N")]
    [InlineData("DATA-FM")]
    [InlineData("C4FM")]
    [InlineData("PSK")]
    [InlineData("")]
    [InlineData(null)]
    public void It_leaves_the_dial_alone_in_other_modes(string? mode) =>
        Assert.Null(AudioClickCentre.ToneRisesWithDial(mode));

    [Theory]
    [InlineData(0, 2210)]
    [InlineData(-5, 2210)]
    [InlineData(4001, 2210)]
    [InlineData(1500, 0)]
    [InlineData(double.NaN, 2210)]
    public void Tones_outside_the_waterfall_are_refused(double fromHz, double toHz) =>
        Assert.Null(AudioClickCentre.DialMove(fromHz, toHz, false));

    [Fact]
    public void The_cw_pitch_comes_from_its_kp_code()
    {
        Assert.Equal(600, AudioClickCentre.CwPitchHz(30));
        Assert.Equal(300, AudioClickCentre.CwPitchHz(0));
        Assert.Equal(1050, AudioClickCentre.CwPitchHz(75));
        Assert.Equal(1050, AudioClickCentre.CwPitchHz(99));
        Assert.True(AudioClickCentre.IsCw("CW-L"));
        Assert.False(AudioClickCentre.IsCw("USB"));
    }

    [Fact]
    public void The_endpoint_is_receive_only_and_reads_before_it_writes()
    {
        var ctl = Read("Controllers/AudioWaterfallController.cs");
        Assert.Contains("[Route(\"api/audio-waterfall\")]", ctl);
        Assert.Contains("[HttpPost(\"centre\")]", ctl);
        Assert.Contains("_state.IsTransmitting", ctl);
        Assert.Contains("_cat.IsConnected", ctl);
        Assert.Contains("QueryFrequencyAAsync", ctl);
        Assert.Contains("RadioCapabilities.IsTunableFrequency", ctl);
        Assert.Contains("MarkUserFrequencyWrite", ctl);
        // Nothing that could key the radio.
        Assert.DoesNotMatch(new Regex(@"Ptt|Transmit\w*Async|SetTx|Tune\w*Async|SendCw", RegexOptions.IgnoreCase), ctl);
        // The transmit check comes before any CAT write.
        Assert.True(ctl.IndexOf("_state.IsTransmitting", StringComparison.Ordinal)
                  < ctl.IndexOf("SetFrequencyAAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void The_page_posts_a_click_and_draws_the_target()
    {
        var js = Read("wwwroot/js/ui/radio-audio-waterfall.js");
        Assert.Contains("export const CENTRE_URL = '/api/audio-waterfall/centre';", js);
        Assert.Contains("addEventListener('click'", js);
        Assert.Contains("JSON.stringify({ fromHz, toHz:", js);
        Assert.Contains("body.targetHz", js);
        Assert.Contains("_drawTarget(", js);
        Assert.Contains("afskMidpointAudioHz", js);
        Assert.Contains("from '../rtty/rtty-settings.js'", js);

        // The names the controller's JSON uses.
        var ctl = Read("Controllers/AudioWaterfallController.cs");
        Assert.Contains("CentreRequest(double FromHz, double? ToHz)", ctl);
        Assert.Contains("string Message, double? TargetHz", ctl);
    }

    [Fact]
    public void Every_radio_has_the_audio_waterfall_page()
    {
        var page = Read("Pages/AudioWaterfall.cshtml");
        Assert.StartsWith("@page \"/audio-waterfall\"", page);
        Assert.Contains("<partial name=\"_AudioWaterfallPartial\" />", page);
        Assert.Contains("attachRadioAudioWaterfall(document)", page);
        Assert.Contains("hub-connection.js", page);
        // Not behind any radio check.
        Assert.DoesNotContain("@if", page);

        var partial = Read("Pages/Shared/_AudioWaterfallPartial.cshtml");
        Assert.Contains("class=\"radio-audio-fall\"", partial);
        Assert.DoesNotMatch(@"<[^>]*radio-audio-fall[^>]*\sid=", partial);

        foreach (var menu in new[] { "Pages/Shared/_Layout.cshtml", "Pages/Shared/_FlexLayout.cshtml" })
            Assert.Contains("asp-page=\"/AudioWaterfall\"", Read(menu));
        foreach (var menu in new[] { "Pages/RadioPanel.cshtml", "Pages/RttyContest.cshtml" })
            Assert.Contains("href: '/audio-waterfall'", Read(menu));

        // The CSS works outside the scope controls too.
        var css = Read("wwwroot/css/site.css");
        Assert.Contains(".radio-audio-fall .radio-audio-fall-canvas {", css);
        Assert.DoesNotContain(".radio-scope-controls .radio-audio-fall-canvas", css);
    }

    [Fact]
    public void The_rtty_contest_page_pops_the_audio_waterfall_out()
    {
        // The page names it, the YWC wrapper passes it on, and the core view
        // builds AUDIO on the POP OUT bar from it.
        Assert.Contains("audioUrl: '/audio-waterfall',", Read("Pages/RttyContest.cshtml"));
        Assert.Contains("audioUrl: cfg.audioUrl,", Read("wwwroot/js/ui/rtty-contest-yaesu.js"));
        var view = Read("core/js/rtty-contest/contest-view.js");
        Assert.Contains("'wheel', 'audio'];", view);
        Assert.Contains("if (p === 'audio' && !opts.audioUrl) continue;", view);
        Assert.Contains("'window=' + encodeURIComponent(winName + p)", view);

        // Opened that way, the page answers as the pop-out of the same name,
        // so the key lights while it is open and goes out when it closes.
        var page = Read("Pages/AudioWaterfall.cshtml");
        Assert.Contains("new URLSearchParams(location.search).get('window')", page);
        Assert.Contains("new PopoutChild({ name, fallbackUrl: '/rtty-contest' })", page);
        Assert.Contains("popout.reattach()", page);
    }

    [Fact]
    public void A_refused_click_stands_out_and_is_logged()
    {
        var js = Read("wwwroot/js/ui/radio-audio-waterfall.js");
        Assert.Contains("radio-audio-fall-refused", js);
        Assert.Contains("body.moved === false && body.offsetHz !== 0", js);
        Assert.Contains(".radio-audio-fall .radio-audio-fall-status.radio-audio-fall-refused {", Read("wwwroot/css/site.css"));
        Assert.Contains("click at {From:0} Hz refused", Read("Controllers/AudioWaterfallController.cs"));
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(RepoRoot(), path));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Yaesu_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
