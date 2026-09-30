// The FT-710's internal FT4222H USB-to-SPI bridge, opened through FTDI's
// D2XX and LibFT4222 libraries.
//
// Lives in the worker, not the main app, for the same reason every SDR does:
// it is native code we did not write, and if it faults it takes the worker
// down rather than YWC.
//
// The open and SPI settings are those found to work on a real FT-710 by
// kd9taw/Nexus (crates/tempo-audio/src/yaesu_wf.rs, GPL-3.0, bench work by
// ON8ST). Not checked on a radio by me; see
// docs/design/ft710-native-scope.md.

using System.Runtime.InteropServices;
using System.Text;
using Yaesu_Web_Control.Services.Sdr;

namespace Yaesu_Web_Control.Workers.Sdr;

internal sealed class Ft4222Bridge : IDisposable
{
    // FTDI device ID as D2XX reports it: VID 0x0403, PID 0x601C.
    private const uint Ft4222Id = 0x0403_601C;

    // LibFT4222 enum values (LibFT4222.h), as used by Nexus.
    private const int  SpiIoSingle     = 1;
    private const int  ClkDiv16        = 4;
    private const int  CpolIdleHigh    = 1;
    private const int  CphaClkTrailing = 1;
    private const byte Ss0             = 1;
    private const int  SysClk48        = 2;

    // Two frames per read. A frame is cut on its trailer out of a stream with
    // no framing, so a one-frame read holds a whole frame only when the
    // boundary happens to fall right (about half the time, measured by
    // Nexus); two frames' worth always holds one.
    public const int ReadBytes = 2 * Ft710ScopeFrame.FrameBytes;

    // ── D2XX ────────────────────────────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint FtCreateDeviceInfoList(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint FtGetDeviceInfoDetail(uint index, out uint flags, out uint type, out uint id,
        out uint locId, byte[] serial, byte[] description, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint FtOpen(int index, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint FtClose(IntPtr handle);

    // ── LibFT4222 ───────────────────────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Ft4222SpiMasterInit(IntPtr handle, int ioLine, int clockDiv, int cpol, int cpha, byte ssoMap);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Ft4222SetClock(IntPtr handle, int clock);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Ft4222SpiMasterSingleRead(IntPtr handle, byte[] buffer, ushort bytesToRead,
        out ushort bytesRead, int isEndTransaction);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Ft4222UnInitialize(IntPtr handle);

    private readonly FtClose                   _close;
    private readonly Ft4222SpiMasterSingleRead _read;
    private readonly Ft4222UnInitialize        _uninit;
    private readonly byte[]                    _readBuf = new byte[ReadBytes];
    private IntPtr _handle;

    public string Description { get; }

    private Ft4222Bridge(IntPtr handle, string description, FtClose close,
        Ft4222SpiMasterSingleRead read, Ft4222UnInitialize uninit)
    {
        _handle = handle;
        Description = description;
        _close  = close;
        _read   = read;
        _uninit = uninit;
    }

