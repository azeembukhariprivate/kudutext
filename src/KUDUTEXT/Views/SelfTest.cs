using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Kudutext.Core;
using NAudio.Wave;

namespace Kudutext.Views
{
    /// <summary>
    /// KUDUTEXT.exe --self-test --model ggml-tiny.bin --samples folder: checks reading sound (a WAV file, an MP3 through
    /// Windows, a file with no sound), then drives the real window: the speech model download (stopped part way and carried
    /// on), turning a real recording into text with the real speech engine, the subtitle and text files, not replacing a
    /// file that's already there, stopping, a broken file, the kept list, and the other windows in both themes.
    /// Writes self-test.log next to the exe; exits with 0 when everything passed.
    /// </summary>
    public static class SelfTest
    {
        public static bool Running { get; private set; }
        public static List<string> Errors { get; } = new List<string>();

        private static StringBuilder _log;
        private static string _temp;

        public static int Run(string modelFile, string samples, string ffmpegZip)
        {
            Running = true;
            _log = new StringBuilder();
            _temp = Path.Combine(Path.GetTempPath(), "kudutext-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_temp);
            var savedFolder = JobList.Folder;
            var savedModels = SpeechModels.Folder;
            var saved = (Settings.Model, Settings.Language, Settings.Translate, Settings.Formats, Settings.OutputFolder);
            JobList.Folder = Path.Combine(_temp, "data");
            SpeechModels.Folder = Path.Combine(_temp, "models");
            var savedFfmpeg = Ffmpeg.Folder;
            Ffmpeg.Folder = Path.Combine(_temp, "ffmpeg");
            Settings.Model = SpeechModels.Tiny.Id;
            Settings.Language = "auto";
            Settings.Translate = false;
            Settings.Formats = new List<string> { "srt", "txt" };
            Settings.OutputFolder = string.Empty;

            try
            {
                Step("Load themes and styles", () => ThemeManager.Init(ThemeMode.Light));
                Step("Theme keys exist", () =>
                {
                    foreach (var key in new[] { "WindowBg", "SurfaceBg", "Text", "SubtleText", "Accent", "BorderBrush", "InputBorder", "OkText", "BadText", "BannerBg",
                                                "PrimaryButton", "SecondaryButton", "GhostButton", "LinkButton", "ChipRadio", "Card",
                                                "IconFolder", "IconFile", "IconPlus", "IconSettings", "IconInfo", "IconImport", "IconPlay", "IconStop", "IconTrash",
                                                "IconRefresh", "IconClose", "IconCheck" })
                        Application.Current.FindResource(key);
                });
                Step("The speech engine is there", () => Expect(File.Exists(MainWindow.EnginePath), "engine\\whisper-cli.exe next to the app"));
                Step("The test files are there", () =>
                {
                    Expect(modelFile != null && File.Exists(modelFile), "--model with the tiny speech model");
                    Expect(samples != null && File.Exists(Path.Combine(samples, "jfk.mp3")), "--samples with jfk.mp3");
                    Expect(SpeechModels.Sha1Of(modelFile) == SpeechModels.Tiny.Sha1, "the tiny model's checksum");
                    Expect(ffmpegZip != null && File.Exists(ffmpegZip), "--ffmpeg with the FFmpeg download");
                    Expect(Downloads.Sha256Of(ffmpegZip) == Ffmpeg.Package.Sha256, "the FFmpeg download's checksum");
                });
                if (Errors.Count > 0)
                {
                    Ffmpeg.Folder = savedFfmpeg;
                    return Finish(savedFolder, savedModels, saved);
                }

                var mp3 = Path.Combine(_temp, "Ask not.mp3");
                File.Copy(Path.Combine(samples, "jfk.mp3"), mp3);
                Sound();
                _ffmpegZip = File.ReadAllBytes(ffmpegZip);
                Screens(mp3, File.ReadAllBytes(modelFile));
            }
            catch (Exception ex)
            {
                Errors.Add("self-test: " + ex);
            }
            Ffmpeg.Folder = savedFfmpeg;
            FfmpegGetter.UrlOverride = null;
            return Finish(savedFolder, savedModels, saved);
        }

        private static byte[] _ffmpegZip;

        private static int Finish(string folder, string models, (string Model, string Language, bool Translate, List<string> Formats, string OutputFolder) saved)
        {
            JobList.Folder = folder;
            SpeechModels.Folder = models;
            Settings.Model = saved.Model;
            Settings.Language = saved.Language;
            Settings.Translate = saved.Translate;
            Settings.Formats = saved.Formats;
            Settings.OutputFolder = saved.OutputFolder;
            _log.AppendLine(Errors.Count == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({Errors.Count})");
            foreach (var e in Errors) _log.AppendLine("  " + e);
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? ".", "self-test.log"), _log.ToString()); }
            catch { /* read-only folder */ }
            try { Directory.Delete(_temp, true); }
            catch { /* ignore */ }
            return Errors.Count == 0 ? 0 : 1;
        }

