using System.Diagnostics;

namespace TablePet.Core;

public interface IClock
{
    long NowMilliseconds { get; }
    double PreciseMilliseconds => NowMilliseconds;
}

/// <summary>A monotonic application clock; wall-clock changes cannot advance a pet animation.</summary>
public sealed class StopwatchClock : IClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public long NowMilliseconds => _watch.ElapsedMilliseconds;
    public double PreciseMilliseconds => _watch.Elapsed.TotalMilliseconds;
}
