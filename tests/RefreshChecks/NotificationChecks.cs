using System.Net;
using ZScape.Models;
using ZScape.Services;

internal static class NotificationChecks
{
    public static void Run()
    {
        var server = CreateServer("Alpha", 10);
        var request = NotificationService.CreateSingleNativeNotificationRequest(
            server, ServerAlertType.Favorite, isTestAlert: true);
        RegressionAssert.That(request.Title == "Favorite server online" && request.Message == "Alpha" &&
            request.Detail == "3/8 players on MAP01 (Cooperative)",
            "Single-server notification content preserves its title, server name and player/map detail");
        RegressionAssert.That(request.Actions.SequenceEqual(new[]
        {
            new AlertActionDefinition("Connect", ServerAlertAction.Connect, IsPrimary: true),
            new AlertActionDefinition("Show Server", ServerAlertAction.ShowServer, IsPrimary: false)
        }) && request.DefaultAction == ServerAlertAction.ShowServer,
            "All presenters receive the same ordered single-server actions and default action");

        var bodyArguments = request.GetActivationArguments();
        var connectArguments = request.GetActivationArguments(ServerAlertAction.Connect);
        RegressionAssert.That(bodyArguments.Count == 4 && bodyArguments["action"] == "ShowServer" &&
            connectArguments["action"] == "Connect" && connectArguments["serverAddress"] == "192.0.2.10:10666" &&
            connectArguments["alertType"] == "Favorite" && connectArguments["test"] == "true",
            "Native notification bodies and buttons retain the shared target, alert type and test flag");

        server.EndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.99"), 10667);
        server.Name = "Renamed";
        var activation = request.CreateEventArgs(ServerAlertAction.Connect);
        RegressionAssert.That(ReferenceEquals(activation.Server, server) &&
            activation.ServerAddress == "192.0.2.10:10666" && activation.AlertType == ServerAlertType.Favorite &&
            activation.Action == ServerAlertAction.Connect && activation.IsTestAlert && request.Message == "Alpha",
            "Custom activation preserves the notification's original target and preview flag when the source model changes");

        NativeNotificationRequest? nativeSeen = null;
        NativeNotificationRequest? fallbackSeen = null;
        NotificationService.PresentNotification(request,
            candidate => { nativeSeen = candidate; return false; },
            candidate => fallbackSeen = candidate);
        RegressionAssert.That(ReferenceEquals(nativeSeen, request) && ReferenceEquals(fallbackSeen, request),
            "Native presentation failure passes the identical content and action context into custom fallback");
        var fallbackCalls = 0;
        NotificationService.PresentNotification(request, _ => true, _ => fallbackCalls++);
        RegressionAssert.That(fallbackCalls == 0,
            "Successful native presentation does not create a duplicate custom notification");

        var manual = NotificationService.CreateSingleNativeNotificationRequest(
            server, ServerAlertType.Manual, isTestAlert: false);
        RegressionAssert.That(manual.Title == "Manual server online" && !manual.IsTestAlert &&
            manual.GetActivationArguments()["test"] == "false" &&
            manual.CreateEventArgs(ServerAlertAction.ShowServer).AlertType == ServerAlertType.Manual,
            "Live manual alerts retain their own title and activation metadata without the preview flag");

        var batch = NotificationService.CreateMultipleNativeNotificationRequest(new[]
        {
            (CreateServer("Alpha", 11), ServerAlertType.Favorite),
            (CreateServer("alpha", 12), ServerAlertType.Manual),
            (CreateServer("Beta", 13), ServerAlertType.Favorite),
            (CreateServer("Gamma", 14), ServerAlertType.Manual),
            (CreateServer("Delta", 15), ServerAlertType.Favorite)
        }, isTestAlert: true);
        RegressionAssert.That(batch.Title == "Servers online" &&
            batch.Message == "5 favorite or manual servers are online" &&
            batch.Detail == "Alpha, Beta, Gamma, and 1 more",
            "Batch content retains server counts and the existing case-insensitive three-name summary");
        RegressionAssert.That(batch.Actions.SequenceEqual(new[]
        {
            new AlertActionDefinition("Show Window", ServerAlertAction.FocusWindow, IsPrimary: true)
        }) && batch.DefaultAction == ServerAlertAction.FocusWindow,
            "Batch notifications retain the focus-window action for every capable presenter");
        var batchArguments = batch.GetActivationArguments();
        var batchActivation = batch.CreateEventArgs(ServerAlertAction.FocusWindow);
        RegressionAssert.That(batchArguments.Count == 2 && batchArguments["action"] == "FocusWindow" &&
            batchArguments["test"] == "true" && batchActivation.Server is null &&
            batchActivation.ServerAddress is null && batchActivation.AlertType is null && batchActivation.IsTestAlert,
            "Batch preview activation has no single-server target and retains its test flag");
    }

    private static ServerInfo CreateServer(string name, byte addressSuffix) => new()
    {
        EndPoint = new IPEndPoint(IPAddress.Parse($"192.0.2.{addressSuffix}"), 10666),
        Name = name,
        CurrentPlayers = 3,
        MaxClients = 8,
        Map = "MAP01",
        GameMode = GameMode.FromType(GameModeType.Cooperative)
    };
}
