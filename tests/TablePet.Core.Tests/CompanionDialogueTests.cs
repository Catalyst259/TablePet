using System.Text.Json;
using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class CompanionDialogueTests
{
    private static readonly DateOnly Date = new(2026, 11, 10);
    private static CompanionSpeechContext Context => new(Date);
    private static CompanionSpeechCue Cue(FakeClock clock, string scene = "daily_click", bool user = true) =>
        new(scene, scene == "daily_click" ? "喵" : "基础提示", 2400, clock.NowMilliseconds, user);
    private static CompanionState State(bool greeted = true) => new()
    {
        Days = { ["2026-11-10"] = new CompanionDay { GreetingHandled = greeted, ReturnEligible = false } }
    };
    private static CompanionDay Day(CompanionState state) => state.Days["2026-11-10"];
    private static void AddStage(CompanionState state, string stage)
    {
        if (stage == "new") return;
        state.Memories.Add(new CompanionMemory { Id = "stage_" + stage, LocalDate = "2026-11-10", Sequence = 1 });
    }
    private static void MakeMilestone(CompanionState state)
    {
        AddStage(state, "familiar");
        state.PendingMilestoneId = "milestone.familiar";
    }

    [Fact]
    public void BundledCatalogHasEveryStageSceneAndAllPermanentMemories()
    {
        var content = CompanionContent.Default;
        Assert.Empty(content.ValidationErrors);
        Assert.Equal(78, content.Texts.Count);
        Assert.Equal(12, content.Memories.Count);
        Assert.Equal(content.Texts.Count, content.Texts.Select(x => x.Id).Distinct().Count());
        foreach (var stage in new[] { "new", "familiar", "close", "in_sync" })
            foreach (var scene in new[] { "daily_greeting", "daily_click", "work_start", "work_break", "rest_click", "return_greeting" })
                Assert.Equal(3, content.ForScene(scene, stage).Count);
        Assert.All(content.Texts, x => Assert.InRange(CompanionContent.VisibleLength(x.ShortText), 1, 14));
        Assert.Equal(CompanionshipRules.MemoryIds.Order(), content.Memories.Select(x => x.Id).Order());
    }

    [Theory]
    [InlineData("familiar")]
    [InlineData("close")]
    [InlineData("in_sync")]
    public void SelectionUsesCurrentStageAndDoesNotRepeatTheLastTwoInTheScene(string stage)
    {
        var state = State();
        AddStage(state, stage);
        var dialogue = new CompanionDialogue(next: _ => 0);
        var selected = new List<string>();
        for (var i = 0; i < 9; i++)
        {
            var choice = dialogue.Select(state, Day(state), "work_break");
            Assert.Equal(stage, choice.Content!.Stage);
            Assert.Equal("work_break", choice.Content.Scene);
            Assert.DoesNotContain(choice.Content.Id, selected.TakeLast(2));
            selected.Add(choice.Content.Id);
            dialogue.CommitDisplayed(state, choice);
        }
        Assert.Equal(8, state.RecentDialogueIds.Count);
        Assert.Equal(2, state.RecentDialogueIdsByScene["work_break"].Count);
    }

    [Fact]
    public void GlobalHistoryIsPreferredButRelaxesBeforeTheSceneHistory()
    {
        var state = State();
        state.RecentDialogueIds.AddRange(["work_start.new.01", "work_start.new.02"]);
        var dialogue = new CompanionDialogue(next: _ => 0);
        var choice = dialogue.Select(state, Day(state), "work_start", false);
        Assert.Equal("work_start.new.03", choice.Content!.Id);
        state.RecentDialogueIds.Add("work_start.new.03");
        state.RecentDialogueIdsByScene["work_start"] = ["work_start.new.02", "work_start.new.03"];
        choice = dialogue.Select(state, Day(state), "work_start", false);
        Assert.Equal("work_start.new.01", choice.Content!.Id);
    }

    [Fact]
    public void EveryAcceptedDailyClickHasNewTextEvenWithAnOldExhaustedAllowance()
    {
        var state = State();
        var dialogue = new CompanionDialogue(next: _ => 0);
        Day(state).OrdinarySpeechCount = 6; // Existing schema-1 records remain valid and usable.
        var selected = new List<string>();
        for (var i = 0; i < 100; i++)
        {
            var choice = dialogue.Select(state, Day(state), "daily_click");
            Assert.Equal(CompanionDialogueKind.Contextual, choice.Kind);
            Assert.NotNull(choice.Content);
            Assert.NotEqual("喵", choice.Content.Text);
            Assert.DoesNotContain(choice.Content.Id, selected.TakeLast(2));
            selected.Add(choice.Content.Id);
            dialogue.CommitDisplayed(state, choice);
        }
        Assert.Equal(6, Day(state).OrdinarySpeechCount);
        Assert.Equal(0, state.TotalPoints);
        Assert.Empty(state.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RandomSelectionAlwaysChoosesAClickLine(int ticket)
    {
        var state = State();
        var dialogue = new CompanionDialogue(next: maximum => maximum == 3 ? ticket : 0);
        Assert.NotNull(dialogue.Select(state, Day(state), "daily_click").Content);
    }

    [Fact]
    public void GreetingWaitsForItsOwnSavedRevision()
    {
        var state = State(false);
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        speech.HandleCue(Cue(clock), Context);
        var consumedRevision = state.StateRevision;
        Assert.True(Day(state).GreetingHandled);
        Assert.Null(speech.SpeechText);
        speech.AcknowledgeSaved(consumedRevision - 1);
        speech.Tick(Context);
        Assert.Null(speech.SpeechText);
        speech.AcknowledgeSaved(consumedRevision);
        speech.Tick(Context);
        Assert.Equal("喵，见面啦。", speech.SpeechText);
        Assert.Equal(0, Day(state).OrdinarySpeechCount);
        Assert.True(state.StateRevision > consumedRevision); // Text history is a newer mutation.
    }

    [Fact]
    public void ReturnWinsGreetingAndStageOnARestClickWithoutDiscardingTheStage()
    {
        var state = State(false);
        MakeMilestone(state);
        Day(state).ReturnEligible = true;
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        speech.HandleCue(Cue(clock, "rest_click"), Context with { IsWorkMode = true });
        speech.AcknowledgeSaved(state.StateRevision);
        speech.Tick(Context with { IsWorkMode = true });
        Assert.Equal("又见面啦，一起歇会儿。", speech.SpeechText);
        Assert.Equal("milestone.familiar", state.PendingMilestoneId);
        speech.Clear();
        speech.HandleCue(Cue(clock, "rest_click"), Context with { IsWorkMode = true });
        Assert.Contains("milestone.familiar", state.ConsumedMilestoneIds);
        speech.AcknowledgeSaved(state.StateRevision);
        speech.Tick(Context with { IsWorkMode = true });
        Assert.Equal("好像更熟悉你一点啦。", speech.SpeechText);
    }

    [Fact]
    public void AutomaticWorkStartDoesNotConsumeTheDailyGreetingButExplicitStartDoes()
    {
        var clock = new FakeClock();
        var state = State(false);
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        speech.HandleCue(Cue(clock, "work_start", false), Context with { IsWorkMode = true });
        Assert.NotNull(speech.SpeechText);
        Assert.False(Day(state).GreetingHandled);
        speech.HandleCue(Cue(clock, "work_start"), Context with { IsWorkMode = true });
        Assert.True(Day(state).GreetingHandled);
        Assert.Null(speech.SpeechText);
        speech.AcknowledgeSaved(state.StateRevision);
        speech.Tick(Context with { IsWorkMode = true });
        Assert.NotNull(speech.SpeechText);
    }

    [Theory]
    [InlineData("milestone.first_day", "first_day")]
    [InlineData("milestone.familiar", "stage_familiar")]
    [InlineData("milestone.close", "stage_close")]
    [InlineData("milestone.in_sync", "stage_in_sync")]
    public void DailyClickCarriesPendingMilestoneOnlyAfterItsOwnSaveReceipt(string milestone, string memory)
    {
        var state = State();
        state.Memories.Add(new CompanionMemory { Id = memory, LocalDate = "2026-11-10", Sequence = 1 });
        state.PendingMilestoneId = milestone;
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock, next: _ => 2);
        // A click can carry the reminder while an idle-only automatic announcement is unavailable.
        speech.HandleCue(Cue(clock), Context);
        var consumedRevision = state.StateRevision;
        Assert.Contains(milestone, state.ConsumedMilestoneIds);
        Assert.Null(state.PendingMilestoneId);
        Assert.Null(speech.SpeechText);
        speech.AcknowledgeSaved(consumedRevision - 1);
        speech.Tick(Context);
        Assert.Null(speech.SpeechText);
        speech.AcknowledgeSaved(consumedRevision);
        speech.Tick(Context);
        Assert.Equal(CompanionContent.Default.FindText(milestone)!.Text, speech.SpeechText);
        Assert.Equal(0, Day(state).OrdinarySpeechCount);
        speech.Clear();
        speech.HandleCue(Cue(clock), Context);
        Assert.Contains(CompanionContent.Default.ForScene("daily_click", state.Stage), line => line.Text == speech.SpeechText);
    }

    [Theory]
    [InlineData(false, "daily_greeting")]
    [InlineData(true, "return_greeting")]
    public void FirstDailyGreetingKeepsThePendingMilestoneForTheNextClick(bool returning, string expectedScene)
    {
        var state = State(false);
        MakeMilestone(state);
        Day(state).ReturnEligible = returning;
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        speech.HandleCue(Cue(clock), Context);
        speech.AcknowledgeSaved(state.StateRevision);
        speech.Tick(Context);
        Assert.Equal(expectedScene, CompanionContent.Default.FindText(state.RecentDialogueIds[^1])!.Scene);
        Assert.Equal("milestone.familiar", state.PendingMilestoneId);
        Assert.DoesNotContain("milestone.familiar", state.ConsumedMilestoneIds);
        speech.Clear();
        speech.HandleCue(Cue(clock), Context);
        speech.AcknowledgeSaved(state.StateRevision);
        speech.Tick(Context);
        Assert.Equal("好像更熟悉你一点啦。", speech.SpeechText);
    }

    [Fact]
    public void AWorkCueReplacesAMilestoneWaitingForDiskWithoutLaterReplayingIt()
    {
        var state = State();
        MakeMilestone(state);
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        speech.Tick(Context with { CanShowMilestone = true });
        Assert.Contains("milestone.familiar", state.ConsumedMilestoneIds);
        var consumedRevision = state.StateRevision;
        speech.HandleCue(Cue(clock, "work_break"), Context with { IsWorkMode = true });
        Assert.Equal("一起歇会儿吧。", speech.SpeechText);
        speech.AcknowledgeSaved(consumedRevision);
        clock.Advance(10_000);
        speech.Tick(Context with { CanShowMilestone = true });
        Assert.Null(speech.SpeechText);
        Assert.Null(state.PendingMilestoneId);
    }

    [Fact]
    public void PendingClickSpeechExpiresWithoutChangingTheDisplayedHistory()
    {
        var state = State();
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        speech.HandleCue(Cue(clock), Context with { MenusOpen = true });
        Assert.True(speech.HasPendingWork);
        clock.Advance(2001);
        speech.Tick(Context);
        Assert.Null(speech.SpeechText);
        Assert.False(speech.HasPendingWork);
        Assert.Equal(0, Day(state).OrdinarySpeechCount);
        Assert.Empty(state.RecentDialogueIds);
        speech.HandleCue(Cue(clock), Context);
        Assert.NotNull(speech.SpeechText);
        Assert.Equal("daily_click", CompanionContent.Default.FindText(Assert.Single(state.RecentDialogueIds))!.Scene);
        Assert.Equal(0, Day(state).OrdinarySpeechCount);
    }

    [Fact]
    public void SaveFailureUsesRegularNewCopyForItsFullDurationWithoutReplayingTheGreeting()
    {
        var state = State(false);
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        speech.HandleCue(Cue(clock), Context);
        clock.Advance(200);
        speech.AcknowledgeSaveFailure();
        speech.Tick(Context);
        var fallback = speech.SpeechText;
        Assert.Contains(CompanionContent.Default.ForScene("daily_click", state.Stage), line => line.Text == fallback);
        Assert.Equal(0, speech.SpeechElapsedMs);
        Assert.InRange(speech.SpeechDurationMs, 3000, 4500);
        speech.AcknowledgeSaved(state.StateRevision);
        speech.Tick(Context);
        Assert.Equal(fallback, speech.SpeechText);
        clock.Advance(speech.SpeechDurationMs);
        speech.Tick(Context);
        Assert.Null(speech.SpeechText);
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("disable")]
    [InlineData("read")]
    public void OneTimeCueDoesNotResurrectAfterItsContextBecomesInvalid(string change)
    {
        var state = State();
        MakeMilestone(state);
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        var context = Context with { CanShowMilestone = true };
        speech.Tick(context);
        if (change == "read") state.Memories.Single().IsRead = true;
        speech.Tick(context with { Visible = change != "hide", Enabled = change != "disable" });
        speech.AcknowledgeSaved(state.StateRevision);
        speech.Tick(context);
        Assert.Null(speech.SpeechText);
        Assert.Contains("milestone.familiar", state.ConsumedMilestoneIds);
    }

    [Fact]
    public void PausingFreezesTheVisibleBubbleAndHidingClearsIt()
    {
        var state = State();
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        speech.HandleCue(Cue(clock, "work_busy"), Context);
        clock.Advance(500);
        speech.Tick(Context with { Paused = true });
        clock.Advance(60_000);
        speech.Tick(Context with { Paused = true });
        Assert.Equal(500, speech.SpeechElapsedMs);
        Assert.NotNull(speech.SpeechText);
        speech.Tick(Context);
        clock.Advance(100);
        speech.Tick(Context);
        Assert.Equal(600, speech.SpeechElapsedMs);
        speech.Tick(Context with { Visible = false });
        speech.Tick(Context);
        Assert.Null(speech.SpeechText);
    }

    [Fact]
    public void DisabledCompanionshipKeepsDailyClicksSilentWithoutCreatingUsageRecords()
    {
        var state = new CompanionState();
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock);
        speech.HandleCue(Cue(clock), Context with { Enabled = false });
        Assert.Null(speech.SpeechText);
        Assert.Empty(state.Days);
        Assert.Equal(0, state.StateRevision);
    }

    [Fact]
    public void InvalidCatalogUsesSafeNewCopyInsteadOfTheLegacyMeow()
    {
        var entry = new CompanionText { Id = "bad", Scene = "daily_click", Stage = "unknown", Text = "喵", ShortText = "喵" };
        var content = CompanionContent.FromJson(JsonSerializer.Serialize(new { texts = new[] { entry }, memories = Array.Empty<object>() }));
        Assert.False(content.IsValid);
        var clock = new FakeClock();
        var state = State(false);
        var speech = new SpeechCoordinator(state, clock, content);
        speech.HandleCue(Cue(clock), Context);
        Assert.Equal("我在这儿呢。", speech.SpeechText);
        Assert.False(Day(state).GreetingHandled);
        speech.Tick(Context with { Enabled = false });
        Assert.Null(speech.SpeechText);
    }

    [Fact]
    public void AutomaticPauseBlocksBothIdleAndClickedMilestonesWithoutConsumingThem()
    {
        var state = State();
        MakeMilestone(state);
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        var paused = Context with { CanShowMilestone = true, AllowMilestone = false };
        speech.Tick(paused);
        Assert.False(speech.HasPendingWork);
        speech.HandleCue(Cue(clock), paused);
        Assert.Contains(CompanionContent.Default.ForScene("daily_click", state.Stage), line => line.Text == speech.SpeechText);
        Assert.Equal("milestone.familiar", state.PendingMilestoneId);
        Assert.DoesNotContain("milestone.familiar", state.ConsumedMilestoneIds);
        clock.Advance(speech.SpeechDurationMs);
        speech.Tick(paused with { AllowMilestone = true });
        speech.AcknowledgeSaved(state.StateRevision);
        speech.Tick(paused with { AllowMilestone = true });
        Assert.Equal(CompanionContent.Default.FindText("milestone.familiar")!.Text, speech.SpeechText);
    }

    [Theory]
    [InlineData("喵")]
    [InlineData(null)]
    public void BothMeowAndWinkCuesUseNewCopyAndRespectTheTextSwitch(string? originalText)
    {
        var state = State();
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        var cue = new CompanionSpeechCue("daily_click", originalText, 1400, 0);
        speech.HandleCue(cue, Context);
        Assert.Contains(CompanionContent.Default.ForScene("daily_click", "new"), line => line.Text == speech.SpeechText);
        Assert.InRange(speech.SpeechDurationMs, 3000, 4500);
        speech.Tick(Context with { Enabled = false });
        Assert.Null(speech.SpeechText);
        speech.HandleCue(cue, Context with { Enabled = false });
        Assert.Null(speech.SpeechText);
    }

    [Fact]
    public void ExpiredGreetingNeverResurrectsAsOldOrNewClickText()
    {
        var state = State(false);
        var clock = new FakeClock();
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        speech.HandleCue(Cue(clock), Context);
        clock.Advance(2001);
        speech.Tick(Context);
        speech.AcknowledgeSaved(state.StateRevision);
        speech.Tick(Context);
        Assert.Null(speech.SpeechText);
        Assert.False(speech.HasPendingWork);
    }
}
