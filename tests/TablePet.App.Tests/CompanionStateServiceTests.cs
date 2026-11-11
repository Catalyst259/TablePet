using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TablePet.App.Services;
using TablePet.Core;
using Xunit;

namespace TablePet.App.Tests;

public sealed class CompanionStateServiceTests
{
    private sealed class FixedClock : IClock
    {
        public long NowMilliseconds => 0;
    }

    [Fact]
    public async Task RestartAfterMilestoneConsumptionWasSavedKeepsTheStageWithoutReplayingTheBubble()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        var store = new CompanionStateService(path);
        store.Load();
        var state = QualifiedState(7);
        state.Memories.Add(new CompanionMemory
        {
            Id = "stage_familiar", LocalDate = "2026-11-10", Sequence = 1,
            UnlockedAtUtc = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero)
        });
        state.PendingMilestoneId = "milestone.familiar";
        var clock = new FixedClock();
        var context = new CompanionSpeechContext(new DateOnly(2026, 11, 10), CanShowMilestone: true);
        var speech = new SpeechCoordinator(state, clock, next: _ => 0);
        speech.Tick(context);
        Assert.Null(speech.SpeechText);
        Assert.Contains("milestone.familiar", state.ConsumedMilestoneIds);
        Assert.True(await store.SaveAsync(state));
        await store.WaitForPendingWritesAsync();
        // Simulate losing all process-local state before the successful receipt is handled.
        var restartedStore = new CompanionStateService(path);
        var restarted = restartedStore.Load();
        var restartedSpeech = new SpeechCoordinator(restarted, new FixedClock(), next: _ => 0);
        restartedSpeech.AcknowledgeSaved(restartedStore.SavedRevision);
        restartedSpeech.Tick(context);
        Assert.Equal("familiar", restarted.Stage);
        Assert.Equal(3, restarted.TotalPoints);
        Assert.Contains(restarted.Memories, memory => memory.Id == "stage_familiar");
        Assert.Null(restarted.PendingMilestoneId);
        Assert.Contains("milestone.familiar", restarted.ConsumedMilestoneIds);
        Assert.Null(restartedSpeech.SpeechText);
        Assert.False(restartedSpeech.HasPendingWork);
    }

    [Fact]
    public void MissingRecordDoesNotCreateAFileOrInventHistory()
    {
        using var directory = new TestDirectory();
        var service = new CompanionStateService(directory.FilePath("companion-state.json"));
        var state = service.Load();
        Assert.True(service.CanWrite);
        Assert.Equal(-1, service.SavedRevision);
        Assert.Null(state.RecordingStartedAtUtc);
        Assert.Empty(state.Days);
        Assert.False(File.Exists(service.StatePath));
    }

    [Fact]
    public async Task SavingUsesAnIndependentSnapshotAndReportsItsActualRevision()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        var service = new CompanionStateService(path);
        service.Load();
        var state = QualifiedState(7);
        var receipt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Saved += revision => receipt.TrySetResult(revision);
        var save = service.SaveAsync(state);
        state.Days["2026-11-10"].QualifiedPresenceMs = 300_000;
        state.Days["2026-11-10"].PointsGranted = 4;
        state.Touch();
        Assert.True(await save);
        await service.WaitForPendingWritesAsync();
        Assert.Equal(7, await receipt.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var restored = new CompanionStateService(path).Load();
        Assert.Equal(7, restored.StateRevision);
        Assert.Equal(3, restored.TotalPoints);
        Assert.NotNull(restored.SavedAtUtc);
        Assert.Equal(8, state.StateRevision);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task AtomicReplacementKeepsThePreviousValidSnapshotAsBackup()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        var service = new CompanionStateService(path);
        service.Load();
        Assert.True(await service.SaveAsync(QualifiedState(1)));
        await service.WaitForPendingWritesAsync();
        var previousBytes = File.ReadAllBytes(path);
        Assert.True(await service.SaveAsync(QualifiedState(2, 300_000)));
        await service.WaitForPendingWritesAsync();
        Assert.Equal(previousBytes, File.ReadAllBytes(service.BackupPath));
        Assert.Equal(2, new CompanionStateService(path).Load().StateRevision);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task FailedReplacementRetainsOldBytesAndCanRetryTheSameRevision()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        var service = new CompanionStateService(path);
        service.Load();
        Assert.True(await service.SaveAsync(QualifiedState(1)));
        await service.WaitForPendingWritesAsync();
        var original = File.ReadAllBytes(path);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(await service.SaveAsync(QualifiedState(2, 300_000)));
            await service.WaitForPendingWritesAsync();
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(1, service.SavedRevision);
            Assert.NotNull(service.LastError);
            Assert.True(service.CanWrite);
            Assert.False(File.Exists(path + ".tmp"));
        }
        Assert.True(await service.SaveAsync(QualifiedState(2, 300_000)));
        await service.WaitForPendingWritesAsync();
        Assert.Null(service.LastError);
        Assert.Equal(2, service.SavedRevision);
        Assert.Equal(4, new CompanionStateService(path).Load().TotalPoints);
    }

    [Fact]
    public void BrokenPrimaryRestoresBackupAndPreservesTheBrokenBytes()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        File.WriteAllText(path, "broken-primary");
        Write(path + ".bak", QualifiedState(12));
        var service = new CompanionStateService(path);
        var restored = service.Load();
        Assert.Equal(12, restored.StateRevision);
        Assert.Equal(3, restored.TotalPoints);
        Assert.True(service.CanWrite);
        Assert.NotNull(service.RecoveryNotice);
        var preserved = Assert.Single(Directory.GetFiles(directory.Root, "companion-state.json.invalid-*.bak"));
        Assert.Equal("broken-primary", File.ReadAllText(preserved));
        Assert.Equal(12, new CompanionStateService(path).Load().StateRevision);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void MissingPrimaryRestoresAnExistingValidBackup()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        Write(path + ".bak", QualifiedState(12));
        var service = new CompanionStateService(path);
        Assert.Equal(12, service.Load().StateRevision);
        Assert.NotNull(service.RecoveryNotice);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("broken-primary")]
    [InlineData("{}")]
    public async Task BrokenPrimaryWithoutBackupPausesRecordingAndPreservesTheOriginal(string brokenPrimary)
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        File.WriteAllText(path, brokenPrimary);
        var original = File.ReadAllBytes(path);
        var service = new CompanionStateService(path);

        var state = service.Load();

        Assert.False(service.CanWrite);
        Assert.False(service.UnsupportedVersion);
        Assert.NotNull(service.LastError);
        Assert.Null(service.RecoveryNotice);
        Assert.Empty(state.Days);
        Assert.False(await service.SaveAsync(QualifiedState(4)));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(service.BackupPath));
        var preserved = Assert.Single(Directory.GetFiles(directory.Root, "companion-state.json.invalid-*.bak"));
        Assert.Equal(original, File.ReadAllBytes(preserved));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"rulesVersion\":1,\"days\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"rulesVersion\":2,\"memories\":{}}")]
    [InlineData("{\"schemaVersion\":99,\"rulesVersion\":1}")]
    public async Task FutureVersionsAreNeverReplacedByAnOlderBackupEvenWhenTheirShapeChanged(string future)
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        File.WriteAllText(path, future);
        Write(path + ".bak", QualifiedState(12));
        var service = new CompanionStateService(path);
        service.Load();
        Assert.True(service.UnsupportedVersion);
        Assert.False(service.CanWrite);
        Assert.False(await service.SaveAsync(QualifiedState(13)));
        Assert.Equal(future, File.ReadAllText(path));
        Assert.Null(service.RecoveryNotice);
        Assert.Throws<InvalidOperationException>(() => service.StartNewRecord());
    }

    [Theory]
    [InlineData("broken-primary", "broken-backup")]
    [InlineData("{}", "[]")]
    public async Task InvalidPrimaryAndBackupBlockWritesUntilAnExplicitRestart(string brokenPrimary, string brokenBackup)
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        File.WriteAllText(path, brokenPrimary);
        File.WriteAllText(path + ".bak", brokenBackup);
        var service = new CompanionStateService(path);
        service.Load();
        Assert.False(service.CanWrite);
        Assert.False(service.UnsupportedVersion);
        Assert.NotNull(service.LastError);
        Assert.False(await service.SaveAsync(QualifiedState(4)));
        Assert.Equal(brokenPrimary, File.ReadAllText(path));
        Assert.Equal(brokenBackup, File.ReadAllText(path + ".bak"));
        var newState = service.StartNewRecord();
        Assert.True(service.CanWrite);
        Assert.Empty(newState.Memories);
        Assert.Empty(newState.Days);
        Assert.False(File.Exists(path + ".bak"));
        var preserved = Directory.GetFiles(directory.Root, "*.restart-*.bak").Select(File.ReadAllText).ToArray();
        Assert.Contains(brokenPrimary, preserved);
        Assert.Contains(brokenBackup, preserved);
        Assert.Empty(new CompanionStateService(path).Load().Days);
    }

    [Fact]
    public async Task ANewerFileAppearingDuringTheSessionIsPreserved()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        var service = new CompanionStateService(path);
        service.Load();
        Assert.True(await service.SaveAsync(QualifiedState(1)));
        await service.WaitForPendingWritesAsync();
        const string future = "{\"schemaVersion\":2,\"rulesVersion\":1,\"days\":[]}";
        File.WriteAllText(path, future);
        Assert.False(await service.SaveAsync(QualifiedState(2)));
        await service.WaitForPendingWritesAsync();
        Assert.True(service.UnsupportedVersion);
        Assert.False(service.CanWrite);
        Assert.Equal(future, File.ReadAllText(path));
        Assert.Equal(1, service.SavedRevision);
    }

    [Fact]
    public async Task OutOfOrderConcurrentRequestsCannotReplaceANewerDurableRevision()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        var service = new CompanionStateService(path);
        service.Load();
        // A larger latest snapshot makes serialization overlap the following small stale requests.
        var latest = QualifiedState(100);
        var firstDate = new DateOnly(2000, 1, 1);
        for (var day = 0; day < 4_000; day++) latest.Days.Add(firstDate.AddDays(day).ToString("yyyy-MM-dd"), new());
        var saves = new List<Task<bool>> { service.SaveAsync(latest) };
        for (var revision = 99; revision >= 1; revision--) saves.Add(service.SaveAsync(QualifiedState(revision)));
        Assert.All(await Task.WhenAll(saves), Assert.True);
        await service.WaitForPendingWritesAsync();
        Assert.Equal(100, service.SavedRevision);
        Assert.Equal(100, new CompanionStateService(path).Load().StateRevision);
    }

    [Fact]
    public async Task NotificationFailuresCannotStrandTheWriterOrFutureRequests()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        var service = new CompanionStateService(path);
        service.Load();
        service.Saved += _ => throw new InvalidOperationException("A closed view cannot accept this receipt.");
        service.StatusChanged += () => throw new InvalidOperationException("A closed status view.");
        Assert.True(await service.SaveAsync(QualifiedState(1)).WaitAsync(TimeSpan.FromSeconds(5)));
        await service.WaitForPendingWritesAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await service.SaveAsync(QualifiedState(2)).WaitAsync(TimeSpan.FromSeconds(5)));
        await service.WaitForPendingWritesAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, new CompanionStateService(path).Load().StateRevision);
    }

    [Fact]
    public async Task CorruptInMemoryAccountingNeverOverwritesTheLastGoodFile()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion-state.json");
        var service = new CompanionStateService(path);
        service.Load();
        Assert.True(await service.SaveAsync(QualifiedState(1)));
        await service.WaitForPendingWritesAsync();
        var original = File.ReadAllBytes(path);
        var invalid = QualifiedState(2);
        invalid.Days["2026-11-10"].PointsGranted = 6;
        Assert.Throws<ArgumentException>(() => { _ = service.SaveAsync(invalid); });
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private static CompanionState QualifiedState(long revision, long presence = 60_000)
    {
        var state = new CompanionState
        {
            StateRevision = revision,
            RecordingStartedAtUtc = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero),
            RecordingStartedLocalDate = "2026-11-10",
            LastActiveLocalDate = "2026-11-10"
        };
        state.Days.Add("2026-11-10", new()
        {
            QualifiedPresenceMs = presence, PointsGranted = CompanionshipRules.PointsFor(presence),
            CompanionDayGranted = presence >= 60_000, ReturnEligible = false
        });
        return state;
    }

    private static void Write(string path, CompanionState state) =>
        File.WriteAllText(path, JsonSerializer.Serialize(state, CompanionStateService.JsonOptions));
}