        // ================================================================== reading sound

        private static void Sound()
        {
            Step("Read a WAV file (stereo, 44.1 kHz) into 16 kHz mono", () =>
            {
                var wav = Tone("tone.wav", 3, 44100, 2);
                var output = Path.Combine(_temp, "tone-16k.wav");
                var seconds = AudioReader.Convert(wav, output, null, CancellationToken.None);
                Expect(Math.Abs(seconds - 3) < 0.1, "3 seconds, got " + seconds);
                using (var r = new WaveFileReader(output))
                {
                    Expect(r.WaveFormat.SampleRate == 16000 && r.WaveFormat.Channels == 1 && r.WaveFormat.BitsPerSample == 16, "16 kHz mono 16-bit, got " + r.WaveFormat);
                    Expect(Math.Abs(r.TotalTime.TotalSeconds - 3) < 0.1, "3 seconds long");
                }
            });
            Step("Read an MP3 through Windows", () =>
            {
                var output = Path.Combine(_temp, "mp3-16k.wav");
                try
                {
                    var seconds = AudioReader.Convert(Path.Combine(_temp, "Ask not.mp3"), output, null, CancellationToken.None);
                    Note($"read {seconds:0.0} s through Windows");
                    Expect(seconds > 10 && seconds < 12, "about 11 seconds, got " + seconds);
                }
                catch (AudioException ex)
                {
                    // Windows editions without Media Foundation (N editions, some servers): the speech engine reads MP3 itself.
                    Note("Windows couldn't read it (" + ex.InnerException?.GetType().Name + "): the speech engine reads MP3 files itself");
                }
            });
            Step("A file with no sound is refused in plain words", () =>
            {
                var bad = Path.Combine(_temp, "broken.mp4");
                File.WriteAllBytes(bad, Encoding.ASCII.GetBytes(new string('x', 5000)));
                try
                {
                    AudioReader.Convert(bad, Path.Combine(_temp, "broken.wav"), null, CancellationToken.None);
                    throw new Exception("expected a refusal");
                }
                catch (AudioException ex)
                {
                    Expect(ex.Message.StartsWith("No sound could be read"), "a plain message");
                }
                Expect(!File.Exists(Path.Combine(_temp, "broken.wav")), "no half-made file left");
            });
        }

        // ================================================================== the window

