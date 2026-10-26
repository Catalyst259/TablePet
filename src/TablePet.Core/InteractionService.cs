namespace TablePet.Core;

[Flags]
public enum InteractionSignal { None = 0, Click = 1, Touch = Click, BeginDrag = 2, EndDrag = 4, Hover = 8 }

/// <summary>Uses physical cursor movement, not visual-tree enter events or animation hit-mask changes.</summary>
public sealed class InteractionService
{
    private readonly IClock _clock;
    private readonly double _dragThresholdX;
    private readonly double _dragThresholdY;
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

    public InteractionService(IClock clock, PetSettings settings, double dragThresholdX = 4, double dragThresholdY = 4)
    {
        settings.EnsureValid();
        _clock = clock;
        _settings = settings;
        _dragThresholdX = Math.Max(1, dragThresholdX);
        _dragThresholdY = Math.Max(1, dragThresholdY);
    }

    public bool IsDragging { get; private set; }
    public bool IsPressed => _pressed;
    public long NextUpdateInMs
    {
        get
        {
            if (IsDragging || _pressed) return long.MaxValue;
            if (_leftAt is { } left) return Math.Max(0, left + 40 - _clock.NowMilliseconds);
            if (_enteredAt is { } entered) return Math.Max(0, entered + _settings.HoverDwellMs - _clock.NowMilliseconds);
            return long.MaxValue;
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
        if (_enteredAt is { } entered && _inside && _armed && _clock.NowMilliseconds - entered >= _settings.HoverDwellMs)
        {
            _enteredAt = null;
            _armed = false;
            return InteractionSignal.Hover;
        }
        return InteractionSignal.None;
    }

    public InteractionSignal PointerDown(double x, double y, bool hit)
    {
        UpdatePointer(x, y, hit);
        if (!hit || IsDragging) return InteractionSignal.None;
        _pressed = true;
        _downX = x;
        _downY = y;
        _enteredAt = null;
        return InteractionSignal.None;
    }

    public InteractionSignal PointerUp(double x, double y, bool hit, bool allowWake = false)
    {
        var wasPressed = _pressed;
        var dragged = IsDragging || (wasPressed && (Math.Abs(x - _downX) >= _dragThresholdX || Math.Abs(y - _downY) >= _dragThresholdY));
        var hadDrag = IsDragging;
        _pressed = false;
        IsDragging = false;
        ResetBaseline(x, y, hit);
        if (hadDrag) return InteractionSignal.EndDrag;
        if (!wasPressed || dragged || !hit) return InteractionSignal.None;
        // Hover and click have independent meanings; persisted legacy TriggerMode and
        // the old allowWake hint no longer gate a valid explicit click.
        return InteractionSignal.Click;
    }

    /// <summary>Capture-loss follows the same drag-release path; pending click/hover candidates are discarded.</summary>
    public InteractionSignal Cancel()
    {
        var signal = IsDragging ? InteractionSignal.EndDrag : InteractionSignal.None;
        IsDragging = false;
        _pressed = false;
        _enteredAt = _leftAt = null;
        _armed = false;
        return signal;
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
