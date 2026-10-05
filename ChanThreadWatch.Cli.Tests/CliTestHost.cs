using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// ctw sets process-wide state (Settings, StoredAuth.Protector, General.HostVersion), so the tests run one at a time
[assembly: DoNotParallelize]

namespace JDP.Cli.Tests {
    // Every test runs ctw in this process against its own temporary settings folder, never the app's folder
    [TestClass]
    public static class CliTestHost {
        [AssemblyInitialize]
        public static void Initialize(TestContext context) {
            // The default folders (Documents) and the application data folder go to a temporary folder, so a fallback
            // to a default never reaches the user's folders
            JDP.Tests.TestDefaultFolders.Redirect();
            // Nothing here asks for the app's settings folder; if anything logged, the log would go next to the
            // test binaries rather than to AppData
            Settings.UseExeDirectoryForSettings = true;
            // The log's path is taken once, by the first log; ctw watch points the settings folder at a test's temporary
            // folder, so the log is opened here first and never holds a file open in a folder a test deletes
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Logger).TypeHandle);
            // ThreadListFile.Parse in the tests never asks DPAPI or a login store either (ctw sets the same)
            StoredAuth.Protector = new KeptStoredAuthProtector();
        }

        [AssemblyCleanup]
        public static void Cleanup() {
            JDP.Tests.TestDefaultFolders.Delete();
        }
    }

    public sealed class CliResult {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }

        public override string ToString() {
            return "exit " + ExitCode + ", stdout: " + Output + ", stderr: " + Error;
        }
    }

    public abstract class CliTestBase {
        protected string Folder { get; private set; }

        protected string ThreadListPath {
            get { return Path.Combine(Folder, Settings.ThreadsFileName); }
        }

        [TestInitialize]
        public void CreateSettingsFolder() {
            Folder = Path.Combine(Path.GetTempPath(), "ctw-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder);
        }

        [TestCleanup]
        public void DeleteSettingsFolder() {
            Directory.Delete(Folder, true);
        }

        protected CliResult Run(params string[] args) {
            return RunIn(Folder, args);
        }

        protected static CliResult RunIn(string folder, params string[] args) {
            StringWriter output = new StringWriter(CultureInfo.InvariantCulture);
            StringWriter error = new StringWriter(CultureInfo.InvariantCulture);
            int exitCode = CliApp.Run(args, output, error, folder);
            return new CliResult { ExitCode = exitCode, Output = output.ToString(), Error = error.ToString() };
        }

        protected static void AssertSucceeded(CliResult result) {
            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual(String.Empty, result.Error, result.ToString());
        }

        // A failure is one line on stderr starting with "ctw: ", and nothing on stdout
        protected static void AssertFailed(CliResult result, int exitCode, string messagePart) {
            Assert.AreEqual(exitCode, result.ExitCode, result.ToString());
            Assert.AreEqual(String.Empty, result.Output, result.ToString());
            Assert.IsTrue(result.Error.StartsWith("ctw: ", StringComparison.Ordinal), result.ToString());
            Assert.AreEqual(1, result.Error.TrimEnd().Split('\n').Length, result.ToString());
            StringAssert.Contains(result.Error, messagePart);
        }

        protected static string[] Lines(string text) {
            return text.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
        }

        // The 13 lines of a thread in thread list version 4 (see ThreadListFile)
        protected static string[] ThreadLines(string url, string pageAuth = "", string imageAuth = "", string description = "", string category = "", string saveDir = "") {
            return new[] {
                url, pageAuth, imageAuth, "300", "0", saveDir, "", description,
                new DateTime(2020, 1, 15, 12, 30, 0, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture), "", "", category, "1"
            };
        }

        // Written like the app writes it, so the bytes of a thread written back unchanged are the same
        protected void WriteThreadList(params string[][] threads) {
            List<string> lines = new List<string> { ThreadListFile.CurrentVersion.ToString(CultureInfo.InvariantCulture) };
            foreach (string[] thread in threads) {
                lines.AddRange(thread);
            }
            TextFile.WriteAllLinesAtomic(ThreadListPath, lines);
        }

        protected ThreadListData ReadThreadList() {
            return ThreadListFile.Parse(File.ReadAllLines(ThreadListPath));
        }
    }
}
