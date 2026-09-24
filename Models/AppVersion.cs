using System.Reflection;

namespace Yaesu_Web_Control;

public static class AppVersion
{
    public const string Current = "2.5.2";

    /// <summary>
    /// The git tag CI built from ("v2.5.2-pre2", "v2.5.2"), or "" for a local
    /// build. The workflow passes it as <c>-p:YwcBuildTag=</c> and the csproj
    /// bakes it in as assembly metadata, so it never has to be edited by hand
    /// and a full release needs nothing beyond the usual version bump.
    /// </summary>
    public static readonly string BuildTag = ReadBuildTag();

    /// <summary>
    /// The version the operator sees. "2.5.2" for a full release and for a
    /// local build; "2.5.2-pre2" for a pre-release. Until v2.5.2-pre2 every
    /// pre-release reported the bare number and nothing in the app could say
    /// which build was installed; the only check was hashing the installer.
    /// A tag that does not belong to <see cref="Current"/> at all is shown
    /// alongside it ("2.5.2 (v2.6.0)") rather than hidden, because that is a
    /// release-prep mistake worth seeing.
    /// </summary>
    public static readonly string Display = ComputeDisplay(Current, BuildTag);

    private static string ReadBuildTag() =>
        typeof(AppVersion).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "YwcBuildTag")?.Value?.Trim() ?? "";

    internal static string ComputeDisplay(string current, string buildTag)
    {
        if (string.IsNullOrWhiteSpace(buildTag))
            return current;

        string tag = buildTag.Trim();
        string bare = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

        if (bare == current)
            return current;                        // full release: v2.5.2
        if (bare.StartsWith(current + "-", StringComparison.Ordinal))
            return bare;                           // pre-release: v2.5.2-pre2
        return $"{current} ({tag})";               // tag and version disagree
    }

    /// <summary>Date this version was released, ISO format.
    /// Bump on actual release; current value reflects the planned ship date.</summary>
    public const string ReleaseDate = "2026-09-24";

    /// <summary>
    /// Firmware versions of the developer's bench radio(s) at the time this
    /// YWC build was cut. Shown on the About page and included in the
    /// diagnostics block so bug reporters can compare against the firmware
    /// YWC was tested on. Some YWC bugs depend on which Yaesu firmware the
    /// radio is running (e.g. the FTdx101 IF Width filter set was extended
    /// from 21 codes to 23 in a recent firmware -- see #50); having this
    /// list visible lets a user spot "I'm on different firmware to Colin,
    /// that might be why my behaviour differs".
    ///
    /// Find your own values:
    ///   FTdx101MP / FTdx101D: Func -> Extension Settings -> Soft Version.
    /// Update this dictionary whenever the bench radio's firmware moves.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TestedFirmware =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["FTdx101MP"] = new Dictionary<string, string>
            {
                ["Main"]     = "V01-28",
                ["Display"]  = "V01-51",
                ["Main DSP"] = "V01-20",
                ["Sub DSP"]  = "V01-20",
                ["Main SDR"] = "V02-08",
                ["Sub SDR"]  = "V02-08",
                ["AF DSP"]   = "V01-00",
            },
        };
}
