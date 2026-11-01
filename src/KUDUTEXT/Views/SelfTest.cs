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

        public static int Run(string modelFile, string samples)
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
                });
                if (Errors.Count > 0) return Finish(savedFolder, savedModels, saved);

                var mp3 = Path.Combine(_temp, "Ask not.mp3");
                File.Copy(Path.Combine(samples, "jfk.mp3"), mp3);
                Sound();
                Screens(mp3, File.ReadAllBytes(modelFile));
            }
            catch (Exception ex)
            {
                Errors.Add("self-test: " + ex);
            }
            return Finish(savedFolder, savedModels, saved);
        }

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
                MainWindow.ModelUrlOverride = null;
            }

            Step("Dark theme", () =>
            {
                ThemeManager.Apply(ThemeMode.Dark);
                Pump();
                Screenshots.All("Dark theme");
                Show(new SettingsWindow(main));
                Show(About.Build(main));
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
