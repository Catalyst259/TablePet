namespace TablePet.Core;

public enum WorkPhase
{
    None, WaitingForIdle, WakingForWork, AnnouncingWork, Entering, Working,
    WaitingForBlink, Exiting, AnnouncingBreak, FallingAsleep, Resting,
    RestWake, RestYawn, WakeForNextWork, LeavingWake
}

public sealed partial class BehaviorController
{
    private bool _canWork;
    private bool _leaveWork;
    private bool _exitToDaily;
    private long _workPhaseStartedAt;
    private long _workStartedAt;
    private long? _breakStartedAt;
    private long? _workExitDueAt;
    private long? _workPausedAt;
    private string? _speechText;
    private long _speechStartedAt;
    private int _speechDurationMs;

    public CompanionMode Mode => Settings.Mode == CompanionMode.Work && CanWork ? CompanionMode.Work : CompanionMode.Daily;
    public bool CanWork => _canWork;
    public WorkPhase WorkPhase { get; private set; }
    private bool WorkSessionActive => WorkPhase != WorkPhase.None;
    private long WorkNow => _workPausedAt ?? (IsDragging ? _dragStartedAt : _clock.NowMilliseconds);
    private bool WorkFrozen => WorkSessionActive && (IsSuspended || IsDragging || (AutomaticActionsPaused && Mode == CompanionMode.Work));
    public long WorkRemainingMs => WorkPhase is WorkPhase.Working or WorkPhase.WaitingForBlink
        ? Math.Max(0, _workStartedAt + Settings.WorkDurationMs - WorkNow) : Settings.WorkDurationMs;
    public long BreakRemainingMs => _breakStartedAt is { } start
        ? Math.Max(0, start + Settings.BreakDurationMs - WorkNow) : Settings.BreakDurationMs;
    public string? SpeechText => _speechText is not null && SpeechElapsedMs < _speechDurationMs ? _speechText : null;
    public long SpeechElapsedMs => Math.Max(0, (WorkSessionActive ? WorkNow : IsDragging ? _dragStartedAt : _clock.NowMilliseconds) - _speechStartedAt);
    public int SpeechDurationMs => _speechDurationMs;
    public double WorkPropsOpacity => WorkPhase switch
    {
        WorkPhase.Entering => Ease((WorkNow - _workPhaseStartedAt) / (double)_clips["work_enter"].DurationMs),
        WorkPhase.Working or WorkPhase.WaitingForBlink => 1,
        WorkPhase.Exiting => 1 - Ease((WorkNow - _workPhaseStartedAt) / (double)_clips["work_exit"].DurationMs),
        _ => 0
    };
    public double WorkBodyOffsetY => WorkPhase switch
    {
        WorkPhase.Working or WorkPhase.WaitingForBlink => Breath(WorkNow - _workStartedAt),
        WorkPhase.Exiting => Breath(WorkNow - _workStartedAt) * WorkPropsOpacity,
        _ => 0
    };

    private static double Breath(long elapsed) => Math.Cos(2 * Math.PI * elapsed / 4800d) - 1;
    private static double Ease(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * t * (10 + t * (-15 + 6 * t));
    }

    private void ShowSpeech(string text, int durationMs = 2400)
    {
        _speechText = text;
        _speechStartedAt = WorkSessionActive ? WorkNow : _clock.NowMilliseconds;
        _speechDurationMs = durationMs;
    }

    private void InitializeWorkMode()
    {
        bool Valid(string name) => _clips.TryGetValue(name, out var clip) && clip.Name == name
            && clip.Frames.Count > 0 && clip.Frames.All(f => !string.IsNullOrWhiteSpace(f.File) && f.DurationMs > 0);
        var idle = _clips["idle"].Frames[0].File;
        _canWork = _canMeow && _canWink
            && new[] { "work_enter", "work_loop", "work_exit", "work_props", "yawn" }.All(Valid)
            && !_clips["work_enter"].Loop && _clips["work_loop"].Loop && !_clips["work_exit"].Loop
            && !_clips["yawn"].Loop && _clips["yawn"].Frames[0].File == idle && _clips["yawn"].Frames[^1].File == idle;
        if (Mode == CompanionMode.Work) RequestWorkStart();
    }

