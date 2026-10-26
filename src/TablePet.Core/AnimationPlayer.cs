namespace TablePet.Core;

public sealed record AnimationFrame(string File, int DurationMs);

public sealed record AnimationClip(string Name, IReadOnlyList<AnimationFrame> Frames, bool Loop = false)
{
    public long DurationMs => Frames.Sum(f => (long)f.DurationMs);

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Name) || Frames.Count == 0 || Frames.Any(f => string.IsNullOrWhiteSpace(f.File) || f.DurationMs <= 0))
            throw new ArgumentException("Animation clips need a name, frames, and positive frame durations.");
    }
}

public sealed record AnimationCompleted(long Token, string ClipName);

/// <summary>Frame selection is based on elapsed time, never on the number of timer callbacks.</summary>
public sealed class AnimationPlayer(IClock clock)
{
    private long _startedAt;
    private long _pausedElapsed;
    private bool _completed;
    private long[] _frameEnds = [];
    public event Action<AnimationCompleted>? Completed;
    public event Action<AnimationFrame>? FrameChanged;
    public AnimationClip? CurrentClip { get; private set; }
    public AnimationFrame? CurrentFrame => CurrentClip?.Frames[FrameIndex];
    public int FrameIndex { get; private set; }
    public long Token { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsCompleted => _completed;
    /// <summary>Only moving clips need a continuous display-refresh callback.</summary>
    public bool NeedsContinuousRendering => CurrentClip is { Frames.Count: > 1 } && !IsPaused && !_completed;
    public long ElapsedMs => IsPaused ? _pausedElapsed : Math.Max(0, clock.NowMilliseconds - _startedAt);

    public long Play(AnimationClip clip)
    {
        clip.EnsureValid();
        // Build once per playback. Dense frame sequences must not sum or scan the entire
        // timeline at every display refresh (or during each scheduling query).
        _frameEnds = new long[clip.Frames.Count];
        long end = 0;
        for (var i = 0; i < clip.Frames.Count; i++) _frameEnds[i] = end += clip.Frames[i].DurationMs;
        CurrentClip = clip;
        _startedAt = clock.NowMilliseconds;
        _pausedElapsed = 0;
        _completed = false;
        IsPaused = false;
        FrameIndex = 0;
        Token++;
        FrameChanged?.Invoke(clip.Frames[0]);
        return Token;
    }

    /// <summary>Return through only the displayed part of a clip, starting on the current frame.</summary>
    public long ReverseToStart(string name = "return_to_idle")
    {
        if (CurrentClip is null) throw new InvalidOperationException("No animation to reverse.");
        var frames = CurrentClip.Frames.Take(FrameIndex + 1).Reverse().ToArray();
        return Play(new AnimationClip(name, frames));
    }

    public void Pause()
    {
        if (IsPaused) return;
        _pausedElapsed = ElapsedMs;
        IsPaused = true;
    }

    public void Resume()
    {
        if (!IsPaused) return;
        _startedAt = clock.NowMilliseconds - _pausedElapsed;
        IsPaused = false;
    }

    public void Tick()
    {
        if (CurrentClip is not { } clip || IsPaused || _completed) return;
        var elapsed = ElapsedMs;
        var token = Token;
        var duration = _frameEnds[^1];
        var finished = !clip.Loop && elapsed >= duration;
        var position = clip.Loop ? elapsed % duration : Math.Min(elapsed, duration - 1);
        var index = FrameAt(position);
        if (FrameIndex != index)
        {
            FrameIndex = index;
            FrameChanged?.Invoke(clip.Frames[index]);
            if (Token != token) return;
        }
        if (finished)
        {
            _completed = true;
            Completed?.Invoke(new AnimationCompleted(Token, clip.Name));
        }
    }

    /// <summary>Milliseconds until a frame change or completion; MaxValue means no work is needed.</summary>
    public long NextUpdateInMs
    {
        get
        {
            if (CurrentClip is not { } clip || IsPaused || _completed || (clip.Loop && clip.Frames.Count == 1)) return long.MaxValue;
            var duration = _frameEnds[^1];
            var elapsed = ElapsedMs;
            if (!clip.Loop && elapsed >= duration) return 0;
            var position = clip.Loop ? elapsed % duration : elapsed;
            return _frameEnds[FrameAt(position)] - position;
        }
    }

    private int FrameAt(long position)
    {
        // End times are exclusive: an exact boundary belongs to the next frame.
        var index = Array.BinarySearch(_frameEnds, position);
        return index >= 0 ? index + 1 : ~index;
    }
}
