using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Cli.Tests {
    [TestClass]
    public class CommandLineTests : CliTestBase {
        [TestMethod]
        [DataRow("--help")]
        [DataRow("-h")]
        public void Help_PrintsUsageAndSucceeds(string option) {
            CliResult result = Run(option);

            AssertSucceeded(result);
            StringAssert.StartsWith(result.Output, "Usage: ctw <command> [options]");
            StringAssert.Contains(result.Output, "add <url> [--description <text>] [--category <text>]");
        }

        [TestMethod]
        public void NoArguments_PrintsUsageAsAnError() {
            CliResult result = Run();

            Assert.AreEqual(CliApp.ExitUsage, result.ExitCode, result.ToString());
            Assert.AreEqual(String.Empty, result.Output);
            StringAssert.StartsWith(result.Error, "Usage: ctw <command> [options]");
        }

        [TestMethod]
        [DataRow(new[] { "watch" }, "Unknown command 'watch'.")]
        [DataRow(new[] { "add" }, "Wrong arguments. Usage: ctw add <url>")]
        [DataRow(new[] { "add", "https://boards.4chan.org/a/thread/1", "https://boards.4chan.org/a/thread/2" }, "Wrong arguments.")]
        [DataRow(new[] { "add", "https://boards.4chan.org/a/thread/1", "--title", "x" }, "Unknown option '--title'.")]
        [DataRow(new[] { "add", "https://boards.4chan.org/a/thread/1", "--category" }, "Option '--category' needs a value")]
        [DataRow(new[] { "add", "https://boards.4chan.org/a/thread/1", "--description", "--category", "x" }, "Option '--description' needs a value")]
        [DataRow(new[] { "add", "https://boards.4chan.org/a/thread/1", "--description", "--" }, "Option '--description' needs a value")]
        [DataRow(new[] { "add", "https://boards.4chan.org/a/thread/1", "-x" }, "Unknown option '-x'.")]
        [DataRow(new[] { "add", "https://boards.4chan.org/a/thread/1", "--category", "a", "--category", "b" }, "Option '--category' is given more than once.")]
        [DataRow(new[] { "remove", "https://boards.4chan.org/a/thread/1", "--description", "x" }, "Unknown option '--description'.")]
        [DataRow(new[] { "list", "extra" }, "Wrong arguments. Usage: ctw list")]
        [DataRow(new[] { "--version", "list" }, "'--version' takes no other arguments.")]
        public void InvalidCommandLine_FailsWithUsageExitCode(string[] args, string message) {
            CliResult result = Run(args);

            AssertFailed(result, CliApp.ExitUsage, message);
            StringAssert.Contains(result.Error, "Run 'ctw --help' for usage.");
            Assert.IsFalse(File.Exists(ThreadListPath));
        }

        [TestMethod]
        [DataRow(new[] { "add", "--help" }, "Usage: ctw add <url> [--description <text>] [--category <text>]")]
        [DataRow(new[] { "add", "https://boards.4chan.org/a/thread/1", "-h" }, "Usage: ctw add <url>")]
        [DataRow(new[] { "remove", "--help" }, "Usage: ctw remove <url>")]
        [DataRow(new[] { "list", "-h" }, "Usage: ctw list")]
        public void CommandHelp_PrintsThatCommandsUsageAndChangesNothing(string[] args, string usage) {
            CliResult result = Run(args);

            AssertSucceeded(result);
            StringAssert.StartsWith(result.Output, usage);
            Assert.IsFalse(File.Exists(ThreadListPath));
        }

        [TestMethod]
        public void Help_NamesTheSettingsFolder() {
            CliResult result = Run("--help");

            AssertSucceeded(result);
            StringAssert.Contains(result.Output, "Settings folder: " + Folder + " (portable)");
            StringAssert.Contains(result.Output, "on this computer");
        }

        [TestMethod]
        public void DoubleDash_EndsTheOptions() {
            AssertSucceeded(Run("add", "--description", "D", "--", "https://boards.4chan.org/a/thread/1"));
            Assert.AreEqual("D", ReadThreadList().Threads[0].Description);

            // After "--", "--help" is the URL, not the help option
            AssertFailed(Run("add", "--", "--help"), CliApp.ExitFailure, "Invalid URL: --help");
        }

        [TestMethod]
        public void Options_MayComeBeforeTheUrl() {
            CliResult result = Run("add", "--category", "Cat", "https://boards.4chan.org/a/thread/1");

            AssertSucceeded(result);
            Assert.AreEqual("Cat", ReadThreadList().Threads[0].Category);
        }

        // G13: one version for every app, read from the WinForms app's AssemblyInfo.cs at build time
        [TestMethod]
        public void Version_IsTheWinFormsAppVersion() {
            string assemblyInfo = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WinFormsAssemblyInfo.cs.txt"));
            Match match = Regex.Match(assemblyInfo, @"(?m)^\[assembly: AssemblyVersion\(""(\d+)\.(\d+)\.(\d+)\.(\d+)""\)\]");
            Assert.IsTrue(match.Success, "AssemblyVersion not found in the WinForms AssemblyInfo.cs");
            string fourPart = String.Join(".", match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value);
            // Major.Minor.Revision, as the app shows it and as the release tag is named
            string expected = match.Groups[1].Value + "." + match.Groups[2].Value + "." + match.Groups[4].Value;

            CliResult result = Run("--version");

            AssertSucceeded(result);
            Assert.AreEqual(expected + Environment.NewLine, result.Output);
            Assembly ctw = typeof(CliApp).Assembly;
            Assert.AreEqual(new Version(fourPart), ctw.GetName().Version);
            Assert.AreEqual(fourPart, FileVersionInfo.GetVersionInfo(ctw.Location).FileVersion);
            Assert.AreEqual(fourPart, ctw.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion);
            Assert.AreEqual(new Version(fourPart), General.HostVersion);
        }
    }
}
