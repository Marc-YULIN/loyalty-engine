namespace Loyalty.Tests;

public sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private long utcTicks = initial.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks);
        Interlocked.Add(ref utcTicks, duration.Ticks);
    }
}
