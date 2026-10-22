using System;
using System.Collections.Generic;
using System.Windows.Input;
using CS2Soundboard.Core.Hotkeys;
using Xunit;

namespace CS2Soundboard.Tests;

public class PushToTalkTests
{
    private sealed class FakeInjector : IPushToTalkInjector
    {
        public readonly List<string> Events = new();
        public void Press(PushToTalkKey key) => Events.Add("down " + key);
        public void Release(PushToTalkKey key) => Events.Add("up " + key);
    }

    private sealed class Clock
    {
        public DateTime Now = new(2026, 1, 1);
        public void Advance(int ms) => Now = Now.AddMilliseconds(ms);
    }

    private static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(300);

    private static (PushToTalkController c, FakeInjector inj, Clock clock) Make(PushToTalkKey? key = null)
    {
        var inj = new FakeInjector();
        var clock = new Clock();
        var c = new PushToTalkController(inj, () => clock.Now) { Key = key ?? PushToTalkKey.FromKey(Key.V) };
        return (c, inj, clock);
    }

    [Fact]
    public void PressesBeforeSound_AndReleasesAfterTail()
    {
        var (c, inj, clock) = Make();
        Assert.True(c.Acquire());          // caller waits the pre-delay
        Assert.Equal(new[] { "down V" }, inj.Events);
        c.Update(false, Tail);              // still inside the pre-delay: pending keeps it held
        clock.Advance(1000);
        c.Update(false, Tail);
        Assert.True(c.IsHolding);
        c.EndPending();

        c.Update(true, Tail);               // sound playing
        clock.Advance(5000);
        c.Update(true, Tail);
        Assert.True(c.IsHolding);

        c.Update(false, Tail);              // sound ended: tail starts
        clock.Advance(299);
        c.Update(false, Tail);
        Assert.True(c.IsHolding);
        clock.Advance(2);
        c.Update(false, Tail);
        Assert.False(c.IsHolding);
        Assert.Equal(new[] { "down V", "up V" }, inj.Events);
    }

    [Fact]
    public void SecondSoundDuringHold_ExtendsWithoutRepressing()
    {
        var (c, inj, clock) = Make();
        c.Acquire(); c.EndPending();
        c.Update(false, Tail);
        clock.Advance(200);
        Assert.False(c.Acquire());          // already held: no extra delay
        c.EndPending();
        c.Update(true, Tail);
        clock.Advance(200);
        c.Update(false, Tail);
        clock.Advance(200);
        c.Update(false, Tail);
        Assert.True(c.IsHolding);           // tail restarted by the new sound
        clock.Advance(200);
        c.Update(false, Tail);
        Assert.False(c.IsHolding);
        Assert.Equal(new[] { "down V", "up V" }, inj.Events);
    }

    [Fact]
    public void UserReleasingTheirKeyDuringSound_DoesNotCutTheSound()
    {
        var (c, inj, clock) = Make();
        c.Acquire(); c.EndPending();
        Assert.False(c.OnUserDown());       // user talks too (passes through)
        Assert.True(c.OnUserUp());          // user lets go: swallowed, sound keeps transmitting
        c.Update(true, Tail);
        Assert.Equal(new[] { "down V" }, inj.Events);
        c.Update(false, Tail);
        clock.Advance(400);
        c.Update(false, Tail);
        Assert.Equal(new[] { "down V", "up V" }, inj.Events); // released once the sound is done
    }

    [Fact]
    public void UserStillHoldingAtRelease_IsNotCutOff()
    {
        var (c, inj, clock) = Make();
        c.Acquire(); c.EndPending();
        c.OnUserDown();                     // user starts talking during the sound
        c.Update(false, Tail);
        clock.Advance(400);
        c.Update(false, Tail);
        Assert.False(c.IsHolding);
        Assert.Equal(new[] { "down V" }, inj.Events); // no "up": the user's own key-up will end it
        Assert.False(c.OnUserUp());         // and that physical up passes through
    }

    [Fact]
    public void UserAlreadyTalking_NoInjectionAndNoDelay()
    {
        var (c, inj, clock) = Make();
        c.OnUserDown();
        Assert.False(c.Acquire());          // mic is already open
        c.EndPending();
        Assert.True(c.OnUserUp());          // user stops talking mid-sound: keep it held
        c.Update(false, Tail);
        clock.Advance(400);
        c.Update(false, Tail);
        Assert.Equal(new[] { "up V" }, inj.Events);
    }

    [Fact]
    public void ReleaseNow_AndKeyChange_NeverLeaveKeyStuck()
    {
        var (c, inj, _) = Make();
        c.Acquire();
        c.ReleaseNow();
        Assert.Equal(new[] { "down V", "up V" }, inj.Events);

        c.Acquire();
        c.Key = PushToTalkKey.FromMouse(PttMouseButton.XButton1); // reconfigured while held
        Assert.Equal(new[] { "down V", "up V", "down V", "up V" }, inj.Events);
        c.EndPending(); c.EndPending();
        Assert.True(c.Acquire());
        Assert.Equal("down Mouse 4 (back)", inj.Events[^1]);
    }

    [Fact]
    public void NoKeyConfigured_DoesNothing()
    {
        var inj = new FakeInjector();
        var c = new PushToTalkController(inj);
        Assert.False(c.Acquire());
        c.EndPending();
        c.Update(false, Tail);
        c.ReleaseNow();
        Assert.Empty(inj.Events);
    }

    [Theory]
    [InlineData("Key:V", "V")]
    [InlineData("Key:LeftAlt", "Left Alt")]
    [InlineData("Key:CapsLock", "CapsLock")]
    [InlineData("Mouse:XButton1", "Mouse 4 (back)")]
    [InlineData("Mouse:XButton2", "Mouse 5 (forward)")]
    [InlineData("Mouse:Middle", "Middle mouse button")]
    public void Key_RoundTrip(string text, string display)
    {
        Assert.True(PushToTalkKey.TryParse(text, out var k));
        Assert.Equal(display, k.ToString());
        Assert.True(PushToTalkKey.TryParse(k.Serialize(), out var again));
        Assert.Equal(k, again);
    }

    [Fact]
    public void Key_ValidationAndConflicts()
    {
        Assert.False(PushToTalkKey.TryParse("Key:Escape", out _));
        Assert.False(PushToTalkKey.TryParse("Mouse:None", out _));
        Assert.False(PushToTalkKey.TryParse("garbage", out _));
        var v = PushToTalkKey.FromKey(Key.V);
        Assert.True(v.ConflictsWith(new Hotkey(ModifierKeys.Control, Key.V)));
        Assert.False(v.ConflictsWith(new Hotkey(ModifierKeys.Control, Key.B)));
        Assert.False(PushToTalkKey.FromMouse(PttMouseButton.XButton1).ConflictsWith(new Hotkey(ModifierKeys.Control, Key.V)));
    }
}