    /// <summary>
    /// Finds the FT4222's interface A, opens it and puts it in SPI-master
    /// mode. Blocking, with no timeout of its own: Nexus saw the first open of
    /// a freshly-appeared bridge hang for minutes once. The caller wraps it in
    /// one (see WorkerHost).
    /// </summary>
    /// <exception cref="DllNotFoundException">FTDI's libraries are not installed.</exception>
    /// <exception cref="InvalidOperationException">No FT4222 found, or it would not open.</exception>
    public static Ft4222Bridge Open(Action<string> log)
    {
        IntPtr d2xx = Ft4222Libraries.Load(Ft4222Libraries.D2xxName);
        IntPtr lib  = Ft4222Libraries.Load(Ft4222Libraries.LibFt4222Name);

        var createList = Bind<FtCreateDeviceInfoList>(d2xx, "FT_CreateDeviceInfoList");
        var getDetail  = Bind<FtGetDeviceInfoDetail>(d2xx, "FT_GetDeviceInfoDetail");
        var open       = Bind<FtOpen>(d2xx, "FT_Open");
        var close      = Bind<FtClose>(d2xx, "FT_Close");
        var spiInit    = Bind<Ft4222SpiMasterInit>(lib, "FT4222_SPIMaster_Init");
        var setClock   = Bind<Ft4222SetClock>(lib, "FT4222_SetClock");
        var read       = Bind<Ft4222SpiMasterSingleRead>(lib, "FT4222_SPIMaster_SingleRead");
        var uninit     = Bind<Ft4222UnInitialize>(lib, "FT4222_UnInitialize");

        uint st = createList(out uint count);
        if (st != 0) throw new InvalidOperationException($"FT_CreateDeviceInfoList failed ({st}).");

        // The FT4222H shows up as two D2XX devices, interfaces A and B; the
        // data comes on A. Prefer the one whose description ends in "A", and
        // otherwise take the first FT4222 in the list.
        int    chosen = -1;
        string chosenDesc = "";
        for (uint i = 0; i < count; i++)
        {
            var serial = new byte[16];
            var desc   = new byte[64];
            if (getDetail(i, out _, out _, out uint id, out _, serial, desc, out _) != 0) continue;
            string d = Cstr(desc);
            log($"FTDI device {i}: id=0x{id:X8} '{d}' serial '{Cstr(serial)}'");
            if (id != Ft4222Id) continue;
            bool isA = d.TrimEnd().EndsWith("A", StringComparison.OrdinalIgnoreCase);
            if (chosen < 0 || isA) { chosen = (int)i; chosenDesc = d; }
            if (isA) break;
        }
        if (chosen < 0)
            throw new InvalidOperationException(
                "No FT4222 found. On the FT-710 set menu item SCU-LAN10 (EX 03-01-26) to ON " +
                "(no SCU-LAN10 unit is needed), and check the radio's USB lead is connected.");

        st = open(chosen, out IntPtr handle);
        if (st != 0 || handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"FT_Open of FTDI device {chosen} failed ({st}). Is another program using the FT-710 scope?");

        var bridge = new Ft4222Bridge(handle, chosenDesc, close, read, uninit);
        try
        {
            int s = spiInit(handle, SpiIoSingle, ClkDiv16, CpolIdleHigh, CphaClkTrailing, Ss0);
            if (s != 0) throw new InvalidOperationException($"FT4222_SPIMaster_Init failed ({s}).");
            s = setClock(handle, SysClk48);
            if (s != 0) throw new InvalidOperationException($"FT4222_SetClock failed ({s}).");
        }
        catch
        {
            bridge.Dispose();
            throw;
        }
        return bridge;
    }

    /// <summary>
    /// One SPI read of <see cref="ReadBytes"/>. Returns the bytes that came
    /// back; the caller cuts frames out with Ft710ScopeFrame.Assembler.
    /// </summary>
    public ReadOnlySpan<byte> Read()
    {
        // isEndTransaction = TRUE, so chip-select is released after every
        // read. Nexus measured that with it held, any pause in reading leaves
        // the radio mid-word and every byte after arrives shifted by one bit,
        // for good. With it released, pauses cost nothing.
        int st = _read(_handle, _readBuf, (ushort)ReadBytes, out ushort got, 1);
        if (st != 0) throw new IOException($"FT4222_SPIMaster_SingleRead failed ({st}).");
        return _readBuf.AsSpan(0, Math.Min((int)got, ReadBytes));
    }

    public void Dispose()
    {
        var h = _handle;
        _handle = IntPtr.Zero;
        if (h == IntPtr.Zero) return;
        try { _uninit(h); } catch { }
        try { _close(h); } catch { }
    }

    private static T Bind<T>(IntPtr lib, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(lib, name, out var fn)
            ? Marshal.GetDelegateForFunctionPointer<T>(fn)
            : throw new DllNotFoundException($"{name} is missing from FTDI's library. {Ft4222Libraries.MissingMessage}");

    private static string Cstr(byte[] b)
    {
        int n = Array.IndexOf(b, (byte)0);
        return Encoding.ASCII.GetString(b, 0, n < 0 ? b.Length : n);
    }
}
