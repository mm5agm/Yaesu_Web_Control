// Yaesu Web Control – SDRplay Device (P/Invoke into sdrplay_api.dll)
// Implements ISdrDevice using the SDRplay API v3.
// The SDRplay API is callback-based; this class bridges the native callback
// thread to the managed consumer via a System.Threading.Channels.Channel<float[]>.
//
// Native struct layout (sdrplay_api_DeviceT, 96 bytes, x64):
//   Offset  0 : SerNo[64]          — ANSI serial number
//   Offset 64 : hwVer (byte)       — hardware version code
//   Offset 68 : tuner  (int)
//   Offset 72 : rspDuoMode (int)
//   Offset 76 : valid  (byte)
//   Offset 80 : rspDuoSampleFreq (double)
//   Offset 88 : Dev  (HANDLE / IntPtr)
//
// sdrplay_api_DeviceParamsT pointer layout (returned by GetDeviceParams):
//   Offset  0 : devParams*        → sdrplay_api_DevParamsT*
//   Offset  8 : rxChannelA*       → sdrplay_api_RxChannelParamsT*
//   Offset 16 : rxChannelB*       → sdrplay_api_RxChannelParamsT* (null for non-RSPduo)
//
// Within sdrplay_api_DevParamsT:
//   Offset 0 : ppm (double)
//   Offset 8 : fsFreq.fsHz (double)  ← sample rate
//
// Within sdrplay_api_RxChannelParamsT.tunerParams (from sdrplay_api_tuner.h):
//   Offset  0 : bwType (int)
//   Offset  4 : ifType (int)
//   Offset  8 : loMode (int)
//   Offset 12 : gain   (24 bytes)
//     gain.gRdB      @ 12  (int)
//     gain.LNAstate  @ 16  (unsigned char — NOT int)
//     gain.syncUpdate@ 17  (unsigned char)
//     gain.minGr     @ 20  (int, enum)
//     gain.gainVals  @ 24  (3 × float)
//   Offset 40 : rfFreq.rfHz (double)  ← sizeof(RfFreqT)=16 (double+uchar+7B tail padding)
//   Offset 56 : dcOffsetTuner (12 bytes) → refreshRateTime @ 64
//   Offset 72 : ctrlParams.dcOffset (2B) then ctrlParams.decimation (3B)

