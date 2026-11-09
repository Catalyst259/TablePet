using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class DoubleClickInteractionTests
{
    private static void FirstClick(InteractionService input, double x = 10, double y = 10)
    {
        Assert.Equal(InteractionSignal.None, input.PointerDown(x, y, true));
        Assert.Equal(InteractionSignal.None, input.PointerUp(x, y, true));
        Assert.True(input.HasPendingClick);
    }

    [Fact]
    public void SingleClickWaitsForSystemIntervalAndBaselineResetPreservesIt()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new(), doubleClickTimeMs: 700);
        FirstClick(input);
        input.ResetBaseline(10, 10, true);
        Assert.Equal(700, input.NextUpdateInMs);
        clock.Advance(699);
        Assert.Equal(InteractionSignal.None, input.Tick());
        Assert.Equal(1, input.NextUpdateInMs);
        clock.Advance(1);
        Assert.Equal(InteractionSignal.Click, input.Tick());
        Assert.False(input.HasPendingClick);
        Assert.Equal(InteractionSignal.None, input.Tick());
    }

    [Fact]
    public void DoubleClickEmitsOnlyOneSignalAfterSecondRelease()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new());
        FirstClick(input);
        clock.Advance(200);
        Assert.Equal(InteractionSignal.None, input.PointerDown(10, 10, true));
        Assert.Equal(InteractionSignal.None, input.Tick());
        Assert.Equal(InteractionSignal.DoubleClick, input.PointerUp(10, 10, true));
        Assert.False(input.HasPendingClick);
        clock.Advance(1000);
        Assert.Equal(InteractionSignal.None, input.Tick());
    }

    [Theory]
    [InlineData(12, 13, true)]
    [InlineData(12.1, 10, false)]
    [InlineData(10, 13.1, false)]
    public void DoubleClickUsesInjectedSystemRectangle(double x, double y, bool matches)
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new(), doubleClickWidth: 4, doubleClickHeight: 6);
        FirstClick(input);
        clock.Advance(100);
        Assert.Equal(matches ? InteractionSignal.None : InteractionSignal.Click, input.PointerDown(x, y, true));
        Assert.Equal(matches ? InteractionSignal.DoubleClick : InteractionSignal.None, input.PointerUp(x, y, true));
        clock.Advance(500);
        Assert.Equal(matches ? InteractionSignal.None : InteractionSignal.Click, input.Tick());
    }

    [Fact]
    public void ExpiredCandidateResolvesEvenWhenTheTimerHasNotRun()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new(), doubleClickTimeMs: 300);
        FirstClick(input);
        clock.Advance(300);
        Assert.Equal(InteractionSignal.Click, input.PointerDown(10, 10, true));
        Assert.Equal(InteractionSignal.None, input.PointerUp(10, 10, true));
        clock.Advance(300);
        Assert.Equal(InteractionSignal.Click, input.Tick());
    }

    [Fact]
    public void MatchingSecondPressCanFinishAfterTheDownToDownInterval()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new());
        FirstClick(input);
        clock.Advance(450);
        input.PointerDown(10, 10, true);
        clock.Advance(2000);
        Assert.Equal(InteractionSignal.None, input.Tick());
        Assert.Equal(long.MaxValue, input.NextUpdateInMs);
        Assert.Equal(InteractionSignal.DoubleClick, input.PointerUp(10, 10, true));
    }

    [Fact]
    public void LongFirstPressDoesNotOpenANewDoubleClickIntervalOnRelease()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new());
        input.PointerDown(10, 10, true);
        clock.Advance(600);
        Assert.Equal(InteractionSignal.None, input.Tick());
        Assert.Equal(InteractionSignal.Click, input.PointerUp(10, 10, true));
        Assert.False(input.HasPendingClick);
    }

    [Fact]
    public void TripleClickIsOneDoubleFollowedByOnePendingSingle()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new());
        FirstClick(input);
        clock.Advance(100);
        input.PointerDown(10, 10, true);
        Assert.Equal(InteractionSignal.DoubleClick, input.PointerUp(10, 10, true));
        clock.Advance(100);
        FirstClick(input);
        clock.Advance(500);
        Assert.Equal(InteractionSignal.Click, input.Tick());
        Assert.Equal(InteractionSignal.None, input.Tick());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SecondPressDragDiscardsBothClickCandidates(bool observedMovement)
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new());
        FirstClick(input);
        clock.Advance(100);
        input.PointerDown(10, 10, true);
        if (observedMovement)
            Assert.Equal(InteractionSignal.BeginDrag, input.UpdatePointer(20, 10, true));
        Assert.Equal(observedMovement ? InteractionSignal.EndDrag : InteractionSignal.None,
            input.PointerUp(20, 10, true));
        Assert.False(input.HasPendingClick);
        clock.Advance(1000);
        Assert.Equal(InteractionSignal.None, input.Tick());
    }

    [Fact]
    public void SecondReleaseOutsideTheBodyDiscardsTheCandidate()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new());
        FirstClick(input);
        clock.Advance(100);
        input.PointerDown(10, 10, true);
        Assert.Equal(InteractionSignal.None, input.PointerUp(10, 10, false));
        clock.Advance(500);
        Assert.Equal(InteractionSignal.None, input.Tick());
        Assert.False(input.HasPendingClick);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("settings")]
    [InlineData("system")]
    public void CancellationOrConfigurationChangeDiscardsPendingClick(string change)
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new());
        FirstClick(input);
        if (change == "cancel") input.Cancel();
        else if (change == "settings") input.ApplySettings(new(), 10, 10, true);
        else input.ConfigureDoubleClick(600, 4, 4);
        clock.Advance(1000);
        Assert.Equal(InteractionSignal.None, input.Tick());
        Assert.False(input.HasPendingClick);
    }

    [Fact]
    public void SameSystemConfigurationPreservesCandidateAndChangesDoNotEndDrag()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new());
        FirstClick(input);
        input.ConfigureDoubleClick(500, 4, 4);
        Assert.True(input.HasPendingClick);
        input.PointerDown(10, 10, true);
        Assert.Equal(InteractionSignal.BeginDrag, input.UpdatePointer(20, 10, true));
        input.ConfigureDoubleClick(600, 6, 6);
        Assert.True(input.IsDragging);
        Assert.Equal(InteractionSignal.EndDrag, input.PointerUp(20, 10, true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfigurationChangeDuringSecondPressDoesNotTurnItsReleaseIntoANewClick(bool system)
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new());
        FirstClick(input);
        clock.Advance(100);
        input.PointerDown(10, 10, true);
        if (system) input.ConfigureDoubleClick(600, 4, 4);
        else input.ApplySettings(new(), 10, 10, true);
        Assert.Equal(InteractionSignal.None, input.PointerUp(10, 10, true));
        clock.Advance(1000);
        Assert.Equal(InteractionSignal.None, input.Tick());
        Assert.False(input.HasPendingClick);
    }

    [Fact]
    public void SuppressedPressCannotBecomeADelayedReactionAfterTheCurrentActionEnds()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new());
        Assert.Equal(InteractionSignal.None, input.PointerDown(10, 10, true, allowClick: false));
        clock.Advance(100);
        Assert.Equal(InteractionSignal.None, input.PointerUp(10, 10, true));
        clock.Advance(1000);
        Assert.Equal(InteractionSignal.None, input.Tick());
        Assert.False(input.HasPendingClick);
        FirstClick(input);
        clock.Advance(500);
        Assert.Equal(InteractionSignal.Click, input.Tick());
    }

    [Fact]
    public void SuppressedSecondPressDiscardsAnExpiredCandidateWithoutDispatchingIt()
    {
        var clock = new FakeClock();
        var input = new InteractionService(clock, new());
        FirstClick(input);
        clock.Advance(600);
        Assert.Equal(InteractionSignal.None, input.PointerDown(10, 10, true, allowClick: false));
        Assert.False(input.HasPendingClick);
        Assert.Equal(InteractionSignal.None, input.PointerUp(10, 10, true));
        clock.Advance(1000);
        Assert.Equal(InteractionSignal.None, input.Tick());
    }

    [Fact]
    public void SuppressedClickStillAllowsDragging()
    {
        var input = new InteractionService(new FakeClock(), new());
        input.PointerDown(10, 10, true, allowClick: false);
        Assert.Equal(InteractionSignal.BeginDrag, input.UpdatePointer(20, 10, true));
        Assert.Equal(InteractionSignal.EndDrag, input.PointerUp(20, 10, true));
        Assert.False(input.HasPendingClick);
    }
}
