using System.Globalization;
using RadioWebControl.Core.Services;
using RadioWebControl.Core.Services.Cw;

namespace Yaesu_Web_Control.Services.Cw
{
    /// <summary>
    /// Turns what the reader copied into a line in an ADIF log.
    ///
    /// Two halves, and the split matters. <see cref="Suggest"/> reads the
    /// decoded text and the live radio state and proposes a draft;
    /// <see cref="SaveAsync"/> writes exactly what the operator confirmed.
    /// Nothing is ever logged from the suggestion directly. Section 4.11h
    /// measured the decoder reporting full confidence on 592 characters of
    /// junk, so a callsign picked out of the copy is a starting point for the
    /// operator to correct, never a fact.
    ///
    /// The file is plain ADIF in the app data folder. Log4OM and GridTracker
    /// both watch ADIF files, so writing one reaches those users with no
    /// integration to build - which is why this is not waiting on the logger
    /// question that is still open with Steve K3FZT.
    /// </summary>
    public sealed class CwQsoLogService
    {
        private readonly CwReaderService _reader;
        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;
        private readonly ILogger<CwQsoLogService> _logger;
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public CwQsoLogService(CwReaderService reader,
                               RadioStateService state,
                               ISettingsService settings,
                               ILogger<CwQsoLogService> logger)
        {
            _reader   = reader;
            _state    = state;
            _settings = settings;
            _logger   = logger;
        }

        /// <summary>The log file, whether or not it exists yet.</summary>
        public static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "ywc-log.adi");

        /// <summary>
        /// A draft QSO built from the recent copy and the radio's current
        /// state, for the operator to correct.
        /// </summary>
        /// <param name="lookBackChars">
        /// How much of the copy to read. A QSO's callsign and report arrive in
        /// the first over, so reading the whole retained buffer would offer
        /// candidates from the previous contact as readily as from this one.
        /// </param>
        public async Task<CwQsoDraft> SuggestAsync(int lookBackChars = 600)
        {
            var snap = _reader.Snapshot(0);
            string text = snap.Text.Length > lookBackChars
                ? snap.Text[^lookBackChars..]
                : snap.Text;

            var settings = await _settings.GetSettingsAsync();

            double mhz = _state.FrequencyA / 1_000_000.0;
            return new CwQsoDraft
            {
                Text          = text,
                WhenUtc       = DateTime.UtcNow,
                FrequencyMhz  = mhz > 0 ? mhz : null,
                Band          = mhz > 0 ? AdifRecordWriter.BandFor(mhz) : null,
                Mode          = "CW",

                // The operator's own callsign is not a setting in its own
                // right. The cluster login is the same callsign for everyone
                // who has set one, so it is offered - and it is offered as a
                // default the operator can overwrite, not asserted.
                StationCall   = Blank(settings.DxClusterLoginCallsign),

                Callsigns     = Map(CwQsoFields.Callsigns(text)),
                SignalReports = Map(CwQsoFields.SignalReports(text)),
                Names         = Map(CwQsoFields.Names(text)),
                Locations     = Map(CwQsoFields.Locations(text)),

                // What we send is not in the copy - it is what the operator
                // decided - so it is a convention, not a suggestion.
                RstSent       = "599",
            };
        }

        /// <summary>
        /// Appends a confirmed QSO to the log, writing the ADIF header if the
        /// file is new.
        /// </summary>
        public async Task<CwQsoSaveResult> SaveAsync(CwQsoSave qso, CancellationToken ct = default)
        {
            if (qso is null) throw new ArgumentNullException(nameof(qso));
            if (string.IsNullOrWhiteSpace(qso.Callsign))
                throw new ArgumentException("A QSO needs a callsign.", nameof(qso));

            double? mhz = qso.FrequencyMhz;
            if (mhz is null && _state.FrequencyA > 0) mhz = _state.FrequencyA / 1_000_000.0;

            var record = new AdifRecordWriter.Qso
            {
                Callsign     = qso.Callsign,
                WhenUtc      = qso.WhenUtc ?? DateTime.UtcNow,
                EndUtc       = qso.EndUtc,
                FrequencyMhz = mhz,
                Band         = Blank(qso.Band),
                Mode         = Blank(qso.Mode) ?? "CW",
                RstSent      = Blank(qso.RstSent),
                RstReceived  = Blank(qso.RstReceived),
                Name         = Blank(qso.Name),
                Qth          = Blank(qso.Qth),
                Comment      = Blank(qso.Comment),
                StationCall  = Blank(qso.StationCall),
                OperatorCall = Blank(qso.OperatorCall),
            };

            string path = LogPath;
            string text = AdifRecordWriter.Write(record);

            await _writeGate.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // The header goes in once, when the file is created. Appending
                // it again would put a second <EOH> in the middle of the log,
                // and a reader that honours it would discard everything before.
                if (!File.Exists(path))
                    text = AdifRecordWriter.Header("Yaesu Web Control",
                                                   AppVersion.Current) + text;

                await File.AppendAllTextAsync(path, text, ct);
            }
            finally
            {
                _writeGate.Release();
            }

            _logger.LogInformation("Logged {Call} on {Band} to {Path}",
                                   record.Callsign, record.Band ?? "?", path);

            return new CwQsoSaveResult { Path = path, Adif = text.TrimEnd('\n') };
        }

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static List<CwQsoSuggestion> Map(IReadOnlyList<CwQsoFields.Candidate> src)
            => src.Take(5)
                  .Select(c => new CwQsoSuggestion { Value = c.Value, Score = c.Score, Why = c.Why })
                  .ToList();
    }

    /// <summary>One suggested value, with the evidence that ranked it.</summary>
    public sealed class CwQsoSuggestion
    {
        public string Value { get; init; } = "";
        public double Score { get; init; }

        /// <summary>
        /// Why this was suggested - "follows DE", "sent 3 times". Shown beside
        /// the value so the operator can judge it rather than trust it.
        /// </summary>
        public string Why { get; init; } = "";
    }

    /// <summary>A QSO the operator has not confirmed yet.</summary>
    public sealed class CwQsoDraft
    {
        /// <summary>The copy the suggestions were drawn from.</summary>
        public string Text { get; init; } = "";

        public DateTime WhenUtc { get; init; }
        public double? FrequencyMhz { get; init; }
        public string? Band { get; init; }
        public string? Mode { get; init; }
        public string? StationCall { get; init; }
        public string? RstSent { get; init; }

        public List<CwQsoSuggestion> Callsigns { get; init; } = new();
        public List<CwQsoSuggestion> SignalReports { get; init; } = new();
        public List<CwQsoSuggestion> Names { get; init; } = new();
        public List<CwQsoSuggestion> Locations { get; init; } = new();
    }

    /// <summary>What the operator confirmed.</summary>
    public sealed class CwQsoSave
    {
        public string Callsign { get; set; } = "";
        public DateTime? WhenUtc { get; set; }
        public DateTime? EndUtc { get; set; }
        public double? FrequencyMhz { get; set; }
        public string? Band { get; set; }
        public string? Mode { get; set; }
        public string? RstSent { get; set; }
        public string? RstReceived { get; set; }
        public string? Name { get; set; }
        public string? Qth { get; set; }
        public string? Comment { get; set; }
        public string? StationCall { get; set; }
        public string? OperatorCall { get; set; }
    }

    public sealed class CwQsoSaveResult
    {
        public string Path { get; init; } = "";

        /// <summary>The record as written, so the UI can show what was logged.</summary>
        public string Adif { get; init; } = "";
    }
}
