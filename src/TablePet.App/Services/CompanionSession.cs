using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Threading;
using TablePet.Core;

namespace TablePet.App.Services;

public sealed record CompanionHostContext(bool Visible, bool SessionAvailable, bool Dragging,
    bool WorkPaused, bool MenusOpen, bool CanShowMilestone, bool IsWorkMode, double PetHeightDip,
    bool AllowMilestone = true);

/// <summary>Adapts desktop facts to the independently testable companionship rules.</summary>
public sealed class CompanionSession : IDisposable
{
    private readonly IClock _clock;
    private readonly ICalendarClock _calendar;
    private readonly BehaviorController _behavior;
    private readonly Func<CompanionHostContext> _context;
    private readonly Dispatcher _dispatcher;
    private PetSettings _settings;
    private bool _disposed;
    private bool _manualWorkAnnouncement;
    private long _lastCheckpoint;
    private long _lastPresenceTick;
    private long? _breakWorkRoundId;
    private long? _breakFactId;
    private long? _currentWorkRoundId;

    public CompanionSession(IClock clock, BehaviorController behavior, PetSettings settings,
        Func<CompanionHostContext> context, string? statePath = null, ICalendarClock? calendar = null)
    {
        _clock = clock; _behavior = behavior; _settings = settings; _context = context;
        _calendar = calendar ?? new SystemCalendarClock();
        _dispatcher = Dispatcher.CurrentDispatcher;
        Store = new CompanionStateService(statePath);
        Store.Load();
        Store.Saved += OnSaved;
        Store.StatusChanged += OnStoreStatusChanged;
        Engine = null!; Speech = null!;
        InitializeState();
        _behavior.ExternalSpeechEnabled = true;
        _behavior.FactOccurred += HandleFact;
        if (_behavior.SpeechText is { } initialText)
            Speech.HandleCue(new CompanionSpeechCue("work_start", initialText,
                _behavior.SpeechDurationMs, _clock.NowMilliseconds, UserInitiated: false), SpeechContext());
    }

    public CompanionStateService Store { get; }
    public CompanionshipEngine Engine { get; private set; }
    public SpeechCoordinator Speech { get; private set; }
    public CompanionState State => Engine.State;
    public event Action? Changed;
    public event Action? PresentationChanged;

    private void InitializeState()
    {
        if (Engine is not null) Engine.StateChanged -= StateChanged;
        if (Speech is not null) Speech.StateChanged -= StateChanged;
        Engine = new CompanionshipEngine(Store.Current, _clock, _calendar);
        Speech = new SpeechCoordinator(Engine.State, _clock);
        Engine.SetContext(PresenceContext());
        Engine.SetEnabled(Store.CanWrite && _settings.CompanionshipEnabled);
        Engine.SetTextEnabled(_settings.CompanionTextEnabled);
        Speech.AcknowledgeSaved(Store.SavedRevision);
        Engine.StateChanged += StateChanged;
        Speech.StateChanged += StateChanged;
        _lastCheckpoint = _lastPresenceTick = _clock.NowMilliseconds;
        if (Store.CanWrite && State.StateRevision > Store.SavedRevision && State.StateRevision > 0) Save();
    }

    private CompanionPresenceContext PresenceContext()
    {
        var c = _context();
        return new CompanionPresenceContext
        {
            IsVisible = c.Visible, IsSessionAvailable = c.SessionAvailable,
            IsDragging = c.Dragging, IsWorkPaused = c.WorkPaused
        };
    }