        private static void Screens(string mp3, byte[] model)
        {
            MainWindow main = null;
            Step("Empty window", () =>
            {
                main = new MainWindow();
                OffScreen(main);
                main.Show();
                Pump();
                Expect(main.Jobs.Count == 0, "an empty list");
            });
            if (main == null) return;

            using (var server = new TestServer())
            {
                server.Add("ggml-tiny.bin", model);
                MainWindow.ModelUrlOverride = server.Url("ggml-tiny.bin");
                Job job = null;

                Step("The speech model download stops part way", () =>
                {
                    server.Drop(1, model.Length / 3);
                    Expect(main.Add(new[] { mp3 }) == 1, "the recording added");
                    job = main.Jobs[0];
                    Wait(() => !main.IsBusy, 60);
                    Note(main.BannerText);
                    Expect(main.BannerShown && main.BannerText.Contains("part way"), "a note that it stopped part way");
                    Expect(job.State == JobState.Waiting, "the recording still waiting");
                    Expect(!SpeechModels.IsReady(SpeechModels.Tiny), "no model kept yet");
                });

                Step("…and carries on, then turns the speech into text", () =>
                {
                    main.Pump();
                    Wait(() => main.IsBusy, 10);
                    Wait(() => job.State == JobState.Working && job.Step == "Listening", 60);
                    Screenshots.All("Working");
                    Wait(() => !main.IsBusy, 300);
                    Expect(server.RangesAsked.Any(r => r.StartsWith("bytes=" + model.Length / 3)), "it asked for the rest only, got " + string.Join(", ", server.RangesAsked));
                    Expect(SpeechModels.IsReady(SpeechModels.Tiny), "the model kept after its checksum matched");
                    Expect(!main.BannerShown, "the note gone");
                    Expect(job.State == JobState.Done, "done, got " + job.State + " " + job.Error);
                    var t = Transcript.Load(JobList.TranscriptFile(job));
                    Note($"heard ({t.Language}): {t.PlainText}");
                    Expect(t.PlainText.IndexOf("country", StringComparison.OrdinalIgnoreCase) >= 0, "the words \"ask not what your country can do for you\"");
                    Expect(t.Language == "en", "English heard, got " + t.Language);
                    var srt = Path.Combine(_temp, "Ask not.srt");
                    var txt = Path.Combine(_temp, "Ask not.txt");
                    Expect(job.Outputs.SequenceEqual(new[] { srt, txt }), "the .srt and .txt files next to it, got " + string.Join(", ", job.Outputs));
                    Expect(File.ReadAllText(srt).StartsWith("1\r\n00:00:0"), "a subtitle file");
                    Expect(File.ReadAllText(txt).IndexOf("country", StringComparison.OrdinalIgnoreCase) >= 0, "a text file");
                    Expect(!Directory.EnumerateFiles(Runner.WorkFolder).Any(), "no work files left");
                });

                Step("All three kinds of file, in a chosen folder", () =>
                {
                    Settings.Formats = new List<string> { "srt", "vtt", "txt" };
                    Settings.OutputFolder = Path.Combine(_temp, "Subtitles");
                    Expect(main.Add(new[] { mp3 }) == 1, "added again");
                    var again = main.Jobs.Last();
                    Wait(() => !main.IsBusy, 300);
                    Expect(again.State == JobState.Done, "done, got " + again.State + " " + again.Error);
                    Expect(again.Outputs.Count == 3 && again.Outputs.All(o => Path.GetDirectoryName(o) == Settings.OutputFolder), "three files in the folder");
                    Expect(File.ReadAllText(Path.Combine(Settings.OutputFolder, "Ask not.vtt")).StartsWith("WEBVTT"), "a web subtitle file");
                    Settings.OutputFolder = string.Empty;
                    Settings.Formats = new List<string> { "srt", "txt" };
                });

                Step("A file already there isn't replaced", () =>
                {
                    var copy = Path.Combine(_temp, "Interview.mp3");
                    File.Copy(mp3, copy);
                    var mine = Path.Combine(_temp, "Interview.srt");
                    File.WriteAllText(mine, "my own subtitles");
                    main.Add(new[] { copy });
                    var j = main.Jobs.Last();
                    Wait(() => !main.IsBusy, 300);
                    Expect(j.State == JobState.Done, "done");
                    Expect(File.ReadAllText(mine) == "my own subtitles", "the file that was there is untouched");
                    Expect(j.Outputs.Contains(Path.Combine(_temp, "Interview (2).srt")), "a new name, got " + string.Join(", ", j.Outputs));
                });

                EditorWindow editor = null;
                Step("Check and correct the text", () =>
                {
                    editor = main.OpenEditor(job, modal: false);
                    Expect(editor != null, "the window opens");
                    OffScreen(editor);
                    editor.Show();
                    Pump();
                    var t = Transcript.Load(JobList.TranscriptFile(job));
                    Expect(editor.Rows.Count == t.Segments.Count && editor.Rows.Count > 0, $"every line ({t.Segments.Count}), got {editor.Rows.Count}");
                    Expect(!editor.IsDirty, "nothing changed yet");
                    Screenshots.One(editor, "Check the text");
                    var replaced = editor.ReplaceAll("country", "nation");
                    Expect(replaced == 2, "\"country\" replaced twice, got " + replaced);
                    Expect(editor.Note.StartsWith("Replaced 2 times"), "a note saying so, got " + editor.Note);
                    editor.Rows[0].Text = "Corrected: " + editor.Rows[0].Text;
                    Expect(editor.IsDirty, "the changes noticed");
                    Expect(editor.Save(), "saved");
                    Expect(!editor.IsDirty, "nothing left to save");
                    var srt = File.ReadAllText(Path.Combine(_temp, "Ask not.srt"));
                    var txt = File.ReadAllText(Path.Combine(_temp, "Ask not.txt"));
                    Expect(srt.Contains("Corrected: ") && srt.Contains("nation") && !srt.Contains("country"), "the subtitle file has the corrections");
                    Expect(txt.Contains("Corrected: ") && txt.Contains("nation"), "the text file has the corrections");
                    Expect(Transcript.Load(JobList.TranscriptFile(job)).PlainText.StartsWith("Corrected: "), "the kept text has them");
                    Note(srt.Replace("\r\n", " | "));
                });

                if (editor != null)
                {
                    Step("Split, join and remove lines", () =>
                    {
                        var before = editor.Rows.Count;
                        var text = editor.Rows[0].Text;
                        var end = editor.Rows[0].EndMs;
                        editor.Selected = 0;
                        Expect(editor.Split(text.IndexOf(" my", StringComparison.Ordinal)), "split at a space");
                        Expect(editor.Rows.Count == before + 1, "one more line");
                        var cut = text.IndexOf(" my", StringComparison.Ordinal);
                        Expect(editor.Rows[0].Text == text.Substring(0, cut).Trim() && editor.Rows[1].Text == text.Substring(cut).Trim(), "the two parts, got " + editor.Rows[0].Text + " | " + editor.Rows[1].Text);
                        Expect(editor.Rows[0].EndMs == editor.Rows[1].StartMs && editor.Rows[1].EndMs == end, "the time shared between the two");
                        Pump();
                        Screenshots.One(editor, "A line split");
                        editor.Selected = 0;
                        Expect(editor.JoinWithNext(), "joined");
                        Expect(editor.Rows.Count == before && editor.Rows[0].Text == text && editor.Rows[0].EndMs == end, "back as it was");
                        editor.Selected = 0;
                        Expect(editor.Split(text.IndexOf(" my", StringComparison.Ordinal)), "split again");
                        editor.Selected = 1;
                        Expect(editor.RemoveSelected(), "removed");
                        Expect(editor.Rows.Count == before, "the same number of lines");
                        Expect(editor.Save(), "saved");
                        var srt = File.ReadAllText(Path.Combine(_temp, "Ask not.srt"));
                        Expect(srt.Split(new[] { "-->" }, StringSplitOptions.None).Length - 1 == before, "the subtitle file has the lines");
                        Expect(job.Lines == before, "the list shows the number of lines");
                    });

                    Step("A wrong time is caught before saving", () =>
                    {
                        var start = editor.Rows[0].Start;
                        var end = editor.Rows[0].End;
                        editor.Rows[0].End = "soon";
                        Expect(editor.Rows[0].Problem != null && editor.Rows[0].Problem.Contains("isn't a time"), "a plain note on the line");
                        editor.Rows[0].Start = "00:00:05.000";
                        editor.Rows[0].End = "00:00:01.000";
                        Expect(editor.Rows[0].Problem == "It ends before it starts.", "ends before it starts, got " + editor.Rows[0].Problem);
                        Expect(!editor.Save(), "not saved");
                        Expect(editor.Note.StartsWith("Line 1 needs a look"), "says which line, got " + editor.Note);
                        Pump();
                        Screenshots.One(editor, "A wrong time");
                        editor.Rows[0].Start = start;
                        editor.Rows[0].End = end;
                        Expect(editor.Rows[0].Problem == null, "fine again");
                        Expect(editor.Save(), "saved");
                    });

                    Step("Play a line", () =>
                    {
                        editor.Selected = 0;
                        editor.PlaySelected();
                        Pump();
                        Expect(editor.IsPlaying || (editor.Note ?? "").StartsWith("The line can't be played"), "playing, or a plain reason");
                        Note(editor.IsPlaying ? "playing the line" : editor.Note);
                    });

                    Step("Close the text window", () =>
                    {
                        editor.SkipClosePrompt = true;
                        editor.Close();
                    });
                }

                Step("Stop one", () =>
                {
                    var tone = Tone("Long tone.wav", 240, 16000, 1);
                    main.Add(new[] { tone });
                    var j = main.Jobs.Last();
                    Wait(() => j.State == JobState.Working && j.Step == "Listening", 60);
                    main.StopOne(j);
                    Wait(() => !main.IsBusy, 30);
                    Expect(j.State == JobState.Cancelled, "stopped, got " + j.State);
                    Expect(!Directory.EnumerateFiles(Runner.WorkFolder).Any(), "no work files left");
                });

                Step("A broken file", () =>
                {
                    main.Add(new[] { Path.Combine(_temp, "broken.mp4") });
                    var j = main.Jobs.Last();
                    Wait(() => !main.IsBusy, 60);
                    Expect(j.State == JobState.Failed && j.Error.StartsWith("No sound could be read"), "refused in plain words, got " + j.State + " " + j.Error);
                });

                Step("The list is kept for next time", () =>
                {
                    var kept = JobList.Load();
                    Expect(kept.Count == main.Jobs.Count, $"{main.Jobs.Count} files, got {kept.Count}");
                    Expect(kept.Select(k => k.State).SequenceEqual(main.Jobs.Select(k => k.State)), "with how each one went");
                    Expect(kept[0].Outputs.Count == 2 && kept[0].Heard == "en", "with their files and language");
                });
                Videos(main, server);
                MainWindow.ModelUrlOverride = null;
            }

            Step("Dark theme", () =>
            {
                ThemeManager.Apply(ThemeMode.Dark);
                Pump();
                Screenshots.All("Dark theme");
                Show(new SettingsWindow(main));
                Show(About.Build(main));
                var first = main.Jobs.FirstOrDefault(j => j.State == JobState.Done);
                if (first != null) Show(main.OpenEditor(first, modal: false));
                var video = main.Jobs.FirstOrDefault(j => j.State == JobState.Done && Ffmpeg.IsVideo(j.File));
                if (video != null) Show(main.OpenVideo(video, modal: false));
                ThemeManager.Apply(ThemeMode.Light);
                Pump();
            });
            Step("Settings window", () => Show(new SettingsWindow(main)));
            Step("About window", () => Show(About.Build(main)));
            Step("Clear finished", () =>
            {
                main.ClearFinished();
                Pump();
                Expect(main.Jobs.Count == 0, "an empty list");
            });
            Step("Close", () =>
            {
                main.SkipClosePrompt = true;
                main.Close();
            });
        }


