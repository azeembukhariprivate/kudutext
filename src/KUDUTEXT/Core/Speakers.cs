// Telling speakers apart: the sound of each line as the voice model hears it, a voice print per line, and lines grouped
// by whose voice they are.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Kudutext.Core
{
    /// <summary>
    /// The sound of speech as the voice model hears it: 80 frequency bands every 10 milliseconds (Kaldi's "fbank", with a
    /// Hamming window, as WeSpeaker's models were trained), each band less the band's average over the clip.
    /// </summary>
    public static class Fbank
    {
        /// <summary>Frequency bands in each 10 ms step.</summary>
        public const int Bands = 80;
        /// <summary>
        /// 16,000 samples a second; 25 ms steps (400 samples) every 10 ms (160); 512-point frequency analysis.
        /// </summary>
        private const int Rate = 16000, FrameLength = 400, FrameShift = 160, FftSize = 512;
        /// <summary>The Hamming window: softens each step's edges before the frequency analysis.</summary>
        private static readonly double[] Window = Enumerable.Range(0, FrameLength).Select(i => 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (FrameLength - 1))).ToArray();
        /// <summary>The 80 triangle filters that turn the frequencies into bands.</summary>
        private static readonly double[][] Filters = MakeFilters();

        /// <summary>A frequency on the mel scale, which spaces pitches as the ear hears them.</summary>
        private static double Mel(double f) => 1127.0 * Math.Log(1 + f / 700.0);

        /// <summary>Makes the 80 triangle filters, evenly spaced on the mel scale from 20 Hz to 8 kHz.</summary>
        private static double[][] MakeFilters()
        {
            double low = Mel(20), high = Mel(Rate / 2.0);
            var centres = Enumerable.Range(0, Bands + 2).Select(i => low + (high - low) * i / (Bands + 1)).ToArray();
            var binMel = Enumerable.Range(0, FftSize / 2 + 1).Select(i => Mel(i * (double)Rate / FftSize)).ToArray();
            var filters = new double[Bands][];
            for (var b = 0; b < Bands; b++)
            {
                filters[b] = new double[FftSize / 2 + 1];
                double l = centres[b], c = centres[b + 1], r = centres[b + 2];
                for (var k = 0; k <= FftSize / 2; k++)
                    filters[b][k] = Math.Max(0, Math.Min((binMel[k] - l) / (c - l), (r - binMel[k]) / (r - c)));
            }
            return filters;
        }

        /// <summary>
        /// The bands of 16 kHz mono sound (samples from -1 to 1), as [frames × 80] row by row. <paramref name="frames"/> is how
        /// many 10 ms steps there are (none when the sound is shorter than 25 ms).
        /// </summary>
        public static float[] Compute(float[] samples, int start, int count, out int frames)
        {
            frames = count < FrameLength ? 0 : 1 + (count - FrameLength) / FrameShift;
            var result = new float[frames * Bands];
            var re = new double[FftSize];
            var im = new double[FftSize];
            var frame = new double[FrameLength];
            const double floor = 1.1920929e-07; // the smallest step of a float near 1, as Kaldi uses
            for (var t = 0; t < frames; t++)
            {
                var offset = start + t * FrameShift;
                double mean = 0;
                for (var i = 0; i < FrameLength; i++)
                {
                    frame[i] = samples[offset + i] * 32768.0; // the model was trained on 16-bit values
                    mean += frame[i];
                }
                mean /= FrameLength;
                for (var i = 0; i < FrameLength; i++) frame[i] -= mean;
                for (var i = FrameLength - 1; i > 0; i--) frame[i] -= 0.97 * frame[i - 1];
                frame[0] -= 0.97 * frame[0];
                Array.Clear(re, 0, FftSize);
                Array.Clear(im, 0, FftSize);
                for (var i = 0; i < FrameLength; i++) re[i] = frame[i] * Window[i];
                Fft(re, im);
                for (var b = 0; b < Bands; b++)
                {
                    double energy = 0;
                    var f = Filters[b];
                    for (var k = 0; k <= FftSize / 2; k++)
                        if (f[k] > 0) energy += f[k] * (re[k] * re[k] + im[k] * im[k]);
                    result[t * Bands + b] = (float)Math.Log(Math.Max(energy, floor));
                }
            }
            // Each band less its average: the room and the microphone count for less.
            for (var b = 0; b < Bands && frames > 0; b++)
            {
                double sum = 0;
                for (var t = 0; t < frames; t++) sum += result[t * Bands + b];
                var avg = (float)(sum / frames);
                for (var t = 0; t < frames; t++) result[t * Bands + b] -= avg;
            }
            return result;
        }

        /// <summary>An in-place radix-2 FFT.</summary>
        private static void Fft(double[] re, double[] im)
        {
            var n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (var len = 2; len <= n; len <<= 1)
            {
                var angle = -2 * Math.PI / len;
                double wr = Math.Cos(angle), wi = Math.Sin(angle);
                for (var i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (var k = 0; k < len / 2; k++)
                    {
                        int a = i + k, c = i + k + len / 2;
                        var xr = re[c] * cr - im[c] * ci;
                        var xi = re[c] * ci + im[c] * cr;
                        re[c] = re[a] - xr;
                        im[c] = im[a] - xi;
                        re[a] += xr;
                        im[a] += xi;
                        var t = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = t;
                    }
                }
            }
        }
    }

    /// <summary>Something that turns speech bands into a voice print: numbers that are alike for the same voice.</summary>
    public interface IVoicePrints : IDisposable
    {
        float[] Print(float[] bands, int frames);
    }

    /// <summary>The voice model (WeSpeaker ResNet34, run with ONNX Runtime on the PC).</summary>
    public sealed class OnnxVoicePrints : IVoicePrints
    {
        /// <summary>The voice model, loaded once.</summary>
        private readonly Microsoft.ML.OnnxRuntime.InferenceSession _session;

        /// <summary>Loads the voice model (an ONNX file).</summary>
        public OnnxVoicePrints(string model)
        {
            var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
            _session = new Microsoft.ML.OnnxRuntime.InferenceSession(model, options);
        }

        /// <summary>The voice print (256 numbers) of some speech, from its bands.</summary>
        public float[] Print(float[] bands, int frames)
        {
            var input = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(bands, new[] { 1, frames, Fbank.Bands });
            using (var results = _session.Run(new[] { Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor("feats", input) }))
                return results.First().AsEnumerable<float>().ToArray();
        }

        /// <summary>Lets the voice model go.</summary>
        public void Dispose() => _session.Dispose();
    }

    /// <summary>
    /// Says who is speaking: a voice print for each line, then the lines whose voices are alike are put together, until
    /// what's left is clearly different (or until there are as many speakers as asked). Speakers are numbered in the order
    /// they first speak. Lines too short to tell take the speaker of the nearest line.
    /// </summary>
    public static class Speakers
    {
        /// <summary>How alike two groups must be, on average, to be one speaker when the number isn't given.</summary>
        public const double SameVoice = 0.25;
        /// <summary>Shorter lines can't be told apart reliably; they take a neighbour's speaker.</summary>
        public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(0.6);
        /// <summary>The longest part of a line used for its voice print (the middle).</summary>
        public static readonly TimeSpan Longest = TimeSpan.FromSeconds(10);

        /// <summary>Gives each line of <paramref name="t"/> a speaker. <paramref name="count"/> 0 finds out how many there are.</summary>
        public static void Assign(Transcript t, float[] samples, IVoicePrints model, int count, Action<int> progress = null, CancellationToken cancel = default)
        {
            var prints = new float[t.Segments.Count][];
            for (var i = 0; i < t.Segments.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var s = t.Segments[i];
                if (s.End - s.Start >= Shortest)
                {
                    var length = Math.Min(s.EndMs - s.StartMs, (long)Longest.TotalMilliseconds);
                    var middle = (s.StartMs + s.EndMs) / 2;
                    var from = (int)Math.Max(0, (middle - length / 2) * 16);
                    var n = (int)Math.Min(samples.Length - from, length * 16);
                    if (n > 0)
                    {
                        var bands = Fbank.Compute(samples, from, n, out var frames);
                        if (frames >= 20) prints[i] = Normalise(model.Print(bands, frames));
                    }
                }
                progress?.Invoke((i + 1) * 100 / Math.Max(1, t.Segments.Count));
            }
            var known = Enumerable.Range(0, prints.Length).Where(i => prints[i] != null).ToList();
            if (known.Count == 0)
            {
                foreach (var s in t.Segments) s.Speaker = 0;
                t.SpeakerNames = new List<string> { DefaultName(0) };
                return;
            }
            var labels = Group(known.Select(i => prints[i]).ToList(), count);
            var speakerOf = new int[prints.Length];
            for (var k = 0; k < known.Count; k++) speakerOf[known[k]] = labels[k];
            // Short lines: the speaker of the nearest line in time that has one.
            for (var i = 0; i < prints.Length; i++)
                if (prints[i] == null)
                {
                    var nearest = known.OrderBy(k => Math.Abs(t.Segments[k].StartMs - t.Segments[i].StartMs)).First();
                    speakerOf[i] = speakerOf[nearest];
                }
            // Numbered in the order they first speak.
            var order = new List<int>();
            foreach (var l in speakerOf) if (!order.Contains(l)) order.Add(l);
            for (var i = 0; i < prints.Length; i++) t.Segments[i].Speaker = order.IndexOf(speakerOf[i]);
            t.SpeakerNames = Enumerable.Range(0, order.Count).Select(DefaultName).ToList();
        }

        /// <summary>A speaker's name before it's given one: "Speaker 1", "Speaker 2"...</summary>
        public static string DefaultName(int index) => Lang.T("Speaker {0}", index + 1);

        /// <summary>
        /// The numbers scaled so they add up (squared) to 1, so prints can be compared by direction only.
        /// </summary>
        public static float[] Normalise(float[] v)
        {
            double sum = 0;
            foreach (var x in v) sum += x * x;
            var length = Math.Sqrt(sum);
            return length <= 0 ? v : v.Select(x => (float)(x / length)).ToArray();
        }

        /// <summary>How alike two voice prints are: 1 for the same, around 0 for unrelated.</summary>
        public static double Alike(float[] a, float[] b)
        {
            double sum = 0;
            for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
            return sum;
        }

        /// <summary>
        /// Groups voice prints: the two most alike groups (by their average likeness) are joined, again and again, until
        /// the most alike are less alike than <see cref="SameVoice"/>, or there are <paramref name="count"/> groups.
        /// Returns a group number for each print.
        /// </summary>
        public static int[] Group(IList<float[]> prints, int count)
        {
            var n = prints.Count;
            var sim = new double[n, n];
            for (var i = 0; i < n; i++)
                for (var j = i; j < n; j++)
                    sim[i, j] = sim[j, i] = Alike(prints[i], prints[j]);
            // Sum of likeness between each pair of groups, so the average is cheap to keep up to date.
            var between = new double[n, n];
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                    between[i, j] = sim[i, j];
            var alive = Enumerable.Range(0, n).ToList();
            var members = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToList();
            while (alive.Count > 1 && (count <= 0 || alive.Count > count))
            {
                double best = double.MinValue;
                int bi = -1, bj = -1;
                for (var x = 0; x < alive.Count; x++)
                    for (var y = x + 1; y < alive.Count; y++)
                    {
                        int a = alive[x], b = alive[y];
                        var average = between[a, b] / (members[a].Count * members[b].Count);
                        if (average > best) { best = average; bi = a; bj = b; }
                    }
                if (count <= 0 && best < SameVoice) break;
                foreach (var c in alive)
                {
                    if (c == bi || c == bj) continue;
                    between[bi, c] += between[bj, c];
                    between[c, bi] = between[bi, c];
                }
                members[bi].AddRange(members[bj]);
                alive.Remove(bj);
            }
            var labels = new int[n];
            for (var g = 0; g < alive.Count; g++)
                foreach (var m in members[alive[g]]) labels[m] = g;
            return labels;
        }
    }

    /// <summary>Reads a 16-bit PCM WAV file (what KUDUTEXT makes in its work folder) as samples from -1 to 1.</summary>
    public static class Wav
    {
        /// <summary>
        /// Reads a 16-bit WAV file's sound, as one channel of numbers from -1 to 1, and its samples a second.
        /// </summary>
        public static float[] Read(string path, out int rate)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a WAV file.");
                r.ReadInt32();
                if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAV file.");
                int channels = 1, bits = 16;
                rate = 16000;
                while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
                {
                    var id = new string(r.ReadChars(4));
                    var size = r.ReadInt32();
                    if (id == "fmt ")
                    {
                        r.ReadInt16();
                        channels = r.ReadInt16();
                        rate = r.ReadInt32();
                        r.ReadInt32();
                        r.ReadInt16();
                        bits = r.ReadInt16();
                        r.BaseStream.Position += size - 16;
                    }
                    else if (id == "data")
                    {
                        if (bits != 16) throw new InvalidDataException("Only 16-bit WAV files are read.");
                        var n = Math.Min(size, (int)(r.BaseStream.Length - r.BaseStream.Position)) / 2 / channels;
                        var samples = new float[n];
                        for (var i = 0; i < n; i++)
                        {
                            double sum = 0;
                            for (var c = 0; c < channels; c++) sum += r.ReadInt16();
                            samples[i] = (float)(sum / channels / 32768.0);
                        }
                        return samples;
                    }
                    else r.BaseStream.Position += size + (size & 1);
                }
                throw new InvalidDataException("The WAV file has no sound in it.");
            }
        }
    }
}
