using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Kudutext.Controls;
using Kudutext.Core;

namespace Kudutext.Views
{
    /// <summary>
    /// The one main window: the choices (accuracy, language, what to save, where), the list of videos and recordings
    /// done one after another, and the speech model download when it's needed the first time.
    /// </summary>
    public sealed class MainWindow : Window
    {
        private readonly List<Job> _jobs;
        private readonly Dictionary<Job, JobRow> _rows = new Dictionary<Job, JobRow>();
        private readonly StackPanel _list = new StackPanel();
        private readonly ScrollViewer _scroll;
        private readonly Border _empty;
        private readonly Button _update = Ui.Link(string.Empty, (s, e) => AppInfo.OpenUrl(AppInfo.ReleasesUrl + "/latest"));
        private readonly TextBlock _status = Ui.Subtle(string.Empty, 12.5, false);
        private readonly Button _start;
        private readonly Button _stop;
        private readonly Button _clear;
        private readonly Dictionary<string, RadioButton> _modelChips = new Dictionary<string, RadioButton>();
        private readonly TextBlock _modelNote = Ui.Subtle(string.Empty, 12, false);
        private readonly TextBlock _folderText = Ui.Text(string.Empty, 12.5);
        private Button _folderReset;

        // The speech model download banner.
        private readonly Border _banner;
        private readonly TextBlock _bannerTitle = Ui.Text(string.Empty, 13, bold: true, wrap: true);
        private readonly TextBlock _bannerText = Ui.Subtle(string.Empty, 12, true);
        private readonly ProgressBar _bannerBar = new ProgressBar { Maximum = 1000, Margin = new Thickness(0, 8, 0, 0) };
        private readonly Button _bannerCancel;
        private readonly Button _bannerRetry;

        private readonly Runner _runner;
        private CancellationTokenSource _jobCancel;
        private CancellationTokenSource _downloadCancel;
        private bool _busy;
        private bool _stopAll;
        private bool _closing;
        private bool _showingChoices;

        /// <summary>For the self-test: where the speech model is downloaded from instead of the usual address.</summary>
        public static string ModelUrlOverride { get; set; }

        /// <summary>For the self-test: close without asking.</summary>
        public bool SkipClosePrompt { get; set; }

        public static string EnginePath => Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? ".", "engine", "whisper-cli.exe");

