using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Api.Tests {
    // What a test extension learned from a hello, and the key it stretched from the code it was given
    public sealed class TestHello {
        public string Origin { get; set; }
        public string ClientNonce { get; set; }
        public string PairingId { get; set; }
        public string Salt { get; set; }
        public string ServerNonce { get; set; }
        public string ServerName { get; set; }
        public string Proof { get; set; }
        public byte[] Key { get; set; }
    }

    // A C# stand-in for the extension (MP-7b design 3.4): the pairing messages, the proofs it checks, and requests
    // without the scripts' token. The clock is the test's, so a test can move it past the code's expiry.
    public abstract class PairingTestBase : ApiTestBase {
        protected const string ChromeOrigin = ApiPairing.ChromeScheme + ApiPairing.ChromeExtensionId;
        protected const string OtherChromeOrigin = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
        protected const string FirefoxOrigin = "moz-extension://0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5f";
        protected const string OtherFirefoxOrigin = "moz-extension://7c9e6679-7425-40de-944b-e07fc1f90ae7";

        protected DateTimeOffset Now { get; set; }
        protected HttpClient Anonymous { get; private set; }

        internal ApiPairingFile PairingFile {
            get { return new ApiPairingFile(Folder); }
        }

        internal ApiClientStore Clients {
            get { return new ApiClientStore(Folder); }
        }

        internal override void ConfigurePolicy(ApiPolicy policy) {
            Now = DateTimeOffset.UtcNow;
            policy.UtcNow = () => Now;
            policy.PairingRequestsPerMinute = 1000;
            policy.ProofRequestsPerMinute = 1000;
        }

        [TestInitialize]
        public void CreateAnonymousClient() {
            Anonymous = CreateClient(Port);
        }

        [TestCleanup]
        public void DisposeAnonymousClient() {
            Anonymous?.Dispose();
            ApiPairingFile.ActingForTesting = null;
            ApiTokenStore.OpeningForTesting = null;
            PairingEndpoints.FinishArrivingForTesting = null;
            PairingEndpoints.CompletingForTesting = null;
            PairingEndpoints.HelloReadForTesting = null;
            PairingEndpoints.FinishReadForTesting = null;
        }

        // From the next read of api-pairing.txt by a writer about to act on it, every read of that file fails (access
        // denied), as if another program held it
        protected void FailPairingFileReadsWhenActing() {
            string pairingPath = PairingFile.Path;
            bool failing = false;
            ApiPairingFile.ActingForTesting = () => failing = true;
            ApiTokenStore.OpeningForTesting = path => {
                if (failing && path == pairingPath) throw new IOException("test: access denied", unchecked((int)0x80070005));
            };
        }

        protected static string Hex(byte[] bytes) {
            return Convert.ToHexStringLower(bytes);
        }

        internal ApiPairingCode NewCode() {
            return PairingFile.Create(Now);
        }

        protected HttpResponseMessage PostAnonymous(string path, string json, string origin, string contentType = "application/json") {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path)) {
                request.Content = new StringContent(json, Encoding.UTF8, contentType);
                if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
                return Anonymous.SendAsync(request).GetAwaiter().GetResult();
            }
        }

        protected HttpResponseMessage PostPairing(object body, string origin) {
            return PostAnonymous(PairingEndpoints.PairingPath, JsonSerializer.Serialize(body), origin);
        }

        protected HttpResponseMessage PostHello(string origin, string clientNonce = null) {
            return PostPairing(new { step = "hello", clientNonce = clientNonce ?? ApiPairing.NewNonce() }, origin);
        }

        // Step 1 with the code the user typed; the key is stretched here as the extension does
        protected TestHello SendHello(string code, string origin) {
            string clientNonce = ApiPairing.NewNonce();
            HttpResponseMessage response = PostHello(origin, clientNonce);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, Body(response));
            JsonElement json = Json(response);
            TestHello hello = new TestHello {
                Origin = origin, ClientNonce = clientNonce, PairingId = json.GetProperty("pairingId").GetString(), Salt = json.GetProperty("salt").GetString(),
                ServerNonce = json.GetProperty("serverNonce").GetString(), ServerName = json.GetProperty("serverName").GetString(), Proof = json.GetProperty("proof").GetString()
            };
            hello.Key = ApiPairing.DeriveKey(code.Replace("-", ""), ApiPairing.FromBase64Url(hello.Salt, ApiPairing.SaltBytes));
            return hello;
        }

        // The extension's check of P1, at the port it contacted
        protected static bool CheckHelloProof(TestHello hello, int port) {
            byte[] expected = ApiPairing.ServerHelloProof(hello.Key, port, hello.Origin, hello.PairingId, hello.ClientNonce, hello.ServerNonce, hello.ServerName);
            return CryptographicOperations.FixedTimeEquals(expected, ApiPairing.FromBase64Url(hello.Proof, ApiPairing.ProofBytes) ?? new byte[0]);
        }

        protected static string FinishProof(TestHello hello, int port, string origin) {
            return ApiPairing.Base64Url(ApiPairing.ClientFinishProof(hello.Key, port, origin, hello.PairingId, hello.ClientNonce, hello.ServerNonce));
        }

        protected HttpResponseMessage SendFinish(TestHello hello, string origin = null, string proof = null, string serverNonce = null) {
            origin = origin ?? hello.Origin;
            return PostPairing(new {
                step = "finish", pairingId = hello.PairingId, clientNonce = hello.ClientNonce, serverNonce = serverNonce ?? hello.ServerNonce,
                proof = proof ?? FinishProof(hello, Port, origin)
            }, origin);
        }

        // The extension's check of P3 before it saves the token
        protected static bool CheckFinishProof(TestHello hello, int port, string token, string proof) {
            byte[] expected = ApiPairing.ServerFinishProof(hello.Key, port, hello.Origin, hello.PairingId, hello.ClientNonce, hello.ServerNonce, token);
            return CryptographicOperations.FixedTimeEquals(expected, ApiPairing.FromBase64Url(proof, ApiPairing.ProofBytes) ?? new byte[0]);
        }

        // A whole pairing with a new code; returns the token after P1 and P3 checked
        protected string Pair(string origin) {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, origin);
            Assert.IsTrue(CheckHelloProof(hello, Port), "P1");
            HttpResponseMessage finish = SendFinish(hello);
            Assert.AreEqual(HttpStatusCode.OK, finish.StatusCode, Body(finish));
            JsonElement json = Json(finish);
            string token = json.GetProperty("token").GetString();
            Assert.IsTrue(CheckFinishProof(hello, Port, token, json.GetProperty("proof").GetString()), "P3");
            return token;
        }

        // A request with this token (and no scripts' token) and Origin
        protected HttpResponseMessage SendWithToken(HttpMethod method, string path, string token, string origin, string json = null) {
            return Send(method, path, json, request => {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
            });
        }

        protected HttpResponseMessage AddWithToken(string token, string origin, string url = ThreadUrl) {
            return SendWithToken(HttpMethod.Post, ThreadsEndpoints.ThreadsPath, token, origin, JsonSerializer.Serialize(new { url }));
        }

        protected HttpResponseMessage PostProof(string origin, string clientNonce) {
            return PostAnonymous(PairingEndpoints.ProofPath, JsonSerializer.Serialize(new { clientNonce }), origin);
        }

        protected static byte[] TokenHash(string token) {
            return SHA256.HashData(Encoding.UTF8.GetBytes(token));
        }

        // The client's check of a proof before use
        protected static bool CheckUseProof(string token, int port, string origin, string clientNonce, string proof) {
            return ApiPairing.Base64Url(ApiPairing.UseProof(TokenHash(token), port, origin, clientNonce)) == proof;
        }

        protected string RawPost(string path, string headers, string body = "{}", string method = "POST") {
            string port = Port.ToString(CultureInfo.InvariantCulture);
            return method + " " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\n" + headers + "Content-Type: application/json\r\nContent-Length: " + Encoding.ASCII.GetByteCount(body) +
                   "\r\nConnection: close\r\n\r\n" + body;
        }

        // A POST whose declared length is over the limit, sent without its body. The two routes refuse it on the declared
        // length before they read anything, and close the connection; a client that also sends the body can then see
        // the connection reset instead of the 413 (the body arrives after the close), so the tests send none.
        protected void AssertDeclaredOversizeRefused(string path, string origin) {
            string originLine = origin != null ? "Origin: " + origin + "\r\n" : "";
            RawResponse response = SendRaw("POST " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "\r\n" + originLine +
                "Content-Type: application/json\r\nContent-Length: 5000\r\nConnection: close\r\n\r\n");
            Assert.AreEqual(413, response.Status, response.ToString());
            StringAssert.Contains(response.Text, "\"code\":\"body_too_large\"");
            StringAssert.Contains(response.Head, "Connection: close");
        }

        protected static int OtherPort(int port) {
            return port == 65535 ? port - 1 : port + 1;
        }
    }

    // The pairing protocol on a real server: hello, finish, burn rules and the proofs
    [TestClass]
    public class PairingTests : PairingTestBase {
        [TestMethod]
        [DataRow(ChromeOrigin, "chrome")]
        [DataRow(FirefoxOrigin, "firefox")]
        public void Pair_HappyPathGivesABoundTokenAndKeepsTheScriptToken(string origin, string family) {
            string scriptHash = FileHash(Tokens.Path);
            string token = Pair(origin);

            StringAssert.Matches(token, new Regex("^ctwe_[A-Za-z0-9_-]{43}$"));
            HttpResponseMessage added = AddWithToken(token, origin);
            Assert.AreEqual(HttpStatusCode.Created, added.StatusCode, Body(added));
            Assert.AreEqual(1, ThreadCount());
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode, "the scripts' token still works");
            Assert.AreEqual(scriptHash, FileHash(Tokens.Path), "api-token.txt is not changed");

            string[] lines = File.ReadAllText(Clients.Path).Split('\n');
            Assert.AreEqual(3, lines.Length);
            Assert.AreEqual("1", lines[0]);
            StringAssert.Matches(lines[1], new Regex("^" + family + " " + Regex.Escape(origin) + " sha256:" + Hex(TokenHash(token)) + " \\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\dZ$"));
            Assert.IsFalse(File.ReadAllText(Clients.Path).Contains(token.Substring(5), StringComparison.Ordinal));
            AssertOwnerOnly(Clients.Path);

            bool untrusted;
            ApiPendingPairing pending = PairingFile.Read(out untrusted);
            Assert.AreEqual(family, pending.PairedFamily);
            StringAssert.EndsWith(File.ReadAllText(PairingFile.Path), "state:paired:" + family + "\n");
            Assert.AreEqual(0, Directory.GetFiles(Folder, "*.tmp").Length);
        }

        internal static void AssertOwnerOnly(string path) {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                Assert.IsTrue(OwnerOnlyFile.IsOwnerOnly(stream), path);
            }
        }

        [TestMethod]
        public void Pair_HelloAnswersTheServerNameAndSalt() {
            NewCode();
            HttpResponseMessage response = PostHello(ChromeOrigin);
            JsonElement json = Json(response);
            Assert.AreEqual("Chan Thread Watch", json.GetProperty("serverName").GetString());
            bool untrusted;
            ApiPendingPairing pending = PairingFile.Read(out untrusted);
            Assert.AreEqual(pending.Id, json.GetProperty("pairingId").GetString());
            Assert.AreEqual(pending.Salt, json.GetProperty("salt").GetString());
            CollectionAssert.AreEquivalent(new[] { "pairingId", "salt", "serverNonce", "serverName", "proof" }, json.EnumerateObject().Select(member => member.Name).ToArray());

            Policy.ServerName = "ctw watch 1.0.0 on TEST-PC";
            Assert.AreEqual("ctw watch 1.0.0 on TEST-PC", Json(PostHello(ChromeOrigin)).GetProperty("serverName").GetString());
        }

        // A host's name made from any text is one the setter takes
        [TestMethod]
        public void ServerName_NormalizeDropsUnprintableAndCutsTo80() {
            string longName = "ctw watch 1.40.0 on " + new string('h', 200);
            string normalized = ApiPolicy.NormalizeServerName(longName);
            Assert.AreEqual(longName.Substring(0, 80), normalized);
            Policy.ServerName = normalized;
            Assert.AreEqual("ctw watch on PC-1", ApiPolicy.NormalizeServerName(" ctw\u0000 watch\n on\u202E PC-1\t\r\n"));
            Assert.AreEqual("Büro", ApiPolicy.NormalizeServerName("B\u00fcro\uD83D\uDE00"));
            foreach (string empty in new[] { null, "", "\n\t", "\u202E" }) {
                Assert.AreEqual("Chan Thread Watch", ApiPolicy.NormalizeServerName(empty), empty ?? "null");
            }
            Assert.AreEqual(new string('a', 79), ApiPolicy.NormalizeServerName(new string('a', 79) + " b"), "no trailing space after the cut");
        }

        // The name is a line of P1, so a line break or another unprintable character could change the message
        [TestMethod]
        public void ServerName_OnlyPrintableNamesOfAtMost80Characters() {
            foreach (string bad in new[] { null, "", new string('a', 81), "a\nb", "a\rb", "a\u2028b", "a\u0085b", "a\tb", "evil\u202Ename", "a\u200Bb", "\uD800" }) {
                Assert.ThrowsExactly<ArgumentException>(() => Policy.ServerName = bad, bad ?? "null");
            }
            Policy.ServerName = new string('a', 80);
            Policy.ServerName = "Chan Thread Watch 1.40.0 on Büro-PC";
            Assert.AreEqual("Chan Thread Watch 1.40.0 on Büro-PC", Policy.ServerName);
        }

        // Acceptance: a wrong code fails P1 in the extension, which sends nothing more; a finish with a wrong proof
        // burns the code
        [TestMethod]
        public void WrongCode_FailsP1AndAFinishBurnsTheCode() {
            ApiPairingCode code = NewCode();
            string wrong = (code.Code[0] == '0' ? "1" : "0") + code.Code.Substring(1);
            TestHello hello = SendHello(wrong, ChromeOrigin);
            Assert.IsFalse(CheckHelloProof(hello, Port));

            AssertProblem(SendFinish(hello), HttpStatusCode.Forbidden, "pairing_failed");
            Assert.IsFalse(File.Exists(PairingFile.Path), "the code is burned");
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            Assert.IsFalse(File.Exists(Clients.Path));
        }

        [TestMethod]
        public void NoCode_Is409() {
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            AssertProblem(PostPairing(new { step = "finish", pairingId = ApiPairing.Base64Url(new byte[16]), clientNonce = ApiPairing.NewNonce(), serverNonce = ApiPairing.NewNonce(), proof = ApiPairing.NewNonce() }, ChromeOrigin),
                HttpStatusCode.Conflict, "pairing_unavailable");
        }

        [TestMethod]
        public void ExpiredCode_HelloIs409AndTheFileIsDeleted() {
            ApiPairingCode code = NewCode();
            Assert.AreEqual(Now.ToUnixTimeSeconds() + 300, code.Expires.ToUnixTimeSeconds());
            Now = code.Expires;
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        [TestMethod]
        public void ExpiredCode_FinishBurnsTheCode() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            Assert.IsTrue(CheckHelloProof(hello, Port));
            Now = code.Expires.AddSeconds(1);
            AssertProblem(SendFinish(hello), HttpStatusCode.Forbidden, "pairing_failed");
            Assert.IsFalse(File.Exists(PairingFile.Path));
            Assert.IsFalse(File.Exists(Clients.Path));
        }

        // A code used once cannot be used again: the same finish again, or a new hello, is 409
        [TestMethod]
        public void UsedCode_CannotBeUsedAgain() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            HttpResponseMessage first = SendFinish(hello);
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            string clients = File.ReadAllText(Clients.Path);

            AssertProblem(SendFinish(hello), HttpStatusCode.Conflict, "pairing_unavailable");
            AssertProblem(SendFinish(hello, FirefoxOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            Assert.AreEqual(clients, File.ReadAllText(Clients.Path), "no second credential");
        }

        [TestMethod]
        public void Hellos_TheFourthBurnsTheCode() {
            ApiPairingCode code = NewCode();
            for (int i = 0; i < ApiPolicy.MaxHellosPerCode; i++) {
                Assert.AreEqual(HttpStatusCode.OK, PostHello(i % 2 == 0 ? ChromeOrigin : FirefoxOrigin).StatusCode, "hello " + i);
            }
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Forbidden, "pairing_failed");
            Assert.IsFalse(File.Exists(PairingFile.Path));
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
        }

        // A burned code stays burned when its file cannot be deleted: the state in memory ends it
        [TestMethod]
        // An open file blocks the delete on Windows only
        [OSCondition(OperatingSystems.Windows)]
        public void BurnedCode_StaysBurnedWhenTheFileCannotBeDeleted() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            using (new FileStream(PairingFile.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                AssertProblem(SendFinish(hello, proof: ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "pairing_failed");
                Assert.IsTrue(File.Exists(PairingFile.Path), "the open file is not deleted on Windows");
                StringAssert.Contains(PairingFileTests.ReadCoreLog(), "Local API: api-pairing.txt could not be deleted.");
                AssertProblem(SendFinish(hello), HttpStatusCode.Conflict, "pairing_unavailable");
                AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            }
        }

        // A used code stays used when its file can be neither marked nor deleted: the state in memory ends it, so the
        // same finish cannot make a second token
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void UsedCode_StaysUsedWhenTheFileCannotBeMarked() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            using (new FileStream(PairingFile.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                Assert.AreEqual(HttpStatusCode.OK, SendFinish(hello).StatusCode);
                Assert.IsNull(PairingFile.Read(out _).PairedFamily, "the file could not be marked");
                string clients = File.ReadAllText(Clients.Path);
                AssertProblem(SendFinish(hello), HttpStatusCode.Conflict, "pairing_unavailable");
                Assert.AreEqual(clients, File.ReadAllText(Clients.Path), "no second credential");
            }
            StringAssert.Contains(PairingFileTests.ReadCoreLog(), "Local API: api-pairing.txt could not be marked as paired:");
        }

        // A creator that writes a new code while a finish fails: the burn re-reads the file and leaves the new code
        [TestMethod]
        public void Burn_LeavesANewerCodeAlone() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            ApiPairingCode newer = null;
            ApiPairingFile.ActingForTesting = () => {
                ApiPairingFile.ActingForTesting = null;
                newer = NewCode();
            };
            AssertProblem(SendFinish(hello, proof: ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "pairing_failed");
            Assert.IsNotNull(newer);
            Assert.AreEqual(newer.Id, PairingFile.Read(out _).Id, "the newer code is still there");
            TestHello next = SendHello(newer.Code, ChromeOrigin);
            Assert.IsTrue(CheckHelloProof(next, Port));
            Assert.AreEqual(HttpStatusCode.OK, SendFinish(next).StatusCode);
        }

        // A creator that writes a new code while a finish completes: the new code is not marked as paired
        [TestMethod]
        public void Complete_LeavesANewerCodeUnmarked() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            ApiPairingCode newer = null;
            ApiPairingFile.ActingForTesting = () => {
                ApiPairingFile.ActingForTesting = null;
                newer = NewCode();
            };
            Assert.AreEqual(HttpStatusCode.OK, SendFinish(hello).StatusCode);
            ApiPendingPairing pending = PairingFile.Read(out _);
            Assert.AreEqual(newer.Id, pending.Id);
            Assert.IsNull(pending.PairedFamily, "the newer code is still pending");
            Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode);
            StringAssert.Contains(PairingFileTests.ReadCoreLog(), "Local API: api-pairing.txt no longer holds the code, so it was not marked as paired.");
        }

        // A hello that read an older code X before the lock arrives after a newer code Y took over: under the lock the
        // file is read again, so the hello is answered for Y and Y's state (answered hellos, hello count) survives
        [TestMethod]
        public void Hello_StaleReadOfAnOlderCodeKeepsTheNewerState() {
            ApiPairingCode older = NewCode();
            Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode, "X has a state");
            ManualResetEventSlim staleRead = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            PairingEndpoints.HelloReadForTesting = () => {
                PairingEndpoints.HelloReadForTesting = null;
                staleRead.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            };
            Task<HttpResponseMessage> stale = Task.Run(() => PostHello(ChromeOrigin));
            Assert.IsTrue(staleRead.Wait(TimeSpan.FromSeconds(10)), "the stale hello read X");
            ApiPairingCode newer = NewCode();
            TestHello first = SendHello(newer.Code, ChromeOrigin);
            release.Set();
            HttpResponseMessage staleResponse = stale.GetAwaiter().GetResult();
            Assert.AreEqual(HttpStatusCode.OK, staleResponse.StatusCode, Body(staleResponse));
            Assert.AreEqual(newer.Id, Json(staleResponse).GetProperty("pairingId").GetString(), "X gets no answer");
            Assert.AreNotEqual(older.Id, newer.Id);
            Assert.AreEqual(HttpStatusCode.OK, PostHello(FirefoxOrigin).StatusCode, "the third hello of Y");
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Forbidden, "pairing_failed");
            AssertProblem(SendFinish(first), HttpStatusCode.Conflict, "pairing_unavailable");
        }

        // The same, ending with a finish: Y's first answered hello still finishes
        [TestMethod]
        public void Hello_StaleReadKeepsTheNewerCodesAnsweredHellos() {
            NewCode();
            ManualResetEventSlim staleRead = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            PairingEndpoints.HelloReadForTesting = () => {
                PairingEndpoints.HelloReadForTesting = null;
                staleRead.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            };
            Task<HttpResponseMessage> stale = Task.Run(() => PostHello(FirefoxOrigin));
            Assert.IsTrue(staleRead.Wait(TimeSpan.FromSeconds(10)));
            ApiPairingCode newer = NewCode();
            TestHello first = SendHello(newer.Code, ChromeOrigin);
            release.Set();
            Assert.AreEqual(newer.Id, Json(stale.GetAwaiter().GetResult()).GetProperty("pairingId").GetString());
            Assert.AreEqual(HttpStatusCode.OK, SendFinish(first).StatusCode);
        }

        // The creator ends the code (deletes the file) after a finish read it and before the finish took the lock: no
        // token is made, the code ends here, and the next finish finds no code
        [TestMethod]
        public void Finish_AfterTheCreatorEndedTheCodeMakesNoToken() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            string logBefore = PairingFileTests.ReadCoreLog();
            PairingEndpoints.FinishReadForTesting = () => {
                PairingEndpoints.FinishReadForTesting = null;
                File.Delete(PairingFile.Path);
            };
            AssertProblem(SendFinish(hello), HttpStatusCode.Conflict, "pairing_unavailable");
            Assert.IsFalse(File.Exists(Clients.Path), "no credential was made");
            Assert.IsFalse(File.Exists(PairingFile.Path), "the file is not made again");
            StringAssert.Contains(PairingFileTests.ReadCoreLog().Substring(logBefore.Length), "Local API: a pairing code was burned (ended or replaced by its creator)");
        }

        // The creator replaces the code with Y after a finish of X read the file: no token for X, X ends, and Y is left
        // to pair
        [TestMethod]
        public void Finish_AfterTheCreatorReplacedTheCodeMakesNoToken() {
            ApiPairingCode older = NewCode();
            TestHello hello = SendHello(older.Code, ChromeOrigin);
            ApiPairingCode newer = null;
            PairingEndpoints.FinishReadForTesting = () => {
                PairingEndpoints.FinishReadForTesting = null;
                newer = NewCode();
            };
            AssertProblem(SendFinish(hello), HttpStatusCode.Conflict, "pairing_unavailable");
            Assert.IsFalse(File.Exists(Clients.Path), "no credential was made");
            Assert.AreEqual(newer.Id, PairingFile.Read(out _).Id, "Y is still there");
            AssertProblem(SendFinish(hello), HttpStatusCode.Conflict, "pairing_unavailable");
            TestHello next = SendHello(newer.Code, FirefoxOrigin);
            Assert.AreEqual(HttpStatusCode.OK, SendFinish(next).StatusCode);
            Assert.AreEqual("firefox", Clients.Read().Single().Family);
        }

        // The file cannot be read when a finish checks that it still holds the code: no token is made, the code ends
        // (the file is deleted anyway), and the log says the file could not be read
        [TestMethod]
        public void Finish_WhenTheFileCannotBeReadAgainMakesNoToken() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            string pairingPath = PairingFile.Path;
            bool failing = false;
            ApiTokenStore.OpeningForTesting = path => {
                if (failing && path == pairingPath) throw new IOException("test: access denied", unchecked((int)0x80070005));
            };
            PairingEndpoints.FinishReadForTesting = () => failing = true;
            string logBefore = PairingFileTests.ReadCoreLog();
            AssertProblem(SendFinish(hello), HttpStatusCode.Conflict, "pairing_unavailable");
            ApiTokenStore.OpeningForTesting = null;
            Assert.IsFalse(File.Exists(Clients.Path), "no credential was made");
            Assert.IsFalse(File.Exists(PairingFile.Path), "the code is ended");
            StringAssert.Contains(PairingFileTests.ReadCoreLog().Substring(logBefore.Length), "Local API: a pairing code was burned (api-pairing.txt could not be read)");
            AssertProblem(SendFinish(hello), HttpStatusCode.Conflict, "pairing_unavailable");
        }

        // A code used here stays ended after another code's state replaced it: its pending file, written back by an
        // outside writer, gives 409 to a hello and a finish, and the live code Y is untouched
        [TestMethod]
        public void EndedCode_UsedThenReplacedStaysEnded() {
            ApiPairingCode older = NewCode();
            string olderText = File.ReadAllText(PairingFile.Path);
            TestHello olderHello = SendHello(older.Code, ChromeOrigin);
            Assert.AreEqual(HttpStatusCode.OK, SendFinish(olderHello).StatusCode);
            string clients = File.ReadAllText(Clients.Path);
            ApiPairingCode newer = NewCode();
            string newerText = File.ReadAllText(PairingFile.Path);
            TestHello newerHello = SendHello(newer.Code, FirefoxOrigin);
            ApiTokenStore.WriteOwnerOnlyText(PairingFile.Path, olderText, "test: ");
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            AssertProblem(SendFinish(olderHello), HttpStatusCode.Conflict, "pairing_unavailable");
            Assert.AreEqual(clients, File.ReadAllText(Clients.Path), "no second credential for the older code");
            ApiTokenStore.WriteOwnerOnlyText(PairingFile.Path, newerText, "test: ");
            Assert.AreEqual(HttpStatusCode.OK, SendFinish(newerHello).StatusCode, "Y's state is untouched");
            Assert.AreEqual(2, Clients.Read().Count);
        }

        // The ended ids are bounded to the last 8: of 9 codes ended in turn, the newest 8 stay ended when their files
        // come back, and the oldest is no longer known. Only an outside writer can bring such a file back (accepted:
        // the code is then live again until its own expiry).
        [TestMethod]
        public void EndedIds_KeepTheLastEight() {
            List<string> texts = new List<string>();
            for (int i = 0; i < 9; i++) {
                ApiPairingCode code = NewCode();
                texts.Add(File.ReadAllText(PairingFile.Path));
                TestHello hello = SendHello(code.Code, ChromeOrigin);
                AssertProblem(SendFinish(hello, proof: ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "pairing_failed");
            }
            foreach (int ended in new[] { 8, 1, 4 }) {
                ApiTokenStore.WriteOwnerOnlyText(PairingFile.Path, texts[ended], "test: ");
                AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            }
            ApiTokenStore.WriteOwnerOnlyText(PairingFile.Path, texts[0], "test: ");
            Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode, "the oldest left the queue");
        }

        // An ended code's file that comes back while code Y is live does not replace Y's state: X gets 409, and Y keeps
        // its answered hellos
        [TestMethod]
        public void EndedCode_ComingBackKeepsTheLiveStatesHellos() {
            string olderText = BurnACodeAndStartAnother(out ApiPairingCode newer);
            string newerText = File.ReadAllText(PairingFile.Path);
            TestHello first = SendHello(newer.Code, ChromeOrigin);
            ApiTokenStore.WriteOwnerOnlyText(PairingFile.Path, olderText, "test: ");
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            ApiTokenStore.WriteOwnerOnlyText(PairingFile.Path, newerText, "test: ");
            Assert.AreEqual(HttpStatusCode.OK, SendFinish(first).StatusCode, "Y's answered hello still finishes");
        }

        // The same, for the hello count: Y's hellos before and after go on counting, so its fourth burns
        [TestMethod]
        public void EndedCode_ComingBackKeepsTheLiveStatesHelloCount() {
            string olderText = BurnACodeAndStartAnother(out ApiPairingCode newer);
            string newerText = File.ReadAllText(PairingFile.Path);
            Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode);
            ApiTokenStore.WriteOwnerOnlyText(PairingFile.Path, olderText, "test: ");
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            ApiTokenStore.WriteOwnerOnlyText(PairingFile.Path, newerText, "test: ");
            Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode, "Y's third hello");
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Forbidden, "pairing_failed");
        }

        // Code X burned by a wrong proof, then code Y made; returns X's file text
        private string BurnACodeAndStartAnother(out ApiPairingCode newer) {
            ApiPairingCode older = NewCode();
            string olderText = File.ReadAllText(PairingFile.Path);
            TestHello hello = SendHello(older.Code, ChromeOrigin);
            AssertProblem(SendFinish(hello, proof: ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "pairing_failed");
            newer = NewCode();
            return olderText;
        }

        // A file that cannot be read at all is no code, and the log says it could not be read (not that it is untrusted)
        [TestMethod]
        public void PairingFile_UnreadableIsNoCodeAndLoggedAsUnreadable() {
            NewCode();
            string pairingPath = PairingFile.Path;
            string logBefore = PairingFileTests.ReadCoreLog();
            ApiTokenStore.OpeningForTesting = path => {
                if (path == pairingPath) throw new IOException("test: access denied", unchecked((int)0x80070005));
            };
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            ApiTokenStore.OpeningForTesting = null;
            string logged = PairingFileTests.ReadCoreLog().Substring(logBefore.Length);
            StringAssert.Contains(logged, "Local API: api-pairing.txt could not be read, so no pairing code is active.");
            Assert.IsFalse(logged.Contains("is not trusted", StringComparison.Ordinal));
            Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode);
        }

        // A code that ended here never gets a new state, even if its file comes back
        [TestMethod]
        public void EndedCode_NeverComesBackInThisProcess() {
            ApiPairingCode older = NewCode();
            string olderText = File.ReadAllText(PairingFile.Path);
            TestHello hello = SendHello(older.Code, ChromeOrigin);
            AssertProblem(SendFinish(hello, proof: ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "pairing_failed");
            NewCode();
            Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode);
            ApiTokenStore.WriteOwnerOnlyText(PairingFile.Path, olderText, "test: ");
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            AssertProblem(SendFinish(hello), HttpStatusCode.Conflict, "pairing_unavailable");
            Assert.IsFalse(File.Exists(Clients.Path));
        }

        // A burn whose second read of the file fails deletes the file anyway (the code must end)
        [TestMethod]
        public void Burn_DeletesTheFileWhenItCannotBeReadAgain() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            string logBefore = PairingFileTests.ReadCoreLog();
            FailPairingFileReadsWhenActing();
            AssertProblem(SendFinish(hello, proof: ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "pairing_failed");
            ApiTokenStore.OpeningForTesting = null;
            Assert.IsFalse(File.Exists(PairingFile.Path));
            Assert.IsFalse(PairingFileTests.ReadCoreLog().Substring(logBefore.Length).Contains("could not be deleted", StringComparison.Ordinal));
        }

        // The same with a file that also cannot be deleted: Delete reports it, and the log says so
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void Burn_LogsAFileThatCanBeNeitherReadAgainNorDeleted() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            FailPairingFileReadsWhenActing();
            using (new FileStream(PairingFile.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                AssertProblem(SendFinish(hello, proof: ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "pairing_failed");
                Assert.IsTrue(File.Exists(PairingFile.Path));
                Assert.IsFalse(PairingFile.Delete(code.Id), "an unreadable file that is still there is not deleted");
            }
            StringAssert.Contains(PairingFileTests.ReadCoreLog(), "Local API: api-pairing.txt could not be deleted.");
            ApiTokenStore.OpeningForTesting = null;
            AssertProblem(SendFinish(hello), HttpStatusCode.Conflict, "pairing_unavailable");
        }

        // A finish whose second read of the file fails cannot mark it: the code ends as with a failed mark (the file is
        // deleted), and the browser still gets its token
        [TestMethod]
        public void Complete_EndsTheCodeWhenTheFileCannotBeReadAgain() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, FirefoxOrigin);
            FailPairingFileReadsWhenActing();
            HttpResponseMessage finish = SendFinish(hello);
            ApiTokenStore.OpeningForTesting = null;
            Assert.AreEqual(HttpStatusCode.OK, finish.StatusCode, Body(finish));
            Assert.IsFalse(File.Exists(PairingFile.Path), "the code is ended");
            StringAssert.Contains(PairingFileTests.ReadCoreLog(), "Local API: api-pairing.txt could not be marked as paired: JDP.Api.ApiTokenException");
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(Json(finish).GetProperty("token").GetString(), FirefoxOrigin).StatusCode);
        }

        // A creator that cancels (deletes the file) while a finish completes: the file is not made again
        [TestMethod]
        public void Complete_NeverRecreatesARemovedFile() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, FirefoxOrigin);
            ApiPairingFile.ActingForTesting = () => {
                ApiPairingFile.ActingForTesting = null;
                File.Delete(PairingFile.Path);
            };
            Assert.AreEqual(HttpStatusCode.OK, SendFinish(hello).StatusCode);
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        // A used code's file stays for the creator until the code's expiry; a pairing request after that removes it
        [TestMethod]
        public void UsedCode_FileIsRemovedWhenMetAfterItsExpiry() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            Assert.AreEqual(HttpStatusCode.OK, SendFinish(hello).StatusCode);
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            Assert.IsTrue(File.Exists(PairingFile.Path), "the creator still has to see the result");
            Now = code.Expires;
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        // A clients file that cannot be read is never replaced: the finish fails with 500, the code ends, and the other
        // family's line is kept
        [TestMethod]
        public void ClientsFile_ReadFailureEndsTheCodeAndKeepsTheOtherFamily() {
            string firefox = Pair(FirefoxOrigin);
            string before = File.ReadAllText(Clients.Path);
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            string clientsPath = Clients.Path;
            ApiTokenStore.OpeningForTesting = path => {
                if (path == clientsPath) throw new IOException("test: access denied", unchecked((int)0x80070005));
            };
            AssertProblem(SendFinish(hello), HttpStatusCode.InternalServerError, "internal_error");
            ApiTokenStore.OpeningForTesting = null;
            Assert.AreEqual(before, File.ReadAllText(Clients.Path));
            Assert.IsFalse(File.Exists(PairingFile.Path), "the code is ended");
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(firefox, FirefoxOrigin).StatusCode);
            StringAssert.Contains(PairingFileTests.ReadCoreLog(), "Local API: a pairing code was burned (api-clients.txt could not be saved: JDP.Api.ApiTokenException)");
        }

        // The finish needs the nonces of a hello this code answered: a server nonce the server never gave, or the
        // nonces of a hello from another Origin, burn the code even with a proof made with the right key
        [TestMethod]
        public void Finish_UnknownNoncesBurnTheCode() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            TestHello forged = new TestHello { Origin = hello.Origin, ClientNonce = hello.ClientNonce, PairingId = hello.PairingId, ServerNonce = ApiPairing.NewNonce(), Key = hello.Key };
            AssertProblem(SendFinish(forged), HttpStatusCode.Forbidden, "pairing_failed");
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        [TestMethod]
        public void Finish_NoncesOfAHelloFromAnotherOriginBurnTheCode() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            AssertProblem(SendFinish(hello, FirefoxOrigin), HttpStatusCode.Forbidden, "pairing_failed");
            Assert.IsFalse(File.Exists(PairingFile.Path));
            Assert.IsFalse(File.Exists(Clients.Path));
        }

        // P2 binds the Origin and the port: a proof made for another Origin, or for another port (a relay), fails
        [TestMethod]
        public void Finish_ProofForAnotherOriginBurnsTheCode() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            AssertProblem(SendFinish(hello, proof: FinishProof(hello, Port, FirefoxOrigin)), HttpStatusCode.Forbidden, "pairing_failed");
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        [TestMethod]
        public void Finish_ProofForAnotherPortBurnsTheCode() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, FirefoxOrigin);
            AssertProblem(SendFinish(hello, proof: FinishProof(hello, OtherPort(Port), FirefoxOrigin)), HttpStatusCode.Forbidden, "pairing_failed");
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        // A finish of an older code (another pairingId) is 409 and leaves the current code alone
        [TestMethod]
        public void Finish_OfAnOlderCodeIs409AndDoesNotBurnTheNewOne() {
            ApiPairingCode oldCode = NewCode();
            TestHello oldHello = SendHello(oldCode.Code, ChromeOrigin);
            ApiPairingCode newCode = NewCode();
            Assert.AreNotEqual(oldCode.Id, newCode.Id);
            AssertProblem(SendFinish(oldHello), HttpStatusCode.Conflict, "pairing_unavailable");
            Assert.IsTrue(File.Exists(PairingFile.Path));
            TestHello hello = SendHello(newCode.Code, ChromeOrigin);
            Assert.IsTrue(CheckHelloProof(hello, Port));
            Assert.AreEqual(HttpStatusCode.OK, SendFinish(hello).StatusCode);
        }

        // The answered hellos of a code all hold, so the extension's hello may follow another; P1 of one hello does
        // not check for another's nonces (no replay of P1)
        [TestMethod]
        public void Replay_P1OfOneHelloDoesNotCheckForAnother() {
            ApiPairingCode code = NewCode();
            TestHello first = SendHello(code.Code, ChromeOrigin);
            TestHello second = SendHello(code.Code, ChromeOrigin);
            Assert.IsTrue(CheckHelloProof(first, Port));
            Assert.IsTrue(CheckHelloProof(second, Port));
            TestHello replayed = new TestHello { Origin = second.Origin, ClientNonce = second.ClientNonce, PairingId = second.PairingId, ServerNonce = second.ServerNonce, ServerName = second.ServerName, Proof = first.Proof, Key = second.Key };
            Assert.IsFalse(CheckHelloProof(replayed, Port));
            Assert.IsFalse(CheckHelloProof(first, OtherPort(Port)), "P1 binds the port");
            Assert.AreEqual(HttpStatusCode.OK, SendFinish(first).StatusCode, "an earlier answered hello still finishes");
        }

        // P2 of one hello, sent with the nonces of another, fails and burns the code
        [TestMethod]
        public void Replay_P2WithTheNoncesOfAnotherHelloBurnsTheCode() {
            ApiPairingCode code = NewCode();
            TestHello first = SendHello(code.Code, ChromeOrigin);
            TestHello second = SendHello(code.Code, ChromeOrigin);
            AssertProblem(SendFinish(second, proof: FinishProof(first, Port, ChromeOrigin)), HttpStatusCode.Forbidden, "pairing_failed");
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        // P2 of a used code, replayed against a new code, finds no such pairing
        [TestMethod]
        public void Replay_P2OfAnOldCodeAgainstANewCodeIs409() {
            ApiPairingCode oldCode = NewCode();
            TestHello oldHello = SendHello(oldCode.Code, ChromeOrigin);
            Assert.AreEqual(HttpStatusCode.OK, SendFinish(oldHello).StatusCode);
            NewCode();
            AssertProblem(SendFinish(oldHello), HttpStatusCode.Conflict, "pairing_unavailable");
            Assert.IsTrue(File.Exists(PairingFile.Path), "the new code is not burned");
        }

        // P3 binds the token and the nonces: a P3 of one pairing does not check for another, so a program that took the
        // port cannot plant a token it knows
        [TestMethod]
        public void Replay_P3OfOnePairingDoesNotCheckForAnother() {
            ApiPairingCode code = NewCode();
            TestHello first = SendHello(code.Code, ChromeOrigin);
            HttpResponseMessage finish = SendFinish(first);
            JsonElement json = Json(finish);
            string token = json.GetProperty("token").GetString();
            string proof = json.GetProperty("proof").GetString();
            Assert.IsTrue(CheckFinishProof(first, Port, token, proof));
            Assert.IsFalse(CheckFinishProof(first, Port, Pair(FirefoxOrigin), proof), "another token");
            Assert.IsFalse(CheckFinishProof(first, OtherPort(Port), token, proof), "another port");
            TestHello other = new TestHello { Origin = first.Origin, ClientNonce = ApiPairing.NewNonce(), PairingId = first.PairingId, ServerNonce = first.ServerNonce, Key = first.Key };
            Assert.IsFalse(CheckFinishProof(other, Port, token, proof), "another client nonce");
        }

        // Of two finishes at once with valid proofs, exactly one gets a token; the other finds the code used. The first
        // to complete waits there until the second has arrived and then a second more, or until the second also starts
        // to complete; without the lock the second would get there and both would complete.
        [TestMethod]
        public void Finish_ConcurrentFinishesGiveExactlyOneToken() {
            ApiPairingCode code = NewCode();
            TestHello[] hellos = { SendHello(code.Code, ChromeOrigin), SendHello(code.Code, FirefoxOrigin) };
            int arrived = 0;
            int completing = 0;
            ManualResetEventSlim bothArrived = new ManualResetEventSlim(false);
            ManualResetEventSlim secondCompleting = new ManualResetEventSlim(false);
            PairingEndpoints.FinishArrivingForTesting = () => {
                if (Interlocked.Increment(ref arrived) == 2) bothArrived.Set();
            };
            PairingEndpoints.CompletingForTesting = () => {
                if (Interlocked.Increment(ref completing) != 1) {
                    secondCompleting.Set();
                    return;
                }
                bothArrived.Wait(TimeSpan.FromSeconds(10));
                secondCompleting.Wait(TimeSpan.FromSeconds(1));
            };
            Task<HttpResponseMessage>[] finishes = hellos.Select(hello => Task.Run(() => SendFinish(hello))).ToArray();
            Task.WaitAll(finishes);
            Assert.IsTrue(bothArrived.IsSet, "the second finish arrived while the first completed");
            HttpResponseMessage[] responses = finishes.Select(finish => finish.Result).ToArray();
            Assert.AreEqual(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK), String.Join(", ", responses.Select(response => response.StatusCode)));
            foreach (HttpResponseMessage response in responses.Where(response => response.StatusCode != HttpStatusCode.OK)) {
                AssertProblem(response, HttpStatusCode.Conflict, "pairing_unavailable");
            }
            Assert.AreEqual(1, Clients.Read().Count);
        }

        // Two codes in a row, two browsers: each pairing gets its own token and its own line
        [TestMethod]
        public void Pair_BothFamiliesHaveTheirOwnSlots() {
            string chrome = Pair(ChromeOrigin);
            string firefox = Pair(FirefoxOrigin);
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(chrome, ChromeOrigin).StatusCode);
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(firefox, FirefoxOrigin, "https://boards.4chan.org/wg/thread/101").StatusCode);
            CollectionAssert.AreEqual(new[] { "chrome", "firefox" }, Clients.Read().Select(client => client.Family).ToArray());
        }

        // A new pairing of a family replaces its line: the old token stops working at once; the other family keeps its
        [TestMethod]
        public void Repair_ReplacesTheFamilySlotOnly() {
            string oldChrome = Pair(ChromeOrigin);
            string firefox = Pair(FirefoxOrigin);
            string newChrome = Pair(ChromeOrigin);
            Assert.AreNotEqual(oldChrome, newChrome);
            AssertProblem(AddWithToken(oldChrome, ChromeOrigin), HttpStatusCode.Unauthorized, "unauthorized");
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(newChrome, ChromeOrigin).StatusCode);
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(firefox, FirefoxOrigin, "https://boards.4chan.org/wg/thread/101").StatusCode);
            Assert.AreEqual(2, Clients.Read().Count);
        }

        // A Firefox reinstall has a new Origin; its pairing replaces the old Firefox line and the old Origin's token
        [TestMethod]
        public void Repair_FirefoxWithANewOriginReplacesTheOldOne() {
            string oldToken = Pair(FirefoxOrigin);
            string newToken = Pair(OtherFirefoxOrigin);
            AssertProblem(AddWithToken(oldToken, FirefoxOrigin), HttpStatusCode.Unauthorized, "unauthorized");
            AssertProblem(AddWithToken(newToken, FirefoxOrigin), HttpStatusCode.Forbidden, "forbidden_origin");
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(newToken, OtherFirefoxOrigin).StatusCode);
            Assert.AreEqual(OtherFirefoxOrigin, Clients.Read().Single().Origin);
        }

        [TestMethod]
        public void Unpair_RemovesTheFamilyAndItsTokenStopsAtOnce() {
            string chrome = Pair(ChromeOrigin);
            string firefox = Pair(FirefoxOrigin);
            Assert.IsTrue(Clients.Remove("chrome"));
            AssertProblem(AddWithToken(chrome, ChromeOrigin), HttpStatusCode.Unauthorized, "unauthorized");
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(firefox, FirefoxOrigin).StatusCode);
            Assert.IsFalse(Clients.Remove("chrome"));
            Assert.IsTrue(Clients.Remove("firefox"));
            Assert.AreEqual("1\n", File.ReadAllText(Clients.Path));
            AssertProblem(AddWithToken(firefox, FirefoxOrigin), HttpStatusCode.Unauthorized, "unauthorized");
            AssertOwnerOnly(Clients.Path);
        }

        // Proof before use: the extension's with its Origin, the scripts' without one; each checks only for its own
        // token, port, Origin and nonce
        [TestMethod]
        public void Proof_ExtensionAndScriptProofsCheck() {
            string token = Pair(ChromeOrigin);
            string nonce = ApiPairing.NewNonce();
            HttpResponseMessage response = PostProof(ChromeOrigin, nonce);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, Body(response));
            string proof = Json(response).GetProperty("proof").GetString();
            Assert.IsTrue(CheckUseProof(token, Port, ChromeOrigin, nonce, proof));
            Assert.IsFalse(CheckUseProof(token, OtherPort(Port), ChromeOrigin, nonce, proof), "a proof for another port fails in the client");
            Assert.IsFalse(CheckUseProof(token, Port, ChromeOrigin, ApiPairing.NewNonce(), proof), "a proof for another nonce fails (no replay)");
            Assert.IsFalse(CheckUseProof(Token, Port, ChromeOrigin, nonce, proof), "the scripts' token's key does not make it");

            string scriptNonce = ApiPairing.NewNonce();
            HttpResponseMessage script = PostProof(null, scriptNonce);
            Assert.AreEqual(HttpStatusCode.OK, script.StatusCode, Body(script));
            string scriptProof = Json(script).GetProperty("proof").GetString();
            Assert.IsTrue(CheckUseProof(Token, Port, "", scriptNonce, scriptProof));
            Assert.IsFalse(CheckUseProof(token, Port, ChromeOrigin, scriptNonce, scriptProof), "a proof without the Origin is not the extension's (no downgrade)");
            AssertCommonHeaders(script);
        }

        // An Origin selects its family's line only if the recorded Origin is the same
        [TestMethod]
        public void Proof_WithoutAMatchingPairingIsNotPaired() {
            AssertProblem(PostProof(ChromeOrigin, ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "not_paired");
            Pair(FirefoxOrigin);
            AssertProblem(PostProof(ChromeOrigin, ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "not_paired");
            AssertProblem(PostProof(OtherFirefoxOrigin, ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "not_paired");
            File.Delete(Tokens.Path);
            AssertProblem(PostProof(null, ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "not_paired");
        }

        [TestMethod]
        public void Proof_OriginRules() {
            Pair(ChromeOrigin);
            foreach (string origin in new[] { "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture), OtherChromeOrigin, "https://example.com", "null" }) {
                AssertProblem(PostProof(origin, ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "forbidden_origin");
            }
        }
    }
}
