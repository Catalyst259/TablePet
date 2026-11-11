using System.Globalization;
using System.Text.Json.Serialization;

namespace TablePet.Core;

/// <summary>Only durable summaries belong here; timers and current activities are process-local.</summary>
public sealed class CompanionState
{
    public int SchemaVersion { get; set; } = 1;
    public int RulesVersion { get; set; } = 1;
    public long StateRevision { get; set; }
    public DateTimeOffset? RecordingStartedAtUtc { get; set; }
    public string? RecordingStartedLocalDate { get; set; }
    public string? LastActiveLocalDate { get; set; }
    public bool ReturnBaselineRequired { get; set; }
    public Dictionary<string, CompanionDay> Days { get; set; } = new(StringComparer.Ordinal);
    public List<CompanionMemory> Memories { get; set; } = [];
    public string? PendingMilestoneId { get; set; }
    public HashSet<string> ConsumedMilestoneIds { get; set; } = new(StringComparer.Ordinal);
    public List<string> RecentDialogueIds { get; set; } = [];
    public Dictionary<string, List<string>> RecentDialogueIdsByScene { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset? SavedAtUtc { get; set; }

    [JsonIgnore] public long TotalPoints => Days.Values.Sum(day => (long)day.PointsGranted);
    [JsonIgnore] public int CompanionDays => Days.Values.Count(day => day.CompanionDayGranted);
    [JsonIgnore] public string Stage
    {
        get
        {
            var rank = CompanionshipRules.StageRank(CompanionshipRules.StageFor(TotalPoints, CompanionDays));
            foreach (var memory in Memories)
                rank = Math.Max(rank, CompanionshipRules.StageMemoryRank(memory.Id));
            return CompanionshipRules.Stages[rank].Id;
        }
    }

    public long Touch() => StateRevision = checked(StateRevision + 1);

    public CompanionState DeepClone() => new()
    {
        SchemaVersion = SchemaVersion, RulesVersion = RulesVersion, StateRevision = StateRevision,
        RecordingStartedAtUtc = RecordingStartedAtUtc, RecordingStartedLocalDate = RecordingStartedLocalDate,
        LastActiveLocalDate = LastActiveLocalDate, ReturnBaselineRequired = ReturnBaselineRequired,
        Days = Days.ToDictionary(pair => pair.Key, pair => pair.Value.DeepClone(), StringComparer.Ordinal),
        Memories = Memories.Select(memory => memory.DeepClone()).ToList(),
        PendingMilestoneId = PendingMilestoneId,
        ConsumedMilestoneIds = new(ConsumedMilestoneIds, StringComparer.Ordinal),
        RecentDialogueIds = [.. RecentDialogueIds],
        RecentDialogueIdsByScene = RecentDialogueIdsByScene.ToDictionary(pair => pair.Key,
            pair => new List<string>(pair.Value), StringComparer.Ordinal),
        SavedAtUtc = SavedAtUtc
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != 1) errors.Add("Unsupported companion schema version.");
        if (RulesVersion != 1) errors.Add("Unsupported companion rules version.");
        if (StateRevision < 0 || StateRevision == long.MaxValue) errors.Add("Invalid state revision.");
        if (RecordingStartedLocalDate is not null && !TryDate(RecordingStartedLocalDate, out _))
            errors.Add("Invalid recording start date.");
        if (LastActiveLocalDate is not null && !TryDate(LastActiveLocalDate, out _))
            errors.Add("Invalid last active date.");
        if (RecordingStartedAtUtc.HasValue != (RecordingStartedLocalDate is not null))
            errors.Add("Recording start time and date must be present together.");
        if (Days is null || Memories is null || ConsumedMilestoneIds is null || RecentDialogueIds is null || RecentDialogueIdsByScene is null)
        {
            errors.Add("Companion collections cannot be null.");
            return errors;
        }
        foreach (var (date, day) in Days)
        {
            if (!TryDate(date, out _) || day is null) { errors.Add("Invalid daily entry."); continue; }
            if (day.QualifiedPresenceMs is < 0 or > CompanionshipRules.DailyCapMs)
                errors.Add($"Invalid presence duration for {date}.");
            if (day.PointsGranted != CompanionshipRules.PointsFor(day.QualifiedPresenceMs))
                errors.Add($"Daily points disagree with presence for {date}.");
            if (day.CompanionDayGranted != (day.QualifiedPresenceMs >= CompanionshipRules.FirstDayMs))
                errors.Add($"Companion day disagrees with presence for {date}.");
            if (day.OrdinarySpeechCount is < 0 or > 6) errors.Add($"Invalid speech budget for {date}.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new HashSet<long>();
        foreach (var memory in Memories)
        {
            if (memory is null) { errors.Add("Memory cannot be null."); continue; }
            if (!CompanionshipRules.MemoryIds.Contains(memory.Id) || !ids.Add(memory.Id)) errors.Add("Invalid or duplicate memory ID.");
            if (!TryDate(memory.LocalDate, out _)) errors.Add("Invalid memory date.");
            if (memory.Sequence <= 0 || memory.Sequence == long.MaxValue || !sequences.Add(memory.Sequence))
                errors.Add("Invalid or duplicate memory sequence.");
        }
        if (ConsumedMilestoneIds.Any(id => !CompanionshipRules.MilestoneIds.Contains(id)))
            errors.Add("Unknown consumed milestone.");
        if (PendingMilestoneId is not null && (!CompanionshipRules.MilestoneIds.Contains(PendingMilestoneId)
            || ConsumedMilestoneIds.Contains(PendingMilestoneId)
            || !ids.Contains(CompanionshipRules.MemoryForMilestone(PendingMilestoneId))))
            errors.Add("Invalid pending milestone.");
        if (RecentDialogueIds.Count > 8 || RecentDialogueIds.Any(string.IsNullOrWhiteSpace))
            errors.Add("Invalid recent dialogue history.");
        if (RecentDialogueIdsByScene.Count > 16 || RecentDialogueIdsByScene.Any(pair =>
            string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || pair.Value.Count > 2 || pair.Value.Any(string.IsNullOrWhiteSpace)))
            errors.Add("Invalid scene dialogue history.");
        return errors;
    }

    public bool TryValidate(out string error)
    {
        var errors = Validate();
        error = string.Join(" ", errors);
        return errors.Count == 0;
    }

    internal static bool TryDate(string? text, out DateOnly date) => DateOnly.TryParseExact(text,
        "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

public sealed class CompanionDay
{
    public long QualifiedPresenceMs { get; set; }
    public int PointsGranted { get; set; }
    public bool CompanionDayGranted { get; set; }
    public bool GreetingHandled { get; set; }
    // Retained for schema-1 compatibility; accepted clicks no longer consume a text allowance.
    public int OrdinarySpeechCount { get; set; }
    public bool AutomaticNoticeUsed { get; set; }
    public bool? ReturnEligible { get; set; }
    public CompanionDay DeepClone() => (CompanionDay)MemberwiseClone();
}

public sealed class CompanionMemory
{
    public string Id { get; set; } = "";
    public DateTimeOffset UnlockedAtUtc { get; set; }
    public string LocalDate { get; set; } = "";
    public long Sequence { get; set; }
    public bool IsRead { get; set; }
    public CompanionMemory DeepClone() => (CompanionMemory)MemberwiseClone();
}

public sealed record CompanionStageThreshold(string Id, int Points, int Days);

public static class CompanionshipRules
{
    public const long FirstDayMs = 60_000;
    public const long DailyCapMs = 1_200_000;
    public const long ActiveWindowMs = 1_800_000;
    public const long WorkMemoryMs = 600_000;
    public const long BreakMemoryMs = 60_000;
    public static IReadOnlyList<CompanionStageThreshold> Stages { get; } = Array.AsReadOnly(new[]
    {
        new CompanionStageThreshold("new", 0, 0), new("familiar", 12, 3), new("close", 40, 8), new("in_sync", 100, 21)
    });
    public static IReadOnlySet<string> MemoryIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "first_day", "stage_familiar", "stage_close", "stage_in_sync", "together_7_days", "together_30_days",
        "together_60_days", "first_work_completed", "first_break_completed", "first_firefly_visit", "first_gentle_wake", "first_return"
    };
    public static IReadOnlySet<string> MilestoneIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    { "milestone.first_day", "milestone.familiar", "milestone.close", "milestone.in_sync" };

    public static int PointsFor(long milliseconds) => milliseconds < FirstDayMs ? 0
        : 3 + (milliseconds >= 300_000 ? 1 : 0) + (milliseconds >= 600_000 ? 1 : 0) + (milliseconds >= DailyCapMs ? 1 : 0);
    public static string StageFor(long points, int days)
    {
        for (var i = Stages.Count - 1; i > 0; i--)
            if (points >= Stages[i].Points && days >= Stages[i].Days) return Stages[i].Id;
        return "new";
    }
    public static int StageRank(string id) => id switch { "familiar" => 1, "close" => 2, "in_sync" => 3, _ => 0 };
    public static int StageMemoryRank(string id) => id switch { "stage_familiar" => 1, "stage_close" => 2, "stage_in_sync" => 3, _ => 0 };
    public static string MemoryForMilestone(string id) => id switch
    {
        "milestone.first_day" => "first_day", "milestone.familiar" => "stage_familiar",
        "milestone.close" => "stage_close", "milestone.in_sync" => "stage_in_sync", _ => ""
    };
    public static string? MilestoneForMemory(string id) => id switch
    {
        "first_day" => "milestone.first_day", "stage_familiar" => "milestone.familiar",
        "stage_close" => "milestone.close", "stage_in_sync" => "milestone.in_sync", _ => null
    };
    public static int MilestoneRank(string id) => id switch
    { "milestone.first_day" => 0, "milestone.familiar" => 1, "milestone.close" => 2, "milestone.in_sync" => 3, _ => -1 };

    public static double ProgressToNextStage(CompanionState state)
    {
        var rank = StageRank(state.Stage);
        if (rank == Stages.Count - 1) return 1;
        var previous = Stages[rank];
        var next = Stages[rank + 1];
        return Math.Clamp(Math.Min((state.TotalPoints - previous.Points) / (double)(next.Points - previous.Points),
            (state.CompanionDays - previous.Days) / (double)(next.Days - previous.Days)), 0, 1);
    }
}
