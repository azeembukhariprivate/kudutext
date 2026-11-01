using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Kudutext.Controls;
using Kudutext.Core;

namespace Kudutext.Views
{
    public sealed class SettingsWindow : Window
    {
        private readonly StackPanel _models = new StackPanel();

        public SettingsWindow(Window owner)
        {
            Width = 580;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "Settings · KUDUTEXT", owner);

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(Ui.Heading("Settings", 20).Margin(0, 0, 0, 14));

            root.Children.Add(Ui.Heading("Appearance", 14).Margin(0, 0, 0, 8));
            var themes = new WrapPanel();
            foreach (var mode in new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark })
            {
                var chip = new RadioButton { Content = mode == ThemeMode.System ? "Same as Windows" : mode.ToString(), GroupName = "theme", IsChecked = Settings.Theme == mode }
                    .Styled("ChipRadio");
                chip.Checked += (s, e) =>
                {
                    Settings.Theme = mode;
                    ThemeManager.Apply(mode);
                };
                themes.Children.Add(chip);
            }
            root.Children.Add(themes);

            root.Children.Add(Ui.Heading("Speech models on this PC", 14).Margin(0, 16, 0, 4));
            root.Children.Add(Ui.Subtle("Each one is downloaded once, the first time it's used. Removing one frees its space; it's downloaded again if it's needed.", 12)
                .Margin(0, 0, 0, 6));
            root.Children.Add(_models);
            root.Children.Add(Ui.Link("Open the models folder", (s, e) =>
            {
                Directory.CreateDirectory(SpeechModels.Folder);
                AppInfo.OpenFolder(SpeechModels.Folder);
            }).Margin(0, 6, 0, 0));
            RefreshModels();

            root.Children.Add(Ui.Heading("Updates", 14).Margin(0, 16, 0, 4));
            var updates = new CheckBox { Content = "Check for a new KUDUTEXT version when it starts", IsChecked = Settings.CheckForUpdates };
            updates.Click += (s, e) => Settings.CheckForUpdates = updates.IsChecked == true;
            root.Children.Add(updates);
            root.Children.Add(Ui.Subtle("Only asks GitHub for the latest version number. Nothing is downloaded without asking.", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Divider().Margin(0, 18, 0, 0));
            var bottom = Ui.Columns(Ui.Star(), Ui.Auto);
            bottom.Children.Add(Ui.Link("About KUDUTEXT", (s, e) => About.Show(this)).At(0, 0));
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.Click += (s, e) => Close();
            bottom.Children.Add(close.At(0, 1));
            root.Children.Add(bottom.Margin(0, 14, 0, 0));
            Content = root;
        }

        private void RefreshModels()
        {
            _models.Children.Clear();
            foreach (var m in SpeechModels.All)
            {
                var model = m;
                var ready = SpeechModels.IsReady(m);
                var row = Ui.Columns(Ui.Px(70), Ui.Star(), Ui.Auto);
                row.Children.Add(Ui.Text(m.Name, 13, bold: true).At(0, 0));
                row.Children.Add(Ui.Subtle(m.About + " · " + Format.Size(m.Size) + (ready ? " · on this PC" : " · not downloaded"), 12.5, false).At(0, 1));
                if (ready)
                    row.Children.Add(Ui.Link("Remove", (s, e) =>
                    {
                        if (!Dialogs.Confirm(this, $"Remove the {model.Name} speech model? It frees {Format.Size(model.Size)}, and is downloaded again if it's needed.")) return;
                        try
                        {
                            File.Delete(SpeechModels.PathOf(model) + ".ok");
                            File.Delete(SpeechModels.PathOf(model));
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            Dialogs.Error(this, "It couldn't be removed now (it may be in use). Try again when nothing is being done.");
                        }
                        RefreshModels();
                    }).At(0, 2));
                _models.Children.Add(row.Margin(0, 3, 0, 3));
            }
        }
    }

    public static class About
    {
        public static Window Build(Window owner)
        {
            var header = Ui.Row(LogoTile.Create(64), Ui.Column(Ui.Text("KUDUTEXT", 20, bold: true), Ui.Subtle("Version " + AppInfo.VersionText + " · Speech to text and subtitles, on your PC", 12.5))
                .Margin(16, 0, 0, 0));
            ((FrameworkElement)header.Children[1]).VerticalAlignment = VerticalAlignment.Center;

            var rows = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            void Row(string label, UIElement value)
            {
                var g = Ui.Columns(Ui.Px(130), Ui.Star());
                g.Children.Add(Ui.Subtle(label, 12.5, false).At(0, 0));
                g.Children.Add(value.At(0, 1));
                rows.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 7, 0, 7), Child = g }.Res(Border.BorderBrushProperty, "BorderBrush"));
            }
            var madeBy = Ui.Text("Azeem, with Claude and ", 12.5);
            var heart = new Run("♥");
            heart.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            madeBy.Inlines.Add(heart);
            Row("Made by", madeBy);
            Row("Licence", Ui.Row(Ui.Text("MIT · free and open source  ", 12.5), Ui.Link("View", (s, e) => OpenLocal("LICENSE.txt"))));
            Row("Source code", Ui.Link("github.com/" + AppInfo.RepoOwner + "/" + AppInfo.RepoName, (s, e) => AppInfo.OpenUrl(AppInfo.RepoUrl)));
            Row("Speech engine", Ui.Text("whisper.cpp (MIT licence)", 12.5));
            Row("Speech models", Ui.Text("Whisper models by OpenAI (MIT licence), downloaded from Hugging Face the first time", 12.5, wrap: true));
            Row("Reading sound", Ui.Text("NAudio (MIT licence) and Windows' own Media Foundation", 12.5, wrap: true));
            Row("Third-party", Ui.Link("Third-party notices", (s, e) => OpenLocal("THIRD-PARTY-NOTICES.txt")));
            rows.Children.Add(Ui.Divider());

            var content = Ui.Column(header, rows,
                Ui.Subtle("KUDUTEXT works on your computer only. Your videos and recordings never leave it: the internet is used only to download a speech model once, and to check for a new version.", 11.5)
                    .Margin(0, 14, 0, 0));

            Window window = null;
            var updates = Ui.Button("Check for updates");
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.IsDefault = true;
            window = Dialogs.Dialog(owner, "About KUDUTEXT", content, 580, updates, close);
            close.Click += (s, e) => window.Close();
            updates.Click += async (s, e) =>
            {
                updates.IsEnabled = false;
                try
                {
                    var info = await UpdateChecker.CheckAsync();
                    if (info == null) Dialogs.Info(window, "Couldn't find any published version yet.");
                    else if (info.IsNewer)
                    {
                        if (Dialogs.Confirm(window, $"KUDUTEXT {info.Latest.ToString(3)} is available. Open the download page?")) AppInfo.OpenUrl(info.Url);
                    }
                    else Dialogs.Info(window, "You have the latest version.");
                }
                catch
                {
                    Dialogs.Error(window, "Couldn't check for updates. Check your internet connection and try again.");
                }
                finally
                {
                    updates.IsEnabled = true;
                }
            };
            return window;
        }

        public static void Show(Window owner) => Build(owner).ShowDialog();

        private static void OpenLocal(string name)
        {
            var path = Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? string.Empty, name);
            if (File.Exists(path)) AppInfo.OpenFile(path);
            else AppInfo.OpenUrl(AppInfo.RepoUrl);
        }
    }
}
