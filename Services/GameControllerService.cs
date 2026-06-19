using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace ZScape.Services;

/// <summary>
/// Directional navigation commands generated from controller input.
/// </summary>
public enum ControllerDirection
{
    Up,
    Down,
    Left,
    Right
}

/// <summary>
/// Action button commands generated from controller input.
/// </summary>
public enum ControllerAction
{
    Accept,   // A button
    Back,     // B button
    Secondary // X button
}

/// <summary>
/// Polls Windows.Gaming.Input.Gamepad on a dispatch timer and fires edge-triggered
/// events for directional navigation and action buttons. Safe to call on systems
/// without a connected controller — polling silently becomes a no-op.
/// </summary>
public class GameControllerService
{
    private static readonly Lazy<GameControllerService> _instance = new(() => new GameControllerService());
    public static GameControllerService Instance => _instance.Value;

    private readonly DispatcherTimer _pollTimer;
    private readonly HashSet<ControllerDirection> _pressedDirs = [];
    private readonly HashSet<ControllerDirection> _prevDirs = [];
    private readonly HashSet<ControllerAction> _pressedActions = [];
    private readonly HashSet<ControllerAction> _prevActions = [];
    private bool _isPolling;

    /// <summary>Fired once when a direction is first pressed.</summary>
    public event Action<ControllerDirection>? DirectionPressed;

    /// <summary>Fired once when an action button is first pressed.</summary>
    public event Action<ControllerAction>? ActionPressed;

    /// <summary>True when at least one gamepad is connected.</summary>
    public bool IsControllerConnected { get; private set; }

    private GameControllerService()
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _pollTimer.Tick += OnPoll;
    }

    public void StartPolling()
    {
        if (_isPolling) return;
        _isPolling = true;
        _pollTimer.Start();
    }

    public void StopPolling()
    {
        _isPolling = false;
        _pollTimer.Stop();
        _pressedDirs.Clear();
        _prevDirs.Clear();
        _pressedActions.Clear();
        _prevActions.Clear();
        IsControllerConnected = false;
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        try
        {
            var gamepads = Windows.Gaming.Input.Gamepad.Gamepads;
            if (gamepads.Count == 0)
            {
                IsControllerConnected = false;
                return;
            }

            IsControllerConnected = true;
            var reading = gamepads[0].GetCurrentReading();
            var buttons = reading.Buttons;

            _pressedDirs.Clear();
            _pressedActions.Clear();

            // D-pad
            if (buttons.HasFlag(Windows.Gaming.Input.GamepadButtons.DPadUp))
                _pressedDirs.Add(ControllerDirection.Up);
            if (buttons.HasFlag(Windows.Gaming.Input.GamepadButtons.DPadDown))
                _pressedDirs.Add(ControllerDirection.Down);
            if (buttons.HasFlag(Windows.Gaming.Input.GamepadButtons.DPadLeft))
                _pressedDirs.Add(ControllerDirection.Left);
            if (buttons.HasFlag(Windows.Gaming.Input.GamepadButtons.DPadRight))
                _pressedDirs.Add(ControllerDirection.Right);

            // Left thumbstick as directional fallback
            const double thumbDeadZone = 0.4;
            if (reading.LeftThumbstickY > thumbDeadZone)
                _pressedDirs.Add(ControllerDirection.Up);
            if (reading.LeftThumbstickY < -thumbDeadZone)
                _pressedDirs.Add(ControllerDirection.Down);
            if (reading.LeftThumbstickX < -thumbDeadZone)
                _pressedDirs.Add(ControllerDirection.Left);
            if (reading.LeftThumbstickX > thumbDeadZone)
                _pressedDirs.Add(ControllerDirection.Right);

            // Action buttons
            if (buttons.HasFlag(Windows.Gaming.Input.GamepadButtons.A))
                _pressedActions.Add(ControllerAction.Accept);
            if (buttons.HasFlag(Windows.Gaming.Input.GamepadButtons.B))
                _pressedActions.Add(ControllerAction.Back);
            if (buttons.HasFlag(Windows.Gaming.Input.GamepadButtons.X))
                _pressedActions.Add(ControllerAction.Secondary);

            // Edge-trigger: fire only on newly pressed
            foreach (var dir in _pressedDirs)
                if (!_prevDirs.Contains(dir))
                    DirectionPressed?.Invoke(dir);

            foreach (var act in _pressedActions)
                if (!_prevActions.Contains(act))
                    ActionPressed?.Invoke(act);

            // Swap buffers
            _prevDirs.Clear();
            foreach (var d in _pressedDirs) _prevDirs.Add(d);
            _prevActions.Clear();
            foreach (var a in _pressedActions) _prevActions.Add(a);
        }
        catch
        {
            // Gamepad access throws on systems without Windows.Gaming.Input support
            IsControllerConnected = false;
        }
    }
}
