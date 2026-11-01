using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kudutext.Core
{
    /// <summary>Why a file's sound couldn't be read, in plain words.</summary>
    public sealed class AudioException : Exception
    {
        public AudioException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// Turns one file in the list into text: reads its sound into a WAV file in the work folder, lets the speech engine
    /// listen to it, keeps what was said (to be checked later) and writes the chosen subtitle and text files.
    /// </summary>
    public sealed class Runner
    {
        /// <summary>Reads a video's or recording's sound into a 16 kHz mono WAV file; returns its length in seconds.</summary>
        public delegate double DecodeFn(string source, string wav, Action<int> progress, CancellationToken cancel);

        public delegate Task<Transcript> TranscribeFn(string model, string audio, string language, bool translate, Action<int> progress, CancellationToken cancel);

        public DecodeFn Decode { get; set; }
        public TranscribeFn Transcribe { get; set; }

        public static string WorkFolder => Path.Combine(JobList.Folder, "Work");

        /// <summary>The share of the bar given to reading the sound; the rest is listening.</summary>
        public const int ReadingShare = 15;

        public async Task Do(Job job, IList<string> formats, string folder, Action changed, CancellationToken cancel)
        {
            job.State = JobState.Working;
            job.Error = null;
            job.Percent = 0;
            job.Step = "Reading the sound";
            changed?.Invoke();
            Directory.CreateDirectory(WorkFolder);
            var wav = Path.Combine(WorkFolder, job.Id + ".wav");
            try
            {
                if (!File.Exists(job.File)) throw new AudioException("The file isn't there any more.");
                var audio = wav;
                try
                {
                    job.LengthSeconds = await Task.Run(() => Decode(job.File, wav, p =>
                    {
                        job.Percent = p * ReadingShare / 100;
                        changed?.Invoke();
                    }, cancel), cancel).ConfigureAwait(false);
                }
                catch (AudioException) when (JobList.EngineReads.Contains(Path.GetExtension(job.File).ToLowerInvariant()))
                {
                    audio = job.File; // the speech engine reads these kinds itself
                }
                cancel.ThrowIfCancellationRequested();

                job.Step = "Listening";
                job.Percent = ReadingShare;
                changed?.Invoke();
                var model = SpeechModels.Find(job.Model);
                var t = await Transcribe(SpeechModels.PathOf(model), audio, job.Language, job.Translate, p =>
                {
                    job.Percent = ReadingShare + p * (100 - ReadingShare) / 100;
                    changed?.Invoke();
                }, cancel).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();

                job.Step = "Saving";
                changed?.Invoke();
                if (job.Translate) t.Language = "en";
                t.Save(JobList.TranscriptFile(job));
                job.Outputs = Outputs.Write(job, t, formats, folder);
                job.Heard = t.Language;
                job.Lines = t.Segments.Count;
                job.Percent = 100;
                job.State = JobState.Done;
            }
            catch (OperationCanceledException)
            {
                job.State = JobState.Cancelled;
            }
            catch (Exception ex) when (ex is AudioException || ex is WhisperException || ex is IOException || ex is UnauthorizedAccessException)
            {
                job.State = JobState.Failed;
                job.Error = ex is UnauthorizedAccessException ? "The text files couldn't be saved there (no permission). Choose another folder in the options." : ex.Message;
            }
            finally
            {
                job.Step = null;
                try { if (File.Exists(wav)) File.Delete(wav); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                changed?.Invoke();
            }
        }

        /// <summary>Clears work files left behind if the app was closed while working.</summary>
        public static void CleanWork()
        {
            try
            {
                if (!Directory.Exists(WorkFolder)) return;
                foreach (var f in Directory.GetFiles(WorkFolder)) File.Delete(f);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }
    }
}
