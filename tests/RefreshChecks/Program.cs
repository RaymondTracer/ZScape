using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZScape.Controls;
using ZScape.Services;

internal static class Program
{
    private sealed record Row(string Key, string Text);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    [STAThread]
    private static void Main()
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        Application.Current!.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        var list = new ResizableListView();
        list.AddColumn(new ListViewColumn { Key = "text", Header = "Text", BindingPath = "Text", Width = 250 });
        list.Build(ListViewOverflowMode.AutoScroll);
        var rows = new ObservableCollection<Row>();
        list.ItemsSource = rows;
        var a = new Row("a", "original");
        var b = new Row("b", "second");
        list.UpdateRows(rows, new[] { a, b }, r => r.Key);
        list.SelectItem(a);
        int selectionEvents = 0, resets = 0;
        list.SelectionChanged += (_, _) => selectionEvents++;
        rows.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
        list.UpdateRows(rows, new[] { b, a }, r => r.Key);
        Check(ReferenceEquals(list.SelectedItem, a) && selectionEvents == 0 && resets == 0, "Reordering retains selection without reset or selection events");
        var updated = new Row("a", "updated");
        list.UpdateRows(rows, new[] { updated, b }, r => r.Key);
        Check(ReferenceEquals(list.SelectedItem, updated), "Selection follows a replacement model with the same key");
        list.UpdateRows(rows, new[] { b }, r => r.Key);
        Check(list.SelectedItem == null, "Removing the selected key clears selection");
        var first = new Row("duplicate", "first");
        var second = new Row("duplicate", "second");
        list.UpdateRows(rows, new[] { first, second }, r => r.Key);
        list.SelectItem(second);
        var nextSecond = new Row("duplicate", "next second");
        list.UpdateRows(rows, new[] { first, nextSecond }, r => r.Key);
        Check(ReferenceEquals(list.SelectedItem, nextSecond), "Duplicate keys retain their occurrence identity");

        var menu = new ContextMenu { ItemsSource = new[] { new MenuItem { Header = "Action" } } };
        list.ContextMenu = menu;
        var window = new Window { Content = list, Width = 500, Height = 300 };
        window.Show();
        menu.Open(list);
        Check(menu.IsOpen, "Headless context menu opened");
        list.UpdateRows(rows, new[] { a }, r => r.Key);
        list.UpdateRows(rows, new[] { b }, r => r.Key);
        Check(rows.Count == 2, "Open menu defers row replacement");
        menu.Close();
        Dispatcher.UIThread.RunJobs();
        Check(rows.Count == 1 && ReferenceEquals(rows[0], b), "Closing menu applies only the latest snapshot");
        menu.Open(list);
        list.UpdateRows(rows, new[] { a }, r => r.Key);
        list.ResetRows(rows);
        Dispatcher.UIThread.RunJobs();
        Check(rows.Count == 0 && list.SelectedItem == null, "Explicit reset discards deferred snapshots");
        var many = Enumerable.Range(0, 100).Select(i => new Row(i.ToString(), "Row " + i)).ToList();
        list.UpdateRows(rows, many, r => r.Key);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>()
            .First(s => s.Extent.Height > s.Viewport.Height);
        scroll.Offset = scroll.Offset.WithY(20 * list.RowHeight);
        window.UpdateLayout();
        var previousOffset = scroll.Offset.Y;
        many.Insert(0, new Row("inserted", "New row"));
        list.UpdateRows(rows, many, r => r.Key);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(Math.Abs(scroll.Offset.Y - previousOffset - list.RowHeight) < 1,
            "Inserting above the viewport preserves the visible row anchor");
        window.Close();

        var batch = new ServerUpdateBatch();
        Parallel.For(0, 1000, i => batch.Add("server:" + i));
        batch.MembershipChanged();
        var result = batch.Take();
        Check(result.Addresses.Count == 1000 && result.MembershipChanged, "Concurrent endpoint updates are retained");
        Check(batch.Take().Addresses.Count == 0, "Taking a batch drains it");
        Console.WriteLine("All refresh regression checks passed.");
    }
}
