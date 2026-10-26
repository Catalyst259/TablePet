namespace TablePet.Core;

public enum TriggerMode { Hover, Click }

public sealed record WindowPlacement
{
    public string MonitorId { get; init; } = "";
    public double RelativeX { get; init; } = 0.8;
    public double RelativeY { get; init; } = 0.8;
}

public sealed record PetSettings
{
    public int SchemaVersion { get; init; } = 1;
    public bool AutoSleep { get; init; } = true;
    public int BlinkMinMs { get; init; } = 3000;
    public int BlinkMaxMs { get; init; } = 8000;
    public int SleepAfterMs { get; init; } = 60000;
    // Retained for loading old configurations; hover wakes and click reacts in both modes.
    public TriggerMode TriggerMode { get; init; } = TriggerMode.Hover;
    public int HoverDwellMs { get; init; } = 120;
    public int HoldMs { get; init; } = 1000;
    public int CooldownMs { get; init; } = 1000;
    public double PetHeightDip { get; init; } = 240;
    public bool Topmost { get; init; } = true;
    public bool ClickThrough { get; init; }
    public bool StartWithWindows { get; init; }
    public WindowPlacement? Position { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != 1) errors.Add("不支持此设置版本。");
        // Bounds prevent overflow and accidentally scheduling multi-year intervals.
        if (BlinkMinMs is < 1 or > 86400000 || BlinkMaxMs is < 1 or > 86400000 || BlinkMinMs > BlinkMaxMs)
            errors.Add("眨眼间隔须为 1 至 86400000 毫秒，且最小值不大于最大值。");
        if (SleepAfterMs is < 1 or > 604800000) errors.Add("入睡时间须为 1 至 604800000 毫秒。");
        if (!Enum.IsDefined(TriggerMode)) errors.Add("请选择悬停或点击触碰模式。");
        if (HoverDwellMs is < 1 or > 10000) errors.Add("悬停时间须为 1 至 10000 毫秒。");
        if (HoldMs is < 1 or > 60000) errors.Add("起身保持时间须为 1 至 60000 毫秒。");
        if (CooldownMs is < 0 or > 60000) errors.Add("反应冷却时间须为 0 至 60000 毫秒。");
        if (!double.IsFinite(PetHeightDip) || PetHeightDip is < 120 or > 480)
            errors.Add("角色高度须为 120 至 480 DIP。");
        if (Position is { } p && (p.MonitorId is null || !double.IsFinite(p.RelativeX) || !double.IsFinite(p.RelativeY)
            || p.RelativeX is < 0 or > 1 || p.RelativeY is < 0 or > 1)) errors.Add("窗口位置无效。");
        return errors;
    }

    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
    }
}
