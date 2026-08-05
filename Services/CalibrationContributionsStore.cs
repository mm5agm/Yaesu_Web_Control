using System.Text.Json;
using Yaesu_Web_Control.Models.Calibration;
using Microsoft.Extensions.Hosting;

namespace Yaesu_Web_Control.Services;

// Who sent us which calibration numbers, and what the shipped default should
// therefore be. See docs/design/calibration-contributions-port-from-iwc.md.
//
// The problem this solves: importing straight into the shipped default is
// last-write-wins. The second contributor silently erases the first, one
// mis-measured radio becomes everyone's default, and there is no record of
// where any number came from. Here every contribution is kept, and the shipped
// value is DERIVED from all of them by taking the median per point — so an
// outlier is outvoted rather than obeyed, and a bad contribution is undone by
// setting "excluded": true and recomputing.
//
// Development artefact, tracked in git, never shipped: calibration-contributions/
// must not appear in wwwroot, the .csproj publish items, or installer.nsi.

// Where the contributors disagreed, so the developer can see it before committing.
public record PointSpread(string Meter, string Label, double Min, double Max, double Chosen, int N);

// Everything the caller needs to know about one recompute.
public class RecomputeResult
{
    // meter name → the per-point raw values the shipped default should now hold.
    // A meter is ABSENT when nothing usable was contributed for it, which means
    // "leave the shipped placeholder alone" — never "set it to zero".
    public Dictionary<string, List<double>> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<PointSpread> Spread { get; } = new();

    // Contributed meters that were thrown away, with the reason. Not errors —
    // the import still succeeds — but they need reading before committing.
    public List<string> Refused { get; } = new();

    // Contributed meters whose point labels/count don't match the shipped
    // table. Reported, never guessed at.
    public List<string> Structural { get; } = new();

    // How many contributions supplied at least one usable meter.
    public int Contributors { get; set; }
}

// Callsign and note for the contribution being recorded by this import.
public record ContributionMeta(string? From, string? Note);

public class CalibrationContributionsStore
{
    private readonly string _dir;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public CalibrationContributionsStore(IHostEnvironment hostEnvironment)
    {
        // Alongside the source, NOT under wwwroot — this must never be served
        // or published. Only ever written by the dev-gated import path, so on
        // an installed copy (where ContentRoot is Program Files) it is never
        // touched.
        _dir = Path.Combine(hostEnvironment.ContentRootPath, "calibration-contributions");
    }

    public string DirectoryPath => _dir;

    public string PathFor(string model) => Path.Combine(_dir, $"{model}.json");

