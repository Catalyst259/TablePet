namespace TablePet.Core;

public enum CompanionDialogueKind { Base, Contextual, Greeting, Milestone }

public sealed record CompanionDialogueChoice(
    CompanionText? Content,
    CompanionDialogueKind Kind,
    string? MilestoneId = null)
{
    public bool RequiresSavedConsumption => Kind is CompanionDialogueKind.Greeting or CompanionDialogueKind.Milestone;
}

/// <summary>Selects text without consuming it; the coordinator commits only an accepted display.</summary>
public sealed class CompanionDialogue
{
    private readonly CompanionContent _content;
    private readonly Func<int, int> _next;
    private static readonly CompanionText SafeDailyResponse = new()
    {
        Id = "fallback.daily_click", Scene = "daily_click", Stage = "any",
        Text = "我在这儿呢。", ShortText = "我在这儿呢。"
    };

    public CompanionDialogue(CompanionContent? content = null, Func<int, int>? next = null)
    {
        _content = content ?? CompanionContent.Default;
        _next = next ?? Random.Shared.Next;
    }

    public CompanionDialogueChoice Select(CompanionState state, CompanionDay day, string scene,
        bool userInitiated = true, bool allowMilestone = true)
    {
        if (scene == "work_busy") return Base();
        if (scene == "work_start")
            return Wrap(Pick(state, scene), userInitiated && !day.GreetingHandled
                ? CompanionDialogueKind.Greeting : CompanionDialogueKind.Contextual);
        if (scene == "work_break") return Wrap(Pick(state, scene), CompanionDialogueKind.Contextual);
        if (scene is not ("daily_click" or "rest_click")) return Base();

        if (!day.GreetingHandled)
        {
            var isReturn = day.ReturnEligible == true;
            var text = scene == "rest_click"
                ? _content.FindText(isReturn ? "rest_short.return" : "rest_short.greeting")
                : Pick(state, isReturn ? "return_greeting" : "daily_greeting");
            return Wrap(text, CompanionDialogueKind.Greeting);
        }

        if (allowMilestone && state.PendingMilestoneId is { } milestone && !day.AutomaticNoticeUsed &&
            (scene == "daily_click" || milestone != "milestone.first_day"))
        {
            var selected = SelectMilestone(state, milestone);
            if (selected.Content is not null) return selected;
        }

        if (scene == "rest_click")
        {
            return Wrap(Pick(state, "rest_click"), CompanionDialogueKind.Contextual);
        }

        return SelectDailyResponse(state);
    }

    // Used for accepted clicks and as a non-celebratory fallback when one-time copy cannot be saved.
    public CompanionDialogueChoice SelectDailyResponse(CompanionState state) =>
        new(Pick(state, "daily_click") ?? SafeDailyResponse, CompanionDialogueKind.Contextual);

    public CompanionDialogueChoice SelectMilestone(CompanionState state, string milestoneId)
    {
        if (state.ConsumedMilestoneIds.Contains(milestoneId) || IsMilestoneRead(state, milestoneId)) return Base();
        var text = _content.FindText(milestoneId);
        return text is null ? Base() : new(text, CompanionDialogueKind.Milestone, milestoneId);
    }

    public long ConsumeBeforeSave(CompanionState state, CompanionDay day, CompanionDialogueChoice choice)
    {
        if (!choice.RequiresSavedConsumption) return state.StateRevision;
        if (choice.Kind == CompanionDialogueKind.Greeting) day.GreetingHandled = true;
        if (choice.MilestoneId is { } milestone)
        {
            state.ConsumedMilestoneIds.Add(milestone);
            if (state.PendingMilestoneId == milestone) state.PendingMilestoneId = null;
            day.AutomaticNoticeUsed = true;
        }
        return state.Touch();
    }

    public long CommitDisplayed(CompanionState state, CompanionDialogueChoice choice)
    {
        if (choice.Content is not { } content) return state.StateRevision;
        AppendRecent(state.RecentDialogueIds, content.Id, 8);
        if (!state.RecentDialogueIdsByScene.TryGetValue(content.Scene, out var recent))
            state.RecentDialogueIdsByScene[content.Scene] = recent = [];
        AppendRecent(recent, content.Id, 2);
        return state.Touch();
    }

    public static bool IsMilestoneRead(CompanionState state, string milestoneId) =>
        state.Memories.Any(x => x.Id == MemoryId(milestoneId) && x.IsRead);

    public static string MemoryId(string milestoneId) => milestoneId switch
    {
        "milestone.first_day" => "first_day",
        "milestone.familiar" => "stage_familiar",
        "milestone.close" => "stage_close",
        "milestone.in_sync" => "stage_in_sync",
        _ => ""
    };

    private CompanionText? Pick(CompanionState state, string scene)
    {
        var candidates = _content.ForScene(scene, state.Stage);
        if (candidates.Count == 0) return null;
        state.RecentDialogueIdsByScene.TryGetValue(scene, out var recent);
        recent ??= [];
        var pool = candidates.Where(x => !state.RecentDialogueIds.Contains(x.Id) && !recent.Contains(x.Id)).ToArray();
        if (pool.Length == 0) pool = candidates.Where(x => !recent.Contains(x.Id)).ToArray();
        if (pool.Length == 0 && recent.Count > 0) pool = candidates.Where(x => x.Id != recent[^1]).ToArray();
        if (pool.Length == 0) pool = candidates.ToArray();
        // The bundled catalog is equally weighted. Keep support for positive future editorial weights.
        var total = pool.Sum(x => (long)x.Weight);
        if (total > int.MaxValue) return pool[Next(pool.Length)];
        var ticket = Next((int)total);
        foreach (var text in pool)
        {
            ticket -= text.Weight;
            if (ticket < 0) return text;
        }
        return pool[^1];
    }

    private int Next(int exclusiveMax) => Math.Clamp(_next(exclusiveMax), 0, exclusiveMax - 1);
    private static void AppendRecent(List<string> list, string id, int maximum)
    {
        list.Add(id);
        if (list.Count > maximum) list.RemoveRange(0, list.Count - maximum);
    }
    private static CompanionDialogueChoice Base() => new(null, CompanionDialogueKind.Base);
    private static CompanionDialogueChoice Wrap(CompanionText? text, CompanionDialogueKind kind) =>
        text is null ? Base() : new(text, kind);
}
