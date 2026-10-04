using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZScape.Controls;
using ZScape.Models;
using ZScape.Services;
using ZScape.Utilities;
using ZScape.Views;

internal static class SettingsPreviewChecks
{
    public static void Run()
    {
        using var fixture = new TestDirectory();
        var settings = new SettingsService(fixture.Path);
        settings.Settings.ServerListRowHeight = 26;
        settings.Save();
        var domains = new DomainThreadConfig(settings.DomainThreadSettings, () => settings.Settings, settings.SaveDomainSettings);
        var presentation = new ServerRowHeightPresentation(() => settings.Settings.ServerListRowHeight);
        var rows = new ObservableCollection<ServerViewModel>
        {
            new(new ServerInfo { Name = "Original row" }, new FavoriteMatchResult(FavoriteMatchKind.None),
                getRowHeight: () => presentation.RowHeight)
        };
        var list = new ResizableListView { RowHeightPath = nameof(ServerViewModel.RowHeight) };
        list.AddColumn(new ListViewColumn { Key = "name", Header = "Name", BindingPath = nameof(ServerViewModel.Name), Width = 250 });
        list.Build(ListViewOverflowMode.AutoScroll);
        list.ItemsSource = rows;
        var owner = new Window { Content = list, Width = 500, Height = 300 };
        var openDialogs = new List<UnifiedSettingsDialog>();
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        void UpdateRows() => presentation.Apply(list, rows);
        void CheckHeight(int expected, string message)
        {
            owner.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            RegressionAssert.That(presentation.RowHeight == expected && rows.All(row => row.RowHeight == expected) && list.RowHeight == expected, message);
            var realizedRow = list.GetVisualDescendants().OfType<Border>().First(border =>
                border.Classes.Contains("resizableListViewRow"));
            RegressionAssert.That(Math.Abs(realizedRow.Height - expected) < 0.01, message + " in the realized row");
        }
        UnifiedSettingsDialog Open(out Task<bool> result)
        {
            var dialog = new UnifiedSettingsDialog(settings, domains);
            openDialogs.Add(dialog);
            result = dialog.ShowDialog<bool>(owner);
            Dispatcher.UIThread.RunJobs();
            return dialog;
        }
        void SetHeight(UnifiedSettingsDialog dialog, int height)
        {
            dialog.FindControl<NumericSpinner>("RowHeightNumeric")!.Value = height;
            Dispatcher.UIThread.RunJobs();
        }
        try
        {
            UpdateRows();
            CheckHeight(26, "Initial presentation uses accepted row height");

            var cancelDialog = Open(out var cancelResult);
            using (presentation.Bind(cancelDialog, UpdateRows))
            {
                SetHeight(cancelDialog, 44);
                CheckHeight(44, "A row-height edit previews through the shared presentation");
                RegressionAssert.That(settings.Settings.ServerListRowHeight == 26, "Preview leaves the live accepted setting unchanged");
                settings.Settings.SearchText = "unrelated change";
                settings.Save();
                var persisted = new SettingsService(fixture.Path).Settings;
                RegressionAssert.That(persisted.ServerListRowHeight == 26 && persisted.SearchText == "unrelated change",
                    "An unrelated settings save persists accepted height rather than the preview draft");
                rows.Add(new ServerViewModel(new ServerInfo { Name = "Added during preview" },
                    new FavoriteMatchResult(FavoriteMatchKind.None), getRowHeight: () => presentation.RowHeight));
                RegressionAssert.That(rows[1].RowHeight == 44, "Rows added during refresh share the same active preview");
                var cancel = cancelDialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel"));
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                RegressionAssert.That(cancelResult.IsCompletedSuccessfully && !cancelResult.Result && !cancelDialog.SettingsChanged,
                    "Cancel closes without accepting settings");
                CheckHeight(26, "Cancel restores accepted presentation");
                SetHeight(cancelDialog, 50);
                CheckHeight(26, "Closed dialogs no longer receive preview ownership");
            }

            var escapeDialog = Open(out var escapeResult);
            using (presentation.Bind(escapeDialog, UpdateRows))
            {
                SetHeight(escapeDialog, 35);
                escapeDialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Dispatcher.UIThread.RunJobs();
                RegressionAssert.That(escapeResult.IsCompletedSuccessfully && !escapeResult.Result, "Escape follows the cancellation path");
                CheckHeight(26, "Escape clears the presentation draft");
            }

            var closeDialog = Open(out var closeResult);
            using (presentation.Bind(closeDialog, UpdateRows))
            {
                SetHeight(closeDialog, 38);
                closeDialog.Close();
                Dispatcher.UIThread.RunJobs();
                RegressionAssert.That(closeResult.IsCompletedSuccessfully && !closeDialog.SettingsChanged,
                    "Window-close cancellation does not accept settings");
                CheckHeight(26, "Window close clears the presentation draft");
            }

            var acceptedDialog = Open(out var acceptedResult);
            using (presentation.Bind(acceptedDialog, UpdateRows))
            {
                SetHeight(acceptedDialog, 42);
                acceptedDialog.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                RegressionAssert.That(acceptedResult.IsCompletedSuccessfully && acceptedResult.Result && acceptedDialog.SettingsChanged,
                    "OK accepts the edited settings");
                RegressionAssert.That(settings.Settings.ServerListRowHeight == 42 && new SettingsService(fixture.Path).Settings.ServerListRowHeight == 42,
                    "Accepted row height is persisted by the settings dialog");
                CheckHeight(42, "Closing accepted settings keeps the committed presentation");
            }

            var exceptionalDialog = Open(out _);
            var exceptionalBinding = presentation.Bind(exceptionalDialog, UpdateRows);
            SetHeight(exceptionalDialog, 49);
            exceptionalBinding.Dispose();
            CheckHeight(42, "Disposing a dialog scope clears the preview even before a normal close");
            SetHeight(exceptionalDialog, 55);
            CheckHeight(42, "Disposing a scope removes its preview subscription");
            exceptionalDialog.Close();

            var currentSettingsDialog = Open(out _);
            using (presentation.Bind(currentSettingsDialog, UpdateRows))
            {
                SetHeight(currentSettingsDialog, 54);
                settings.Settings.ServerListRowHeight = 45;
                settings.Save();
                currentSettingsDialog.Close();
                Dispatcher.UIThread.RunJobs();
                CheckHeight(45, "Cancellation restores the latest accepted height rather than a stale snapshot");
                RegressionAssert.That(new SettingsService(fixture.Path).Settings.ServerListRowHeight == 45,
                    "Cancelling a preview does not overwrite a separately accepted settings change");
            }
        }
        finally
        {
            foreach (var dialog in openDialogs) dialog.Close();
            owner.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
