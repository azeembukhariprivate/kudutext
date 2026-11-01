using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Kudutext.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Kudutext.Views
{
    /// <summary>
    /// Reads the sound of a video or recording into the 16 kHz mono WAV file the speech engine listens to. WAV files are
    /// read directly; everything else through Windows' own Media Foundation (MP4, MOV, WMV, MP3, M4A and more).
    /// </summary>
    public static class AudioReader
    {
        public static readonly WaveFormat Target = new WaveFormat(16000, 16, 1);

        public static double Convert(string source, string wav, Action<int> progress, CancellationToken cancel)
        {
            try
            {
                if (string.Equals(Path.GetExtension(source), ".wav", StringComparison.OrdinalIgnoreCase))
                {
                    using (var reader = new WaveFileReader(source))
                        return Write(reader, reader.TotalTime, () => reader.CurrentTime, wav, progress, cancel);
                }
                using (var reader = new MediaFoundationReader(source, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true }))
                    return Write(reader, reader.TotalTime, () => reader.CurrentTime, wav, progress, cancel);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is COMException || ex is FormatException || ex is InvalidOperationException || ex is ArgumentException || ex is InvalidDataException
                                       || ex is EndOfStreamException || ex is NotSupportedException || ex is DllNotFoundException || ex is TypeInitializationException)
            {
                TryDelete(wav);
                throw new AudioException("No sound could be read from this file. It may have no sound, or be a kind Windows can't play.", ex);
            }
        }

        private static double Write(WaveStream stream, TimeSpan total, Func<TimeSpan> position, string wav, Action<int> progress, CancellationToken cancel)
        {
            ISampleProvider samples = stream.ToSampleProvider();
            if (samples.WaveFormat.Channels > 1) samples = new MonoMix(samples);
            if (samples.WaveFormat.SampleRate != Target.SampleRate) samples = new WdlResamplingSampleProvider(samples, Target.SampleRate);
            var pcm = samples.ToWaveProvider16();
            var buffer = new byte[Target.AverageBytesPerSecond];
            long written = 0;
            var last = -1;
            using (var writer = new WaveFileWriter(wav, Target))
            {
                int n;
                while ((n = pcm.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    writer.Write(buffer, 0, n);
                    written += n;
                    if (total.TotalSeconds > 0)
                    {
                        var p = (int)Math.Min(100, position().TotalSeconds * 100 / total.TotalSeconds);
                        if (p != last) progress?.Invoke(last = p);
                    }
                }
            }
            if (written == 0)
            {
                TryDelete(wav);
                throw new InvalidDataException("no sound");
            }
            return (double)written / Target.AverageBytesPerSecond;
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { }
        }

        /// <summary>Mixes any number of channels down to one.</summary>
        private sealed class MonoMix : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly int _channels;
            private float[] _buffer = new float[0];

            public MonoMix(ISampleProvider source)
            {
                _source = source;
                _channels = source.WaveFormat.Channels;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                var need = count * _channels;
                if (_buffer.Length < need) _buffer = new float[need];
                var read = _source.Read(_buffer, 0, need);
                var frames = read / _channels;
                for (var i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (var c = 0; c < _channels; c++) sum += _buffer[i * _channels + c];
                    buffer[offset + i] = sum / _channels;
                }
                return frames;
            }
        }

        // ================================================================ short paths

        /// <summary>The short (8.3) form of a path with letters outside plain ASCII, when Windows keeps one; else the path.</summary>
        public static string ShortPath(string path)
        {
            if (string.IsNullOrEmpty(path) || IsAscii(path)) return path;
            var sb = new StringBuilder(1024);
            var n = GetShortPathName(path, sb, sb.Capacity);
            return n > 0 && n < sb.Capacity && IsAscii(sb.ToString()) ? sb.ToString() : path;
        }

        private static bool IsAscii(string s)
        {
            foreach (var c in s)
                if (c > 126)
                    return false;
            return true;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferSize);
    }
}
