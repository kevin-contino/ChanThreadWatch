using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Api.Tests {
    // api-clients.txt and api-pairing.txt: owner-only as api-token.txt, re-read on each request, and failing closed
    [TestClass]
    public class PairingFileTests : PairingTestBase {
        private const string Version = "1\n";

        private static string ClientLine(string family, string origin, string token) {
            return family + " " + origin + " sha256:" + Hex(TokenHash(token)) + " 2026-10-06T21:30:00Z\n";
        }

        // A file that is owner-only but does not parse lets no paired browser in; the scripts' token still works, and
        // the log names the file once
        [TestMethod]
        public void ClientsFile_MalformedFailsClosed() {
            string chrome = Pair(ChromeOrigin);
            string firefox = Pair(FirefoxOrigin);
            string good = File.ReadAllText(Clients.Path);
            string logBefore = ReadCoreLog();
            string chromeLine = good.Split('\n')[1] + "\n";
            string firefoxLine = good.Split('\n')[2] + "\n";
            string[] contents = {
                "", "1", "2\n" + chromeLine, "1\r\n" + chromeLine, Version + chromeLine + chromeLine, Version + chromeLine.TrimEnd('\n'), Version + chromeLine + "\n",
                Version + chromeLine.Replace("chrome ", "firefox "), Version + firefoxLine.Replace("firefox ", "chrome "),
                Version + chromeLine.Replace(ApiPairing.ChromeExtensionId, "abcdefghijklmnopabcdefghijklmnop"), Version + chromeLine.Replace(" sha256:", " sha256:A"),
                Version + chromeLine.Replace("Z\n", "\n"), Version + chromeLine.Replace("Z\n", "Z \n"), Version + chromeLine.Replace(" ", "  "), Version + chromeLine.Replace("\n", "\r\n"),
                Version + chromeLine + firefoxLine + "edge chrome-extension://x sha256:" + new string('a', 64) + " 2026-10-06T21:30:00Z\n",
                Version + chromeLine + firefoxLine + new string('#', 1100) + "\n", " " + Version + chromeLine, Version + "Chrome" + chromeLine.Substring(6)
            };
            foreach (string content in contents) {
                ApiTokenStore.WriteOwnerOnlyText(Clients.Path, content, "test: ");
                Assert.IsNull(Clients.Read(), content);
                AssertProblem(AddWithToken(chrome, ChromeOrigin), HttpStatusCode.Unauthorized, "unauthorized");
                AssertProblem(AddWithToken(firefox, FirefoxOrigin), HttpStatusCode.Unauthorized, "unauthorized");
                AssertProblem(PostProof(ChromeOrigin, ApiPairing.NewNonce()), HttpStatusCode.Forbidden, "not_paired");
                Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode, "scripts' token: " + content);
            }
            string logged = ReadCoreLog().Substring(logBefore.Length);
            Assert.AreEqual(1, logged.Split("Local API: api-clients.txt is not trusted, so no paired browser can connect.").Length - 1, "logged once");

            ApiTokenStore.WriteOwnerOnlyText(Clients.Path, good, "test: ");
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(chrome, ChromeOrigin).StatusCode, "trusted again");
        }

        // Pairing over an untrusted file starts a new file with the new line alone, so a damaged file never blocks
        // pairing; the other family pairs again
        [TestMethod]
        public void ClientsFile_PairingReplacesARefusedFile() {
            string firefox = Pair(FirefoxOrigin);
            ApiTokenStore.WriteOwnerOnlyText(Clients.Path, Version + "garbage\n", "test: ");
            string logBefore = ReadCoreLog();
            string chrome = Pair(ChromeOrigin);
            Assert.AreEqual(1, ReadCoreLog().Substring(logBefore.Length).Split("Local API: an untrusted api-clients.txt was replaced by a new pairing.").Length - 1);
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(chrome, ChromeOrigin).StatusCode);
            AssertProblem(AddWithToken(firefox, FirefoxOrigin), HttpStatusCode.Unauthorized, "unauthorized");
            Assert.AreEqual("chrome", Clients.Read().Single().Family);
            PairingTests.AssertOwnerOnly(Clients.Path);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [SupportedOSPlatform("windows")]
        public void ClientsFile_WindowsInheritedAclFailsClosed() {
            string chrome = Pair(ChromeOrigin);
            string text = File.ReadAllText(Clients.Path);
            File.Delete(Clients.Path);
            File.WriteAllText(Clients.Path, text);
            Assert.IsFalse(new FileInfo(Clients.Path).GetAccessControl().AreAccessRulesProtected);
            Assert.IsNull(Clients.Read());
            AssertProblem(AddWithToken(chrome, ChromeOrigin), HttpStatusCode.Unauthorized, "unauthorized");
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [UnsupportedOSPlatform("windows")]
        public void ClientsFile_UnixReadableByOthersFailsClosed() {
            string chrome = Pair(ChromeOrigin);
            File.SetUnixFileMode(Clients.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Assert.IsNull(Clients.Read());
            AssertProblem(AddWithToken(chrome, ChromeOrigin), HttpStatusCode.Unauthorized, "unauthorized");
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [UnsupportedOSPlatform("windows")]
        public void ClientsFile_UnixSymlinkFailsClosed() {
            string chrome = Pair(ChromeOrigin);
            string otherFolder = Directory.CreateDirectory(Path.Combine(Folder, "other")).FullName;
            File.Move(Clients.Path, Path.Combine(otherFolder, ApiClientStore.FileName));
            File.CreateSymbolicLink(Clients.Path, Path.Combine(otherFolder, ApiClientStore.FileName));
            Assert.IsNotNull(new ApiClientStore(otherFolder).Read());
            Assert.IsNull(Clients.Read());
            AssertProblem(AddWithToken(chrome, ChromeOrigin), HttpStatusCode.Unauthorized, "unauthorized");
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [SupportedOSPlatform("windows")]
        public void Files_WindowsHaveOneProtectedRuleForTheCurrentUser() {
            NewCode();
            AssertOwnerOnlyAcl(PairingFile.Path);
            Pair(ChromeOrigin);
            AssertOwnerOnlyAcl(Clients.Path);
            AssertOwnerOnlyAcl(PairingFile.Path);
        }

        [SupportedOSPlatform("windows")]
        private static void AssertOwnerOnlyAcl(string path) {
            FileSecurity security = new FileInfo(path).GetAccessControl();
            Assert.IsTrue(security.AreAccessRulesProtected, "inherited rules");
            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            Assert.AreEqual(1, rules.Count);
            FileSystemAccessRule rule = (FileSystemAccessRule)rules[0];
            Assert.AreEqual(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
            Assert.AreEqual(AccessControlType.Allow, rule.AccessControlType);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [UnsupportedOSPlatform("windows")]
        public void Files_UnixAreMode0600() {
            NewCode();
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PairingFile.Path));
            Pair(ChromeOrigin);
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Clients.Path));
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PairingFile.Path));
        }

        // The creator writes the salt and the stretched key, never the code; a new code replaces the older one
        [TestMethod]
        public void PairingFile_CreateWritesTheKeyNeverTheCode() {
            ApiPairingCode code = NewCode();
            StringAssert.Matches(code.Code, new Regex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$"));
            string text = File.ReadAllText(PairingFile.Path);
            Assert.IsFalse(text.Contains(code.Code.Replace("-", ""), StringComparison.Ordinal));
            Assert.IsFalse(text.Contains(code.Code, StringComparison.Ordinal));
            bool refused;
            ApiPendingPairing pending = PairingFile.Read(out refused);
            Assert.IsFalse(refused);
            Assert.AreEqual(code.Id, pending.Id);
            Assert.IsNull(pending.PairedFamily);
            byte[] salt = ApiPairing.FromBase64Url(pending.Salt, 16);
            CollectionAssert.AreEqual(Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(code.Code.Replace("-", "")), salt, 600000, HashAlgorithmName.SHA256, 32), pending.Key);
            Assert.AreEqual(Now.ToUnixTimeSeconds() + 300, pending.Expires.ToUnixTimeSeconds());
            Assert.AreEqual("1\nid:" + pending.Id + "\nsalt:" + pending.Salt + "\nkey:" + Hex(pending.Key) + "\nexpires:" + pending.Expires.ToUnixTimeSeconds() + "\nstate:pending\n", text);
            PairingTests.AssertOwnerOnly(PairingFile.Path);

            ApiPairingCode next = NewCode();
            Assert.AreNotEqual(code.Id, next.Id);
            Assert.AreEqual(next.Id, PairingFile.Read(out refused).Id);
            Assert.AreEqual(0, Directory.GetFiles(Folder, "*.tmp").Length);
            Assert.IsTrue(PairingFile.Delete(code.Id), "an older code is not in the file");
            Assert.IsTrue(File.Exists(PairingFile.Path), "the newer code is not deleted");
            Assert.IsTrue(PairingFile.Delete(next.Id));
            Assert.IsFalse(File.Exists(PairingFile.Path));
            Assert.IsTrue(PairingFile.Delete(next.Id), "a missing file is deleted");
        }

        [TestMethod]
        public void PairingFile_ParseAcceptsOnlyTheSixLineForm() {
            ApiPairingCode code = NewCode();
            string good = File.ReadAllText(PairingFile.Path);
            Assert.IsNotNull(ApiPairingFile.Parse(good));
            Assert.AreEqual("chrome", ApiPairingFile.Parse(good.Replace("state:pending", "state:paired:chrome")).PairedFamily);
            string[] bad = {
                "", good.TrimEnd('\n'), good + "\n", good.Replace("\n", "\r\n"), "2" + good.Substring(1), good.Replace("state:pending", "state:paired:edge"),
                good.Replace("state:pending", "state:Pending"), good.Replace("state:pending", "state:paired:"), good.Replace("id:", "ID:"), good.Replace("id:" + code.Id, "id:" + code.Id + "A"),
                good.Replace("salt:", "salt:="), good.Replace("key:", "key:A"), good.Replace("key:", "key:0"), good.Replace("expires:", "expires:-"), good.Replace("expires:", "expires: "),
                good.Replace("expires:", "expires:9999999999999"), good.Replace("expires:" + code.Expires.ToUnixTimeSeconds(), "expires:999999999999"), good.Replace("expires:", "expires:+"), good.Replace("\nstate:pending", "")
            };
            foreach (string content in bad) {
                Assert.IsNull(ApiPairingFile.Parse(content), content);
            }
            Assert.AreEqual(99999999999, ApiPairingFile.Parse(good.Replace("expires:" + code.Expires.ToUnixTimeSeconds(), "expires:99999999999")).Expires.ToUnixTimeSeconds());
        }

        // A pairing file that is not trusted (malformed, a link, open to others, or expiring too far ahead) is no code:
        // 409, and the log says so
        [TestMethod]
        public void PairingFile_RefusedIsNoCode() {
            ApiPairingCode code = NewCode();
            string good = File.ReadAllText(PairingFile.Path);
            ApiPendingPairing pending = PairingFile.Read(out _);
            string farAhead = good.Replace("expires:" + pending.Expires.ToUnixTimeSeconds(), "expires:" + (Now.ToUnixTimeSeconds() + 3600));
            string logBefore = ReadCoreLog();
            foreach (string content in new[] { "1\n", good.Replace("state:pending", "state:x"), farAhead, good.Replace("expires:" + pending.Expires.ToUnixTimeSeconds(), "expires:999999999999") }) {
                ApiTokenStore.WriteOwnerOnlyText(PairingFile.Path, content, "test: ");
                AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            }
            string logged = ReadCoreLog().Substring(logBefore.Length);
            Assert.AreEqual(1, logged.Split("Local API: api-pairing.txt is not trusted, so no pairing code is active.").Length - 1, "logged once");
            ApiTokenStore.WriteOwnerOnlyText(PairingFile.Path, good, "test: ");
            Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode);
            Assert.AreEqual(code.Id, Json(PostHello(ChromeOrigin)).GetProperty("pairingId").GetString());
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [SupportedOSPlatform("windows")]
        public void PairingFile_WindowsInheritedAclIsNoCode() {
            NewCode();
            string text = File.ReadAllText(PairingFile.Path);
            File.Delete(PairingFile.Path);
            File.WriteAllText(PairingFile.Path, text);
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            bool refused;
            Assert.IsNull(PairingFile.Read(out refused));
            Assert.IsTrue(refused);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [UnsupportedOSPlatform("windows")]
        public void PairingFile_UnixReadableByOthersIsNoCode() {
            NewCode();
            File.SetUnixFileMode(PairingFile.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [UnsupportedOSPlatform("windows")]
        public void PairingFile_UnixSymlinkIsNoCode() {
            string otherFolder = Directory.CreateDirectory(Path.Combine(Folder, "other")).FullName;
            new ApiPairingFile(otherFolder).Create(Now);
            File.CreateSymbolicLink(PairingFile.Path, Path.Combine(otherFolder, ApiPairingFile.FileName));
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
        }

        // A folder that keeps no owner-only access: no code file and no credential is written; a finish that cannot
        // save the credential ends the code with 500
        [TestMethod]
        public void Files_VolumeWithoutOwnerOnlyAccessWritesNothing() {
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            OwnerOnlyFile.NewFileCheckForTesting = stream => false;
            Assert.ThrowsExactly<ApiTokenException>(() => new ApiPairingFile(Path.Combine(Folder, "downloads")).Create(Now));
            Assert.ThrowsExactly<ApiTokenException>(() => Clients.Pair(ChromeOrigin, Now));
            AssertProblem(SendFinish(hello), HttpStatusCode.InternalServerError, "internal_error");
            OwnerOnlyFile.NewFileCheckForTesting = null;
            Assert.IsFalse(File.Exists(Clients.Path));
            Assert.IsFalse(File.Exists(PairingFile.Path), "the code is ended");
            Assert.AreEqual(0, Directory.GetFiles(Folder, "*.tmp", SearchOption.AllDirectories).Length);
            StringAssert.Contains(ReadCoreLog(), "Local API: a pairing code was burned (api-clients.txt could not be saved: JDP.Api.ApiTokenException)");
        }

        [TestMethod]
        public void ClientsFile_ParseAcceptsVersionOnlyAndBothFamilies() {
            Assert.AreEqual(0, ApiClientStore.Parse("1\n").Count);
            string text = Version + ClientLine("chrome", ChromeOrigin, "ctwe_a") + ClientLine("firefox", FirefoxOrigin, "ctwe_b");
            IReadOnlyList<ApiClient> clients = ApiClientStore.Parse(text);
            CollectionAssert.AreEqual(new[] { "chrome", "firefox" }, clients.Select(client => client.Family).ToArray());
            Assert.AreEqual(new DateTimeOffset(2026, 10, 6, 21, 30, 0, TimeSpan.Zero), clients[0].PairedAt);
            CollectionAssert.AreEqual(TokenHash("ctwe_b"), clients[1].Hash);
            Assert.IsNull(ApiClientStore.Parse(Version + ClientLine("firefox", ChromeOrigin, "ctwe_a")));
        }

        // The app's settings folder move: the copy holds the same lines, is owner-only, and the browsers' tokens pass
        // with it
        [TestMethod]
        public void ClientsFile_CopyToAnotherFolderIsOwnerOnlyAndKeepsTheBrowsers() {
            Pair(ChromeOrigin);
            Pair(FirefoxOrigin);
            ApiClientStore other = new ApiClientStore(Directory.CreateDirectory(Path.Combine(Folder, "other")).FullName);

            Assert.IsTrue(Clients.CopyTo(other));

            Assert.AreEqual(File.ReadAllText(Clients.Path), File.ReadAllText(other.Path));
            CollectionAssert.AreEqual(new[] { "chrome", "firefox" }, other.Read().Select(client => client.Family).ToArray());
            PairingTests.AssertOwnerOnly(other.Path);
        }

        // A file that is missing, not trusted or lists no browser is not copied: nothing is written in the other folder
        [TestMethod]
        public void ClientsFile_CopyOfAMissingUntrustedOrEmptyFileWritesNothing() {
            ApiClientStore other = new ApiClientStore(Directory.CreateDirectory(Path.Combine(Folder, "other")).FullName);
            Assert.IsFalse(Clients.CopyTo(other));
            ApiTokenStore.WriteOwnerOnlyText(Clients.Path, Version, "test: ");
            Assert.IsFalse(Clients.CopyTo(other));
            ApiTokenStore.WriteOwnerOnlyText(Clients.Path, Version + "garbage\n", "test: ");
            Assert.IsFalse(Clients.CopyTo(other));
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(other.Path)).Length);
        }

        // A copy that can't be made owner-only, or a file that can't be read just now, fails, and leaves no file in the
        // other folder
        [TestMethod]
        public void ClientsFile_CopyThatCannotBeOwnerOnlyOrReadIsAnError() {
            Pair(ChromeOrigin);
            ApiClientStore other = new ApiClientStore(Directory.CreateDirectory(Path.Combine(Folder, "other")).FullName);
            ApiTokenException notOwnerOnly;
            OwnerOnlyFile.NewFileCheckForTesting = stream => false;
            try {
                notOwnerOnly = Assert.ThrowsExactly<ApiTokenException>(() => Clients.CopyTo(other));
            }
            finally {
                OwnerOnlyFile.NewFileCheckForTesting = null;
            }
            Assert.IsTrue(notOwnerOnly.OwnerOnlyNotSupported);
            Assert.IsFalse(notOwnerOnly.ClientsUnreadable);
            ApiTokenException unreadable;
            FailClientsReads(new IOException("test: access denied", unchecked((int)0x80070005)));
            try {
                unreadable = Assert.ThrowsExactly<ApiTokenException>(() => Clients.CopyTo(other));
            }
            finally {
                ApiTokenStore.OpeningForTesting = null;
            }
            Assert.IsFalse(unreadable.OwnerOnlyNotSupported);
            Assert.IsTrue(unreadable.ClientsUnreadable, "The settings folder move tells this failure apart.");
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(other.Path)).Length);
        }

        private void FailClientsReads(Exception failure) {
            string clientsPath = Clients.Path;
            ApiTokenStore.OpeningForTesting = path => {
                if (path == clientsPath) throw failure;
            };
        }

        // An unpair with the hash the dialog showed removes only that pairing: a browser that paired again meanwhile
        // keeps its new line
        [TestMethod]
        public void ClientsFile_RemoveWithAHashRemovesOnlyThatPairing() {
            Pair(ChromeOrigin);
            string firefox = Pair(FirefoxOrigin);
            byte[] shown = Clients.Read().Single(client => client.Family == "chrome").Hash;
            string chromeAgain = Pair(ChromeOrigin);

            Assert.IsFalse(Clients.Remove("chrome", shown), "A newer pairing was removed.");
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(chromeAgain, ChromeOrigin).StatusCode);

            byte[] current = Clients.Read().Single(client => client.Family == "chrome").Hash;
            Assert.IsTrue(Clients.Remove("chrome", current));
            AssertProblem(AddWithToken(chromeAgain, ChromeOrigin), HttpStatusCode.Unauthorized, "unauthorized");
            Assert.IsFalse(Clients.Remove("chrome", current), "not paired");
            CollectionAssert.AreEqual(TokenHash(firefox), Clients.Read().Single().Hash, "the other family stays");
            Assert.IsTrue(Clients.Remove("firefox", null), "a null hash removes any");
            Assert.AreEqual(0, Clients.Read().Count);
        }

        // A current user without a security identifier (InvalidOperationException from the access check) fails the
        // read like an I/O failure: no paired browser passes, and a writer does not replace the file
        [TestMethod]
        public void ClientsFile_InvalidOperationOnReadFailsClosed() {
            string chrome = Pair(ChromeOrigin);
            string text = File.ReadAllText(Clients.Path);
            FailClientsReads(new InvalidOperationException("test: no security identifier"));
            try {
                OwnerOnlyRead status;
                Assert.IsNull(ApiTokenStore.ReadOwnerOnlyText(Clients.Path, 1024, out status));
                Assert.AreEqual(OwnerOnlyRead.Failed, status);
                Assert.IsNull(Clients.Read());
                AssertProblem(AddWithToken(chrome, ChromeOrigin), HttpStatusCode.Unauthorized, "unauthorized");
                Assert.IsTrue(Assert.ThrowsExactly<ApiTokenException>(() => Clients.Pair(FirefoxOrigin, Now)).ClientsUnreadable);
            }
            finally {
                ApiTokenStore.OpeningForTesting = null;
            }
            Assert.AreEqual(text, File.ReadAllText(Clients.Path), "The file was replaced.");
        }

        // The start rule is unchanged: a paired browser without the scripts' token does not start the API
        [TestMethod]
        public void Start_StillNeedsTheScriptToken() {
            Pair(ChromeOrigin);
            Server.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            File.Delete(Tokens.Path);
            ApiServer server = new ApiServer(new ApiThreadService(Session, new OwnerThreadDispatcher(Owner.Post), url => NewThread.Create(url, null, null), Policy), Tokens);
            Assert.AreEqual(ApiStartError.TokenMissing, Assert.ThrowsExactly<ApiStartException>(() => server.StartAsync(0).GetAwaiter().GetResult()).Error);
        }

        internal static string ReadCoreLog() {
            using (FileStream stream = new FileStream(ApiTestHost.LogPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream)) {
                return reader.ReadToEnd();
            }
        }
    }

    // No secret of a pairing in any log: the code, the key, the token and its hash, the nonces and the proofs
    [TestClass]
    public class PairingLogTests : PairingTestBase {
        [TestMethod]
        public void Log_PairingNeverHoldsASecret() {
            List<string> secrets = new List<string>();
            string coreLogBefore = PairingFileTests.ReadCoreLog();

            // A full pairing, then a proof
            ApiPairingCode code = NewCode();
            TestHello hello = SendHello(code.Code, ChromeOrigin);
            HttpResponseMessage finish = SendFinish(hello);
            JsonElement finished = Json(finish);
            string token = finished.GetProperty("token").GetString();
            string nonce = ApiPairing.NewNonce();
            string proof = Json(PostProof(ChromeOrigin, nonce)).GetProperty("proof").GetString();
            AddWithToken(token, ChromeOrigin);
            AddWithToken(token, FirefoxOrigin);
            secrets.AddRange(HelloSecrets(code, hello));
            secrets.AddRange(new[] { FinishProof(hello, Port, ChromeOrigin), finished.GetProperty("proof").GetString(), token, token.Substring(5), Hex(TokenHash(token)), nonce, proof });

            // A failed pairing: a wrong code, a wrong proof (burns), then a used code
            ApiPairingCode second = NewCode();
            TestHello wrong = SendHello("00000000", FirefoxOrigin);
            SendFinish(wrong);
            PostHello(FirefoxOrigin);
            secrets.AddRange(HelloSecrets(second, wrong));
            secrets.Add(FinishProof(wrong, Port, FirefoxOrigin));

            IReadOnlyList<string> entries = Log.Entries;
            string coreLog = PairingFileTests.ReadCoreLog().Substring(coreLogBefore.Length);
            StringAssert.Contains(coreLog, "Local API: paired a chrome extension");
            StringAssert.Contains(coreLog, "Local API: a pairing code was burned (wrong proof)");
            foreach (string secret in secrets.Where(secret => !String.IsNullOrEmpty(secret))) {
                Assert.IsFalse(coreLog.Contains(secret, StringComparison.OrdinalIgnoreCase), "log.txt holds a secret");
                foreach (string entry in entries) {
                    Assert.IsFalse(entry.Contains(secret, StringComparison.OrdinalIgnoreCase), "a log entry holds a secret");
                }
            }
        }

        private static IEnumerable<string> HelloSecrets(ApiPairingCode code, TestHello hello) {
            return new[] { code.Code, code.Code.Replace("-", ""), Hex(hello.Key), ApiPairing.Base64Url(hello.Key), hello.ClientNonce, hello.ServerNonce, hello.Proof };
        }
    }

    // The shared cross-language vectors (PairingVectors.json, fixed fake inputs; the extension's tests read the same
    // file): the key, P1, P2, P3 and the proofs before use, made here exactly as listed
    [TestClass]
    public class PairingVectorTests {
        // SHA-256 of PairingVectors.json with LF line endings; change it only with a reviewed change of the vectors
        private const string VectorsHash = "c42b62d55b71e3d0d82807269d4b8c58dd15f2019a60d5f9097cca0854268844";

        private static JsonElement Vectors {
            get { return JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "PairingVectors.json"))).RootElement; }
        }

        [TestMethod]
        public void Vectors_ReproduceEveryValue() {
            JsonElement document = Vectors;
            Assert.AreEqual(ApiPolicy.PairingIterations, document.GetProperty("pbkdf2Iterations").GetInt32());
            int count = 0;
            foreach (JsonElement vector in document.GetProperty("vectors").EnumerateArray()) {
                AssertVector(vector);
                count++;
            }
            Assert.AreEqual(2, count);
            foreach (JsonElement script in document.GetProperty("scriptProofs").EnumerateArray()) {
                string token = script.GetProperty("token").GetString();
                Assert.AreEqual(script.GetProperty("tokenHashHex").GetString(), Hex(token));
                Assert.AreEqual("", script.GetProperty("origin").GetString());
                Assert.AreEqual(script.GetProperty("useProof").GetString(), ApiPairing.Base64Url(ApiPairing.UseProof(ApiTokenStore.Hash(token), script.GetProperty("port").GetInt32(), "", script.GetProperty("clientNonce").GetString())));
            }
        }

        private static void AssertVector(JsonElement vector) {
            string name = vector.GetProperty("name").GetString();
            string code = vector.GetProperty("code").GetString();
            int port = vector.GetProperty("port").GetInt32();
            string origin = vector.GetProperty("origin").GetString();
            string id = vector.GetProperty("pairingId").GetString();
            string clientNonce = vector.GetProperty("clientNonce").GetString();
            string serverNonce = vector.GetProperty("serverNonce").GetString();
            string token = vector.GetProperty("token").GetString();
            Assert.AreEqual(name, ApiPairing.FamilyOf(origin));
            Assert.IsTrue(ApiPairing.IsPairingOrigin(origin), name);
            Assert.AreEqual(vector.GetProperty("displayCode").GetString(), ApiPairing.FormatCode(code));
            Assert.IsTrue(ApiPairing.IsValidServerName(vector.GetProperty("serverName").GetString()), name);
            Assert.IsNotNull(ApiPairing.FromBase64Url(id, ApiPairing.IdBytes), name);
            Assert.IsNotNull(ApiPairing.FromBase64Url(clientNonce, ApiPairing.NonceBytes), name);
            byte[] key = ApiPairing.DeriveKey(code, ApiPairing.FromBase64Url(vector.GetProperty("salt").GetString(), ApiPairing.SaltBytes));
            Assert.AreEqual(vector.GetProperty("keyHex").GetString(), Convert.ToHexStringLower(key), name);
            Assert.AreEqual(vector.GetProperty("serverHelloProof").GetString(), ApiPairing.Base64Url(ApiPairing.ServerHelloProof(key, port, origin, id, clientNonce, serverNonce, vector.GetProperty("serverName").GetString())), name);
            Assert.AreEqual(vector.GetProperty("clientFinishProof").GetString(), ApiPairing.Base64Url(ApiPairing.ClientFinishProof(key, port, origin, id, clientNonce, serverNonce)), name);
            Assert.AreEqual(vector.GetProperty("serverFinishProof").GetString(), ApiPairing.Base64Url(ApiPairing.ServerFinishProof(key, port, origin, id, clientNonce, serverNonce, token)), name);
            Assert.AreEqual(vector.GetProperty("tokenHashHex").GetString(), Hex(token), name);
            Assert.AreEqual(vector.GetProperty("useProof").GetString(), ApiPairing.Base64Url(ApiPairing.UseProof(ApiTokenStore.Hash(token), port, origin, clientNonce)), name);
        }

        private static string Hex(string token) {
            return Convert.ToHexStringLower(ApiTokenStore.Hash(token));
        }

        // The file is the reviewed one (its line endings aside): a change to any value shows here first
        [TestMethod]
        public void Vectors_FileIsTheReviewedOne() {
            string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PairingVectors.json")).Replace("\r\n", "\n");
            Assert.AreEqual(VectorsHash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        }

        [TestMethod]
        public void Origins_FormsAndThePinnedChromeId() {
            Assert.AreEqual("chrome", ApiPairing.FamilyOf("chrome-extension://abcdefghijklmnopabcdefghijklmnop"));
            Assert.IsFalse(ApiPairing.IsPairingOrigin("chrome-extension://abcdefghijklmnopabcdefghijklmnop"));
            Assert.IsTrue(ApiPairing.IsPairingOrigin("chrome-extension://eifjifphdncjkkolefjepjhdlmcdbndh"));
            Assert.IsTrue(ApiPairing.IsPairingOrigin("moz-extension://0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5f"));
            foreach (string origin in new[] { null, "", "chrome-extension://", "chrome-extension://eifjifphdncjkkolefjepjhdlmcdbndq", "chrome-extension://eifjifphdncjkkolefjepjhdlmcdbnd",
                "chrome-extension://eifjifphdncjkkolefjepjhdlmcdbndh/", " chrome-extension://eifjifphdncjkkolefjepjhdlmcdbndh", "chrome-extension://eifjifphdncjkkolefjepjhdlmcdbndh\n",
                "moz-extension://0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5F", "moz-extension://0d4f2a8c_5b1e-4c7a-9f3d-2e6b8a1c4d5f", "moz-extension://0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5f\n",
                "safari-web-extension://0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5f", "http://127.0.0.1:47710" }) {
                Assert.IsNull(ApiPairing.FamilyOf(origin), origin ?? "null");
                Assert.IsFalse(ApiPairing.IsPairingOrigin(origin), origin ?? "null");
            }
        }
    }
}
