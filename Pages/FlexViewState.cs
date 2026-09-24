using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// Server-side values the Flex UI partials share. Mirrors the Razor locals
    /// computed in Pages/Index.cshtml so the duplicated flex markup can stay as
    /// close to the classic page as possible.
    /// </summary>
    public class FlexViewState
    {
        public bool IsFtdx10 { get; init; }
        public bool IsFt710 { get; init; }
        public bool IsFtdx3000 { get; init; }
        public bool IsFtdx5000 { get; init; }
        public bool IsFtdx101 { get; init; }
        public bool IsSingleReceiver { get; init; }
        public bool HasAntennaSelector { get; init; }
        public bool HasQmb { get; init; }
        public bool HasVcTune { get; init; }
        public bool HasVcTuneSub { get; init; }
        public int MaxPowerWatts { get; init; }
        public string ClarModeInit { get; init; } = "off";
        public int ContourMaxFreq { get; init; }
        public int ContourStep { get; init; }
        public int ApfStep { get; init; }
        public string IfWidthDefaultA { get; init; } = "0";
        public string IfWidthDefaultB { get; init; } = "0";
        public string RoofingOptionsJson { get; init; } = "[]";
        public (string Code, string Label)[] CtcssTones { get; init; } = System.Array.Empty<(string, string)>();

        /// <summary>True when Settings has Radio Display (video capture) enabled.</summary>
        public bool VideoDisplayEnabled { get; init; }

        /// <summary>True when the radio supports CAT scope control for its front panel.</summary>
        public bool SupportsSpectrumScopeCat { get; init; }

        /// <summary>True on the Windows product host (SDR/voice available).</summary>
        public bool IsWindowsHost { get; init; }

        public static FlexViewState Build(IndexModel model)
        {
            var radioModel = model.RadioModel;
            bool isFtdx10 = radioModel == "FTdx10";
            bool isFt710 = radioModel == "FT-710";
            bool isFtdx3000 = radioModel == "FTDX3000";
            bool isFtdx5000 = radioModel is "FTDX5000MP" or "FTDX5000D";
            bool isFtdx101 = radioModel is "FTdx101MP" or "FTdx101D";

            string clarModeInit = (model.RadioState.RxClarOn, model.RadioState.TxClarOn) switch
            {
                (true, true) => "rxtx",
                (true, false) => "rx",
                (false, true) => "tx",
                _ => "off"
            };

            int contourMaxFreq = isFtdx3000 ? 4000 : 3200;
            int contourStep = isFtdx3000 ? 100 : 10;
            int apfStep = isFtdx3000 ? 25 : 10;

            var roofingOptions = new (string Value, string Label)[]
                    { ("A","300 Hz"), ("9","600 Hz"), ("8","1.2 kHz"), ("7","3 kHz"), ("6","12 kHz") }
                .Where(o => model.InstalledRoofingFilters.Contains(o.Value))
                .ToArray();

            var roofingOptionsFtdx10 = new (string Value, string Label)[]
                    { ("6","12 kHz"), ("7","3 kHz"), ("9","500 Hz"), ("A","300 Hz") }
                .Where(o => o.Value == "6" || o.Value == "7" || o.Value == "9"
                         || model.InstalledRoofingFilters.Contains(o.Value))
                .ToArray();

            var roofingOptions3000 = new (string Value, string Label)[]
                    { ("0","Auto"), ("1","15 kHz"), ("2","6 kHz"), ("3","3 kHz"), ("4","600 Hz"), ("5","300 Hz") }
                .Where(o => o.Value == "0" || o.Value == "1" || o.Value == "2" || o.Value == "3"
                         || model.InstalledRoofingFilters.Contains(o.Value))
                .ToArray();

            var activeRoofingOptions = isFt710
                ? System.Array.Empty<(string Value, string Label)>()
                : isFtdx10 ? roofingOptionsFtdx10
                : isFtdx3000 ? roofingOptions3000
                : roofingOptions;

            var roofingOptionsJson = System.Text.Json.JsonSerializer.Serialize(
                activeRoofingOptions.Select(o => new { id = o.Value, label = o.Label }));

            var ctcssTones = new (string Code, string Label)[]
            {
                ("000","67.0"),  ("001","69.3"),  ("002","71.9"),  ("003","74.4"),  ("004","77.0"),
                ("005","79.7"),  ("006","82.5"),  ("007","85.4"),  ("008","88.5"),  ("009","91.5"),
                ("010","94.8"),  ("011","97.4"),  ("012","100.0"), ("013","103.5"), ("014","107.2"),
                ("015","110.9"), ("016","114.8"), ("017","118.8"), ("018","123.0"), ("019","127.3"),
                ("020","131.8"), ("021","136.5"), ("022","141.3"), ("023","146.2"), ("024","151.4"),
                ("025","156.7"), ("026","159.8"), ("027","162.2"), ("028","165.5"), ("029","167.9"),
                ("030","171.3"), ("031","173.8"), ("032","177.3"), ("033","179.9"), ("034","183.5"),
                ("035","186.2"), ("036","189.9"), ("037","192.8"), ("038","196.6"), ("039","199.5"),
                ("040","203.5"), ("041","206.5"), ("042","210.7"), ("043","218.1"), ("044","225.7"),
                ("045","229.1"), ("046","233.6"), ("047","241.8"), ("048","250.3"), ("049","254.1")
            };

            return new FlexViewState
            {
                IsFtdx10 = isFtdx10,
                IsFt710 = isFt710,
                IsFtdx3000 = isFtdx3000,
                IsFtdx5000 = isFtdx5000,
                IsFtdx101 = isFtdx101,
                IsSingleReceiver = RadioCapabilities.IsSingleReceiver(radioModel),
                HasAntennaSelector = RadioCapabilities.HasAntennaSelector(radioModel),
                HasQmb = RadioCapabilities.SupportsQmb(radioModel),
                HasVcTune = RadioCapabilities.SupportsVCTuneMain(radioModel)
                         && RadioCapabilities.SupportsVCTuneCat(model.RadioState.Id),
                HasVcTuneSub = RadioCapabilities.SupportsVCTuneSubStatic(radioModel)
                         && RadioCapabilities.SupportsVCTuneCat(model.RadioState.Id),
                MaxPowerWatts = RadioCapabilities.MaxPowerWatts(radioModel),
                ClarModeInit = clarModeInit,
                ContourMaxFreq = contourMaxFreq,
                ContourStep = contourStep,
                ApfStep = apfStep,
                IfWidthDefaultA = isFtdx10 ? "0" : isFt710 ? "12" : isFtdx3000 ? "14" : "0",
                IfWidthDefaultB = isFtdx10 ? "0" : isFt710 ? "12" : isFtdx3000 ? "14" : "0",
                RoofingOptionsJson = roofingOptionsJson,
                CtcssTones = ctcssTones,
                VideoDisplayEnabled = model.VideoDisplayEnabled,
                SupportsSpectrumScopeCat = RadioCapabilities.SupportsSpectrumScopeCat(radioModel),
                IsWindowsHost = model.IsWindowsHost
            };
        }
    }
}
