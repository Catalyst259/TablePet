namespace TablePet.Core;

public enum BehaviorFactKind
{
    InteractionCommitted, WorkRoundStarted, WorkRoundEnded, BreakStarted, BreakEnded,
    IdleActionStarted, IdleActionCompleted, IdleActionInvalidated, SpeechCue
}

public enum BehaviorInteractionSource { None, Daily, Rest, Working }
public enum BehaviorSpeechScene { DailyClick, WorkStart, WorkBreak, RestClick, WorkingClick }
public enum WorkEndReason { DeadlineReached, UserCancelled, Shutdown }

/// <summary>
/// A semantic fact emitted on the UI thread. Id is unique within this controller;
/// RelatedId links an end to its start, a break start to its work round, or speech to an interaction.
/// ElapsedMs excludes behavior pauses but does not imply the user was present or productive.
/// </summary>
public sealed record BehaviorFact(BehaviorFactKind Kind, long Id, long MonoAt)
{
    public long? RelatedId { get; init; }
    public BehaviorInteractionSource Source { get; init; }
    public bool FromNaturalSleep { get; init; }
    public BehaviorSpeechScene? Scene { get; init; }
    public string? DefaultText { get; init; }
    public int DefaultDurationMs { get; init; }
    public WorkEndReason? Reason { get; init; }
    public long ElapsedMs { get; init; }
    public int PlannedDurationMs { get; init; }
    public bool DurationChanged { get; init; }
    public string? Name { get; init; }
}

public sealed partial class BehaviorController
{
    private long _nextFactId;
    private long? _pendingInteractionId;
    private bool _pendingInteractionFromSleep;
    private long? _restInteractionId;
    private long? _fireflyFactId;
    private long? _workRoundId;
    private long? _breakId;
    private bool _workRoundEnded;
    private bool _breakEnded;
    private long _workFactStartedAt;
    private int _workPlannedDurationMs;
    private int _breakPlannedDurationMs;
    private bool _workDurationChanged;
    private bool _breakDurationChanged;
    private bool _factsShutdown;

    public event Action<BehaviorFact>? FactOccurred;

    /// <summary>When true, the host owns speech presentation through SpeechCue facts.</summary>
    public bool ExternalSpeechEnabled { get; set; }

    private BehaviorFact Fact(BehaviorFactKind kind) => new(kind, ++_nextFactId, _clock.NowMilliseconds);
    private void Publish(BehaviorFact fact)
    {
        if (!_factsShutdown) FactOccurred?.Invoke(fact);
    }

    private void RememberInteraction(bool fromNaturalSleep = false)
    {
        if (_pendingInteractionId is not null) return;
        _pendingInteractionId = ++_nextFactId;
        _pendingInteractionFromSleep = fromNaturalSleep;
    }

    private void CancelPendingInteraction()
    {
        _pendingInteractionId = null;
        _pendingInteractionFromSleep = false;
    }

    private long CommitInteraction(BehaviorInteractionSource source)
    {
        var id = source == BehaviorInteractionSource.Daily ? _pendingInteractionId ?? ++_nextFactId : ++_nextFactId;
        var fromSleep = source == BehaviorInteractionSource.Daily && _pendingInteractionFromSleep;
        if (source == BehaviorInteractionSource.Daily) CancelPendingInteraction();
        Publish(new(BehaviorFactKind.InteractionCommitted, id, _clock.NowMilliseconds)
        {
            Source = source, FromNaturalSleep = fromSleep
        });
        return id;
    }

    private void CueSpeech(BehaviorSpeechScene scene, string? text, int durationMs, long? relatedId = null)
    {
        // A resting response interrupted by hide/lock must not become a fresh external cue on resume.
        if (scene != BehaviorSpeechScene.RestClick || relatedId is not null)
            Publish(Fact(BehaviorFactKind.SpeechCue) with
            {
                Scene = scene, DefaultText = text, DefaultDurationMs = durationMs, RelatedId = relatedId,
                Source = scene switch
                {
                    BehaviorSpeechScene.DailyClick => BehaviorInteractionSource.Daily,
                    BehaviorSpeechScene.RestClick => BehaviorInteractionSource.Rest,
                    BehaviorSpeechScene.WorkingClick => BehaviorInteractionSource.Working,
                    _ => BehaviorInteractionSource.None
                }
            });
        if (!ExternalSpeechEnabled && text is not null) ShowSpeech(text, durationMs);
    }

    private void StartFireflyFact()
    {
        var fact = Fact(BehaviorFactKind.IdleActionStarted) with { Name = "firefly" };
        _fireflyFactId = fact.Id;
        Publish(fact);
    }

    private void FinishFireflyFact(bool invalidated)
    {
        if (_fireflyFactId is not { } id) return;
        _fireflyFactId = null;
        Publish(Fact(invalidated ? BehaviorFactKind.IdleActionInvalidated : BehaviorFactKind.IdleActionCompleted)
            with { RelatedId = id, Name = "firefly" });
    }

    private void StartWorkRoundFact()
    {
        _workFactStartedAt = WorkNow;
        _workPlannedDurationMs = Settings.WorkDurationMs;
        _workDurationChanged = false;
        _workRoundEnded = false;
        var fact = Fact(BehaviorFactKind.WorkRoundStarted) with { PlannedDurationMs = _workPlannedDurationMs };
        _workRoundId = fact.Id;
        Publish(fact);
    }

    private void EndWorkRoundFact(WorkEndReason reason)
    {
        if (_workRoundId is not { } id || _workRoundEnded) return;
        _workRoundEnded = true;
        Publish(Fact(BehaviorFactKind.WorkRoundEnded) with
        {
            RelatedId = id, Reason = reason, ElapsedMs = Math.Max(0, WorkNow - _workFactStartedAt),
            PlannedDurationMs = _workPlannedDurationMs, DurationChanged = _workDurationChanged
        });
    }

    private void StartBreakFact()
    {
        _breakEnded = false;
        _breakDurationChanged = false;
        _breakPlannedDurationMs = Settings.BreakDurationMs;
        var fact = Fact(BehaviorFactKind.BreakStarted) with
        {
            RelatedId = _workRoundId, PlannedDurationMs = _breakPlannedDurationMs
        };
        _breakId = fact.Id;
        Publish(fact);
    }

    private void EndBreakFact(WorkEndReason reason)
    {
        if (_breakId is not { } id || _breakEnded) return;
        _breakEnded = true;
        Publish(Fact(BehaviorFactKind.BreakEnded) with
        {
            RelatedId = id, Reason = reason, ElapsedMs = Math.Max(0, WorkNow - (_breakStartedAt ?? WorkNow)),
            PlannedDurationMs = _breakPlannedDurationMs, DurationChanged = _breakDurationChanged
        });
    }

    /// <summary>Call before disposing the host; does not change animation or persisted mode.</summary>
    public void NotifyShutdown()
    {
        if (_factsShutdown) return;
        EndWorkRoundFact(WorkEndReason.Shutdown);
        EndBreakFact(WorkEndReason.Shutdown);
        FinishFireflyFact(invalidated: true);
        CancelPendingInteraction();
        _restInteractionId = null;
        _factsShutdown = true;
    }
}
