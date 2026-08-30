namespace ZScape.Models;

/// <summary>
/// Controls the optional detail markers shown after the concise player-column
/// capacity. The base display is always occupied client slots / max clients.
/// </summary>
public sealed class PlayerColumnDetails
{
    /// <summary>
    /// Shows the <c>c</c> suffix after the base connected-client capacity.
    /// </summary>
    public bool ShowClientCapacityMarker { get; set; }

    /// <summary>
    /// Shows the active playing count and game player limit, marked with <c>p</c>.
    /// </summary>
    public bool ShowPlayingCapacity { get; set; }

    /// <summary>
    /// Shows active bots, marked with <c>b</c>.
    /// </summary>
    public bool ShowBots { get; set; }

    /// <summary>
    /// Shows connected spectators, marked with <c>s</c>.
    /// </summary>
    public bool ShowSpectators { get; set; }
}