    private void ApplyWorkSettings(CompanionMode previousMode)
    {
        if (Mode == CompanionMode.Work)
        {
            _leaveWork = false;
            if (!WorkSessionActive) RequestWorkStart();
            else if (previousMode == CompanionMode.Daily && WorkPhase == WorkPhase.WaitingForBlink
                && WorkNow < _workStartedAt + Settings.WorkDurationMs)
            {
                _workExitDueAt = null;
                WorkPhase = WorkPhase.Working;
                // Returning before the visual exit may resume the same animation,
                // but the cancelled round must never later become a completed one.
                if (_workRoundEnded) StartWorkRoundFact();
            }
            return;
        }
        if (!WorkSessionActive) return;
        EndWorkRoundFact(WorkEndReason.UserCancelled);
        EndBreakFact(WorkEndReason.UserCancelled);
        _restInteractionId = null;
        _leaveWork = true;
        // An explicit return to daily mode may finish its transition even when
        // automatic actions are paused; daily pause never freezes manual input.
        SyncWorkPause();
        switch (WorkPhase)
        {
            case WorkPhase.WaitingForIdle:
                CancelPendingWork();
                break;
            case WorkPhase.Working:
            case WorkPhase.WaitingForBlink:
                RequestWorkExit();
                break;
            case WorkPhase.Resting:
                BeginWorkWake(WorkPhase.LeavingWake);
                break;
        }
    }

    private void RequestWorkStart()
    {
        _leaveWork = false;
        CancelPendingInteraction();
        _reactionAfterIdleAction = _reactionAfterWake = ReactionKind.None;
        if (State is PetState.Sleep or PetState.FallAsleep)
            BeginWorkWake(WorkPhase.WakingForWork);
        else if (State == PetState.Idle && Player.CurrentClip?.Name == "idle")
            StartWorkAnnouncement();
        else
        {
            WorkPhase = WorkPhase.WaitingForIdle;
            SyncWorkPause();
        }
    }

    private void StartWorkAnnouncement()
    {
        if (_leaveWork || Mode != CompanionMode.Work) { CompleteWorkToDaily(); return; }
        // A completion callback can run before TickWorkMode observes the deadline.
        // Settle the previous break before clearing its clock for the new round.
        if (_breakStartedAt is not null)
            EndBreakFact(BreakRemainingMs == 0 ? WorkEndReason.DeadlineReached : WorkEndReason.UserCancelled);
        _breakStartedAt = null;
        _workExitDueAt = null;
        _exitToDaily = false;
        WorkPlay(WorkPhase.AnnouncingWork, "meow", PetState.React, ReactionPhase.Meow);
        CueSpeech(BehaviorSpeechScene.WorkStart, "开始工作啦~", (int)Math.Min(int.MaxValue, _clips["meow"].DurationMs));
    }

    private void WorkPlay(WorkPhase phase, string name, PetState state = PetState.Work,
        ReactionPhase reaction = ReactionPhase.None, bool? loop = null)
    {
        WorkPhase = phase;
        _workPhaseStartedAt = WorkNow;
        ReactionPhase = reaction;
        SetState(state);
        Play(name, loop);
        if (WorkFrozen) Player.Pause();
        SyncWorkPause();
    }

    private void BeginWorkWake(WorkPhase phase)
    {
        var wasFalling = State == PetState.FallAsleep;
        WorkPhase = phase;
        _workPhaseStartedAt = WorkNow;
        ReactionPhase = ReactionPhase.None;
        SetState(PetState.Wake);
        if (wasFalling)
        {
            _expectedToken = Player.ReverseToStart("work_sleep_return");
            _expectedClip = Player.CurrentClip!.Name;
            _expectedState = State;
        }
        else if (!ReturnLoopToStart("work_sleep_to_wake")) Play("wake");
        if (WorkFrozen) Player.Pause();
        SyncWorkPause();
    }

