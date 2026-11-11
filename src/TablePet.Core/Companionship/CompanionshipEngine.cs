namespace TablePet.Core;

/// <summary>
/// Accounts for short, explicitly activated periods of visible companionship. It never controls animations.
/// Call on one application thread; state changes are published after each complete operation.
/// </summary>
public sealed class CompanionshipEngine
{
    private readonly IClock _clock;
    private readonly ICalendarClock _calendar;
    private long _previousMilliseconds;
    private CompanionCalendarTime _previousCalendar;
    private CompanionCalendarTime _nowCalendar;
    private long? _activeUntil;
    private CompanionPresenceContext _context = new();
    private bool _textEnabled = true;
    private bool _changed;
    private bool _critical;
    private long? _workRoundId;
    private long _workQualifiedMs;
    private long? _completedQualifiedWorkId;
    private long? _breakRoundId;
    private bool _breakFollowsQualifiedWork;
    private long _breakQualifiedMs;
    private long? _fireflyActionId;
    private bool _fireflyQualified;

    public CompanionState State { get; }
    public bool Enabled { get; private set; } = true;
    public bool HasActiveWindow => Enabled && _activeUntil.HasValue && _clock.NowMilliseconds < _activeUntil.Value;
    public long? LastActivationMilliseconds { get; private set; }
    public long CurrentWorkQualifiedMs => _workQualifiedMs;
    public long CurrentBreakQualifiedMs => _breakQualifiedMs;
    public CompanionCalendarTime CurrentCalendar => _nowCalendar;
    public CompanionPresenceContext Context => _context;

    /// <summary>True means a score, memory, or other durable event needs immediate persistence.</summary>
    public event Action<bool>? StateChanged;

