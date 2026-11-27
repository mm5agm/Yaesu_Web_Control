namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>
/// Remembers which front-panel levels changed while YWC ran, in any mode,
/// so closing YWC can ask to save first. Fed from the live radio state
/// (the radio reports MIC GAIN, AMC and so on as they change).
///
/// Only the last value per mode is kept. Whether it is really a change is
/// decided at close, against the newest saved file (see
/// <see cref="RadioMenuReminder.Compare"/>), so a level turned and turned
/// back is not a change.
///
/// Values that arrive just after a mode change, after connecting, or while
/// YWC's own save/load pass switches modes are the radio reporting, not the
/// user turning a knob: they update the value but do not mark it changed.
/// </summary>
public sealed class RadioMenuSessionChanges
{
    /// <summary>After a mode change the radio sends that mode's levels; not a user change.</summary>
    public static readonly TimeSpan ModeSettle = TimeSpan.FromSeconds(2);

    /// <summary>After connecting, YWC reads every level; not a user change.</summary>
    public static readonly TimeSpan ConnectSettle = TimeSpan.FromSeconds(20);

    private readonly object _lock = new();
    private readonly Dictionary<(string Mode, string Command), (int Value, bool Touched)> _levels = new();
    private string? _modeCode;
    private DateTime _settleUntil = DateTime.MinValue;
    private int _passes;

    /// <summary>The main band's mode, as its MD0 code, changed (or became known).</summary>
    public void OnMode(string? modeCode, DateTime at)
    {
        lock (_lock)
        {
            if (modeCode == _modeCode) return;
            _modeCode = modeCode;
            Settle(at + ModeSettle);
        }
    }

    /// <summary>Ignore level changes as user changes until then (connect, or YWC's own pass).</summary>
    public void Settle(DateTime until)
    {
        lock (_lock)
            if (until > _settleUntil) _settleUntil = until;
    }

    /// <summary>YWC's own save/load pass starts: it switches modes and sets levels itself.</summary>
    public void PassStarted()
    {
        lock (_lock) _passes++;
    }

    /// <summary>The pass ended; the radio's last reports are still settling.</summary>
    public void PassEnded(DateTime at)
    {
        lock (_lock)
        {
            if (_passes > 0) _passes--;
            if (at + ModeSettle > _settleUntil) _settleUntil = at + ModeSettle;
        }
    }

    /// <summary>A level arrived from the radio, by command (MG, AO, PR0, ...).</summary>
    public void OnLevel(string command, int value, DateTime at)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_modeCode)) return;
            var key = (_modeCode, command);
            bool user = _passes == 0 && at >= _settleUntil;
            if (_levels.TryGetValue(key, out var old))
                _levels[key] = (value, old.Touched || (user && old.Value != value));
            else
                _levels[key] = (value, false);
        }
    }

    /// <summary>Levels changed by the user this session: mode code, command, last value.</summary>
    public IReadOnlyList<(string Mode, string Command, int Value)> Touched()
    {
        lock (_lock)
            return _levels.Where(l => l.Value.Touched).Select(l => (l.Key.Mode, l.Key.Command, l.Value.Value)).ToList();
    }

    /// <summary>A save just took every level: nothing is changed any more.</summary>
    public void Saved()
    {
        lock (_lock)
            foreach (var key in _levels.Keys.ToList())
                _levels[key] = (_levels[key].Value, false);
    }
}
