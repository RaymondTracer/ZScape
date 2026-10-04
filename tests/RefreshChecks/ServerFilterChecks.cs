using System.Net;
using ZScape.Models;
using ZScape.Utilities;

internal static class ServerFilterChecks
{
    public static void Run()
    {
        CheckBasicVisibility();
        CheckBotScopesAndLayering();
        CheckLooseSearch();
        CheckAdvancedFilters();
    }

    private static void CheckBasicVisibility()
    {
        var empty = Server();
        empty.CurrentPlayers = 0;
        RegressionAssert.That(Basic(empty) && !Basic(empty, hideEmpty: true) &&
            new ServerFilter { ShowEmpty = true }.Matches(empty) &&
            !new ServerFilter { ShowEmpty = false }.Matches(empty),
            "Toolbar and advanced empty filters share the same empty predicate");

        var full = Server();
        full.CurrentPlayers = full.MaxClients;
        full.MaxPlayers = full.MaxClients + 10;
        RegressionAssert.That(Basic(full) && !Basic(full, hideFull: true) &&
            !new ServerFilter { ShowFull = false }.Matches(full),
            "Full visibility uses connected client capacity, not the playing-slot limit");

        var server = Server();
        RegressionAssert.That(Basic(server, passworded: FilterMode.DontCare) &&
            Basic(server, passworded: FilterMode.Hide) && !Basic(server, passworded: FilterMode.ShowOnly),
            "Password filtering accepts unpassworded servers for DontCare and Hide only");
        server.IsPassworded = true;
        RegressionAssert.That(Basic(server, passworded: FilterMode.DontCare) &&
            !Basic(server, passworded: FilterMode.Hide) && Basic(server, passworded: FilterMode.ShowOnly) &&
            !new ServerFilter { PasswordedServers = FilterMode.Hide }.Matches(server) &&
            new ServerFilter { PasswordedServers = FilterMode.ShowOnly }.Matches(server),
            "Toolbar and advanced password filters use the same hide/show-only rules");

        server.IsOnline = false;
        RegressionAssert.That(!new ServerFilter().Matches(server) &&
            new ServerFilter { ShowUnresponsive = true }.Matches(server),
            "Advanced unresponsive visibility remains a separate policy from basic visibility");
    }

    private static void CheckBotScopesAndLayering()
    {
        var cases = new[]
        {
            (Name: "empty", Count: 0, Players: Array.Empty<PlayerInfo>(), Toolbar: true, Advanced: true),
            (Name: "only bots", Count: 2, Players: new[] { Bot(), Bot() }, Toolbar: false, Advanced: false),
            (Name: "bot and playing human", Count: 2, Players: new[] { Bot(), Human() }, Toolbar: true, Advanced: true),
            (Name: "bot and human spectator", Count: 2, Players: new[] { Bot(), Human(spectator: true) }, Toolbar: true, Advanced: false),
            (Name: "human spectators only", Count: 1, Players: new[] { Human(spectator: true) }, Toolbar: true, Advanced: false),
            (Name: "playing human", Count: 1, Players: new[] { Human() }, Toolbar: true, Advanced: true),
            (Name: "snapshot without player details", Count: 2, Players: Array.Empty<PlayerInfo>(), Toolbar: true, Advanced: false)
        };
        foreach (var test in cases)
        {
            var server = Server();
            server.CurrentPlayers = test.Count;
            server.Players = test.Players.ToList();
            RegressionAssert.That(Basic(server, hideBotOnly: true) == test.Toolbar &&
                new ServerFilter { TreatBotOnlyAsEmpty = true, ShowEmpty = true }.Matches(server) == test.Advanced,
                $"Bot-only scopes retain existing toolbar/advanced behavior for {test.Name}");
        }

        var empty = Server();
        empty.CurrentPlayers = 0;
        var disabled = new ServerFilter { Enabled = false, ShowEmpty = false };
        RegressionAssert.That(disabled.Matches(empty) && !Basic(empty, hideEmpty: true),
            "Disabling an advanced preset does not disable independent toolbar filters");
        RegressionAssert.That(Basic(empty) && !new ServerFilter { ShowEmpty = false }.Matches(empty),
            "An advanced preset still filters rows accepted by the toolbar");
    }