    public CompanionshipEngine(CompanionState state, IClock clock, ICalendarClock calendar)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(calendar);
        if (!state.TryValidate(out var error)) throw new ArgumentException(error, nameof(state));
        State = state;
        _clock = clock;
        _calendar = calendar;
        _previousMilliseconds = clock.NowMilliseconds;
        _previousCalendar = _nowCalendar = calendar.GetCurrent();
    }

    public void Advance() => Change(AdvanceCore);

    public void SetContext(CompanionPresenceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Change(() =>
        {
            AdvanceCore();
            _context = context;
            if (!context.AllowsPresence) _fireflyQualified = false;
        });
    }

    public void Activate() => Change(() => { AdvanceCore(); ActivateCore(); });

    public void SetEnabled(bool enabled) => Change(() =>
    {
        AdvanceCore();
        if (Enabled == enabled) return;
        Enabled = enabled;
        ClearActivities();
        _activeUntil = null;
        LastActivationMilliseconds = null;
        if (!enabled)
        {
            State.ReturnBaselineRequired = true;
            Changed(true);
            ConsumePendingCore();
        }
    });

    public void SetTextEnabled(bool enabled) => Change(() =>
    {
        AdvanceCore();
        _textEnabled = enabled;
        if (!enabled) ConsumePendingCore();
    });

    public void WorkStarted(long roundId) => Change(() =>
    {
        AdvanceCore();
        if (!Enabled || _workRoundId == roundId) return;
        _workRoundId = roundId;
        _workQualifiedMs = 0;
        _completedQualifiedWorkId = null;
        _breakRoundId = null;
        _breakQualifiedMs = 0;
        _breakFollowsQualifiedWork = false;
    });

    public void WorkEnded(long roundId, bool deadlineReached) => Change(() =>
    {
        AdvanceCore();
        if (_workRoundId != roundId) return;
        if (Enabled && deadlineReached && _workQualifiedMs >= CompanionshipRules.WorkMemoryMs)
        {
            Unlock("first_work_completed");
            _completedQualifiedWorkId = roundId;
        }
        else _completedQualifiedWorkId = null;
        _workRoundId = null;
        _workQualifiedMs = 0;
    });

    public void BreakStarted(long roundId) => Change(() =>
    {
        AdvanceCore();
        if (!Enabled || _breakRoundId == roundId) return;
        _breakRoundId = roundId;
        _breakQualifiedMs = 0;
        _breakFollowsQualifiedWork = _completedQualifiedWorkId == roundId;
        _completedQualifiedWorkId = null;
    });

    public void BreakEnded(long roundId, bool deadlineReached) => Change(() =>
    {
        AdvanceCore();
        if (_breakRoundId != roundId) return;
        if (Enabled && deadlineReached && _breakFollowsQualifiedWork && _breakQualifiedMs >= CompanionshipRules.BreakMemoryMs)
            Unlock("first_break_completed");
        _breakRoundId = null;
        _breakQualifiedMs = 0;
        _breakFollowsQualifiedWork = false;
    });

    public void FireflyStarted(long actionId) => Change(() =>
    {
        AdvanceCore();
        if (_fireflyActionId == actionId) return;
        _fireflyActionId = actionId;
        _fireflyQualified = Enabled && _context.AllowsPresence && HasActiveWindow;
    });

    public void FireflyCompleted(long actionId, bool completed = true) => Change(() =>
    {
        AdvanceCore();
        if (_fireflyActionId != actionId) return;
        if (completed && _fireflyQualified && Enabled && _context.AllowsPresence
            && _activeUntil.HasValue && _clock.NowMilliseconds <= _activeUntil.Value)
            Unlock("first_firefly_visit");
        _fireflyActionId = null;
        _fireflyQualified = false;
    });

    /// <summary>The host calls only after an explicit click has actually begun responding from natural sleep.</summary>
    public void GentleWakeCommitted() => Change(() =>
    {
        AdvanceCore();
        ActivateCore();
        if (Enabled && _context.AllowsPresence) Unlock("first_gentle_wake");
    });

    public void MarkMemoriesRead(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        Change(() =>
        {
            AdvanceCore();
            foreach (var memory in State.Memories.Where(memory => wanted.Contains(memory.Id)))
            {
                if (!memory.IsRead) { memory.IsRead = true; Changed(true); }
                var milestone = CompanionshipRules.MilestoneForMemory(memory.Id);
                if (milestone is null) continue;
                if (State.ConsumedMilestoneIds.Add(milestone)) Changed(true);
                if (State.PendingMilestoneId == milestone) { State.PendingMilestoneId = null; Changed(true); }
            }
        });
    }

    public void DismissPendingMilestone() => Change(() => { AdvanceCore(); ConsumePendingCore(); });

    private void ActivateCore()
    {
        if (!Enabled) return;
        var now = _clock.NowMilliseconds;
        _activeUntil = now > long.MaxValue - CompanionshipRules.ActiveWindowMs ? long.MaxValue : now + CompanionshipRules.ActiveWindowMs;
        LastActivationMilliseconds = now;
        var date = _nowCalendar.LocalDate;
        var day = GetDay(date);
        if (State.RecordingStartedAtUtc is null)
        {
            State.RecordingStartedAtUtc = _nowCalendar.UtcNow.ToUniversalTime();
            State.RecordingStartedLocalDate = date;
            Changed(true);
        }
        if (State.ReturnBaselineRequired)
        {
            day.ReturnEligible = false;
            State.ReturnBaselineRequired = false;
            Changed(true);
        }
        else if (day.ReturnEligible is null)
        {
            day.ReturnEligible = CompanionState.TryDate(State.LastActiveLocalDate, out var previous)
                && CompanionState.TryDate(date, out var current) && current.DayNumber - previous.DayNumber >= 7;
            Changed(true);
        }
        if (State.LastActiveLocalDate != date)
        {
            State.LastActiveLocalDate = date;
            Changed(true);
        }
        CheckReturn(day, date);
    }

    private void AdvanceCore()
    {
        var now = _clock.NowMilliseconds;
        var calendar = _calendar.GetCurrent();
        _nowCalendar = calendar;
        var delta = now - _previousMilliseconds;
        var wallDelta = (calendar.UtcNow - _previousCalendar.UtcNow).TotalMilliseconds;
        var continuous = delta >= 0 && delta <= 5_000 && wallDelta >= 0
            && Math.Abs(wallDelta - delta) <= 1_000
            && calendar.TimeZone.Id == _previousCalendar.TimeZone.Id
            && calendar.LocalNow.Offset == _previousCalendar.LocalNow.Offset;
        if (!continuous)
        {
            // Unknown intervals are never converted into companionship. An unobserved firefly is not a witnessed one.
            _fireflyQualified = false;
            if (delta < 0) { _activeUntil = null; LastActivationMilliseconds = null; }
        }
        else if (delta > 0)
        {
            var eligible = Enabled && _context.AllowsPresence && _activeUntil.HasValue;
            var end = eligible ? Math.Min(now, _activeUntil!.Value) : _previousMilliseconds;
            var duration = Math.Max(0, end - _previousMilliseconds);
            if (duration > 0)
            {
                AddDailyInterval(duration, delta, wallDelta, calendar);
                if (!_context.IsWorkPaused)
                {
                    if (_workRoundId.HasValue) _workQualifiedMs = Math.Min(CompanionshipRules.WorkMemoryMs, _workQualifiedMs + duration);
                    if (_breakRoundId.HasValue) _breakQualifiedMs = Math.Min(CompanionshipRules.BreakMemoryMs, _breakQualifiedMs + duration);
                }
            }
            if (duration != delta) _fireflyQualified = false;
        }
        _previousMilliseconds = now;
        _previousCalendar = calendar;
    }

    private void AddDailyInterval(long duration, long delta, double wallDelta, CompanionCalendarTime calendar)
    {
        var before = _previousCalendar.LocalNow;
        var midnight = new DateTimeOffset(before.Date.AddDays(1), before.Offset).ToUniversalTime();
        var fractionToMidnight = wallDelta <= 0 ? double.PositiveInfinity
            : (midnight - _previousCalendar.UtcNow).TotalMilliseconds / wallDelta;
        var millisecondsToMidnight = double.IsPositiveInfinity(fractionToMidnight) ? long.MaxValue
            : (long)Math.Ceiling(fractionToMidnight * delta);
        if (millisecondsToMidnight >= 0 && millisecondsToMidnight < duration)
        {
            if (millisecondsToMidnight > 0) AddPresence(_previousCalendar.LocalDate, millisecondsToMidnight);
            AddPresence(calendar.LocalDate, duration - millisecondsToMidnight);
        }
        else AddPresence(_previousCalendar.LocalDate, duration);
    }

    private void AddPresence(string date, long milliseconds)
    {
        var day = GetDay(date);
        var updated = Math.Min(CompanionshipRules.DailyCapMs, day.QualifiedPresenceMs + milliseconds);
        if (updated == day.QualifiedPresenceMs) return;
        day.QualifiedPresenceMs = updated;
        Changed(false);
        var points = CompanionshipRules.PointsFor(updated);
        if (points != day.PointsGranted)
        {
            day.PointsGranted = points;
            Changed(true);
        }
        if (!day.CompanionDayGranted && updated >= CompanionshipRules.FirstDayMs)
        {
            day.CompanionDayGranted = true;
            Changed(true);
        }
        if (day.CompanionDayGranted)
        {
            Unlock("first_day", date);
            var days = State.CompanionDays;
            if (days >= 7) Unlock("together_7_days", date);
            if (days >= 30) Unlock("together_30_days", date);
            if (days >= 60) Unlock("together_60_days", date);
            var stage = CompanionshipRules.StageRank(CompanionshipRules.StageFor(State.TotalPoints, days));
            for (var i = 1; i <= stage; i++) Unlock("stage_" + CompanionshipRules.Stages[i].Id, date);
            CheckReturn(day, date);
        }
    }

    private CompanionDay GetDay(string date)
    {
        if (State.Days.TryGetValue(date, out var day)) return day;
        day = new CompanionDay();
        State.Days.Add(date, day);
        Changed(false);
        return day;
    }

    private void CheckReturn(CompanionDay day, string date)
    {
        if (day.ReturnEligible == true && day.CompanionDayGranted) Unlock("first_return", date);
    }

    private void Unlock(string id, string? localDate = null)
    {
        if (!Enabled || State.Memories.Any(memory => memory.Id == id)) return;
        State.Memories.Add(new CompanionMemory
        {
            Id = id, UnlockedAtUtc = _nowCalendar.UtcNow.ToUniversalTime(), LocalDate = localDate ?? _nowCalendar.LocalDate,
            Sequence = State.Memories.Count == 0 ? 1 : checked(State.Memories.Max(memory => memory.Sequence) + 1)
        });
        Changed(true);
        var milestone = CompanionshipRules.MilestoneForMemory(id);
        if (milestone is null || State.ConsumedMilestoneIds.Contains(milestone)) return;
        if (!_textEnabled)
        {
            State.ConsumedMilestoneIds.Add(milestone);
            return;
        }
        if (State.PendingMilestoneId is null || CompanionshipRules.MilestoneRank(milestone) > CompanionshipRules.MilestoneRank(State.PendingMilestoneId))
        {
            if (State.PendingMilestoneId is not null) State.ConsumedMilestoneIds.Add(State.PendingMilestoneId);
            State.PendingMilestoneId = milestone;
        }
        else State.ConsumedMilestoneIds.Add(milestone);
    }

    private void ConsumePendingCore()
    {
        if (State.PendingMilestoneId is null) return;
        State.ConsumedMilestoneIds.Add(State.PendingMilestoneId);
        State.PendingMilestoneId = null;
        Changed(true);
    }

    private void ClearActivities()
    {
        _workRoundId = _completedQualifiedWorkId = _breakRoundId = _fireflyActionId = null;
        _workQualifiedMs = _breakQualifiedMs = 0;
        _breakFollowsQualifiedWork = _fireflyQualified = false;
    }

    private void Changed(bool critical)
    {
        _changed = true;
        _critical |= critical;
    }

    private void Change(Action action)
    {
        _changed = _critical = false;
        action();
        if (!_changed) return;
        State.Touch();
        StateChanged?.Invoke(_critical);
    }
}
