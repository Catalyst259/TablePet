namespace TablePet.Core;

public enum PetState { Idle, FallAsleep, Sleep, Wake, React }
public enum ReactionPhase { None, Rise, Hold, Settle }

/// <summary>The sole owner of behavior state. All calls are made on the application's UI thread.</summary>
public sealed class BehaviorController
{
    private readonly IClock _clock;
    private readonly IReadOnlyDictionary<string, AnimationClip> _clips;
    private readonly Func<int, int, int> _randomBetween;
    private string? _lastIdleAction;
    private long _expectedToken;
    private PetState _expectedState;
    private string _expectedClip = "";
    private long _holdStartedAt;
    private long _dragStartedAt;
    private long _sleepOrigin;
    private long? _reactionEndedAt;
    private PetState _suspendedState;
    private bool _inputBusy;
    private bool _reactionAfterIdleAction;
    private bool _reactionAfterWake;

    public BehaviorController(IClock clock, IReadOnlyDictionary<string, AnimationClip> clips,
        PetSettings? settings = null, Func<int, int, int>? randomBetween = null,
        IEnumerable<IdleAction>? idleActions = null)
    {
        _clock = clock;
        _clips = clips;
        Settings = settings ?? new PetSettings();
        Settings.EnsureValid();
        _randomBetween = randomBetween ?? ((min, max) => Random.Shared.Next(min, max + 1));
        foreach (var name in new[] { "idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle" })
        {
            if (!clips.TryGetValue(name, out var clip)) throw new ArgumentException($"Missing animation: {name}", nameof(clips));
            clip.EnsureValid();
        }
        IdleActions = IdleAction.Available(clips, idleActions);
        Player = new AnimationPlayer(clock);
        Player.Completed += HandleAnimationCompleted;
        LastPetInteraction = _sleepOrigin = clock.NowMilliseconds;
        EnterIdle();
    }

    public event Action<PetState>? StateChanged;
    public AnimationPlayer Player { get; }
    public PetSettings Settings { get; private set; }
    public PetState State { get; private set; } = PetState.Idle;
    public ReactionPhase ReactionPhase { get; private set; }
    public bool IsDragging { get; private set; }
    public bool IsSuspended { get; private set; }
    public bool AutomaticActionsPaused { get; private set; }
    public long LastPetInteraction { get; private set; }
    public long NextBlinkAt { get; private set; }
    // The stored BlinkMinMs/BlinkMaxMs settings continue to control this shared cadence.
    public long NextIdleActionAt => NextBlinkAt;
    public IReadOnlyList<IdleAction> IdleActions { get; }
    public long SleepDeadline => Math.Max(LastPetInteraction, _sleepOrigin) + Settings.SleepAfterMs;
    public long ReactionCooldownUntil { get; private set; }

    public void Tick()
    {
        if (IsSuspended || IsDragging) return;
        Player.Tick();
        var now = _clock.NowMilliseconds;
        if (State == PetState.React && ReactionPhase == ReactionPhase.Hold && now >= _holdStartedAt + Settings.HoldMs)
        {
            ReactionPhase = ReactionPhase.Settle;
            if (!ReturnLoopToStart("hold_to_settle")) Play("settle");
        }
        if (State != PetState.Idle || Player.CurrentClip?.Name != "idle" || AutomaticActionsPaused) return;
        // Sleep wins when both deadlines fall on the same timer dispatch.
        if (Settings.AutoSleep && !_inputBusy && now >= SleepDeadline)
        {
            SetState(PetState.FallAsleep);
            Play("fall_asleep");
        }
        else if (now >= NextIdleActionAt) PlayNextIdleAction();
    }

    /// <summary>Hover wakes into the resting pose without starting a reaction.</summary>
    public void Hover() => Interact(react: false);

    /// <summary>An explicit click wakes if needed, then starts a reaction.</summary>
    public void Click() => Interact(react: true);

    // Retained for existing hosts and runtime probes; a touch means an explicit click.
    public void Touch() => Click();

    private void Interact(bool react)
    {
        if (IsSuspended || IsDragging) return;
        LastPetInteraction = _clock.NowMilliseconds;
        if (State == PetState.FallAsleep)
        {
            _reactionAfterWake = react;
            SetState(PetState.Wake);
            // Do not sample a later frame here: return from exactly the frame the user saw.
            _expectedToken = Player.ReverseToStart();
            _expectedClip = Player.CurrentClip!.Name;
            _expectedState = State;
        }
        else if (State == PetState.Sleep)
        {
            _reactionAfterWake = react;
            SetState(PetState.Wake);
            if (!ReturnLoopToStart("sleep_to_wake")) Play("wake");
        }
        else if (State == PetState.Wake)
        {
            // A later click may upgrade a hover wake; another hover cannot cancel it.
            _reactionAfterWake |= react;
        }
        else if (react && State == PetState.Idle && _clock.NowMilliseconds >= ReactionCooldownUntil)
        {
            if (Player.CurrentClip?.Name == "idle") BeginReaction();
            else if (!_reactionAfterIdleAction)
            {
                // Keep the short blink intact. Longer expressions return through their
                // existing frames so a click never pops an open mouth into the rise pose.
                _reactionAfterIdleAction = true;
                if (Player.CurrentClip?.Name != "blink" && !ReturnLoopToStart("idle_to_react")) BeginReaction();
            }
        }
    }