    private static void CheckLooseSearch()
    {
        var server = Server();
        server.Name = "\u001cgGhoul's BYOC v3.0 Beta";
        server.Map = "UNIQUE_MAP42";
        server.IWAD = "freedoom2.wad";
        server.GameMode = GameMode.FromType(GameModeType.TeamDeathmatch);
        server.Country = "NZ";
        server.GameVersion = "3.3-alpha-r260112-1855";
        server.PWADs =
        [
            new PWadInfo { Name = "byocv3.0betatest5.pk3" },
            new PWadInfo { Name = "skulltag_content-3.2-beta2.pk3", IsOptional = true }
        ];

        RegressionAssert.That(ServerFilter.MatchesLooseSearch(server, "BYOCV3.0BETATEST") &&
            ServerFilter.MatchesLooseSearch(server, "skulltag beta2"),
            "Loose server search includes required and optional PWAD filenames with punctuation ignored");
        RegressionAssert.That(ServerFilter.MatchesLooseSearch(server, "ghouls beta") &&
            ServerFilter.MatchesLooseSearch(server, "BYOCV3.0") &&
            !ServerFilter.MatchesLooseSearch(server, "byoc3.0betatest"),
            "Visible names ignore Doom colors/apostrophes, but search does not invent missing filename characters");

        foreach (var query in new[] { "unique map42", "freedoom2", "tdm", "team deathmatch", "NZ", "r260112", "192.0.2.5:10666" })
        {
            RegressionAssert.That(ServerFilter.MatchesLooseSearch(server, query),
                $"Loose search retains the existing server field for query {query}");
        }
        RegressionAssert.That(!ServerFilter.MatchesLooseSearch(server, "ghouls map42") &&
            !ServerFilter.MatchesLooseSearch(server, "betatest skulltag"),
            "Every search token must match one field or one WAD, not a combination of separate fields");
        RegressionAssert.That(ServerFilter.MatchesLooseSearch(server, null) &&
            ServerFilter.MatchesLooseSearch(server, string.Empty) &&
            !ServerFilter.MatchesLooseSearch(server, " ") && !ServerFilter.MatchesLooseSearch(server, "!!!"),
            "Empty search skips filtering, while whitespace/punctuation-only search retains its existing non-match behavior");

        server.IsRefreshPending = true;
        RegressionAssert.That(ServerFilter.MatchesLooseSearch(server, string.Empty) &&
            ServerFilter.MatchesLooseSearch(server, "192.0.2.5") &&
            !ServerFilter.MatchesLooseSearch(server, "ghouls") &&
            !ServerFilter.MatchesLooseSearch(server, "byocv3.0betatest"),
            "Pending-row search exposes only its known endpoint, even when a previous snapshot has names/WADs");

        RegressionAssert.That(TextMatchUtility.IsLooseSearchMatch("Café Ghoul’s", "cafe ghouls") &&
            TextMatchUtility.IsAnyLooseSearchMatch(new[] { "other", "Café Ghoul’s" }, "cafe ghouls") &&
            !TextMatchUtility.IsAnyLooseSearchMatch(new[] { "Café", "Ghoul’s" }, "cafe ghouls"),
            "Single- and multi-field loose matching share accent/token rules without distributing tokens across fields");
        RegressionAssert.That(TextMatchUtility.FindLooseMatchRanges("Ghoul's", "ghouls")
            .SequenceEqual(new[] { new TextMatchRange(0, 7) }),
            "Loose search highlighting retains original-text ranges after apostrophe normalization");
    }

    private static void CheckAdvancedFilters()
    {
        var server = Server();
        server.Name = "\u001cgKnown Server";
        server.Map = "MAP01";
        server.IWAD = "DOOM2.WAD";
        server.GameVersion = "3.3-alpha";
        server.PWADs = [new PWadInfo { Name = "BYOCV3.0BetaTest5.pk3" }, new PWadInfo { Name = "skulltag.pk3" }];
        var filter = new ServerFilter
        {
            RequireWads = ["doom2", "byocv3"],
            IncludeAnyWads = ["missing", "SKULLTAG"],
            ExcludeWads = ["doom1"],
            RequireIWAD = "doom2",
            ServerNameFilter = "Known*",
            ServerNameMatchMode = TextMatchMode.Wildcard,
            MapFilter = "^MAP[0-9]+$",
            MapMatchMode = TextMatchMode.Regex,
            IncludeGameModes = [GameModeType.Cooperative],
            RequireVersion = "ALPHA"
        };
        RegressionAssert.That(filter.Matches(server),
            "Advanced WAD require/include/exclude filters retain case-insensitive IWAD/PWAD substring matching together");
        filter.RequireWads.Add("missing");
        RegressionAssert.That(!filter.Matches(server), "Every required WAD must still be present");
        filter.RequireWads.Remove("missing");
        filter.IncludeAnyWads = ["missing"];
        RegressionAssert.That(!filter.Matches(server), "At least one included WAD must still be present");
        filter.IncludeAnyWads = [];
        filter.ExcludeWads.Add("betaTEST5");
        RegressionAssert.That(!filter.Matches(server), "Any excluded PWAD still hides the server");
        filter.ExcludeWads = ["DOOM2"];
        RegressionAssert.That(!filter.Matches(server), "The IWAD remains part of excluded-WAD matching");
        filter.ExcludeWads = [];
        filter.ServerNameFilter = "[invalid";
        filter.ServerNameMatchMode = TextMatchMode.Regex;
        RegressionAssert.That(!filter.Matches(server), "An invalid advanced regex remains a non-match, not an exception");
    }

    private static bool Basic(ServerInfo server, bool hideEmpty = false, bool hideBotOnly = false,
        bool hideFull = false, FilterMode passworded = FilterMode.DontCare) =>
        ServerFilter.MatchesBasicVisibility(server, hideEmpty, hideBotOnly, hideFull, passworded,
            ServerFilter.BotOnlyScope.ConnectedClients);

    private static ServerInfo Server() => new()
    {
        EndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.5"), 10666),
        Name = "Known server",
        IsOnline = true,
        IsQueried = true,
        CurrentPlayers = 1,
        MaxClients = 16,
        MaxPlayers = 16,
        GameMode = GameMode.FromType(GameModeType.Cooperative),
        Players = [Human()]
    };

    private static PlayerInfo Bot() => new() { IsBot = true };
    private static PlayerInfo Human(bool spectator = false) => new() { IsSpectator = spectator };
}
