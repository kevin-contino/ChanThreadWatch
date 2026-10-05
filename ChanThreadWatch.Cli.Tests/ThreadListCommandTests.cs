using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Cli.Tests {
    [TestClass]
    public class ThreadListCommandTests : CliTestBase {
        private const string Url1 = "https://boards.4chan.org/a/thread/111";
        private const string Url2 = "https://boards.4chan.org/b/thread/222";

        [TestMethod]
        public void List_WithoutThreadList_PrintsNothing() {
            CliResult result = Run("list");

            AssertSucceeded(result);
            Assert.AreEqual(String.Empty, result.Output);
            Assert.IsFalse(File.Exists(ThreadListPath));
        }

        [TestMethod]
        public void AddThenList_PrintsTheThreadsInFileOrder() {
            AssertSucceeded(Run("add", Url2, "--description", "Second", "--category", "Cat"));
            AssertSucceeded(Run("add", Url1));
            AssertSucceeded(Run("add", "https://example.com/forum/thread/3", "--description", "Only a description"));

            CliResult result = Run("list");

            AssertSucceeded(result);
            CollectionAssert.AreEqual(new[] {
                Url2 + "\tCat\tSecond",
                Url1,
                "https://example.com/forum/thread/3\t\tOnly a description"
            }, Lines(result.Output));
        }

        [TestMethod]
        public void Add_PrintsTheCleanedUrl() {
            CliResult result = Run("add", "  boards.4chan.org/a/thread/111#p5  ");

            AssertSucceeded(result);
            Assert.AreEqual("Added http://boards.4chan.org/a/thread/111 (settings folder: " + Folder + ")" + Environment.NewLine, result.Output);
            Assert.AreEqual("http://boards.4chan.org/a/thread/111", ReadThreadList().Threads[0].URL);
        }

        // MP-6b acceptance: an entry ctw writes reads back through ThreadListFile as written, and as the app's Add
        // button creates it (frmChanThreadWatch.AddThread) with the settings' defaults
        [TestMethod]
        public void Add_EntryReadsBackIdenticalThroughThreadListFile() {
            DateTime before = DateTime.Now;
            AssertSucceeded(Run("add", Url1, "--description", "Desc", "--category", "Cat"));

            string[] lines = File.ReadAllLines(ThreadListPath);
            ThreadListData data = ThreadListFile.Parse(lines);
            Assert.AreEqual(ThreadListFile.CurrentVersion, data.FileVersion);
            Assert.AreEqual(0, data.TrailingLineCount);
            Assert.HasCount(1, data.Threads);
            ThreadInfo thread = data.Threads[0];
            Assert.AreEqual(Url1, thread.URL);
            Assert.AreEqual(String.Empty, thread.PageAuth);
            Assert.AreEqual(String.Empty, thread.ImageAuth);
            Assert.AreEqual(3 * 60, thread.CheckIntervalSeconds);
            Assert.IsFalse(thread.OneTimeDownload);
            Assert.AreEqual(String.Empty, thread.SaveDir);
            Assert.IsNull(thread.StopReason);
            Assert.AreEqual("Desc", thread.Description);
            Assert.AreEqual("Cat", thread.Category);
            Assert.IsFalse(thread.AutoFollow);
            Assert.AreEqual(String.Empty, thread.ExtraData.AddedFrom);
            Assert.IsNull(thread.ExtraData.LastImageOn);
            Assert.IsTrue(thread.ExtraData.AddedOn >= before.AddSeconds(-1) && thread.ExtraData.AddedOn <= DateTime.Now.AddSeconds(1), thread.ExtraData.AddedOn.ToString("o"));
            CollectionAssert.AreEqual(lines, ThreadListFile.Serialize(data.Threads));
        }

        [TestMethod]
        [DataRow("CheckEvery=10\r\nAutoFollow=1\r\n", 600, false, true)]
        [DataRow("CheckEvery=1\r\n", 0, false, false)]
        [DataRow("CheckEvery=10\r\nOneTimeDownload=1\r\n", 0, true, false)]
        public void Add_UsesTheAppDefaultsFromTheSettings(string settings, int checkIntervalSeconds, bool oneTimeDownload, bool autoFollow) {
            File.WriteAllText(Path.Combine(Folder, Settings.SettingsFileName), settings);

            AssertSucceeded(Run("add", Url1));

            ThreadInfo thread = ReadThreadList().Threads[0];
            Assert.AreEqual(checkIntervalSeconds, thread.CheckIntervalSeconds);
            Assert.AreEqual(oneTimeDownload, thread.OneTimeDownload);
            Assert.AreEqual(autoFollow, thread.AutoFollow);
        }

        [TestMethod]
        [DataRow(Url1)]
        [DataRow("http://boards.4chan.org/a/thread/111")]
        [DataRow("https://boards.4chan.org/a/thread/111/some-slug#p1")]
        public void Add_SameThreadAgain_IsRefusedAndLeavesTheFileUnchanged(string sameThread) {
            AssertSucceeded(Run("add", Url1));
            byte[] before = File.ReadAllBytes(ThreadListPath);

            CliResult result = Run("add", sameThread, "--description", "again");

            AssertFailed(result, CliApp.ExitFailure, "The thread is already in the thread list.");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(ThreadListPath));
        }

        [TestMethod]
        public void Add_BlacklistedThread_IsRefused() {
            File.WriteAllLines(Path.Combine(Folder, Settings.BlacklistFileName), new[] { ThreadUrl.GetPageID(Url1) });

            CliResult result = Run("add", Url1);

            AssertFailed(result, CliApp.ExitFailure, "The thread is blacklisted.");
            Assert.IsFalse(File.Exists(ThreadListPath));
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("not a url")]
        [DataRow("https://example.com")]
        [DataRow("javascript:alert(1)")]
        public void Add_InvalidUrl_FailsWithoutWriting(string url) {
            CliResult result = Run("add", url);

            AssertFailed(result, CliApp.ExitFailure, "Invalid URL");
            Assert.IsFalse(File.Exists(ThreadListPath));
            Assert.IsFalse(File.Exists(SettingsFolderLock.GetLockPath(Folder)), "the URL is checked before the lock is taken");
        }

        [TestMethod]
        public void Remove_RemovesOnlyThatThreadAndKeepsTheOthersAsWritten() {
            string[] first = ThreadLines(Url1, description: "First", category: "A");
            string[] third = ThreadLines("https://example.com/forum/thread/3", description: "Third");
            WriteThreadList(first, ThreadLines(Url2), third);

            CliResult result = Run("remove", "http://boards.4chan.org/b/thread/222#p9");

            AssertSucceeded(result);
            // The entry's own URL, not the one given
            Assert.AreEqual("Removed " + Url2 + " (settings folder: " + Folder + ")" + Environment.NewLine, result.Output);
            string[] expected = new string[1 + first.Length + third.Length];
            expected[0] = ThreadListFile.CurrentVersion.ToString();
            first.CopyTo(expected, 1);
            third.CopyTo(expected, 1 + first.Length);
            CollectionAssert.AreEqual(expected, File.ReadAllLines(ThreadListPath));
        }

        [TestMethod]
        public void Remove_KeepsTheThreadFolder() {
            string threadFolder = Path.Combine(Folder, "downloads", "a_111");
            Directory.CreateDirectory(threadFolder);
            File.WriteAllText(Path.Combine(threadFolder, "image.jpg"), "x");
            WriteThreadList(ThreadLines(Url1, saveDir: threadFolder));

            AssertSucceeded(Run("remove", Url1));

            Assert.IsTrue(File.Exists(Path.Combine(threadFolder, "image.jpg")));
            Assert.HasCount(0, ReadThreadList().Threads);
        }

        [TestMethod]
        public void Remove_ThreadNotInTheList_FailsAndLeavesTheFileUnchanged() {
            WriteThreadList(ThreadLines(Url1));
            byte[] before = File.ReadAllBytes(ThreadListPath);

            CliResult result = Run("remove", Url2);

            AssertFailed(result, CliApp.ExitFailure, "The thread is not in the thread list: " + Url2);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(ThreadListPath));
        }

        [TestMethod]
        public void Remove_WithoutThreadList_Fails() {
            CliResult result = Run("remove", Url1);

            AssertFailed(result, CliApp.ExitFailure, "The thread is not in the thread list");
            Assert.IsFalse(File.Exists(ThreadListPath));
        }

        [TestMethod]
        public void ThreadListThatDoesNotLoad_IsNeitherListedNorChanged() {
            File.WriteAllLines(ThreadListPath, new[] { "99", Url1 });
            byte[] before = File.ReadAllBytes(ThreadListPath);

            AssertFailed(Run("list"), CliApp.ExitFailure, "threads.txt could not be read");
            AssertFailed(Run("add", Url2), CliApp.ExitFailure, "threads.txt could not be read");
            AssertFailed(Run("remove", Url1), CliApp.ExitFailure, "threads.txt could not be read");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(ThreadListPath));
        }

        [TestMethod]
        public void IncompleteThreadList_IsListedWithAWarningButNotChanged() {
            WriteThreadList(ThreadLines(Url1));
            File.AppendAllLines(ThreadListPath, new[] { Url2, "", "" });
            byte[] before = File.ReadAllBytes(ThreadListPath);

            CliResult list = Run("list");
            Assert.AreEqual(CliApp.ExitSuccess, list.ExitCode, list.ToString());
            Assert.AreEqual(Url1 + Environment.NewLine, list.Output);
            StringAssert.StartsWith(list.Error, "ctw: warning: the end of threads.txt is incomplete");

            AssertFailed(Run("add", "https://example.com/forum/thread/3"), CliApp.ExitFailure, "is incomplete, so ctw does not change it");
            AssertFailed(Run("remove", Url1), CliApp.ExitFailure, "is incomplete, so ctw does not change it");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(ThreadListPath));
        }

        // Descriptions come from thread titles, so a page could put terminal escape sequences in them
        [TestMethod]
        public void List_ShowsControlCharactersAsSpaces() {
            WriteThreadList(ThreadLines(Url1, description: "Title\u001b[2J\u0007\tend", category: "C\u009bat"));

            CliResult result = Run("list");

            AssertSucceeded(result);
            Assert.AreEqual(Url1 + "\tC at\tTitle [2J  end" + Environment.NewLine, result.Output);
        }

        // A blacklist that can't be read must not let a blacklisted thread in (WatchSession.LoadBlacklist would log
        // the failure and go on with an empty blacklist). A folder in its place can't be read on every system.
        [TestMethod]
        public void Add_UnreadableBlacklist_FailsWithoutWriting() {
            Directory.CreateDirectory(Path.Combine(Folder, Settings.BlacklistFileName));

            CliResult result = Run("add", Url1);

            AssertFailed(result, CliApp.ExitFailure, "blacklist.txt could not be read, so ctw does not add the thread");
            Assert.IsFalse(File.Exists(ThreadListPath));
        }

        // The address's login would end up as plaintext in the thread list
        [TestMethod]
        [DataRow("https://name:hunter2pass@boards.4chan.org/a/thread/111")]
        [DataRow("boards.4chan.org@evil.example/a/thread/111")]
        public void Add_UrlWithLogin_IsRefusedWithoutShowingIt(string url) {
            CliResult result = Run("add", url);

            AssertFailed(result, CliApp.ExitFailure, "The URL holds a login (name:password@).");
            Assert.IsFalse(File.Exists(ThreadListPath));
            Assert.IsFalse(result.Error.Contains("hunter2pass", StringComparison.Ordinal) || result.Error.Contains("evil", StringComparison.Ordinal), result.Error);
        }

        // Any URL ctw prints, also one that is not valid, is shown without its login
        [TestMethod]
        [DataRow(new[] { "add", "https://name:hunter2pass@" }, "Invalid URL: https://")]
        [DataRow(new[] { "add", "name:hunter2pass@example.com" }, "Invalid URL: example.com")]
        [DataRow(new[] { "remove", "name:hunter2pass@example.com" }, "Invalid URL: example.com")]
        [DataRow(new[] { "remove", "https://name:hunter2pass@boards.4chan.org/a/thread/9" }, "The thread is not in the thread list: https://boards.4chan.org/a/thread/9")]
        public void UrlWithLogin_IsShownWithoutIt(string[] args, string message) {
            WriteThreadList(ThreadLines(Url1));

            CliResult result = Run(args);

            AssertFailed(result, CliApp.ExitFailure, message);
            Assert.IsFalse(result.Error.Contains("hunter2pass", StringComparison.Ordinal) || result.Error.Contains("name:", StringComparison.Ordinal), result.Error);
        }

        [TestMethod]
        public void StoredUrlsWithLogins_AreListedAndNamedWithoutThem() {
            const string First = "https://name:hunter2pass@boards.example.com/a/thread/5";
            const string Second = "https://other:s3cret@www.example.org/a/thread/5";
            WriteThreadList(ThreadLines(First), ThreadLines(Second));

            CliResult list = Run("list");
            CliResult remove = Run("remove", "https://www.example.org/a/thread/5");

            AssertSucceeded(list);
            Assert.AreEqual("https://boards.example.com/a/thread/5" + Environment.NewLine + "https://www.example.org/a/thread/5" + Environment.NewLine, list.Output);
            AssertFailed(remove, CliApp.ExitFailure, "ctw removes none: https://boards.example.com/a/thread/5, https://www.example.org/a/thread/5.");
            Assert.IsFalse(remove.Error.Contains("hunter2pass", StringComparison.Ordinal) || remove.Error.Contains("s3cr", StringComparison.Ordinal), remove.Error);
        }

        [TestMethod]
        [DataRow("https://boards.4chan.org/a/thread/1", "https://boards.4chan.org/a/thread/1")]
        [DataRow("https://u:p@host/a@b", "https://host/a@b")]
        [DataRow("https://u:p@ss@host/x", "https://host/x")]
        [DataRow("u:p@host/x", "host/x")]
        [DataRow("https://host/a@b", "https://host/a@b")]
        public void CleanUrl_LeavesOutTheLogin(string url, string expected) {
            Assert.AreEqual(expected, ConsoleText.CleanUrl(url));
        }

        // Settings.Load would log the failure, copy the file aside and go on with the built-in defaults
        [TestMethod]
        public void Add_UnreadableSettings_FailsWithoutWritingOrCopyingAnything() {
            string settingsPath = Path.Combine(Folder, Settings.SettingsFileName);
            File.WriteAllText(settingsPath, "CheckEvery=10\r\n");
            using (new FileStream(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                CliResult result = Run("add", Url1);

                AssertFailed(result, CliApp.ExitFailure, "settings.txt could not be read, so ctw does not add the thread");
            }
            CollectionAssert.AreEquivalent(new[] { Settings.SettingsFileName, SettingsFolderLock.FileName },
                Array.ConvertAll(Directory.GetFileSystemEntries(Folder), Path.GetFileName));
        }

        // A thread list that is locked for a moment (another program reading or swapping it) is read again
        [TestMethod]
        public void List_ThreadListLockedForAMoment_IsReadAgain() {
            WriteThreadList(ThreadLines(Url1));
            FileStream locked = new FileStream(ThreadListPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            System.Threading.Tasks.Task release = System.Threading.Tasks.Task.Delay(100).ContinueWith(_ => locked.Dispose());

            CliResult result = Run("list");

            release.Wait();
            AssertSucceeded(result);
            Assert.AreEqual(Url1 + Environment.NewLine, result.Output);
        }

        // Two entries of different hosts can be the same thread for the app (the generic site helper's page ID is
        // the second-level domain, board and thread), so ctw does not pick one
        [TestMethod]
        public void Remove_TwoEntriesOfTheSameThread_RemovesNoneAndListsThem() {
            const string First = "https://boards.example.com/a/thread/5";
            const string Second = "https://www.example.org/a/thread/5";
            Assert.AreEqual(ThreadUrl.GetPageID(First), ThreadUrl.GetPageID(Second));
            WriteThreadList(ThreadLines(First), ThreadLines(Second));
            byte[] before = File.ReadAllBytes(ThreadListPath);

            CliResult result = Run("remove", Second);

            AssertFailed(result, CliApp.ExitFailure, "2 entries are this thread, so ctw removes none: " + First + ", " + Second + ".");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(ThreadListPath));
        }

        // A thread list that another program keeps open without sharing is an error, not an empty list
        [TestMethod]
        public void List_ThreadListOpenWithoutSharing_Fails() {
            WriteThreadList(ThreadLines(Url1));
            using (new FileStream(ThreadListPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                CliResult result = Run("list");

                Assert.AreEqual(CliApp.ExitFailure, result.ExitCode, result.ToString());
                Assert.AreEqual(String.Empty, result.Output);
            }
        }

        // list only reads: a settings folder that does not exist (e.g. the application data folder before the app
        // first ran) is not created
        [TestMethod]
        public void List_MissingSettingsFolder_PrintsNothingAndCreatesNothing() {
            string missing = Path.Combine(Folder, "missing");

            CliResult result = RunIn(missing, "list");

            AssertSucceeded(result);
            Assert.AreEqual(String.Empty, result.Output);
            Assert.IsFalse(Directory.Exists(missing));
        }

        [TestMethod]
        public void Remove_MissingSettingsFolder_FailsWithoutCreatingIt() {
            string missing = Path.Combine(Folder, "missing");

            AssertFailed(RunIn(missing, "remove", Url1), CliApp.ExitFailure, "there is no settings folder " + missing);
            Assert.IsFalse(Directory.Exists(missing));
        }

        [TestMethod]
        public void Add_MissingSettingsFolder_FailsWithoutCreatingIt() {
            string missing = Path.Combine(Folder, "missing");

            CliResult result = RunIn(missing, "add", Url1);

            AssertFailed(result, CliApp.ExitFailure, "Cannot write to the settings folder " + missing);
            Assert.IsFalse(Directory.Exists(missing));
        }
    }
}
