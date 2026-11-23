// What was said in a file: lines with their times (and speakers), kept as JSON, and written as SRT and VTT subtitles or
// plain text.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Kudutext.Core
{
    /// <summary>One line of speech: when it starts and ends, and what was said.</summary>
    [DataContract]
    public sealed class Segment
    {
        [DataMember(Name = "start", Order = 1)] public long StartMs { get; set; }
        [DataMember(Name = "end", Order = 2)] public long EndMs { get; set; }
        [DataMember(Name = "text", Order = 3)] public string Text { get; set; }
        /// <summary>Who says it: a number into the transcript's speaker names, or -1 when speakers weren't looked for.</summary>
        [DataMember(Name = "speaker", Order = 4, EmitDefaultValue = false)] public int SpeakerPlusOne { get; set; }

        [IgnoreDataMember]
        public int Speaker
        {
            get => SpeakerPlusOne - 1;
            set => SpeakerPlusOne = value + 1;
        }

        /// <summary>When the line starts.</summary>
        public TimeSpan Start => TimeSpan.FromMilliseconds(StartMs);
        /// <summary>When the line ends.</summary>
        public TimeSpan End => TimeSpan.FromMilliseconds(EndMs);
    }

    /// <summary>What was said in a file, line by line, and the language it was in.</summary>
    [DataContract]
    public sealed class Transcript
    {
        [DataMember(Name = "language", Order = 1)] public string Language { get; set; }
        [DataMember(Name = "segments", Order = 2)] public List<Segment> Segments { get; set; } = new List<Segment>();
        /// <summary>The speakers' names ("Speaker 1", or names given in Check text), when speakers were looked for.</summary>
        [DataMember(Name = "speakers", Order = 3, EmitDefaultValue = false)] public List<string> SpeakerNames { get; set; }

        /// <summary>True when speakers were looked for and the lines say who speaks.</summary>
        public bool HasSpeakers => SpeakerNames != null && SpeakerNames.Count > 0 && Segments.Any(s => s.Speaker >= 0);

        /// <summary>The name of the speaker of a line, or null.</summary>
        public string SpeakerOf(Segment s) => HasSpeakers && s.Speaker >= 0 && s.Speaker < SpeakerNames.Count ? SpeakerNames[s.Speaker] : null;

        /// <summary>The words; with speakers, a new paragraph with the name each time someone else speaks.</summary>
        public string PlainText
        {
            get
            {
                if (!HasSpeakers) return string.Join(Environment.NewLine, Segments.Select(s => s.Text));
                var sb = new StringBuilder();
                string last = null;
                foreach (var s in Segments)
                {
                    var who = SpeakerOf(s);
                    if (who != last)
                    {
                        if (sb.Length > 0) sb.Append(Environment.NewLine).Append(Environment.NewLine);
                        sb.Append(who ?? "?").Append(": ");
                        last = who;
                    }
                    else sb.Append(' ');
                    sb.Append(s.Text);
                }
                return sb.ToString();
            }
        }

        /// <summary>Reads a kept text.</summary>
        public static Transcript Load(string file)
        {
            using (var s = File.OpenRead(file))
                return (Transcript)new DataContractJsonSerializer(typeof(Transcript)).ReadObject(s);
        }

        /// <summary>Keeps the text (written to a new file first, so a crash never leaves half a file).</summary>
        public void Save(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var temp = file + ".new";
            using (var s = File.Create(temp))
                new DataContractJsonSerializer(typeof(Transcript)).WriteObject(s, this);
            if (File.Exists(file)) File.Delete(file);
            File.Move(temp, file);
        }
    }

    /// <summary>Reads the JSON file whisper.cpp writes (whisper-cli -oj).</summary>
    public static class WhisperJson
    {
        [DataContract]
        private sealed class Output
        {
            [DataMember(Name = "result")] public Result Result { get; set; }
            [DataMember(Name = "transcription")] public List<Line> Transcription { get; set; }
        }

        [DataContract]
        private sealed class Result
        {
            [DataMember(Name = "language")] public string Language { get; set; }
        }

        [DataContract]
        private sealed class Line
        {
            [DataMember(Name = "offsets")] public Offsets Offsets { get; set; }
            [DataMember(Name = "text")] public string Text { get; set; }
        }

        [DataContract]
        private sealed class Offsets
        {
            [DataMember(Name = "from")] public long From { get; set; }
            [DataMember(Name = "to")] public long To { get; set; }
        }

        /// <summary>Reads the speech engine's JSON answer into lines with times.</summary>
        public static Transcript Parse(string json)
        {
            Output o;
            using (var s = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
                o = (Output)new DataContractJsonSerializer(typeof(Output)).ReadObject(s);
            var t = new Transcript { Language = o?.Result?.Language };
            foreach (var line in o?.Transcription ?? new List<Line>())
            {
                var text = Clean(line.Text);
                if (text.Length == 0 || line.Offsets == null) continue;
                t.Segments.Add(new Segment { StartMs = line.Offsets.From, EndMs = Math.Max(line.Offsets.To, line.Offsets.From), Text = text });
            }
            return t;
        }

        /// <summary>Takes away the spaces whisper puts around lines, and its notes for silence (such as "[BLANK_AUDIO]").</summary>
        public static string Clean(string text)
        {
            var s = (text ?? string.Empty).Trim();
            if (s.StartsWith("[") && s.EndsWith("]") && s.IndexOf(']') == s.Length - 1) return string.Empty;
            return s;
        }
    }

    /// <summary>Subtitle files (SubRip .srt, WebVTT .vtt) and plain transcripts (.txt).</summary>
    public static class SubtitleFiles
    {
        /// <summary>The kinds of file that can be written.</summary>
        public static readonly IReadOnlyList<string> Known = new[] { "srt", "vtt", "txt" };

        /// <summary>A kind of file in words, for the options.</summary>
        public static string Describe(string format)
        {
            switch (format)
            {
                case "srt": return "Subtitles (.srt), for most players";
                case "vtt": return "Subtitles (.vtt), for web pages";
                default: return "Text (.txt), the words only";
            }
        }

        /// <summary>The text as a file of one kind.</summary>
        public static string Write(Transcript t, string format)
        {
            switch (format)
            {
                case "srt": return Srt(t);
                case "vtt": return Vtt(t);
                default: return t.PlainText + Environment.NewLine;
            }
        }

        /// <summary>The text as SRT subtitles (most video players).</summary>
        public static string Srt(Transcript t)
        {
            var sb = new StringBuilder();
            var n = 1;
            foreach (var s in t.Segments)
            {
                sb.Append(n++).Append("\r\n");
                sb.Append(Time(s.Start, ',')).Append(" --> ").Append(Time(s.End, ',')).Append("\r\n");
                var who = t.SpeakerOf(s);
                sb.Append(who == null ? string.Empty : who + ": ").Append(s.Text.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n\r\n");
            }
            return sb.ToString();
        }

        /// <summary>The text as WebVTT subtitles (web pages).</summary>
        public static string Vtt(Transcript t)
        {
            var sb = new StringBuilder("WEBVTT\n\n");
            foreach (var s in t.Segments)
            {
                sb.Append(Time(s.Start, '.')).Append(" --> ").Append(Time(s.End, '.')).Append('\n');
                // "-->" can't appear in a cue's text; a speaker goes in WebVTT's own voice tag.
                var who = t.SpeakerOf(s);
                if (who != null) sb.Append("<v ").Append(who.Replace(">", string.Empty).Replace("<", string.Empty)).Append('>');
                sb.Append(s.Text.Replace("-->", "->")).Append("\n\n");
            }
            return sb.ToString();
        }

        /// <summary>"01:02:03,456" (or with a dot for WebVTT).</summary>
        public static string Time(TimeSpan t, char separator)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return ((int)t.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + t.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                   t.Seconds.ToString("00", CultureInfo.InvariantCulture) + separator + t.Milliseconds.ToString("000", CultureInfo.InvariantCulture);
        }

        /// <summary>Where a format's file goes: next to the original ("talk.mp4" → "talk.srt"), or in the chosen folder.</summary>
        public static string PathFor(string source, string format, string folder)
        {
            var dir = string.IsNullOrEmpty(folder) ? Path.GetDirectoryName(source) : folder;
            return Path.Combine(dir ?? string.Empty, Path.GetFileNameWithoutExtension(source) + "." + format);
        }
    }
}
