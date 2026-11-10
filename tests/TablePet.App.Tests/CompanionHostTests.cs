using System.IO;
using System.Reflection;
using System.Windows;
using TablePet.App.Services;
using TablePet.Core;
using Xunit;

namespace TablePet.App.Tests;

public sealed class CompanionHostTests
{
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    [Fact]
    public void HiddenClickThroughCatCanOpenOnePanelWithoutBecomingVisible() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var settings = new SettingsService(directory.FilePath("settings.json"));
        settings.Update(new PetSettings { ClickThrough = true, AutoSleep = false });
        var assets = new AssetCatalog(FindAssets());
        var pet = new PetWindow(settings, assets, directory.FilePath("companion.json"));
        var session = Field<CompanionSession>(pet, "_companion");
        try
        {
            // Exercise the actual hide command while keeping the pet itself offscreen during the test.
            typeof(PetWindow).GetMethod("ToggleVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pet, null);
            Assert.True(Field<bool>(pet, "_hidden"));
            pet.OpenCompanion();
            var panel = Field<CompanionWindow>(pet, "_companionWindow");
            Assert.True(panel.IsVisible);
            Assert.False(pet.IsVisible);
            Assert.False(session.Engine.Context.IsVisible);
            Assert.True(session.Engine.HasActiveWindow);
            Assert.True(settings.Current.ClickThrough);
            panel.WindowState = WindowState.Minimized;

            pet.OpenCompanion();
            Assert.Same(panel, Field<CompanionWindow>(pet, "_companionWindow"));
            Assert.Equal(WindowState.Normal, panel.WindowState);
            Assert.False(pet.IsVisible);
            Assert.True(Field<bool>(pet, "_hidden"));
            Assert.Equal(0, session.State.TotalPoints);
            panel.Close();
            Assert.Null(Field<CompanionWindow?>(pet, "_companionWindow"));
        }
        finally
        {
            Field<CompanionWindow?>(pet, "_companionWindow")?.Close();
            session.Dispose();
            Field<PlatformService>(pet, "_platform").Dispose();
            typeof(PetWindow).GetField("_closing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pet, true);
            pet.Close();
        }
    });

    private static string FindAssets()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "assets", "strawberry-cat");
            if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
        }
        throw new DirectoryNotFoundException("Production strawberry-cat assets are unavailable.");
    }
}
