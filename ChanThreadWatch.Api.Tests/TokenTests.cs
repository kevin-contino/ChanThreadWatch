using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Api.Tests {
    // Authorization: Bearer, the token file and its access (security item 15, design D2-D4)
    [TestClass]
    public class TokenTests : ApiTestBase {
        [TestMethod]
        public void Token_MissingIsRefusedWithBearerChallenge() {
            Client.DefaultRequestHeaders.Authorization = null;
            foreach (string path in new[] { ThreadsEndpoints.ThreadsPath, ThreadsEndpoints.OpenApiPath }) {
                HttpResponseMessage response = Get(path);
                AssertProblem(response, HttpStatusCode.Unauthorized, "unauthorized");
                Assert.AreEqual("Bearer", response.Headers.WwwAuthenticate.ToString());
            }
            AssertProblem(AddThread(ThreadUrl), HttpStatusCode.Unauthorized, "unauthorized");
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Token_WrongValuesAreRefused() {
            string[] values = {
                "Bearer " + Token + "x", "Bearer " + Token.Substring(1), "Bearer " + Token.ToUpperInvariant(), "Bearer  " + Token,
                "Basic " + Token, "Token " + Token, Token, "Bearer", "Bearer ", "Bearer " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token))),
                "Bearer sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token)))
            };
            Client.DefaultRequestHeaders.Authorization = null;
            foreach (string value in values) {
                HttpResponseMessage response = Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.TryAddWithoutValidation("Authorization", value));
                AssertProblem(response, HttpStatusCode.Unauthorized, "unauthorized");
            }
        }

        [TestMethod]
        public void Token_SchemeIsCaseInsensitive() {
            Client.DefaultRequestHeaders.Authorization = null;
            HttpResponseMessage response = Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.TryAddWithoutValidation("Authorization", "bearer " + Token));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [TestMethod]
        public void Token_OverlongValueIsRefused() {
            Client.DefaultRequestHeaders.Authorization = null;
            string overlong = "Bearer " + Token + new string('A', 257 - 7 - Token.Length);
            Assert.AreEqual(257, overlong.Length);
            AssertProblem(Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.TryAddWithoutValidation("Authorization", overlong)), HttpStatusCode.Unauthorized, "unauthorized");
            Assert.IsNull(new ApiCredentials(Tokens, new ApiClientStore(Folder)).Identify(Token + new string('A', 300)));
        }

        [TestMethod]
        public void Token_TwoAuthorizationHeadersAreRefused() {
            string port = Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RawResponse response = SendRaw(RawGet(ThreadsEndpoints.ThreadsPath, "127.0.0.1:" + port, "Authorization: Bearer " + Token + "\r\n"));
            Assert.AreEqual(401, response.Status, response.ToString());
        }

        [TestMethod]
        public void Token_HasThePrefixAnd32RandomBytes() {
            StringAssert.Matches(Token, new Regex("^ctw_[A-Za-z0-9_-]{43}$"));
            Assert.AreNotEqual(Token, Tokens.Generate());
        }

        // Only the hash is saved; the file never holds the token
        [TestMethod]
        public void TokenFile_HoldsOnlyTheHash() {
            string text = File.ReadAllText(Tokens.Path);
            Assert.AreEqual("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token))) + "\n", text);
            Assert.IsFalse(text.Contains(Token.Substring(4), StringComparison.Ordinal));
            Assert.AreEqual(Path.Combine(Folder, "api-token.txt"), Tokens.Path);
            Assert.AreEqual(0, Directory.GetFiles(Folder, "*.tmp").Length);
        }

        [TestMethod]
        public void Token_RegeneratedInProcessReplacesTheOldOneWithoutRestart() {
            string newToken = Tokens.Generate();
            AssertProblem(Get(ThreadsEndpoints.ThreadsPath), HttpStatusCode.Unauthorized, "unauthorized");
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode);
        }

        // Another process (ctw api-token) writes the file; the server reads it on the next request
        [TestMethod]
        public void Token_RegeneratedByAnotherProcessReplacesTheOldOne() {
            string newToken = new ApiTokenStore(Folder).Generate();
            AssertProblem(Get(ThreadsEndpoints.ThreadsPath), HttpStatusCode.Unauthorized, "unauthorized");
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode);

            // The same file size and, within the file system's timestamp resolution, the same time: still read again
            string thirdToken = "ctw_" + new string('b', 43);
            Tokens.WriteHashFile("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(thirdToken))));
            AssertProblem(Get(ThreadsEndpoints.ThreadsPath), HttpStatusCode.Unauthorized, "unauthorized");
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", thirdToken);
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode);
        }

        // Fail closed: no file or a damaged file lets no request through
        [TestMethod]
        public void TokenFile_MissingOrMalformedRefusesEveryToken() {
            File.Delete(Tokens.Path);
            AssertProblem(Get(ThreadsEndpoints.ThreadsPath), HttpStatusCode.Unauthorized, "unauthorized");
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token)));
            foreach (string content in new[] { "", "sha256:", hash, "sha256:" + hash.ToUpperInvariant(), "sha256:" + hash + "0", "md5:" + hash, " sha256:" + hash, "sha256:" + hash + "\nsha256:" + hash }) {
                Tokens.WriteHashFile(content);
                Assert.IsFalse(Tokens.IsConfigured(), content);
                AssertProblem(Get(ThreadsEndpoints.ThreadsPath), HttpStatusCode.Unauthorized, "unauthorized");
            }
        }

        [TestMethod]
        public void Start_WithoutATokenRefusesToStart() {
            Server.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            int oldPort = Port;
            File.Delete(Tokens.Path);
            ApiServer server = new ApiServer(new ApiThreadService(Session, new OwnerThreadDispatcher(Owner.Post), url => NewThread.Create(url, null, null), Policy), Tokens);
            ApiStartException error = Assert.ThrowsExactly<ApiStartException>(() => server.StartAsync(0).GetAwaiter().GetResult());
            Assert.AreEqual(ApiStartError.TokenMissing, error.Error);
            Assert.AreEqual(0, server.BoundPort);

            Tokens.WriteHashFile("sha256:not-a-hash");
            error = Assert.ThrowsExactly<ApiStartException>(() => server.StartAsync(0).GetAwaiter().GetResult());
            Assert.AreEqual(ApiStartError.TokenMissing, error.Error);
            AssertNotListening(oldPort);
        }

        private static void AssertNotListening(int port) {
            using (TcpClient client = new TcpClient()) {
                Assert.ThrowsExactly<SocketException>(() => client.Connect(IPAddress.Loopback, port));
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [SupportedOSPlatform("windows")]
        public void TokenFile_WindowsHasOneProtectedRuleForTheCurrentUser() {
            AssertOwnerOnlyAcl(Tokens.Path);
            Tokens.Generate();
            AssertOwnerOnlyAcl(Tokens.Path);
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
            Assert.IsFalse(rule.IsInherited);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [UnsupportedOSPlatform("windows")]
        public void TokenFile_UnixIsMode0600() {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Tokens.Path));
            Tokens.Generate();
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Tokens.Path));
        }

        // A folder the token cannot be written to: an error, and no file left behind
        [TestMethod]
        public void TokenFile_UnwritableFolderIsAnError() {
            ApiTokenStore store = new ApiTokenStore(Path.Combine(Folder, "missing-folder"));
            Assert.ThrowsExactly<ApiTokenException>(() => store.Generate());
            Assert.IsFalse(Directory.Exists(Path.Combine(Folder, "missing-folder")));
        }

        // A file over 128 bytes is refused as a whole, not cut: the valid line followed by many blank lines does not pass
        [TestMethod]
        public void TokenFile_LongerThanTheLimitIsRefusedAsAWhole() {
            string line = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token)));
            Tokens.WriteHashFile(line + new string('\n', 3));
            Assert.IsTrue(Tokens.IsConfigured());
            Tokens.WriteHashFile(line + new string('\n', 128 - line.Length - 1));
            Assert.AreEqual(128, File.ReadAllBytes(Tokens.Path).Length);
            Assert.IsTrue(Tokens.IsConfigured(), "128 bytes is the limit");
            Tokens.WriteHashFile(line + new string('\n', 128 - line.Length));
            Assert.IsFalse(Tokens.IsConfigured());
            AssertProblem(Get(ThreadsEndpoints.ThreadsPath), HttpStatusCode.Unauthorized, "unauthorized");
        }

        [TestMethod]
        public void TokenFile_ParseAcceptsOnlyTheOneLineForm() {
            string hash = new string('a', 64);
            Assert.IsNotNull(ApiTokenStore.ParseHash("sha256:" + hash));
            Assert.IsNull(ApiTokenStore.ParseHash("sha256:" + new string('g', 64)));
            Assert.IsNull(ApiTokenStore.ParseHash("SHA256:" + hash));
            Assert.AreEqual(32, ApiTokenStore.ParseHash("sha256:" + hash).Length);
        }

        [TestMethod]
        public void Token_EveryEndpointNeedsIt() {
            Client.DefaultRequestHeaders.Authorization = null;
            foreach (string path in new[] { ThreadsEndpoints.ThreadsPath, ThreadsEndpoints.OpenApiPath, "/", "/api", "/api/v1" }) {
                Assert.AreEqual(HttpStatusCode.Unauthorized, Get(path).StatusCode, path);
            }
            Assert.IsTrue(new[] { ThreadsEndpoints.ThreadsPath }.All(path => Send(HttpMethod.Post, path, "{}").StatusCode == HttpStatusCode.Unauthorized));
        }

        // Access is checked on every read: a file that others can read is not trusted (the start refuses it too)
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [UnsupportedOSPlatform("windows")]
        public void TokenFile_UnixReadableByOthersIsRefused() {
            File.SetUnixFileMode(Tokens.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            AssertTokenFileRefused();
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [UnsupportedOSPlatform("windows")]
        public void TokenFile_UnixSymlinkIsRefused() {
            string otherFolder = Path.Combine(Folder, "other");
            Directory.CreateDirectory(otherFolder);
            ApiTokenStore other = new ApiTokenStore(otherFolder);
            Token = other.Generate();
            File.Delete(Tokens.Path);
            File.CreateSymbolicLink(Tokens.Path, other.Path);
            Assert.IsTrue(other.Verify(Token));
            AssertTokenFileRefused();
        }

        // A file written without the owner-only ACL (it inherits the folder's rules) is not trusted
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [SupportedOSPlatform("windows")]
        public void TokenFile_WindowsInheritedAclIsRefused() {
            string line = File.ReadAllText(Tokens.Path);
            File.Delete(Tokens.Path);
            File.WriteAllText(Tokens.Path, line);
            Assert.IsFalse(new FileInfo(Tokens.Path).GetAccessControl().AreAccessRulesProtected);
            AssertTokenFileRefused();
        }

        // The same token is refused, and a new server does not start
        private void AssertTokenFileRefused() {
            Assert.IsFalse(Tokens.Verify(Token));
            Assert.IsFalse(Tokens.IsConfigured());
            AssertProblem(Get(ThreadsEndpoints.ThreadsPath), HttpStatusCode.Unauthorized, "unauthorized");
            ApiServer server = new ApiServer(new ApiThreadService(Session, new OwnerThreadDispatcher(Owner.Post), url => NewThread.Create(url, null, null), Policy), Tokens);
            Assert.AreEqual(ApiStartError.TokenMissing, Assert.ThrowsExactly<ApiStartException>(() => server.StartAsync(0).GetAwaiter().GetResult()).Error);
        }

        // Regenerate while the server checks tokens (one thread verifies, another regenerates, ~1000 times): the
        // replace never fails on a reader holding the file, and the new token works at once
        [TestMethod]
        // The race is Windows sharing semantics; a Unix rename never fails on an open reader
        [OSCondition(OperatingSystems.Windows)]
        public void Token_RegenerateWhileVerifyingNeverFails() {
            string current = Token;
            bool done = false;
            List<Exception> verifyErrors = new List<Exception>();
            Thread verifier = new Thread(() => {
                while (!Volatile.Read(ref done)) {
                    try {
                        Tokens.Verify(Volatile.Read(ref current));
                    }
                    catch (Exception ex) {
                        lock (verifyErrors) verifyErrors.Add(ex);
                    }
                }
            });
            verifier.Start();
            try {
                for (int i = 0; i < 1000; i++) {
                    string next = Tokens.Generate();
                    Volatile.Write(ref current, next);
                    Assert.IsTrue(Tokens.Verify(next), "iteration " + i);
                }
            }
            finally {
                Volatile.Write(ref done, true);
                verifier.Join();
            }
            Assert.AreEqual(0, verifyErrors.Count, String.Join("; ", verifyErrors));
            Assert.AreEqual(0, Directory.GetFiles(Folder, "*.tmp").Length);
        }
        // A protected DACL that also lets Everyone read is not owner-only
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [SupportedOSPlatform("windows")]
        public void TokenFile_WindowsProtectedAclWithAnotherAllowRuleIsRefused() {
            string line = File.ReadAllText(Tokens.Path);
            File.Delete(Tokens.Path);
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            FileSecurity security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            using (FileStream stream = new FileInfo(Tokens.Path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Synchronize, FileShare.None, 4096, FileOptions.None, security)) {
                byte[] bytes = Encoding.ASCII.GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
            }
            Assert.IsTrue(new FileInfo(Tokens.Path).GetAccessControl().AreAccessRulesProtected);
            AssertTokenFileRefused();
        }

        // The app's settings folder move: the copy holds the same hash, is owner-only, and the same token passes there
        [TestMethod]
        public void TokenFile_CopyToAnotherFolderIsOwnerOnlyAndKeepsTheToken() {
            ApiTokenStore other = new ApiTokenStore(Directory.CreateDirectory(Path.Combine(Folder, "other")).FullName);

            Assert.IsTrue(Tokens.CopyTo(other));

            Assert.AreEqual(File.ReadAllText(Tokens.Path), File.ReadAllText(other.Path));
            Assert.IsTrue(other.Verify(Token));
            using (FileStream stream = new FileStream(other.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                Assert.IsTrue(OwnerOnlyFile.IsOwnerOnly(stream));
            }
        }

        // A file that is missing or not valid is not copied: nothing is written in the other folder
        [TestMethod]
        public void TokenFile_CopyOfAMissingOrMalformedFileWritesNothing() {
            ApiTokenStore other = new ApiTokenStore(Directory.CreateDirectory(Path.Combine(Folder, "other")).FullName);
            Tokens.WriteHashFile("sha256:");
            Assert.IsFalse(Tokens.CopyTo(other));
            File.Delete(Tokens.Path);
            Assert.IsFalse(Tokens.CopyTo(other));
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(other.Path)).Length);
        }

        // A copy that can't be made owner-only fails, and leaves no file in the other folder
        [TestMethod]
        public void TokenFile_CopyToAVolumeWithoutOwnerOnlyAccessIsAnError() {
            ApiTokenStore other = new ApiTokenStore(Directory.CreateDirectory(Path.Combine(Folder, "other")).FullName);
            OwnerOnlyFile.NewFileCheckForTesting = stream => false;
            Assert.ThrowsExactly<ApiTokenException>(() => Tokens.CopyTo(other));
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(other.Path)).Length);
        }

        // A volume that keeps no owner-only access (FAT on Windows): Generate fails with a clear message and leaves
        // the old file and no temporary file
        [TestMethod]
        public void TokenFile_VolumeWithoutOwnerOnlyAccessIsAnError() {
            string before = File.ReadAllText(Tokens.Path);
            OwnerOnlyFile.NewFileCheckForTesting = stream => false;
            ApiTokenException error = Assert.ThrowsExactly<ApiTokenException>(() => Tokens.Generate());
            StringAssert.Contains(error.Message, OperatingSystem.IsWindows() ? "move it to an NTFS folder" : "mode 0600");
            Assert.AreEqual(before, File.ReadAllText(Tokens.Path));
            Assert.AreEqual(0, Directory.GetFiles(Folder, "*.tmp").Length);
            OwnerOnlyFile.NewFileCheckForTesting = null;
            Assert.IsTrue(Tokens.Verify(Token));
        }

        // An access check that throws (an ACL that can't be read, a file system without ACLs) is an ApiTokenException
        // too: the new file is closed and deleted, and the old token still works
        [TestMethod]
        [DataRow(typeof(UnauthorizedAccessException))]
        [DataRow(typeof(NotSupportedException))]
        public void TokenFile_AccessCheckThatThrowsIsAnErrorAndLeavesNoFile(Type exceptionType) {
            string before = File.ReadAllText(Tokens.Path);
            OwnerOnlyFile.NewFileCheckForTesting = stream => throw (Exception)Activator.CreateInstance(exceptionType);
            try {
                ApiTokenException error = Assert.ThrowsExactly<ApiTokenException>(() => Tokens.Generate());
                Assert.IsInstanceOfType(error.InnerException, exceptionType);
            }
            finally {
                OwnerOnlyFile.NewFileCheckForTesting = null;
            }
            Assert.AreEqual(before, File.ReadAllText(Tokens.Path));
            Assert.AreEqual(0, Directory.GetFiles(Folder, "*.tmp").Length);
            Assert.IsTrue(Tokens.Verify(Token));
        }

        // A link put in the file's place between the link check and the open is refused (the open follows it)
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [UnsupportedOSPlatform("windows")]
        public void TokenFile_UnixSymlinkSwappedInBeforeTheOpenIsRefused() {
            string otherFolder = Path.Combine(Folder, "other");
            Directory.CreateDirectory(otherFolder);
            ApiTokenStore other = new ApiTokenStore(otherFolder);
            string otherToken = other.Generate();
            ApiTokenStore.OpeningForTesting = path => {
                ApiTokenStore.OpeningForTesting = null;
                File.Delete(Tokens.Path);
                File.CreateSymbolicLink(Tokens.Path, other.Path);
            };
            try {
                Assert.IsFalse(Tokens.Verify(otherToken));
            }
            finally {
                ApiTokenStore.OpeningForTesting = null;
            }
            Assert.IsTrue(other.Verify(otherToken));
        }

        // Only a sharing or lock violation is read again; access denied and other errors fail the check at once
        [TestMethod]
        public void TokenFile_OnlySharingViolationsAreRetried() {
            IOException sharing = new IOException("sharing", unchecked((int)0x80070020));
            IOException locked = new IOException("lock", unchecked((int)0x80070021));
            Assert.IsTrue(ApiTokenStore.IsRetryable(sharing, 1));
            Assert.IsTrue(ApiTokenStore.IsRetryable(locked, 9));
            Assert.IsFalse(ApiTokenStore.IsRetryable(sharing, 10));
            Assert.IsFalse(ApiTokenStore.IsRetryable(new IOException("other", unchecked((int)0x80070005)), 1));
            Assert.IsFalse(ApiTokenStore.IsRetryable(new FileNotFoundException(), 1));
        }    }
}
