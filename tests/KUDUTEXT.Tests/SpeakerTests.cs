// Telling speakers apart, with the real voice model and recordings of known speakers (downloaded and checked into
// samples-cache).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using Kudutext.Core;

namespace Kudutext.Tests
{
    /// <summary>
    /// Telling speakers apart. The real voice model and two recordings with known speakers are downloaded from the
    /// sherpa-onnx project's releases (SHA-256 checked) into samples-cache, next to this project.
    /// </summary>
    public static partial class Program
    {
        /// <summary>Where the voice model and the recordings are downloaded from.</summary>
        private const string Release = "https://github.com/k2-fsa/sherpa-onnx/releases/download/";
        /// <summary>The files the tests need, each with its checksum.</summary>
        private static readonly (string Name, string Url, string Sha256)[] VoiceSamples =
        {
            ("0-four-speakers-zh.wav", Release + "speaker-segmentation-models/0-four-speakers-zh.wav", "bedf036caed208386c67b4ef4b11f83d74dd0d420b102163a1c33cd09cde7010"),
            ("1-two-speakers-en.wav", Release + "speaker-segmentation-models/1-two-speakers-en.wav", "f1c877dc01595e28be7147bf2fe38e5268147a868bf3fdb5c37b97f5940e21f3"),
            (VoiceModel.FileName, VoiceModel.Url, VoiceModel.Sha256),
        };

