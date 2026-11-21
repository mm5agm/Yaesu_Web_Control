// Yaesu Web Control — the FTdx101's audio scope, from its ACC socket.
//
// Every frame from the ACC board carries both receivers' audio scopes as the
// radio's MULTI screen draws them: an AF-FFT of 200 points, 20 Hz apart (0 to
// 3,980 Hz), and an oscilloscope of 200 columns, each the highest and lowest
// sample in its time. The radio has one oscilloscope time and one pair of
// attenuators for both receivers. YaesuAccScope reads them (AudioScope in
// AccFrame.cs, docs/protocol.md in mm5agm/Yaesu_ACC_Spectrum); this turns a
// frame into what the browser draws.

using YaesuAccScope;

namespace Yaesu_Web_Control.Services.Sdr;

/// <summary>One receiver's audio scope for the browser.</summary>
/// <param name="Fft">200 AF-FFT points, 0 to 3,980 Hz, higher = stronger.</param>
/// <param name="Osc">200 oscilloscope columns as max, min pairs (400 bytes), 128 the middle.</param>
public sealed record AccAudioReceiver(byte[] Fft, byte[] Osc);

/// <summary>
/// What a page gets on RadioStateUpdate as property "AccAudio": both
/// receivers and the shared settings, or only <see cref="Problem"/> when
/// there are no frames to show. On the FTdx10 Sub is null (one receiver) and
/// <see cref="HasSettings"/> is false: nobody has found where its frames
/// keep the attenuators and oscilloscope time, so the page doesn't show them.
/// </summary>
public sealed record AccAudioFrame(
    AccAudioReceiver? Main,
    AccAudioReceiver? Sub,
    int FftAtt,
    int OscAtt,
    int OscTime,
    string? Problem,
    bool HasSettings = true);

public static class AccAudio
{
    /// <summary>A frame's audio scopes, ready to send.</summary>
    public static AccAudioFrame Build(AccFrame frame) => frame.Radio == AccRadio.Ftdx101
        ? new(
            Receiver(frame.MainAudio),
            Receiver(frame.SubAudio),
            frame.MainAudio.FftAttenuator,
            frame.MainAudio.OscAttenuator,
            frame.MainAudio.OscTime,
            null)
        : new(
            Receiver(frame.MainAudio),
            frame.HasSub ? Receiver(frame.SubAudio) : null,
            0, 0, 0,
            null,
            HasSettings: false);

    /// <summary>Nothing to draw, and why.</summary>
    public static AccAudioFrame Problem(string problem) => new(null, null, 0, 0, 0, problem);

    /// <summary>
    /// What to tell the operator when no frames are coming: the reader's own
    /// problem when it has one, or what it's doing.
    /// </summary>
    public static string ProblemText(AccScopeState state) =>
        state.Problem ?? state.Status switch
        {
            AccScopeStatus.Reading => $"The ACC board on {state.Port} is sending no frames. Is the radio on?",
            _                      => "Looking for the ACC board...",
        };

    private static AccAudioReceiver Receiver(AudioScope audio)
    {
        var osc = new byte[AudioScope.OscColumns * 2];
        for (int c = 0; c < AudioScope.OscColumns; c++)
        {
            osc[2 * c]     = audio.OscMax(c);
            osc[2 * c + 1] = audio.OscMin(c);
        }
        return new AccAudioReceiver(audio.Fft, osc);
    }
}
