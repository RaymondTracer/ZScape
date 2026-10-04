using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using ZScape.Controls;
using ZScape.Services;

internal static class WadPathPresentationChecks
{
    public static void Run()
    {
        using var fixture = new TestDirectory();
        var rawPath = fixture.GetPath("unavailable");
        var response = NewResponse();
        var owner = new Window();
        var list = new ListBox { ItemsSource = new[] { rawPath } };
        using var presenter = new WadPathListPresenter(owner, list, path => path, (_, _) => response.Task);
        var row = presenter.CreateRow(rawPath);
        owner.Content = row.Content;
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        RegressionAssert.That(row.Status.Text == "[Checking…]", "Unavailable-root checks start with a visible pending status");
        var heartbeat = false;
        Dispatcher.UIThread.Post(() => heartbeat = true);
        Dispatcher.UIThread.RunJobs();
        RegressionAssert.That(heartbeat && !response.Task.IsCompleted,
            "The settings UI continues processing input while path availability is unresolved");
        response.SetResult(false);
        DrainUntil(() => row.Status.Text == "[Unavailable]");
        RegressionAssert.That(list.ItemsSource!.Cast<string>().Single() == rawPath && row.Path == rawPath,
            "Unavailable labels never alter the underlying editable/saved path");

        response = NewResponse();
        var retry = presenter.RefreshAsync(row);
        response.SetResult(true);
        DrainUntil(() => retry.IsCompleted);
        RegressionAssert.That(row.Status.Text == string.Empty, "A reconnected directory loses its Unavailable label on the next check");

        response = NewResponse();
        var closingCheck = presenter.RefreshAsync(row);
        var statusBeforeClose = row.Status.Text;
        owner.Close();
        response.SetResult(false);
        DrainUntil(() => closingCheck.IsCompleted);
        RegressionAssert.That(row.Status.Text == statusBeforeClose,
            "Closing Settings prevents a late availability result from changing its rows");
        RegressionAssert.That(!WadManager.IsSearchPathAvailableAsync(rawPath, CancellationToken.None).GetAwaiter().GetResult(),
            "Real missing directories are reported unavailable without changing configured roots");
    }

    private static TaskCompletionSource<bool> NewResponse() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void DrainUntil(Func<bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed() && timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        RegressionAssert.That(completed(), "Background path presentation completion reached the UI");
    }
}
