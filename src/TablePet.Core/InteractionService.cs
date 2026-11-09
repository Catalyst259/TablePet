namespace TablePet.Core;

[Flags]
public enum InteractionSignal { None = 0, Click = 1, Touch = Click, BeginDrag = 2, EndDrag = 4, Hover = 8, DoubleClick = 16 }

/// <summary>Uses physical cursor movement, not visual-tree enter events or animation hit-mask changes.</summary>
public sealed class InteractionService
{
    private readonly IClock _clock;
    private readonly double _dragThresholdX;
    private readonly double _dragThresholdY;
    private int _doubleClickTimeMs;
    private double _doubleClickRadiusX;
    private double _doubleClickRadiusY;
    private PetSettings _settings;
    private bool _hasBaseline;
    private double _x;
    private double _y;
    private bool _inside;
    private bool _armed = true;
    private long? _enteredAt;
    private long? _leftAt;
    private bool _pressed;
    private double _downX;
    private double _downY;
    private long _downAt;
    private long? _pendingClickAt;
    private double _pendingClickX;
    private double _pendingClickY;
    private bool _secondClickCandidate;
    private bool _pressCancelled;

    public InteractionService(IClock clock, PetSettings settings, double dragThresholdX = 4, double dragThresholdY = 4,
        int doubleClickTimeMs = 500, double doubleClickWidth = 4, double doubleClickHeight = 4)
    {
        settings.EnsureValid();
        _clock = clock;
        _settings = settings;
        _dragThresholdX = Math.Max(1, dragThresholdX);
        _dragThresholdY = Math.Max(1, dragThresholdY);
        ConfigureDoubleClick(doubleClickTimeMs, doubleClickWidth, doubleClickHeight);
    }

    public bool IsDragging { get; private set; }
    public bool IsPressed => _pressed;
    public bool HasPendingClick => _pendingClickAt is not null;

    /// <summary>Refresh the system interval and full rectangle size in the pointer's coordinate space.</summary>
    public void ConfigureDoubleClick(int doubleClickTimeMs, double doubleClickWidth, double doubleClickHeight)
    {
        var time = Math.Max(1, doubleClickTimeMs);
        var radiusX = Math.Max(1, doubleClickWidth) / 2;
        var radiusY = Math.Max(1, doubleClickHeight) / 2;
        if (_doubleClickTimeMs == time && _doubleClickRadiusX == radiusX && _doubleClickRadiusY == radiusY) return;
        _doubleClickTimeMs = time;
        _doubleClickRadiusX = radiusX;
        _doubleClickRadiusY = radiusY;
        ClearPendingClick();
        _pressCancelled |= _pressed;
    }

    public long NextUpdateInMs
    {
        get
        {
            if (IsDragging || _pressed) return long.MaxValue;
            var delay = long.MaxValue;
            if (_pendingClickAt is { } clicked) delay = Math.Max(0, clicked + _doubleClickTimeMs - _clock.NowMilliseconds);
            if (_leftAt is { } left) delay = Math.Min(delay, Math.Max(0, left + 40 - _clock.NowMilliseconds));
            if (_enteredAt is { } entered) delay = Math.Min(delay, Math.Max(0, entered + _settings.HoverDwellMs - _clock.NowMilliseconds));
            return delay;
        }
    }

    public void ResetBaseline(double x, double y, bool hit)
    {
        _hasBaseline = true;
        _x = x;
        _y = y;
        _inside = hit;
        _armed = !hit;
        _enteredAt = _leftAt = null;
    }

    public void ApplySettings(PetSettings settings, double x, double y, bool hit)
    {
        settings.EnsureValid();
        _settings = settings;
        ClearPendingClick();
        _pressCancelled |= _pressed;
        ResetBaseline(x, y, hit);
    }

    public InteractionSignal UpdatePointer(double x, double y, bool hit)
    {
        if (!_hasBaseline)
        {
            ResetBaseline(x, y, hit);
            return InteractionSignal.None;
        }
        if (x == _x && y == _y)
        {
            // A changing silhouette may cancel an incomplete dwell, but cannot create an entry
            // or rearm a completed hover while the physical pointer stays still.
            if (!hit) _enteredAt = null;
            return InteractionSignal.None;
        }
        // An already-observed stable departure can rearm before this new movement enters.
        RearmAfterDeparture();
        _x = x;
        _y = y;
        if (_pressed)
        {
            if (!IsDragging && (Math.Abs(x - _downX) >= _dragThresholdX || Math.Abs(y - _downY) >= _dragThresholdY))
            {
                IsDragging = true;
                ClearPendingClick();
                _enteredAt = _leftAt = null;
                _armed = false;
                return InteractionSignal.BeginDrag;
            }
            return InteractionSignal.None;
        }
        if (hit != _inside)
        {
            _inside = hit;
            if (hit)
            {
                _leftAt = null;
                _enteredAt = _armed ? _clock.NowMilliseconds : null;
            }
            else
            {
                _enteredAt = null;
                _leftAt = _clock.NowMilliseconds;
            }
        }
        return InteractionSignal.None;
    }

