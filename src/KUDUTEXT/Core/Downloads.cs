using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Kudutext.Core
{
    /// <summary>Downloads a big file in a way that carries on after a drop and keeps it only when its checksum matches.</summary>
    public static class Downloads
    {
        /// <summary>
        /// Downloads <paramref name="url"/> into <paramref name="part"/>, carrying on from what's already there. When it's
        /// complete, <paramref name="check"/> must say it's right, or it's deleted. Throws DownloadFailedException with a
        /// plain reason that names <paramref name="what"/> ("the speech model").
        /// </summary>
        public static async Task Fetch(HttpClient http, string url, string part, long size, Func<string, bool> check, string what,
            Action<long, long> progress, CancellationToken cancel)
        {
            var stopped = $"The download of {what} stopped part way. Check the internet connection; it carries on from where it stopped.";
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > size)
            {
                File.Delete(part);
                have = 0;
            }
            if (have < size)
            {
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
                        {
                            if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // the site sends it all again
                            if (!response.IsSuccessStatusCode) throw new DownloadFailedException($"{Capital(what)} couldn't be downloaded (the site said {(int)response.StatusCode}).");
                            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write))
                            {
                                var buffer = new byte[81920];
                                int n;
                                var done = have;
                                while ((n = await input.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false)) > 0)
                                {
                                    await output.WriteAsync(buffer, 0, n, cancel).ConfigureAwait(false);
                                    done += n;
                                    progress?.Invoke(done, size);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || (ex is TaskCanceledException && !cancel.IsCancellationRequested))
                {
                    throw new DownloadFailedException(stopped);
                }
            }
            if (!File.Exists(part) || new FileInfo(part).Length != size) throw new DownloadFailedException(stopped);
            if (!check(part))
            {
                File.Delete(part);
                throw new DownloadFailedException($"The downloaded {what.Replace("the ", string.Empty)} didn't check out, so it wasn't kept. Please try again.");
            }
        }

        public static string Sha256Of(string file)
        {
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(file))
                return string.Concat(sha.ComputeHash(s).Select(b => b.ToString("x2")));
        }

        private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
