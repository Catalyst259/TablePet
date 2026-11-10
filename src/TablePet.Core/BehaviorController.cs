namespace TablePet.Core;

public enum PetState { Idle, FallAsleep, Sleep, Wake, React, Work }
public enum ReactionPhase { None, Rise, Hold, Settle, Wink, Meow, Firefly }

/// <summary>The sole owner of behavior state. All calls are made on the application's UI thread.</summary>
public sealed partial class BehaviorController
{
    private readonly IClock _clock;
    private readonly IReadOnlyDictionary<string, AnimationClip> _clips;
    private readonly Func<int, int, int> _randomBetween;
    private readonly bool _canWink;
    private readonly bool _canMeow;
    private readonly bool _canFirefly;
    private long _expectedToken;
    private PetState _expectedState;
    private string _expectedClip = "";
    private long _holdStartedAt;
    private long _dragStartedAt;
    private long _sleepOrigin;
    private long? _reactionEndedAt;
    private PetState _suspendedState;
    private bool _inputBusy;
    private ReactionKind _reactionAfterIdleAction;
    private ReactionKind _reactionAfterWake;
    private enum ReactionKind { None, Click, Meow }

    public BehaviorController(IClock clock, IReadOnlyDictionary<string, AnimationClip> clips,
        PetSettings? settings = null, Func<int, int, int>? randomBetween = null,
        IEnumerable<IdleAction>? idleActions = null, Action<BehaviorFact>? factSink = null,
        bool externalSpeechEnabled = false)
    {
        _clock = clock;
        FactOccurred = factSink;
        ExternalSpeechEnabled = externalSpeechEnabled;
        _clips = clips;
        Settings = settings ?? new PetSettings();
        Settings.EnsureValid();
        _randomBetween = randomBetween ?? ((min, max) => Random.Shared.Next(min, max + 1));
        foreach (var name in new[] { "idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle" })
        {
            if (!clips.TryGetValue(name, out var clip)) throw new ArgumentException($"Missing animation: {name}", nameof(clips));
            clip.EnsureValid();
        }
        var restingFrame = clips["idle"].Frames[0].File;
        bool SafeReaction(string name) => clips.TryGetValue(name, out var clip) && clip.Name == name && !clip.Loop
            && clip.Frames.Count >= 2 && clip.Frames.All(f => !string.IsNullOrWhiteSpace(f.File) && f.DurationMs > 0)
            && clip.Frames[0].File == restingFrame && clip.Frames[^1].File == restingFrame;
        _canWink = SafeReaction("wink");
        _canMeow = SafeReaction("meow");
        _canFirefly = SafeReaction("firefly");
        IdleActions = IdleAction.Available(clips, idleActions);
        Player = new AnimationPlayer(clock);
        Player.Completed += HandleAnimationCompleted;
        LastPetInteraction = _sleepOrigin = clock.NowMilliseconds;
        EnterIdle();
        InitializeWorkMode();
    }

    public event Action<PetState>? StateChanged;
    public AnimationPlayer Player { get; }
    public PetSettings Settings { get; private set; }
    public PetState State { get; private set; } = PetState.Idle;
    public ReactionPhase ReactionPhase { get; private set; }
    public bool IsDragging { get; private set; }
    public bool IsSuspended { get; private set; }
    public bool AutomaticActionsPaused { get; private set; }
    public bool IsAutomaticActionActive => State == PetState.Idle && Player.CurrentClip?.Name is { } name && name != "idle";
    public bool IsFireflyActive => !IsSuspended && _canFirefly && State is PetState.Idle or PetState.React
        && ReferenceEquals(Player.CurrentClip, _clips["firefly"]);
    public long LastPetInteraction { get; private set; }
    public long NextBlinkAt { get; private set; }
    // The stored BlinkMinMs/BlinkMaxMs settings continue to control this shared cadence.
    public long NextIdleActionAt => NextBlinkAt;
    public IReadOnlyList<IdleAction> IdleActions { get; }
    public long SleepDeadline => Math.Max(LastPetInteraction, _sleepOrigin) + Settings.SleepAfterMs;
    public long ReactionCooldownUntil { get; private set; }