    public void BeginDrag()
    {
        if (IsSuspended || IsDragging) return;
        _reactionAfterIdleAction = false;
        _reactionAfterWake = false;
        IsDragging = true;
        _dragStartedAt = _clock.NowMilliseconds;
        Player.Pause();
    }

    public void EndDrag()
    {
        if (!IsDragging) return;
        IsDragging = false;
        if (IsSuspended) return;
        LastPetInteraction = _clock.NowMilliseconds;
        if (State == PetState.React && ReactionPhase == ReactionPhase.Hold)
            _holdStartedAt += _clock.NowMilliseconds - _dragStartedAt;
        Player.Resume();
        if (State is PetState.FallAsleep or PetState.Sleep) Hover();
    }

    /// <summary>Host aggregates hidden, session-lock and suspend reasons before calling this.</summary>
    public void SetSuspended(bool suspended)
    {
        if (IsSuspended == suspended) return;
        IsSuspended = suspended;
        if (suspended)
        {
            _reactionAfterIdleAction = false;
            _reactionAfterWake = false;
            _suspendedState = State;
            IsDragging = false;
            _inputBusy = false;
            Player.Pause();
            return;
        }
        LastPetInteraction = _sleepOrigin = _clock.NowMilliseconds;
        ReactionCooldownUntil = 0;
        _reactionEndedAt = null;
        if (_suspendedState == PetState.Sleep)
        {
            SetState(PetState.Sleep);
            Play("sleep_loop", loop: true);
        }
        else EnterIdle();
    }

    /// <summary>Menu, settings, or a held mouse button suppress sleep. Closing starts a fresh quiet period.</summary>
    public void SetInputBusy(bool busy)
    {
        if (_inputBusy == busy) return;
        _inputBusy = busy;
        if (!busy) _sleepOrigin = _clock.NowMilliseconds;
    }

    public void SetAutomaticActionsPaused(bool paused)
    {
        if (AutomaticActionsPaused == paused) return;
        AutomaticActionsPaused = paused;
        if (!paused)
        {
            _sleepOrigin = _clock.NowMilliseconds;
            ScheduleBlink();
        }
    }

    public void ApplySettings(PetSettings settings)
    {
        settings.EnsureValid();
        var rescheduleBlink = Settings.BlinkMinMs != settings.BlinkMinMs || Settings.BlinkMaxMs != settings.BlinkMaxMs;
        var rescheduleCooldown = Settings.CooldownMs != settings.CooldownMs;
        Settings = settings;
        if (rescheduleBlink) ScheduleBlink();
        if (rescheduleCooldown && _reactionEndedAt is { } endedAt)
            ReactionCooldownUntil = endedAt + settings.CooldownMs;
        Tick();
    }

    /// <summary>Public so asynchronous hosts can forward a completion; obsolete tokens are harmless.</summary>
    public void HandleAnimationCompleted(AnimationCompleted completion)
    {
        if (IsSuspended || IsDragging || completion.Token != _expectedToken || completion.Token != Player.Token
            || State != _expectedState || completion.ClipName != _expectedClip) return;
        switch (State)
        {
            case PetState.Idle:
                if (_reactionAfterIdleAction) BeginReaction();
                else EnterIdle();
                break;
            case PetState.FallAsleep:
                SetState(PetState.Sleep);
                Play("sleep_loop", loop: true);
                break;
            case PetState.Wake:
                if (completion.ClipName == "sleep_to_wake") Play("wake");
                else if (_reactionAfterWake) BeginReaction();
                else
                {
                    // Start a full quiet period once the hover wake is visibly complete.
                    _sleepOrigin = _clock.NowMilliseconds;
                    EnterIdle();
                }
                break;
            case PetState.React when ReactionPhase == ReactionPhase.Rise:
                ReactionPhase = ReactionPhase.Hold;
                _holdStartedAt = _clock.NowMilliseconds;
                Play("hold", loop: true);
                break;
            case PetState.React when ReactionPhase == ReactionPhase.Settle:
                if (completion.ClipName == "hold_to_settle")
                {
                    Play("settle");
                    break;
                }
                _reactionEndedAt = _clock.NowMilliseconds;
                ReactionCooldownUntil = _clock.NowMilliseconds + Settings.CooldownMs;
                EnterIdle();
                break;
        }
    }

