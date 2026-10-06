using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using JDP.Api;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Cli.Tests {
    // ctw api-token (MP-7a L3): the token is printed once on stdout and only its hash is saved, in an owner-only
    // api-token.txt; nothing else in the settings folder changes. Every token here is made at run time in the test's
    // temporary folder, and a token, or stdout, never goes into an assertion's message.
    [TestClass]
    public class ApiTokenCommandTests : CliTestBase {
        // "ctw_" and 32 random bytes in base64url without padding
        private static readonly Regex _tokenLine = new Regex("^ctw_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant);

        private string TokenPath {
            get { return Path.Combine(Folder, ApiTokenStore.FileName); }
        }

        // Not root, also in a container that runs as root, so the command runs
        [TestInitialize]
        public void RunAsAUser() {
            OwnerOnlyFile.IsRootOnUnix = () => false;
        }

        [TestCleanup]
        public void ResetTokenFileHooks() {
            OwnerOnlyFile.IsRootOnUnix = () => !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess;
            OwnerOnlyFile.NewFileCheckForTesting = null;
        }

        [TestMethod]
        public void ApiToken_PrintsTheTokenOnceAndSavesOnlyItsHash() {
            Dictionary<string, byte[]> before = WriteOtherSettingsFiles();

            CliResult result = Run("api-token");

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, Describe(result));
            string token = AssertOneToken(result);
            // stderr has guidance only, never the token
            Assert.IsFalse(result.Error.Contains(token, StringComparison.Ordinal), "The token is on stderr.");
            StringAssert.Contains(result.Error, "It is shown only this once");
            Assert.IsFalse(result.Error.Contains("replaces", StringComparison.Ordinal), result.Error);
            Assert.IsTrue(Lines(result.Error).All(line => line.StartsWith("ctw: note: ", StringComparison.Ordinal)), result.Error);
            string content = File.ReadAllText(TokenPath, Encoding.ASCII);
            Assert.AreEqual("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))) + "\n", content);
            Assert.IsFalse(content.Contains(token, StringComparison.Ordinal));
            Assert.IsTrue(new ApiTokenStore(Folder).Verify(token));
            AssertOnlyTheTokenFileChanged(before);
        }

        [TestMethod]
        public void ApiToken_AgainReplacesThePreviousToken() {
            string first = AssertOneToken(Run("api-token"));
            Dictionary<string, byte[]> before = WriteOtherSettingsFiles();

            CliResult result = Run("api-token");

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, Describe(result));
            string second = AssertOneToken(result);
            Assert.IsFalse(first == second, "The token did not change.");
            StringAssert.Contains(result.Error, "It replaces the previous token, which no longer works");
            ApiTokenStore store = new ApiTokenStore(Folder);
            Assert.IsFalse(store.Verify(first));
            Assert.IsTrue(store.Verify(second));
            AssertOnlyTheTokenFileChanged(before);
        }

        // Only the current user can read or write the file: a protected DACL with no other allow rule, owned by the
        // user, on Windows; mode 0600 elsewhere
        [TestMethod]
        public void ApiToken_TheFileIsOwnerOnly() {
            AssertOneToken(Run("api-token"));

            if (OperatingSystem.IsWindows()) {
                AssertOwnerOnlyOnWindows(TokenPath);
            }
            else {
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TokenPath));
            }
        }

        // ctw watch holds the folder's lock while it runs; api-token does not need it
        [TestMethod]
        public void ApiToken_WorksWhileWatchHoldsTheLock() {
            SettingsFolderLock watchLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(Folder, SettingsFolderLockHolder.Watch, out watchLock));
            using (watchLock) {
                CliResult result = Run("api-token");

                Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, Describe(result));
                Assert.IsTrue(new ApiTokenStore(Folder).Verify(AssertOneToken(result)));
            }
        }

        // A file system that keeps no owner-only access (as FAT): an error, and no token is printed or left behind
        [TestMethod]
        public void ApiToken_WithoutOwnerOnlyAccessFailsAndWritesNothing() {
            OwnerOnlyFile.NewFileCheckForTesting = stream => false;

            CliResult result = Run("api-token");

            AssertFailedWithoutToken(result, OperatingSystem.IsWindows() ? "does not support owner-only files" : "does not keep file permissions");
            CollectionAssert.AreEqual(new string[0], Directory.GetFiles(Folder));
        }

        // An access check that throws (an ACL that can't be read, no ACL support) is a one-line error, not a stack trace
        [TestMethod]
        [DataRow(typeof(UnauthorizedAccessException))]
        [DataRow(typeof(NotSupportedException))]
        public void ApiToken_AccessCheckThatThrowsFailsAndWritesNothing(Type exceptionType) {
            OwnerOnlyFile.NewFileCheckForTesting = stream => throw (Exception)Activator.CreateInstance(exceptionType);

            CliResult result = Run("api-token");

            AssertFailedWithoutToken(result, "The API token file could not be written: ");
            CollectionAssert.AreEqual(new string[0], Directory.GetFiles(Folder));
        }

        [TestMethod]
        public void ApiToken_RefusesToRunAsRoot() {
            OwnerOnlyFile.IsRootOnUnix = () => true;

            CliResult result = Run("api-token");

            AssertFailedWithoutToken(result, "ctw api-token does not run as root");
            Assert.IsFalse(File.Exists(TokenPath));
        }

        [TestMethod]
        public void ApiToken_TakesNoArguments() {
            AssertFailed(Run("api-token", "extra"), CliApp.ExitUsage, "Wrong arguments. Usage: ctw api-token");
            AssertFailed(Run("api-token", "--force"), CliApp.ExitUsage, "Unknown option '--force'. Usage: ctw api-token");
            CliResult help = Run("api-token", "--help");
            AssertSucceeded(help);
            Assert.AreEqual("Usage: ctw api-token" + Environment.NewLine, help.Output);
            Assert.IsFalse(File.Exists(TokenPath));
        }

        // A note that can't be written (a closed stderr) is dropped: the token is out, so the command succeeds
        [TestMethod]
        public void ApiToken_SucceedsWhenTheNotesCannotBeWritten() {
            StringWriter output = new StringWriter(CultureInfo.InvariantCulture);

            int exitCode = CliApp.Run(new[] { "api-token" }, output, new FailingWriter(), Folder);

            Assert.AreEqual(CliApp.ExitSuccess, exitCode);
            Assert.IsTrue(new ApiTokenStore(Folder).Verify(AssertOneToken(new CliResult { ExitCode = exitCode, Output = output.ToString(), Error = "" })));
        }

        // A token that can't be written to stdout is lost, and its hash already replaced the previous one: an error that
        // says to run the command again
        [TestMethod]
        public void ApiToken_FailsWhenTheTokenCannotBeWritten() {
            StringWriter error = new StringWriter(CultureInfo.InvariantCulture);

            int exitCode = CliApp.Run(new[] { "api-token" }, new FailingWriter(), error, Folder);

            Assert.AreEqual(CliApp.ExitFailure, exitCode, error.ToString());
            Assert.AreEqual("ctw: The new token could not be written to stdout, and the previous token, if any, no longer works. Run 'ctw api-token' again." +
                Environment.NewLine, error.ToString());
            Assert.IsTrue(File.Exists(TokenPath));
        }

        // Exit code and stderr only
        private static string Describe(CliResult result) {
            return "exit " + result.ExitCode + ", stderr: " + result.Error;
        }

        // As AssertFailed, without stdout in the messages
        private static void AssertFailedWithoutToken(CliResult result, string messagePart) {
            Assert.AreEqual(CliApp.ExitFailure, result.ExitCode, Describe(result));
            Assert.AreEqual(0, result.Output.Length, "Something was printed on stdout. " + Describe(result));
            Assert.IsTrue(result.Error.StartsWith("ctw: ", StringComparison.Ordinal), Describe(result));
            Assert.AreEqual(1, Lines(result.Error).Length, Describe(result));
            StringAssert.Contains(result.Error, messagePart);
        }

        // stdout is the token alone, on one line
        private static string AssertOneToken(CliResult result) {
            string[] lines = Lines(result.Output);
            Assert.HasCount(1, lines, Describe(result));
            Assert.IsTrue(result.Output == lines[0] + Environment.NewLine, "More than the token on stdout. " + Describe(result));
            Assert.IsTrue(_tokenLine.IsMatch(lines[0]), "Not a token: " + lines[0].Length + " characters");
            return lines[0];
        }

        // The files a settings folder holds beside the token, with their content
        private Dictionary<string, byte[]> WriteOtherSettingsFiles() {
            File.WriteAllLines(Path.Combine(Folder, Settings.SettingsFileName), new[] { "ApiEnabled=1", "ApiPort=47711" });
            WriteThreadList(ThreadLines("https://boards.4chan.org/a/thread/111"));
            File.WriteAllLines(Path.Combine(Folder, Settings.ApiThreadsFileName), new[] { "1" });
            return Directory.GetFiles(Folder).Where(path => path != TokenPath).ToDictionary(path => path, File.ReadAllBytes);
        }

        private void AssertOnlyTheTokenFileChanged(Dictionary<string, byte[]> before) {
            CollectionAssert.AreEquivalent(before.Keys.Concat(new[] { TokenPath }).ToArray(), Directory.GetFiles(Folder));
            foreach (KeyValuePair<string, byte[]> file in before) {
                CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key), file.Key);
            }
        }

        private sealed class FailingWriter : StringWriter {
            public FailingWriter() : base(CultureInfo.InvariantCulture) {
            }

            public override void Write(char value) {
                throw new IOException("The pipe is closed.");
            }

            public override void Write(string value) {
                throw new IOException("The pipe is closed.");
            }

            public override void WriteLine(string value) {
                throw new IOException("The pipe is closed.");
            }
        }

        [SupportedOSPlatform("windows")]
        private static void AssertOwnerOnlyOnWindows(string path) {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            FileSecurity security = new FileInfo(path).GetAccessControl();
            Assert.AreEqual(user, security.GetOwner(typeof(SecurityIdentifier)));
            Assert.IsTrue(security.AreAccessRulesProtected, "The file inherits access rules.");
            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            Assert.IsTrue(rules.Count > 0);
            foreach (FileSystemAccessRule rule in rules) {
                Assert.AreEqual(user, rule.IdentityReference, "Another identity has a rule.");
            }
        }
    }
}
