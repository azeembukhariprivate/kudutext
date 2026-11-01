using System;
using System.IO;
using System.Runtime.InteropServices;
using Kudutext.Core;
using NAudio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Kudutext.Views
{
    /// <summary>Plays one line's piece of the original video or recording, to hear what was said.</summary>
    public sealed class LinePlayer : IDisposable
    {
        private WaveOutEvent _out;
        private WaveStream _reader;

        /// <summary>The piece has finished playing (or was stopped).</summary>
        public event Action Finished;

        public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;

        /// <summary>Plays from <paramref name="startMs"/> to <paramref name="endMs"/>. Throws AudioException with a plain reason.</summary>
        public void Play(string file, long startMs, long endMs)
        {
            Stop();
            if (!File.Exists(file)) throw new AudioException("The video or recording isn't there any more, so the line can't be played.");
            try
            {
                _reader = string.Equals(Path.GetExtension(file), ".wav", StringComparison.OrdinalIgnoreCase)
                    ? (WaveStream)new WaveFileReader(file)
                    : new MediaFoundationReader(file);
                _reader.CurrentTime = TimeSpan.FromMilliseconds(Math.Max(0, startMs));
                var piece = new OffsetSampleProvider(_reader.ToSampleProvider()) { Take = TimeSpan.FromMilliseconds(Math.Max(300, endMs - startMs)) };
                var output = new WaveOutEvent();
                _out = output;
                // Only this piece ending counts, not an earlier one stopped to play this.
                output.PlaybackStopped += (s, e) =>
                {
                    if (ReferenceEquals(_out, output)) Finished?.Invoke();
                };
                _out.Init(piece);
                _out.Play();
            }
            catch (Exception ex) when (ex is MmException || ex is COMException || ex is InvalidOperationException || ex is FormatException
                                       || ex is ArgumentException || ex is InvalidDataException || ex is IOException)
            {
                Stop();
                throw new AudioException(ex is MmException
                    ? "The line can't be played: no speakers or headphones were found."
                    : "The line can't be played: Windows can't play this kind of file.", ex);
            }
        }

        public void Stop()
        {
            try { _out?.Stop(); }
            catch (MmException) { }
            _out?.Dispose();
            _out = null;
            _reader?.Dispose();
            _reader = null;
        }

        public void Dispose() => Stop();
    }
}
