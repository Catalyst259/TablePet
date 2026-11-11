using TablePet.Core;

namespace TablePet.App;

/// <summary>A separately positioned visual channel with one slot per character frame.</summary>
public sealed record AnimationDecoration(double CanvasWidth, double CanvasHeight,
    double OffsetX, double OffsetY, AnimationClip Clip);