    public void Tick(TimeSpan? presentationTime = null)
    {
        if (IsSuspended || IsDragging) return;
        if (WorkSessionActive && AutomaticActionsPaused && Mode == CompanionMode.Work) return;
        using var presentation = Player.EnterPresentation(presentationTime);
        Player.Tick(presentationTime);
        if (TickWorkMode()) return;
        var now = _clock.NowMilliseconds;
        if (ReactionPhase == ReactionPhase.Hold && now >= _holdStartedAt + Settings.HoldMs)
        {
            ReactionPhase = ReactionPhase.Settle;
            if (!ReturnLoopToStart("hold_to_settle")) Play("settle");
        }
        if (State != PetState.Idle || Player.CurrentClip?.Name != "idle" || AutomaticActionsPaused || _inputBusy) return;
        // Sleep wins when both deadlines fall on the same timer dispatch.
        if (Settings.AutoSleep && now >= SleepDeadline)
        {
            SetState(PetState.FallAsleep);
            Play("fall_asleep");
        }
        else if (now >= NextIdleActionAt) PlayNextIdleAction();
    }

    /// <summary>Hover wakes into the resting pose without starting a reaction.</summary>
    public void Hover() => Interact(ReactionKind.None);

    /// <summary>An explicit click wakes if needed, then starts a reaction.</summary>
    public void Click() => Interact(ReactionKind.Click);

    /// <summary>A double click wakes if needed, then performs one meow.</summary>
    public void DoubleClick() => Interact(ReactionKind.Meow);

    // Retained for existing hosts and runtime probes; a touch means an explicit click.
    public void Touch() => Click();

    private void Interact(ReactionKind reaction)
    {
        if (IsSuspended || IsDragging) return;
        if (WorkSessionActive || Mode == CompanionMode.Work) { InteractWithWork(reaction); return; }
        LastPetInteraction = _clock.NowMilliseconds;
        if (State == PetState.FallAsleep)
        {
            if (reaction != ReactionKind.None) RememberInteraction();
            _reactionAfterWake = reaction;
            SetState(PetState.Wake);
            // Do not sample a later frame here: return from exactly the frame the user saw.
            _expectedToken = Player.ReverseToStart();
            _expectedClip = Player.CurrentClip!.Name;
            _expectedState = State;
        }
        else if (State == PetState.Sleep)
        {
            if (reaction != ReactionKind.None) RememberInteraction(fromNaturalSleep: true);
            _reactionAfterWake = reaction;
            SetState(PetState.Wake);
            if (!ReturnLoopToStart("sleep_to_wake")) Play("wake");
        }
        else if (State == PetState.Wake)
        {
            if (reaction != ReactionKind.None) RememberInteraction();
            // Explicit input may upgrade a hover wake. Meow takes precedence over
            // a pending ordinary click, and later hover/click input cannot erase it.
            if (reaction > _reactionAfterWake) _reactionAfterWake = reaction;
        }
        else if (reaction != ReactionKind.None && State == PetState.Idle && _clock.NowMilliseconds >= ReactionCooldownUntil)
        {
            RememberInteraction();
            if (Player.CurrentClip?.Name == "idle") BeginReaction(reaction);
            else if (_reactionAfterIdleAction == ReactionKind.None)
            {
                // Blink and the complete automatic rise/firefly sequence finish
                // naturally. In particular, a firefly return clip would lose its
                // synchronized decorative channel. Only yawn uses a short return.
                _reactionAfterIdleAction = reaction;
                if (ReactionPhase == ReactionPhase.None && Player.CurrentClip?.Name != "blink"
                    && !ReturnLoopToStart("idle_to_react")) BeginReaction(reaction);
            }
            else if (reaction > _reactionAfterIdleAction) _reactionAfterIdleAction = reaction;
        }
    }

    public void BeginDrag()
    {
        if (IsSuspended || IsDragging) return;
        CancelPendingInteraction();
        _restInteractionId = null;
        FinishFireflyFact(invalidated: true);
        _reactionAfterIdleAction = ReactionKind.None;
        _reactionAfterWake = ReactionKind.None;
        IsDragging = true;
        _dragStartedAt = _clock.NowMilliseconds;
        Player.Pause();
        SyncWorkPause();
    }

    public void EndDrag()
    {
        if (!IsDragging) return;
        if (!WorkSessionActive) _speechStartedAt += _clock.NowMilliseconds - _dragStartedAt;
        IsDragging = false;
        if (WorkSessionActive) { SyncWorkPause(); return; }
        if (IsSuspended) return;
        LastPetInteraction = _clock.NowMilliseconds;
        if (ReactionPhase == ReactionPhase.Hold)
            _holdStartedAt += _clock.NowMilliseconds - _dragStartedAt;
        Player.Resume();
        if (State is PetState.FallAsleep or PetState.Sleep) Hover();
    }

