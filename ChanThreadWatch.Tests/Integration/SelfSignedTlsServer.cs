using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace JDP.Tests.Integration {
    // Accepts TLS connections on 127.0.0.1 with a freshly made self-signed certificate, which no
    // client trusts, so every handshake fails certificate validation on the client side.
    public sealed class SelfSignedTlsServer : IDisposable {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate = CreateCertificate();
        private readonly Thread _acceptThread;
        private volatile bool _disposed;

        public SelfSignedTlsServer() {
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "SelfSignedTlsServer:" + Port };
            _acceptThread.Start();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string URL(string path) => "https://127.0.0.1:" + Port + path;

        private static X509Certificate2 CreateCertificate() {
            using (RSA rsa = RSA.Create(2048)) {
                var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using (X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1))) {
                    // SslStream needs the private key in a key container, which a PFX round trip gives it
                    return new X509Certificate2(certificate.Export(X509ContentType.Pfx), (string)null, X509KeyStorageFlags.Exportable);
                }
            }
        }

        private void AcceptLoop() {
            try {
                while (!_disposed) {
                    TcpClient client = _listener.AcceptTcpClient();
                    new Thread(() => Handshake(client)) { IsBackground = true }.Start();
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void Handshake(TcpClient client) {
            try {
                using (client)
                using (var ssl = new SslStream(client.GetStream(), false)) {
                    ssl.AuthenticateAsServer(_certificate, false, SslProtocols.Tls12, false);
                }
            }
            catch (AuthenticationException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() {
            _disposed = true;
            _listener.Stop();
            _acceptThread.Join(5000);
            _certificate.Dispose();
        }
    }
}