    public long NextUpdateInMs
    {
        get
        {
            if (IsSuspended || IsDragging) return long.MaxValue;
            var delay = Player.NextUpdateInMs;
            var now = _clock.NowMilliseconds;
            if (State == PetState.React && ReactionPhase == ReactionPhase.Hold)
                delay = Math.Min(delay, Math.Max(0, _holdStartedAt + Settings.HoldMs - now));
            if (State == PetState.Idle && Player.CurrentClip?.Name == "idle" && !AutomaticActionsPaused)
            {
                delay = Math.Min(delay, Math.Max(0, NextIdleActionAt - now));
                if (Settings.AutoSleep && !_inputBusy) delay = Math.Min(delay, Math.Max(0, SleepDeadline - now));
            }
            return delay;
        }
    }

    private void BeginReaction()
    {
        _reactionAfterIdleAction = false;
        _reactionAfterWake = false;
        SetState(PetState.React);
        ReactionPhase = ReactionPhase.Rise;
        Play("rise");
    }

    private void EnterIdle()
    {
        _reactionAfterIdleAction = false;
        _reactionAfterWake = false;
        ReactionPhase = ReactionPhase.None;
        SetState(PetState.Idle);
        Play("idle", loop: true);
        ScheduleBlink();
    }

    private void SetState(PetState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private void Play(string name, bool? loop = null)
    {
        var clip = _clips[name];
        if (loop is { } repeats) clip = clip with { Loop = repeats };
        _expectedState = State;
        _expectedClip = clip.Name;
        _expectedToken = Player.Play(clip);
    }

    /// <summary>
    /// A loop or idle expression can be interrupted at any phase. Reuse its shortest path back to
    /// the shared base pose, keeping the first visible frame exact and the response brief.
    /// </summary>
    private bool ReturnLoopToStart(string name)
    {
        var clip = Player.CurrentClip!;
        var baseFile = clip.Frames[0].File;
        if (Player.CurrentFrame!.File == baseFile) return false;
        var omitExpressionHolds = name == "idle_to_react";
        AnimationFrame[] Path(int step)
        {
            var path = new List<AnimationFrame> { clip.Frames[Player.FrameIndex] };
            for (var offset = 1; offset <= clip.Frames.Count; offset++)
            {
                var index = (Player.FrameIndex + step * offset + clip.Frames.Count) % clip.Frames.Count;
                // A deliberate expression hold must not consume most of the brief
                // click response. Keep one sample per consecutive visible pose;
                // the source animation and the sleep/standing return paths keep
                // their original timing.
                if (!omitExpressionHolds || clip.Frames[index].File != path[^1].File)
                    path.Add(clip.Frames[index]);
                if (clip.Frames[index].File == baseFile) break;
            }
            return path.ToArray();
        }
        var forward = Path(1);
        var reverse = Path(-1);
        var forwardDuration = forward.Sum(frame => (long)frame.DurationMs);
        var reverseDuration = reverse.Sum(frame => (long)frame.DurationMs);
        var route = forwardDuration <= reverseDuration ? forward : reverse;
        var duration = (int)Math.Min(240, Math.Min(forwardDuration, reverseDuration));
        // 60 samples over 240 ms retain up to 250 Hz, without waiting for a full breath.
        var count = Math.Min(route.Length, Math.Min(60, duration));
        var frames = Enumerable.Range(0, count).Select(i => new AnimationFrame(
            route[(int)Math.Round(i * (route.Length - 1d) / (count - 1))].File,
            (int)Math.Round((i + 1d) * duration / count) - (int)Math.Round(i * (double)duration / count))).ToArray();
        _expectedState = State;
        _expectedClip = name;
        _expectedToken = Player.Play(new AnimationClip(name, frames));
        return true;
    }

    private void ScheduleBlink() => NextBlinkAt = _clock.NowMilliseconds + _randomBetween(Settings.BlinkMinMs, Settings.BlinkMaxMs);

    private void PlayNextIdleAction()
    {
        var choices = IdleActions.Count == 1 ? IdleActions : IdleActions.Where(a => a.Clip != _lastIdleAction).ToArray();
        var selected = choices[0];
        if (choices.Count > 1)
        {
            var ticket = _randomBetween(1, choices.Sum(a => a.Weight));
            foreach (var choice in choices)
            {
                ticket -= choice.Weight;
                if (ticket > 0) continue;
                selected = choice;
                break;
            }
        }
        _lastIdleAction = selected.Clip;
        Play(selected.Clip, loop: false);
    }
}
