using System.IO;
using System.Text.Json;
using TablePet.App.Services;
using TablePet.Core;
using Xunit;

namespace TablePet.App.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void MissingFileUsesDefaultsWithoutCreatingAFile()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("settings.json");
        using var service = new SettingsService(path);
        Assert.Equal(new PetSettings(), service.Load());
        Assert.False(File.Exists(path));
        Assert.Null(service.LastError);
    }

    [Fact]
    public void MissingFieldsKeepCurrentDefaultsAndExistingValues()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("settings.json");
        File.WriteAllText(path, """{"petHeightDip":180,"triggerMode":"click"}""");
        using var service = new SettingsService(path);
        Assert.Equal(new PetSettings { PetHeightDip = 180, TriggerMode = TriggerMode.Click }, service.Load());
    }

    [Fact]
    public void ValidSettingsRoundTripWithReadableEnumAndSavedMonitorPosition()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("settings.json");
        var expected = new PetSettings
        {
            PetHeightDip = 300,
            TriggerMode = TriggerMode.Click,
            HoldMs = 1234,
            Position = new WindowPlacement { MonitorId = "DISPLAY2", RelativeX = 0.1, RelativeY = 0.7 }
        };
        using (var service = new SettingsService(path))
        {
            var published = new List<PetSettings>();
            service.Changed += published.Add;
            service.Update(expected, saveImmediately: true);
            Assert.Equal(expected, Assert.Single(published));
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("click", document.RootElement.GetProperty("triggerMode").GetString());
        }
        using var restarted = new SettingsService(path);
        Assert.Equal(expected, restarted.Load());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void InvalidUpdateNeverPublishesOrOverwritesSavedConfiguration()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("settings.json");
        using var service = new SettingsService(path);
        service.Update(new PetSettings { PetHeightDip = 300 }, saveImmediately: true);
        var saved = File.ReadAllBytes(path);
        var before = service.Current;
        var published = 0;
        service.Changed += _ => published++;
        Assert.Throws<ArgumentException>(() => service.Update(before with { PetHeightDip = double.NaN }, saveImmediately: true));
        Assert.Equal(before, service.Current);
        Assert.Equal(0, published);
        Assert.Equal(saved, File.ReadAllBytes(path));
    }

    [Fact]
    public void FailedAtomicReplacementPreservesOldBytesAndCanRetry()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("settings.json");
        using var service = new SettingsService(path);
        service.Update(new PetSettings { PetHeightDip = 180 }, saveImmediately: true);
        var saved = File.ReadAllBytes(path);
        var failures = new List<string>();
        service.SaveFailed += failures.Add;
        // FileShare.Read permits verification but deliberately denies File.Replace's delete access.
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            service.Update(service.Current with { PetHeightDip = 360 }, saveImmediately: true);
            Assert.NotEmpty(failures);
            Assert.NotNull(service.LastError);
            Assert.Equal(saved, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        Assert.True(service.Flush());
        Assert.Null(service.LastError);
        using var restarted = new SettingsService(path);
        Assert.Equal(360, restarted.Load().PetHeightDip);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":99}")]
    [InlineData("{\"blinkMinMs\":9000,\"blinkMaxMs\":3000}")]
    [InlineData("{\"triggerMode\":99}")]
    public void InvalidFilesAreBackedUpExactlyAndResetRepairsEvenDefaultState(string invalid)
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("settings.json");
        File.WriteAllText(path, invalid);
        using var service = new SettingsService(path);
        Assert.Equal(new PetSettings(), service.Load());
        var backup = Assert.Single(Directory.GetFiles(directory.Root, "settings.json.invalid-*.bak"));
        Assert.Equal(invalid, File.ReadAllText(backup));
        Assert.Equal(invalid, File.ReadAllText(path));
        Assert.NotNull(service.LastError);
        service.ResetDefaults();
        using var restarted = new SettingsService(path);
        Assert.Equal(new PetSettings(), restarted.Load());
        Assert.Null(restarted.LastError);
        Assert.Equal(invalid, File.ReadAllText(backup));
    }
}
