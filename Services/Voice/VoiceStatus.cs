using System.Text.Json.Serialization;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Lifecycle state of the voice recogniser. Broadcast over SignalR as
    /// part of <see cref="VoiceStatusUpdate"/> so the on-screen mic button
    /// can render the right colour. Serialized by name (not the default
    /// numeric System.Text.Json enum encoding) -- both the REST status
    /// endpoint and the SignalR hub protocol use System.Text.Json with no
    /// global enum converter configured, and the frontend (voice-control.js,
    /// the Settings diagnostics panel, the "Test this pack" modal) all
    /// switch on the PascalCase state name.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum VoiceState
    {
        /// <summary>Mic button up; recogniser is constructed but not listening.</summary>
        Idle,
        /// <summary>PTT button held; recogniser is capturing audio.</summary>
        Listening,
        /// <summary>An utterance was recognised and matched a grammar rule.</summary>
        Heard,
        /// <summary>An utterance was heard but didn't match any rule (rejected).</summary>
        Unrecognised,
        /// <summary>The matched intent is being dispatched to the CAT layer.</summary>
        Executing,
        /// <summary>Something failed (no mic, no SAPI engine, grammar load failure).</summary>
        Error,
    }

    /// <summary>
    /// SignalR payload broadcast on every voice state change. <c>LastHeard</c>
    /// is the raw recognised phrase (for sanity-checking what SAPI thought it
    /// heard); <c>LastIntent</c> is the parsed semantic intent name (e.g.
    /// "SetFrequency"); <c>ErrorMessage</c> is set only when <c>State</c> is
    /// <see cref="VoiceState.Error"/>. <c>Confidence</c> is the SAPI match
    /// confidence (0-1) of the last recognition, surfaced for the §6.6
    /// diagnostics panel -- previously enforced (MinConfidence = 0.6f) but
    /// never shown to the user. <c>DryRun</c> is true while a §6.5 "Test this
    /// pack" session is active (recognition runs, no CAT command is sent).
    /// <c>Vfo</c> ("A" or "B") is which VFO's mic button the current/last
    /// listening session targeted -- only one SAPI engine session can be
    /// active at a time, so each VFO's mic button on the Index page filters
    /// this broadcast to know whether it's the one that's live.
    /// </summary>
    public sealed record VoiceStatusUpdate(
        VoiceState State,
        string? LastHeard,
        string? LastIntent,
        string? ErrorMessage,
        float? Confidence = null,
        bool DryRun = false,
        string Vfo = "A"
    );

    /// <summary>
    /// Result of dispatching a voice (or VC-Tune) intent. <c>Success</c> drives
    /// spoken-confirmation status; <c>ConfirmationPhrase</c> is the human-readable
    /// description. <c>IsReadBack</c> = true for status queries — spoken without
    /// appending ", successful".
    /// </summary>
    public record DispatchResult(bool Success, string ConfirmationPhrase, bool IsReadBack = false);
}
