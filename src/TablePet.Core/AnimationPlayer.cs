namespace TablePet.Core;

public sealed record AnimationFrame(string File, int DurationMs);

public sealed record AnimationClip(string Name, IReadOnlyList<AnimationFrame> Frames, bool Loop = false, int? SampleFps = null)
{
    public long DurationMs => Frames.Sum(f => (long)f.DurationMs);
    /// <summary>Generated transitions may have evenly spaced slots at a noninteger frame rate.</summary>
    public bool UniformFrameTiming { get; init; }

    // Sampling metadata describes rounded storage, not arbitrary expression holds.
    // Older/variable clips keep their explicit durations if the declaration does not fit.
    internal double? UniformFrameIntervalMs
    {
        get
        {
            if (Frames.Count < 2 || !UniformFrameTiming && SampleFps is not (> 0 and <= 1000)) return null;
            var duration = DurationMs;
            double Boundary(int i) => UniformFrameTiming ? i * (double)duration / Frames.Count : i * 1000d / SampleFps!.Value;
            var rounding = UniformFrameTiming ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero;
            long start = 0;
            for (var i = 0; i < Frames.Count; i++)
            {
                var expectedStart = Math.Round(Boundary(i), rounding);
                var expectedEnd = Math.Min(duration, Math.Round(Boundary(i + 1), rounding));
                if (start != expectedStart || start + Frames[i].DurationMs != expectedEnd) return null;
                start += Frames[i].DurationMs;
            }
            return Boundary(1);
        }
    }

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
    private double _startedAt;
    private double _pausedElapsed;
    private bool _completed;
    private double[] _frameEnds = [];
    private int? _sampleFps;
    private double? _uniformFrameIntervalMs;
    private TimeSpan? _presentationAnchor, _lastPresentation, _presentationContext;
    private double _presentationAnchorElapsed;
    private const double FloatingPointToleranceMs = 0.0000001;
    private const double PresentationToleranceMs = 1d / TimeSpan.TicksPerMillisecond + FloatingPointToleranceMs;
    public event Action<AnimationCompleted>? Completed;
    public event Action<AnimationFrame>? FrameChanged;
    public AnimationClip? CurrentClip { get; private set; }
    public AnimationFrame? CurrentFrame => CurrentClip?.Frames[FrameIndex];
    public int FrameIndex { get; private set; }
    public long Token { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsCompleted => _completed;
    /// <summary>Elapsed time used by the latest presentation, unchanged between ticks or while paused.</summary>
    public double PresentedElapsedMs { get; private set; }
    /// <summary>Only moving clips need a continuous display-refresh callback.</summary>
    public bool NeedsContinuousRendering => CurrentClip is { Frames.Count: > 1 } && !IsPaused && !_completed;
    private double PreciseElapsedMs => IsPaused ? _pausedElapsed : Math.Max(0, clock.PreciseMilliseconds - _startedAt);
    public long ElapsedMs => (long)PreciseElapsedMs;

    public long Play(AnimationClip clip)
    {
        clip.EnsureValid();
        // Build once per playback. Dense frame sequences must not sum or scan the entire
        // timeline at every display refresh (or during each scheduling query).
        _frameEnds = new double[clip.Frames.Count];
        _uniformFrameIntervalMs = clip.UniformFrameIntervalMs;
        _sampleFps = _uniformFrameIntervalMs is not null && !clip.UniformFrameTiming ? clip.SampleFps : null;
        long end = 0;
        for (var i = 0; i < clip.Frames.Count; i++) _frameEnds[i] = end += clip.Frames[i].DurationMs;
        if (_uniformFrameIntervalMs is { } interval)
            for (var i = 0; i < clip.Frames.Count - 1; i++) _frameEnds[i] = (i + 1) * interval;
        CurrentClip = clip;
        _startedAt = clock.PreciseMilliseconds;
        AnchorPresentation(_presentationContext, 0);
        _pausedElapsed = 0;
        _completed = false;
        IsPaused = false;
        FrameIndex = 0;
        PresentedElapsedMs = 0;
        Token++;
        FrameChanged?.Invoke(clip.Frames[0]);
        return Token;
    }

    /// <summary>Return through only the displayed part of a clip, starting on the current frame.</summary>
    public long ReverseToStart(string name = "return_to_idle")
    {
        if (CurrentClip is null) throw new InvalidOperationException("No animation to reverse.");
        var frames = CurrentClip.Frames.Take(FrameIndex + 1).Reverse().ToArray();
        var duration = frames.Sum(frame => (long)frame.DurationMs);
        int? sampleFps = null;
        var uniformTiming = false;
        if (_sampleFps is > 0 and <= 120 && frames.Length > 1)
        {
            // Reversing rounded sampling slots changes their rounding phase. Rebuild
            // their canonical timing so the reverse retains the source's true cadence.
            sampleFps = _sampleFps;
            var period = 1000d / sampleFps.Value;
            frames = frames.Select((frame, i) => frame with
            {
                DurationMs = (int)(Math.Min(duration, Math.Round((i + 1) * period, MidpointRounding.AwayFromZero))
                    - Math.Round(i * period, MidpointRounding.AwayFromZero))
            }).ToArray();
        }
        else if (frames.Length > 1 && (frames.Length * 1000L > duration * 60 || frames.Any(frame => frame.DurationMs < 16)))
        {
            // Old dense packs can still be interrupted. Generated reversal clips
            // retain the visible first and resting last pose without exceeding 60 Hz.
            duration = Math.Max(34, duration);
            // A sparse old clip may mix a very long hold with short transitions.
            // Do not expand that hold into thousands of duplicate timeline slots.
            var count = (int)Math.Min(frames.Length, Math.Floor(duration * 60d / 1000));
            uniformTiming = true;
            var original = frames;
            var ends = original.Select(frame => (long)frame.DurationMs).ToArray();
            for (var i = 1; i < ends.Length; i++) ends[i] += ends[i - 1];
            frames = Enumerable.Range(0, count).Select(i =>
            {
                var start = Math.Round(i * (double)duration / count);
                var end = Math.Round((i + 1d) * duration / count);
                var time = (long)(i * (double)ends[^1] / count);
                var index = Array.BinarySearch(ends, time);
                index = i == count - 1 ? original.Length - 1 : index >= 0 ? index + 1 : ~index;
                return new AnimationFrame(original[index].File,
                    (int)(end - start));
            }).ToArray();
        }
        return Play(new AnimationClip(name, frames, SampleFps: sampleFps) { UniformFrameTiming = uniformTiming });
    }

    public void Pause()
    {
        if (IsPaused) return;
        _pausedElapsed = PreciseElapsedMs;
        AnchorPresentation(null, 0);
        IsPaused = true;
    }

    public void Resume()
    {
        if (!IsPaused) return;
        _startedAt = clock.PreciseMilliseconds - _pausedElapsed;
        AnchorPresentation(_presentationContext, _pausedElapsed);
        IsPaused = false;
    }

    public void Tick(TimeSpan? presentationTime = null)
    {
        if (CurrentClip is not { } clip || IsPaused || _completed) return;
        if (presentationTime is { } present && _lastPresentation == present) return;
        using var presentation = EnterPresentation(presentationTime);
        TickAt(clip, ElapsedAtPresentation(presentationTime));
    }

    // The controller keeps this scope through deadline decisions after Player.Tick,
    // so every clip started during the same compositor callback shares its zero point.
    internal PresentationScope EnterPresentation(TimeSpan? time) => new(this, time);

    internal readonly struct PresentationScope : IDisposable
    {
        private readonly AnimationPlayer _player;
        private readonly TimeSpan? _previous;
        internal PresentationScope(AnimationPlayer player, TimeSpan? time)
        {
            _player = player;
            _previous = player._presentationContext;
            player._presentationContext = time;
        }
        public void Dispose() => _player._presentationContext = _previous;
    }

    private double ElapsedAtPresentation(TimeSpan? presentationTime)
    {
        var now = clock.PreciseMilliseconds;
        var elapsed = Math.Max(0, now - _startedAt);
        if (presentationTime is not { } present)
        {
            AnchorPresentation(null, 0);
            return elapsed;
        }
        if (_presentationAnchor is null || _lastPresentation is { } last && present < last)
            AnchorPresentation(present, elapsed);
        elapsed = _presentationAnchorElapsed + (present - _presentationAnchor!.Value).TotalMilliseconds;
        _lastPresentation = present;
        // Input events and pause/resume run between compositor callbacks. Keep
        // their wall-clock extrapolation continuous with the last presented pose.
        _startedAt = now - elapsed;
        return elapsed;
    }

    private void AnchorPresentation(TimeSpan? time, double elapsed)
    {
        _presentationAnchor = _lastPresentation = time;
        _presentationAnchorElapsed = elapsed;
    }

    private void TickAt(AnimationClip clip, double elapsed)
    {
        PresentedElapsedMs = elapsed;
        var token = Token;
        var duration = _frameEnds[^1];
        var finished = !clip.Loop && elapsed >= duration;
        var position = clip.Loop ? LoopPosition(elapsed, duration) : Math.Min(elapsed, duration);
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
            var elapsed = PreciseElapsedMs;
            if (!clip.Loop && elapsed >= duration) return 0;
            var position = clip.Loop ? LoopPosition(elapsed, duration) : elapsed;
            return (long)Math.Ceiling(Math.Max(0, _frameEnds[FrameAt(position)] - position));
        }
    }

    private double LoopPosition(double elapsed, double duration)
    {
        var position = elapsed % duration;
        return _uniformFrameIntervalMs is not null && duration - position <= FloatingPointToleranceMs ? 0 : position;
    }

    private int FrameAt(double position)
    {
        // End times are exclusive: an exact boundary belongs to the next frame.
        // RenderingTime is quantized to 100 ns ticks. Allow one tick at sampled
        // frame boundaries; completion and loop wrapping retain their exact ends.
        var tolerance = _uniformFrameIntervalMs is null ? 0 : _lastPresentation is null ? FloatingPointToleranceMs : PresentationToleranceMs;
        var index = Array.BinarySearch(_frameEnds, position + tolerance);
        return Math.Min(_frameEnds.Length - 1, index >= 0 ? index + 1 : ~index);
    }
}
