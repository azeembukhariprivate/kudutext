using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kudutext.Core;

namespace Kudutext.Tests
{
    public static class Program
    {
        private static int _failed;
        private static int _passed;
        private static string _root;

        public static int Main()
        {
            _root = Path.Combine(Path.GetTempPath(), "kudutext-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            JobList.Folder = Path.Combine(_root, "data");
            SpeechModels.Folder = Path.Combine(_root, "models");
            try
            {
                foreach (var m in typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name.StartsWith("Test")))
                {
                    try
                    {
                        m.Invoke(null, null);
                        _passed++;
                        Console.WriteLine("PASS " + m.Name);
                    }
                    catch (TargetInvocationException ex)
                    {
                        _failed++;
                        Console.WriteLine("FAIL " + m.Name + ": " + ex.InnerException);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(_root, true); }
                catch { /* ignore */ }
            }
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ================================================================== subtitle and text files

        private static Transcript Sample() => new Transcript
        {
            Language = "en",
            Segments = new List<Segment>
            {
                new Segment { StartMs = 0, EndMs = 2500, Text = "And so my fellow Americans," },
                new Segment { StartMs = 2500, EndMs = 3723_456, Text = "ask not --> what" }
            }
        };

        private static void TestOtherLanguages() => LangTests.Run();


        private static void TestSrt()
        {
            Equal("1\r\n00:00:00,000 --> 00:00:02,500\r\nAnd so my fellow Americans,\r\n\r\n" +
                  "2\r\n00:00:02,500 --> 01:02:03,456\r\nask not --> what\r\n\r\n", SubtitleFiles.Srt(Sample()), "srt");
        }

        private static void TestVtt()
        {
            Equal("WEBVTT\n\n00:00:00.000 --> 00:00:02.500\nAnd so my fellow Americans,\n\n" +
                  "00:00:02.500 --> 01:02:03.456\nask not -> what\n\n", SubtitleFiles.Vtt(Sample()), "vtt, with no arrow in the text");
        }

        private static void TestPlainText()
        {
            Equal("And so my fellow Americans," + Environment.NewLine + "ask not --> what" + Environment.NewLine, SubtitleFiles.Write(Sample(), "txt"), "txt");
        }

        private static void TestTimes()
        {
            Equal("00:00:00,000", SubtitleFiles.Time(TimeSpan.FromSeconds(-3), ','), "below zero");
            Equal("27:46:40.000", SubtitleFiles.Time(TimeSpan.FromSeconds(100000), '.'), "over a day");
            Equal("00:01:01,001", SubtitleFiles.Time(TimeSpan.FromMilliseconds(61001), ','), "a minute");
        }

        private static void TestWhereFilesGo()
        {
            var video = Path.Combine(_root, "talk.final.mp4");
            Equal(Path.Combine(_root, "talk.final.srt"), SubtitleFiles.PathFor(video, "srt", ""), "next to it");
            Equal(Path.Combine(_root, "out", "talk.final.vtt"), SubtitleFiles.PathFor(video, "vtt", Path.Combine(_root, "out")), "in a folder");
        }

        // ================================================================== the speech engine

        private const string EngineJson = @"{
	""systeminfo"": ""WHISPER : CPU : AVX = 1 | "",
	""model"": { ""type"": ""tiny"", ""multilingual"": true },
	""params"": { ""model"": ""ggml-tiny.bin"", ""language"": ""auto"", ""translate"": false },
	""result"": { ""language"": ""ml"" },
	""transcription"": [
		{ ""timestamps"": { ""from"": ""00:00:00,000"", ""to"": ""00:00:01,000"" }, ""offsets"": { ""from"": 0, ""to"": 1000 }, ""text"": "" [BLANK_AUDIO]"" },
		{ ""timestamps"": { ""from"": ""00:00:01,000"", ""to"": ""00:00:04,200"" }, ""offsets"": { ""from"": 1000, ""to"": 4200 }, ""text"": "" \u0d28\u0d2e\u0d38\u0d4d\u0d15\u0d3e\u0d30\u0d02, \""friends\""! "" },
		{ ""timestamps"": { ""from"": ""00:00:04,200"", ""to"": ""00:00:04,100"" }, ""offsets"": { ""from"": 4200, ""to"": 4100 }, ""text"": "" Back\\slash "" },
		{ ""timestamps"": { ""from"": ""00:00:05,000"", ""to"": ""00:00:06,000"" }, ""offsets"": { ""from"": 5000, ""to"": 6000 }, ""text"": ""   "" }
	]
}";

        private static void TestReadEngineAnswer()
        {
            var t = WhisperJson.Parse(EngineJson);
            Equal("ml", t.Language, "language");
            Equal(2, t.Segments.Count, "silence notes and empty lines left out");
            Equal("നമസ്കാരം, \"friends\"!", t.Segments[0].Text, "trimmed, other letters and quotes kept");
            Equal(1000L, t.Segments[0].StartMs, "start");
            Equal(4200L, t.Segments[0].EndMs, "end");
            Equal("Back\\slash", t.Segments[1].Text, "backslash");
            Equal(4200L, t.Segments[1].EndMs, "an end before the start is moved to the start");
            Equal(0, WhisperJson.Parse(@"{""result"":{""language"":""en""},""transcription"":[]}").Segments.Count, "nothing said");
        }

        private static void TestCleanLines()
        {
            Equal("", WhisperJson.Clean(" [Music] "), "a note");
            Equal("[1] is the answer", WhisperJson.Clean(" [1] is the answer "), "brackets inside text stay");
            Equal("", WhisperJson.Clean(null), "nothing");
        }

        private static void TestEngineArguments()
        {
            var a = Whisper.Arguments(@"C:\m\ggml-small.bin", @"C:\w\a.wav", @"C:\w\x-text", "", false, 7);
            Equal("-m|C:\\m\\ggml-small.bin|-f|C:\\w\\a.wav|-l|auto|-t|7|-oj|-of|C:\\w\\x-text|-pp", string.Join("|", a), "arguments");
            var b = Whisper.Arguments("m", "w", "o", "ml", true, 0);
            Check(b.Contains("-tr") && b[b.IndexOf("-l") + 1] == "ml" && b[b.IndexOf("-t") + 1] == "1", "translate, a language, at least one thread");
            Equal("\"C:\\My Videos\\a b.wav\"", Args.Quote(@"C:\My Videos\a b.wav"), "a path with spaces is quoted");
        }

        private static void TestEngineProgress()
        {
            Equal(45, Whisper.Progress("whisper_print_progress_callback: progress =  45%"), "a progress line");
            Equal(100, Whisper.Progress("whisper_print_progress_callback: progress = 272%"), "never above 100");
            Equal(-1, Whisper.Progress("whisper_full_with_state: auto-detected language: en (p = 0.97)"), "another line");
            Equal(-1, Whisper.Progress(null), "nothing");
            Check(Whisper.Threads >= 1 && Whisper.Threads <= 16, "threads");
        }

        private static void TestLanguages()
        {
            Equal("auto", Languages.All[0].Key, "found by itself comes first");
            Equal(100, Languages.All.Count - 1, "every language the engine knows");
            Equal("Malayalam", Languages.Name("ml"), "a name");
            Equal("xx", Languages.Name("xx"), "an unknown code");
            Check(Languages.Known("yue") && !Languages.Known("xx"), "known");
            Equal(Languages.All.Count, Languages.All.Select(l => l.Key).Distinct().Count(), "no code twice");
        }

        // ================================================================== speech models

        private static void TestModelChoices()
        {
            Equal("good", SpeechModels.Default.Id, "Good is the usual choice");
            Equal("ggml-small.bin", SpeechModels.Find("good").FileName, "good is small");
            Equal("ggml-base.bin", SpeechModels.Find("fast").FileName, "fast is base");
            Equal("ggml-medium.bin", SpeechModels.Find("best").FileName, "best is medium");
            Equal(SpeechModels.Default, SpeechModels.Find("nonsense"), "unknown falls back");
            Equal(SpeechModels.Tiny, SpeechModels.Find("tiny"), "tiny for the self-test");
            foreach (var m in SpeechModels.All.Concat(new[] { SpeechModels.Tiny }))
            {
                Check(m.Sha1.Length == 40 && m.Sha1.All(Uri.IsHexDigit), m.Id + " checksum");
                Check(m.Url == "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + m.FileName, m.Id + " address");
            }
        }

        private static (SpeechModel Model, byte[] Body) FakeModel(int size, string name)
        {
            var body = new byte[size];
            new Random(size).NextBytes(body);
            var sha = string.Concat(SHA1.HashData(body).Select(b => b.ToString("x2")));
            return (new SpeechModel { Id = name, Name = name, FileName = name + ".bin", Size = size, Sha1 = sha }, body);
        }

        private static void TestModelDownload()
        {
            var (model, body) = FakeModel(300_000, "plain");
            using (var server = new TestServer())
            using (var http = new HttpClient())
            {
                server.Add("m.bin", body);
                Check(!SpeechModels.IsReady(model), "not ready before");
                long last = 0;
                SpeechModels.Download(http, model, (done, total) => last = done, CancellationToken.None, server.Url("m.bin")).GetAwaiter().GetResult();
                Check(SpeechModels.IsReady(model), "ready after");
                Equal(300_000L, last, "progress reached the end");
                Check(File.ReadAllBytes(SpeechModels.PathOf(model)).SequenceEqual(body), "the same bytes");
                Check(!File.Exists(SpeechModels.PathOf(model) + ".part"), "no part left");
            }
        }

        private static void TestModelDownloadCarriesOn()
        {
            var (model, body) = FakeModel(500_000, "dropped");
            using (var server = new TestServer())
            using (var http = new HttpClient())
            {
                server.Add("m.bin", body);
                server.Drop(1, 200_000);
                try
                {
                    SpeechModels.Download(http, model, null, CancellationToken.None, server.Url("m.bin")).GetAwaiter().GetResult();
                    throw new Exception("expected the download to stop");
                }
                catch (DownloadFailedException ex)
                {
                    Check(ex.Message.Contains("part way"), "a plain reason");
                }
                Check(!SpeechModels.IsReady(model), "not ready after a drop");
                Equal(200_000L, new FileInfo(SpeechModels.PathOf(model) + ".part").Length, "the part kept");
                SpeechModels.Download(http, model, null, CancellationToken.None, server.Url("m.bin")).GetAwaiter().GetResult();
                Check(server.RangesAsked.Contains("bytes=200000-"), "asked for the rest only");
                Check(SpeechModels.IsReady(model) && File.ReadAllBytes(SpeechModels.PathOf(model)).SequenceEqual(body), "complete and right");
            }
        }

        private static void TestModelDownloadRefusesWrongChecksum()
        {
            var (model, body) = FakeModel(100_000, "wrong");
            using (var server = new TestServer())
            using (var http = new HttpClient())
            {
                var other = (byte[])body.Clone();
                other[500] ^= 0xFF;
                server.Add("m.bin", other);
                try
                {
                    SpeechModels.Download(http, model, null, CancellationToken.None, server.Url("m.bin")).GetAwaiter().GetResult();
                    throw new Exception("expected a refusal");
                }
                catch (DownloadFailedException ex)
                {
                    Check(ex.Message.Contains("didn't check out"), "a plain reason");
                }
                Check(!File.Exists(SpeechModels.PathOf(model)) && !File.Exists(SpeechModels.PathOf(model) + ".part"), "nothing kept");
                Check(!SpeechModels.IsReady(model), "not ready");
            }
        }

        private static void TestModelDownloadMissing()
        {
            var (model, _) = FakeModel(1000, "missing");
            using (var server = new TestServer())
            using (var http = new HttpClient())
            {
                try
                {
                    SpeechModels.Download(http, model, null, CancellationToken.None, server.Url("nothing.bin")).GetAwaiter().GetResult();
                    throw new Exception("expected a refusal");
                }
                catch (DownloadFailedException ex)
                {
                    Check(ex.Message.Contains("404"), "says what the site said");
                }
            }
        }

        private static void TestModelNotReadyWhenNoteIsWrong()
        {
            var (model, body) = FakeModel(1000, "note");
            Directory.CreateDirectory(SpeechModels.Folder);
            File.WriteAllBytes(SpeechModels.PathOf(model), body);
            Check(!SpeechModels.IsReady(model), "no note: not ready");
            File.WriteAllText(SpeechModels.PathOf(model) + ".ok", "something else");
            Check(!SpeechModels.IsReady(model), "a wrong note: not ready");
            File.WriteAllText(SpeechModels.PathOf(model) + ".ok", model.Sha1);
            Check(SpeechModels.IsReady(model), "the right note: ready");
        }

        // ================================================================== the list

        private static void TestKindsOfFiles()
        {
            Check(JobList.CanTake(@"C:\a\Talk.MP4") && JobList.CanTake("x.mkv") && JobList.CanTake("y.m4a") && JobList.CanTake("z.opus"), "videos and recordings");
            Check(!JobList.CanTake("notes.txt") && !JobList.CanTake("photo.jpg") && !JobList.CanTake("noext") && !JobList.CanTake(null), "other files");
        }

        private static void TestListKept()
        {
            var jobs = new List<Job>
            {
                new Job { File = @"C:\v\one.mp4", State = JobState.Done, Outputs = new List<string> { @"C:\v\one.srt" }, Heard = "en", Lines = 12, LengthSeconds = 61.5, Model = "good" },
                new Job { File = @"C:\v\two.mp3", State = JobState.Working, Language = "ml", Translate = true },
                new Job { File = @"C:\v\three.wav", State = JobState.Failed, Error = "No sound" }
            };
            JobList.Save(jobs);
            JobList.Save(jobs); // replacing works too
            var back = JobList.Load();
            Equal(3, back.Count, "three files");
            Equal(JobState.Done, back[0].State, "done");
            Equal(@"C:\v\one.srt", back[0].Outputs.Single(), "its files");
            Equal(61.5, back[0].LengthSeconds, "its length");
            Equal("en", back[0].Heard, "the language heard");
            Equal(JobState.Waiting, back[1].State, "one being done when the app closed waits again");
            Check(back[1].Translate && back[1].Language == "ml", "its choices");
            Equal("No sound", back[2].Error, "why it failed");
            Equal(jobs[0].Id, back[0].Id, "the same id");
            File.WriteAllText(JobList.ListFile, "{ not json");
            Equal(0, JobList.Load().Count, "a damaged list starts empty");
            File.Delete(JobList.ListFile);
            Equal(0, JobList.Load().Count, "no list yet");
        }

        private static void TestTranscriptKept()
        {
            var file = Path.Combine(_root, "texts", "a.json");
            Sample().Save(file);
            Sample().Save(file);
            var t = Transcript.Load(file);
            Equal("en", t.Language, "language");
            Equal(2, t.Segments.Count, "lines");
            Equal("ask not --> what", t.Segments[1].Text, "text");
            Equal(3723_456L, t.Segments[1].EndMs, "times");
        }

        // ================================================================== writing the files

        private static string Folder(string name)
        {
            var d = Path.Combine(_root, name);
            Directory.CreateDirectory(d);
            return d;
        }

        private static void TestOutputsNextToFile()
        {
            var dir = Folder("next");
            var job = new Job { File = Path.Combine(dir, "talk.mp4") };
            var outs = Outputs.Write(job, Sample(), new[] { "srt", "txt", "bogus", "srt" }, "");
            Check(outs.SequenceEqual(new[] { Path.Combine(dir, "talk.srt"), Path.Combine(dir, "talk.txt") }), "srt and txt, once each: " + string.Join(", ", outs));
            var bytes = File.ReadAllBytes(outs[0]);
            Check(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "srt marked as UTF-8 for players");
            Equal(SubtitleFiles.Srt(Sample()), File.ReadAllText(outs[0]), "srt text");
        }

        private static void TestOutputsInFolder()
        {
            var dir = Folder("src");
            var target = Path.Combine(_root, "subs", "new");
            var job = new Job { File = Path.Combine(dir, "talk.mp4") };
            var outs = Outputs.Write(job, Sample(), new[] { "vtt" }, target);
            Equal(Path.Combine(target, "talk.vtt"), outs.Single(), "in the folder (made if needed)");
            Check(File.ReadAllBytes(outs[0])[0] == (byte)'W', "vtt starts with WEBVTT, no mark");
        }

        private static void TestOutputsNeverReplaceOtherFiles()
        {
            var dir = Folder("keep");
            var job = new Job { File = Path.Combine(dir, "talk.mp4") };
            File.WriteAllText(Path.Combine(dir, "talk.srt"), "mine");
            File.WriteAllText(Path.Combine(dir, "talk (2).srt"), "mine too");
            var outs = Outputs.Write(job, Sample(), new[] { "srt" }, "");
            Equal(Path.Combine(dir, "talk (3).srt"), outs.Single(), "a free name");
            Equal("mine", File.ReadAllText(Path.Combine(dir, "talk.srt")), "the first untouched");
            Equal("mine too", File.ReadAllText(Path.Combine(dir, "talk (2).srt")), "the second untouched");

            // Doing it again replaces its own file instead of making another.
            job.Outputs = outs;
            var again = Outputs.Write(job, Sample(), new[] { "srt" }, "");
            Equal(outs.Single(), again.Single(), "its own file is replaced");
        }

        private static void TestOutputNeverTheSourceItself()
        {
            Equal(Path.Combine(_root, "a (2).wav"), Outputs.FreeName(Path.Combine(_root, "a.wav"), new List<string>(), Path.Combine(_root, "a.wav")), "never the file itself");
        }

        // ================================================================== turning a file into text

        private static Runner FakeRunner(List<string> heardFrom, bool decodeFails = false, Func<CancellationToken, Task> during = null)
        {
            return new Runner
            {
                Decode = (source, wav, progress, cancel) =>
                {
                    if (decodeFails) throw new AudioException("No sound could be read from this file.");
                    File.WriteAllText(wav, "sound");
                    progress(50);
                    progress(100);
                    return 42.5;
                },
                Transcribe = async (model, audio, language, translate, progress, cancel) =>
                {
                    heardFrom?.Add(audio + "|" + language + "|" + translate + "|" + Path.GetFileName(model));
                    progress(50);
                    if (during != null) await during(cancel);
                    return Sample();
                }
            };
        }

        private static void TestRunnerDoesAFile()
        {
            var dir = Folder("run");
            var job = new Job { File = Path.Combine(dir, "a.mp4"), Model = "fast", Language = "auto" };
            File.WriteAllText(job.File, "video");
            var heard = new List<string>();
            var percents = new List<int>();
            FakeRunner(heard).Do(job, new[] { "srt", "txt" }, "", () => percents.Add(job.Percent), CancellationToken.None).GetAwaiter().GetResult();
            Equal(JobState.Done, job.State, "done");
            Equal(42.5, job.LengthSeconds, "its length");
            Equal("en", job.Heard, "language heard");
            Equal(2, job.Lines, "lines");
            Equal(2, job.Outputs.Count, "two files");
            Check(heard.Single().StartsWith(Path.Combine(Runner.WorkFolder, job.Id + ".wav") + "|auto|False|ggml-base.bin"), "the engine got the work file: " + heard.Single());
            Check(!File.Exists(Path.Combine(Runner.WorkFolder, job.Id + ".wav")), "the work file is removed");
            Check(File.Exists(JobList.TranscriptFile(job)), "the text is kept for checking later");
            Check(percents.Contains(Runner.ReadingShare) && percents.Contains(100) && percents.SequenceEqual(percents.OrderBy(p => p)), "progress only goes up: " + string.Join(",", percents));
            Equal(null, job.Step, "no step left");
        }

        private static void TestRunnerLetsEngineReadMp3()
        {
            var dir = Folder("mp3");
            var job = new Job { File = Path.Combine(dir, "song.mp3") };
            File.WriteAllText(job.File, "mp3");
            var heard = new List<string>();
            FakeRunner(heard, decodeFails: true).Do(job, new[] { "txt" }, "", null, CancellationToken.None).GetAwaiter().GetResult();
            Equal(JobState.Done, job.State, "done by the engine itself");
            Check(heard.Single().StartsWith(job.File + "|"), "the engine got the file itself");
        }

        private static void TestRunnerFailsInPlainWords()
        {
            var dir = Folder("fail");
            var job = new Job { File = Path.Combine(dir, "clip.mp4") };
            File.WriteAllText(job.File, "x");
            FakeRunner(null, decodeFails: true).Do(job, new[] { "srt" }, "", null, CancellationToken.None).GetAwaiter().GetResult();
            Equal(JobState.Failed, job.State, "failed");
            Equal("No sound could be read from this file.", job.Error, "why");
            Check(!File.Exists(Path.Combine(dir, "clip.srt")), "no files written");

            var gone = new Job { File = Path.Combine(dir, "gone.mp4") };
            FakeRunner(null).Do(gone, new[] { "srt" }, "", null, CancellationToken.None).GetAwaiter().GetResult();
            Equal(JobState.Failed, gone.State, "a missing file fails");
            Equal("The file isn't there any more.", gone.Error, "why");

            var engine = new Job { File = job.File };
            var r = FakeRunner(null);
            r.Transcribe = (m, a, l, t, p, c) => throw new WhisperException("The speech engine stopped (code 3).");
            r.Do(engine, new[] { "srt" }, "", null, CancellationToken.None).GetAwaiter().GetResult();
            Equal(JobState.Failed, engine.State, "the engine failing");
            Equal("The speech engine stopped (code 3).", engine.Error, "why");
        }

        private static void TestRunnerStops()
        {
            var dir = Folder("stop");
            var job = new Job { File = Path.Combine(dir, "long.mp4") };
            File.WriteAllText(job.File, "x");
            using (var cts = new CancellationTokenSource())
            {
                var r = FakeRunner(null, during: async c =>
                {
                    cts.Cancel();
                    await Task.Delay(10, c);
                });
                r.Do(job, new[] { "srt" }, "", null, cts.Token).GetAwaiter().GetResult();
            }
            Equal(JobState.Cancelled, job.State, "stopped");
            Check(!File.Exists(Path.Combine(dir, "long.srt")), "no files written");
            Check(!File.Exists(Path.Combine(Runner.WorkFolder, job.Id + ".wav")), "the work file is removed");
        }

        private static void TestRunnerTranslates()
        {
            var dir = Folder("tr");
            var job = new Job { File = Path.Combine(dir, "hindi.mp4"), Language = "hi", Translate = true };
            File.WriteAllText(job.File, "x");
            var heard = new List<string>();
            var r = FakeRunner(heard);
            r.Transcribe = (m, a, l, t, p, c) =>
            {
                heard.Add(l + "|" + t);
                var s = Sample();
                s.Language = "hi";
                return Task.FromResult(s);
            };
            r.Do(job, new[] { "txt" }, "", null, CancellationToken.None).GetAwaiter().GetResult();
            Equal("hi|True", heard.Single(), "asked to translate from Hindi");
            Equal("en", job.Heard, "the text is in English");
        }


        // ================================================================== checking and correcting

        private static void TestReadTimes()
        {
            Equal(62500L, TextEdits.ParseTime("00:01:02.500"), "full, dot");
            Equal(62500L, TextEdits.ParseTime("00:01:02,500"), "full, comma");
            Equal(62500L, TextEdits.ParseTime(" 1:02.5 "), "short");
            Equal(62500L, TextEdits.ParseTime("62.5"), "seconds");
            Equal(3723000L, TextEdits.ParseTime("1:02:03"), "hours");
            Equal(0L, TextEdits.ParseTime("0"), "zero");
            foreach (var bad in new[] { "", "soon", "1:75", "1:60:00", "-3", "1:2:3:4", "1.2.3", "00:01:02.5000", null })
                Equal((long?)null, TextEdits.ParseTime(bad), "not a time: " + bad);
            Equal("00:01:02.500", TextEdits.ShowTime(62500), "shown");
            Equal(62500L, TextEdits.ParseTime(TextEdits.ShowTime(62500)), "read back");
        }

        private static void TestSplitAndJoin()
        {
            Check(TextEdits.SplitText("ask not what your country", 7, out var a, out var b) && a == "ask not" && b == "what your country", "split at the cursor");
            Check(!TextEdits.SplitText("ask not", 0, out _, out _), "nothing before the cursor");
            Check(!TextEdits.SplitText("ask not ", 8, out _, out _), "nothing after the cursor");
            Check(!TextEdits.SplitText("ask", 99, out _, out _), "past the end");
            Equal(1000L + 3000, TextEdits.SplitTime(1000, 9000, 3, 5), "time shared by letters");
            Equal(1000L, TextEdits.SplitTime(1000, 1000, 3, 5), "no time to share");
            Equal("ask not what", TextEdits.JoinText(" ask not ", "what "), "joined with one space");
            Equal("what", TextEdits.JoinText("", "what"), "an empty part");
        }

        private static void TestReplaceWords()
        {
            Equal("ask what your nation, nation", TextEdits.Replace("ask what your country, COUNTRY", "country", "nation", false, out var n), "every one, any case");
            Equal(2, n, "counted");
            Equal("Country and nation", TextEdits.Replace("Country and country", "country", "nation", true, out var m), "matching case");
            Equal(1, m, "counted with case");
            Equal("aaa", TextEdits.Replace("aaa", "", "b", false, out var z), "nothing to find");
            Equal(0, z, "none");
            Equal("bb", TextEdits.Replace("aaaa", "aa", "b", false, out _), "no overlaps");
            Check(TextEdits.Contains("Your Country", "country", false) && !TextEdits.Contains("Your Country", "country", true), "finding");
        }

        private static void TestTimeProblems()
        {
            Equal(null, TextEdits.Problem("00:00:01.000", "00:00:02.000", 0), "fine");
            Equal(null, TextEdits.Problem("1", "1", null), "an instant is fine");
            Check(TextEdits.Problem("x", "1", null).StartsWith("The start isn't a time"), "a bad start");
            Check(TextEdits.Problem("1", "y", null).StartsWith("The end isn't a time"), "a bad end");
            Equal("It ends before it starts.", TextEdits.Problem("5", "1", null), "backwards");
            Equal("It starts before the line above.", TextEdits.Problem("1", "2", 3000), "out of order");
        }

        private static void TestBuildLeavesOutEmptyLines()
        {
            var t = TextEdits.Build("en", new[] { (0L, 1000L, " one "), (1000L, 2000L, "  "), (2000L, 3000L, "three") });
            Equal(2, t.Segments.Count, "the empty line left out");
            Equal("one", t.Segments[0].Text, "trimmed");
            Equal("en", t.Language, "language");
        }

        private static void TestRewriteOwnFiles()
        {
            var dir = Folder("rewrite");
            var job = new Job { File = Path.Combine(dir, "talk.mp4") };
            job.Outputs = Outputs.Write(job, Sample(), new[] { "srt", "vtt" }, "");
            var t = Sample();
            t.Segments[0].Text = "Corrected";
            File.Delete(job.Outputs[1]); // a file the person removed comes back
            var again = Outputs.Rewrite(job, t, new[] { "txt" }, "");
            Check(again.SequenceEqual(job.Outputs), "the same files, not the current choice");
            Check(File.ReadAllText(again[0]).Contains("Corrected") && File.ReadAllText(again[1]).Contains("Corrected"), "both corrected");
            Check(!File.Exists(Path.Combine(dir, "talk.txt")), "no new kinds made");

            var fresh = new Job { File = Path.Combine(dir, "other.mp4") };
            var made = Outputs.Rewrite(fresh, t, new[] { "txt" }, "");
            Equal(Path.Combine(dir, "other.txt"), made.Single(), "none before: made as for a new file");
        }


        // ================================================================== FFmpeg

        private static void TestFfmpegPackage()
        {
            Check(Ffmpeg.Package.Url.StartsWith("https://github.com/GyanD/codexffmpeg/releases/download/" + Ffmpeg.Package.Version + "/"), "a pinned address");
            Check(Ffmpeg.Package.Sha256.Length == 64 && Ffmpeg.Package.Sha256.All(Uri.IsHexDigit), "a SHA-256 checksum");
            Check(Ffmpeg.Package.Size > 50_000_000, "its size");
        }

        private static byte[] FakeFfmpegZip()
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                {
                    void Add(string name, string text)
                    {
                        using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text);
                    }
                    Add("ffmpeg-9-essentials/bin/ffplay.exe", "not this");
                    Add("ffmpeg-9-essentials/bin/ffmpeg.exe", "the tool");
                    Add("ffmpeg-9-essentials/LICENSE", "GPL");
                }
                return ms.ToArray();
            }
        }

