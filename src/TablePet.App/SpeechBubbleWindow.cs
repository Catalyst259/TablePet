using System.Windows;
using System.Windows.Media;
using TablePet.App.Services;

namespace TablePet.App;

/// <summary>An owned, click-through speech bubble that shares the pet animation clock.</summary>
public sealed class SpeechBubbleWindow : Window
{
    private readonly Window _pet;
    private readonly SpeechBubbleVisual _bubble = new();
    private bool _nativeReady;
    private bool _closed;

    public SpeechBubbleWindow(Window pet)
    {
        _pet = pet;
        Owner = pet;
        Title = "草莓猫喵气泡";
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
        Content = _bubble;
        Closed += (_, _) => _closed = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PlatformService.ConfigureDecorationWindow(this);
        _nativeReady = true;
        PlatformService.AlignDecorationWindow(_pet, this);
    }

    public void Update(long elapsedMs)
    {
        Dispatcher.VerifyAccess();
        if (_closed) return;
        if (!_pet.IsVisible || _pet.WindowState == WindowState.Minimized || elapsedMs <= 180 || elapsedMs >= 1400)
        {
            if (IsVisible) Hide();
            return;
        }
        _bubble.Update(elapsedMs);
        if (IsVisible) return;
        PlatformService.ShowDecorationWindow(_pet, this);
        SyncBounds();
    }

    public void SyncBounds()
    {
        Dispatcher.VerifyAccess();
        if (!_closed && _nativeReady && IsVisible)
            PlatformService.AlignDecorationWindow(_pet, this);
    }
}
