using System.Globalization;

namespace TablePet.Core;

public readonly record struct CompanionCalendarTime(DateTimeOffset UtcNow, TimeZoneInfo TimeZone)
{
    public DateTimeOffset LocalNow => TimeZoneInfo.ConvertTime(UtcNow, TimeZone);
    public string LocalDate => LocalNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public interface ICalendarClock
{
    CompanionCalendarTime GetCurrent();
}

public sealed class SystemCalendarClock : ICalendarClock
{
    public CompanionCalendarTime GetCurrent() => new(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
}

public sealed record CompanionPresenceContext
{
    public bool IsVisible { get; init; } = true;
    public bool IsSessionAvailable { get; init; } = true;
    public bool IsDragging { get; init; }
    public bool IsWorkPaused { get; init; }
    public bool AllowsPresence => IsVisible && IsSessionAvailable && !IsDragging;
}