        private static void TestFfmpegDownload()
        {
            var body = FakeFfmpegZip();
            var package = new FfmpegPackage { Version = "test", Size = body.Length, Sha256 = string.Concat(SHA256.HashData(body).Select(b => b.ToString("x2"))) };
            var saved = Ffmpeg.Folder;
            Ffmpeg.Folder = Path.Combine(_root, "ffmpeg");
            try
            {
                using (var server = new TestServer())
                using (var http = new HttpClient())
                {
                    server.Add("ff.zip", body);
                    server.Drop(1, body.Length / 2);
                    Check(!Ffmpeg.IsReady(package), "not there before");
                    try
                    {
                        Ffmpeg.Download(http, null, CancellationToken.None, server.Url("ff.zip"), package).GetAwaiter().GetResult();
                        throw new Exception("expected a stop");
                    }
                    catch (DownloadFailedException ex)
                    {
                        Check(ex.Message.Contains("FFmpeg") && ex.Message.Contains("part way"), "a plain reason: " + ex.Message);
                    }
                    Ffmpeg.Download(http, null, CancellationToken.None, server.Url("ff.zip"), package).GetAwaiter().GetResult();
                    Check(server.RangesAsked.Any(), "carried on");
                    Check(Ffmpeg.IsReady(package), "ready");
                    Equal("the tool", File.ReadAllText(Ffmpeg.ExePath), "ffmpeg.exe taken out of it");
                    Equal("GPL", File.ReadAllText(Path.Combine(Ffmpeg.Folder, "LICENSE.txt")), "its licence too");
                    Check(!File.Exists(Path.Combine(Ffmpeg.Folder, "ffmpeg.zip.part")), "the download removed");
                    Check(!Ffmpeg.IsReady(), "not taken for the real one");
                    Ffmpeg.Remove();
                    Check(!Ffmpeg.IsReady(package) && !File.Exists(Ffmpeg.ExePath), "removed");

                    var wrong = new FfmpegPackage { Version = "x", Size = body.Length, Sha256 = new string('0', 64) };
                    try
                    {
                        Ffmpeg.Download(http, null, CancellationToken.None, server.Url("ff.zip"), wrong).GetAwaiter().GetResult();
                        throw new Exception("expected a refusal");
                    }
                    catch (DownloadFailedException ex)
                    {
                        Check(ex.Message.Contains("didn't check out"), "a wrong checksum is refused: " + ex.Message);
                    }
                    Check(!File.Exists(Ffmpeg.ExePath), "nothing kept");
                }
            }
            finally
            {
                Ffmpeg.Folder = saved;
            }
        }

