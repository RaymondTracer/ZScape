using System;
using System.Collections.Generic;
using Avalonia.Threading;
using static SDL2.SDL;

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
    Accept,   // A button (SDL: SDL_CONTROLLER_BUTTON_A)
    Back,     // B button (SDL: SDL_CONTROLLER_BUTTON_B)
    Secondary // X button (SDL: SDL_CONTROLLER_BUTTON_X)
}

/// <summary>
/// Polls SDL2 game controllers on a dispatch timer and fires edge-triggered
/// events for directional navigation and action buttons. Works identically
/// on Windows, Linux, and macOS via ppy.SDL2-CS.
/// Safe to call on systems without a connected controller — polling is a no-op.
/// </summary>
public class GameControllerService : IDisposable
{
    private static readonly Lazy<GameControllerService> _instance = new(() => new GameControllerService());
    public static GameControllerService Instance => _instance.Value;

    private readonly DispatcherTimer _pollTimer;
    private readonly HashSet<ControllerDirection> _pressedDirs = [];
    private readonly HashSet<ControllerDirection> _prevDirs = [];
    private readonly HashSet<ControllerAction> _pressedActions = [];
    private readonly HashSet<ControllerAction> _prevActions = [];
    private IntPtr _controllerHandle = IntPtr.Zero;
    private bool _sdlInitialized;
    private bool _isPolling;

    /// <summary>Fired once when a direction is first pressed.</summary>
    public event Action<ControllerDirection>? DirectionPressed;

    /// <summary>Fired once when an action button is first pressed.</summary>
    public event Action<ControllerAction>? ActionPressed;

    /// <summary>True when at least one gamepad is connected and opened.</summary>
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

        try
        {
            if (SDL_Init(SDL_INIT_GAMECONTROLLER) >= 0)
                _sdlInitialized = true;
        }
        catch
        {
            // SDL2 native library not available on this system
        }

        _pollTimer.Start();
    }

    public void StopPolling()
    {
        _isPolling = false;
        _pollTimer.Stop();

        CloseController();
        _pressedDirs.Clear();
        _prevDirs.Clear();
        _pressedActions.Clear();
        _prevActions.Clear();
        IsControllerConnected = false;
    }

    public void Dispose()
    {
        StopPolling();

        if (_sdlInitialized)
        {
            try { SDL_Quit(); } catch { }
            _sdlInitialized = false;
        }
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        if (!_sdlInitialized) return;

        try
        {
            TryOpenController();

            if (_controllerHandle == IntPtr.Zero)
            {
                IsControllerConnected = false;
                return;
            }

            IsControllerConnected = true;

            _pressedDirs.Clear();
            _pressedActions.Clear();

            // D-pad
            if (SDL_GameControllerGetButton(_controllerHandle, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP) != 0)
                _pressedDirs.Add(ControllerDirection.Up);
            if (SDL_GameControllerGetButton(_controllerHandle, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN) != 0)
                _pressedDirs.Add(ControllerDirection.Down);
            if (SDL_GameControllerGetButton(_controllerHandle, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT) != 0)
                _pressedDirs.Add(ControllerDirection.Left);
            if (SDL_GameControllerGetButton(_controllerHandle, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT) != 0)
                _pressedDirs.Add(ControllerDirection.Right);

            // Left thumbstick as directional fallback
            const short thumbDeadZone = 12000; // ~0.37 of the [-32768, 32767] range
            var stickX = SDL_GameControllerGetAxis(_controllerHandle, SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX);
            var stickY = SDL_GameControllerGetAxis(_controllerHandle, SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY);

            if (stickY < -thumbDeadZone)
                _pressedDirs.Add(ControllerDirection.Up);
            if (stickY > thumbDeadZone)
                _pressedDirs.Add(ControllerDirection.Down);
            if (stickX < -thumbDeadZone)
                _pressedDirs.Add(ControllerDirection.Left);
            if (stickX > thumbDeadZone)
                _pressedDirs.Add(ControllerDirection.Right);

            // Action buttons
            if (SDL_GameControllerGetButton(_controllerHandle, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A) != 0)
                _pressedActions.Add(ControllerAction.Accept);
            if (SDL_GameControllerGetButton(_controllerHandle, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B) != 0)
                _pressedActions.Add(ControllerAction.Back);
            if (SDL_GameControllerGetButton(_controllerHandle, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_X) != 0)
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
            CloseController();
            IsControllerConnected = false;
        }
    }

    private void TryOpenController()
    {
        if (_controllerHandle != IntPtr.Zero) return;

        var count = SDL_NumJoysticks();
        for (int i = 0; i < count; i++)
        {
            if (SDL_IsGameController(i) == SDL_bool.SDL_TRUE)
            {
                var handle = SDL_GameControllerOpen(i);
                if (handle != IntPtr.Zero)
                {
                    _controllerHandle = handle;
                    return;
                }
            }
        }
    }

    private void CloseController()
    {
        if (_controllerHandle != IntPtr.Zero)
        {
            SDL_GameControllerClose(_controllerHandle);
            _controllerHandle = IntPtr.Zero;
        }
    }
}
