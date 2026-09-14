using System.Text.Json.Serialization;

namespace Yaesu_Web_Control.Models.Calibration;

// The dev-side record of who contributed which calibration numbers, one file per
// radio model in calibration-contributions/. See
// docs/design/calibration-contributions-port-from-iwc.md.
//
// This is a DEVELOPMENT artefact tracked in git, never a shipped asset: it must
// not appear in wwwroot, in the .csproj publish items, or in installer.nsi. The
// shipped default files are DERIVED from it (median per point across
// contributors) rather than edited in place by each import, which is what makes
// one bad contribution reversible.

// One meter's numbers as a contributor sent them.
public class ContributionMeter
{
    // Stored beside Raw so a structural change — a meter gaining or losing a
    // calibration point — is detectable per contribution rather than silently
    // mis-indexed against the shipped table.
    [JsonPropertyName("labels")]
    public List<string> Labels { get; set; } = new();

    [JsonPropertyName("raw")]
    public List<double> Raw { get; set; } = new();
}

public class Contribution
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    // Callsign only — public information in amateur radio. Never an email
    // address, a real name or a location: this file is committed to git.
    // Null when the provenance genuinely isn't known; say so in Note rather
    // than inventing a callsign.
    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("meters")]
    public Dictionary<string, ContributionMeter> Meters { get; set; } = new();

    // Set by the import, not by the contributor: meters whose values matched a
    // known placeholder exactly. Kept for the audit trail, excluded from
    // aggregation — a contributor who calibrated only the S-meter still sends a
    // full set of seeded values for every other meter.
    [JsonPropertyName("unmeasured")]
    public List<string> Unmeasured { get; set; } = new();

    // Set by hand to drop a contribution without deleting it, then recompute.
    [JsonPropertyName("excluded")]
    public bool Excluded { get; set; }

    [JsonPropertyName("excludedReason")]
    public string? ExcludedReason { get; set; }
}

public class CalibrationContributions
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    // Every value-vector the shipped default has ever held, per meter. This is
    // how values echoed back from a seeded user file are recognised as
    // un-measured. Appended to on every recompute and NEVER pruned — pruning
    // would break detection for contributors still running an older release.
    [JsonPropertyName("placeholders")]
    public Dictionary<string, List<List<double>>> Placeholders { get; set; } = new();

    [JsonPropertyName("contributions")]
    public List<Contribution> Contributions { get; set; } = new();
}
