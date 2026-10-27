using System.IO;
using System.Windows;
using TablePet.App.Services;

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
                foreach (var file in check.Clips.Values.SelectMany(c => c.Frames).Select(f => f.File).Distinct()) check.Image(file);
                Shutdown(check.IsFallback || check.HasFrameFallback || check.HasOptionalActionFallback ? 2 : 0);
            }
            catch (Exception error) { ErrorLogger.Write("素材自检失败。", error); Shutdown(2); }
            return;
        }
        _showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TablePet.ShowSettings");
        _exitRequested = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TablePet.Exit");
        _singleInstance = new Mutex(true, @"Local\TablePet.StrawberryCat", out var isFirst);
        if (!isFirst)
        {
            if (e.Args.Contains("--quit")) _exitRequested.Set(); else _showSettings.Set();
            Shutdown(); return;
        }
        if (e.Args.Contains("--quit")) { Shutdown(); return; }
        try
        {
            var settings = new SettingsService(); settings.Load();
            var assets = new AssetCatalog(Path.Combine(AppContext.BaseDirectory, "Assets", "strawberry-cat"));
            var pet = new PetWindow(settings, assets);
            MainWindow = pet; pet.Show();
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSettings, (_, _) => Dispatcher.BeginInvoke(pet.OpenSettings), null, -1, false);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitRequested, (_, _) => Dispatcher.BeginInvoke(pet.Quit), null, -1, false);
            DispatcherUnhandledException += (_, args) => ErrorLogger.Write("未处理的界面异常。", args.Exception);
            if (e.Args.Contains("--settings")) pet.OpenSettings();
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
