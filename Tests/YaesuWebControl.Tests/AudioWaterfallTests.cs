using System.Text.Json;
using System.Text.RegularExpressions;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.Audio;

namespace YaesuWebControl.Tests;

/// <summary>
/// The radio audio waterfall's plumbing: the names the page reads, the hub
/// methods, the group not needing the ACC board, and WSJT-X decodes as hints.
/// The guessing itself is in <see cref="AudioModeClassifierTests"/>.
/// </summary>
public class AudioWaterfallTests
{
    [Fact]
    public void The_json_has_the_names_the_page_reads()
    {
        var guess = new AudioModeGuess(1490.4, 1551.6, "FT8", "about 50 Hz wide", true);
        var json = JsonSerializer.Serialize(AudioWaterfallService.ToWire(guess), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1490, root.GetProperty("lowHz").GetDouble());
        Assert.Equal(1552, root.GetProperty("highHz").GetDouble());
        Assert.Equal("FT8", root.GetProperty("mode").GetString());
        Assert.True(root.GetProperty("fromWsjtx").GetBoolean());
        Assert.True(root.TryGetProperty("detail", out _));

        var js = Read("wwwroot/js/ui/radio-audio-waterfall.js");
        foreach (var used in new[] { "g.lowHz", "g.highHz", "g.mode", "g.detail", "g.fromWsjtx", "value.bins", "value.binHz",
                                     "'AudioWaterfall'", "'AudioWaterfallModes'" })
            Assert.Contains(used, js);

        var service = Read("Services/Audio/AudioWaterfallService.cs");
        Assert.Contains("FrameProperty = \"AudioWaterfall\"", service);
        Assert.Contains("ModesProperty = \"AudioWaterfallModes\"", service);
    }

    [Fact]
    public void The_waterfall_has_a_fixed_level_scale_with_ref()
    {
        // Colin, 2026-11-18: with the aerial out it looked just the same,
        // because each row was stretched from its own median to its own peak.
        var js = Read("wwwroot/js/ui/radio-audio-waterfall.js");
        Assert.DoesNotContain("rowStretch", js);
        Assert.Contains("export const REF_DEFAULT_DB = -30;", js);
        Assert.Contains("export const SPAN_DB = 60;", js);
        Assert.Contains("const { low, high } = scaleFor(this.ref);", js);
        Assert.Contains("'ywc.radioAudioFallRef'", js);
        // dbToByte must match the host's ToByte: 0 at -140 dBFS, 255 at -20.
        Assert.Contains("return (db + 140) * 255 / 120;", js);
        Assert.Equal(0, AudioWaterfallAnalyser.ToByte(-140f));
        Assert.Equal(255, AudioWaterfallAnalyser.ToByte(-20f));

        var partial = Read("Pages/Shared/_AudioWaterfallPartial.cshtml");
        Assert.Contains("radio-audio-fall-ref-value", partial);
        Assert.Contains("data-ref-step=\"-1\"", partial);
        Assert.Contains("data-ref-step=\"1\"", partial);
    }

    [Fact]
    public void A_frame_send_that_never_finishes_does_not_stop_the_waterfall()
    {
        var service = Read("Services/Audio/AudioWaterfallService.cs");
        Assert.DoesNotContain("_sendInFlight", service);
        Assert.Contains("StuckSendMs = 2000", service);
        Assert.Contains("catch (Exception ex)", service);
    }

    [Fact]
    public void The_audio_group_needs_no_acc_board_and_is_found_by_class()
    {
        var partial = Read("Pages/Shared/_RadioScopeButtonsPartial.cshtml");
        int group = partial.IndexOf("radio-audio-fall-group", StringComparison.Ordinal);
        Assert.True(group > 0, "no AUDIO group in the scope partial");
        // Outside every @if: after the last closing brace of the MULTI block.
        int lastIfEnd = partial.LastIndexOf("\n}", StringComparison.Ordinal);
        Assert.True(group > lastIfEnd, "the AUDIO group is inside an @if");
        Assert.True(partial.IndexOf("<partial name=\"_AudioWaterfallPartial\" />", StringComparison.Ordinal) > group,
            "the AUDIO group does not hold the audio waterfall partial");
        Assert.DoesNotMatch(@"<[^>]*radio-audio-fall[^>]*\sid=", partial);
        Assert.DoesNotMatch(@"<[^>]*radio-audio-fall[^>]*\sid=", Read("Pages/Shared/_AudioWaterfallPartial.cshtml"));

        // MULTI remembers its own open state; AUDIO has its own.
        var scope = Read("wwwroot/js/ui/radio-scope.js");
        Assert.Contains("details.radio-scope-multi:not(.radio-audio-fall-group)", scope);
        Assert.Contains("radio-audio-waterfall.js", scope);
    }

    [Fact]
    public void The_hub_serves_it_and_lets_go_on_disconnect()
    {
        var js = Read("wwwroot/js/ui/radio-audio-waterfall.js");
        Assert.Contains("invoke('SubscribeAudioWaterfall')", js);
        Assert.Contains("invoke('UnsubscribeAudioWaterfall')", js);

        var hub = Read("RadioHub.cs");
        Assert.Contains("public async Task<string?> SubscribeAudioWaterfall()", hub);
        Assert.Contains("public async Task UnsubscribeAudioWaterfall()", hub);
        var disconnected = Regex.Match(hub, @"OnDisconnectedAsync\(Exception\? exception\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.Contains("_audioWaterfall?.Unsubscribe(Context.ConnectionId)", disconnected.Groups["body"].Value);

        Assert.Contains("AddSingleton<Yaesu_Web_Control.Services.Audio.AudioWaterfallService>()", Read("Program.cs"));
    }

    [Fact]
    public void Wsjtx_mode_symbols_become_names()
    {
        Assert.Equal("FT8", WsjtxUdpService.DecodeModeName("~", "FT4"));
        Assert.Equal("FT4", WsjtxUdpService.DecodeModeName("+", "FT8"));
        Assert.Equal("JT65", WsjtxUdpService.DecodeModeName("#", "JT65"));
        Assert.Equal("WSJT-X", WsjtxUdpService.DecodeModeName("@", ""));
    }

    [Fact]
    public void The_waterfall_holds_a_whole_ft8_cycle_in_100_rows()
    {
        var analyser = new AudioWaterfallAnalyser();
        var service = Read("Services/Audio/AudioWaterfallService.cs");
        int sendEvery = int.Parse(Regex.Match(service, @"const int SendEvery = (\d+);").Groups[1].Value);
        var js = Read("wwwroot/js/ui/radio-audio-waterfall.js");
        int framesPerRow = int.Parse(Regex.Match(js, @"export const FRAMES_PER_ROW = (\d+);").Groups[1].Value);

        double secondsIn100Rows = 100 * framesPerRow * sendEvery * analyser.FrameSeconds;

        Assert.InRange(secondsIn100Rows, 15, 30);
    }

    [Fact]
    public void The_page_waits_as_long_as_the_guesser_does()
    {
        var js = Read("wwwroot/js/ui/radio-audio-waterfall.js");
        int ms = int.Parse(Regex.Match(js, @"export const GUESS_AFTER_MS = (\d+);").Groups[1].Value);
        Assert.Equal(AudioModeClassifier.MinHistorySeconds * 1000, ms);
        Assert.Contains($"start after {AudioModeClassifier.MinHistorySeconds:0} s", js);
        Assert.InRange(AudioModeClassifier.MinHistorySeconds, 15, 20);   // one whole FT8 cycle
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(RepoRoot(), path));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Yaesu_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