        // ================================================================== FFmpeg and videos

        private static void Videos(MainWindow main, TestServer server)
        {
            server.Add("ffmpeg.zip", _ffmpegZip);
            FfmpegGetter.UrlOverride = server.Url("ffmpeg.zip");

            Step("Getting FFmpeg stops part way", () =>
            {
                Show(new SettingsWindow(main));
                server.Drop(1, _ffmpegZip.Length / 3);
                var get = FfmpegGetter.Get(null, CancellationToken.None);
                Wait(() => get.IsCompleted, 120);
                Expect(get.IsFaulted && get.Exception.InnerException is DownloadFailedException d && d.Message.Contains("part way"),
                    "a note that it stopped part way, got " + get.Exception?.InnerException?.Message);
                Expect(!Ffmpeg.IsReady(), "not kept yet");
            });

            Step("…and carries on", () =>
            {
                var get = FfmpegGetter.Get(null, CancellationToken.None);
                Wait(() => get.IsCompleted, 300);
                Expect(!get.IsFaulted, "got it, " + get.Exception?.InnerException?.Message);
                Expect(Ffmpeg.IsReady(), "kept after its checksum matched");
                Expect(server.RangesAsked.Any(r => r.StartsWith("bytes=" + _ffmpegZip.Length / 3)), "it asked for the rest only");
                Expect(File.Exists(Path.Combine(Ffmpeg.Folder, "LICENSE.txt")), "its licence kept beside it");
            });
            if (!Ffmpeg.IsReady()) return;

            var talk = Path.Combine(_temp, "Talk.mp4");
            Job job = null;
            Step("Turn a video's speech into text", () =>
            {
                // A test video: a plain picture with the recording as its sound.
                var make = Ffmpeg.Run(Ffmpeg.ExePath, new[]
                {
                    "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=0x335577:s=640x360:r=25", "-i", Path.Combine(_temp, "Ask not.mp3"),
                    "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", talk
                }, _temp, null, CancellationToken.None);
                Wait(() => make.IsCompleted, 120);
                Expect(!make.IsFaulted && File.Exists(talk), "a test video made, " + make.Exception?.InnerException?.Message);
                Expect(main.Add(new[] { talk }) == 1, "added");
                job = main.Jobs.Last();
                Wait(() => !main.IsBusy, 300);
                Expect(job.State == JobState.Done, "done, got " + job.State + " " + job.Error);
                Expect(Transcript.Load(JobList.TranscriptFile(job)).PlainText.IndexOf("country", StringComparison.OrdinalIgnoreCase) >= 0, "the words heard");
            });
            if (job?.State != JobState.Done) return;

            Step("Read sound with FFmpeg", () =>
            {
                var wav = Path.Combine(_temp, "via-ffmpeg.wav");
                var seconds = AudioReader.ViaFfmpeg(talk, wav, null, CancellationToken.None);
                Expect(seconds > 10 && seconds < 12, "about 11 seconds, got " + seconds);
            });

            Step("Subtitles as a track", () =>
            {
                var w = main.OpenVideo(job, modal: false);
                OffScreen(w);
                w.Show();
                Pump();
                Screenshots.One(w, "Subtitles into the video");
                var run = w.Start(VideoMode.Track, "normal");
                Wait(() => run.IsCompleted, 300);
                Expect(run.Result, "made, got: " + w.Note);
                Expect(w.LastOutput == Path.Combine(_temp, "Talk (subtitles).mp4") && File.Exists(w.LastOutput), "saved beside the video, got " + w.LastOutput);
                var info = Probe(w.LastOutput);
                Expect(info.Contains("Subtitle: mov_text") && info.Contains("(eng)"), "a subtitle track marked English");
                Expect(info.Contains("Video: h264"), "the picture kept");
                Screenshots.One(w, "Subtitles added");
                w.Close();
            });

            Step("Subtitles written into the picture", () =>
            {
                var w = main.OpenVideo(job, modal: false);
                OffScreen(w);
                w.Show();
                var run = w.Start(VideoMode.Burn, "normal");
                Wait(() => run.IsCompleted, 600);
                Expect(run.Result, "made, got: " + w.Note);
                Expect(w.LastOutput == Path.Combine(_temp, "Talk (subtitles in picture).mp4") && File.Exists(w.LastOutput), "saved, got " + w.LastOutput);
                var info = Probe(w.LastOutput);
                Expect(info.Contains("Video: h264") && !info.Contains("Subtitle:"), "one picture, no separate track");
                // The words are in the picture: a frame differs from the original's at the same moment.
                var before = Frame(talk, 3);
                var after = Frame(w.LastOutput, 3);
                var changed = before.Zip(after, (a, b) => Math.Abs(a - b) > 40 ? 1 : 0).Sum();
                Note($"{changed} of {before.Length} points changed by the words");
                Expect(changed > 30, "words drawn on the picture");
                w.Close();
            });

            Step("Stopping a video part way leaves nothing behind", () =>
            {
                var w = main.OpenVideo(job, modal: false);
                OffScreen(w);
                w.Show();
                var run = w.Start(VideoMode.Burn, "large");
                Wait(() => w.IsRunning, 10);
                w.Stop();
                Wait(() => run.IsCompleted, 60);
                Expect(!run.Result && w.Note.StartsWith("Stopped"), "stopped, got " + w.Note);
                Expect(!Directory.EnumerateFiles(_temp, "*.unfinished.*").Any(), "no unfinished video left");
                Expect(!File.Exists(Path.Combine(_temp, "Talk (subtitles in picture) (2).mp4")), "nothing saved");
                Expect(!Directory.EnumerateFiles(Runner.WorkFolder, "*.srt").Any(), "no work files left");
                w.SkipClosePrompt = true;
                w.Close();
            });
        }

