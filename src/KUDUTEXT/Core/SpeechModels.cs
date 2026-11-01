using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Kudutext.Core
{
    /// <summary>A speech model whisper.cpp uses: bigger ones are more accurate and slower.</summary>
    public sealed class SpeechModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string About { get; set; }
        public string FileName { get; set; }
        public long Size { get; set; }
        /// <summary>The SHA-1 checksum whisper.cpp publishes for the file (models/README.md).</summary>
        public string Sha1 { get; set; }
        public string Url => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileName;
    }

    /// <summary>
    /// The speech models KUDUTEXT offers. They're downloaded the first time they're needed into
    /// %LocalAppData%\KUDUTEXT\Models, and kept only when their checksum matches the one whisper.cpp publishes.
    /// </summary>
    public static class SpeechModels
    {
        public static readonly IReadOnlyList<SpeechModel> All = new[]
        {
            new SpeechModel { Id = "fast", Name = "Fast", About = "Quick, for clear speech", FileName = "ggml-base.bin", Size = 147951465, Sha1 = "465707469ff3a37a2b9b8d8f89f2f99de7299dac" },
            new SpeechModel { Id = "good", Name = "Good", About = "The best balance of speed and accuracy", FileName = "ggml-small.bin", Size = 487601967, Sha1 = "55356645c2b361a969dfd0ef2c5a50d530afd8d5" },
            new SpeechModel { Id = "best", Name = "Best", About = "Most accurate, slowest", FileName = "ggml-medium.bin", Size = 1533763059, Sha1 = "fd9727b6e1217c2f614f9b698455c4ffd82463b4" }
        };

        /// <summary>The smallest model, used by the self-test only (not offered in the app).</summary>
        public static readonly SpeechModel Tiny = new SpeechModel { Id = "tiny", Name = "Tiny", FileName = "ggml-tiny.bin", Size = 77691713, Sha1 = "bd577a113a864445d4c299885e0cb97d4ba92b5f" };

        public static SpeechModel Default => All[1];

        public static SpeechModel Find(string id) => All.FirstOrDefault(m => m.Id == id) ?? (id == Tiny.Id ? Tiny : Default);

        /// <summary>Where models are kept (the self-test uses its own folder).</summary>
        public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KUDUTEXT", "Models");

        public static string PathOf(SpeechModel m) => System.IO.Path.Combine(Folder, m.FileName);

        /// <summary>The model is downloaded and was checked (a small ".ok" note is written beside it once its checksum matched).</summary>
        public static bool IsReady(SpeechModel m)
        {
            var file = PathOf(m);
            return File.Exists(file) && File.Exists(file + ".ok") && File.ReadAllText(file + ".ok").Trim() == m.Sha1;
        }

        public static string Sha1Of(string file)
        {
            using (var sha = SHA1.Create())
            using (var s = File.OpenRead(file))
                return string.Concat(sha.ComputeHash(s).Select(b => b.ToString("x2")));
        }

        /// <summary>
        /// Downloads a model (carrying on from a part already here), checks it, and keeps it only if its checksum matches.
        /// <paramref name="progress"/> gets the bytes so far. Throws DownloadFailedException with a plain reason.
        /// </summary>
        public static async Task Download(HttpClient http, SpeechModel m, Action<long, long> progress, CancellationToken cancel, string url = null)
        {
            Directory.CreateDirectory(Folder);
            var file = PathOf(m);
            var part = file + ".part";
            await Downloads.Fetch(http, url ?? m.Url, part, m.Size, p => string.Equals(Sha1Of(p), m.Sha1, StringComparison.OrdinalIgnoreCase),
                "the speech model", progress, cancel).ConfigureAwait(false);
            if (File.Exists(file)) File.Delete(file);
            File.Move(part, file);
            File.WriteAllText(file + ".ok", m.Sha1);
        }
    }

    public sealed class DownloadFailedException : Exception
    {
        public DownloadFailedException(string message) : base(message)
        {
        }
    }
}
