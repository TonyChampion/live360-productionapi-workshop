namespace Ticketing.Api.Chaos;

/// <summary>
/// Live knobs for the fault-injection decorator. One shared instance (singleton),
/// changed at runtime through /dev/chaos or the client's chaos panel.
/// </summary>
public class ChaosSettings
{
    private readonly Lock _gate = new();
    private ChaosState _state = ChaosState.Off;

    public ChaosState Current
    {
        get { lock (_gate) return _state; }
    }

    public ChaosState Apply(ChaosState next)
    {
        var clamped = next with
        {
            LatencyMs = Math.Clamp(next.LatencyMs, 0, 30_000),
            ErrorRate = Math.Clamp(next.ErrorRate, 0, 1),
            FailNextCalls = Math.Max(0, next.FailNextCalls)
        };
        lock (_gate) _state = clamped;
        return clamped;
    }

    public ChaosState Reset() => Apply(ChaosState.Off);

    /// <summary>Uses up one of the "fail next N calls" failures, if any are left.</summary>
    public bool TryConsumeFailure()
    {
        lock (_gate)
        {
            if (_state.FailNextCalls <= 0) return false;
            _state = _state with { FailNextCalls = _state.FailNextCalls - 1 };
            return true;
        }
    }
}

/// <param name="LatencyMs">Delay added to every store call.</param>
/// <param name="ErrorRate">Chance (0 to 1) that a store call throws a transient error.</param>
/// <param name="Available">When false, every store call fails as if the dependency were down.</param>
/// <param name="FailNextCalls">The next N store calls throw a transient error, then calls succeed again.
/// Deterministic, unlike ErrorRate, so it's what tests use.</param>
public record ChaosState(int LatencyMs, double ErrorRate, bool Available, int FailNextCalls = 0)
{
    public static ChaosState Off => new(0, 0, true);
}