    /// <summary>Host aggregates hidden, session-lock and suspend reasons before calling this.</summary>
    public void SetSuspended(bool suspended)
    {
        if (IsSuspended == suspended) return;
        if (suspended)
        {
            CancelPendingInteraction();
            _restInteractionId = null;
            FinishFireflyFact(invalidated: true);
        }
        IsSuspended = suspended;
        if (WorkSessionActive)
        {
            if (suspended) { IsDragging = false; _inputBusy = false; }
            SyncWorkPause();
            return;
        }
        if (suspended)
        {
            _reactionAfterIdleAction = ReactionKind.None;
            _reactionAfterWake = ReactionKind.None;
            _suspendedState = State;
            IsDragging = false;
            _inputBusy = false;
            Player.Pause();
            return;
        }
        LastPetInteraction = _sleepOrigin = _clock.NowMilliseconds;
        _speechText = null;
        ReactionCooldownUntil = 0;
        _reactionEndedAt = null;
        if (_suspendedState == PetState.Sleep)
        {
            SetState(PetState.Sleep);
            Play("sleep_loop", loop: true);
        }
        else EnterIdle();
    }

    /// <summary>Menu, settings, and pending gestures suppress new automatic actions. Closing starts a fresh quiet period.</summary>
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
        if (WorkSessionActive) { SyncWorkPause(); return; }
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
        var oldMode = Mode;
        if (_workRoundId is not null && !_workRoundEnded && Settings.WorkDurationMs != settings.WorkDurationMs)
            _workDurationChanged = true;
        if (_breakId is not null && !_breakEnded && Settings.BreakDurationMs != settings.BreakDurationMs)
            _breakDurationChanged = true;
        Settings = settings;
        ApplyWorkSettings(oldMode);
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
        if (WorkSessionActive && AutomaticActionsPaused && Mode == CompanionMode.Work) return;
        if (HandleWorkCompleted(completion)) return;
        if (completion.ClipName == "firefly") FinishFireflyFact(invalidated: false);
        switch (State)
        {
            case PetState.Idle:
                if (ReactionPhase == ReactionPhase.Rise) { BeginHold(); break; }
                if (ReactionPhase == ReactionPhase.Hold) break;
                if (ReactionPhase == ReactionPhase.Settle && completion.ClipName == "hold_to_settle")
                {
                    Play("settle");
                    break;
                }
                // Automatic actions neither consume reaction cooldown nor extend
                // the sleep deadline. Tick can sleep at this resting endpoint.
                if (_reactionAfterIdleAction != ReactionKind.None) BeginReaction(_reactionAfterIdleAction);
                else EnterIdle();
                break;
            case PetState.FallAsleep:
                SetState(PetState.Sleep);
                Play("sleep_loop", loop: true);
                break;
            case PetState.Wake:
                if (completion.ClipName == "sleep_to_wake") Play("wake");
                else if (_reactionAfterWake != ReactionKind.None) BeginReaction(_reactionAfterWake);
                else
                {
                    // Start a full quiet period once the hover wake is visibly complete.
                    _sleepOrigin = _clock.NowMilliseconds;
                    EnterIdle();
                }
                break;
            case PetState.React when ReactionPhase == ReactionPhase.Rise:
                BeginHold();
                break;
            case PetState.React when ReactionPhase == ReactionPhase.Settle:
                if (completion.ClipName == "hold_to_settle")
                {
                    Play("settle");
                    break;
                }
                EndReaction();
                break;
            case PetState.React when ReactionPhase == ReactionPhase.Wink:
            case PetState.React when ReactionPhase == ReactionPhase.Meow:
            case PetState.React when ReactionPhase == ReactionPhase.Firefly:
                EndReaction();
                break;
        }
    }

    public long NextUpdateInMs
    {
        get
        {
            if (IsSuspended || IsDragging) return long.MaxValue;
            if (WorkSessionActive && AutomaticActionsPaused && Mode == CompanionMode.Work) return long.MaxValue;
            var delay = Player.NextUpdateInMs;
            if (WorkSessionActive && WorkPhase != WorkPhase.WaitingForIdle) return WorkNextUpdate(delay);
            var now = _clock.NowMilliseconds;
            if (ReactionPhase == ReactionPhase.Hold)
                delay = Math.Min(delay, Math.Max(0, _holdStartedAt + Settings.HoldMs - now));
            if (State == PetState.Idle && Player.CurrentClip?.Name == "idle" && !AutomaticActionsPaused && !_inputBusy)
            {
                delay = Math.Min(delay, Math.Max(0, NextIdleActionAt - now));
                if (Settings.AutoSleep) delay = Math.Min(delay, Math.Max(0, SleepDeadline - now));
            }
            return delay;
        }
    }

    private void BeginReaction(ReactionKind reaction)
    {
        _reactionAfterIdleAction = ReactionKind.None;
        _reactionAfterWake = ReactionKind.None;
        SetState(PetState.React);
        var interactionId = CommitInteraction(BehaviorInteractionSource.Daily);
        if (reaction == ReactionKind.Meow && _canMeow)
        {
            ReactionPhase = ReactionPhase.Meow;
            Play("meow");
            CueSpeech(BehaviorSpeechScene.DailyClick, "喵", (int)Math.Min(int.MaxValue, _clips["meow"].DurationMs), interactionId);
            return;
        }
        // Single clicks choose meow/wink equally; old packs use whichever safe
        // clip exists, then rise if neither does. Keep the legacy double-click
        // fallback when its optional meow is missing.
        var wink = reaction == ReactionKind.Meow
            ? _canWink && _randomBetween(0, 1) == 1
            : _canWink && (!_canMeow || _randomBetween(0, 1) == 1);
        if (wink)
        {
            ReactionPhase = ReactionPhase.Wink;
            Play("wink");
        }
        else if (_canMeow)
        {
            ReactionPhase = ReactionPhase.Meow;
            Play("meow");
        }
        else
        {
            ReactionPhase = ReactionPhase.Rise;
            Play("rise");
        }
        CueSpeech(BehaviorSpeechScene.DailyClick, ReactionPhase == ReactionPhase.Meow ? "喵" : null,
            (int)Math.Min(int.MaxValue, Player.CurrentClip!.DurationMs), interactionId);
    }

    private void BeginHold()
    {
        ReactionPhase = ReactionPhase.Hold;
        _holdStartedAt = _clock.NowMilliseconds;
        Play("hold", loop: true);
    }

    private void EndReaction()
    {
        _reactionEndedAt = _clock.NowMilliseconds;
        ReactionCooldownUntil = _clock.NowMilliseconds + Settings.CooldownMs;
        EnterIdle();
    }

    private void EnterIdle()
    {
        CancelPendingInteraction();
        _reactionAfterIdleAction = ReactionKind.None;
        _reactionAfterWake = ReactionKind.None;
        ReactionPhase = ReactionPhase.None;
        SetState(PetState.Idle);
        Play("idle", loop: true);
        ScheduleBlink();
        if (WorkPhase == WorkPhase.WaitingForIdle) StartWorkAnnouncement();
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
        var duration = (int)Math.Clamp(Math.Min(forwardDuration, reverseDuration), 34, 240);
        // At least two slots retain both endpoints; at most 60 slots per second
        // avoid recreating the former 240/250Hz timeline in dynamic return paths.
        var count = Math.Min(route.Length, Math.Max(2, duration * 60 / 1000));
        var frames = Enumerable.Range(0, count).Select(i => new AnimationFrame(
            route[(int)Math.Round(i * (route.Length - 1d) / (count - 1))].File,
            (int)Math.Round((i + 1d) * duration / count) - (int)Math.Round(i * (double)duration / count))).ToArray();
        _expectedState = State;
        _expectedClip = name;
        _expectedToken = Player.Play(new AnimationClip(name, frames) { UniformFrameTiming = true });
        return true;
    }

    private void ScheduleBlink() => NextBlinkAt = _clock.NowMilliseconds + _randomBetween(Settings.BlinkMinMs, Settings.BlinkMaxMs);

    private void PlayNextIdleAction()
    {
        var choices = IdleActions;
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
        // Independent draws deliberately allow repetition, so the declared
        // 2:1:1:1 weights remain the actual per-draw probabilities.
        ReactionPhase = selected.Clip switch
        {
            "rise" => ReactionPhase.Rise,
            "firefly" => ReactionPhase.Firefly,
            _ => ReactionPhase.None
        };
        // Keep the catalog firefly identity for paired overlay playback/cache.
        if (selected.Clip == "firefly")
        {
            Play("firefly");
            StartFireflyFact();
        }
        else Play(selected.Clip, loop: false);
    }
}