    private bool TickWorkMode()
    {
        if (!WorkSessionActive) return false;
        if (WorkPhase == WorkPhase.WaitingForIdle) return false;
        var now = WorkNow;
        // The break deadline is a fact even when a click's wake/yawn is still
        // finishing; the animation may continue without extending this round.
        if (_breakStartedAt is not null && BreakRemainingMs == 0)
            EndBreakFact(WorkEndReason.DeadlineReached);
        if (WorkPhase == WorkPhase.Working && now >= _workStartedAt + Settings.WorkDurationMs)
        {
            EndWorkRoundFact(WorkEndReason.DeadlineReached);
            RequestWorkExit();
        }
        else if (WorkPhase == WorkPhase.WaitingForBlink && now >= _workExitDueAt)
            StartWorkExit();
        else if (WorkPhase == WorkPhase.Resting && BreakRemainingMs == 0)
            BeginWorkWake(_leaveWork ? WorkPhase.LeavingWake : WorkPhase.WakeForNextWork);
        return true;
    }

    private void RequestWorkExit()
    {
        if (WorkPhase == WorkPhase.WaitingForBlink) return;
        var elapsed = Math.Max(0, WorkNow - _workStartedAt) % _clips["work_loop"].DurationMs;
        foreach (var start in new long[] { 1250, 6700 })
        {
            if (elapsed >= start && elapsed < start + 375)
            {
                _workExitDueAt = WorkNow + start + 375 - elapsed;
                WorkPhase = WorkPhase.WaitingForBlink;
                return;
            }
        }
        StartWorkExit();
    }

    private void StartWorkExit()
    {
        _exitToDaily = _leaveWork;
        _workExitDueAt = null;
        WorkPlay(WorkPhase.Exiting, "work_exit");
    }

    private void InteractWithWork(ReactionKind reaction)
    {
        if (reaction == ReactionKind.None || WorkFrozen || _leaveWork) return;
        if (WorkPhase is WorkPhase.Resting or WorkPhase.FallingAsleep)
        {
            _restInteractionId = CommitInteraction(BehaviorInteractionSource.Rest);
            BeginWorkWake(WorkPhase.RestWake);
            return;
        }
        if (WorkPhase is WorkPhase.RestWake or WorkPhase.RestYawn or WorkPhase.FallingAsleep
            or WorkPhase.AnnouncingBreak or WorkPhase.WakeForNextWork or WorkPhase.LeavingWake) return;
        var interactionId = CommitInteraction(BehaviorInteractionSource.Working);
        CueSpeech(BehaviorSpeechScene.WorkingClick, "工作呢，别打扰我", 2400, interactionId);
    }

    private bool HandleWorkCompleted(AnimationCompleted completion)
    {
        if (!WorkSessionActive || WorkPhase == WorkPhase.WaitingForIdle) return false;
        switch (WorkPhase)
        {
            case WorkPhase.AnnouncingWork:
                if (_leaveWork) CompleteWorkToDaily();
                else WorkPlay(WorkPhase.Entering, "work_enter");
                break;
            case WorkPhase.Entering:
                _workStartedAt = WorkNow;
                WorkPlay(WorkPhase.Working, "work_loop", loop: true);
                StartWorkRoundFact();
                if (_leaveWork)
                {
                    EndWorkRoundFact(WorkEndReason.UserCancelled);
                    RequestWorkExit();
                }
                break;
            case WorkPhase.Exiting:
                if (_leaveWork) CompleteWorkToDaily();
                else if (_exitToDaily) StartWorkAnnouncement();
                else
                {
                    WorkPlay(WorkPhase.AnnouncingBreak, "wink", PetState.React, ReactionPhase.Wink);
                    CueSpeech(BehaviorSpeechScene.WorkBreak, "休息一下吧~", (int)Math.Min(int.MaxValue, _clips["wink"].DurationMs), _workRoundId);
                }
                break;
            case WorkPhase.AnnouncingBreak:
                if (_leaveWork) CompleteWorkToDaily();
                else WorkPlay(WorkPhase.FallingAsleep, "fall_asleep", PetState.FallAsleep);
                break;
            case WorkPhase.FallingAsleep:
                if (_leaveWork) BeginWorkWake(WorkPhase.LeavingWake);
                else if (_breakStartedAt is not null && BreakRemainingMs == 0) BeginWorkWake(WorkPhase.WakeForNextWork);
                else
                {
                    var firstSleep = _breakStartedAt is null;
                    _breakStartedAt ??= WorkNow;
                    WorkPlay(WorkPhase.Resting, "sleep_loop", PetState.Sleep, loop: true);
                    if (firstSleep) StartBreakFact();
                }
                break;
            case WorkPhase.WakingForWork:
            case WorkPhase.WakeForNextWork:
            case WorkPhase.LeavingWake:
            case WorkPhase.RestWake:
                if (completion.ClipName == "work_sleep_to_wake")
                {
                    Play("wake");
                    break;
                }
                if (_leaveWork) CompleteWorkToDaily();
                else if (WorkPhase == WorkPhase.RestWake)
                {
                    WorkPlay(WorkPhase.RestYawn, "yawn", PetState.React);
                    CueSpeech(BehaviorSpeechScene.RestClick, "和我一起休息", (int)Math.Min(int.MaxValue, _clips["yawn"].DurationMs), _restInteractionId);
                    _restInteractionId = null;
                }
                else StartWorkAnnouncement();
                break;
            case WorkPhase.RestYawn:
                if (_leaveWork) CompleteWorkToDaily();
                else if (BreakRemainingMs == 0) StartWorkAnnouncement();
                else WorkPlay(WorkPhase.FallingAsleep, "fall_asleep", PetState.FallAsleep);
                break;
        }
        return true;
    }