        public MainWindow()
        {
            Width = 1040;
            Height = 760;
            MinWidth = 760;
            MinHeight = 520;
            Dialogs.Prepare(this, AppInfo.Name);
            AllowDrop = true;

            _runner = new Runner
            {
                Decode = AudioReader.Convert,
                Transcribe = (model, audio, language, translate, progress, cancel) => Whisper.Run(EnginePath, model, audio, language, translate, progress, cancel)
            };
            Whisper.SafePath = AudioReader.ShortPath;
            _jobs = JobList.Load();
            Runner.CleanWork();

            var root = new Grid { Margin = new Thickness(22, 16, 22, 16) };
            for (var i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            // ---- header
            var header = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            var title = Ui.Row(LogoTile.Create(38), Ui.Column(Ui.Text(AppInfo.Name, 18, bold: true), Ui.Subtle("Speech to text and subtitles, on your PC", 12, false)).Margin(12, 0, 0, 0));
            ((FrameworkElement)title.Children[1]).VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(title.At(0, 0));
            _update.Visibility = Visibility.Collapsed;
            _update.HorizontalAlignment = HorizontalAlignment.Right;
            header.Children.Add(_update.Margin(0, 0, 14, 0).At(0, 1));
            var add = Ui.Button("Add files", "IconPlus", "PrimaryButton", (s, e) => PickFiles());
            add.ToolTip = "Choose videos or recordings (Ctrl+O)";
            var settings = Ui.Button(null, "IconSettings", "GhostButton", (s, e) => new SettingsWindow(this).ShowDialog()).Tip("Settings");
            var about = Ui.Button(null, "IconInfo", "GhostButton", (s, e) => About.Show(this)).Tip("About KUDUTEXT");
            header.Children.Add(Ui.Row(add, settings.Margin(8, 0, 0, 0), about.Margin(4, 0, 0, 0)).At(0, 2));
            root.Children.Add(header.At(0));

            // ---- choices
            root.Children.Add(Ui.Card(BuildChoices(), 16).Margin(0, 16, 0, 0).At(1));

            // ---- model download banner
            _bannerCancel = Ui.Small(Ui.Button("Stop", null, "SecondaryButton", (s, e) => _downloadCancel?.Cancel()));
            _bannerRetry = Ui.Small(Ui.Button("Try again", null, "PrimaryButton", (s, e) => { HideBanner(); Pump(); }));
            var bannerButtons = Ui.Row(_bannerRetry, _bannerCancel.Margin(8, 0, 0, 0));
            bannerButtons.VerticalAlignment = VerticalAlignment.Top;
            var bannerGrid = Ui.Columns(Ui.Star(), Ui.Auto);
            bannerGrid.Children.Add(Ui.Column(_bannerTitle, _bannerText.Margin(0, 2, 0, 0), _bannerBar).At(0, 0));
            bannerGrid.Children.Add(bannerButtons.Margin(16, 0, 0, 0).At(0, 1));
            _banner = Ui.Banner("IconImport", bannerGrid);
            _banner.Visibility = Visibility.Collapsed;
            root.Children.Add(_banner.Margin(0, 12, 0, 0).At(2));

            // ---- the list
            _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var drop = Ui.Column(
                Ui.Icon("IconImport", 34).Margin(0, 0, 0, 10),
                Ui.Text("Drop videos or recordings here", 16, bold: true),
                Ui.Subtle("MP4, MKV, MOV, AVI, WMV, MP3, WAV, M4A, FLAC and more. They're done one after another, on this PC.", 12.5, true).Margin(0, 6, 0, 14),
                Ui.Button("Choose files", "IconFolder", "SecondaryButton", (s, e) => PickFiles()));
            drop.HorizontalAlignment = HorizontalAlignment.Center;
            drop.VerticalAlignment = VerticalAlignment.Center;
            foreach (FrameworkElement c in drop.Children) c.HorizontalAlignment = HorizontalAlignment.Center;
            ((TextBlock)drop.Children[2]).TextAlignment = TextAlignment.Center;
            ((TextBlock)drop.Children[2]).MaxWidth = 420;
            _empty = new Border { Child = drop, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(10), Padding = new Thickness(24) }
                .Res(Border.BorderBrushProperty, "InputBorder").Res(Border.BackgroundProperty, "SurfaceBg");
            var listArea = new Grid();
            listArea.Children.Add(new Border { Child = _scroll, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), ClipToBounds = true }
                .Res(Border.BorderBrushProperty, "BorderBrush").Res(Border.BackgroundProperty, "SurfaceBg"));
            listArea.Children.Add(_empty);
            root.Children.Add(listArea.Margin(0, 12, 0, 0).At(3));

            // ---- bottom bar
            var bottom = Ui.Columns(Ui.Star(), Ui.Auto);
            bottom.Children.Add(_status.At(0, 0));
            _clear = Ui.Button("Clear finished", "IconTrash", "GhostButton", (s, e) => ClearFinished());
            _stop = Ui.Button("Stop", "IconStop", "SecondaryButton", (s, e) => StopAll());
            _start = Ui.Button("Start", "IconPlay", "PrimaryButton", (s, e) => Pump());
            bottom.Children.Add(Ui.Row(_clear, _stop.Margin(8, 0, 0, 0), _start.Margin(8, 0, 0, 0)).At(0, 1));
            root.Children.Add(bottom.Margin(0, 12, 0, 0).At(4));

            Content = root;

            foreach (var job in _jobs) AddRow(job);
            RefreshChoices();
            UpdateBottom();

            Drop += (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Add(files);
            };
            DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            InputBindings.Add(new KeyBinding(new Command(PickFiles), Key.O, ModifierKeys.Control));
            Loaded += async (s, e) => await CheckForUpdates();
            Closing += (s, e) =>
            {
                if (_busy && !SkipClosePrompt && !Dialogs.Confirm(this, "A file is being turned into text. Stop it and close KUDUTEXT?\n\nIt starts again from the beginning next time."))
                {
                    e.Cancel = true;
                    return;
                }
                _closing = true;
                _jobCancel?.Cancel();
                _downloadCancel?.Cancel();
                JobList.Save(_jobs);
            };
        }

