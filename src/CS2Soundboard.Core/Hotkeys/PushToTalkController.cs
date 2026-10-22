using System;

namespace CS2Soundboard.Core.Hotkeys;

/// <summary>Sends synthetic key/button presses for the push-to-talk key.</summary>
public interface IPushToTalkInjector
{
    void Press(PushToTalkKey key);
    void Release(PushToTalkKey key);
}

/// <summary>
/// Holds the game's push-to-talk key while soundboard sounds play.
///
/// Lifecycle per sound:  <see cref="Acquire"/>  (wait the pre-delay if it returned true)  play the sound
///                       <see cref="EndPending"/>. A periodic <see cref="Update"/> releases the key once
///                       no sound has been playing for the release delay.
///
/// The user may also press the real push-to-talk key at any time. The input hook reports those physical
/// events through <see cref="OnUserDown"/>/<see cref="OnUserUp"/>, so that:
///  * releasing the real key while a sound plays does not cut the sound off (the release is swallowed and
///    sent later, when the sound ends), and
///  * the key is not released under the user while they are still holding it to talk.
///
/// Thread-safe: called from the UI thread and the input-hook thread.
/// </summary>
public sealed class PushToTalkController
{
    private readonly IPushToTalkInjector _injector;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();

    private PushToTalkKey? _key;
    private bool _holding;      // we keep the key down on behalf of sounds
    private bool _keyDownSent;  // the game currently has a "down" from us that needs an "up"
    private bool _userHolding;  // the user physically holds the key
    private int _pending;       // sounds being started (inside the pre-delay)
    private DateTime? _releaseAt;

    public PushToTalkController(IPushToTalkInjector injector, Func<DateTime>? clock = null)
    {
        _injector = injector;
        _now = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Raised (on the calling thread) when the held state changes.</summary>
    public event Action<bool>? HoldingChanged;

    public PushToTalkKey? Key
    {
        get { lock (_gate) return _key; }
        set
        {
            lock (_gate)
            {
                if (_key == value) return;
                ReleaseCore();
                _key = value;
                _userHolding = false;
            }
        }
    }

    public bool IsHolding { get { lock (_gate) return _holding; } }
    public bool IsUserHolding { get { lock (_gate) return _userHolding; } }

    /// <summary>
    /// Called before a sound starts. Returns true if the key was not active yet, in which case the caller
    /// should wait the pre-delay so the game opens the microphone before the sound begins.
    /// Every call must be matched by <see cref="EndPending"/>.
    /// </summary>
    public bool Acquire()
    {
        bool changed;
        bool newlyPressed = false;
        lock (_gate)
        {
            _pending++;
            _releaseAt = null;
            if (_key is not { } key || _holding)
                return false;
            _holding = true;
            changed = true;
            if (!_userHolding)
            {
                _injector.Press(key);
                _keyDownSent = true;
                newlyPressed = true;
            }
        }
        if (changed) HoldingChanged?.Invoke(true);
        return newlyPressed;
    }

    /// <summary>The sound has been started (or failed to start).</summary>
    public void EndPending()
    {
        lock (_gate)
        {
            if (_pending > 0) _pending--;
        }
    }

    /// <summary>Periodic tick: releases the key after <paramref name="releaseDelay"/> without any sound.</summary>
    public void Update(bool soundsActive, TimeSpan releaseDelay)
    {
        bool released = false;
        lock (_gate)
        {
            if (!_holding) return;
            if (soundsActive || _pending > 0)
            {
                _releaseAt = null;
                return;
            }
            var now = _now();
            _releaseAt ??= now + releaseDelay;
            if (now >= _releaseAt.Value) released = ReleaseCore();
        }
        if (released) HoldingChanged?.Invoke(false);
    }

    /// <summary>Releases immediately (exit, settings change, emergency).</summary>
    public void ReleaseNow()
    {
        bool released;
        lock (_gate) released = ReleaseCore();
        if (released) HoldingChanged?.Invoke(false);
    }

    private bool ReleaseCore()
    {
        if (!_holding && !_keyDownSent) return false;
        bool wasHolding = _holding;
        _holding = false;
        _releaseAt = null;
        _pending = 0;
        if (_keyDownSent && !_userHolding && _key is { } key)
            _injector.Release(key);
        _keyDownSent = false;
        return wasHolding;
    }

    /// <summary>Physical press of the push-to-talk key. Returns true to swallow the event.</summary>
    public bool OnUserDown()
    {
        lock (_gate)
        {
            _userHolding = true;
            return false; // always let the game see the user's press
        }
    }

    /// <summary>Physical release of the push-to-talk key. Returns true to swallow the event.</summary>
    public bool OnUserUp()
    {
        lock (_gate)
        {
            _userHolding = false;
            if (_holding)
            {
                // A sound is still playing: keep the key down in the game and release it when the sound ends.
                _keyDownSent = true;
                return true;
            }
            return false;
        }
    }
}