    public InteractionSignal Tick()
    {
        if (IsDragging || _pressed) return InteractionSignal.None;
        RearmAfterDeparture();
        var signal = InteractionSignal.None;
        if (_pendingClickAt is { } clicked && _clock.NowMilliseconds - clicked >= _doubleClickTimeMs)
        {
            ClearPendingClick();
            signal = InteractionSignal.Click;
        }
        if (_enteredAt is { } entered && _inside && _armed && _clock.NowMilliseconds - entered >= _settings.HoverDwellMs)
        {
            _enteredAt = null;
            _armed = false;
            signal |= InteractionSignal.Hover;
        }
        return signal;
    }

    /// <summary>Suppressed reaction input still tracks the press so dragging remains available.</summary>
    public InteractionSignal PointerDown(double x, double y, bool hit, bool allowClick = true)
    {
        UpdatePointer(x, y, hit);
        if (!hit || IsDragging) return InteractionSignal.None;
        var signal = InteractionSignal.None;
        if (!allowClick) ClearPendingClick();
        _secondClickCandidate = _pendingClickAt is { } clicked
            && _clock.NowMilliseconds - clicked < _doubleClickTimeMs
            && Math.Abs(x - _pendingClickX) <= _doubleClickRadiusX
            && Math.Abs(y - _pendingClickY) <= _doubleClickRadiusY;
        if (_pendingClickAt is not null && !_secondClickCandidate)
        {
            // A new press outside the system interval/rectangle resolves the earlier
            // single click even when a delayed timer has not dispatched it yet.
            ClearPendingClick();
            signal = InteractionSignal.Click;
        }
        _pressed = true;
        _pressCancelled = !allowClick;
        _downX = x;
        _downY = y;
        _downAt = _clock.NowMilliseconds;
        _enteredAt = null;
        return signal;
    }

    public InteractionSignal PointerUp(double x, double y, bool hit, bool allowWake = false)
    {
        var wasPressed = _pressed;
        var dragged = IsDragging || (wasPressed && (Math.Abs(x - _downX) >= _dragThresholdX || Math.Abs(y - _downY) >= _dragThresholdY));
        var hadDrag = IsDragging;
        var cancelled = _pressCancelled;
        _pressed = false;
        _pressCancelled = false;
        IsDragging = false;
        ResetBaseline(x, y, hit);
        if (dragged || cancelled || (wasPressed && !hit))
        {
            ClearPendingClick();
            return hadDrag ? InteractionSignal.EndDrag : InteractionSignal.None;
        }
        if (!wasPressed) return InteractionSignal.None;
        // Hover and click have independent meanings; persisted legacy TriggerMode and
        // the old allowWake hint no longer gate a valid explicit click.
        if (_secondClickCandidate)
        {
            ClearPendingClick();
            return InteractionSignal.DoubleClick;
        }
        _pendingClickAt = _downAt;
        _pendingClickX = _downX;
        _pendingClickY = _downY;
        // Match Windows' down-to-down interval. A long first press has already
        // exhausted the interval, but a valid second press may finish after it.
        if (_clock.NowMilliseconds - _downAt < _doubleClickTimeMs) return InteractionSignal.None;
        ClearPendingClick();
        return InteractionSignal.Click;
    }

    /// <summary>Capture-loss follows the same drag-release path; pending click/hover candidates are discarded.</summary>
    public InteractionSignal Cancel()
    {
        var signal = IsDragging ? InteractionSignal.EndDrag : InteractionSignal.None;
        IsDragging = false;
        _pressed = false;
        _pressCancelled = false;
        ClearPendingClick();
        _enteredAt = _leftAt = null;
        _armed = false;
        return signal;
    }

    private void ClearPendingClick()
    {
        _pendingClickAt = null;
        _secondClickCandidate = false;
    }

    private void RearmAfterDeparture()
    {
        if (!_inside && _leftAt is { } left && _clock.NowMilliseconds - left >= 40)
        {
            _armed = true;
            _leftAt = null;
        }
    }
}
