using System.Text.Json;
using System.Text.RegularExpressions;
using Yaesu_Web_Control.Services.Sdr;
using YaesuAccScope;

namespace YaesuWebControl.Tests;

/// <summary>
/// The FTdx101's audio scope from its ACC socket, as YWC sends it to the
/// MULTI group: what a frame becomes, the JSON names acc-audio-scope.js reads,
/// and the wiring from page to hub. Frames are built byte by byte at the
/// offsets YaesuAccScope 0.3.0 reads (AudioScope in AccFrame.cs); the library
/// has its own tests against real captures.
/// </summary>
public class AccAudioTests
{
    // Main: AF-FFT 1700-1899, oscilloscope 1900-2299. Sub: 2300-2499, 2500-2899.
    private const int MainFftAt = 1700, MainOscAt = 1900, SubFftAt = 2300, SubOscAt = 2500;
    private const int SettingsAt = 3056;

    /// <summary>
    /// A frame with a 1 kHz tone on Main, 2 kHz on Sub, a different
    /// oscilloscope shape on each, and the shared settings in byte 3056.
    /// </summary>
    private static AccFrame Frame(int fftAtt = 1, int oscAtt = 2, int oscTime = 4)
    {
        var raw = new byte[4096];
        for (int i = 0; i < 200; i++)
        {
            raw[MainFftAt + i] = (byte)(i == 50 ? 255 - 200 : 255 - 40);   // sent inverted
            raw[SubFftAt + i]  = (byte)(i == 100 ? 255 - 180 : 255 - 30);
            raw[MainOscAt + 2 * i]     = (byte)(128 + i % 100);             // max
            raw[MainOscAt + 2 * i + 1] = (byte)(128 - i % 100);             // min
            raw[SubOscAt + 2 * i]      = 140;
            raw[SubOscAt + 2 * i + 1]  = 116;
        }
        raw[SettingsAt] = (byte)((fftAtt << 5) | (oscAtt << 3) | oscTime);
        return AccFrame.Parse(raw);
    }

    [Fact]
    public void Each_receiver_gets_its_own_af_fft_the_right_way_up()
    {
        var f = AccAudio.Build(Frame());

        Assert.Equal(200, f.Main!.Fft.Length);
        Assert.Equal(200, f.Main.Fft[50]);
        Assert.Equal(40, f.Main.Fft[51]);
        Assert.Equal(180, f.Sub!.Fft[100]);
        Assert.Equal(30, f.Sub.Fft[50]);
    }