    private void CompleteWorkToDaily()
    {
        EndWorkRoundFact(WorkEndReason.UserCancelled);
        EndBreakFact(WorkEndReason.UserCancelled);
        _restInteractionId = null;
        if (_workPausedAt is not null)
        {
            ShiftWorkClocks(_clock.NowMilliseconds - _workPausedAt.Value);
            _workPausedAt = null;
        }
        WorkPhase = WorkPhase.None;
        _breakStartedAt = _workExitDueAt = null;
        _speechText = null;
        _leaveWork = false;
        LastPetInteraction = _sleepOrigin = _clock.NowMilliseconds;
        EnterIdle();
        if (IsSuspended || IsDragging) Player.Pause();
    }

    private void CancelPendingWork()
    {
        if (_workPausedAt is { } paused)
        {
            ShiftWorkClocks(_clock.NowMilliseconds - paused);
            _workPausedAt = null;
        }
        WorkPhase = WorkPhase.None;
        _leaveWork = false;
        if (!IsSuspended && !IsDragging) Player.Resume();
    }

    private void SyncWorkPause()
    {
        if (!WorkSessionActive) return;
        if (WorkFrozen)
        {
            _workPausedAt ??= IsDragging ? _dragStartedAt : _clock.NowMilliseconds;
            Player.Pause();
        }
        else if (_workPausedAt is { } start)
        {
            ShiftWorkClocks(_clock.NowMilliseconds - start);
            _workPausedAt = null;
            Player.Resume();
        }
    }

    private void ShiftWorkClocks(long delta)
    {
        _workPhaseStartedAt += delta;
        _workStartedAt += delta;
        _workFactStartedAt += delta;
        if (_breakStartedAt is { } start) _breakStartedAt = start + delta;
        if (_workExitDueAt is { } exit) _workExitDueAt = exit + delta;
        _speechStartedAt += delta;
        if (ReactionPhase == ReactionPhase.Hold) _holdStartedAt += delta;
    }

    private long WorkNextUpdate(long delay)
    {
        var now = WorkNow;
        if (WorkPhase == WorkPhase.Working) delay = Math.Min(delay, Math.Max(0, _workStartedAt + Settings.WorkDurationMs - now));
        if (WorkPhase == WorkPhase.WaitingForBlink && _workExitDueAt is { } exit) delay = Math.Min(delay, Math.Max(0, exit - now));
        if (WorkPhase == WorkPhase.Resting) delay = Math.Min(delay, BreakRemainingMs);
        if (SpeechText is not null) delay = Math.Min(delay, Math.Max(1, _speechDurationMs - SpeechElapsedMs));
        // Separate opacity/body layers also need refresh when a pack deduplicates frames.
        if (WorkPhase is WorkPhase.Entering or WorkPhase.Working or WorkPhase.WaitingForBlink or WorkPhase.Exiting)
            delay = Math.Min(delay, 16);
        return delay;
    }
}
