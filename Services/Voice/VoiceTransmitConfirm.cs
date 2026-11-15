namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>A phrase the recogniser matched, ready to run.</summary>
    public sealed record VoiceRecognisedCommand(
        string Intent,
        IReadOnlyDictionary<string, object> Args,
        string Vfo,
        bool DryRun);

    public enum VoiceTransmitStep
    {
        /// <summary>Run <see cref="VoiceTransmitDecision.Command"/> now.</summary>
        Run,
        /// <summary>It would go on the air: hold it and ask the operator to confirm.</summary>
        AskToConfirm,
        /// <summary>It would go on the air, or confirm one, but the match wasn't clear enough.</summary>
        NotClear,
        /// <summary>"Confirm" was heard with nothing waiting for it, or too late.</summary>
        NothingToConfirm,
    }

    public sealed record VoiceTransmitDecision(VoiceTransmitStep Step, VoiceRecognisedCommand Command);

    /// <summary>
    /// A score can't keep a wrong word off the air. The recogniser only says how well
    /// speech fits its list of commands, and "transmission" has scored 0.92 as
    /// "transmit on". So anything that can key the radio has to be asked for twice: it
    /// is held until the operator says "confirm", and the next phrase decides. Anything
    /// else that is recognised drops it, and so does waiting too long.
    ///
    /// A held command keeps the dry run it was heard in and only a session of the same
    /// kind can confirm it, so a live command can't be released from inside a dry run.
    ///
    /// Commands that stop transmitting are not held: <see cref="VoiceTransmitGate"/>
    /// leaves them out, so getting off the air stays as easy as it was.
    /// </summary>
    public sealed class VoiceTransmitConfirm
    {
        public const string ConfirmIntent = "ConfirmTransmit";

        /// <summary>
        /// Long enough to hear the question and press the button again. Measured from
        /// the transmit phrase being recognised, which is after it was said.
        /// </summary>
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(6);

        private readonly object _lock = new();
        private VoiceRecognisedCommand? _held;
        private DateTime _heldAtUtc;

        public VoiceTransmitDecision Decide(
            VoiceRecognisedCommand heard, float confidence, DateTime nowUtc)
        {
            lock (_lock)
            {
                var held = _held;
                var heldAt = _heldAtUtc;
                _held = null;

                if (heard.Intent == ConfirmIntent)
                {
                    bool waiting = held != null && held.DryRun == heard.DryRun && nowUtc - heldAt <= Window;
                    if (!waiting) return new(VoiceTransmitStep.NothingToConfirm, heard);
                    if (confidence < VoiceTransmitGate.MinConfidence) return new(VoiceTransmitStep.NotClear, heard);
                    return new(VoiceTransmitStep.Run, held!);
                }

                heard.Args.TryGetValue("macroCat", out var macroCat);
                if (!VoiceTransmitGate.StartsTransmit(heard.Intent, macroCat as string))
                    return new(VoiceTransmitStep.Run, heard);

                if (confidence < VoiceTransmitGate.MinConfidence)
                    return new(VoiceTransmitStep.NotClear, heard);

                _held = heard;
                _heldAtUtc = nowUtc;
                return new(VoiceTransmitStep.AskToConfirm, heard);
            }
        }

        /// <summary>Drop anything held, for a phrase that never reaches <see cref="Decide"/>.</summary>
        public void Forget()
        {
            lock (_lock) _held = null;
        }
    }
}
