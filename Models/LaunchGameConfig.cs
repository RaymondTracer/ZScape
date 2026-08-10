using Avalonia.Media;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ZScape.Models;

/// <summary>
/// Serializable state of the Launch Game dialog, used for persisting last-used
/// settings and saving/loading named configurations. Passwords are never persisted.
/// </summary>
public class LaunchGameConfig
{
    public bool IsHostMode { get; set; }
    public bool IsDedicated { get; set; }
    /// <summary>Full path to the Zandronum executable, or null for stable.</summary>
    public string? ExePath { get; set; }
    public string? IwadPath { get; set; }
    /// <summary>
    /// Legacy PWAD path list retained for compatibility with older settings
    /// files. New configurations use <see cref="PwadEntries"/> so disabled and
    /// host-optional files can be persisted.
    /// </summary>
    public List<string> PwadPaths { get; set; } = [];
    public List<LaunchPwadConfig> PwadEntries { get; set; } = [];
    public string? Map { get; set; }
    public int Skill { get; set; } = 3;
    public int MaxPlayers { get; set; } = 8;
    public int MaxClients { get; set; } = 32;
    public string? ServerName { get; set; }
}

/// <summary>
/// Persisted PWAD load-order entry for the Launch Game dialog.
/// </summary>
public class LaunchPwadConfig
{
    public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool Optional { get; set; }
}

/// <summary>
/// Editable PWAD entry used by the Launch Game dialog. It deliberately keeps
/// missing paths visible so a bad load-order entry can be disabled or repaired.
/// </summary>
public sealed class LaunchPwadEntry : INotifyPropertyChanged
{
    private string _path = string.Empty;
    private bool _enabled = true;
    private bool _optional;

    public LaunchPwadEntry() { }

    public LaunchPwadEntry(string path, bool enabled = true, bool optional = false)
    {
        _path = path;
        _enabled = enabled;
        _optional = optional;
    }

    public string Path
    {
        get => _path;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_path, normalized, StringComparison.Ordinal))
                return;

            _path = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(StatusForeground));
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;

            _enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDisplay));
        }
    }

    public bool Optional
    {
        get => _optional;
        set
        {
            if (_optional == value)
                return;

            _optional = value;
            OnPropertyChanged();
        }
    }

    public string FileName => string.IsNullOrWhiteSpace(Path)
        ? "(empty path)"
        : System.IO.Path.GetFileName(Path);

    public bool Exists => !string.IsNullOrWhiteSpace(Path) && File.Exists(Path);

    public string StatusDisplay => !Exists
        ? "Missing"
        : Enabled ? "Ready" : "Disabled";

    public IBrush StatusForeground => !Exists
        ? Brushes.IndianRed
        : Enabled ? Brushes.LightGreen : Brushes.Gray;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// A named, saved launch configuration.
/// </summary>
public class NamedLaunchGameConfig
{
    public string Name { get; set; } = string.Empty;
    public LaunchGameConfig Config { get; set; } = new();
}