    public CalibrationContributions Load(string model)
    {
        var path = PathFor(model);
        if (!File.Exists(path))
            return new CalibrationContributions { Model = model };
        try
        {
            var store = JsonSerializer.Deserialize<CalibrationContributions>(
                File.ReadAllText(path), ReadOptions) ?? new CalibrationContributions();
            if (string.IsNullOrWhiteSpace(store.Model)) store.Model = model;
            return store;
        }
        catch
        {
            // A corrupt store must not let the import fall back to last-write-wins
            // — that would silently reintroduce the exact bug this file prevents.
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} could not be parsed. Fix or restore it from git before importing.");
        }
    }

    public void Save(CalibrationContributions store)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathFor(store.Model), CollapseScalarArrays(JsonSerializer.Serialize(store, WriteOptions)));
    }

    // WriteIndented puts every array element on its own line, which turns a
    // six-point calibration vector into six lines and a placeholder history into
    // a wall. This file exists to be read in a git diff, so put arrays of plain
    // numbers and strings back on one line. Only matches arrays whose elements
    // are bare numbers or simple quoted strings — nothing nested, nothing
    // escaped — so it cannot mangle the structure.
    private static readonly System.Text.RegularExpressions.Regex ScalarArrayRe = new(
        @"\[\s*((?:-?\d+(?:\.\d+)?|""[^""\\\r\n]*"")(?:\s*,\s*(?:-?\d+(?:\.\d+)?|""[^""\\\r\n]*""))*)\s*\]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Re-scanned for elements rather than split on ',' — a label or note is
    // allowed to contain a comma, and splitting would insert a space inside it.
    private static readonly System.Text.RegularExpressions.Regex ScalarElemRe = new(
        @"-?\d+(?:\.\d+)?|""[^""\\\r\n]*""",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string CollapseScalarArrays(string json) =>
        ScalarArrayRe.Replace(json, m =>
            "[" + string.Join(", ", ScalarElemRe.Matches(m.Groups[1].Value).Select(x => x.Value)) + "]");

    // ── Placeholders ──────────────────────────────────────────────────────────
    //
    // Every value-vector the shipped default has ever held. A contributor who
    // calibrated only the S-meter still sends back a full file, with the shipped
    // placeholders untouched in every other meter; without this we would count
    // those echoes as seven independent measurements agreeing with each other.

    public static bool MatchesPlaceholder(
        CalibrationContributions store, string meterName, IReadOnlyList<double> raw) =>
        store.Placeholders.TryGetValue(meterName, out var known) &&
        known.Any(v => v.Count == raw.Count && v.SequenceEqual(raw));

    // Record the file's current vectors as placeholders. Called with the shipped
    // default both before and after the surgery, so today's shipped numbers —
    // including ones we derived ourselves — are recognised when they come back
    // in someone's user file tomorrow. Never prunes: an old placeholder still
    // needs recognising while anyone is running an old release.
    public static void RememberPlaceholders(CalibrationContributions store, CalibrationFile file)
    {
        foreach (var meter in file.Meters)
        {
            var raw = meter.Points.Select(p => p.Raw).ToList();
            if (raw.Count == 0) continue;
            if (!store.Placeholders.TryGetValue(meter.Name, out var known))
                store.Placeholders[meter.Name] = known = new List<List<double>>();
            if (!known.Any(v => v.Count == raw.Count && v.SequenceEqual(raw)))
                known.Add(raw);
        }
    }

    // ── Recording a contribution ──────────────────────────────────────────────

    // Turn an incoming calibration file into a contribution record, flagging the
    // meters that are just the shipped placeholders echoed back.
    //
    // ONE CONTRIBUTION PER CALLSIGN PER MODEL: if this callsign has contributed
    // before, the new numbers replace the old ones rather than being added
    // alongside them. Otherwise re-importing the same person's file — which is
    // exactly what happens when Colin promotes his own bench calibration twice —
    // would give them two votes in the median. Anonymous contributions (no
    // callsign) can't be matched up, so they always append.
    public static Contribution Record(
        CalibrationContributions store, CalibrationFile incoming, ContributionMeta? meta, string appVersion)
    {
        var from = string.IsNullOrWhiteSpace(meta?.From) ? null : meta!.From!.Trim().ToUpperInvariant();
        var note = string.IsNullOrWhiteSpace(meta?.Note) ? null : meta!.Note!.Trim();

        var contribution = new Contribution
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            From = from,
            Date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            AppVersion = appVersion,
            Note = note
        };

        foreach (var meter in incoming.Meters)
        {
            var raw = meter.Points.Select(p => p.Raw).ToList();
            if (raw.Count == 0) continue;
            contribution.Meters[meter.Name] = new ContributionMeter
            {
                Labels = meter.Points.Select(p => p.Radio ?? "").ToList(),
                Raw = raw
            };
            if (MatchesPlaceholder(store, meter.Name, raw))
                contribution.Unmeasured.Add(meter.Name);
        }

        var existing = from is null
            ? null
            : store.Contributions.FirstOrDefault(
                c => string.Equals(c.From, from, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            store.Contributions.Add(contribution);
        }
        else
        {
            // Supersede in place, keeping the original id so the history in git
            // reads as one contributor's numbers changing, not a new person.
            contribution.Id = existing.Id;
            contribution.Excluded = existing.Excluded;
            contribution.ExcludedReason = existing.ExcludedReason;
            store.Contributions[store.Contributions.IndexOf(existing)] = contribution;
        }

        return contribution;
    }

    // ── Aggregation ───────────────────────────────────────────────────────────

    // What the shipped default for `current` should hold, given everything in
    // the store. Pure: reads the store, writes no files, edits nothing.
    //
    //   0 usable contributions → meter absent from Values (keep the placeholder)
    //   1                      → that contributor's numbers, as they sent them
    //   2 or more              → the median per point; on an even count, the
    //                            mean of the two middle values, rounded
    //
    // The median is the point of the whole exercise: it needs a majority of
    // contributors to be wrong in the same direction before the shipped value
    // moves, where a mean would be dragged by any single wild reading.
    public static RecomputeResult Recompute(CalibrationContributions store, CalibrationFile current)
    {
        var result = new RecomputeResult();
        var counted = new HashSet<string>();

        foreach (var meter in current.Meters)
        {
            var labels = meter.Points.Select(p => p.Radio ?? "").ToList();
            if (labels.Count == 0) continue;

            // Contributions usable for THIS meter.
            var usable = new List<(Contribution c, List<double> raw)>();

            foreach (var c in store.Contributions)
            {
                if (c.Excluded) continue;
                if (!c.Meters.TryGetValue(meter.Name, out var cm)) continue;

                // Un-measured is decided once, at import, against the
                // placeholders known then — never re-derived here. Re-deriving
                // would discard the very contribution that produced the current
                // shipped value, the moment that value became a placeholder.
                if (c.Unmeasured.Contains(meter.Name, StringComparer.OrdinalIgnoreCase)) continue;

                if (!cm.Labels.SequenceEqual(labels, StringComparer.OrdinalIgnoreCase) ||
                    cm.Raw.Count != labels.Count)
                {
                    result.Structural.Add($"{meter.Name} ({Who(c)})");
                    continue;
                }
                if (cm.Raw.Any(v => v < 0 || v > 255))
                {
                    result.Refused.Add($"{meter.Name} ({Who(c)}): raw value outside 0–255");
                    continue;
                }
                // A calibration curve that doesn't rise is not a measurement,
                // it's a transcription error — two points swapped, or a column
                // pasted out of order. Interpolating through it would produce
                // gauge readings that go backwards.
                if (!StrictlyIncreasing(cm.Raw))
                {
                    result.Refused.Add($"{meter.Name} ({Who(c)}): raw values not increasing");
                    continue;
                }

                usable.Add((c, cm.Raw));
            }

            if (usable.Count == 0) continue;

            var values = new List<double>(labels.Count);
            for (int i = 0; i < labels.Count; i++)
                values.Add(Median(usable.Select(u => u.raw[i]).ToList()));

            // Rounding can flatten two close medians into the same number even
            // though every contributor was strictly increasing. Keep the
            // placeholder rather than ship a table with a flat step in it.
            if (!StrictlyIncreasing(values))
            {
                result.Refused.Add(
                    $"{meter.Name}: median of {usable.Count} contributions is not strictly increasing — placeholder kept");
                continue;
            }

            result.Values[meter.Name] = values;
            foreach (var u in usable) counted.Add(u.c.Id);

            if (usable.Count < 2) continue;
            for (int i = 0; i < labels.Count; i++)
            {
                var col = usable.Select(u => u.raw[i]).ToList();
                double min = col.Min(), max = col.Max();
                if (max > min)
                    result.Spread.Add(new PointSpread(meter.Name, labels[i], min, max, values[i], col.Count));
            }
        }

        result.Contributors = counted.Count;
        return result;
    }

    private static string Who(Contribution c) => c.From ?? $"anon {c.Id}";

    private static bool StrictlyIncreasing(IReadOnlyList<double> v)
    {
        for (int i = 1; i < v.Count; i++)
            if (v[i] <= v[i - 1]) return false;
        return true;
    }

    private static double Median(List<double> values)
    {
        var s = values.OrderBy(x => x).ToList();
        int n = s.Count;
        var m = n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
        return Math.Round(m, MidpointRounding.AwayFromZero);
    }
}
