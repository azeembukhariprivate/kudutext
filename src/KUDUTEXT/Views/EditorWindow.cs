using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Kudutext.Controls;
using Kudutext.Core;

namespace Kudutext.Views
{
    /// <summary>One line of speech while it's being checked: its times as typed, its words, and anything wrong with it.</summary>
    public sealed class LineRow : INotifyPropertyChanged
    {
        private string _start, _end, _text, _problem;
        private bool _playing;

        public string Start { get => _start; set => Set(ref _start, value); }
        public string End { get => _end; set => Set(ref _end, value); }
        public string Text { get => _text; set => Set(ref _text, value); }
        public string Problem { get => _problem; set => Set(ref _problem, value, false); }
        public bool Playing { get => _playing; set => Set(ref _playing, value, false); }

        public long StartMs => TextEdits.ParseTime(Start) ?? 0;
        public long EndMs => TextEdits.ParseTime(End) ?? StartMs;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>A change made by the person (not a note the window adds itself).</summary>
        public event Action Edited;

        private void Set<T>(ref T field, T value, bool edit = true, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (edit) Edited?.Invoke();
        }
    }

    /// <summary>
    /// Checking and correcting a file's text: each line with its start and end, played from the original to hear it,
    /// split, joined, removed, words replaced everywhere, then saved back into the subtitle and text files.
    /// </summary>
    public sealed class EditorWindow : Window
    {
        private readonly Job _job;
        private readonly Transcript _original;
        private readonly ObservableCollection<LineRow> _rows = new ObservableCollection<LineRow>();
        private readonly ListBox _list;
        private readonly TextBox _find = new TextBox { Width = 180 };
        private readonly TextBox _replace = new TextBox { Width = 180 };
        private readonly CheckBox _matchCase = new CheckBox { Content = "Match case", VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _status = Ui.Subtle(string.Empty, 12.5, false);
        private readonly Border _note;
        private readonly TextBlock _noteText = Ui.Text(string.Empty, 12.5, wrap: true);
        private readonly LinePlayer _player = new LinePlayer();
        private TextBox _lastText;
        private bool _dirty;
        private bool _loading;

        /// <summary>For the self-test: close without asking about unsaved changes.</summary>
        public bool SkipClosePrompt { get; set; }

        /// <summary>Raised after a save, so the list can show the new number of lines.</summary>
        public event Action Saved;

        public EditorWindow(Window owner, Job job)
        {
            _job = job;
            _original = Transcript.Load(JobList.TranscriptFile(job));
            Width = 1000;
            Height = 740;
            MinWidth = 760;
            MinHeight = 480;
            Dialogs.Prepare(this, "Check the text · " + job.FileName, owner);

            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            for (var i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            // ---- title
            var title = Ui.Column(Ui.Text(job.FileName, 17, bold: true),
                Ui.Subtle("Play a line to hear it, correct its words or times, then save. "
                          + (_original.Language != null ? "Language: " + Languages.Name(_original.Language) + "." : string.Empty), 12.5, true).Margin(0, 3, 0, 0));
            root.Children.Add(title.At(0));

            // ---- tools: the line, and replacing words everywhere
            var tools = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            tools.Children.Add(Ui.Small(Ui.Button("Play", "IconPlay", "SecondaryButton", (s, e) => PlaySelected())).Tip("Play the chosen line (Ctrl+Space)").Margin(0, 0, 6, 6));
            tools.Children.Add(Ui.Small(Ui.Button("Split", "IconMove", "SecondaryButton", (s, e) => Split())).Tip("Split the line where the cursor is (Ctrl+Enter)").Margin(0, 0, 6, 6));
            tools.Children.Add(Ui.Small(Ui.Button("Join with next", "IconLink", "SecondaryButton", (s, e) => JoinWithNext())).Tip("Join the chosen line with the one after it").Margin(0, 0, 6, 6));
            tools.Children.Add(Ui.Small(Ui.Button("Remove", "IconTrash", "SecondaryButton", (s, e) => RemoveSelected())).Tip("Remove the chosen line").Margin(0, 0, 18, 6));
            System.Windows.Automation.AutomationProperties.SetName(_find, "Find");
            System.Windows.Automation.AutomationProperties.SetName(_replace, "Replace with");
            tools.Children.Add(Ui.Text("Find", 12.5).Margin(0, 0, 6, 6));
            tools.Children.Add(_find.Margin(0, 0, 6, 6));
            tools.Children.Add(Ui.Small(Ui.Button("Next", "IconSearch", "SecondaryButton", (s, e) => FindNext())).Margin(0, 0, 12, 6));
            tools.Children.Add(Ui.Text("Replace with", 12.5).Margin(0, 0, 6, 6));
            tools.Children.Add(_replace.Margin(0, 0, 6, 6));
            tools.Children.Add(Ui.Small(Ui.Button("Replace all", null, "SecondaryButton", (s, e) => ReplaceAll())).Margin(0, 0, 10, 6));
            tools.Children.Add(_matchCase.Margin(0, 0, 0, 6));
            _find.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) FindNext();
            };
            root.Children.Add(tools.At(1));

            // ---- a note (what was replaced, why it can't be saved yet, playing problems)
            _note = Ui.Banner("IconInfo", _noteText);
            _note.Visibility = Visibility.Collapsed;
            root.Children.Add(_note.Margin(0, 6, 0, 0).At(2));

            // ---- the lines
            _list = new ListBox
            {
                ItemsSource = _rows,
                ItemTemplate = RowTemplate(),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0)
            };
            _list.Res(Control.BorderBrushProperty, "BorderBrush").Res(Control.BackgroundProperty, "SurfaceBg");
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
            // Clicking into a line's boxes chooses that line.
            _list.PreviewGotKeyboardFocus += (s, e) =>
            {
                if (e.NewFocus is DependencyObject d)
                {
                    var item = Ancestor<ListBoxItem>(d);
                    if (item != null) item.IsSelected = true;
                    if (d is TextBox box && (string)box.Tag == "text") _lastText = box;
                }
            };
            root.Children.Add(_list.Margin(0, 10, 0, 0).At(3));

