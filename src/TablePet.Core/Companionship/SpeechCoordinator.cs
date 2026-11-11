using System.Globalization;

namespace TablePet.Core;

/// <param name="CueAtMs">The monotonic time the behavior reaches its speech cue, not the original mouse request.</param>
public sealed record CompanionSpeechCue(string Scene, string? DefaultText, int DefaultDurationMs,
    long CueAtMs, bool UserInitiated = true);

/// <param name="CanShowMilestone">The host has verified idle pose, no pending interaction, and five seconds since the last operation.</param>
public sealed record CompanionSpeechContext(DateOnly LocalDate, bool Enabled = true, bool Visible = true,
    bool Paused = false, bool MenusOpen = false, bool CanShowMilestone = false,
    bool IsWorkMode = false, double PetHeightDip = 240, bool AllowMilestone = true);

/// <summary>
/// Owns one visible bubble and at most one pending cue. One-time copy is consumed before its
/// snapshot is saved; an old save receipt can never authorize a new greeting or celebration.
/// </summary>
public sealed class SpeechCoordinator
{
    public const int PendingCueLifetimeMs = 2000;
    private readonly CompanionState _state;
    private readonly IClock _clock;
    private readonly CompanionDialogue _dialogue;
    private PendingSpeech? _pending;
    private ActiveSpeech? _active;
    private long _savedRevision = -1;
    private long _lastTickMs;
    private bool _wasPaused;

    public SpeechCoordinator(CompanionState state, IClock clock, CompanionContent? content = null,
        Func<int, int>? next = null)
    {
        _state = state;
        _clock = clock;
        _lastTickMs = clock.NowMilliseconds;
        _dialogue = new(content, next);
    }

    public event Action<bool>? StateChanged;
    public string? SpeechText => _active is { } active && SpeechElapsedMs < active.DurationMs ? active.Text : null;
    public int SpeechDurationMs => _active?.DurationMs ?? 0;
    public long SpeechElapsedMs => _active is { } active
        ? active.ElapsedMs + (_wasPaused ? 0 : Math.Max(0, _clock.NowMilliseconds - _lastTickMs)) : 0;
    public bool HasPendingWork => _pending is not null || SpeechText is not null;
    public long SavedRevision => _savedRevision;

    /// <summary>Receipts only advance durability knowledge; they never replace mutable application state.</summary>
    public void AcknowledgeSaved(long savedRevision) => _savedRevision = Math.Max(_savedRevision, savedRevision);

    /// <summary>Fall back on the next tick without showing unpersisted one-time content.</summary>
    public void AcknowledgeSaveFailure()
    {
        if (_pending is { RequiredRevision: > 0 } pending && pending.RequiredRevision > _savedRevision)
            pending.SaveFailed = true;
    }

    public void HandleCue(CompanionSpeechCue cue, CompanionSpeechContext context)
    {
        var now = _clock.NowMilliseconds;
        Advance(context, now);
        if (!context.Visible || now - cue.CueAtMs > PendingCueLifetimeMs) return;
        var priority = Priority(cue.Scene);
        if (_pending is { } oldPending && oldPending.Priority > priority) return;
        // A fresh operation cancels a lower-priority celebration even if that operation has no text.
        if (_active is { } oldActive && oldActive.Priority <= priority) _active = null;
        var choice = context.Enabled
            ? _dialogue.Select(_state, GetDay(context.LocalDate), cue.Scene, cue.UserInitiated, context.AllowMilestone)
            : new CompanionDialogueChoice(null, CompanionDialogueKind.Base);
        Queue(cue, context, choice, priority, automatic: false);
        Pump(context, now);
    }

    public void Tick(CompanionSpeechContext context)
    {
        var now = _clock.NowMilliseconds;
        Advance(context, now);
        Pump(context, now);
        if (_pending is not null || _active is not null || !CanShowAutomatic(context)) return;
        if (_state.PendingMilestoneId is not { } milestoneId) return;
        var day = GetDay(context.LocalDate);
        if (day.AutomaticNoticeUsed) return;
        var choice = _dialogue.SelectMilestone(_state, milestoneId);
        if (choice.Content is null) return;
        Queue(new("milestone", null, 0, now), context, choice, 1, automatic: true);
        Pump(context, now);
    }

    /// <summary>For hiding, locking, shutdown or replacing the bound state. Consumed reminders never replay.</summary>
    public void Clear()
    {
        _pending = null;
        _active = null;
        _lastTickMs = _clock.NowMilliseconds;
        _wasPaused = false;
    }

    private void Queue(CompanionSpeechCue cue, CompanionSpeechContext context, CompanionDialogueChoice choice,
        int priority, bool automatic)
    {
        var pending = new PendingSpeech(cue, choice, context.LocalDate, priority, automatic);
        _pending = pending;
        if (choice.RequiresSavedConsumption)
        {
            pending.RequiredRevision = _dialogue.ConsumeBeforeSave(_state, GetDay(context.LocalDate), choice);
            StateChanged?.Invoke(true);
        }
    }

