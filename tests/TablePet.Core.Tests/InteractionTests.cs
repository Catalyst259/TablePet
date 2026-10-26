using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class InteractionTests
{
    [Theory]
    [InlineData(TriggerMode.Hover)]
    [InlineData(TriggerMode.Click)]
    public void HoverTriggersOnceAndStationaryHitChangesDoNotRearm(TriggerMode legacyMode)
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new PetSettings { TriggerMode = legacyMode });
        input.ResetBaseline(0, 0, false);
        input.UpdatePointer(10, 0, true);
        clock.Advance(119);
        Assert.Equal(InteractionSignal.None, input.Tick());
        clock.Advance(1);
        Assert.Equal(InteractionSignal.Hover, input.Tick());
        input.UpdatePointer(10, 0, false);
        clock.Advance(1000);
        input.UpdatePointer(10, 0, true);
        Assert.Equal(InteractionSignal.None, input.Tick());
        input.UpdatePointer(0, 0, false);
        clock.Advance(40);
        input.UpdatePointer(10, 0, true);
        clock.Advance(120);
        Assert.Equal(InteractionSignal.Hover, input.Tick());
    }

    [Fact]
    public void ShortEdgeJitterDoesNotRearmHover()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new PetSettings());
        input.ResetBaseline(0, 0, false);
        input.UpdatePointer(10, 0, true);
        clock.Advance(120);
        Assert.Equal(InteractionSignal.Hover, input.Tick());
        input.UpdatePointer(9, 0, false);
        clock.Advance(20);
        input.UpdatePointer(10, 0, true);
        clock.Advance(120);
        Assert.Equal(InteractionSignal.None, input.Tick());
    }

    [Fact]
    public void BaselineInsidePetRequiresRealLeaveBeforeHover()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new PetSettings());
        input.ResetBaseline(10, 10, true);
        input.UpdatePointer(11, 10, true);
        clock.Advance(10000);
        Assert.Equal(InteractionSignal.None, input.Tick());
    }

    [Theory]
    [InlineData(TriggerMode.Hover)]
    [InlineData(TriggerMode.Click)]
    public void ClickIsDistinctFromHoverAndAlwaysAcceptedInLegacyModes(TriggerMode legacyMode)
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new PetSettings { TriggerMode = legacyMode });
        input.ResetBaseline(0, 0, false);
        input.UpdatePointer(10, 10, true);
        clock.Advance(120);
        Assert.Equal(InteractionSignal.Hover, input.Tick());
        input.PointerDown(10, 10, true);
        Assert.Equal(InteractionSignal.Click, input.PointerUp(10, 10, true));
        input.PointerDown(10, 10, true);
        Assert.Equal(InteractionSignal.Click, input.PointerUp(10, 10, true, allowWake: true));
        clock.Advance(1000);
        Assert.Equal(InteractionSignal.None, input.Tick());
    }

    [Fact]
    public void ClickRequiresPressAndReleaseOnBody()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new PetSettings { TriggerMode = TriggerMode.Click });
        input.PointerDown(0, 0, false);
        Assert.Equal(InteractionSignal.None, input.PointerUp(1, 0, true));
        input.PointerDown(10, 10, true);
        Assert.Equal(InteractionSignal.None, input.PointerUp(11, 10, false));
        input.PointerDown(10, 10, true);
        Assert.Equal(InteractionSignal.Click, input.PointerUp(11, 10, true));
    }

    [Fact]
    public void DragCancelsClickAndHoverAndCaptureLossEndsDrag()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new PetSettings());
        input.ResetBaseline(0, 0, false);
        input.UpdatePointer(10, 10, true);
        input.PointerDown(10, 10, true);
        Assert.Equal(InteractionSignal.BeginDrag, input.UpdatePointer(15, 10, false));
        clock.Advance(1000);
        Assert.Equal(InteractionSignal.None, input.Tick());
        Assert.Equal(InteractionSignal.EndDrag, input.Cancel());
        Assert.False(input.IsPressed);
        Assert.False(input.IsDragging);
        Assert.Equal(InteractionSignal.None, input.PointerUp(15, 10, true, allowWake: true));
    }

    [Fact]
    public void ChangingSettingsWithStationaryCursorDoesNotCreateHover()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new PetSettings());
        input.ResetBaseline(0, 0, false);
        input.ApplySettings(new PetSettings { PetHeightDip = 480 }, 0, 0, true);
        clock.Advance(10000);
        Assert.Equal(InteractionSignal.None, input.Tick());
    }

    [Fact]
    public void SilhouetteMovingAwayCancelsDwellWithoutCreatingNewEntry()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new PetSettings());
        input.ResetBaseline(0, 0, false);
        input.UpdatePointer(10, 0, true);
        clock.Advance(60);
        input.UpdatePointer(10, 0, false);
        clock.Advance(1000);
        input.UpdatePointer(10, 0, true);
        Assert.Equal(InteractionSignal.None, input.Tick());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(119)]
    [InlineData(481)]
    public void InvalidSizeIsRejected(double height)
    {
        Assert.NotEmpty((new PetSettings { PetHeightDip = height }).Validate());
    }

    [Fact]
    public void InvalidPositionAndTriggerModeAreRejected()
    {
        Assert.NotEmpty((new PetSettings { Position = new WindowPlacement { RelativeX = double.NaN } }).Validate());
        Assert.NotEmpty((new PetSettings { TriggerMode = (TriggerMode)99 }).Validate());
    }
}
