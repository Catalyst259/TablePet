using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TablePet.App.Services;

namespace TablePet.App;

/// <summary>A registered, click-through layer sampled from the cat's own timeline.</summary>
public sealed class FireflyWindow : Window
{
    private readonly Window _pet;
    private readonly AssetCatalog _assets;
    private readonly AnimationDecoration _decoration;
    private readonly Rect _relativeBounds;
    private readonly Image _image = new() { Stretch = Stretch.Fill, Focusable = false };
    private string? _file;
    private bool _nativeReady, _closed;

    public FireflyWindow(Window pet, AssetCatalog assets, AnimationDecoration decoration)
    {
        _pet = pet;
        _assets = assets;
        _decoration = decoration;
        _relativeBounds = new Rect(decoration.OffsetX / assets.CanvasWidth, decoration.OffsetY / assets.CanvasHeight,
            decoration.CanvasWidth / assets.CanvasWidth, decoration.CanvasHeight / assets.CanvasHeight);
        Owner = pet;
        Title = "草莓猫萤火虫";
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
        Width = pet.Width * _relativeBounds.Width;
        Height = pet.Height * _relativeBounds.Height;
        Left = pet.Left + pet.Width * _relativeBounds.X;
        Top = pet.Top + pet.Height * _relativeBounds.Y;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Content = _image;
        Closed += (_, _) => _closed = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PlatformService.ConfigureDecorationWindow(this);
        _nativeReady = true;
        PlatformService.AlignDecorationWindow(_pet, this, _relativeBounds);
    }

    public void Update(int frameIndex)
    {
        Dispatcher.VerifyAccess();
        if (_closed) return;
        if (!_pet.IsVisible || _pet.WindowState == WindowState.Minimized ||
            frameIndex < 0 || frameIndex >= _decoration.Clip.Frames.Count)
        {
            if (IsVisible) Hide();
            return;
        }
        var file = _decoration.Clip.Frames[frameIndex].File;
        if (_file != file)
        {
            _image.Source = _assets.Image(file);
            _file = file;
        }
        if (IsVisible) return;
        PlatformService.ShowDecorationWindow(_pet, this, _relativeBounds);
        SyncBounds();
    }

    public void SyncBounds()
    {
        Dispatcher.VerifyAccess();
        if (!_closed && _nativeReady && IsVisible)
            PlatformService.AlignDecorationWindow(_pet, this, _relativeBounds);
    }
}
