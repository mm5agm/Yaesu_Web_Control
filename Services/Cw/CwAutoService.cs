using RadioWebControl.Core.Services.Cw;

namespace Yaesu_Web_Control.Services.Cw
{
    /// <summary>
    /// The CW reader's Auto button: find the station, move the dial so its
    /// tone sits on the pitch, then set the filter to suit the speed it is
    /// keying at. What RTTY Auto does for RTTY.
    ///
    /// Two stages. <b>Coarse</b>, when the decoder can hear a station but
    /// cannot read it - on the bench, 470 Hz off pitch in a 2 kHz filter with
    /// neighbours either side: move onto the tone the tracker keeps returning
    /// to and close the filter to 500 Hz, which is what lets the decoder get a
    /// readable lock at all. <b>Fine</b>, once it is readable: move by the
    /// decoder's settled zero-in reading and set the width for the speed. A
    /// station already readable skips the coarse stage.
    ///
    /// <b>The order within each stage is the point.</b> The dial moves first,
    /// because a station 120 Hz off pitch is still inside a 500 Hz filter and
    /// would be outside a 250 Hz one: narrowing first throws away the signal
    /// Auto has just found.
    ///
    /// The dial move uses the decoder's own readings rather than the radio's
    /// ZI command. ZI centres on whatever the radio's own detector picks,
    /// which with two stations in the passband need not be the one the reader
    /// is copying.
    ///
    /// The filter and APF go through Reader Mode, so Stop puts them back. The
    /// dial does not go back: that would take the operator off the station.
    /// </summary>
    public sealed class CwAutoService
    {
        /// <summary>How long each search waits before giving up.</summary>
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

        /// <summary>How long to look for an already-readable station before going coarse.</summary>
        private static readonly TimeSpan FineFirst = TimeSpan.FromSeconds(2);

        /// <summary>Time for the tone tracker to follow a dial move.</summary>
        private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);

        /// <summary>The tracker confidence the coarse stage will act on - CwZeroIn's floor.</summary>
        private const double MinConfidence = 0.5;

        private readonly CwReaderService _reader;
        private readonly CwReaderModeService _readerMode;
        private readonly ICatClient _cat;
        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;
        private readonly ILogger<CwAutoService> _logger;

        // One press at a time. Two overlapping would both measure the offset
        // before either moved the dial, and move it twice.
        private readonly SemaphoreSlim _gate = new(1, 1);

        public CwAutoService(CwReaderService reader,
                             CwReaderModeService readerMode,
                             ICatClient cat,
                             RadioStateService state,
                             ISettingsService settings,
                             ILogger<CwAutoService> logger)
        {
            _reader     = reader;
            _readerMode = readerMode;
            _cat        = cat;
            _state      = state;
            _settings   = settings;
            _logger     = logger;
        }

