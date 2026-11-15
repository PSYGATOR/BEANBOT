namespace BeanBot.Transport;

/// <summary>
/// Jittered exponential backoff helper capped at 60s (Section 12).
/// </summary>
public sealed class ReconnectBackoff
{
    private readonly int _baseDelayMs;
    private readonly int _maxDelayMs;
    private readonly Random _random;
    private int _attempt;

    public ReconnectBackoff(int baseDelayMs = 1000, int maxDelayMs = 60_000, Random? random = null)
    {
        _baseDelayMs = baseDelayMs;
        _maxDelayMs = maxDelayMs;
        _random = random ?? Random.Shared;
    }

    public int Attempt => _attempt;

    public TimeSpan NextDelay()
    {
        var shift = Math.Min(_attempt, 10);
        var ceilingMs = Math.Min(_maxDelayMs, _baseDelayMs * (1 << shift));
        var jitteredMs = _random.Next(ceilingMs / 2, ceilingMs + 1);
        _attempt++;
        return TimeSpan.FromMilliseconds(jitteredMs);
    }

    public void Reset() => _attempt = 0;
}
