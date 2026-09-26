namespace Yaesu_Web_Control.Services;

/// <summary>
/// One-at-a-time gate for SS (spectrum scope) CAT traffic. Shared by
/// ScopeController, which reads and writes the radio's scope settings for the
/// Scope panel, and SdrManager, which reads the FT-710's span and mode to
/// place its own scope data. Both talk on the port the meter poll uses.
/// </summary>
public static class ScopeCatGate
{
    public static readonly SemaphoreSlim Instance = new(1, 1);
}