        public IReadOnlyList<Job> Jobs => _jobs;
        public bool IsBusy => _busy;
        public bool BannerShown => _banner.Visibility == Visibility.Visible;
        public string BannerText => _bannerTitle.Text + " " + _bannerText.Text;

        // ================================================================ choices

        private UIElement BuildChoices()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left: accuracy and language.
            var left = new StackPanel();
            left.Children.Add(Ui.Heading("Accuracy", 13).Margin(0, 0, 0, 6));
            var models = new WrapPanel();
            foreach (var m in SpeechModels.All)
            {
                var model = m;
                var chip = new RadioButton { Content = m.Name, GroupName = "model", ToolTip = m.About + " · " + Format.Size(m.Size) }.Styled("ChipRadio");
                chip.Checked += (s, e) =>
                {
                    if (_showingChoices) return; // only a click changes the choice
                    Settings.Model = model.Id;
                    RefreshChoices();
                };
                _modelChips[m.Id] = chip;
                models.Children.Add(chip);
            }
            left.Children.Add(models);
            left.Children.Add(_modelNote.Margin(0, 2, 0, 0));

            left.Children.Add(Ui.Heading("Language spoken", 13).Margin(0, 14, 0, 6));
            var language = new ComboBox { Width = 230, HorizontalAlignment = HorizontalAlignment.Left, DisplayMemberPath = "Value", SelectedValuePath = "Key", ItemsSource = Languages.All };
            System.Windows.Automation.AutomationProperties.SetName(language, "Language spoken");
            language.SelectedValue = Languages.Known(Settings.Language) ? Settings.Language : "auto";
            language.SelectionChanged += (s, e) =>
            {
                if (language.SelectedValue is string code) Settings.Language = code;
            };
            left.Children.Add(language);
            var translate = new CheckBox { Content = "Write it in English (translate)", IsChecked = Settings.Translate, Margin = new Thickness(0, 8, 0, 0) };
            translate.ToolTip = "Speech in any language becomes English text";
            translate.Click += (s, e) => Settings.Translate = translate.IsChecked == true;
            left.Children.Add(translate);
            grid.Children.Add(left.At(0, 0));

            // Right: what to save and where.
            var right = new StackPanel();
            right.Children.Add(Ui.Heading("Save as", 13).Margin(0, 0, 0, 6));
            var formats = Settings.Formats;
            foreach (var f in SubtitleFiles.Known)
            {
                var format = f;
                var box = new CheckBox { Content = SubtitleFiles.Describe(f), IsChecked = formats.Contains(f), Margin = new Thickness(0, 1, 0, 3) };
                box.Click += (s, e) =>
                {
                    var now = Settings.Formats.Where(x => x != format).ToList();
                    if (box.IsChecked == true) now.Add(format);
                    if (now.Count == 0)
                    {
                        box.IsChecked = true; // at least one
                        return;
                    }
                    Settings.Formats = SubtitleFiles.Known.Where(now.Contains).ToList();
                };
                right.Children.Add(box);
            }

