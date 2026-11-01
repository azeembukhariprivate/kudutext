using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kudutext.Core
{
    /// <summary>
    /// A small web server on this PC, for the tests and the self-test only (never used otherwise): serves files, in parts
    /// when asked for a range, and can drop the connection part way to check that a download carries on.
    /// </summary>
    public sealed class TestServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly ConcurrentDictionary<string, byte[]> _files = new ConcurrentDictionary<string, byte[]>();
        private int _drops;
        private long _dropAfter;

        public TestServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(Accept);
        }

        public int Port { get; }
        public int Requests;
        public ConcurrentQueue<string> RangesAsked { get; } = new ConcurrentQueue<string>();

        public string Url(string path) => "http://127.0.0.1:" + Port + "/" + path.TrimStart('/');

        public void Add(string path, byte[] body) => _files["/" + path.TrimStart('/')] = body;

        /// <summary>The next <paramref name="times"/> answers stop after <paramref name="bytes"/> bytes.</summary>
        public void Drop(int times, long bytes)
        {
            _dropAfter = bytes;
            _drops = times;
        }

        private async Task Accept()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    var first = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (first == null) return;
                    string range = null, line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line.Substring(6).Trim();
                    Interlocked.Increment(ref Requests);
                    var path = first.Split(' ')[1];
                    if (!_files.TryGetValue(path, out var body))
                    {
                        await Write(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                        return;
                    }
                    long from = 0;
                    if (range != null)
                    {
                        RangesAsked.Enqueue(range);
                        var r = range.Replace("bytes=", string.Empty).Split('-');
                        long.TryParse(r[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out from);
                        if (from >= body.Length) from = 0;
                    }
                    var count = body.Length - from;
                    var head = range != null
                        ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {from}-{body.Length - 1}/{body.Length}\r\n"
                        : "HTTP/1.1 200 OK\r\n";
                    head += $"Content-Type: application/octet-stream\r\nContent-Length: {count}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n";
                    await Write(stream, head).ConfigureAwait(false);
                    var send = count;
                    if (Interlocked.Decrement(ref _drops) >= 0) send = Math.Min(count, _dropAfter);
                    await stream.WriteAsync(body, (int)from, (int)send).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                }
            }
        }

        private static Task Write(Stream s, string text)
        {
            var b = Encoding.ASCII.GetBytes(text);
            return s.WriteAsync(b, 0, b.Length);
        }

        public void Dispose() => _listener.Stop();
    }
}
