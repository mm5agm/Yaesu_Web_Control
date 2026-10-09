namespace Yaesu_Web_Control.Services.Rtty
{
    /// <summary>
    /// The radio's own RTTY tone settings - MARK FREQUENCY and SHIFT FREQUENCY
    /// in the extended menu - read and written over CAT.
    ///
    /// <para>This is the counterpart of IWC's <c>GetRttyToneSettingsAsync</c> /
    /// <c>SetRttyToneSettingsAsync</c> on its radio seam, and it exists as a
    /// service here rather than as more code in <c>CatController</c> for two
    /// reasons: the tuner's "From radio" button and the tuner's own push both
    /// need it, and the EX addresses are per-model Yaesu register plumbing which
    /// has no business anywhere near <c>core/</c>. The addresses themselves live
    /// in <see cref="RttyToneMap"/>, which was already here for click-to-tune in
    /// RTTY and which this only adds the write direction to.</para>
    ///
    /// <para><b>The menu is a short list of indexes, not a pair of numbers.</b>
    /// Mark can be 1275 or 2125 and nothing else; shift can be 170, 200, 425 or
    /// 850. The tuner deliberately offers 450 as well, because the DWD weather
    /// stations send it all day and the decoder here copies it perfectly - so a
    /// push will routinely have no rung for what the operator has set. That is
    /// reported as a null rather than snapped to the nearest rung: snapping
    /// would quietly set the radio's own decoder to a shift the operator is not
    /// copying, which is worse than leaving it alone and saying so.</para>
    ///
    /// <para><b>Nothing here checks the mode.</b> The caller does, because the
    /// answer differs: a read in an AFSK mode is still worth having with a note
    /// attached, while a write in an AFSK mode must not happen at all - those
    /// menu items describe how the radio transmits FSK, and a receive-side
    /// tuning aid has no business changing that.</para>
    /// </summary>
    public sealed class RttyRadioToneService
    {
        private readonly ICatClient _cat;
        private readonly ISettingsService _settings;
        private readonly ILogger<RttyRadioToneService> _logger;

        public RttyRadioToneService(ICatClient cat,
                                    ISettingsService settings,
                                    ILogger<RttyRadioToneService> logger)
        {
            _cat = cat;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>What the radio's menu says, or null if it could not be read.</summary>
        public sealed record Tones(int MarkHz, int ShiftHz, bool PolarityRev);

        /// <summary>
        /// What a push managed. A null member means this radio's menu has no
        /// rung for that figure - see the class notes.
        /// </summary>
        public sealed record Written(int? MarkHz, int? ShiftHz);

        public async Task<Tones?> ReadAsync(CancellationToken ct = default)
        {
            var settings = await _settings.GetSettingsAsync();
            var addr = RttyToneMap.For(settings.RadioModel);
            if (addr == null) return null;
            if (!_cat.IsConnected) return null;

            try
            {
                var markHz  = RttyToneMap.MarkHzFromCode(await ReadExAsync(addr.MarkFreq, ct));
                var shiftHz = RttyToneMap.ShiftHzFromCode(await ReadExAsync(addr.ShiftFreq, ct));
                if (markHz == null && shiftHz == null) return null;

                bool rev = false;
                if (addr.PolarityRx != null)
                {
                    var pol = await ReadExAsync(addr.PolarityRx, ct);
                    rev = pol?.TrimStart('0') == "1";
                }

                // A half-answer is still useful: the figure that did come back is
                // the radio's, and the other falls back to Yaesu's default, which
                // is what the overwhelming majority run.
                return new Tones(markHz ?? RttyToneMap.DefaultMarkHz,
                                 shiftHz ?? RttyToneMap.DefaultShiftHz,
                                 rev);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the radio's RTTY tone settings");
                return null;
            }
        }

        /// <summary>
        /// Push a mark and a shift into the radio's menu. Returns which of the
        /// two the radio had a rung for; both null means neither did, and the
        /// menu was not written at all.
        /// </summary>
        public async Task<Written> WriteAsync(int markHz, int shiftHz, CancellationToken ct = default)
        {
            var settings = await _settings.GetSettingsAsync();
            var addr = RttyToneMap.For(settings.RadioModel);
            if (addr == null || !_cat.IsConnected) return new Written(null, null);

            int? wroteMark = null, wroteShift = null;

            if (addr.MarkFreq != null && RttyToneMap.CodeForMarkHz(markHz) is { } markCode)
            {
                await WriteExAsync(addr.MarkFreq, markCode, ct);
                wroteMark = markHz;
            }

            if (addr.ShiftFreq != null && RttyToneMap.CodeForShiftHz(shiftHz) is { } shiftCode)
            {
                await WriteExAsync(addr.ShiftFreq, shiftCode, ct);
                wroteShift = shiftHz;
            }

            if (wroteMark != null || wroteShift != null)
                _logger.LogInformation(
                    "RTTY menu written: mark {Mark}, shift {Shift}",
                    wroteMark?.ToString() ?? "(no rung)",
                    wroteShift?.ToString() ?? "(no rung)");

            return new Written(wroteMark, wroteShift);
        }

        /// <summary>
        /// One EX item by its raw address (the text after "EX"), or null if the
        /// radio did not answer in the expected shape. The same reader
        /// <c>CatController</c> uses, kept here as well rather than shared from
        /// there because that one is private to a controller and this service
        /// has no business reaching into one.
        /// </summary>
        private async Task<string?> ReadExAsync(string? address, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(address)) return null;

            var response = await _cat.SendCommandAsync($"EX{address};", "RttyTones", ct);
            if (string.IsNullOrWhiteSpace(response)) return null;

            var body = response.TrimEnd(';');
            if (!body.StartsWith("EX", StringComparison.OrdinalIgnoreCase)) return null;
            body = body.Substring(2);
            if (!body.StartsWith(address, StringComparison.OrdinalIgnoreCase)) return null;

            var code = body.Substring(address.Length);
            return code.Length == 0 ? null : code;
        }

        private Task WriteExAsync(string address, string code, CancellationToken ct)
            => _cat.SendCommandAsync($"EX{address}{code};", "RttyTones", ct);
    }
}
