// When to change an SDRplay receiver's fixed gain because of ADC overload.
//
// YWC runs the SDRplay receivers with the API's AGC off and a fixed gain
// reduction, because the AGC turned the gain down for strong stations on a
// busy band and lost the weak ones (measured 2026-11-14). A fixed gain can
// overload on a strong band, though: on 160 m at night, medium-wave stations
// in the 1 MHz span made the RSP1 show a false 46 dB signal. So:
//
//   - while the API says the ADC is overloaded, raise the gain reduction a
//     step at a time;
//   - after a quiet spell, lower it again a step at a time, back to the start
//     value, because the worker is never told when the radio changes band and
//     a gain lowered for 160 m would otherwise stay low on 20 m;
//   - if lowering it brings the overload straight back, hold that level for a
//     while before trying again, so the floor doesn't keep jumping.
//
// No hardware here, so the rules can be unit tested. Not thread-safe: the
// caller locks.

namespace Yaesu_Web_Control.Services.Sdr;

internal sealed class OverloadGainPolicy
{
    public const int StartGrDb = 40;
    public const int MaxGrDb   = 59;   // RSP1 IF gain reduction range is 20-59
    public const int StepDb    = 3;

    public static readonly TimeSpan QuietBeforeStepDown = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BlameWindow         = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Hold                = TimeSpan.FromMinutes(10);

    private DateTime _lastOverload = DateTime.MinValue;
    private DateTime _lastStepDown = DateTime.MinValue;
    private DateTime _holdUntil    = DateTime.MinValue;

    public int GrDb { get; private set; } = StartGrDb;

    public void Reset()
    {
        GrDb = StartGrDb;
        _lastOverload = _lastStepDown = _holdUntil = DateTime.MinValue;
    }

    /// <summary>
    /// The ADC is overloaded. Returns the new gain reduction, or null if it
    /// is already at the maximum.
    /// </summary>
    public int? OnOverload(DateTime now)
    {
        if (now - _lastStepDown < BlameWindow) _holdUntil = now + Hold;
        _lastOverload = now;
        if (GrDb >= MaxGrDb) return null;
        GrDb = Math.Min(MaxGrDb, GrDb + StepDb);
        return GrDb;
    }

    /// <summary>
    /// Called every few seconds. Returns the new, lower gain reduction when
    /// it is time to step back down, otherwise null.
    /// </summary>
    public int? OnTick(DateTime now, bool overloaded)
    {
        if (overloaded || GrDb <= StartGrDb) return null;
        if (now - _lastOverload < QuietBeforeStepDown || now < _holdUntil) return null;
        GrDb = Math.Max(StartGrDb, GrDb - StepDb);
        _lastStepDown = now;
        return GrDb;
    }
}