            // ---- bottom
            var bottom = Ui.Columns(Ui.Star(), Ui.Auto);
            bottom.Children.Add(_status.At(0, 0));
            var close = Ui.Button("Close", null, "SecondaryButton", (s, e) => Close());
            var save = Ui.Button("Save", "IconSave", "PrimaryButton", (s, e) => Save());
            save.ToolTip = "Save the text and the subtitle files again (Ctrl+S)";
            bottom.Children.Add(Ui.Row(close, save.Margin(8, 0, 0, 0)).At(0, 1));
            root.Children.Add(bottom.Margin(0, 12, 0, 0).At(4));
            Content = root;

            Load(_original);

            InputBindings.Add(new KeyBinding(new Command(() => Save()), Key.S, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(new Command(PlaySelected), Key.Space, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(new Command(() => Split()), Key.Enter, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(new Command(() => _find.Focus()), Key.F, ModifierKeys.Control));
            Closing += (s, e) =>
            {
                if (_dirty && !SkipClosePrompt)
                {
                    var answer = MessageBox.Show(this, "Save the changes to the text?", AppInfo.Name, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !Save()))
                    {
                        e.Cancel = true;
                        return;
                    }
                }
                _player.Dispose();
            };
            _player.Finished += () =>
            {
                foreach (var r in _rows) r.Playing = false;
            };
        }

        // ================================================================ for the window and the self-test

        public IList<LineRow> Rows => _rows;
        public bool IsDirty => _dirty;
        public string Note => _note.Visibility == Visibility.Visible ? _noteText.Text : null;
        public bool IsPlaying => _player.IsPlaying;

        public int Selected
        {
            get => _list.SelectedIndex;
            set
            {
                _list.SelectedIndex = value;
                if (value >= 0 && value < _rows.Count) _list.ScrollIntoView(_rows[value]);
            }
        }

        private void Load(Transcript t)
        {
            _loading = true;
            _rows.Clear();
            foreach (var s in t.Segments) _rows.Add(NewRow(s.StartMs, s.EndMs, s.Text));
            _loading = false;
            CheckProblems();
            _dirty = false;
            UpdateStatus();
        }

        private LineRow NewRow(long start, long end, string text)
        {
            var row = new LineRow { Start = TextEdits.ShowTime(start), End = TextEdits.ShowTime(end), Text = text };
            row.Edited += () =>
            {
                if (_loading) return;
                _dirty = true;
                CheckProblems();
                UpdateStatus();
            };
            return row;
        }

        private void CheckProblems()
        {
            long? previous = null;
            foreach (var r in _rows)
            {
                r.Problem = TextEdits.Problem(r.Start, r.End, previous);
                var s = TextEdits.ParseTime(r.Start);
                if (s != null) previous = s;
            }
        }

        private void UpdateStatus()
        {
            var problems = _rows.Count(r => r.Problem != null);
            // Put together from pieces, each in the language in use.
            _status.Text = Format.Count(_rows.Count, "line")
                           + (problems > 0 ? " · " + Lang.T(problems == 1 ? "1 line needs a look" : "{0} lines need a look", problems.ToString("N0")) : string.Empty)
                           + (_dirty ? " · " + Lang.T("not saved yet") : string.Empty);
            Title = (_dirty ? "• " : string.Empty) + Lang.T("Check the text · {0}", _job.FileName);
        }

        private void ShowNote(string text)
        {
            _noteText.Text = text;
            _note.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ================================================================ editing

        private LineRow Current => _list.SelectedItem as LineRow;

        /// <summary>Splits the chosen line where the cursor is in its words (or at <paramref name="caret"/>).</summary>
        public bool Split(int? caret = null)
        {
            var row = Current;
            if (row == null)
            {
                ShowNote("Click into a line first, then put the cursor where it should be split.");
                return false;
            }
            var at = caret ?? (_lastText != null && ReferenceEquals(_lastText.DataContext, row) ? _lastText.CaretIndex : -1);
            if (!TextEdits.SplitText(row.Text, at, out var first, out var second))
            {
                ShowNote("Put the cursor between two words of the line to split it there.");
                return false;
            }
            var start = row.StartMs;
            var end = row.EndMs;
            var middle = TextEdits.SplitTime(start, end, first.Length, second.Length);
            var index = _rows.IndexOf(row);
            row.Text = first;
            row.End = TextEdits.ShowTime(middle);
            _rows.Insert(index + 1, NewRow(middle, end, second));
            Changed();
            Selected = index + 1;
            ShowNote(null);
            return true;
        }

        public bool JoinWithNext()
        {
            var row = Current;
            var index = row == null ? -1 : _rows.IndexOf(row);
            if (index < 0 || index + 1 >= _rows.Count)
            {
                ShowNote("Choose a line that has another line after it.");
                return false;
            }
            var next = _rows[index + 1];
            row.Text = TextEdits.JoinText(row.Text, next.Text);
            row.End = TextEdits.ShowTime(Math.Max(row.EndMs, next.EndMs));
            _rows.RemoveAt(index + 1);
            Changed();
            Selected = index;
            ShowNote(null);
            return true;
        }

        public bool RemoveSelected()
        {
            var index = Current == null ? -1 : _rows.IndexOf(Current);
            if (index < 0) return false;
            _rows.RemoveAt(index);
            Changed();
            Selected = Math.Min(index, _rows.Count - 1);
            return true;
        }

        public int ReplaceAll(string find = null, string replacement = null)
        {
            find = find ?? _find.Text;
            replacement = replacement ?? _replace.Text;
            if (string.IsNullOrEmpty(find))
            {
                ShowNote("Type the words to find first.");
                return 0;
            }
            var total = 0;
            var lines = 0;
            foreach (var r in _rows)
            {
                var text = TextEdits.Replace(r.Text, find, replacement, _matchCase.IsChecked == true, out var n);
                if (n == 0) continue;
                r.Text = text;
                total += n;
                lines++;
            }
            ShowNote(total == 0 ? $"\"{find}\" wasn't found." : $"Replaced {Format.Count(total, "time")} in {Format.Count(lines, "line")}.");
            return total;
        }

        public bool FindNext(string find = null)
        {
            find = find ?? _find.Text;
            if (string.IsNullOrEmpty(find) || _rows.Count == 0) return false;
            var from = Math.Max(-1, _list.SelectedIndex);
            for (var k = 1; k <= _rows.Count; k++)
            {
                var i = (from + k) % _rows.Count;
                if (!TextEdits.Contains(_rows[i].Text, find, _matchCase.IsChecked == true)) continue;
                Selected = i;
                ShowNote(null);
                return true;
            }
            ShowNote($"\"{find}\" wasn't found.");
            return false;
        }

        private void Changed()
        {
            _dirty = true;
            CheckProblems();
            UpdateStatus();
        }

        // ================================================================ hearing a line

        public void PlaySelected()
        {
            var row = Current ?? _rows.FirstOrDefault();
            if (row == null) return;
            foreach (var r in _rows) r.Playing = false;
            try
            {
                _player.Play(_job.File, row.StartMs, row.EndMs);
                row.Playing = true;
                ShowNote(null);
            }
            catch (AudioException ex)
            {
                ShowNote(ex.Message);
            }
        }

        // ================================================================ saving

        /// <summary>Saves the text and writes the subtitle and text files again. False (with a note) when a line needs fixing first.</summary>
        public bool Save()
        {
            CheckProblems();
            var bad = _rows.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Problem != null);
            if (bad.r != null)
            {
                Selected = bad.i;
                ShowNote($"Line {bad.i + 1} needs a look before saving: {bad.r.Problem}");
                UpdateStatus();
                return false;
            }
            var t = TextEdits.Build(_original.Language, _rows.Select(r => (r.StartMs, r.EndMs, r.Text ?? string.Empty)));
            try
            {
                t.Save(JobList.TranscriptFile(_job));
                _job.Outputs = Outputs.Rewrite(_job, t, Settings.Formats, Settings.OutputFolder);
                _job.Lines = t.Segments.Count;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowNote("It couldn't be saved: " + ex.Message);
                return false;
            }
            // Lines with no words were left out: show what was saved.
            if (t.Segments.Count != _rows.Count) Load(t);
            _dirty = false;
            UpdateStatus();
            ShowNote("Saved: " + string.Join(", ", _job.Outputs.Select(Path.GetFileName)) + ".");
            Saved?.Invoke();
            return true;
        }

        // ================================================================ how a line looks

        private DataTemplate RowTemplate()
        {
            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.SetValue(MarginProperty, new Thickness(6, 6, 6, 6));
            foreach (var w in new[] { GridLength.Auto, new GridLength(118), new GridLength(118), new GridLength(1, GridUnitType.Star) })
            {
                var c = new FrameworkElementFactory(typeof(ColumnDefinition));
                c.SetValue(ColumnDefinition.WidthProperty, w);
                grid.AppendChild(c);
            }
            for (var i = 0; i < 2; i++)
            {
                var r = new FrameworkElementFactory(typeof(RowDefinition));
                r.SetValue(RowDefinition.HeightProperty, GridLength.Auto);
                grid.AppendChild(r);
            }

            var play = new FrameworkElementFactory(typeof(Button));
            play.SetResourceReference(StyleProperty, "GhostButton");
            play.SetValue(ToolTipProperty, "Play this line");
            play.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Play this line");
            play.SetValue(MinHeightProperty, 30.0);
            play.SetValue(PaddingProperty, new Thickness(8, 0, 8, 0));
            play.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
            var glyph = new FrameworkElementFactory(typeof(Glyph));
            glyph.SetValue(WidthProperty, 14.0);
            glyph.SetValue(HeightProperty, 14.0);
            glyph.SetResourceReference(Glyph.DataProperty, "IconPlay");
            play.AppendChild(glyph);
            play.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if (((FrameworkElement)s).DataContext is LineRow row)
                {
                    _list.SelectedItem = row;
                    PlaySelected();
                }
            }));
            grid.AppendChild(play);

