namespace core_service.Services;

public sealed class CircuitBreakerState
{
    private readonly object _gate = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _breakDuration;

    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;

    public CircuitBreakerState(int failureThreshold, TimeSpan breakDuration)
    {
        _failureThreshold = Math.Max(1, failureThreshold);
        _breakDuration = breakDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : breakDuration;
    }

    public bool TryBeginOperation(out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_openUntil is { } openUntil)
            {
                if (openUntil > now)
                {
                    retryAfter = openUntil - now;
                    return false;
                }

                _openUntil = null;
                _consecutiveFailures = 0;
            }

            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures < _failureThreshold)
            {
                return;
            }

            _openUntil = DateTimeOffset.UtcNow.Add(_breakDuration);
            _consecutiveFailures = 0;
        }
    }
}