        private static string SamplesCache
        {
            get
            {
                var dir = AppContext.BaseDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "KUDUTEXT.Tests.csproj"))) dir = Path.GetDirectoryName(dir);
                return Path.Combine(dir ?? Path.GetTempPath(), "samples-cache");
            }
        }

        /// <summary>One of those files, downloaded and checked the first time.</summary>
        private static string VoiceSample(string name)
        {
            var s = VoiceSamples.Single(x => x.Name == name);
            var path = Path.Combine(SamplesCache, name);
            string Sum(string f) { using (var sha = SHA256.Create()) using (var st = File.OpenRead(f)) return string.Concat(sha.ComputeHash(st).Select(b => b.ToString("x2"))); }
            if (File.Exists(path) && Sum(path) == s.Sha256) return path;
            Directory.CreateDirectory(SamplesCache);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var input = http.GetStreamAsync(s.Url).GetAwaiter().GetResult())
            using (var output = File.Create(path + ".part"))
                input.CopyTo(output);
            if (Sum(path + ".part") != s.Sha256) throw new Exception("Wrong checksum for " + name);
            File.Move(path + ".part", path, true);
            return path;
        }

        /// <summary>A made-up voice print.</summary>
        private static float[] Prints(params float[] v) => Speakers.Normalise(v);

        /// <summary>Grouping voice prints: three voices found, or as many as asked for.</summary>
        private static void TestGroupVoices()
        {
            // Three voices, each a little different every time it speaks.
            var prints = new List<float[]>
            {
                Prints(1, 0.1f, 0), Prints(0, 1, 0.1f), Prints(0.9f, 0.2f, 0), Prints(0.1f, 0, 1),
                Prints(0, 0.9f, 0.2f), Prints(0.2f, 0, 0.9f), Prints(1, 0, 0.1f),
            };
            var labels = Speakers.Group(prints, 0);
            Equal(3, labels.Distinct().Count(), "speakers found");
            Check(labels[0] == labels[2] && labels[2] == labels[6], "first voice");
            Check(labels[1] == labels[4] && labels[3] == labels[5], "second and third voices");
            Equal(2, Speakers.Group(prints, 2).Distinct().Count(), "two asked for");
            Equal(1, Speakers.Group(prints.Take(1).ToList(), 0).Length, "one line");
            Equal(7, Speakers.Group(prints, 7).Distinct().Count(), "as many as lines");
        }

        /// <summary>Reading a WAV file.</summary>
        private static void TestReadWav()
        {
            var file = Path.Combine(Folder("wav"), "a.wav");
            using (var w = new BinaryWriter(File.Create(file)))
            {
                short[] data = { 0, 16384, -32768, 32767 };
                w.Write("RIFF".ToCharArray()); w.Write(36 + data.Length * 2 + 12); w.Write("WAVE".ToCharArray());
                w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
                w.Write("LIST".ToCharArray()); w.Write(4); w.Write("INFO".ToCharArray());
                w.Write("data".ToCharArray()); w.Write(data.Length * 2);
                foreach (var d in data) w.Write(d);
            }
            var samples = Wav.Read(file, out var rate);
            Equal(16000, rate, "rate");
            Equal(4, samples.Length, "samples");
            Check(Math.Abs(samples[1] - 0.5f) < 1e-4 && Math.Abs(samples[2] + 1f) < 1e-4, "values");
        }

        /// <summary>Speakers in the SRT, VTT and text files, and kept with the text.</summary>
        private static void TestSpeakersInFiles()
        {
            var t = Sample();
            t.Segments.Add(new Segment { StartMs = 3723_456, EndMs = 3725_000, Text = "your country" });
            t.Segments[0].Speaker = 0;
            t.Segments[1].Speaker = 1;
            t.Segments[2].Speaker = 1;
            t.SpeakerNames = new List<string> { "Speaker 1", "Jack <K>" };
            Check(SubtitleFiles.Srt(t).Contains("Speaker 1: And so my fellow Americans,") && SubtitleFiles.Srt(t).Contains("Jack <K>: ask not"), "srt");
            Check(SubtitleFiles.Vtt(t).Contains("<v Speaker 1>And so") && SubtitleFiles.Vtt(t).Contains("<v Jack K>ask not"), "vtt");
            var nl = Environment.NewLine;
            Equal("Speaker 1: And so my fellow Americans," + nl + nl + "Jack <K>: ask not --> what your country", t.PlainText, "txt");

            var file = Path.Combine(Folder("texts"), "speakers.json");
            t.Save(file);
            var back = Transcript.Load(file);
            Equal("Jack <K>", back.SpeakerOf(back.Segments[2]), "kept");
            Equal(0, back.Segments[0].Speaker, "first speaker kept");
            Check(!Sample().HasSpeakers && Sample().Segments[0].Speaker == -1, "none by default");
            var plain = Path.Combine(_root, "texts", "plain.json");
            Sample().Save(plain);
            Check(!File.ReadAllText(plain).Contains("speaker"), "files without speakers stay as before");

            var built = TextEdits.Build("en", new[] { (0L, 1000L, " hi ", 1), (1000L, 2000L, " ", 0) }, new List<string> { "A", "B" });
            Equal(1, built.Segments.Count, "empty left out");
            Equal("B", built.SpeakerOf(built.Segments[0]), "speaker of a line");
        }

        /// <summary>The sound's bands match those of a separate numpy version.</summary>
        private static void TestVoiceBands()
        {
            // Checked against a separate implementation of Kaldi's fbank (with numpy).
            var samples = Wav.Read(VoiceSample("0-four-speakers-zh.wav"), out var rate);
            Equal(16000, rate, "rate");
            var bands = Fbank.Compute(samples, 0, 24000, out var frames);
            Equal(148, frames, "frames");
            var expected = new[] { (0, 0, -0.1052), (0, 10, -4.3893), (0, 40, -4.5245), (0, 79, 0.2559), (50, 0, -0.2411), (50, 10, -2.9066), (50, 40, -2.0161), (50, 79, -1.0103) };
            foreach (var (t, b, v) in expected)
                Check(Math.Abs(bands[t * Fbank.Bands + b] - v) < 0.002, $"frame {t} band {b}: {bands[t * Fbank.Bands + b]} not {v}");
            Equal(0, Fbank.Compute(samples, 0, 399, out frames).Length + frames, "too short");
        }

        /// <summary>Three known voices in real recordings are told apart.</summary>
        private static void TestTellSpeakersApart()
        {
            var zh = Wav.Read(VoiceSample("0-four-speakers-zh.wav"), out _);
            var en = Wav.Read(VoiceSample("1-two-speakers-en.wav"), out _);
            const int w = 24000; // 1.5 seconds
            // Known parts: the first speaker of the Chinese recording, its third speaker, and one of the English speakers.
            var parts = new List<(float[] From, int Index, char Who)>();
            parts.AddRange(new[] { 0, 1, 2, 3, 4 }.Select(i => (zh, i, 'A')));
            parts.AddRange(new[] { 9, 10, 11 }.Select(i => (zh, i, 'C')));
            parts.AddRange(new[] { 6, 7, 8, 9 }.Select(i => (en, i, 'E')));
            var order = new[] { 7, 2, 10, 0, 5, 11, 3, 8, 1, 9, 4, 6 };
            var samples = new float[order.Length * w];
            var t = new Transcript { Language = "en", Segments = new List<Segment>() };
            for (var k = 0; k < order.Length; k++)
            {
                var p = parts[order[k]];
                Array.Copy(p.From, p.Index * w, samples, k * w, w);
                t.Segments.Add(new Segment { StartMs = k * 1500, EndMs = (k + 1) * 1500, Text = p.Who.ToString() });
            }
            t.Segments.Add(new Segment { StartMs = 18000, EndMs = 18300, Text = "short" });

            using (var model = new OnnxVoicePrints(VoiceSample(VoiceModel.FileName)))
            {
                var print = Speakers.Normalise(model.Print(Fbank.Compute(zh, 0, w, out var frames), frames));
                Equal(256, print.Length, "voice print size");
                var expected = new[] { -0.06069, 0.05762, -0.00961, -0.16293, -0.0318, -0.07977 };
                for (var i = 0; i < expected.Length; i++) Check(Math.Abs(print[i] - expected[i]) < 0.002, $"print {i}: {print[i]} not {expected[i]}");

                foreach (var count in new[] { 0, 3 })
                {
                    var steps = new List<int>();
                    Speakers.Assign(t, samples, model, count, steps.Add);
                    Equal(3, t.SpeakerNames.Count, $"speakers found (asked for {count})");
                    Equal("Speaker 1", t.SpeakerNames[0], "name");
                    Equal(0, t.Segments[0].Speaker, "the first to speak is Speaker 1");
                    for (var a = 0; a < order.Length; a++)
                        for (var b = 0; b < order.Length; b++)
                            Equal(t.Segments[a].Text == t.Segments[b].Text, t.Segments[a].Speaker == t.Segments[b].Speaker, $"lines {a} and {b}");
                    Equal(t.Segments[11].Speaker, t.Segments[12].Speaker, "a short line takes its neighbour's speaker");
                    Equal(100, steps.Last(), "progress");
                }
            }
        }
    
        /// <summary>The runner finds speakers when asked, and keeps the text when that fails.</summary>
        private static void TestRunnerFindsSpeakers()
        {
            var dir = Folder("run-speakers");
            foreach (var fails in new[] { false, true })
            {
                var job = new Job { File = Path.Combine(dir, fails ? "b.mp4" : "a.mp4"), Model = "fast", Language = "auto", FindSpeakers = true, SpeakerCount = 2 };
                File.WriteAllText(job.File, "video");
                var runner = FakeRunner(null);
                var decode = runner.Decode;
                runner.Decode = (source, wav, progress, cancel) =>
                {
                    var length = decode(source, wav, progress, cancel);
                    using (var w = new BinaryWriter(File.Create(wav)))
                    {
                        w.Write("RIFF".ToCharArray()); w.Write(36 + 4); w.Write("WAVE".ToCharArray());
                        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
                        w.Write("data".ToCharArray()); w.Write(4); w.Write((short)1); w.Write((short)2);
                    }
                    return length;
                };
                int asked = -1, heard = -1;
                runner.FindSpeakers = (t, samples, count, progress, cancel) =>
                {
                    asked = count;
                    heard = samples.Length;
                    if (fails) throw new InvalidOperationException("no voice model");
                    t.Segments[0].Speaker = 0;
                    t.Segments[1].Speaker = 1;
                    t.SpeakerNames = new List<string> { "Speaker 1", "Speaker 2" };
                };
                runner.Do(job, new[] { "srt" }, "", null, CancellationToken.None).GetAwaiter().GetResult();
                Equal(JobState.Done, job.State, "done");
                Equal(2, asked, "number of speakers passed on");
                Equal(2, heard, "the sound passed on");
                var srt = File.ReadAllText(job.Outputs.Single());
                Equal(!fails, srt.Contains("Speaker 2: ask not"), "speakers in the subtitles");
                Equal(!fails, Transcript.Load(JobList.TranscriptFile(job)).HasSpeakers, "speakers kept");
            }
        }
    }
}
