using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Kudutext.Core
{
    /// <summary>The FFmpeg download: a free video tool, fetched once, the first time it's needed.</summary>
    public sealed class FfmpegPackage
    {
        public string Version { get; set; }
        public string Url { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
    }

    /// <summary>How the subtitles go into a video.</summary>
    public enum VideoMode
    {
        /// <summary>A subtitle track that players can turn on and off; the picture isn't changed (quick).</summary>
        Track,

        /// <summary>Written into the picture, so they always show, on any player or website (takes longer).</summary>
        Burn
    }

    /// <summary>
    /// FFmpeg, used to put subtitles into a video and to read sound Windows can't. It's downloaded once into
    /// %LocalAppData%\KUDUTEXT\FFmpeg from Gyan's Windows builds (the ones ffmpeg.org links to), and kept only when the
    /// download's SHA-256 checksum matches the one written here.
    /// </summary>
    public static class Ffmpeg
    {
        public static readonly FfmpegPackage Package = new FfmpegPackage
        {
            Version = "9.0.2",
            Url = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip",
            Size = 114768076,
            Sha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba"
        };

        public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KUDUTEXT", "FFmpeg");
        public static string ExePath => Path.Combine(Folder, "ffmpeg.exe");

        /// <summary>FFmpeg is here and came from a download whose checksum matched (a small ".ok" note says which).</summary>
        public static bool IsReady(FfmpegPackage p = null)
        {
            p = p ?? Package;
            return File.Exists(ExePath) && File.Exists(ExePath + ".ok") && File.ReadAllText(ExePath + ".ok").Trim() == p.Sha256;
        }

        /// <summary>Downloads and checks the package, then takes ffmpeg.exe and its licence out of it.</summary>
        public static async Task Download(HttpClient http, Action<long, long> progress, CancellationToken cancel, string url = null, FfmpegPackage p = null)
        {
            p = p ?? Package;
            Directory.CreateDirectory(Folder);
            var part = Path.Combine(Folder, "ffmpeg.zip.part");
            await Downloads.Fetch(http, url ?? p.Url, part, p.Size, f => string.Equals(Downloads.Sha256Of(f), p.Sha256, StringComparison.OrdinalIgnoreCase),
                "FFmpeg", progress, cancel).ConfigureAwait(false);
            try
            {
                using (var zip = ZipFile.OpenRead(part))
                {
                    var exe = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                    if (exe == null) throw new DownloadFailedException("The FFmpeg download didn't have ffmpeg.exe in it.");
                    var temp = ExePath + ".new";
                    exe.ExtractToFile(temp, true);
                    if (File.Exists(ExePath)) File.Delete(ExePath);
                    File.Move(temp, ExePath);
                    var licence = zip.Entries.FirstOrDefault(e => e.Name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase));
                    licence?.ExtractToFile(Path.Combine(Folder, "LICENSE.txt"), true);
                }
            }
            catch (InvalidDataException)
            {
                throw new DownloadFailedException("The FFmpeg download couldn't be opened. Please try again.");
            }
            finally
            {
                try { File.Delete(part); }
                catch (IOException) { }
            }
            File.WriteAllText(ExePath + ".ok", p.Sha256);
        }

        /// <summary>Takes FFmpeg off this PC (it's downloaded again when it's needed).</summary>
        public static void Remove()
        {
            foreach (var f in new[] { ExePath + ".ok", ExePath, Path.Combine(Folder, "LICENSE.txt") })
                if (File.Exists(f)) File.Delete(f);
        }

        // ================================================================ what to ask FFmpeg

        private static readonly string[] Start = { "-hide_banner", "-nostdin", "-y" };

        /// <summary>Reads a file's sound into the 16 kHz mono WAV the speech engine listens to.</summary>
        public static List<string> SoundArgs(string input, string wav) =>
            Start.Concat(new[] { "-i", input, "-vn", "-sn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", wav }).ToList();

        /// <summary>Kinds whose own container takes a subtitle track as it is (MP4 and its kin take "mov_text", MKV takes SRT).</summary>
        private static readonly string[] Mp4Like = { ".mp4", ".m4v", ".mov" };

        /// <summary>Kinds of file that have a picture (subtitles can only go into these).</summary>
        public static readonly IReadOnlyList<string> VideoKinds = new[] { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".flv", ".ts" };

        public static bool IsVideo(string file) => VideoKinds.Contains(Path.GetExtension(file ?? string.Empty).ToLowerInvariant());

        /// <summary>The new video's name: "talk (subtitles).mp4" for a track, "talk (subtitles in picture).mp4" when written in.</summary>
        public static string OutputFor(string video, VideoMode mode, string folder)
        {
            var ext = Path.GetExtension(video).ToLowerInvariant();
            string newExt;
            if (mode == VideoMode.Burn) newExt = ".mp4";
            else newExt = Mp4Like.Contains(ext) || ext == ".mkv" ? ext : ".mkv";
            var dir = string.IsNullOrEmpty(folder) ? Path.GetDirectoryName(video) : folder;
            var name = Path.GetFileNameWithoutExtension(video) + (mode == VideoMode.Burn ? " (subtitles in picture)" : " (subtitles)") + newExt;
            return Path.Combine(dir ?? string.Empty, name);
        }

        /// <summary>
        /// Adds the subtitles as a track: the picture and sound are copied as they are (no quality lost, quick).
        /// <paramref name="language"/> is a two-letter code, or null.
        /// </summary>
        public static List<string> TrackArgs(string video, string srt, string output, string language)
        {
            var mp4 = Mp4Like.Contains(Path.GetExtension(output).ToLowerInvariant());
            var args = Start.Concat(new[]
            {
                "-i", video, "-i", srt,
                "-map", "0:v", "-map", "0:a?", "-map", "1:0",
                "-c:v", "copy", "-c:a", "copy", "-c:s", mp4 ? "mov_text" : "srt",
                "-disposition:s:0", "default"
            }).ToList();
            var three = LanguageCode3(language);
            if (three != null) args.AddRange(new[] { "-metadata:s:s:0", "language=" + three });
            if (mp4) args.AddRange(new[] { "-movflags", "+faststart" });
            args.Add(output);
            return args;
        }

        /// <summary>The sizes offered for subtitles written into the picture, as the subtitle drawer's font size.</summary>
        public static int FontSize(string size) => size == "small" ? 16 : size == "large" ? 28 : 21;

        /// <summary>
        /// Writes the subtitles into the picture. The subtitle file is given by its plain name (<paramref name="srtName"/>,
        /// in FFmpeg's working folder) because the filter can't take a Windows path with a drive letter as it is.
        /// </summary>
        public static List<string> BurnArgs(string video, string srtName, string output, string size)
        {
            if (srtName.IndexOfAny(new[] { ':', '\\', '/', '\'', ',', '[', ']', ';' }) >= 0) throw new ArgumentException("a plain file name is needed", nameof(srtName));
            var style = "FontSize=" + FontSize(size) + ",Outline=2,Shadow=1,MarginV=24";
            return Start.Concat(new[]
            {
                "-i", video,
                "-vf", $"subtitles={srtName}:force_style='{style}'",
                "-map", "0:v:0", "-map", "0:a?",
                "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k",
                "-movflags", "+faststart",
                output
            }).ToList();
        }

        private static readonly Dictionary<string, string> Iso3 = new Dictionary<string, string>
        {
            ["en"] = "eng", ["ml"] = "mal", ["hi"] = "hin", ["ta"] = "tam", ["te"] = "tel", ["kn"] = "kan", ["bn"] = "ben", ["mr"] = "mar", ["gu"] = "guj",
            ["pa"] = "pan", ["ur"] = "urd", ["ar"] = "ara", ["fr"] = "fre", ["de"] = "ger", ["es"] = "spa", ["pt"] = "por", ["it"] = "ita", ["nl"] = "dut",
            ["ru"] = "rus", ["uk"] = "ukr", ["pl"] = "pol", ["tr"] = "tur", ["fa"] = "per", ["zh"] = "chi", ["ja"] = "jpn", ["ko"] = "kor", ["id"] = "ind",
            ["ms"] = "may", ["th"] = "tha", ["vi"] = "vie", ["sv"] = "swe", ["no"] = "nor", ["da"] = "dan", ["fi"] = "fin", ["el"] = "gre", ["he"] = "heb",
            ["cs"] = "cze", ["ro"] = "rum", ["hu"] = "hun", ["sw"] = "swa", ["tl"] = "tgl", ["ne"] = "nep", ["si"] = "sin"
        };

        /// <summary>The three-letter language code video files use ("en" → "eng"), or null when it isn't known.</summary>
        public static string LanguageCode3(string code) => code != null && Iso3.TryGetValue(code, out var three) ? three : null;

        // ================================================================ reading what FFmpeg says

        private static readonly Regex DurationLine = new Regex(@"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant);
        private static readonly Regex TimeLine = new Regex(@"time=\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant);

        /// <summary>The length in a "Duration: 00:01:02.50, start: ..." line, in seconds; -1 when there isn't one.</summary>
        public static double Duration(string line) => Seconds(DurationLine.Match(line ?? string.Empty));

        /// <summary>How far it's got in a "frame= ... time=00:00:12.34 ..." line, in seconds; -1 when there isn't one.</summary>
        public static double Position(string line) => Seconds(TimeLine.Match(line ?? string.Empty));

        private static double Seconds(Match m) => m.Success
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
              + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)
            : -1;

        /// <summary>
        /// Runs FFmpeg. <paramref name="progress"/> gets 0..100 from its "time=" lines and the input's length. Cancelling
        /// stops it. Throws FfmpegException with its last error line when it fails.
        /// </summary>
        public static async Task Run(string exe, IEnumerable<string> args, string workingFolder, Action<int> progress, CancellationToken cancel)
        {
            var psi = new ProcessStartInfo(exe, Args.Join(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingFolder ?? Path.GetDirectoryName(exe) ?? string.Empty
            };
            var tail = new Queue<string>();
            double length = -1;
            using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var exited = new TaskCompletionSource<int>();
                p.Exited += (s, e) => exited.TrySetResult(0);
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (tail)
                    {
                        tail.Enqueue(e.Data);
                        if (tail.Count > 12) tail.Dequeue();
                    }
                    var d = Duration(e.Data);
                    if (d > 0 && length < 0) length = d;
                    var at = Position(e.Data);
                    if (at >= 0 && length > 0) progress?.Invoke((int)Math.Max(0, Math.Min(100, at * 100 / length)));
                };
                p.OutputDataReceived += (s, e) => { };
                try { p.Start(); }
                catch (System.ComponentModel.Win32Exception ex) { throw new FfmpegException("FFmpeg couldn't be started: " + ex.Message); }
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                using (cancel.Register(() => { try { if (!p.HasExited) p.Kill(); } catch (Exception) { } }))
                    await exited.Task.ConfigureAwait(false);
                p.WaitForExit();
                cancel.ThrowIfCancellationRequested();
                if (p.ExitCode != 0)
                {
                    string why;
                    lock (tail) why = tail.LastOrDefault(l => l.Trim().Length > 0)?.Trim();
                    throw new FfmpegException("FFmpeg stopped" + (why != null ? ": " + why : " (code " + p.ExitCode + ")."));
                }
            }
        }
    }

    public sealed class FfmpegException : Exception
    {
        public FfmpegException(string message) : base(message)
        {
        }
    }
}
