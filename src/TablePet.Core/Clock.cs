using System.Diagnostics;

namespace TablePet.Core;

public interface IClock
{
    long NowMilliseconds { get; }
}

/// <summary>A monotonic application clock; wall-clock changes cannot advance a pet animation.</summary>
public sealed class StopwatchClock : IClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public long NowMilliseconds => _watch.ElapsedMilliseconds;
}
