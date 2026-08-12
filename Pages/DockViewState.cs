using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// Razor view variables shared by Dock.cshtml and DockPartials/*.
    /// Mirrors the @{} block on Index.cshtml so partials can bind via Model.View.
    /// </summary>
    public sealed class DockViewState
    {
        public string[] AllModes { get; init; } = [];
        public bool IsFtdx10 { get; init; }
        public bool IsFt710 { get; init; }
        public bool IsFtdx3000 { get; init; }
        public bool IsFtdx5000 { get; init; }
        public bool IsSingleReceiver { get; init; }
        public bool HasAntennaSelector { get; init; }
        public bool HasQmb { get; init; }
        public bool HasVcTune { get; init; }
        public bool HasVcTuneSub { get; init; }
        public string ClarModeInit { get; init; } = "off";
        public int ContourMaxFreq { get; init; }
        public int ContourStep { get; init; }
        public int ApfStep { get; init; }
        public (string Value, string Label)[] IfWidthOptions { get; init; } = [];
        public string IfWidthDefaultA { get; init; } = "0";
        public string IfWidthDefaultB { get; init; } = "0";
        public (string Value, string Label)[] RoofingOptions { get; init; } = [];
        public (string Value, string Label)[] RoofingOptionsFtdx10 { get; init; } = [];
        public (string Value, string Label)[] RoofingOptions3000 { get; init; } = [];
        public (string Code, string Label)[] CtcssTones { get; init; } = [];

        public static string ModeLabel(string mode) =>
            mode == "DATA-FM-N" ? "D-FM-N" : mode;

        public static DockViewState Build(IndexModel model)
        {
            var radioModel = model.RadioModel;
            var isFtdx10 = radioModel == "FTdx10";
            var isFt710 = radioModel == "FT-710";
            var isFtdx3000 = radioModel == "FTDX3000";
            var isFtdx5000 = radioModel == "FTDX5000MP" || radioModel == "FTDX5000D";

            var clarModeInit = (model.RadioState.RxClarOn, model.RadioState.TxClarOn) switch
            {
                (true, true)  => "rxtx",
                (true, false) => "rx",
                (false, true) => "tx",
                _             => "off"
            };

            var ifWidthOptions = isFtdx10
                ? new (string Value, string Label)[]
                {
                    ("0","3.0 kHz"),
                    ("1","300 Hz"), ("2","400 Hz"), ("3","600 Hz"), ("4","850 Hz"),
                    ("5","1.1 kHz"), ("6","1.2 kHz"), ("7","1.5 kHz"), ("8","1.65 kHz"),
                    ("9","1.8 kHz"), ("10","1.95 kHz"), ("11","2.1 kHz"), ("12","2.25 kHz"),
                    ("13","2.4 kHz"), ("14","2.45 kHz"), ("15","2.5 kHz"), ("16","2.6 kHz"),
                    ("17","2.7 kHz"), ("18","2.8 kHz"), ("19","2.9 kHz"), ("20","3.2 kHz"),
                    ("21","3.5 kHz"), ("22","4.0 kHz")
                }
                : isFt710
                ? new (string Value, string Label)[]
                {
                    ("0","300 Hz"), ("2","600 Hz"), ("3","850 Hz"), ("5","1.2 kHz"),
                    ("7","1.65 kHz"), ("9","1.95 kHz"), ("12","2.4 kHz"), ("16","2.7 kHz"),
                    ("19","3.0 kHz"), ("20","3.2 kHz"), ("21","3.5 kHz"), ("22","4.0 kHz")
                }
                : isFtdx3000
                ? new (string Value, string Label)[]
                {
                    ("1","200 Hz"), ("2","400 Hz"), ("3","600 Hz"), ("4","850 Hz"),
                    ("6","1.35 kHz"), ("7","1.5 kHz"), ("9","1.8 kHz"), ("12","2.2 kHz"),
                    ("14","2.4 kHz"), ("16","2.6 kHz"), ("18","2.8 kHz"), ("20","3.0 kHz"),
                    ("22","3.4 kHz"), ("25","4.0 kHz")
                }
                : new (string Value, string Label)[]
                {
                    ("0","3.0 kHz"),
                    ("1","300 Hz"), ("2","400 Hz"), ("3","600 Hz"), ("4","850 Hz"),
                    ("5","1.1 kHz"), ("6","1.2 kHz"), ("7","1.5 kHz"), ("8","1.65 kHz"),
                    ("9","1.8 kHz"), ("10","1.95 kHz"), ("11","2.1 kHz"), ("12","2.2 kHz"),
                    ("13","2.3 kHz"), ("14","2.4 kHz"), ("15","2.5 kHz"), ("16","2.6 kHz"),
                    ("17","2.7 kHz"), ("18","2.8 kHz"), ("19","2.9 kHz"), ("20","3.0 kHz"),
                    ("21","3.2 kHz"), ("22","3.5 kHz"), ("23","4.0 kHz")
                };

            return new DockViewState
            {
                AllModes =
                [
                    "LSB", "USB", "CW-U", "CW-L", "FM", "FM-N", "AM", "AM-N", "RTTY-L", "RTTY-U",
                    "DATA-L", "DATA-U", "DATA-FM", "DATA-FM-N", "PSK"
                ],
                IsFtdx10 = isFtdx10,
                IsFt710 = isFt710,
                IsFtdx3000 = isFtdx3000,
                IsFtdx5000 = isFtdx5000,
                IsSingleReceiver = RadioCapabilities.IsSingleReceiver(radioModel),
                HasAntennaSelector = RadioCapabilities.HasAntennaSelector(radioModel),
                HasQmb = RadioCapabilities.SupportsQmb(radioModel),
                HasVcTune = RadioCapabilities.SupportsVCTuneMain(radioModel)
                           && RadioCapabilities.SupportsVCTuneCat(model.RadioState.Id),
                HasVcTuneSub = RadioCapabilities.SupportsVCTuneSubStatic(radioModel)
                            && RadioCapabilities.SupportsVCTuneCat(model.RadioState.Id),
                ClarModeInit = clarModeInit,
                ContourMaxFreq = isFtdx3000 ? 4000 : 3200,
                ContourStep = isFtdx3000 ? 100 : 10,
                ApfStep = isFtdx3000 ? 25 : 10,
                IfWidthOptions = ifWidthOptions,
                IfWidthDefaultA = isFtdx10 ? "0" : isFt710 ? "12" : isFtdx3000 ? "14" : "0",
                IfWidthDefaultB = isFtdx10 ? "0" : isFt710 ? "12" : isFtdx3000 ? "14" : "0",
                RoofingOptions = new (string Value, string Label)[]
                    { ("A","300 Hz"), ("9","600 Hz"), ("8","1.2 kHz"), ("7","3 kHz"), ("6","12 kHz") }
                    .Where(o => model.InstalledRoofingFilters.Contains(o.Value))
                    .ToArray(),
                RoofingOptionsFtdx10 = new (string Value, string Label)[]
                    { ("6","12 kHz"), ("7","3 kHz"), ("9","500 Hz"), ("A","300 Hz") }
                    .Where(o => o.Value == "6" || o.Value == "7" || o.Value == "9"
                             || model.InstalledRoofingFilters.Contains(o.Value))
                    .ToArray(),
                RoofingOptions3000 = new (string Value, string Label)[]
                    { ("0","Auto"), ("1","15 kHz"), ("2","6 kHz"), ("3","3 kHz"), ("4","600 Hz"), ("5","300 Hz") }
                    .Where(o => o.Value == "0" || o.Value == "1" || o.Value == "2" || o.Value == "3"
                             || model.InstalledRoofingFilters.Contains(o.Value))
                    .ToArray(),
                CtcssTones =
                [
                    ("01","67.0"), ("02","69.3"), ("03","71.9"), ("04","74.4"), ("05","77.0"),
                    ("06","79.7"), ("07","82.5"), ("08","85.4"), ("09","88.5"), ("10","91.5"),
                    ("11","94.8"), ("12","97.4"), ("13","100.0"), ("14","103.5"), ("15","107.2"),
                    ("16","110.9"), ("17","114.8"), ("18","118.8"), ("19","123.0"), ("20","127.3"),
                    ("21","131.8"), ("22","136.5"), ("23","141.3"), ("24","146.2"), ("25","150.0"),
                    ("26","151.4"), ("27","156.7"), ("28","162.2"), ("29","165.5"), ("30","167.9"),
                    ("31","171.3"), ("32","173.8"), ("33","177.3"), ("34","179.9"), ("35","183.5"),
                    ("36","186.2"), ("37","189.9"), ("38","192.8"), ("39","196.6"), ("40","199.5"),
                    ("41","203.5"), ("42","206.5"), ("43","210.7"), ("44","218.1"), ("45","225.7"),
                    ("46","229.1"), ("47","233.6"), ("48","241.8"), ("49","250.3"), ("50","254.1")
                ],
            };
        }
    }
}
