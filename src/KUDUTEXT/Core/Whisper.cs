using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Kudutext.Core
{
    /// <summary>
    /// Runs whisper.cpp's whisper-cli on a 16 kHz mono WAV file and reads back what was said. Nothing leaves the PC.
    /// </summary>
    public static class Whisper
    {
        private static readonly Regex ProgressLine = new Regex(@"progress\s*=\s*(\d+)%", RegexOptions.CultureInvariant);

        /// <summary>The arguments for whisper-cli: the model, the sound, the language (or "auto"), translating, threads, JSON out.</summary>
        public static List<string> Arguments(string model, string wav, string outPrefix, string language, bool translate, int threads)
        {
            var args = new List<string>
            {
                "-m", model,
                "-f", wav,
                "-l", string.IsNullOrEmpty(language) ? "auto" : language,
                "-t", Math.Max(1, threads).ToString(),
                "-oj", "-of", outPrefix,
                "-pp"
            };
            if (translate) args.Add("-tr");
            return args;
        }

        /// <summary>The percentage in one of whisper's progress lines ("...: progress =  45%"), or -1.</summary>
        public static int Progress(string line)
        {
            var m = ProgressLine.Match(line ?? string.Empty);
            return m.Success && int.TryParse(m.Groups[1].Value, out var p) ? Math.Max(0, Math.Min(100, p)) : -1;
        }

        /// <summary>
        /// whisper-cli reads its command line in the system's code page, so a path with other letters (a Windows user name
        /// such as "Zoë" or "അസീം") is given in its short form. The app sets this; the tests leave it as it is.
        /// </summary>
        public static Func<string, string> SafePath { get; set; } = p => p;

        /// <summary>A sensible number of threads: all but one of the processor's cores (at least one, at most 16).</summary>
        public static int Threads => Math.Max(1, Math.Min(16, Environment.ProcessorCount - 1));

        /// <summary>
        /// Turns the speech in <paramref name="wav"/> into text. <paramref name="progress"/> gets 0..100. Cancelling stops
        /// whisper. Throws WhisperException with a plain reason when it fails.
        /// </summary>
        public static async Task<Transcript> Run(string exe, string model, string wav, string language, bool translate, Action<int> progress, CancellationToken cancel)
        {
            // whisper's own file goes in the work folder, never next to the user's file.
            Directory.CreateDirectory(Runner.WorkFolder);
            var outPrefix = Path.Combine(Runner.WorkFolder, Guid.NewGuid().ToString("N") + "-text");
            var outArg = Path.Combine(SafePath(Runner.WorkFolder), Path.GetFileName(outPrefix));
            var psi = new ProcessStartInfo(exe, Args.Join(Arguments(SafePath(model), SafePath(wav), outArg, language, translate, Threads)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty
            };
            var errors = new StringBuilder();
            using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var exited = new TaskCompletionSource<int>();
                p.Exited += (s, e) => exited.TrySetResult(0);
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    var pc = Progress(e.Data);
                    if (pc >= 0) progress?.Invoke(pc);
                    else lock (errors) { if (errors.Length < 20000) errors.AppendLine(e.Data); }
                };
                p.OutputDataReceived += (s, e) => { };
                try { p.Start(); }
                catch (System.ComponentModel.Win32Exception ex) { throw new WhisperException("The speech engine couldn't be started: " + ex.Message); }
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                using (cancel.Register(() => { try { if (!p.HasExited) p.Kill(); } catch (Exception) { } }))
                    await exited.Task.ConfigureAwait(false);
                p.WaitForExit();
                cancel.ThrowIfCancellationRequested();
                var json = outPrefix + ".json";
                if (p.ExitCode != 0 || !File.Exists(json))
                {
                    var why = errors.ToString().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0);
                    throw new WhisperException("The speech engine stopped" + (why != null ? ": " + why : " (code " + p.ExitCode + ")."));
                }
                try { return WhisperJson.Parse(File.ReadAllText(json, Encoding.UTF8)); }
                finally { try { File.Delete(json); } catch (IOException) { } }
            }
        }
    }

    public sealed class WhisperException : Exception
    {
        public WhisperException(string message) : base(message)
        {
        }
    }
}
