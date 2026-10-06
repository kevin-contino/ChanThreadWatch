using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Cli.Tests {
    // MP-7a L2b: ctw add and remove keep api-threads.txt (the marks of the threads added through the local API)
    // consistent with the thread list, as the app does: a removed thread's entry goes, and a thread ctw adds is not
    // one the API added. A file ctw can't read is left as it is (the app then guards every thread).
    [TestClass]
    public class ApiThreadsFileTests : CliTestBase {
        private const string Url1 = "https://boards.4chan.org/a/thread/111";
        private const string Url2 = "https://boards.4chan.org/b/thread/222";

        private string ApiThreadsPath {
            get { return Path.Combine(Folder, Settings.ApiThreadsFileName); }
        }

        private string SettingsPath {
            get { return Path.Combine(Folder, Settings.SettingsFileName); }
        }

        // A settings file as the app leaves one, so a write of api-threads.txt can set ApiThreadsFileWritten in it
        [TestInitialize]
        public void WriteEmptySettings() {
            File.WriteAllBytes(SettingsPath, new byte[0]);
        }

        [TestMethod]
        public void Remove_DropsTheThreadsEntryAndKeepsTheOthers() {
            WriteThreadList(ThreadLines(Url1), ThreadLines(Url2));
            File.WriteAllLines(ApiThreadsPath, new[] { "1", "4chan/a/111", "4chan/b/222", "4chan/z/9" });

            AssertSucceeded(Run("remove", Url2));

            CollectionAssert.AreEqual(new[] { "1", "4chan/a/111", "4chan/z/9" }, File.ReadAllLines(ApiThreadsPath));
        }

        // Fix 7: threads followed from the removed thread (its AddedFrom chain, also through a thread that is not in the
        // list) are marked through its entry, so they get entries of their own before it goes
        [TestMethod]
        public void Remove_KeepsTheMarksOfTheThreadsFollowedFromIt() {
            WriteThreadList(ThreadLines(Url1), Followed("https://boards.4chan.org/a/thread/112", "4chan/a/111"),
                Followed("https://boards.4chan.org/a/thread/113", "4chan/a/112"), Followed("https://boards.4chan.org/a/thread/114", "4chan/a/999"), ThreadLines(Url2));
            File.WriteAllLines(ApiThreadsPath, new[] { "1", "4chan/a/111" });

            AssertSucceeded(Run("remove", Url1));

            CollectionAssert.AreEqual(new[] { "1", "4chan/a/112", "4chan/a/113" }, File.ReadAllLines(ApiThreadsPath));
            ThreadListData data = ReadThreadList();
            WatchSession.MarkGuardedThreads(data.Threads, ApiThreadsFile.Parse(File.ReadAllLines(ApiThreadsPath)), false);
            CollectionAssert.AreEqual(new[] { true, true, false, false }, data.Threads.ConvertAll(thread => thread.Guarded));
        }

        // The same when ctw add drops an entry left for the thread it adds
        [TestMethod]
        public void Add_KeepsTheMarksOfTheThreadsFollowedFromTheDroppedEntry() {
            WriteThreadList(Followed("https://boards.4chan.org/a/thread/112", "4chan/a/111"), ThreadLines(Url2));
            File.WriteAllLines(ApiThreadsPath, new[] { "1", "4chan/a/111" });

            AssertSucceeded(Run("add", Url1));

            CollectionAssert.AreEqual(new[] { "1", "4chan/a/112" }, File.ReadAllLines(ApiThreadsPath));
        }

        // A thread auto-followed from the page ID (AddedFrom)
        private static string[] Followed(string url, string addedFrom) {
            string[] lines = ThreadLines(url);
            lines[10] = addedFrom;
            return lines;
        }

        // Rounds 2 and 3: a write of api-threads.txt by ctw sets ApiThreadsFileWritten=1 in settings.txt itself: every
        // other byte stays, a plaintext login of an older version included (Settings.Save would refuse it)
        [TestMethod]
        [DataRow("CheckEvery=7\r\nPageAuth=user:secret\r\nSomeFutureKey=x\r\n", "CheckEvery=7\r\nPageAuth=user:secret\r\nSomeFutureKey=x\r\nApiThreadsFileWritten=1\r\n")]
        [DataRow("CheckEvery=7\nPageAuth=user:secret", "CheckEvery=7\nPageAuth=user:secret\nApiThreadsFileWritten=1\n")]
        [DataRow("ApiThreadsFileWritten=0\r\nPageAuth=user:secret\r\n", "ApiThreadsFileWritten=1\r\nPageAuth=user:secret\r\n")]
        [DataRow("PageAuth=user:secret\r\nApiThreadsFileWritten=1\r\n", "PageAuth=user:secret\r\nApiThreadsFileWritten=1\r\n")]
        // Round 4: the name in any case (as Settings.Load reads it), and a UTF-8 byte order mark that stays
        [DataRow("apithreadsfilewritten=0\r\nPageAuth=user:secret\r\n", "apithreadsfilewritten=1\r\nPageAuth=user:secret\r\n")]
        [DataRow("﻿CheckEvery=7\r\nPageAuth=user:secret\r\n", "﻿CheckEvery=7\r\nPageAuth=user:secret\r\nApiThreadsFileWritten=1\r\n")]
        [DataRow("﻿ApiThreadsFileWritten=0\r\n", "﻿ApiThreadsFileWritten=1\r\n")]
        public void Remove_WritingTheFileSetsTheSettingInPlace(string before, string after) {
            string settingsPath = Path.Combine(Folder, Settings.SettingsFileName);
            File.WriteAllBytes(settingsPath, System.Text.Encoding.UTF8.GetBytes(before));
            WriteThreadList(ThreadLines(Url1), ThreadLines(Url2));
            File.WriteAllLines(ApiThreadsPath, new[] { "1", "4chan/b/222" });

            AssertSucceeded(Run("remove", Url2));

            CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes(after), File.ReadAllBytes(settingsPath));
            Assert.IsEmpty(Directory.GetFiles(Folder, "settings.txt.corrupt-*"));
        }

        // Round 4: a settings file ctw can't change byte for byte (UTF-16, not valid UTF-8, a lone CR) is left as it is,
        // with a warning; the command still succeeds
        [TestMethod]
        [DataRow(new byte[] { 0xFF, 0xFE, 0x43, 0x00, 0x3D, 0x00, 0x31, 0x00 })]
        [DataRow(new byte[] { 0xFE, 0xFF, 0x00, 0x43, 0x00, 0x3D, 0x00, 0x31 })]
        [DataRow(new byte[] { 0x43, 0x3D, 0xC3, 0x28, 0x0D, 0x0A })]
        [DataRow(new byte[] { 0x43, 0x3D, 0x31, 0x0D, 0x44, 0x3D, 0x32, 0x0D, 0x0A })]
        public void Remove_LeavesASettingsFileItCannotChangeByteForByte(byte[] settings) {
            File.WriteAllBytes(SettingsPath, settings);
            WriteThreadList(ThreadLines(Url1), ThreadLines(Url2));
            File.WriteAllLines(ApiThreadsPath, new[] { "1", "4chan/b/222" });

            CliResult result = Run("remove", Url2);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            StringAssert.StartsWith(result.Error, "ctw: warning: ApiThreadsFileWritten=1 could not be saved in settings.txt (");
            CollectionAssert.AreEqual(settings, File.ReadAllBytes(SettingsPath));
            CollectionAssert.AreEqual(new[] { "1" }, File.ReadAllLines(ApiThreadsPath));
        }

        // Round 4: without settings.txt ctw warns and does not create one
        [TestMethod]
        public void Remove_WithoutASettingsFileWarnsAndCreatesNone() {
            File.Delete(SettingsPath);
            WriteThreadList(ThreadLines(Url1), ThreadLines(Url2));
            File.WriteAllLines(ApiThreadsPath, new[] { "1", "4chan/b/222" });

            CliResult result = Run("remove", Url2);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual("ctw: warning: ApiThreadsFileWritten=1 could not be saved in settings.txt (the file does not exist); until it is, a missing api-threads.txt is read as no thread added through the local API." + Environment.NewLine, result.Error);
            Assert.IsFalse(File.Exists(SettingsPath));
        }

        // Round 3: removing a thread with no entry of its own that links others to a marked thread (A has an entry; R
        // was followed from A and D from R under v1.39) gives the threads that stay entries of their own
        [TestMethod]
        public void Remove_AnIntermediateThreadWithoutAnEntryKeepsTheChainMarked() {
            const string a = "https://boards.4chan.org/a/thread/111";
            const string r = "https://boards.4chan.org/a/thread/112";
            const string d = "https://boards.4chan.org/a/thread/113";
            WriteThreadList(ThreadLines(a), Followed(r, "4chan/a/111"), Followed(d, "4chan/a/112"), ThreadLines(Url2));
            File.WriteAllLines(ApiThreadsPath, new[] { "1", "4chan/a/111" });

            AssertSucceeded(Run("remove", r));

            CollectionAssert.AreEqual(new[] { "1", "4chan/a/111", "4chan/a/113" }, File.ReadAllLines(ApiThreadsPath));
            ThreadListData data = ReadThreadList();
            WatchSession.MarkGuardedThreads(data.Threads, ApiThreadsFile.Parse(File.ReadAllLines(ApiThreadsPath)), false);
            CollectionAssert.AreEqual(new[] { true, true, false }, data.Threads.ConvertAll(thread => thread.Guarded));
        }

        // Removing an unmarked thread leaves the file as it is
        [TestMethod]
        public void Remove_AnUnmarkedThreadLeavesTheFile() {
            WriteThreadList(ThreadLines(Url1), Followed("https://boards.4chan.org/a/thread/112", "4chan/a/111"), ThreadLines(Url2));
            File.WriteAllLines(ApiThreadsPath, new[] { "1", "4chan/a/111" });
            byte[] before = File.ReadAllBytes(ApiThreadsPath);

            AssertSucceeded(Run("remove", Url2));

            CollectionAssert.AreEqual(before, File.ReadAllBytes(ApiThreadsPath));
        }

        // With the setting on and the file missing, ctw leaves things as they are: there is no entry to drop, and no
        // file is made (an empty one would hide the loss from the app, which guards every thread for its session)
        [TestMethod]
        public void AddAndRemove_WithTheFileMissingAfterAWrite_MakeNoFile() {
            File.WriteAllLines(Path.Combine(Folder, Settings.SettingsFileName), new[] { "ApiThreadsFileWritten=1" });

            AssertSucceeded(Run("add", Url1));
            AssertSucceeded(Run("remove", Url1));

            Assert.IsFalse(File.Exists(ApiThreadsPath));
            CollectionAssert.Contains(File.ReadAllLines(Path.Combine(Folder, Settings.SettingsFileName)), "ApiThreadsFileWritten=1");
        }

        [TestMethod]
        public void Remove_ThreadWithoutAnEntry_LeavesTheFileUnchanged() {
            WriteThreadList(ThreadLines(Url1), ThreadLines(Url2));
            File.WriteAllLines(ApiThreadsPath, new[] { "1", "4chan/a/111" });
            byte[] before = File.ReadAllBytes(ApiThreadsPath);

            AssertSucceeded(Run("remove", Url2));

            CollectionAssert.AreEqual(before, File.ReadAllBytes(ApiThreadsPath));
        }

        // An entry left without a thread is for a thread the API added; ctw adding the thread itself drops it
        [TestMethod]
        public void Add_DropsAnEntryForTheSameThread() {
            File.WriteAllLines(ApiThreadsPath, new[] { "1", "4chan/a/111", "4chan/z/9" });

            AssertSucceeded(Run("add", Url1));

            CollectionAssert.AreEqual(new[] { "1", "4chan/z/9" }, File.ReadAllLines(ApiThreadsPath));
            Assert.HasCount(1, ReadThreadList().Threads);
        }

        [TestMethod]
        public void AddAndRemove_WithoutTheFile_CreateNone() {
            AssertSucceeded(Run("add", Url1));
            AssertSucceeded(Run("remove", Url1));

            Assert.IsFalse(File.Exists(ApiThreadsPath));
        }

        // A version ctw does not know, or a file that is not one, is left for the app (which then guards every thread)
        [TestMethod]
        [DataRow("2|4chan/a/111")]
        [DataRow("not a version|4chan/a/111")]
        public void AddAndRemove_LeaveAnUnreadableFileUnchanged(string lines) {
            File.WriteAllLines(ApiThreadsPath, lines.Split('|'));
            byte[] before = File.ReadAllBytes(ApiThreadsPath);

            AssertSucceeded(Run("add", Url1));
            AssertSucceeded(Run("remove", Url1));

            CollectionAssert.AreEqual(before, File.ReadAllBytes(ApiThreadsPath));
        }

        // ctw add's thread is never guarded by an entry that was left: after the add, the app's load marks nothing
        [TestMethod]
        public void Add_ThreadLoadsUnguardedInTheApp() {
            File.WriteAllLines(ApiThreadsPath, new[] { "1", "4chan/a/111" });
            AssertSucceeded(Run("add", Url1));
            ThreadListData data = ReadThreadList();

            WatchSession.MarkGuardedThreads(data.Threads, ApiThreadsFile.Parse(File.ReadAllLines(ApiThreadsPath)), false);

            Assert.IsFalse(data.Threads[0].Guarded);
        }
    }
}