using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Yaesu_Web_Control.Services.Sdr
{
    public sealed class SdrplayDevice : ISdrDevice
    {
        // ── Constants ────────────────────────────────────────────────────────────

        private const string DllName             = "sdrplay_api";
        private const int    DeviceStructSize    = 96;
        private const int    MaxDevices          = 4;
        private const int    DevHandleOffset     = 88;
        private const int    HwVerOffset         = 64;
        private const int    DevParamsOffset     = 0;   // within DeviceParamsT
        private const int    RxChannelAOffset    = 8;   // within DeviceParamsT
        private const int    FsHzOffset          = 8;   // within DevParamsT
        private const int    RfHzOffset          = 40;  // tunerParams.rfFreq.rfHz — gain(24)+pad(4) after loMode offset 12
        private const int    CallbackFnsSize     = 24;  // 3 × IntPtr

        // Offsets within sdrplay_api_RxChannelParamsT (= tunerParams is first member at offset 0).
        // tunerParams layout: bwType(0) ifType(4) loMode(8) gain(12…35) rfFreq.rfHz(40)
        // gain.LNAstate is unsigned char at gain+4 — Marshal.WriteInt32 writes 4 bytes but
        // the next fields (syncUpdate uchar, 2-byte padding) are all safely zeroed that way.
        private const int    BwTypeOffset        = 0;   // sdrplay_api_Bw_MHzT (int)
        private const int    IfTypeOffset        = 4;   // sdrplay_api_If_kHzT (int)
        private const int    LoModeOffset        = 8;   // sdrplay_api_LoModeT (int)
        private const int    GrDbOffset          = 12;  // gain.gRdB     (int) — first field
        private const int    LnaStateOffset      = 16;  // gain.LNAstate (int) — second field

        // sdrplay_api_ControlParamsT starts at rxChannelA + 72 (= sizeof TunerParamsT).
        // sizeof(TunerParamsT) = 72 because RfFreqT (double+uchar) has 7 bytes tail padding
        // → sizeof(RfFreqT)=16, dcOffsetTuner starts at 56, ends at 68, padded to 72.
        //   ctrlParams.dcOffset.DCenable           @ 72
        //   ctrlParams.dcOffset.IQenable           @ 73
        //   ctrlParams.decimation.enable           @ 74
        //   ctrlParams.decimation.decimationFactor @ 75
        //   ctrlParams.decimation.wideBandSignal   @ 76
        private const int    DcEnableOffset         = 72;
        private const int    IqEnableOffset         = 73;
        private const int    DecimationEnableOffset = 74;
        private const int    DecimationFactorOffset = 75;

        // sdrplay_api_Bw_MHzT enum values (numeric value = bandwidth in kHz).
        private const int    BW_0_200            = 200;
        private const int    BW_0_300            = 300;
        private const int    BW_0_600            = 600;
        private const int    BW_1_536            = 1536;
        private const int    BW_5_000            = 5000;

        // sdrplay_api_If_kHzT enum values (numeric value = IF in kHz).
        private const int    IF_ZERO             = 0;
        private const int    IF_0_450            = 450;
        private const int    IF_2_048            = 2048;

        // Key prefix that identifies SDRplay devices across the codebase.
        public const string KeyPrefix = "sdrplay:";

        // ── P/Invoke declarations ────────────────────────────────────────────────

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_Open();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_Close();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_GetDevices(
            IntPtr devices, ref uint numDevices, uint maxNumDevices);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_SelectDevice(IntPtr device);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_ReleaseDevice(IntPtr device);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_GetDeviceParams(
            IntPtr dev, out IntPtr deviceParams);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_Init(
            IntPtr dev, IntPtr callbackFns, IntPtr cbContext);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_Uninit(IntPtr dev);

        // Returns a pointer to sdrplay_api_ErrorInfoT (owned by the API, do not free).
        // ErrorInfoT layout: file[256] @ 0, function[256] @ 256, line(int) @ 512, message[1024] @ 516
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sdrplay_api_GetLastError(IntPtr device);

        // ── Callback delegates (must stay rooted to prevent GC) ─────────────────

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void StreamCallbackDelegate(
            IntPtr xi, IntPtr xq,
            IntPtr streamCbParams,
            uint numSamples,
            uint reset,
            IntPtr cbContext);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EventCallbackDelegate(
            int eventId, int tuner, IntPtr eventParams, IntPtr cbContext);

        // ── Instance state ────────────────────────────────────────────────────────

        public string Key   { get; }
        public string Label { get; private set; }

        private readonly Channel<float[]> _channel =
            Channel.CreateBounded<float[]>(new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

        // Config (set in Configure, read in callback)
        private int     _fftSize;
        private float[] _accumBuffer = [];
        private int     _accumOffset;

        // Native handles
        private IntPtr _deviceStructPtr;  // unmanaged copy of the selected DeviceT
        private IntPtr _devHandle;        // Dev field from DeviceT (HANDLE)
        private IntPtr _callbackFnsPtr;   // unmanaged CallbackFnsT struct

        // Kept from Configure so the applied tuner settings can be read back
        // after Init. The API hands out pointers to its own live structures,
        // so if it rejects or rewrites a value, reading these afterwards shows
        // it — the return code alone does not.
        private IntPtr _devParamsPtr;
        private IntPtr _rxChannelAPtr;
        private GCHandle _selfHandle;     // pins 'this' for native callback context

        // Delegate fields — kept alive by the instance
        private StreamCallbackDelegate? _streamDelegate;
        private EventCallbackDelegate?  _eventDelegate;

        private bool _apiOpen;
        private bool _streaming;
        private bool _disposed;

        // ── Constructor ───────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a device wrapper for the given key.
        /// <para>
        /// Key format (v2.3.0+): <c>sdrplay:hw&lt;hwVer&gt;-&lt;serialNumber&gt;</c>
        /// — e.g. <c>sdrplay:hw6-2405242660</c> for an RSP1B.
        /// </para>
        /// <para>
        /// Legacy format (v2.2.x and earlier): <c>sdrplay:&lt;serialNumber&gt;</c>.
        /// Still accepted for backward compatibility — matched on serial only.
        /// </para>
        /// </summary>
        public SdrplayDevice(string key)
        {
            if (!key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Key must start with '{KeyPrefix}'.", nameof(key));

            Key   = key;
            Label = key;   // Placeholder; updated to the full model name in Configure().
        }

        /// <summary>
        /// Parses a device key into hwVer (or null for legacy keys) and serial.
        /// New: "sdrplay:hw6-2405242660"  → (6, "2405242660")
        /// Old: "sdrplay:2405242660"      → (null, "2405242660")
        /// </summary>
        private static (byte? hwVer, string serial) ParseKey(string key)
        {
            string body = key[KeyPrefix.Length..];
            // New format: "hw<digits>-<serial>"
            if (body.StartsWith("hw", StringComparison.OrdinalIgnoreCase))
            {
                int dash = body.IndexOf('-');
                if (dash > 2 &&
                    byte.TryParse(body.AsSpan(2, dash - 2), out byte hw))
                {
                    return (hw, body[(dash + 1)..]);
                }
            }
            return (null, body);
        }

        // ── Public API ────────────────────────────────────────────────────────────

        // Common installation paths for the official SDRplay API on Windows x64.
        private static readonly string[] _knownDllPaths =
        [
            @"C:\Program Files\SDRplay\API\x64\sdrplay_api.dll",
            @"C:\Program Files (x86)\SDRplay\API\x64\sdrplay_api.dll",
        ];

        /// <summary>
        /// Returns all connected SDRplay devices.
        /// <paramref name="diagnosticNote"/> receives a plain-English explanation
        /// of any problem encountered (DLL missing, service not running, etc.).
        /// </summary>
        public static IReadOnlyList<SdrDeviceInfo> EnumerateDevices(out string? diagnosticNote)
        {
            diagnosticNote = null;
            var devices = new List<SdrDeviceInfo>();
            try
            {
                int err = sdrplay_api_Open();
                if (err != 0)
                {
                    diagnosticNote =
                        $"sdrplay_api.dll loaded but sdrplay_api_Open() returned error {err}. " +
                        "This usually means the SDRplay API Service is not running. " +
                        "Open Windows Services (services.msc) and check that " +
                        "'SDRplay API Service' (or 'SDRPlayService') is started. " +
                        "Also close SDR Console or any other SDR app before scanning — " +
                        "only one application can hold the API open at a time.";
                    return devices;
                }

                try
                {
                    IntPtr deviceArray = Marshal.AllocHGlobal(DeviceStructSize * MaxDevices);
                    try
                    {
                        uint count = 0;
                        err = sdrplay_api_GetDevices(deviceArray, ref count, MaxDevices);
                        if (err != 0)
                        {
                            diagnosticNote =
                                $"sdrplay_api_GetDevices() returned error {err}. " +
                                "The device may be in use by another application.";
                        }
                        else if (count == 0)
                        {
                            diagnosticNote =
                                "SDRplay API opened successfully but found 0 devices. " +
                                "Check the RSP1 is plugged in to a USB port and that the " +
                                "SDRplay USB driver is installed (Device Manager should show " +
                                "'SDRplay RSP1' under 'Software Defined Radio', not under " +
                                "'Unknown devices' or with a yellow warning icon).";
                        }
                        else
                        {
                            ReadDeviceList(deviceArray, count, devices);
                        }
                    }
                    finally { Marshal.FreeHGlobal(deviceArray); }
                }
                finally { sdrplay_api_Close(); }
            }
            catch (DllNotFoundException)
            {
                // Check whether the DLL exists somewhere we know about but isn't in PATH.
                string? found = Array.Find(_knownDllPaths, File.Exists);
                if (found != null)
                {
                    diagnosticNote =
                        $"sdrplay_api.dll is installed at '{found}' but is not on the " +
                        $"system PATH, so the app cannot load it. Copy it to the app output " +
                        $"folder: {AppContext.BaseDirectory}";
                }
                else
                {
                    diagnosticNote =
                        "sdrplay_api.dll not found. Install the official SDRplay API from " +
                        "www.sdrplay.com/softwaredownloads/ then restart the app.";
                }
            }
            catch (Exception ex)
            {
                diagnosticNote = $"SDRplay API unexpected error: {ex.GetType().Name} — {ex.Message}";
            }

            return devices;
        }

        /// <summary>
        /// A coherent hardware configuration for one requested span.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>HardwareRateHz</c>, <c>BwType</c> and <c>IfType</c> are chosen
        /// <b>together</b> and must never be picked independently. The API enables
        /// its low-IF down-conversion only for specific (fsHz, bwType, ifType)
        /// triples — API spec v3.15 §3.15, "Conditions for LIF down-conversion" —
        /// and an unmatched triple raises no error. It silently falls back to
        /// zero-IF, where the tuner's own DC offset lands exactly on the tuned
        /// frequency and draws a permanent false signal down the centre of the
        /// spectrum. Colin reported that as a trace that made it impossible to
        /// tell whether there was really a signal on the frequency he was
        /// listening to. Bundling the three fields into one value is what stops a
        /// later edit reintroducing it by changing one of them alone.
        /// </para>
        /// <para>
        /// Every row of <see cref="PlanFor"/> satisfies three constraints:
        /// it matches a documented LIF triple; its analogue bandwidth is at least
        /// the achieved span, so the display is not looking through the skirts of
        /// the IF filter; and the DC offset — which sits <c>IfType</c> kHz from
        /// centre — falls outside the visible span.
        /// </para>
        /// <para>
        /// Decimation factors are powers of two throughout. The valid range of
        /// <c>decimationFactor</c> is an undocumented <c>unsigned char</c> — it
        /// appears in neither the header nor the specification — so this avoids
        /// discovering the limit by hitting it.
        /// </para>
        /// </remarks>
        private readonly record struct TunePlan(
            double HardwareRateHz,
            int    DecimationFactor,
            int    BwType,
            int    IfType)
        {
            /// <summary>
            /// The API's own low-IF down-conversion decimates by four before
            /// <c>decimationFactor</c> is applied, so fs = 2 MHz reaches user
            /// decimation as 500 kHz and fs = 8 MHz as 2 MHz.
            /// </summary>
            /// <remarks>
            /// Omitting this is what made every span four times narrower than it
            /// claimed. Measured against the radio on 2026-09-03 by stepping VFO A
            /// a known amount and cross-correlating the whole spectrum before and
            /// after: a +10 kHz step moved the trace 164 bins at the nominal
            /// 250 kHz span where 41 was required, and 41 bins at the nominal
            /// 1 MHz span where 10 was required. Ratio 4.004 on both, and on both
            /// IF types (450 kHz at fs = 2 MHz, 2.048 MHz at fs = 8 MHz), so the
            /// factor is a property of LIF mode rather than of any one row.
            /// </remarks>
            public const int LifDecimation = 4;

            /// <summary>The span actually produced, after both decimations.</summary>
            public double AchievedRateHz =>
                HardwareRateHz / LifDecimation / DecimationFactor;
        }

        /// <summary>
        /// Maps a requested span onto the hardware settings that deliver it
        /// without a centre spike. See <see cref="TunePlan"/> for why these
        /// fields travel together.
        /// </summary>
        /// <remarks>
        /// The 2 MHz / 450 kHz rows run out at a 600 kHz span, because the only
        /// bandwidths the API will accept alongside a 450 kHz IF at fs = 2 MHz are
        /// 200, 300 and 600 kHz. Anything wider moves to fs = 8 MHz with a
        /// 2.048 MHz IF and the 5 MHz filter, which costs four times the USB
        /// bandwidth — around 32 MB/s per device — and is the reason the span list
        /// stops at 2 MHz rather than going wider.
        /// </remarks>
        private static TunePlan PlanFor(double requestedRateHz) => requestedRateHz switch
        {
            //                      fsHz      ÷   bwType     ifType     → span
            //                     (÷4 by LIF before this column applies)
            <=    62_500 => new(2_000_000, 8, BW_0_200, IF_0_450), //   62.5 kHz
            <=   125_000 => new(2_000_000, 4, BW_0_200, IF_0_450), //  125   kHz
            <=   250_000 => new(2_000_000, 2, BW_0_300, IF_0_450), //  250   kHz
            <=   500_000 => new(2_000_000, 1, BW_0_600, IF_0_450), //  500   kHz
            <= 1_000_000 => new(8_000_000, 2, BW_5_000, IF_2_048), //    1   MHz

            _            => new(8_000_000, 1, BW_5_000, IF_2_048), //    2   MHz
        };

        /// <inheritdoc/>
        public double ActualSampleRateHz { get; private set; }

        /// <inheritdoc/>
        public void Configure(long centreFrequencyHz, double sampleRateHz, int fftSize)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(SdrplayDevice));

            _fftSize     = fftSize;
            _accumBuffer = new float[fftSize * 2];
            _accumOffset = 0;

            // Open the API
            ThrowIfError(sdrplay_api_Open(), "sdrplay_api_Open");
            _apiOpen = true;

            // Enumerate and find our device. We support both the new
            // "sdrplay:hw<N>-<serial>" key format and the legacy serial-only one.
            var (wantHwVer, serial) = ParseKey(Key);

            IntPtr deviceArray = Marshal.AllocHGlobal(DeviceStructSize * MaxDevices);
            try
            {
                uint count = 0;
                ThrowIfError(sdrplay_api_GetDevices(deviceArray, ref count, MaxDevices), "GetDevices");

                IntPtr matchPtr = FindDevice(deviceArray, count, wantHwVer, serial);
                if (matchPtr == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"SDRplay device '{serial}'" +
                        (wantHwVer.HasValue ? $" (hwVer {wantHwVer})" : "") +
                        " not found. Check it is connected.");

                // Update Label now that we know the hardware version.
                byte hwVer = Marshal.ReadByte(matchPtr, HwVerOffset);
                Label = $"SDRplay {HwVerToModel(hwVer)} ({serial})";

                // Copy the struct to our own buffer before freeing the array.
                _deviceStructPtr = Marshal.AllocHGlobal(DeviceStructSize);
                for (int i = 0; i < DeviceStructSize; i++)
                    Marshal.WriteByte(_deviceStructPtr, i, Marshal.ReadByte(matchPtr, i));
            }
            finally { Marshal.FreeHGlobal(deviceArray); }

            // Select the device (populates _deviceStructPtr.Dev)
            ThrowIfError(sdrplay_api_SelectDevice(_deviceStructPtr), "SelectDevice");
            _devHandle = Marshal.ReadIntPtr(_deviceStructPtr, DevHandleOffset);

            // Retrieve parameter pointers and set frequency + sample rate
            ThrowIfError(sdrplay_api_GetDeviceParams(_devHandle, out IntPtr deviceParamsPtr), "GetDeviceParams");

            IntPtr devParams  = Marshal.ReadIntPtr(deviceParamsPtr, DevParamsOffset);
            IntPtr rxChannelA = Marshal.ReadIntPtr(deviceParamsPtr, RxChannelAOffset);
            _devParamsPtr   = devParams;
            _rxChannelAPtr  = rxChannelA;

            // Sample rate, decimation, analogue bandwidth and IF mode — one
            // decision, because the API only enables low-IF down-conversion for
            // certain combinations of the four. See TunePlan.
            TunePlan plan = PlanFor(sampleRateHz);

            // Diagnostic escape hatch: YWC_SDR_FORCE_IF_ZERO=1 runs the
            // identical plan with the IF forced back to zero. Whether low-IF
            // down-conversion is really engaging can only be answered by
            // measuring the same radio on the same band both ways. Measured
            // 2026-08-30 at the 62.5 kHz span: centre bin +46.9 dB over the
            // noise floor forced to zero-IF, +8.4 dB with low-IF, +0.7 dB with
            // low-IF and the DC blocker. One reading alone proves nothing.
            if (Environment.GetEnvironmentVariable("YWC_SDR_FORCE_IF_ZERO") == "1")
                plan = plan with { IfType = IF_ZERO };

            // The achieved span is rarely the requested one exactly, and the
            // browser draws its frequency scale from whatever we report. Record
            // what the hardware will really produce, not what was asked for.
            ActualSampleRateHz = plan.AchievedRateHz;

            WriteDouble(devParams, FsHzOffset, plan.HardwareRateHz);

            Marshal.WriteByte(rxChannelA, DecimationEnableOffset,
                              (byte)(plan.DecimationFactor > 1 ? 1 : 0));
            Marshal.WriteByte(rxChannelA, DecimationFactorOffset,
                              (byte)plan.DecimationFactor);

            // Centre frequency
            WriteDouble(rxChannelA, RfHzOffset, (double)centreFrequencyHz);

            // Analog bandwidth — must be set to match the sample rate.
            // Default after GetDeviceParams is 200 kHz, which rejects almost all of
            // the displayed span and leaves the spectrum showing only noise floor.
            Marshal.WriteInt32(rxChannelA, BwTypeOffset, plan.BwType);

            // IF mode. Left unwritten this defaults to sdrplay_api_IF_Zero, which
            // is what put a permanent trace down the centre of the display.
            Marshal.WriteInt32(rxChannelA, IfTypeOffset, plan.IfType);

            // An unmatched fsHz/bwType/ifType triple is not an error — the API
            // simply reverts to zero-IF and says nothing, and the only visible
            // symptom is the centre spike coming back. Log what we asked for so
            // that failure can be told apart from "low-IF worked and the spike
            // is coming from somewhere else".
            Console.Error.WriteLine(
                $"[SdrplayDevice] tune plan: requested={sampleRateHz:0} Hz  " +
                $"fsHz={plan.HardwareRateHz:0}  decim={plan.DecimationFactor}  " +
                $"bwType={plan.BwType}  ifType={plan.IfType}  " +
                $"achieved={plan.AchievedRateHz:0} Hz  rfHz={centreFrequencyHz}");

            // Gain — gRdB 40 (moderate IF gain reduction, safe for strong IF inputs),
            // LNAstate 0 (minimum LNA attenuation = maximum LNA sensitivity).
            // RSP1 valid ranges: gRdB 20–59, LNAstate 0–3.
            Marshal.WriteInt32(rxChannelA, GrDbOffset,     40);
            Marshal.WriteInt32(rxChannelA, LnaStateOffset,  0);
        }

        /// <inheritdoc/>
        public void StartStreaming()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(SdrplayDevice));
            if (_streaming) return;

            // Pin 'this' so the native callback can recover the instance.
            _selfHandle = GCHandle.Alloc(this);

            // Create delegates and keep references alive on the instance.
            _streamDelegate = new StreamCallbackDelegate(StreamACallback);
            _eventDelegate  = new EventCallbackDelegate(EventCallback);

            // Build the unmanaged CallbackFnsT struct: [StreamA, StreamB, Event]
            _callbackFnsPtr = Marshal.AllocHGlobal(CallbackFnsSize);
            Marshal.WriteIntPtr(_callbackFnsPtr, 0,  Marshal.GetFunctionPointerForDelegate(_streamDelegate));
            Marshal.WriteIntPtr(_callbackFnsPtr, 8,  Marshal.GetFunctionPointerForDelegate(_streamDelegate));
            Marshal.WriteIntPtr(_callbackFnsPtr, 16, Marshal.GetFunctionPointerForDelegate(_eventDelegate));

            int initErr = sdrplay_api_Init(
                _devHandle,
                _callbackFnsPtr,
                GCHandle.ToIntPtr(_selfHandle));
            if (initErr != 0)
                throw new InvalidOperationException(
                    $"sdrplay_api: sdrplay_api_Init returned error code {initErr}" +
                    ReadLastErrorDetail(_deviceStructPtr));

            // Read the tuner settings back out of the API's own structures now
            // that Init has run. Init returning success does not mean it used
            // what we asked for: an unmatched fsHz/bwType/ifType triple leaves
            // the device in zero-IF and reports nothing. If these read back
            // changed, the API overrode us; if they read back as asked but the
            // centre spike is still there, the device accepted the settings and
            // did not act on them.
            Console.Error.WriteLine(
                "[SdrplayDevice] after Init: " +
                $"fsHz={ReadDouble(_devParamsPtr, FsHzOffset):0}  " +
                $"bwType={Marshal.ReadInt32(_rxChannelAPtr, BwTypeOffset)}  " +
                $"ifType={Marshal.ReadInt32(_rxChannelAPtr, IfTypeOffset)}  " +
                $"loMode={Marshal.ReadInt32(_rxChannelAPtr, LoModeOffset)}  " +
                $"rfHz={ReadDouble(_rxChannelAPtr, RfHzOffset):0}  " +
                $"decEnable={Marshal.ReadByte(_rxChannelAPtr, DecimationEnableOffset)}  " +
                $"decFactor={Marshal.ReadByte(_rxChannelAPtr, DecimationFactorOffset)}  " +
                $"DCenable={Marshal.ReadByte(_rxChannelAPtr, DcEnableOffset)}  " +
                $"IQenable={Marshal.ReadByte(_rxChannelAPtr, IqEnableOffset)}");

            _streaming = true;
        }

        /// <inheritdoc/>
        public async ValueTask<bool> TryReadIqFrameAsync(
            float[] buffer, int timeoutMs, CancellationToken ct = default)
        {
            using var linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeoutMs);
            try
            {
                var frame = await _channel.Reader.ReadAsync(linkedCts.Token).ConfigureAwait(false);
                Array.Copy(frame, buffer, Math.Min(frame.Length, buffer.Length));
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (ChannelClosedException)     { return false; }
        }

        /// <inheritdoc/>
        public void Stop()
        {
            if (!_streaming) return;
            _streaming = false;

            try { sdrplay_api_Uninit(_devHandle); }
            catch { /* ignore errors during stop */ }

            if (_callbackFnsPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_callbackFnsPtr);
                _callbackFnsPtr = IntPtr.Zero;
            }

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();

            // Release delegate references so the GC can collect them.
            _streamDelegate = null;
            _eventDelegate  = null;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            if (_devHandle != IntPtr.Zero)
            {
                try { sdrplay_api_ReleaseDevice(_deviceStructPtr); } catch { }
                _devHandle = IntPtr.Zero;
            }

            if (_deviceStructPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_deviceStructPtr);
                _deviceStructPtr = IntPtr.Zero;
            }

            if (_apiOpen)
            {
                try { sdrplay_api_Close(); } catch { }
                _apiOpen = false;
            }

            _channel.Writer.TryComplete();
        }

        // ── Native callbacks ─────────────────────────────────────────────────────

        private void StreamACallback(
            IntPtr xi, IntPtr xq,
            IntPtr _streamCbParams,
            uint numSamples, uint reset,
            IntPtr _cbContext)
        {
            if (reset != 0)
            {
                _accumOffset = 0;
                return;
            }

            float[] accum = _accumBuffer;
            int fftSize   = _fftSize;
            if (accum.Length == 0 || fftSize <= 0) return;

            int srcIdx    = 0;
            int available = (int)numSamples;

            while (srcIdx < available)
            {
                int space  = fftSize - _accumOffset;
                int toCopy = Math.Min(available - srcIdx, space);

                for (int i = 0; i < toCopy; i++)
                {
                    accum[(_accumOffset + i) * 2]     =
                        Marshal.ReadInt16(xi, (srcIdx + i) * 2) / 32768f;
                    accum[(_accumOffset + i) * 2 + 1] =
                        Marshal.ReadInt16(xq, (srcIdx + i) * 2) / 32768f;
                }

                _accumOffset += toCopy;
                srcIdx       += toCopy;

                if (_accumOffset >= fftSize)
                {
                    var frame = new float[fftSize * 2];
                    Array.Copy(accum, frame, fftSize * 2);
                    _channel.Writer.TryWrite(frame);
                    _accumOffset = 0;
                }
            }
        }

        private static void EventCallback(
            int _eventId, int _tuner, IntPtr _eventParams, IntPtr _cbContext)
        {
            // SDRplay events (gain change, overload, etc.) — not needed for spectrum display.
        }

        // ── Static helpers ────────────────────────────────────────────────────────

        private static void ReadDeviceList(IntPtr deviceArray, uint count, List<SdrDeviceInfo> list)
        {
            for (uint i = 0; i < count; i++)
            {
                IntPtr ptr    = deviceArray + (int)(i * DeviceStructSize);
                string serial = Marshal.PtrToStringAnsi(ptr) ?? $"device{i}";
                byte   hwVer  = Marshal.ReadByte(ptr, HwVerOffset);
                string model  = HwVerToModel(hwVer);
                string label  = $"SDRplay {model} ({serial})";

                // Key format includes hwVer so two devices that happen to share a
                // serial (notably an RSP1 with the factory-default "0000000001"
                // placeholder, alongside an RSP1B with a real serial) remain
                // distinguishable. See USER_MANUAL FAQ "Why does my RSP1 show
                // serial 0000000001?" for the background.
                list.Add(new SdrDeviceInfo(
                    Key:    $"{KeyPrefix}hw{hwVer}-{serial}",
                    Label:  label,
                    Driver: "sdrplay"));
            }
        }

        /// <summary>
        /// Find a device in the enumeration by hwVer+serial (preferred) or by
        /// serial alone (legacy keys). Falls back to serial-only if no hwVer
        /// match is found — keeps v2.2.x-saved keys working until the next save
        /// migrates them to the new format.
        /// </summary>
        private static IntPtr FindDevice(IntPtr deviceArray, uint count, byte? wantHwVer, string serial)
        {
            IntPtr serialOnlyMatch = IntPtr.Zero;
            for (uint i = 0; i < count; i++)
            {
                IntPtr ptr       = deviceArray + (int)(i * DeviceStructSize);
                string devSerial = Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
                if (!string.Equals(devSerial, serial, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (wantHwVer.HasValue)
                {
                    byte devHwVer = Marshal.ReadByte(ptr, HwVerOffset);
                    if (devHwVer == wantHwVer.Value)
                        return ptr;
                }
                else
                {
                    // Legacy key — first serial match wins.
                    return ptr;
                }
                // Remember a serial-only match in case no hwVer match is found.
                if (serialOnlyMatch == IntPtr.Zero) serialOnlyMatch = ptr;
            }
            return serialOnlyMatch;
        }

        // hwVer codes per the official SDRplay API header
        // (C:\Program Files\SDRplay\API\inc\sdrplay_api.h):
        //   SDRPLAY_RSP1_ID    = 1
        //   SDRPLAY_RSP2_ID    = 2
        //   SDRPLAY_RSPduo_ID  = 3
        //   SDRPLAY_RSPdx_ID   = 4
        //   SDRPLAY_RSP1B_ID   = 6
        //   SDRPLAY_RSPdxR2_ID = 7
        //   SDRPLAY_RSP1A_ID   = 255   (deliberately out-of-sequence)
        // The previous table was shifted by one slot at codes 3-5 and was
        // missing 255, so RSPdx devices were labelled "RSPduo" (Issue #10)
        // and RSP1A devices showed as "RSP (hwVer=255)".
        private static string HwVerToModel(byte hwVer) => hwVer switch
        {
            1   => "RSP1",
            2   => "RSP2",
            3   => "RSPduo",
            4   => "RSPdx",
            6   => "RSP1B",
            7   => "RSPdx R2",
            255 => "RSP1A",
            _   => $"RSP (hwVer={hwVer})"
        };

        /// <summary>
        /// Produces a human-readable label for an SDRplay key WITHOUT going
        /// through the API. Used by SdrController to label devices that
        /// workers are currently holding (and which therefore can't be
        /// re-enumerated until the worker releases them).
        /// Accepts both "sdrplay:&lt;serial&gt;" (legacy) and
        /// "sdrplay:hw&lt;N&gt;-&lt;serial&gt;" (v2.3.0+) formats.
        /// </summary>
        public static string LabelForKey(string key)
        {
            if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
                return key;
            string suffix = key.Substring(KeyPrefix.Length);
            // New format: "hw<N>-<serial>"
            if (suffix.StartsWith("hw", StringComparison.OrdinalIgnoreCase))
            {
                int dash = suffix.IndexOf('-');
                if (dash > 2)
                {
                    if (byte.TryParse(suffix.AsSpan(2, dash - 2), out byte hw))
                    {
                        string serial = suffix.Substring(dash + 1);
                        return $"SDRplay {HwVerToModel(hw)} ({serial})";
                    }
                }
            }
            // Legacy format: "sdrplay:<serial>" — model unknown without enumeration.
            return $"SDRplay ({suffix})";
        }

        private static void WriteDouble(IntPtr ptr, int offset, double value)
        {
            Marshal.WriteInt64(ptr, offset, BitConverter.DoubleToInt64Bits(value));
        }

        private static double ReadDouble(IntPtr ptr, int offset)
        {
            return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(ptr, offset));
        }

        private static void ThrowIfError(int err, string operation)
        {
            if (err != 0)
                throw new InvalidOperationException(
                    $"sdrplay_api: {operation} returned error code {err}");
        }

        // Reads fields from sdrplay_api_ErrorInfoT: file[256]@0, function[256]@256, line(int)@512, message[1024]@516
        private static string ReadLastErrorDetail(IntPtr deviceStructPtr)
        {
            try
            {
                IntPtr info = sdrplay_api_GetLastError(deviceStructPtr);
                if (info == IntPtr.Zero) return " [GetLastError=null]";
                string? file    = Marshal.PtrToStringAnsi(info + 0);
                string? func    = Marshal.PtrToStringAnsi(info + 256);
                int     line    = Marshal.ReadInt32(info + 512);
                string? message = Marshal.PtrToStringAnsi(info + 516);
                return $" [{func}:{line} {message} ({file})]";
            }
            catch (Exception ex) { return $" [GetLastError threw {ex.GetType().Name}]"; }
        }
    }
}
