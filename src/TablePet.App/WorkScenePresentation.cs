using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TablePet.Core;

namespace TablePet.App;

/// <summary>Uses the same transparent cat canvas and a stationary foreground prop.</summary>
public static class WorkScenePresentation
{
    public static void Apply(TranslateTransform catOffset, Image propsImage,
        BehaviorController behavior, AssetCatalog assets, double displayedHeight)
    {
        catOffset.Y = behavior.WorkBodyOffsetY * displayedHeight / assets.CanvasHeight;
        var opacity = Math.Clamp(behavior.WorkPropsOpacity, 0, 1);
        if (opacity <= 0 || !assets.Clips.TryGetValue("work_props", out var props))
        {
            propsImage.Visibility = Visibility.Collapsed;
            propsImage.Opacity = 0;
            return;
        }
        // The foreground never inherits the cat's breathing transform.
        propsImage.Source ??= assets.Image(props.Frames[0].File);
        propsImage.Opacity = opacity;
        propsImage.Visibility = Visibility.Visible;
    }
}
