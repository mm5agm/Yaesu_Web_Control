using Yaesu_Web_Control.Services.Sdr;

namespace YaesuWebControl.Tests;

/// <summary>
/// The FT-710's own scope data: cutting frames out of the FT4222 byte stream,
/// turning the MAIN row into levels, and deciding where it sits from the
/// radio's SS span and mode. A wrong answer in any of these is silent on
/// screen - a plausible waterfall at the wrong frequency - which is why they
/// are tested here rather than left to the bench.
///
/// The fixture is a real frame captured from an FT-710 by ON8ST, taken from
/// kd9taw/Nexus (GPL-3.0). These tests show the C# port reads it the way Nexus
/// does; they say nothing about whether a live radio agrees, which is the
/// bench check in docs/design/ft710-native-scope.md.
/// </summary>
public class Ft710ScopeFrameTests
{
    private static byte[] Fixture() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ft710_wf_frame.bin"));

    // ── The captured frame ──────────────────────────────────────────────────

    [Fact]
    public void Fixture_is_one_frame_ending_in_the_trailer()
    {
        var frame = Fixture();

        Assert.Equal(Ft710ScopeFrame.FrameBytes, frame.Length);
        Assert.True(frame.AsSpan(^16).SequenceEqual(Ft710ScopeFrame.Trailer));
        Assert.Equal(Ft710ScopeFrame.FrameBytes, Ft710ScopeFrame.LastFrameEnd(frame));
    }

    [Fact]
    public void Row_stops_before_the_two_zero_padding_bytes()
    {
        // Bytes 850 and 851 are always zero. Inverted, a zero is the strongest
        // level there is, so reading 852 bins would draw a full-scale carrier
        // at the top edge of every span.
        var frame = Fixture();
        Assert.Equal(0, frame[850]);
        Assert.Equal(0, frame[851]);

        var row = Ft710ScopeFrame.ParseMainRowDb(frame);
        Assert.Equal(850, row.Length);
        Assert.DoesNotContain(Ft710ScopeFrame.ByteToDb(0), row);
    }

    [Fact]
    public void Levels_are_inverted_so_the_lowest_byte_is_the_strongest_bin()
    {
        var frame = Fixture();
        var row   = Ft710ScopeFrame.ParseMainRowDb(frame);

        int strongest = Array.IndexOf(row, row.Max());
        int weakest   = Array.IndexOf(row, row.Min());

        // The fixture's lowest byte (80) is at bin 195; its highest is 240.
        Assert.Equal(195, strongest);
        Assert.Equal(80,  frame[strongest]);
        Assert.Equal(240, frame[weakest]);
        Assert.True(row[strongest] > row[weakest]);
    }

    [Fact]
    public void Most_of_the_row_is_noise_well_below_the_strongest_signal()
    {
        // A mostly-dark row, as Nexus checks for this capture. If the bytes
        // were read the wrong way up the noise would be near the top instead.
        var row    = Ft710ScopeFrame.ParseMainRowDb(Fixture());
        float top  = row.Max();
        float mean = row.Average();
        Assert.True(top - mean > 20f, $"mean {mean:F1} dB is only {top - mean:F1} dB below the peak {top:F1} dB");
    }

    [Fact]
    public void Byte_scale_is_half_a_dB_per_step_from_minus_20()
    {
        Assert.Equal(-20f,   Ft710ScopeFrame.ByteToDb(0));
        Assert.Equal(-60f,   Ft710ScopeFrame.ByteToDb(80));
        Assert.Equal(-147.5f, Ft710ScopeFrame.ByteToDb(255));
    }

    [Fact]
    public void A_frame_of_the_wrong_length_is_refused_not_padded()
    {
        Assert.Throws<ArgumentException>(() => Ft710ScopeFrame.ParseMainRowDb(new byte[4095]));
        Assert.Throws<ArgumentException>(() => Ft710ScopeFrame.ParseMainRowDb(new byte[8192]));
    }

    // ── Cutting frames out of the stream ────────────────────────────────────