        /// <summary>What FFmpeg says about a file (its streams).</summary>
        private static string Probe(string file)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Ffmpeg.ExePath, Args.Join(new[] { "-hide_banner", "-nostdin", "-i", file }))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                var text = p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                return text;
            }
        }

        /// <summary>A small grey picture of one moment of a video, as bytes.</summary>
        private static byte[] Frame(string file, double seconds)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Ffmpeg.ExePath, Args.Join(new[]
            {
                "-hide_banner", "-nostdin", "-v", "error", "-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", file,
                "-frames:v", "1", "-vf", "scale=160:90", "-pix_fmt", "gray", "-f", "rawvideo", "-"
            }))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            using (var ms = new MemoryStream())
            {
                p.StandardOutput.BaseStream.CopyTo(ms);
                p.WaitForExit(30000);
                Expect(ms.Length == 160 * 90, "a picture from " + Path.GetFileName(file));
                return ms.ToArray();
            }
        }

        // ================================================================== helpers

        /// <summary>A WAV file with a soft tone (no speech).</summary>
        private static string Tone(string name, int seconds, int rate, int channels)
        {
            var path = Path.Combine(_temp, name);
            using (var w = new WaveFileWriter(path, new WaveFormat(rate, 16, channels)))
            {
                var frame = new byte[2 * channels];
                for (var i = 0; i < rate * seconds; i++)
                {
                    var v = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 3000);
                    for (var c = 0; c < channels; c++)
                    {
                        frame[c * 2] = (byte)(v & 0xFF);
                        frame[c * 2 + 1] = (byte)((v >> 8) & 0xFF);
                    }
                    w.Write(frame, 0, frame.Length);
                }
            }
            return path;
        }

        private static void Step(string name, Action action)
        {
            var before = Errors.Count;
            var started = DateTime.UtcNow;
            try
            {
                action();
                Pump();
                Screenshots.All(name);
                if (Errors.Count > before) throw new Exception(string.Join(" | ", Errors.Skip(before)));
                _log.AppendLine($"ok    {name} ({(DateTime.UtcNow - started).TotalSeconds:0.0}s)");
            }
            catch (Exception ex)
            {
                if (Errors.Count == before) Errors.Add(name + ": " + ex.Message);
                _log.AppendLine("FAIL  " + name + ": " + ex);
            }
        }

        private static void Note(string text) => _log.AppendLine("      " + text);

        private static void Expect(bool condition, string what)
        {
            if (!condition) throw new Exception("expected " + what);
        }

        private static void Wait(Func<bool> condition, int seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > until) throw new TimeoutException($"timed out after {seconds} s");
                Pump();
                Thread.Sleep(30);
            }
            Pump();
        }

        private static void Pump()
        {
            foreach (Window w in Application.Current.Windows) w.UpdateLayout();
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(20);
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        private static void OffScreen(Window w)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.WindowState = WindowState.Normal;
            w.Left = -20000;
            w.Top = -20000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
        }

        private static void Show(Window w)
        {
            OffScreen(w);
            w.Show();
            Pump();
            Screenshots.One(w, w.Title);
            w.Close();
        }
    }
}
