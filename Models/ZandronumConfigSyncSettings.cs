namespace ZScape.Models;

/// <summary>
/// Persisted choices for synchronizing Zandronum's per-user configuration
/// files between the versions managed or launched by ZScape.
/// </summary>
public sealed class ZandronumConfigSyncSettings
{
    /// <summary>
    /// When enabled, the configuration belonging to a Zandronum process that
    /// ZScape launched is synchronized after that exact process exits.
    /// </summary>
    public bool AutoSyncEnabled { get; set; }

    /// <summary>
    /// When true, the source INI replaces every target INI as a whole. The
    /// normal, safer mode copies only <see cref="SelectedSections"/>.
    /// </summary>
    public bool SyncWholeFile { get; set; }

    /// <summary>
    /// Case-insensitive INI section names to copy in selective-sync mode.
    /// </summary>
    public List<string> SelectedSections { get; set; } = [];
}
