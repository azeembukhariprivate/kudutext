using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Kudutext.Controls;
using Kudutext.Core;

namespace Kudutext.Views
{
    /// <summary>Getting FFmpeg, from the video window or Settings.</summary>
    public static class FfmpegGetter
    {
        /// <summary>For the self-test: where FFmpeg is downloaded from instead of the usual address.</summary>
        public static string UrlOverride { get; set; }

        public static async Task Get(Action<long, long> progress, CancellationToken cancel)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.Name + "/" + AppInfo.VersionText);
                await Ffmpeg.Download(http, progress, cancel, UrlOverride);
            }
        }
    }

    /// <summary>
    /// Puts a finished file's subtitles into its video, as a track players can turn on and off (quick) or written into
    /// the picture (always shown). The new video is saved beside the original (never over anything); FFmpeg is fetched
    /// the first time.
    /// </summary>
    public sealed class VideoWindow : Window
    {
        private readonly Job _job;
        private readonly RadioButton _track;
        private readonly RadioButton _burn;
        private readonly WrapPanel _sizes = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        private readonly StackPanel _sizeRow;
        private readonly TextBlock _where = Ui.Subtle(string.Empty, 12.5, true);
        private readonly Border _note;
        private readonly TextBlock _noteText = Ui.Text(string.Empty, 12.5, wrap: true);
        private readonly ProgressBar _bar = new ProgressBar { Maximum = 1000, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Button _start;
        private readonly Button _stop;
        private readonly Button _show;
        private CancellationTokenSource _cancel;
        private string _size = "normal";

        /// <summary>For the self-test: close without asking while working.</summary>
        public bool SkipClosePrompt { get; set; }

        public VideoWindow(Window owner, Job job)
        {
            _job = job;
            Width = 620;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "Subtitles into the video · " + job.FileName, owner);

            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            root.Children.Add(Ui.Heading("Put the subtitles into the video", 18));
            root.Children.Add(Ui.Subtle(job.FileName, 12.5, false).Margin(0, 3, 0, 14));

            _track = Option("As subtitles that can be turned on and off",
                "Quick, and the picture isn't changed. Most players show them (VLC, Windows Media Player, many TVs); if not, turn them on in the player.");
            _burn = Option("Written into the picture",
                "Always shown, on any player, phone or website. Takes longer, because the whole video is made again.");
            _track.IsChecked = true;
            _track.Checked += (s, e) => Refresh();
            _burn.Checked += (s, e) => Refresh();
            root.Children.Add(_track);
            root.Children.Add(_burn);

            foreach (var (id, text) in new[] { ("small", "Small"), ("normal", "Normal"), ("large", "Large") })
            {
                var chip = new RadioButton { Content = text, GroupName = "size-" + job.Id, IsChecked = id == _size }.Styled("ChipRadio");
                chip.Checked += (s, e) => _size = id;
                _sizes.Children.Add(chip);
            }
            _sizeRow = Ui.Column(Ui.Heading("Size of the words", 13), _sizes);
            _sizeRow.Margin = new Thickness(0, 4, 0, 4);
            root.Children.Add(_sizeRow);
            root.Children.Add(_where.Margin(0, 8, 0, 0));

            _note = Ui.Banner("IconInfo", Ui.Column(_noteText, _bar));
            root.Children.Add(_note.Margin(0, 14, 0, 0));

            root.Children.Add(Ui.Divider().Margin(0, 18, 0, 0));
            var bottom = Ui.Columns(Ui.Star(), Ui.Auto);
            _show = Ui.Button("Show the video", "IconFolder", "SecondaryButton", (s, e) =>
            {
                if (LastOutput != null && File.Exists(LastOutput)) AppInfo.OpenFolder(Path.GetDirectoryName(LastOutput), LastOutput);
            });
            _show.Visibility = Visibility.Collapsed;
            bottom.Children.Add(_show.At(0, 0));
            var close = Ui.Button("Close", null, "SecondaryButton", (s, e) => Close());
            close.IsCancel = true;
            _stop = Ui.Button("Stop", "IconStop", "SecondaryButton", (s, e) => Stop());
            _start = Ui.Button("Start", "IconPlay", "PrimaryButton", async (s, e) => await Start(_burn.IsChecked == true ? VideoMode.Burn : VideoMode.Track, _size));
            bottom.Children.Add(Ui.Row(close, _stop.Margin(8, 0, 0, 0), _start.Margin(8, 0, 0, 0)).At(0, 1));
            root.Children.Add(bottom.Margin(0, 14, 0, 0));
            Content = root;

            Refresh();
            ShowNote(Ffmpeg.IsReady()
                ? "The new video is saved beside the original, which isn't changed."
                : $"This uses FFmpeg, a free video tool. It's downloaded once ({Format.Size(Ffmpeg.Package.Size)}) when you start.");
            Closing += (s, e) =>
            {
                if (IsRunning && !SkipClosePrompt && !Dialogs.Confirm(this, "The video is being made. Stop it and close?"))
                {
                    e.Cancel = true;
                    return;
                }
                _cancel?.Cancel();
            };
        }

        public bool IsRunning => _cancel != null;
        public string LastOutput { get; private set; }
        public string Note => _noteText.Text;

        private static RadioButton Option(string title, string text)
        {
            var content = Ui.Column(Ui.Text(title, 13.5, bold: true), Ui.Subtle(text, 12, true).Margin(0, 3, 0, 0));
            var r = new RadioButton { Content = content, GroupName = "mode" }.Styled("OptionCard");
            System.Windows.Automation.AutomationProperties.SetName(r, title);
            return r;
        }

        private VideoMode Mode => _burn.IsChecked == true ? VideoMode.Burn : VideoMode.Track;

        private void Refresh()
        {
            _sizeRow.Visibility = Mode == VideoMode.Burn ? Visibility.Visible : Visibility.Collapsed;
            var target = Ffmpeg.OutputFor(_job.File, Mode, Settings.OutputFolder);
            _where.Text = "Saved as \"" + Path.GetFileName(target) + "\" " + (string.IsNullOrEmpty(Settings.OutputFolder) ? "next to the video." : "in " + Settings.OutputFolder + ".");
            _start.Visibility = IsRunning ? Visibility.Collapsed : Visibility.Visible;
            _stop.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;
            _track.IsEnabled = _burn.IsEnabled = !IsRunning;
            foreach (UIElement c in _sizes.Children) c.IsEnabled = !IsRunning;
        }

        private void ShowNote(string text, double progress = -1)
        {
            _noteText.Text = text;
            _bar.Visibility = progress >= 0 ? Visibility.Visible : Visibility.Collapsed;
            if (progress >= 0) _bar.Value = progress * 10;
        }

        public void Stop() => _cancel?.Cancel();

        /// <summary>Makes the new video. True when it was saved.</summary>
        public async Task<bool> Start(VideoMode mode, string size)
        {
            if (IsRunning) return false;
            _track.IsChecked = mode == VideoMode.Track;
            _burn.IsChecked = mode == VideoMode.Burn;
            _size = size;
            _cancel = new CancellationTokenSource();
            _show.Visibility = Visibility.Collapsed;
            Refresh();
            var token = _cancel.Token;
            string srt = null, temp = null;
            try
            {
                if (!Ffmpeg.IsReady())
                {
                    ShowNote("Getting FFmpeg, once…", 0);
                    var lastShown = DateTime.MinValue;
                    // A late progress report mustn't write over the note shown once the download has ended.
                    var getting = true;
                    try
                    {
                        await FfmpegGetter.Get((done, total) =>
                        {
                            if ((DateTime.UtcNow - lastShown).TotalMilliseconds < 200 && done < total) return;
                            lastShown = DateTime.UtcNow;
                            Dispatcher.BeginInvoke(new Action(() => { if (getting) ShowNote($"Getting FFmpeg, once: {Format.Size(done)} of {Format.Size(total)}", total > 0 ? done * 100.0 / total : 0); }));
                        }, token);
                    }
                    finally
                    {
                        getting = false;
                    }
                }

                // The subtitles as they are now (after any corrections), in the work folder under a plain name.
                var t = Transcript.Load(JobList.TranscriptFile(_job));
                if (t.Segments.Count == 0)
                {
                    ShowNote("There's no speech in this file's text, so there are no subtitles to add.");
                    return false;
                }
                Directory.CreateDirectory(Runner.WorkFolder);
                var srtName = _job.Id + "-subtitles.srt";
                srt = Path.Combine(Runner.WorkFolder, srtName);
                File.WriteAllText(srt, SubtitleFiles.Srt(t), new System.Text.UTF8Encoding(false));

                var output = Outputs.FreeName(Ffmpeg.OutputFor(_job.File, mode, Settings.OutputFolder), new string[0], _job.File);
                Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
                temp = Path.Combine(Path.GetDirectoryName(output) ?? ".", Path.GetFileNameWithoutExtension(output) + ".unfinished" + Path.GetExtension(output));
                var args = mode == VideoMode.Burn
                    ? Ffmpeg.BurnArgs(_job.File, srtName, temp, size)
                    : Ffmpeg.TrackArgs(_job.File, srt, temp, t.Language);
                var verb = mode == VideoMode.Burn ? "Writing the subtitles into the picture" : "Adding the subtitles";
                ShowNote(verb + "…", 0);
                var making = true; // as above: no late "99%" over "Saved" or the reason it failed
                try
                {
                    await Ffmpeg.Run(Ffmpeg.ExePath, args, Runner.WorkFolder,
                        p => Dispatcher.BeginInvoke(new Action(() => { if (making) ShowNote($"{verb}: {p}%", p); })), token);
                }
                finally
                {
                    making = false;
                }
                token.ThrowIfCancellationRequested();
                File.Move(temp, output);
                temp = null;
                LastOutput = output;
                _show.Visibility = Visibility.Visible;
                ShowNote("Saved \"" + Path.GetFileName(output) + "\".");
                return true;
            }
            catch (OperationCanceledException)
            {
                ShowNote("Stopped. Nothing was saved.");
                return false;
            }
            catch (DownloadFailedException ex)
            {
                ShowNote(ex.Message);
                return false;
            }
            catch (FfmpegException ex)
            {
                ShowNote("The video couldn't be made. " + ex.Message);
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Runtime.Serialization.SerializationException)
            {
                ShowNote("The video couldn't be saved: " + ex.Message);
                return false;
            }
            finally
            {
                foreach (var f in new[] { srt, temp })
                {
                    try { if (f != null && File.Exists(f)) File.Delete(f); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                _cancel = null;
                Refresh();
            }
        }
    }
}
