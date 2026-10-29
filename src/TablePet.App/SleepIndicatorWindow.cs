using System.Windows;
using System.Windows.Media;
using TablePet.App.Services;

namespace TablePet.App;

/// <summary>
/// A non-interactive owned surface for sleep decoration. Keeping its native window
/// click-through preserves desktop input beneath the letters as well as the empty canvas.
/// </summary>
public sealed class SleepIndicatorWindow : Window
{
    private readonly Window _pet;
    private readonly SleepIndicatorVisual _indicator = new();
    private bool _nativeReady;
    private bool _closed;

    public SleepIndicatorWindow(Window pet)
    {
        _pet = pet;
        Owner = pet;
        Title = "草莓猫睡眠装饰";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Width = pet.Width;
        Height = pet.Height;
        Left = pet.Left;
        Top = pet.Top;
        Content = _indicator;
        Closed += (_, _) => _closed = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PlatformService.ConfigureDecorationWindow(this);
        _nativeReady = true;
        // Align the hidden HWND before its first Show so it cannot flash at the
        // default monitor position, including when the pet is on a second screen.
        PlatformService.AlignDecorationWindow(_pet, this);
    }

    /// <summary>
    /// Uses the existing player's elapsed time; pausing the player freezes the letters.
    /// The host calls Hide when sleep ends or the pet is suspended.
    /// </summary>
    public void Update(long elapsedMs)
    {
        Dispatcher.VerifyAccess();
        if (_closed) return;
        if (!_pet.IsVisible || _pet.WindowState == WindowState.Minimized)
        {
            if (IsVisible) Hide();
            return;
        }

        _indicator.Update(elapsedMs);
        if (IsVisible) return;

        PlatformService.ShowDecorationWindow(_pet, this);
        SyncBounds();
    }

    /// <summary>Call on owner movement, resize, DPI and topmost changes, including during drag.</summary>
    public void SyncBounds()
    {
        Dispatcher.VerifyAccess();
        if (!_closed && _nativeReady && IsVisible)
            PlatformService.AlignDecorationWindow(_pet, this);
    }
}
