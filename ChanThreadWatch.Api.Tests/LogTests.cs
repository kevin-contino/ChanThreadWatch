using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Api.Tests {
    // Security items 7 and 15: no token, hash or Authorization header in any log
    [TestClass]
    public class LogTests : ApiTestBase {
        [TestMethod]
        public void Log_NeverHoldsTheTokenOrItsHash() {
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token)));
            string port = Port.ToString(CultureInfo.InvariantCulture);
            Get(ThreadsEndpoints.ThreadsPath);
            Get(ThreadsEndpoints.ThreadsPath + "?token=" + Token);
            Get("/missing/" + Token);
            Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.TryAddWithoutValidation("Origin", "http://" + Token));
            Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", Token));
            Send(HttpMethod.Put, ThreadsEndpoints.ThreadsPath, "{\"token\":\"" + Token + "\"}");
            PostJson("{\"url\":\"" + Token + "\"}");
            PostJson("{\"token\":\"" + Token + "\"," + new string(' ', 5000) + "}");
            SendRaw("GET /api/v1/threads?token=" + Token + " HTTP/9.9\r\nHost: 127.0.0.1:" + port + "\r\n\r\n");
            SendRaw("GET /api/v1/threads HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nAuthorization: Bearer " + Token + "\r\nBad Header " + Token + "\r\n\r\n");
            SendRaw(RawGet(ThreadsEndpoints.ThreadsPath, "evil.example:" + port));
            Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token + "x");
            Get(ThreadsEndpoints.ThreadsPath);

            IReadOnlyList<string> entries = Log.Entries;
            Assert.AreNotEqual(0, entries.Count, "nothing was captured, so the check below proves nothing");
            string[] secrets = { Token, Token.Substring(4), hash, hash.ToUpperInvariant() };
            foreach (string entry in entries) {
                foreach (string secret in secrets) {
                    Assert.IsFalse(entry.Contains(secret, StringComparison.Ordinal), "log entry holds a secret: " + entry.Replace(Token, "<token>"));
                }
            }
            string coreLog = ReadCoreLog();
            foreach (string secret in secrets) {
                Assert.IsFalse(coreLog.Contains(secret, StringComparison.Ordinal), "log.txt holds a secret");
            }
        }

        // A failure in the handler gives 500 and logs the exception's type only: its text can hold a URL with a login
        [TestMethod]
        public void Log_FailedRequestLogsTheTypeOnly() {
            NewThreadFactory = url => throw new InvalidOperationException("http://u:secret@boards.4chan.org/a/thread/1");
            AssertProblem(AddThread(ThreadUrl), HttpStatusCode.InternalServerError, "internal_error");
            string coreLog = ReadCoreLog();
            StringAssert.Contains(coreLog, "Local API: a request failed: System.InvalidOperationException");
            Assert.IsFalse(coreLog.Contains("secret", StringComparison.Ordinal));
            Assert.IsFalse(Log.Entries.Any(entry => entry.Contains("secret", StringComparison.Ordinal)));
        }

        // A malformed chunked body is Kestrel's BadHttpRequestException: 400 invalid_body, not 500, and nothing logged
        [TestMethod]
        public void Body_MalformedChunkIs400AndNotLogged() {
            string before = ReadCoreLog();
            string port = Port.ToString(CultureInfo.InvariantCulture);
            RawResponse response = SendRaw("POST " + ThreadsEndpoints.ThreadsPath + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nAuthorization: Bearer " + Token +
                "\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\nZZ" + Token + "\r\n{}\r\n0\r\n\r\n");
            Assert.AreEqual(400, response.Status, response.ToString());
            StringAssert.Contains(response.Text, "\"code\":\"invalid_body\"");
            Assert.AreEqual(before, ReadCoreLog());
            Assert.IsFalse(Log.Entries.Any(entry => entry.Contains(Token, StringComparison.Ordinal)));
            Assert.AreEqual(0, ThreadCount());
        }

        // Warnings and errors reach log.txt as category, event and exception type, never the message or exception text
        [TestMethod]
        public void CoreLogger_WritesWarningsWithoutMessageOrExceptionText() {
            string marker = "ApiTest." + Guid.NewGuid().ToString("N");
            string secret = "secret-" + Guid.NewGuid().ToString("N");
            using (CoreLoggerProvider provider = new CoreLoggerProvider()) {
                ILogger logger = provider.CreateLogger(marker);
                Assert.IsFalse(logger.IsEnabled(LogLevel.Information));
                Assert.IsFalse(logger.IsEnabled(LogLevel.None));
                Assert.IsTrue(logger.IsEnabled(LogLevel.Warning));
                logger.Log(LogLevel.Information, new EventId(1, "Info"), secret, null, (state, error) => state);
                logger.Log(LogLevel.Error, new EventId(7, "Failed"), "message " + secret, new InvalidOperationException("exception " + secret), (state, error) => state);
            }
            string coreLog = ReadCoreLog();
            StringAssert.Contains(coreLog, "Local API Error: " + marker + ", event 7 (Failed), System.InvalidOperationException");
            Assert.AreEqual(1, coreLog.Split(marker).Length - 1);
            Assert.IsFalse(coreLog.Contains(secret, StringComparison.Ordinal));
        }

        private static string ReadCoreLog() {
            using (FileStream stream = new FileStream(ApiTestHost.LogPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream)) {
                return reader.ReadToEnd();
            }
        }
    }

    // L1 review (b) and the SSRF guard: the library never uses the test-only hooks of Core
    [TestClass]
    public class SourceTests {
        [TestMethod]
        public void Library_NeverCallsTestOnlyHooks() {
            AssertNoFileUses("RegisterHostForTesting", "UnregisterHostForTesting", "AllowLoopbackForTesting", "AllowSettingsChangeForTesting", "BeforeConnect", "AppDataDirectoryForTesting", "DefaultFoldersParentForTesting", "ServiceMode =");
        }

        // The API marks the thread list as changed (SaveThreadListPending) and leaves the saving to the host; it never
        // writes threads.txt or settings.txt itself
        [TestMethod]
        public void Library_NeverWritesTheThreadListOrSettings() {
            AssertNoFileUses("Settings.Save", "Settings.Load", "ThreadListFile", "TextFile.", "SaveThreadList(", "File.WriteAll", "File.AppendAll", "BackupThreadList");
        }

        private static void AssertNoFileUses(params string[] forbidden) {
            string folder = Path.Combine(FindRepositoryRoot(), "src", "ChanThreadWatch.Api");
            string[] files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)).ToArray();
            Assert.AreNotEqual(0, files.Length);
            foreach (string file in files) {
                string text = File.ReadAllText(file);
                foreach (string name in forbidden) {
                    Assert.IsFalse(text.Contains(name, StringComparison.Ordinal), Path.GetFileName(file) + " uses " + name);
                }
            }
        }

        private static string FindRepositoryRoot() {
            for (DirectoryInfo folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent) {
                if (File.Exists(Path.Combine(folder.FullName, "ChanThreadWatch.sln"))) return folder.FullName;
            }
            throw new InvalidOperationException("The repository root (ChanThreadWatch.sln) was not found above " + AppContext.BaseDirectory);
        }
    }
}
