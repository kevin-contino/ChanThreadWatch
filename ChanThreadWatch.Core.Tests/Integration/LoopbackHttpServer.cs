using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace JDP.Tests.Integration {
    // One request as the server received it
    public sealed class RecordedRequest {
        public string Method { get; set; }
        public string Path { get; set; }
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Raw { get; set; }

        // 1-based number of the TCP connection the request arrived on, in order of acceptance
        public int ConnectionId { get; set; }

        public string Header(string name) {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }

        // Decodes a "Basic" Authorization header to "user:pass", or returns null if there is none
        public string BasicAuth {
            get {
                string value = Header("Authorization");
                if (value == null || !value.StartsWith("Basic ", StringComparison.Ordinal)) return null;
                return Encoding.GetEncoding("iso-8859-1").GetString(Convert.FromBase64String(value.Substring(6)));
            }
        }

        public override string ToString() {
            return Method + " " + Path;
        }
    }

    // A response the server sends for a route: either a status/headers/body triple or raw bytes sent as-is
    public sealed class LoopbackResponse {
        public int Status { get; set; } = 200;
        public string Reason { get; set; } = "OK";
        public List<KeyValuePair<string, string>> Headers { get; } = new List<KeyValuePair<string, string>>();
        public byte[] Body { get; set; } = new byte[0];
        public byte[] RawBytes { get; set; }

        // After RawBytes are sent, wait briefly and then reset the connection (TCP RST)
        public bool ResetConnection { get; set; }

        public static LoopbackResponse Bytes(byte[] body, string contentType = "application/octet-stream") {
            var response = new LoopbackResponse { Body = body };
            response.Headers.Add(new KeyValuePair<string, string>("Content-Type", contentType));
            return response;
        }

        public static LoopbackResponse Html(string html) {
            return Bytes(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
        }

        public static LoopbackResponse Text(string text) {
            return Bytes(Encoding.ASCII.GetBytes(text), "text/plain");
        }

        // A 200 response whose body is sent with chunked transfer encoding, so it has no
        // Content-Length; the connection is closed afterwards
        public static LoopbackResponse Chunked(byte[] body, string contentType, int chunkSize = 1000) {
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            var raw = new MemoryStream();
            WriteASCII(raw, "HTTP/1.1 200 OK\r\nContent-Type: " + contentType + "\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
            for (int offset = 0; offset < body.Length; offset += chunkSize) {
                int length = Math.Min(chunkSize, body.Length - offset);
                WriteASCII(raw, length.ToString("x") + "\r\n");
                raw.Write(body, offset, length);
                WriteASCII(raw, "\r\n");
            }
            WriteASCII(raw, "0\r\n\r\n");
            return new LoopbackResponse { RawBytes = raw.ToArray() };
        }

        private static void WriteASCII(Stream stream, string text) {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        public static LoopbackResponse StatusOnly(int status, string reason) {
            return new LoopbackResponse { Status = status, Reason = reason };
        }

        // Sent verbatim; the connection is closed afterwards
        public static LoopbackResponse Raw(string raw) {
            return new LoopbackResponse { RawBytes = Encoding.ASCII.GetBytes(raw) };
        }

        // Sent verbatim, then the connection stays open (stalled) until release is set or 30 s pass
        public static LoopbackResponse RawThenStall(string raw, WaitHandle release) {
            return new LoopbackResponse { RawBytes = Encoding.ASCII.GetBytes(raw), StallUntil = release };
        }

        public WaitHandle StallUntil { get; set; }

        // Sent verbatim, then dripSize more body bytes every dripInterval until release is set,
        // the client closes the connection or 30 s pass
        public static LoopbackResponse RawThenDrip(string raw, WaitHandle release, TimeSpan dripInterval, int dripSize = 1) {
            return new LoopbackResponse { RawBytes = Encoding.ASCII.GetBytes(raw), StallUntil = release, DripInterval = dripInterval, DripSize = dripSize };
        }

        public TimeSpan? DripInterval { get; set; }

        public int DripSize { get; set; } = 1;

        // Sends the status line, the headers (announcing the full body length) and the first
        // sentBodyLength bytes of the body, then resets the connection as a network failure would
        public static LoopbackResponse ResetAfter(byte[] body, int sentBodyLength, string contentType) {
            string head = "HTTP/1.1 200 OK\r\nContent-Type: " + contentType + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            byte[] raw = new byte[headBytes.Length + sentBodyLength];
            Buffer.BlockCopy(headBytes, 0, raw, 0, headBytes.Length);
            Buffer.BlockCopy(body, 0, raw, headBytes.Length, sentBodyLength);
            return new LoopbackResponse { RawBytes = raw, ResetConnection = true };
        }

        public LoopbackResponse WithHeader(string name, string value) {
            Headers.Add(new KeyValuePair<string, string>(name, value));
            return this;
        }

        internal byte[] Serialize(bool keepAlive) {
            if (RawBytes != null) return RawBytes;
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(Status).Append(' ').Append(Reason).Append("\r\n");
            foreach (KeyValuePair<string, string> header in Headers) {
                sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
            sb.Append("Content-Length: ").Append(Body.Length).Append("\r\n");
            sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            byte[] all = new byte[head.Length + Body.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(Body, 0, all, head.Length, Body.Length);
            return all;
        }
    }

    // Minimal HTTP/1.1 server on 127.0.0.1 with an ephemeral port. Serves routes by exact path (query
    // string included), answers unknown paths with 404, records every request, and handles each
    // connection on its own thread. By default every response carries "Connection: close"; set
    // KeepAlive to serve several requests per connection.
    public sealed class LoopbackHttpServer : IDisposable {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly Thread _acceptThread;
        private readonly Dictionary<string, Func<RecordedRequest, LoopbackResponse>> _routes = new Dictionary<string, Func<RecordedRequest, LoopbackResponse>>(StringComparer.Ordinal);
        private readonly List<RecordedRequest> _requests = new List<RecordedRequest>();
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private volatile bool _disposed;
        private int _connectionCount;

        public LoopbackHttpServer() {
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LoopbackHttpServer:" + Port };
            _acceptThread.Start();
        }

        public bool KeepAlive { get; set; }

        // Number of TCP connections accepted so far
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        // Base URL using the given host name, e.g. "http://127.0.0.1:1234" or "http://localhost:1234"
        public string BaseURL(string host = "127.0.0.1") => "http://" + host + ":" + Port;

        public string URL(string path, string host = "127.0.0.1") => BaseURL(host) + path;

        // Snapshot of the requests received so far, in arrival order
        public List<RecordedRequest> Requests {
            get { lock (_requests) return new List<RecordedRequest>(_requests); }
        }

        public List<RecordedRequest> RequestsTo(string path) {
            return Requests.FindAll(r => r.Path == path);
        }

        public void Route(string path, LoopbackResponse response) {
            Route(path, r => response);
        }

        public void Route(string path, Func<RecordedRequest, LoopbackResponse> handler) {
            lock (_routes) _routes[path] = handler;
        }

        // Answers the n-th request to the path (0-based) with responses[n], repeating the last one
        public void RouteSequence(string path, params LoopbackResponse[] responses) {
            int count = 0;
            Route(path, r => responses[Math.Min(Interlocked.Increment(ref count) - 1, responses.Length - 1)]);
        }

        private Func<RecordedRequest, LoopbackResponse> FindRoute(string path) {
            lock (_routes) {
                Func<RecordedRequest, LoopbackResponse> handler;
                return _routes.TryGetValue(path, out handler) ? handler : null;
            }
        }

        private void AcceptLoop() {
            try {
                while (!_disposed) {
                    TcpClient client = _listener.AcceptTcpClient();
                    lock (_clients) _clients.Add(client);
                    int connectionId = Interlocked.Increment(ref _connectionCount);
                    new Thread(() => ServeConnection(client, connectionId)) { IsBackground = true }.Start();
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void ServeConnection(TcpClient client, int connectionId) {
            try {
                using (client)
                using (NetworkStream stream = client.GetStream()) {
                    bool keepGoing = true;
                    while (keepGoing && !_disposed) {
                        RecordedRequest request = ReadRequest(stream);
                        if (request == null) break;
                        request.ConnectionId = connectionId;
                        lock (_requests) _requests.Add(request);
                        keepGoing = Respond(client, stream, request);
                    }
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally {
                lock (_clients) _clients.Remove(client);
            }
        }

        // Returns whether the connection stays open for another request
        private bool Respond(TcpClient client, NetworkStream stream, RecordedRequest request) {
            Func<RecordedRequest, LoopbackResponse> handler = FindRoute(request.Path);
            LoopbackResponse response = handler != null ? handler(request) : LoopbackResponse.StatusOnly(404, "Not Found");
            bool keepAlive = KeepAlive && response.RawBytes == null &&
                !String.Equals(request.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);
            byte[] bytes = response.Serialize(keepAlive);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            if (response.StallUntil != null) Stall(stream, response);
            if (response.ResetConnection) {
                // Give the client time to read what was sent; a reset discards unread data
                Thread.Sleep(300);
                client.Client.LingerState = new LingerOption(true, 0);
                client.Client.Close();
                return false;
            }
            return keepAlive;
        }

        // Waits for StallUntil, sending DripSize more body bytes every DripInterval if it is set
        private static void Stall(NetworkStream stream, LoopbackResponse response) {
            if (response.DripInterval == null) {
                response.StallUntil.WaitOne(TimeSpan.FromSeconds(30));
                return;
            }
            byte[] piece = Encoding.ASCII.GetBytes(new string('a', response.DripSize));
            DateTime end = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < end && !response.StallUntil.WaitOne(response.DripInterval.Value)) {
                stream.Write(piece, 0, piece.Length);
                stream.Flush();
            }
        }

        // Reads one request (headers, then any Content-Length body); returns null at end of stream
        private static RecordedRequest ReadRequest(NetworkStream stream) {
            string head = ReadHead(stream);
            if (head == null) return null;
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] requestLine = lines[0].Split(' ');
            var request = new RecordedRequest {
                Method = requestLine[0],
                Path = requestLine.Length > 1 ? requestLine[1] : String.Empty,
                Raw = head
            };
            for (int i = 1; i < lines.Length; i++) {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            SkipBody(stream, request.Header("Content-Length"));
            return request;
        }

        private static string ReadHead(NetworkStream stream) {
            var bytes = new List<byte>();
            while (true) {
                int b = stream.ReadByte();
                if (b == -1) return null;
                bytes.Add((byte)b);
                int n = bytes.Count;
                if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n') {
                    return Encoding.ASCII.GetString(bytes.ToArray(), 0, n - 4);
                }
            }
        }

        private static void SkipBody(NetworkStream stream, string contentLength) {
            int remaining;
            if (!Int32.TryParse(contentLength, out remaining)) return;
            var buffer = new byte[4096];
            while (remaining > 0) {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read == 0) return;
                remaining -= read;
            }
        }

        public void Dispose() {
            _disposed = true;
            _listener.Stop();
            _acceptThread.Join(5000);
            lock (_clients) {
                foreach (TcpClient client in _clients) client.Close();
            }
        }
    }
}