            grid.AppendChild(Box("Start", 1, "Start", true));
            grid.AppendChild(Box("End", 2, "End", true));
            var text = Box("Text", 3, "Words", false);
            text.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
            text.SetValue(TextBox.AcceptsReturnProperty, true);
            text.SetValue(TagProperty, "text");
            grid.AppendChild(text);

            var problem = new FrameworkElementFactory(typeof(TextBlock));
            problem.SetValue(Grid.RowProperty, 1);
            problem.SetValue(Grid.ColumnProperty, 1);
            problem.SetValue(Grid.ColumnSpanProperty, 3);
            problem.SetValue(TextBlock.FontSizeProperty, 12.0);
            problem.SetValue(MarginProperty, new Thickness(4, 4, 0, 0));
            problem.SetResourceReference(TextBlock.ForegroundProperty, "BadText");
            problem.SetBinding(TextBlock.TextProperty, new Binding("Problem"));
            problem.SetBinding(VisibilityProperty, new Binding("Problem") { Converter = new ShowWhenText() });
            grid.AppendChild(problem);

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(3, 0, 0, 0));
            border.SetBinding(Border.BorderBrushProperty, new Binding("Playing") { Converter = new PlayingBrush() });
            border.AppendChild(grid);
            return new DataTemplate { VisualTree = border };
        }

        private static FrameworkElementFactory Box(string property, int column, string name, bool time)
        {
            var box = new FrameworkElementFactory(typeof(TextBox));
            box.SetValue(Grid.ColumnProperty, column);
            box.SetValue(MarginProperty, new Thickness(4, 0, 4, 0));
            box.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
            box.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, name);
            if (time) box.SetResourceReference(FontFamilyProperty, "MonoFont");
            box.SetBinding(TextBox.TextProperty, new Binding(property)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = time ? UpdateSourceTrigger.LostFocus : UpdateSourceTrigger.PropertyChanged
            });
            return box;
        }

        private static T Ancestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && !(d is T)) d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        private sealed class ShowWhenText : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
                string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
        }

        private sealed class PlayingBrush : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
                value is bool b && b ? Ui.Brush("Accent") : Brushes.Transparent;

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
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
}