    [Fact]
    public void The_oscilloscope_is_max_then_min_for_each_column()
    {
        var f = AccAudio.Build(Frame());

        Assert.Equal(400, f.Main!.Osc.Length);
        Assert.Equal(128 + 7, f.Main.Osc[2 * 7]);
        Assert.Equal(128 - 7, f.Main.Osc[2 * 7 + 1]);
        Assert.All(Enumerable.Range(0, 200), c =>
        {
            Assert.Equal(140, f.Sub!.Osc[2 * c]);
            Assert.Equal(116, f.Sub.Osc[2 * c + 1]);
        });
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 2, 4)]
    [InlineData(2, 1, 5)]
    public void The_shared_settings_come_with_it(int fftAtt, int oscAtt, int oscTime)
    {
        var f = AccAudio.Build(Frame(fftAtt, oscAtt, oscTime));

        Assert.Equal(fftAtt, f.FftAtt);
        Assert.Equal(oscAtt, f.OscAtt);
        Assert.Equal(oscTime, f.OscTime);
        Assert.Null(f.Problem);
    }

    [Fact]
    public void A_problem_has_nothing_to_draw()
    {
        var f = AccAudio.Problem("No ACC board found.");

        Assert.Null(f.Main);
        Assert.Null(f.Sub);
        Assert.Equal("No ACC board found.", f.Problem);
    }

    [Fact]
    public void The_reader_s_own_problem_is_passed_on()
    {
        var state = new AccScopeState(AccScopeStatus.NoBoard, null, "No ACC board found. Plug it in.");

        Assert.Equal("No ACC board found. Plug it in.", AccAudio.ProblemText(state));
    }

    [Fact]
    public void Without_a_problem_it_says_what_it_is_doing()
    {
        Assert.Contains("Looking", AccAudio.ProblemText(new AccScopeState(AccScopeStatus.Searching, null, null)));
        Assert.Contains("COM6", AccAudio.ProblemText(new AccScopeState(AccScopeStatus.Reading, "COM6", null)));
    }

    [Fact]
    public void The_json_has_the_names_the_page_reads()
    {
        // SignalR's JSON protocol uses the web defaults: camelCase, byte[] as base64.
        var json = JsonSerializer.Serialize(AccAudio.Build(Frame()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        foreach (var name in new[] { "main", "sub", "fftAtt", "oscAtt", "oscTime", "problem" })
            Assert.True(root.TryGetProperty(name, out _), $"no \"{name}\" in {json}");
        Assert.Equal(200, Convert.FromBase64String(root.GetProperty("main").GetProperty("fft").GetString()!).Length);
        Assert.Equal(400, Convert.FromBase64String(root.GetProperty("sub").GetProperty("osc").GetString()!).Length);

        var js = Read("wwwroot/js/ui/acc-audio-scope.js");
        foreach (var used in new[] { "value.problem", "data.fft", "data.osc", "value.fftAtt", "value.oscAtt", "value.oscTime", "'AccAudio'" })
            Assert.Contains(used, js);
    }

    [Fact]
    public void Only_ftdx101_pages_get_the_panes()
    {
        var partial = Read("Pages/Shared/_RadioScopeButtonsPartial.cshtml");

        Assert.Matches(@"hasAccAudio\s*=\s*Yaesu_Web_Control\.Services\.Sdr\.AccScope\.AudioScopeWanted\(radioModel, accAudioCfg\.SdrDeviceKeyA, accAudioCfg\.SdrDeviceKeyB\)", partial);
        var gated = Regex.Match(partial, @"@if \(hasAccAudio\)\s*\{(?<body>.*?)</figure>\s*</div>", RegexOptions.Singleline);
        Assert.True(gated.Success, "the ACC audio panes are not inside @if (hasAccAudio)");
        Assert.Contains("data-receiver=\"main\"", gated.Groups["body"].Value);
        Assert.Contains("data-receiver=\"sub\"", partial);
        // Found by class, never id: the card and the dialog can share a page.
        Assert.DoesNotMatch(@"<[^>]*acc-audio[^>]*\sid=", partial);
    }

    [Theory]
    [InlineData("FTdx101MP", AccScope.MainKey, "", true)]
    [InlineData("FTdx101D", "sdrplay:123", AccScope.SubKey, true)]
    [InlineData("FTdx101MP", "sdrplay:123", "sdrplay:456", false)]   // SDRs on both: no audio scope
    [InlineData("FTdx101MP", "", "", false)]
    [InlineData("FTdx10", AccScope.MainKey, "", false)]              // no ACC scope on this radio
    public void The_audio_scope_is_offered_only_with_the_acc_scope_as_a_source(string model, string keyA, string keyB, bool wanted)
    {
        Assert.Equal(wanted, AccScope.AudioScopeWanted(model, keyA, keyB));
    }

    [Fact]
    public void The_panes_stay_hidden_until_the_board_sends_frames()
    {
        var partial = Read("Pages/Shared/_RadioScopeButtonsPartial.cshtml");
        Assert.Contains("<div class=\"acc-audio-panes\" hidden>", partial);
        var js = Read("wwwroot/js/ui/acc-audio-scope.js");
        Assert.Contains("this.show(false)", js);
        Assert.Contains("this.show(true)", js);
        // And the service won't open the board when no spectrum uses it.
        Assert.Contains("AccScope.AudioScopeWanted(", Read("Services/Sdr/AccAudioService.cs"));
    }

    [Fact]
    public void The_scope_controls_load_the_panes_and_the_hub_serves_them()
    {
        Assert.Contains("acc-audio-scope.js", Read("wwwroot/js/ui/radio-scope.js"));

        var js = Read("wwwroot/js/ui/acc-audio-scope.js");
        Assert.Contains("invoke('SubscribeAccAudio')", js);
        Assert.Contains("invoke('UnsubscribeAccAudio')", js);

        var hub = Read("RadioHub.cs");
        Assert.Contains("public async Task<string?> SubscribeAccAudio()", hub);
        Assert.Contains("public async Task UnsubscribeAccAudio()", hub);
        // A page that goes without unsubscribing must not keep the board's port open.
        var disconnected = Regex.Match(hub, @"OnDisconnectedAsync\(Exception\? exception\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.Contains("_accAudio?.Unsubscribe(Context.ConnectionId)", disconnected.Groups["body"].Value);
    }

    [Fact]
    public void The_audio_waterfall_holds_a_whole_ft8_cycle_in_100_pixels()
    {
        // The board sends 25 frames a second; the service passes on every SendEvery'th.
        var service = Read("Services/Sdr/AccAudioService.cs");
        int sendEvery = int.Parse(Regex.Match(service, @"const int SendEvery = (\d+);").Groups[1].Value);
        var js = Read("wwwroot/js/ui/acc-audio-scope.js");
        int framesPerRow = int.Parse(Regex.Match(js, @"export const FRAMES_PER_ROW = (\d+);").Groups[1].Value);

        double secondsIn100Rows = 100.0 * framesPerRow / (25.0 / sendEvery);

        Assert.InRange(secondsIn100Rows, 15, 30);   // an FT8 cycle is 15 seconds
    }

    [Fact]
    public void The_panes_show_the_waterfall_unless_the_oscilloscope_box_is_ticked()
    {
        var partial = Read("Pages/Shared/_RadioScopeButtonsPartial.cshtml");
        Assert.Matches(@"<input type=""checkbox"" class=""acc-audio-osc[ ""]", partial);

        var js = Read("wwwroot/js/ui/acc-audio-scope.js");
        Assert.Contains("querySelector('.acc-audio-osc')", js);
        Assert.Contains("let showOsc = false;", js);
        Assert.Contains("localStorage.getItem(OSC_KEY) === '1'", js);
        Assert.Matches(@"if \(showOsc\) this\._drawOsc\(.*\);\s*else this\._drawFall\(", js);
        // The waterfall gets more of the height than the oscilloscope.
        double fall = double.Parse(Regex.Match(js, @"FFT_SHARE_FALL = ([\d.]+);").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        double osc = double.Parse(Regex.Match(js, @"FFT_SHARE_OSC = ([\d.]+);").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(fall < osc);
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(RepoRoot(), path));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Yaesu_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
