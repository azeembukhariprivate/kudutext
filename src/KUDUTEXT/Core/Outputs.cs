using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Kudutext.Core
{
    /// <summary>Writes the subtitle and text files for a finished file, never over a file KUDUTEXT didn't make.</summary>
    public static class Outputs
    {
        /// <summary>
        /// Writes each format next to the original (or in <paramref name="folder"/>) and returns the files written.
        /// A file already there is replaced only when this job wrote it before; otherwise a new name is used
        /// ("talk (2).srt").
        /// </summary>
        public static List<string> Write(Job job, Transcript t, IEnumerable<string> formats, string folder)
        {
            var before = new HashSet<string>(job.Outputs ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var written = new List<string>();
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            foreach (var format in formats.Where(SubtitleFiles.Known.Contains).Distinct())
            {
                var path = FreeName(SubtitleFiles.PathFor(job.File, format, folder), before, job.File);
                File.WriteAllText(path, SubtitleFiles.Write(t, format), format == "vtt" ? new UTF8Encoding(false) : new UTF8Encoding(true));
                written.Add(path);
            }
            return written;
        }

        /// <summary>
        /// Writes the corrected text again into the files this job made before (each in its own kind, by its extension).
        /// When there are none (they were never made), they're made as for a new file.
        /// </summary>
        public static List<string> Rewrite(Job job, Transcript t, IEnumerable<string> formats, string folder)
        {
            var ours = (job.Outputs ?? new List<string>())
                .Where(p => SubtitleFiles.Known.Contains(Path.GetExtension(p).TrimStart('.').ToLowerInvariant())).ToList();
            if (ours.Count == 0) return Write(job, t, formats, folder);
            foreach (var path in ours)
            {
                var format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, SubtitleFiles.Write(t, format), format == "vtt" ? new UTF8Encoding(false) : new UTF8Encoding(true));
            }
            return ours;
        }

        /// <summary>The path itself, or "name (2).ext", "name (3).ext"... when something else is already there.</summary>
        public static string FreeName(string path, ICollection<string> ours, string source)
        {
            bool Taken(string p) => string.Equals(p, source, StringComparison.OrdinalIgnoreCase) || (File.Exists(p) && !ours.Contains(p));
            if (!Taken(path)) return path;
            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (var i = 2; ; i++)
            {
                var p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!Taken(p)) return p;
            }
        }
    }
}
