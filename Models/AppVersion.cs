namespace Yaesu_Web_Control;

public static class AppVersion
{
    public const string Current = "2.4.2-pre22";

    /// <summary>Date this version was released, ISO format.
    /// Bump on actual release; current value reflects the planned ship date.</summary>
    public const string ReleaseDate = "2026-07-28";

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
