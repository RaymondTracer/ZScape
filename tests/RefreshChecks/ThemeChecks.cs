using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZScape.Controls;
using ZScape.Services;
using ZScape.Views;

internal static class ThemeChecks
{
    public static void Run()
    {
        var app = Application.Current!;
        var originalVariant = app.RequestedThemeVariant;
        var originalStyleCount = app.Styles.Count;
        var fixture = Path.GetFullPath(Path.Combine("artifacts", "theme-checks-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(fixture);
        var userThemes = Path.Combine(fixture, "Themes");
        Window? window = null;
        try
        {
            var settings = new SettingsService(fixture);
            settings.Settings.ThemeId = "Light";
            settings.Settings.Theme = AppTheme.Dark;
            settings.Save();
            var settingsFile = Path.Combine(fixture, "settings.json");
            var originalJson = File.ReadAllText(settingsFile);
            var themes = new ThemeService(settings, userThemes);
            themes.Initialize();
            Check(themes.ActiveThemeId == "Light" && themes.CurrentTheme == ThemeVariant.Light,
                "Built-in theme identity determines its variant even with a legacy conflicting setting");
            Check(File.ReadAllText(settingsFile) == originalJson, "Initializing appearance does not save settings");

            var primaryBrush = (SolidColorBrush)ThemeService.GetBrush("PrimaryBackgroundBrush", "#000000");
            var originalColor = primaryBrush.Color;
            var selectedBrush = (SolidColorBrush)ThemeService.GetBrush("RowSelectedBrush", "#000000");
            var originalSelectedColor = selectedBrush.Color;
            var list = new ResizableListView();
            list.AddColumn(new ListViewColumn { Key = "name", Header = "Name", BindingPath = "Name", Width = 180 });
            list.Build(ListViewOverflowMode.AutoScroll);
            window = new Window { Content = list, Width = 400, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var appliedWindowBrush = (SolidColorBrush)window.Background!;

            using (var preview = themes.BeginPreview())
            {
                preview.Update(new Dictionary<string, Color>
                {
                    ["PrimaryBackground"] = Color.Parse("#214365"),
                    ["RowSelectedColor"] = Color.Parse("#765432"),
                    ["ThemeChecksTemporaryColor"] = Colors.Fuchsia
                });
                Check(ReferenceEquals(primaryBrush, ThemeService.GetBrush("PrimaryBackgroundBrush", "#000000"))
                    && primaryBrush.Color == Color.Parse("#214365"), "Preview updates existing brush instances");
                Check(appliedWindowBrush.Color == primaryBrush.Color,
                    "Already-open controls reflect preview colors without rebuilding their styling");
                Check(ReferenceEquals(list.SelectedRowBrush, selectedBrush)
                    && selectedBrush.Color == Color.Parse("#765432"), "Shared list selection follows the owned live palette");
                Check(themes.ActiveThemeId == "Light" && settings.Settings.ThemeId == "Light"
                    && File.ReadAllText(settingsFile) == originalJson, "Preview changes neither accepted identity nor persisted settings");
            }
            Check(primaryBrush.Color == originalColor && selectedBrush.Color == originalSelectedColor,
                "Cancelling preview restores existing brush colors");
            Check(!app.TryGetResource("ThemeChecksTemporaryColor", app.ActualThemeVariant, out _),
                "Cancelling removes resources introduced only by the preview");

            using (var preview = themes.BeginPreview())
            {
                preview.Update(new Dictionary<string, Color> { ["PrimaryBackground"] = Color.Parse("#112233") });
                preview.Save("Custom Check");
                preview.Update(new Dictionary<string, Color> { ["PrimaryBackground"] = Colors.Red });
            }
            Check(themes.ActiveThemeId == "Custom Check" && themes.CurrentTheme == ThemeVariant.Light
                && primaryBrush.Color == Color.Parse("#112233"), "Closing after Save retains the saved custom theme and cancels only later edits");
            var savedTheme = Path.Combine(userThemes, "Custom Check.axaml");
            Check(File.ReadAllText(savedTheme).Contains("ZScapeBaseTheme"), "Saved custom palettes record their base variant");
            var acceptedJson = File.ReadAllText(settingsFile);

            var externalTheme = Path.Combine(fixture, "import.axaml");
            File.WriteAllText(externalTheme, """
                <ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <x:String x:Key="ZScapeBaseTheme">Dark</x:String>
                    <Color x:Key="PrimaryBackground">#445566</Color>
                    <Color x:Key="AccentColor">#AA1122</Color>
                </ResourceDictionary>
                """);
            using (var preview = themes.BeginPreview())
            {
                preview.Load(externalTheme);
                Check(themes.CurrentTheme == ThemeVariant.Dark && preview.Colors["PrimaryBackground"] == Color.Parse("#445566"),
                    "Imported files load their own palette and base variant");
                Check(((SolidColorBrush)ThemeService.GetBrush("AccentBrush", "#000000")).Color == Color.Parse("#AA1122"),
                    "Imported color changes use the existing canonical brush mapping");
                Check(File.ReadAllText(settingsFile) == acceptedJson, "Loading a theme in the editor remains a preview");
            }
            Check(themes.ActiveThemeId == "Custom Check" && themes.CurrentTheme == ThemeVariant.Light
                && primaryBrush.Color == Color.Parse("#112233"), "Cancelling an import restores custom identity, base and colors");

            var editor = new ThemeEditorDialog(themes);
            try
            {
                editor.Show();
                Dispatcher.UIThread.RunJobs();
                Check(editor.FindControl<TextBox>("ThemeNameBox")!.Text == "Custom Check",
                    "The editor starts from the accepted custom theme");
                var colorRow = editor.GetVisualDescendants().OfType<StackPanel>().First(panel =>
                    panel.Children.OfType<TextBlock>().Any(label => label.Text == "PrimaryBackground"));
                colorRow.Children.OfType<TextBox>().Single().Text = "#789ABC";
                Dispatcher.UIThread.RunJobs();
                Check(primaryBrush.Color == Color.Parse("#789ABC"), "Editing a color uses the shared live preview");
            }
            finally
            {
                editor.Close();
                Dispatcher.UIThread.RunJobs();
            }
            Check(themes.ActiveThemeId == "Custom Check" && primaryBrush.Color == Color.Parse("#112233")
                && File.ReadAllText(settingsFile) == acceptedJson, "Closing the actual editor restores appearance without writing settings");

            var invalidTheme = Path.Combine(fixture, "invalid.axaml");
            File.WriteAllText(invalidTheme, "<ResourceDictionary><Color>not a palette</Color></ResourceDictionary>");
            using (var preview = themes.BeginPreview())
            {
                var rejected = false;
                try { preview.Load(invalidTheme); } catch (FormatException) { rejected = true; }
                Check(rejected && themes.ActiveThemeId == "Custom Check" && primaryBrush.Color == Color.Parse("#112233"),
                    "Invalid imports leave the current preview intact");
                rejected = false;
                try { preview.Save("../invalid"); } catch (ArgumentException) { rejected = true; }
                Check(rejected && File.ReadAllText(settingsFile) == acceptedJson, "Invalid save names leave accepted settings intact");
            }

            var reloadedSettings = new SettingsService(fixture);
            reloadedSettings.Settings.Theme = AppTheme.Dark;
            var reloaded = new ThemeService(reloadedSettings, userThemes);
            reloaded.Initialize();
            Check(reloaded.ActiveThemeId == "Custom Check" && reloaded.CurrentTheme == ThemeVariant.Light
                && ThemeService.GetColor("PrimaryBackground", "#000000") == Color.Parse("#112233"),
                "Restart loads a saved custom palette and its recorded base variant");
            using (var preview = reloaded.BeginPreview())
            {
                preview.Update(new Dictionary<string, Color> { ["PrimaryBackground"] = Colors.Purple });
                reloaded.ApplyTheme("Dark");
            }
            Check(reloaded.ActiveThemeId == "Dark" && reloaded.IsDark,
                "Applying a theme ends an old preview without a later Dispose reverting the accepted choice");
        }
        finally
        {
            window?.Close();
            while (app.Styles.Count > originalStyleCount) app.Styles.RemoveAt(app.Styles.Count - 1);
            app.RequestedThemeVariant = originalVariant;
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