        private static void TestVideoNames()
        {
            var dir = Path.Combine(_root, "v");
            Equal(Path.Combine(dir, "talk (subtitles).mp4"), Ffmpeg.OutputFor(Path.Combine(dir, "talk.mp4"), VideoMode.Track, ""), "a track in an MP4");
            Equal(Path.Combine(dir, "talk (subtitles).mov"), Ffmpeg.OutputFor(Path.Combine(dir, "talk.MOV"), VideoMode.Track, ""), "a track in a MOV");
            Equal(Path.Combine(dir, "talk (subtitles).mkv"), Ffmpeg.OutputFor(Path.Combine(dir, "talk.mkv"), VideoMode.Track, ""), "a track in an MKV");
            Equal(Path.Combine(dir, "talk (subtitles).mkv"), Ffmpeg.OutputFor(Path.Combine(dir, "talk.avi"), VideoMode.Track, ""), "AVI can't take one: MKV");
            Equal(Path.Combine(_root, "out", "talk (subtitles in picture).mp4"), Ffmpeg.OutputFor(Path.Combine(dir, "talk.webm"), VideoMode.Burn, Path.Combine(_root, "out")), "written in: MP4, in the folder");
            Check(Ffmpeg.IsVideo("a.MP4") && Ffmpeg.IsVideo("b.mkv") && !Ffmpeg.IsVideo("c.mp3") && !Ffmpeg.IsVideo("d.wav") && !Ffmpeg.IsVideo(null), "which files are videos");
            Check(Ffmpeg.VideoKinds.All(JobList.Kinds.Contains), "every video kind can be added");
        }

