using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Kudutext.Core
{
    public enum JobState
    {
        Waiting,
        Working,
        Done,
        Failed,
        Cancelled
    }

    /// <summary>One video or recording in the list, and what's become of it.</summary>
    [DataContract]
    public sealed class Job
    {
        [DataMember(Name = "id", Order = 1)] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember(Name = "file", Order = 2)] public string File { get; set; }
        [DataMember(Name = "state", Order = 3)] public string StateText { get; set; } = "waiting";
        [DataMember(Name = "added", Order = 4)] public DateTime Added { get; set; } = DateTime.UtcNow;
        [DataMember(Name = "language", Order = 5, EmitDefaultValue = false)] public string Language { get; set; }
        [DataMember(Name = "error", Order = 6, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "outputs", Order = 7, EmitDefaultValue = false)] public List<string> Outputs { get; set; }
        [DataMember(Name = "length", Order = 8, EmitDefaultValue = false)] public double LengthSeconds { get; set; }
        /// <summary>The model, language choice and translation it was (or will be) done with.</summary>
        [DataMember(Name = "model", Order = 9, EmitDefaultValue = false)] public string Model { get; set; }
        [DataMember(Name = "translate", Order = 10, EmitDefaultValue = false)] public bool Translate { get; set; }
        /// <summary>The language that was heard (a code such as "en"), once done.</summary>
        [DataMember(Name = "heard", Order = 11, EmitDefaultValue = false)] public string Heard { get; set; }
        /// <summary>How many lines of speech were found, once done.</summary>
        [DataMember(Name = "lines", Order = 12, EmitDefaultValue = false)] public int Lines { get; set; }

        /// <summary>How far it's got, 0 to 100 (while working; not saved).</summary>
        [IgnoreDataMember] public int Percent { get; set; }
        /// <summary>What it's doing now, in plain words (while working; not saved).</summary>
        [IgnoreDataMember] public string Step { get; set; }

        [IgnoreDataMember]
        public JobState State
        {
            get => Enum.TryParse(StateText, true, out JobState s) ? s : JobState.Failed;
            set => StateText = value.ToString().ToLowerInvariant();
        }

        public string FileName => Path.GetFileName(File ?? string.Empty);
    }

    /// <summary>The list of files, kept in %LocalAppData%\KUDUTEXT\jobs.json with each one's text beside it.</summary>
    public static class JobList
    {
        [DataContract]
        private sealed class Stored
        {
            [DataMember(Name = "jobs", Order = 1)] public List<Job> Jobs { get; set; }
        }

        public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KUDUTEXT");
        public static string ListFile => Path.Combine(Folder, "jobs.json");

        /// <summary>Where a job's text is kept (what was said, line by line), so it can be checked and saved again later.</summary>
        public static string TranscriptFile(Job job) => Path.Combine(Folder, "Texts", job.Id + ".json");

        /// <summary>The kinds of files that can be turned into text.</summary>
        public static readonly IReadOnlyList<string> Kinds = new[]
        {
            ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".flv", ".ts",
            ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".amr"
        };

        public static bool CanTake(string file) => Kinds.Contains(Path.GetExtension(file ?? string.Empty).ToLowerInvariant());

        /// <summary>Kinds the speech engine can read by itself when Windows can't read them.</summary>
        public static readonly IReadOnlyList<string> EngineReads = new[] { ".wav", ".mp3", ".flac" };

        /// <summary>
        /// Reads the list. A file that was being worked on when the app closed goes back to waiting, to be done again.
        /// </summary>
        public static List<Job> Load()
        {
            try
            {
                if (!System.IO.File.Exists(ListFile)) return new List<Job>();
                List<Job> list;
                using (var s = System.IO.File.OpenRead(ListFile))
                    list = ((Stored)new DataContractJsonSerializer(typeof(Stored)).ReadObject(s))?.Jobs ?? new List<Job>();
                list = list.Where(j => j != null && !string.IsNullOrEmpty(j.File)).ToList();
                foreach (var j in list.Where(j => j.State == JobState.Working)) j.State = JobState.Waiting;
                return list;
            }
            catch (Exception ex) when (ex is IOException || ex is SerializationException || ex is UnauthorizedAccessException || ex is InvalidCastException)
            {
                return new List<Job>();
            }
        }

        public static void Save(IEnumerable<Job> jobs)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var temp = ListFile + ".new";
                using (var s = System.IO.File.Create(temp))
                    new DataContractJsonSerializer(typeof(Stored)).WriteObject(s, new Stored { Jobs = jobs.ToList() });
                if (System.IO.File.Exists(ListFile)) System.IO.File.Replace(temp, ListFile, null);
                else System.IO.File.Move(temp, ListFile);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Best-effort: the list just won't come back this time.
            }
        }
    }
}