        public async Task<CwAutoResult> RunAsync(CancellationToken ct = default)
        {
            if (!await _gate.WaitAsync(0, ct))
                return CwAutoResult.Fail("Auto is already looking.");
            try
            {
                if (!IsCw(_state.ModeA))
                    return CwAutoResult.Fail(
                        $"Auto works in CW and the radio is in {_state.ModeA ?? "an unknown mode"}. " +
                        "Switch to CW, or press Reader Mode, then Auto.");
                if (_state.IsTransmitting)
                    return CwAutoResult.Fail("Not while transmitting.");

                if (!_reader.IsRunning) await _reader.StartAsync(ct);

                var result = new CwAutoResult();

                // Fine first: a station already readable needs no coarse step,
                // and the coarse step's 500 Hz would be a step backwards for an
                // operator already at 250.
                var fine = await WaitForReadableAsync(DateTime.UtcNow + FineFirst, ct);

                if (fine is null)
                {
                    var tone = await WaitForSteadyToneAsync(DateTime.UtcNow + Wait, ct);
                    if (tone is null)
                        return CwAutoResult.Fail(
                            $"No steady CW tone in {Wait.TotalSeconds:F0} s, so nothing was changed. " +
                            "Auto needs a station keying - a carrier, noise, or two stations " +
                            "of the same strength will not settle.");

                    result.MovedHz = await MoveDialAsync(tone.Value, "coarse", ct);
                    result.ReaderMode = await _readerMode.ApplyForAutoAsync(CwAutoTune.CoarseWidthHz, ct);
                    result.WidthHz = result.ReaderMode.IfWidthHz;

                    // The tracker needs a moment to follow the tone onto the
                    // pitch, and its readings until then are of the old tone.
                    await Task.Delay(Settle, ct);
                    fine = await WaitForReadableAsync(DateTime.UtcNow + Wait, ct);
                    if (fine is null)
                    {
                        result.Ok = true;
                        result.Message =
                            $"Auto: moved {Signed(result.MovedHz)} Hz onto the strongest tone and set the filter to " +
                            $"{result.WidthHz?.ToString() ?? "?"} Hz, but it is not readable yet, so the width " +
                            "was not set for its speed. Press Auto again once it is copying. Stop puts your filter back.";
                        return result;
                    }
                }

                var (offsetHz, wpm) = fine.Value;
                result.Ok = true;
                result.Wpm = Math.Round(wpm);

                // The dial before the filter - see the class comment for why.
                if (await MoveDialAsync(offsetHz, "fine", ct) is { } m)
                    result.MovedHz = (result.MovedHz ?? 0) + m;

                var settings = await _settings.GetSettingsAsync();
                int widthHz = CwAutoTune.WidthForSpeedHz(wpm, settings.CwReaderFilterHz);
                result.ReaderMode = await _readerMode.ApplyForAutoAsync(widthHz, ct);
                result.WidthHz = result.ReaderMode.IfWidthHz;

                _logger.LogInformation("CW Auto: {Wpm:F0} wpm, asked for {Wanted} Hz, radio reports {Width}",
                                       wpm, widthHz, result.WidthHz?.ToString() ?? "unknown");

                result.Message = Describe(result);
                return result;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Moves VFO A by <paramref name="offsetHz"/>, read-then-write. Returns
        /// the whole-Hz move made, or null if none was.
        /// </summary>
        private async Task<long?> MoveDialAsync(double offsetHz, string stage, CancellationToken ct)
        {
            if (CwAutoTune.DialMoveHz(offsetHz) is not { } move || _state.IsTransmitting) return null;

            // Read, do not assume: this is written back as an absolute figure,
            // so a stale cached frequency would move the radio somewhere it has
            // already been.
            long from = await _cat.QueryFrequencyAAsync("CwAuto", ct);
            if (from <= 0) return null;

            long to = from + move;
            await _cat.SetFrequencyAAsync(to);
            _state.FrequencyA = to;
            _logger.LogInformation("CW Auto ({Stage}): tone {Offset:+0;-0} Hz off pitch - moved VFO A {From} to {To}",
                                   stage, offsetHz, from, to);
            return move;
        }

        /// <summary>
        /// Polls the reader until it has given <see cref="CwAutoTune.SamplesWanted"/>
        /// readings in a row with the station locked and readable, and returns
        /// the median offset and the latest speed. A reading that is not locked
        /// or not readable starts the count again: the fine stage acts on a
        /// station the reader is copying, not one it glimpsed.
        /// </summary>
        private async Task<(double OffsetHz, double Wpm)?> WaitForReadableAsync(DateTime until, CancellationToken ct)
        {
            var offsets = new List<double>();
            while (DateTime.UtcNow < until)
            {
                var snap = _reader.Snapshot(long.MaxValue);
                if (snap.IsLocked && snap.Readability == nameof(CwReadability.Readable)
                    && snap.ZeroInOffsetHz is { } o)
                {
                    offsets.Add(o);
                    if (offsets.Count >= CwAutoTune.SamplesWanted)
                        return (CwAutoTune.SettledOffsetHz(offsets) ?? 0, snap.WordsPerMinute);
                }
                else
                {
                    offsets.Clear();
                }
                await Task.Delay(Tick, ct);
            }
            return null;
        }

        /// <summary>
        /// The coarse stage's reading: the tone tracker, with no readability
        /// test, until its last few readings agree. Returns the dial offset
        /// that puts that tone on the pitch.
        /// </summary>
        private async Task<double?> WaitForSteadyToneAsync(DateTime until, CancellationToken ct)
        {
            var tones = new Queue<double>();
            while (DateTime.UtcNow < until)
            {
                var spec = _reader.Spectrum();
                if (spec.SignalPresent && spec.Confidence >= MinConfidence && spec.ToneHz > 0)
                {
                    tones.Enqueue(spec.ToneHz);
                    while (tones.Count > CwAutoTune.SamplesWanted) tones.Dequeue();

                    if (tones.Count == CwAutoTune.SamplesWanted
                        && CwAutoTune.SteadyToneHz(tones.ToArray()) is { } tone)
                    {
                        // No 500 Hz cap here, unlike the zero-in display: the
                        // tracker only looks inside the search window, so
                        // anything it reports is somewhere the operator's own
                        // filter already lets through.
                        var offset = CwZeroIn.ComputeOffsetHz(
                            measuredToneHz: tone,
                            targetPitchHz:  spec.PitchHz,
                            lowerSideband:  _state.ModeA == "CW-L",
                            maxOffsetHz:    2000,
                            confidence:     spec.Confidence,
                            minConfidence:  MinConfidence);
                        _logger.LogInformation(
                            "CW Auto (coarse): steady tone {Tone:F0} Hz, pitch {Pitch:F0} Hz, confidence {Conf:F2}",
                            tone, spec.PitchHz, spec.Confidence);
                        if (offset is not null) return offset;
                    }
                }
                else
                {
                    tones.Clear();
                }
                await Task.Delay(Tick, ct);
            }
            return null;
        }

        private static string Describe(CwAutoResult r)
        {
            string dial = r.MovedHz is { } m && m != 0
                ? $"moved the dial {Signed(m)} Hz onto pitch"
                : "already on pitch";
            string width = r.WidthHz is { } w ? $"filter {w} Hz" : "filter unchanged";
            string apf = r.ReaderMode?.ApfOn == true ? ", APF on" : "";
            return $"Auto: {r.Wpm:F0} wpm, {dial}, {width}{apf}. Stop puts your filter back.";
        }

        private static string Signed(long? hz) => hz is { } h ? $"{(h > 0 ? "+" : "")}{h}" : "0";

        private static bool IsCw(string? mode) => mode is "CW-U" or "CW-L";
    }

    public sealed class CwAutoResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public double Wpm { get; set; }
        public long? MovedHz { get; set; }
        public int? WidthHz { get; set; }

        /// <summary>Reader Mode's state afterwards, so the panel's button follows.</summary>
        public CwReaderModeStatus? ReaderMode { get; set; }

        public static CwAutoResult Fail(string message) => new() { Ok = false, Message = message };
    }
}