        private static void TestFfmpegArguments()
        {
            var track = Ffmpeg.TrackArgs("v/talk.mp4", "w/s.srt", "v/talk (subtitles).mp4", "ml");
            Equal("-hide_banner|-nostdin|-y|-i|v/talk.mp4|-i|w/s.srt|-map|0:v|-map|0:a?|-map|1:0|-c:v|copy|-c:a|copy|-c:s|mov_text|-disposition:s:0|default|-metadata:s:s:0|language=mal|-movflags|+faststart|v/talk (subtitles).mp4",
                string.Join("|", track), "a track: copied as it is, mov_text in MP4, the language");
            var mkv = Ffmpeg.TrackArgs("a.mkv", "s.srt", "a (subtitles).mkv", "xx");
            Check(mkv[mkv.IndexOf("-c:s") + 1] == "srt" && !mkv.Contains("-movflags") && !mkv.Any(a => a.StartsWith("language=")), "MKV: SRT, no unknown language");
            var burn = Ffmpeg.BurnArgs("a.webm", "job-subtitles.srt", "a (subtitles in picture).mp4", "large");
            Equal("subtitles=job-subtitles.srt:force_style='FontSize=28,Outline=2,Shadow=1,MarginV=24'", burn[burn.IndexOf("-vf") + 1], "the subtitle drawer, with the size");
            Check(burn.Contains("libx264") && burn.Contains("aac") && burn.Last() == "a (subtitles in picture).mp4", "made again as MP4");
            try
            {
                Ffmpeg.BurnArgs("a.mp4", @"C:\w\s.srt", "o.mp4", "normal");
                throw new Exception("expected a refusal");
            }
            catch (ArgumentException) { }
            var sound = Ffmpeg.SoundArgs("in.webm", "out.wav");
            Check(string.Join(" ", sound).EndsWith("-i in.webm -vn -sn -ac 1 -ar 16000 -c:a pcm_s16le -f wav out.wav"), "sound: 16 kHz mono WAV");
            Equal(16, Ffmpeg.FontSize("small"), "small");
            Equal(21, Ffmpeg.FontSize("normal"), "normal");
            Equal("eng", Ffmpeg.LanguageCode3("en"), "English");
            Equal(null, Ffmpeg.LanguageCode3(null), "none");
        }

        private static void TestReadFfmpegLines()
        {
            Equal(62.5, Ffmpeg.Duration("  Duration: 00:01:02.50, start: 0.000000, bitrate: 128 kb/s"), "the length");
            Equal(3723.0, Ffmpeg.Duration("Duration: 01:02:03.00,"), "an hour");
            Equal(-1.0, Ffmpeg.Duration("Duration: N/A, bitrate: N/A"), "no length");
            Equal(12.34, Ffmpeg.Position("frame=  308 fps=0.0 q=-1.0 size=    1024kB time=00:00:12.34 bitrate= 680.1kbits/s speed=24.6x"), "how far");
            Equal(-1.0, Ffmpeg.Position("frame=  1 time=N/A bitrate=N/A"), "not yet");
            Equal(-1.0, Ffmpeg.Position(null), "nothing");
        }

        // ================================================================== helpers

        private static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception("Check failed: " + what);
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
        }
    }
}
