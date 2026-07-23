using ZScape.Models;
using ZScape.Services;

namespace ZScape.Utilities;

/// <summary>
/// Favorite and hidden server rule evaluation helpers.
/// </summary>
public static class ServerRuleUtility
{
    public static FavoriteMatchResult GetFavoriteMatch(AppSettings settings, ServerInfo server)
    {
        var address = GetServerAddress(server);
        if (settings.FavoriteServers.Contains(address))
        {
            return new FavoriteMatchResult(FavoriteMatchKind.ExplicitAddress);
        }

        return TextMatchUtility.MatchesAny(GetComparableServerName(server), settings.FavoriteServerNameRules)
            ? new FavoriteMatchResult(FavoriteMatchKind.NameRule)
            : new FavoriteMatchResult(FavoriteMatchKind.None);
    }

    public static bool IsHiddenByRule(AppSettings settings, ServerInfo server)
    {
        return TextMatchUtility.MatchesAny(GetComparableServerName(server), settings.HiddenServerNameRules);
    }

    public static string GetServerAddress(ServerInfo server)
    {
        return $"{server.Address}:{server.Port}";
    }

    public static string GetComparableServerName(ServerInfo server)
    {
        return DoomColorCodes.StripColorCodes(server.Name ?? string.Empty);
    }
}

/// <summary>
/// Distinguishes explicit address favorites from rule-based favorites.
/// </summary>
public enum FavoriteMatchKind
{
    None = 0,
    ExplicitAddress = 1,
    NameRule = 2
}

/// <summary>
/// Favorite match result used by the UI and refresh logic.
/// </summary>
public readonly record struct FavoriteMatchResult(FavoriteMatchKind Kind)
{
    public bool IsFavorite => Kind != FavoriteMatchKind.None;

    public bool IsExplicitAddressFavorite => Kind == FavoriteMatchKind.ExplicitAddress;

    public bool IsRuleFavorite => Kind == FavoriteMatchKind.NameRule;
}