    private void Advance(CompanionSpeechContext context, long now)
    {
        if (_active is { } active && !_wasPaused) active.ElapsedMs += Math.Max(0, now - _lastTickMs);
        _lastTickMs = now;
        _wasPaused = context.Paused;
        if (_active is { } expired && expired.ElapsedMs >= expired.DurationMs) _active = null;
        if (!context.Visible)
        {
            _active = null;
            _pending = null;
            return;
        }
        if (!context.Enabled)
        {
            if (_active?.IsExtra == true) _active = null;
            if (_pending?.Choice.Content is not null) _pending = null;
            SuppressUnshownMilestone();
        }
        if (_pending?.Choice.MilestoneId is { } id &&
            (CompanionDialogue.IsMilestoneRead(_state, id) ||
             _state.PendingMilestoneId is { } replacement && replacement != id))
            _pending = null;
    }

    private void Pump(CompanionSpeechContext context, long now)
    {
        if (_pending is not { } pending) return;
        if (!context.Visible || pending.LocalDate != context.LocalDate)
        {
            _pending = null;
            return;
        }
        if (now - pending.Cue.CueAtMs > PendingCueLifetimeMs || pending.SaveFailed)
        {
            _pending = null;
            // Expired daily requests stay silent. A timely save failure can use a regular response.
            if (pending.Choice.RequiresSavedConsumption && CanStart(context, pending))
                StartBase(pending, context, now);
            return;
        }
        if (!CanStart(context, pending)) return;
        if (pending.Choice.Kind == CompanionDialogueKind.Milestone && !context.AllowMilestone)
        {
            _pending = null;
            StartBase(pending, context, now);
            return;
        }
        if (pending.Automatic && !CanShowAutomatic(context))
        {
            _pending = null;
            return;
        }
        if (pending.RequiredRevision > _savedRevision) return;
        _pending = null;
        if (pending.Choice.Content is { } content)
        {
            _active = new(content.ForHeight(context.PetHeightDip), content.DurationMs(context.PetHeightDip),
                pending.Priority, true);
            _dialogue.CommitDisplayed(_state, pending.Choice);
            StateChanged?.Invoke(false);
        }
        else StartBase(pending, context, now);
    }

    private bool CanStart(CompanionSpeechContext context, PendingSpeech pending) =>
        context.Visible && !context.Paused && !context.MenusOpen &&
        (_active is null || _active.Priority <= pending.Priority);

    private static bool CanShowAutomatic(CompanionSpeechContext context) =>
        context.Enabled && context.AllowMilestone && context.Visible && !context.Paused && !context.MenusOpen &&
        !context.IsWorkMode && context.CanShowMilestone;

    private void StartBase(PendingSpeech pending, CompanionSpeechContext context, long now)
    {
        var elapsed = Math.Max(0, now - pending.Cue.CueAtMs);
        if (pending.Cue.Scene == "daily_click")
        {
            // The old one-character meow is animation metadata, never a displayed fallback.
            if (!context.Enabled || elapsed > PendingCueLifetimeMs) return;
            var choice = _dialogue.SelectDailyResponse(_state);
            var content = choice.Content!;
            _active = new(content.ForHeight(context.PetHeightDip), content.DurationMs(context.PetHeightDip),
                pending.Priority, true);
            _dialogue.CommitDisplayed(_state, choice);
            StateChanged?.Invoke(false);
            return;
        }
        if (string.IsNullOrEmpty(pending.Cue.DefaultText) || elapsed >= pending.Cue.DefaultDurationMs) return;
        _active = new(pending.Cue.DefaultText, pending.Cue.DefaultDurationMs, pending.Priority, false)
        {
            ElapsedMs = elapsed
        };
    }

    private CompanionDay GetDay(DateOnly date)
    {
        var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_state.Days.TryGetValue(key, out var day)) return day;
        _state.Days[key] = day = new CompanionDay();
        _state.Touch();
        StateChanged?.Invoke(false);
        return day;
    }

    private void SuppressUnshownMilestone()
    {
        if (_state.PendingMilestoneId is not { } id) return;
        _state.ConsumedMilestoneIds.Add(id);
        _state.PendingMilestoneId = null;
        _state.Touch();
        StateChanged?.Invoke(true);
    }

    private static int Priority(string scene) => scene is "work_start" or "work_break" ? 3 : 2;

    private sealed class PendingSpeech(CompanionSpeechCue cue, CompanionDialogueChoice choice,
        DateOnly localDate, int priority, bool automatic)
    {
        public CompanionSpeechCue Cue { get; } = cue;
        public CompanionDialogueChoice Choice { get; } = choice;
        public DateOnly LocalDate { get; } = localDate;
        public int Priority { get; } = priority;
        public bool Automatic { get; } = automatic;
        public long RequiredRevision { get; set; } = -1;
        public bool SaveFailed { get; set; }
    }

    private sealed class ActiveSpeech(string text, int durationMs, int priority, bool isExtra)
    {
        public string Text { get; } = text;
        public int DurationMs { get; } = durationMs;
        public int Priority { get; } = priority;
        public bool IsExtra { get; } = isExtra;
        public long ElapsedMs { get; set; }
    }
}
