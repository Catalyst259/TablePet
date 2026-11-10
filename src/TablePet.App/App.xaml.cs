using System.IO;
using System.Windows;
using TablePet.App.Services;
using TablePet.Core;

namespace TablePet.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSettings;
    private EventWaitHandle? _exitRequested;
    private RegisteredWaitHandle? _showWait;
    private RegisteredWaitHandle? _exitWait;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Noninteractive deployment check: no windows, settings, tray, or instance signals.
        if (e.Args.Contains("--check-assets"))
        {
            try
            {
                var check = new AssetCatalog(Path.Combine(AppContext.BaseDirectory, "Assets", "strawberry-cat"));
                check.CacheForSize(240, 1.5);
                foreach (var file in check.AllFrameFiles) check.Image(file);
                // These are loaded by filesystem path, too. Single-file publish
                // must not silently embed the fallback or replace the tray icon.
                check.Image("fallback.png");
                using var trayIcon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "strawberry-cat", "tray.ico"));
                var missingMemoryArt = CompanionContent.Default.Memories.Any(memory =>
                    CompanionMemoryIllustrations.Default.Find(memory.Id) is null);
                Shutdown(check.IsFallback || check.HasFrameFallback || check.HasOptionalActionFallback || missingMemoryArt ? 2 : 0);
            }
            catch (Exception error) { ErrorLogger.Write("素材自检失败。", error); Shutdown(2); }
            return;
        }
        // An explicit development data directory isolates previews from the user's pet and saved relationship.
        ApplicationDataLocation dataLocation;
        var dataArgument = Array.IndexOf(e.Args, "--data-dir");
        if (dataArgument >= 0 && dataArgument + 1 >= e.Args.Length) { Shutdown(2); return; }
        try
        {
            dataLocation = ApplicationDataLocation.Resolve(dataArgument >= 0 ? e.Args[dataArgument + 1] : null);
        }
        catch (Exception error) { ErrorLogger.Write("预览目录无效。", error); Shutdown(2); return; }
        var instanceSuffix = dataLocation.InstanceSuffix;
        _showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TablePet.Skeletal.ShowSettings" + instanceSuffix);
        _exitRequested = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TablePet.Skeletal.Exit" + instanceSuffix);
        _singleInstance = new Mutex(true, @"Local\TablePet.Skeletal.StrawberryCat" + instanceSuffix, out var isFirst);
        if (!isFirst)
        {
            if (e.Args.Contains("--quit")) _exitRequested.Set(); else _showSettings.Set();
            Shutdown(); return;
        }
        if (e.Args.Contains("--quit")) { Shutdown(); return; }
        try
        {
            var settings = new SettingsService(Path.Combine(dataLocation.DirectoryPath, "settings.json")); settings.Load();
            var assets = new AssetCatalog(Path.Combine(AppContext.BaseDirectory, "Assets", "strawberry-cat"));
            var pet = new PetWindow(settings, assets, Path.Combine(dataLocation.DirectoryPath, "companion-state.json"));
            MainWindow = pet; pet.Show();
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSettings, (_, _) => Dispatcher.BeginInvoke(pet.OpenSettings), null, -1, false);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitRequested, (_, _) => Dispatcher.BeginInvoke(pet.Quit), null, -1, false);
            DispatcherUnhandledException += (_, args) => ErrorLogger.Write("未处理的界面异常。", args.Exception);
            if (e.Args.Contains("--settings")) pet.OpenSettings();
            if (e.Args.Contains("--companion")) pet.OpenCompanion();
        }
        catch (Exception error)
        {
            ErrorLogger.Write("应用启动失败。", error);
            MessageBox.Show("草莓猫未能启动：\n" + error.Message, "草莓猫", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null); _exitWait?.Unregister(null);
        _showSettings?.Dispose(); _exitRequested?.Dispose(); _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