    private CompanionSpeechContext SpeechContext()
    {
        var c = _context();
        var localDate = DateOnly.ParseExact(_calendar.GetCurrent().LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var quiet = Engine.LastActivationMilliseconds is { } last && _clock.NowMilliseconds - last >= 5000;
        return new CompanionSpeechContext(localDate,
            Enabled: Store.CanWrite && _settings.CompanionshipEnabled && _settings.CompanionTextEnabled,
            Visible: c.Visible && c.SessionAvailable,
            Paused: c.Dragging || c.WorkPaused,
            MenusOpen: c.MenusOpen,
            CanShowMilestone: c.CanShowMilestone && quiet,
            IsWorkMode: c.IsWorkMode, PetHeightDip: c.PetHeightDip, AllowMilestone: c.AllowMilestone);
    }

    public void SynchronizeContext()
    {
        if (_disposed) return;
        Engine.SetContext(PresenceContext());
        UpdatePresentation();
    }

    public void Tick()
    {
        if (_disposed) return;
        if (_clock.NowMilliseconds - _lastPresenceTick >= 1000)
        {
            _lastPresenceTick = _clock.NowMilliseconds;
            Engine.SetContext(PresenceContext());
            if (_clock.NowMilliseconds - _lastCheckpoint >= 30000 && State.StateRevision > Store.SavedRevision) Save();
        }
        UpdatePresentation();
    }

    public void UpdatePresentation()
    {
        if (!_disposed) Speech.Tick(SpeechContext());
    }

    public void Activate()
    {
        if (_disposed || !Store.CanWrite) return;
        Engine.SetContext(PresenceContext());
        Engine.Activate();
        UpdatePresentation();
    }

    public void PrepareManualWorkStart()
    {
        Activate();
        _manualWorkAnnouncement = _behavior.Mode != CompanionMode.Work;
    }

    public void ApplySettings(PetSettings settings)
    {
        var wasEnabled = Engine.Enabled;
        // Neither a visible cue nor a delayed save receipt belongs to the newly selected mode.
        if (_settings.Mode != settings.Mode) Speech.Clear();
        _settings = settings;
        Engine.SetEnabled(Store.CanWrite && settings.CompanionshipEnabled);
        if (!wasEnabled && Engine.Enabled && _currentWorkRoundId is { } round) Engine.WorkStarted(round);
        Engine.SetTextEnabled(settings.CompanionTextEnabled);
        if (settings.Mode != CompanionMode.Work) _manualWorkAnnouncement = false;
        UpdatePresentation();
        Changed?.Invoke();
    }

    public void HandleFact(BehaviorFact fact)
    {
        if (_disposed) return;
        Engine.SetContext(PresenceContext());
        switch (fact.Kind)
        {
            case BehaviorFactKind.InteractionCommitted:
                if (fact.FromNaturalSleep) Engine.GentleWakeCommitted(); else Engine.Activate();
                break;
            case BehaviorFactKind.WorkRoundStarted:
                _currentWorkRoundId = fact.Id;
                Engine.WorkStarted(fact.Id);
                break;
            case BehaviorFactKind.WorkRoundEnded when fact.RelatedId is { } workId:
                Engine.WorkEnded(workId, fact.Reason == WorkEndReason.DeadlineReached);
                _currentWorkRoundId = null;
                break;
            case BehaviorFactKind.BreakStarted when fact.RelatedId is { } parentId:
                _breakFactId = fact.Id; _breakWorkRoundId = parentId;
                Engine.BreakStarted(parentId);
                break;
            case BehaviorFactKind.BreakEnded when fact.RelatedId == _breakFactId && _breakWorkRoundId is { } parentId:
                Engine.BreakEnded(parentId, fact.Reason == WorkEndReason.DeadlineReached);
                _breakFactId = _breakWorkRoundId = null;
                break;
            case BehaviorFactKind.IdleActionStarted when fact.Name == "firefly":
                Engine.FireflyStarted(fact.Id);
                break;
            case BehaviorFactKind.IdleActionCompleted when fact.RelatedId is { } actionId:
                Engine.FireflyCompleted(actionId);
                break;
            case BehaviorFactKind.IdleActionInvalidated when fact.RelatedId is { } actionId:
                Engine.FireflyCompleted(actionId, completed: false);
                break;
            case BehaviorFactKind.SpeechCue:
                var scene = fact.Scene switch
                {
                    BehaviorSpeechScene.WorkStart => "work_start", BehaviorSpeechScene.WorkBreak => "work_break",
                    BehaviorSpeechScene.RestClick => "rest_click", BehaviorSpeechScene.WorkingClick => "work_busy",
                    _ => "daily_click"
                };
                var userInitiated = fact.Scene == BehaviorSpeechScene.WorkStart ? _manualWorkAnnouncement
                    : fact.Scene is BehaviorSpeechScene.DailyClick or BehaviorSpeechScene.RestClick or BehaviorSpeechScene.WorkingClick;
                Speech.HandleCue(new CompanionSpeechCue(scene, fact.DefaultText, fact.DefaultDurationMs,
                    fact.MonoAt, userInitiated), SpeechContext());
                if (fact.Scene == BehaviorSpeechScene.WorkStart) _manualWorkAnnouncement = false;
                PresentationChanged?.Invoke();
                break;
        }
    }

    public void MarkMemoriesRead(IEnumerable<string> ids) => Engine.MarkMemoriesRead(ids);

    private void StateChanged(bool critical)
    {
        if (critical) Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        if (!Store.CanWrite) return;
        _lastCheckpoint = _clock.NowMilliseconds;
        _ = Store.SaveAsync(State);
    }

    private void OnSaved(long revision) => Dispatch(() =>
    {
        Speech.AcknowledgeSaved(revision);
        UpdatePresentation();
        Changed?.Invoke();
        PresentationChanged?.Invoke();
    });

    private void OnStoreStatusChanged() => Dispatch(() =>
    {
        if (!Store.CanWrite) Engine.SetEnabled(false);
        if (Store.LastError is not null) Speech.AcknowledgeSaveFailure();
        Changed?.Invoke();
        PresentationChanged?.Invoke();
    });

    private void Dispatch(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }

    public async Task RetryAsync()
    {
        if (Store.CanWrite) { await Store.SaveAsync(State); return; }
        await Store.WaitForPendingWritesAsync();
        Store.Load();
        InitializeState();
        if (_currentWorkRoundId is { } round) Engine.WorkStarted(round);
        Activate();
        Changed?.Invoke();
    }

    public async Task StartNewRecordAsync()
    {
        await Store.WaitForPendingWritesAsync();
        Store.StartNewRecord();
        InitializeState();
        if (_currentWorkRoundId is { } round) Engine.WorkStarted(round);
        Activate();
        Changed?.Invoke();
    }

    public void Checkpoint()
    {
        Engine.Advance();
        if (State.StateRevision > Store.SavedRevision) Save();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _behavior.NotifyShutdown();
        Checkpoint();
        _disposed = true;
        _behavior.FactOccurred -= HandleFact;
        Store.Saved -= OnSaved;
        Store.StatusChanged -= OnStoreStatusChanged;
        Engine.StateChanged -= StateChanged;
        Speech.StateChanged -= StateChanged;
        Speech.Clear();
        Store.WaitForPendingWritesAsync().GetAwaiter().GetResult();
    }
}