            right.Children.Add(Ui.Heading("Save to", 13).Margin(0, 12, 0, 6));
            _folderText.TextTrimming = TextTrimming.CharacterEllipsis;
            var change = Ui.Link("Change", (s, e) =>
            {
                var folder = Dialogs.PickFolder(this, "Where should the text files go?", Settings.OutputFolder);
                if (folder == null) return;
                Settings.OutputFolder = folder;
                RefreshChoices();
            });
            _folderReset = Ui.Link("Next to each file", (s, e) =>
            {
                Settings.OutputFolder = string.Empty;
                RefreshChoices();
            });
            var folderRow = Ui.Columns(Ui.Star(), Ui.Auto, Ui.Auto);
            folderRow.Children.Add(Ui.Row(Ui.Icon("IconFolder", 14).Margin(0, 0, 6, 0), _folderText).At(0, 0));
            folderRow.Children.Add(change.Margin(10, 0, 0, 0).At(0, 1));
            folderRow.Children.Add(_folderReset.Margin(10, 0, 0, 0).At(0, 2));
            right.Children.Add(folderRow);
            grid.Children.Add(right.At(0, 2));
            return grid;
        }

        private void RefreshChoices()
        {
            var current = SpeechModels.Find(Settings.Model);
            _showingChoices = true;
            foreach (var pair in _modelChips) pair.Value.IsChecked = pair.Key == current.Id;
            _showingChoices = false;
            _modelNote.Text = current.About + " · " + (SpeechModels.IsReady(current) ? "ready on this PC" : Format.Size(current.Size) + " to download once, the first time");
            var folder = Settings.OutputFolder;
            _folderText.Text = string.IsNullOrEmpty(folder) ? "Next to each video or recording" : folder;
            _folderText.ToolTip = _folderText.Text;
            _folderReset.Visibility = string.IsNullOrEmpty(folder) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ================================================================ the list

        private void PickFiles()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose videos or recordings",
                Filter = "Videos and recordings|" + string.Join(";", JobList.Kinds.Select(k => "*" + k)) + "|All files|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) == true) Add(dialog.FileNames);
        }

        /// <summary>Adds files (and the videos and recordings in dropped folders) to the list and starts on them.</summary>
        public int Add(IEnumerable<string> paths)
        {
            var found = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    try { found.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(JobList.CanTake).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(500)); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
                else if (File.Exists(p)) found.Add(p);
            }
            var skipped = found.Count(f => !JobList.CanTake(f));
            var added = 0;
            foreach (var file in found.Where(JobList.CanTake))
            {
                // A file already waiting or being done isn't added twice.
                if (_jobs.Any(j => string.Equals(j.File, file, StringComparison.OrdinalIgnoreCase) && (j.State == JobState.Waiting || j.State == JobState.Working))) continue;
                var job = new Job { File = file, Model = SpeechModels.Find(Settings.Model).Id, Language = Settings.Language, Translate = Settings.Translate };
                _jobs.Add(job);
                AddRow(job);
                added++;
            }
            if (skipped > 0)
                Dialogs.Info(this, skipped == 1 && added == 0
                    ? "That kind of file can't be turned into text. Choose a video or a recording (MP4, MKV, MOV, MP3, WAV, M4A and more)."
                    : Format.Count(skipped, "file was", "files were") + " left out: only videos and recordings can be turned into text.");
            JobList.Save(_jobs);
            UpdateBottom();
            if (added > 0)
            {
                _scroll.ScrollToEnd();
                Pump();
            }
            return added;
        }

        private void AddRow(Job job)
        {
            var row = new JobRow(job, this);
            _rows[job] = row;
            _list.Children.Add(row.Element);
            row.Refresh();
        }

        internal void Remove(Job job)
        {
            if (job.State == JobState.Working) return;
            _jobs.Remove(job);
            if (_rows.TryGetValue(job, out var row)) _list.Children.Remove(row.Element);
            _rows.Remove(job);
            try { if (File.Exists(JobList.TranscriptFile(job))) File.Delete(JobList.TranscriptFile(job)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            JobList.Save(_jobs);
            UpdateBottom();
        }

        internal void Retry(Job job)
        {
            if (job.State == JobState.Working) return;
            job.State = JobState.Waiting;
            job.Error = null;
            job.Percent = 0;
            job.Model = SpeechModels.Find(Settings.Model).Id;
            job.Language = Settings.Language;
            job.Translate = Settings.Translate;
            Refresh(job);
            JobList.Save(_jobs);
            Pump();
        }

        /// <summary>Opens the window to check and correct a finished file's text.</summary>
        internal EditorWindow OpenEditor(Job job, bool modal = true)
        {
            if (job.State != JobState.Done) return null;
            EditorWindow editor;
            try { editor = new EditorWindow(this, job); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Runtime.Serialization.SerializationException)
            {
                Dialogs.Error(this, "The text for this file couldn't be read. Add the file again to turn it into text again.");
                return null;
            }
            editor.Saved += () =>
            {
                Refresh(job);
                JobList.Save(_jobs);
            };
            if (modal) editor.ShowDialog();
            return editor;
        }

        /// <summary>Opens the window to put a finished video's subtitles into it.</summary>
        internal VideoWindow OpenVideo(Job job, bool modal = true)
        {
            if (job.State != JobState.Done || !File.Exists(job.File))
            {
                if (job.State == JobState.Done) Dialogs.Info(this, "The video isn't there any more.");
                return null;
            }
            var window = new VideoWindow(this, job);
            if (modal) window.ShowDialog();
            return window;
        }

        internal void StopOne(Job job)
        {
            if (job.State == JobState.Working) _jobCancel?.Cancel();
        }

        public void ClearFinished()
        {
            foreach (var job in _jobs.Where(j => j.State == JobState.Done || j.State == JobState.Cancelled || j.State == JobState.Failed).ToList())
            {
                _jobs.Remove(job);
                if (_rows.TryGetValue(job, out var row)) _list.Children.Remove(row.Element);
                _rows.Remove(job);
            }
            JobList.Save(_jobs);
            UpdateBottom();
        }

        public void StopAll()
        {
            _stopAll = true;
            _jobCancel?.Cancel();
            _downloadCancel?.Cancel();
        }

        private void Refresh(Job job)
        {
            if (_rows.TryGetValue(job, out var row)) row.Refresh();
        }

        private void UpdateBottom()
        {
            var waiting = _jobs.Count(j => j.State == JobState.Waiting);
            var done = _jobs.Count(j => j.State == JobState.Done);
            var failed = _jobs.Count(j => j.State == JobState.Failed);
            var parts = new List<string>();
            if (_busy) parts.Add("Working");
            if (waiting > 0) parts.Add(waiting + " waiting");
            if (done > 0) parts.Add(done + " done");
            if (failed > 0) parts.Add(failed + " couldn't be done");
            _status.Text = _jobs.Count == 0 ? "Nothing in the list yet" : string.Join(" · ", parts);
            _empty.Visibility = _jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _start.Visibility = !_busy && waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
            _stop.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
            _clear.Visibility = _jobs.Any(j => j.State == JobState.Done || j.State == JobState.Cancelled || j.State == JobState.Failed) ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================================================================ doing the work

        /// <summary>Works through the waiting files one after another (downloading the speech model first if needed).</summary>
        public async void Pump()
        {
            if (_busy) return;
            _busy = true;
            _stopAll = false;
            UpdateBottom();
            try
            {
                while (!_closing && !_stopAll)
                {
                    var job = _jobs.FirstOrDefault(j => j.State == JobState.Waiting);
                    if (job == null) break;
                    var model = SpeechModels.Find(job.Model);
                    if (!SpeechModels.IsReady(model) && !await GetModel(model)) break;
                    if (_closing || _stopAll) break;
                    if (!File.Exists(EnginePath))
                    {
                        job.State = JobState.Failed;
                        job.Error = "The speech engine is missing. Please install KUDUTEXT again.";
                        Refresh(job);
                        break;
                    }
                    _jobCancel = new CancellationTokenSource();
                    var formats = Settings.Formats;
                    var folder = Settings.OutputFolder;
                    await _runner.Do(job, formats, folder, () => Dispatcher.BeginInvoke(new Action(() => Refresh(job))), _jobCancel.Token);
                    Refresh(job);
                    JobList.Save(_jobs);
                    UpdateBottom();
                }
            }
            finally
            {
                _busy = false;
                _jobCancel = null;
                if (!_closing)
                {
                    RefreshChoices();
                    UpdateBottom();
                }
            }
        }

        /// <summary>Downloads a speech model, showing how far it's got. False when it was stopped or failed.</summary>
        private async Task<bool> GetModel(SpeechModel model)
        {
            _downloadCancel = new CancellationTokenSource();
            _bannerTitle.Text = $"Getting the {model.Name} speech model ({Format.Size(model.Size)})";
            _bannerText.Text = "This happens once. After that everything works without the internet, and your files never leave this PC.";
            _bannerBar.Value = 0;
            _bannerBar.Visibility = Visibility.Visible;
            _bannerCancel.Visibility = Visibility.Visible;
            _bannerRetry.Visibility = Visibility.Collapsed;
            _banner.Visibility = Visibility.Visible;
            var started = DateTime.UtcNow;
            long startBytes = -1;
            var lastShown = DateTime.MinValue;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.Name + "/" + AppInfo.VersionText);
                    await SpeechModels.Download(http, model, (done, total) =>
                    {
                        if (startBytes < 0) startBytes = done;
                        var now = DateTime.UtcNow;
                        if ((now - lastShown).TotalMilliseconds < 200 && done < total) return;
                        lastShown = now;
                        var speed = (done - startBytes) / Math.Max(0.5, (now - started).TotalSeconds);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _bannerBar.Value = total > 0 ? done * 1000.0 / total : 0;
                            _bannerText.Text = $"{Format.Size(done)} of {Format.Size(total)} · {Format.Speed(speed)}. This happens once; after that it all works without the internet.";
                        }));
                    }, _downloadCancel.Token, ModelUrlOverride);
                }
                HideBanner();
                RefreshChoices();
                return true;
            }
            catch (OperationCanceledException)
            {
                if (_closing) return false;
                ShowBannerError($"The {model.Name} speech model wasn't downloaded", "It carries on from where it stopped when you try again.");
                return false;
            }
            catch (DownloadFailedException ex)
            {
                ShowBannerError($"The {model.Name} speech model couldn't be downloaded", ex.Message);
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowBannerError($"The {model.Name} speech model couldn't be saved", ex.Message);
                return false;
            }
            finally
            {
                _downloadCancel = null;
            }
        }

        private void ShowBannerError(string title, string text)
        {
            _bannerTitle.Text = title;
            _bannerText.Text = text;
            _bannerBar.Visibility = Visibility.Collapsed;
            _bannerCancel.Visibility = Visibility.Collapsed;
            _bannerRetry.Visibility = Visibility.Visible;
            _banner.Visibility = Visibility.Visible;
        }

        private void HideBanner() => _banner.Visibility = Visibility.Collapsed;

        private async Task CheckForUpdates()
        {
            if (!Settings.CheckForUpdates || SelfTest.Running) return;
            try
            {
                var info = await UpdateChecker.CheckAsync();
                if (info == null || !info.IsNewer || Settings.SkippedVersion == info.Latest.ToString(3)) return;
                _update.Content = $"{AppInfo.Name} {info.Latest.ToString(3)} is available";
                _update.Visibility = Visibility.Visible;
            }
            catch
            {
                // No internet: nothing to show.
            }
        }

        private sealed class Command : ICommand
        {
            private readonly Action _run;
            public Command(Action run) => _run = run;
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => _run();
        }
    }

    /// <summary>One file in the list: its name, how far it's got, and what can be done with it.</summary>
    internal sealed class JobRow
    {
        private readonly Job _job;
        private readonly MainWindow _owner;
        private readonly TextBlock _name = Ui.Text(string.Empty, 13.5, bold: true);
        private readonly TextBlock _detail = Ui.Subtle(string.Empty, 12, false);
        private readonly ProgressBar _bar = new ProgressBar { Maximum = 100, Width = 150, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly ContentControl _chip = new ContentControl { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        private readonly Button _show;
        private readonly Button _check;
        private readonly Button _video;
        private readonly Button _retry;
        private readonly Button _stop;
        private readonly Button _remove;

        public JobRow(Job job, MainWindow owner)
        {
            _job = job;
            _owner = owner;
            _check = Ui.Small(Ui.Button("Check text", "IconEdit", "SecondaryButton", (s, e) => _owner.OpenEditor(_job)));
            _check.ToolTip = "Read through the text, correct it and save again";
            _video = Ui.Small(Ui.Button("Into video", "IconImport", "SecondaryButton", (s, e) => _owner.OpenVideo(_job))).Margin(6, 0, 0, 0);
            _video.ToolTip = "Put the subtitles into the video";
            _show = Ui.Small(Ui.Button("Show files", "IconFolder", "SecondaryButton", (s, e) => ShowFiles())).Margin(6, 0, 0, 0);
            _retry = Ui.Small(Ui.Button("Try again", "IconRefresh", "SecondaryButton", (s, e) => _owner.Retry(_job)));
            _stop = Ui.Small(Ui.Button("Stop", "IconStop", "SecondaryButton", (s, e) => _owner.StopOne(_job)));
            _remove = Ui.Button(null, "IconClose", "GhostButton", (s, e) => _owner.Remove(_job)).Tip("Take it off the list");
            _remove.MinHeight = 30;

            var grid = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto);
            grid.Children.Add(Ui.Icon("IconFile", 18).Margin(0, 0, 12, 0).At(0, 0));
            var text = Ui.Column(_name, _detail.Margin(0, 2, 0, 0));
            text.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(text.At(0, 1));
            grid.Children.Add(_bar.At(0, 2));
            grid.Children.Add(_chip.At(0, 3));
            var actions = Ui.Row(_check, _video, _show, _retry, _stop, _remove.Margin(6, 0, 0, 0));
            actions.Margin = new Thickness(14, 0, 0, 0);
            grid.Children.Add(actions.At(0, 4));
            Element = new Border { Child = grid, Padding = new Thickness(16, 10, 10, 10), BorderThickness = new Thickness(0, 0, 0, 1) }.Res(Border.BorderBrushProperty, "BorderBrush");
            Element.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2 && _job.State == JobState.Done) _owner.OpenEditor(_job);
            };
        }

        public Border Element { get; }

        public void Refresh()
        {
            _name.Text = _job.FileName;
            _name.ToolTip = _job.File;
            var model = SpeechModels.Find(_job.Model);
            string detail;
            switch (_job.State)
            {
                case JobState.Working:
                    detail = (_job.Step ?? "Starting") + (_job.Percent > 0 ? $" · {_job.Percent}%" : string.Empty) + " · " + model.Name;
                    break;
                case JobState.Done:
                    var outs = (_job.Outputs ?? new List<string>()).Select(Path.GetFileName);
                    detail = (_job.Lines == 0 ? "No speech was found" : Languages.Name(_job.Heard ?? "") + " · " + Format.Count(_job.Lines, "line"))
                             + (_job.LengthSeconds > 0 ? " · " + Format.Duration(TimeSpan.FromSeconds(_job.LengthSeconds)) : string.Empty)
                             + " · " + string.Join(", ", outs);
                    break;
                case JobState.Failed:
                    detail = _job.Error ?? "It couldn't be done.";
                    break;
                case JobState.Cancelled:
                    detail = "Stopped";
                    break;
                default:
                    detail = "Waiting · " + model.Name + " · " + (_job.Translate ? "into English" : Languages.Name(_job.Language ?? "auto"));
                    break;
            }
            _detail.Text = detail;
            _detail.ToolTip = detail;
            _detail.Res(TextBlock.ForegroundProperty, _job.State == JobState.Failed ? "BadText" : "SubtleText");

            _bar.Visibility = _job.State == JobState.Working ? Visibility.Visible : Visibility.Collapsed;
            _bar.Value = _job.Percent;
            _bar.IsIndeterminate = _job.State == JobState.Working && _job.Percent == 0;
            switch (_job.State)
            {
                case JobState.Done: _chip.Content = Ui.Chip("Done", Tone.Ok, "IconCheck"); break;
                case JobState.Failed: _chip.Content = Ui.Chip("Couldn't be done", Tone.Bad); break;
                case JobState.Cancelled: _chip.Content = Ui.Chip("Stopped", Tone.Neutral); break;
                case JobState.Working: _chip.Content = Ui.Chip("Working", Tone.Accent); break;
                default: _chip.Content = Ui.Chip("Waiting", Tone.Neutral); break;
            }
            _show.Visibility = _job.State == JobState.Done && (_job.Outputs?.Count ?? 0) > 0 ? Visibility.Visible : Visibility.Collapsed;
            _check.Visibility = _job.State == JobState.Done && File.Exists(JobList.TranscriptFile(_job)) ? Visibility.Visible : Visibility.Collapsed;
            _video.Visibility = _check.Visibility == Visibility.Visible && _job.Lines > 0 && Ffmpeg.IsVideo(_job.File) ? Visibility.Visible : Visibility.Collapsed;
            _retry.Visibility = _job.State == JobState.Failed || _job.State == JobState.Cancelled ? Visibility.Visible : Visibility.Collapsed;
            _stop.Visibility = _job.State == JobState.Working ? Visibility.Visible : Visibility.Collapsed;
            _remove.Visibility = _job.State == JobState.Working ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowFiles()
        {
            var first = _job.Outputs?.FirstOrDefault(File.Exists);
            if (first != null) AppInfo.OpenFolder(Path.GetDirectoryName(first), first);
            else Dialogs.Info(_owner, "The text files aren't there any more.");
        }
    }
}