    [Fact]
    public void A_trailer_with_less_than_a_frame_before_it_is_not_a_frame()
    {
        var frame = Fixture();
        // The tail of one frame and nothing else: the trailer is there, but
        // what is in front of it is not a whole frame.
        Assert.Equal(-1, Ft710ScopeFrame.LastFrameEnd(frame.AsSpan(100)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(850)]
    [InlineData(2048)]
    [InlineData(4095)]
    public void Assembler_recovers_the_frame_at_any_rotation(int rotation)
    {
        // Two SPI reads' worth of back-to-back frames, starting at an
        // arbitrary point in the first one: the radio's frames are not
        // aligned to our reads.
        var frame  = Fixture();
        var stream = Repeat(frame, 3).AsSpan(rotation, 2 * Ft710ScopeFrame.FrameBytes).ToArray();

        var got = new Ft710ScopeFrame.Assembler().Push(stream);

        Assert.NotNull(got);
        Assert.Equal(frame, got);
    }

    [Fact]
    public void Assembler_joins_a_frame_split_across_reads()
    {
        var frame = Fixture();
        var asm   = new Ft710ScopeFrame.Assembler();

        // Start mid-frame, as a first read will: the partial frame before the
        // first trailer is not a frame and must not be returned.
        Assert.Null(asm.Push(frame.AsSpan(3000)));
        Assert.Null(asm.Push(frame.AsSpan(0, 2000)));

        var got = asm.Push(frame.AsSpan(2000));
        Assert.NotNull(got);
        Assert.Equal(frame, got);
        Assert.Equal(0, asm.Buffered);
    }

    [Fact]
    public void Assembler_never_returns_the_same_frame_twice()
    {
        var frame = Fixture();
        var asm   = new Ft710ScopeFrame.Assembler();

        Assert.NotNull(asm.Push(frame));
        Assert.Null(asm.Push(ReadOnlySpan<byte>.Empty));
        Assert.Null(asm.Push(frame.AsSpan(0, 100)));
    }

    [Fact]
    public void Assembler_holds_no_more_than_three_frames_of_unframed_bytes()
    {
        // A stream with no trailers in it at all (a mis-set SPI mode, say)
        // must not grow the buffer without end.
        var asm  = new Ft710ScopeFrame.Assembler();
        var junk = new byte[5000];
        for (int i = 0; i < 5; i++) Assert.Null(asm.Push(junk));
        Assert.Equal(Ft710ScopeFrame.Assembler.Capacity, asm.Buffered);
    }

    // ── Device keys ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("yaesu-scope:ft710", true)]
    [InlineData("YAESU-SCOPE:ft710", true)]
    [InlineData("sdrplay:1234", false)]
    [InlineData("driver=rtlsdr,serial=1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Scope_keys_are_told_apart_from_SDR_keys(string? key, bool expected) =>
        Assert.Equal(expected, Ft710ScopeFrame.IsScopeKey(key));

    [Theory]
    [InlineData("FT-710", true)]
    [InlineData("FTdx10", false)]
    [InlineData("FTdx101MP", false)]
    [InlineData(null, false)]
    public void Only_the_FT710_has_the_bridge(string? model, bool expected) =>
        Assert.Equal(expected, Ft710ScopeFrame.ModelHasBridge(model));

    // ── Placement from SS05 / SS06 ──────────────────────────────────────────

    [Theory]
    [InlineData('0', 1_000)]
    [InlineData('3', 10_000)]
    [InlineData('5', 50_000)]
    [InlineData('9', 1_000_000)]
    public void Span_codes_follow_the_FT710_CAT_manual(char code, long hz) =>
        Assert.Equal(hz, Ft710ScopePlacement.SpanHz(code));

    [Fact]
    public void An_unknown_span_code_is_not_guessed()
    {
        Assert.Null(Ft710ScopePlacement.SpanHz('A'));
        Assert.False(Ft710ScopePlacement.Resolve('A', '3').CanPlace);
    }

    [Theory]
    [InlineData('0')]   // 3DSS CENTER
    [InlineData('3')]   // W/F CENTER EXPAND
    [InlineData('4')]   // W/F CENTER NORMAL
    public void Center_modes_place_the_row_at_the_radio_span(char mode)
    {
        var r = Ft710ScopePlacement.Resolve('5', mode);
        Assert.True(r.CanPlace);
        Assert.Equal(50_000, r.SpanHz);
    }

    [Theory]
    [InlineData('1', "CURSOR")]   // 3DSS CURSOR
    [InlineData('6', "CURSOR")]   // W/F CURSOR
    [InlineData('7', "CURSOR")]
    [InlineData('2', "FIX")]      // 3DSS FIX
    [InlineData('9', "FIX")]      // W/F FIX
    [InlineData('A', "FIX")]
    public void Cursor_and_fix_modes_are_held_back_with_the_reason(char mode, string named)
    {
        var r = Ft710ScopePlacement.Resolve('5', mode);
        Assert.False(r.CanPlace);
        Assert.Contains(named, r.Problem);
        Assert.Contains("CENTER", r.Problem);
    }

    [Theory]
    [InlineData('5')]   // documented as "-" on the FT-710
    [InlineData('8')]
    [InlineData('B')]
    [InlineData('?')]
    public void Undocumented_mode_codes_are_held_back(char mode) =>
        Assert.False(Ft710ScopePlacement.Resolve('5', mode).CanPlace);

    [Fact]
    public void Nothing_is_placed_until_both_settings_have_been_read()
    {
        Assert.False(Ft710ScopePlacement.Resolve(null, null).CanPlace);
        Assert.False(Ft710ScopePlacement.Resolve('5', null).CanPlace);
        Assert.False(Ft710ScopePlacement.Resolve(null, '3').CanPlace);
    }

    private static byte[] Repeat(byte[] frame, int times)
    {
        var all = new byte[frame.Length * times];
        for (int i = 0; i < times; i++) frame.CopyTo(all, i * frame.Length);
        return all;
    }
}
