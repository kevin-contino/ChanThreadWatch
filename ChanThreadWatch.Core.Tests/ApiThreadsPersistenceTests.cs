using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // MP-7a L2b: the mark of the threads added through the local API lives in api-threads.txt beside threads.txt,
    // which stays byte for byte as the previous release (v1.39.0) writes and reads it. Work for the owner's thread
    // runs inline, the settings folder is a temporary one, and every thread is stopped, so nothing is downloaded.
    [TestClass]
    public class ApiThreadsPersistenceTests {
        private static readonly DateTime AddedOn = new DateTime(2020, 1, 15, 12, 30, 0, DateTimeKind.Local);
        private const string Url1 = "https://boards.4chan.org/a/thread/1";
        private const string Url2 = "https://boards.4chan.org/a/thread/2";
        private const string Url3 = "https://boards.4chan.org/b/thread/3";
        private const string Url5 = "https://boards.4chan.org/a/thread/5";
        private string _dir;

        [ClassInitialize]
        public static void UseExeDirectoryForLog(TestContext context) {
            // Logger writes next to the test binaries instead of AppData
            Settings.UseExeDirectoryForSettings = true;
        }

        [TestInitialize]
        public void Setup() {
            _dir = NewFolder();
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Settings.DownloadFolder = Path.Combine(_dir, "downloads");
            Settings.DownloadFolderIsRelative = false;
            // Skips the one-time migration, which would save the settings and restart threads
            Settings.ChildThreadsAreNewFormat = true;
            _logStart = ReadWholeLog().Length;
        }

        private int _logStart;

        [TestCleanup]
        public void Cleanup() {
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Directory.Delete(_dir, true);
        }

        private string NewFolder() {
            string dir = Path.Combine(Path.GetTempPath(), "ctw-api-threads-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private string ThreadListPath {
            get { return Path.Combine(_dir, Settings.ThreadsFileName); }
        }

        private string ApiThreadsPath {
            get { return Path.Combine(_dir, Settings.ApiThreadsFileName); }
        }

        private WatchSession CreateSession(string dir = null) {
            return new WatchSession(a => a(), a => a(), dir ?? _dir);
        }

        // Stopped by the user, so neither AddThread nor a load starts it
        private static ThreadInfo StoppedThread(string url, bool guarded, string addedFrom = "") {
            return new ThreadInfo {
                URL = url, PageAuth = String.Empty, ImageAuth = String.Empty, CheckIntervalSeconds = 600, SaveDir = null,
                Description = "D", StopReason = StopReason.UserRequest, Category = String.Empty,
                ExtraData = new WatcherExtraData { AddedOn = AddedOn, AddedFrom = addedFrom }, Guarded = guarded
            };
        }

        // threads.txt lines (version 4) of a stopped thread
        private static string[] ThreadLines(string url, string addedFrom = "") {
            return new[] { url, "", "", "600", "0", "", "3", "D", AddedOn.ToUniversalTime().Ticks.ToString(), "", addedFrom, "", "0" };
        }

        private void WriteThreadList(params string[][] threads) {
            List<string> lines = new List<string> { "4" };
            foreach (string[] thread in threads) lines.AddRange(thread);
            File.WriteAllLines(ThreadListPath, lines);
        }

        private void WriteApiThreads(params string[] lines) {
            File.WriteAllLines(ApiThreadsPath, lines);
        }

        private string[] ReadApiThreads() {
            return File.ReadAllLines(ApiThreadsPath);
        }

        private static ThreadWatcher GetWatcher(WatchSession session, string pageID) {
            ThreadWatcher watcher;
            Assert.IsTrue(session.TryGetThreadWatcher(pageID, out watcher), pageID);
            return watcher;
        }

        private WatchSession Load() {
            WatchSession session = CreateSession();
            session.LoadThreadList();
            return session;
        }

        // Stop condition: threads.txt is the same with and without guarded threads, and loads in v1.39.0's format
        [TestMethod]
        public void TheThreadListIsByteIdenticalWithAndWithoutGuardedThreads() {
            string otherDir = NewFolder();
            try {
                WatchSession guarded = Load();
                Assert.IsTrue(guarded.AddThread(StoppedThread(Url1, true)));
                Assert.IsTrue(guarded.AddThread(StoppedThread(Url2, false)));
                Assert.IsTrue(guarded.SaveThreadList());
                WatchSession plain = CreateSession(otherDir);
                plain.LoadThreadList();
                Assert.IsTrue(plain.AddThread(StoppedThread(Url1, false)));
                Assert.IsTrue(plain.AddThread(StoppedThread(Url2, false)));
                Assert.IsTrue(plain.SaveThreadList());

                CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(otherDir, Settings.ThreadsFileName)), File.ReadAllBytes(ThreadListPath));
                string[] lines = File.ReadAllLines(ThreadListPath);
                Assert.AreEqual("4", lines[0]);
                Assert.HasCount(1 + 2 * ThreadListFile.GetLinesPerThread(4), lines);
                Assert.HasCount(2, ThreadListFile.Parse(lines).Threads);
                CollectionAssert.AreEqual(new[] { "1", "4chan/a/1" }, ReadApiThreads());
                Assert.IsFalse(File.Exists(Path.Combine(otherDir, Settings.ApiThreadsFileName)), "no file until a thread is added through the API");
            }
            finally {
                Directory.Delete(otherDir, true);
            }
        }

        // The marks are written first: if threads.txt then fails, a new API thread is still marked
        [TestMethod]
        public void TheMarksAreWrittenBeforeTheThreadList() {
            WatchSession session = Load();
            Assert.IsTrue(session.SaveThreadList());
            Assert.IsTrue(session.AddThread(StoppedThread(Url1, true)));
            File.Delete(ThreadListPath);
            // A folder where the file goes makes its write fail
            Directory.CreateDirectory(ThreadListPath);

            Assert.IsFalse(session.SaveThreadList());

            CollectionAssert.AreEqual(new[] { "1", "4chan/a/1" }, ReadApiThreads());
        }

        // A failed write of the marks leaves threads.txt as it was
        [TestMethod]
        public void AFailedWriteOfTheMarksDoesNotWriteTheThreadList() {
            WatchSession session = Load();
            Assert.IsTrue(session.SaveThreadList());
            byte[] before = File.ReadAllBytes(ThreadListPath);
            Assert.IsTrue(session.AddThread(StoppedThread(Url1, true)));
            Directory.CreateDirectory(ApiThreadsPath);

            Assert.IsFalse(session.SaveThreadList());

            CollectionAssert.AreEqual(before, File.ReadAllBytes(ThreadListPath));
        }

        [TestMethod]
        public void TheMarkSurvivesARestart() {
            WatchSession first = Load();
            Assert.IsTrue(first.AddThread(StoppedThread(Url1, true)));
            Assert.IsTrue(first.AddThread(StoppedThread(Url3, false)));
            Assert.IsTrue(first.SaveThreadList());

            WatchSession second = Load();

            Assert.IsTrue(GetWatcher(second, "4chan/a/1").Guarded);
            Assert.IsFalse(GetWatcher(second, "4chan/b/3").Guarded);
        }

        // v1.39.0 never opens api-threads.txt and writes threads.txt without the mark; children it followed from a
        // guarded thread carry only AddedFrom, which marks them again (the whole chain, and a cycle ends)
        [TestMethod]
        public void TheAddedFromChainMarksThreadsAgain() {
            WriteThreadList(ThreadLines(Url1), ThreadLines(Url2, "4chan/a/1"), ThreadLines(Url5, "4chan/a/2"), ThreadLines(Url3),
                ThreadLines("https://boards.4chan.org/c/thread/7", "4chan/c/8"), ThreadLines("https://boards.4chan.org/c/thread/8", "4chan/c/7"),
                ThreadLines("https://boards.4chan.org/d/thread/9", "4chan/z/99"));
            WriteApiThreads("1", "4chan/a/1", "4chan/z/99");

            WatchSession session = Load();

            Assert.IsTrue(GetWatcher(session, "4chan/a/1").Guarded);
            Assert.IsTrue(GetWatcher(session, "4chan/a/2").Guarded);
            Assert.IsTrue(GetWatcher(session, "4chan/a/5").Guarded);
            Assert.IsFalse(GetWatcher(session, "4chan/b/3").Guarded);
            Assert.IsFalse(GetWatcher(session, "4chan/c/7").Guarded);
            Assert.IsFalse(GetWatcher(session, "4chan/c/8").Guarded);
            // Followed from a thread that is no longer in the list, whose entry is kept
            Assert.IsTrue(GetWatcher(session, "4chan/d/9").Guarded);
            Assert.IsTrue(session.SaveThreadList());
            CollectionAssert.AreEqual(new[] { "1", "4chan/a/1", "4chan/a/2", "4chan/a/5", "4chan/d/9", "4chan/z/99" }, ReadApiThreads());
        }

        // A thread followed at run time from a guarded thread is guarded, also when its own info has no mark
        [TestMethod]
        public void AThreadAddedFromAGuardedThreadIsGuarded() {
            WatchSession session = Load();
            Assert.IsTrue(session.AddThread(StoppedThread(Url1, true)));

            Assert.IsTrue(session.AddThread(StoppedThread(Url2, false, "4chan/a/1")));

            Assert.IsTrue(GetWatcher(session, "4chan/a/2").Guarded);
        }

        // An entry without a thread is kept (fail closed); a removed thread's entry goes, and so does an entry whose
        // thread is added again without the mark
        [TestMethod]
        public void OrphanEntriesAreKeptUntilTheirThreadIsRemovedOrAddedUnguarded() {
            WriteThreadList(ThreadLines(Url1), ThreadLines(Url2));
            WriteApiThreads("1", "4chan/a/1", "4chan/a/2", "4chan/z/98", "4chan/z/99");
            WatchSession session = Load();

            Assert.IsTrue(session.SaveThreadList());
            CollectionAssert.AreEqual(new[] { "1", "4chan/a/1", "4chan/a/2", "4chan/z/98", "4chan/z/99" }, ReadApiThreads());

            session.RemoveThreads(new[] { GetWatcher(session, "4chan/a/2") });
            Assert.IsTrue(session.AddThread(StoppedThread("https://boards.4chan.org/z/thread/99", false)));
            Assert.IsTrue(session.SaveThreadList());

            CollectionAssert.AreEqual(new[] { "1", "4chan/a/1", "4chan/z/98" }, ReadApiThreads());
            Assert.IsFalse(GetWatcher(session, "4chan/z/99").Guarded);
            // Removed and added again through the API: marked again
            Assert.IsTrue(session.AddThread(StoppedThread(Url2, true)));
            Assert.IsTrue(session.SaveThreadList());
            CollectionAssert.AreEqual(new[] { "1", "4chan/a/1", "4chan/a/2", "4chan/z/98" }, ReadApiThreads());
        }

        // The marks are read before threads.txt, so a thread list that does not load keeps them
        [TestMethod]
        public void AThreadListThatDoesNotLoadKeepsTheMarks() {
            File.WriteAllLines(ThreadListPath, new[] { "not a version" });
            WriteApiThreads("1", "4chan/a/1");
            WatchSession session = Load();

            Assert.IsTrue(session.SaveThreadList());

            CollectionAssert.AreEqual(new[] { "1", "4chan/a/1" }, ReadApiThreads());
        }

        // Round 2: the settings say api-threads.txt was written, and it is missing: like a file that can't be used,
        // every loaded thread (and those followed from them) is guarded for this session only. It is not locked, so
        // the marks this session makes are saved, and no empty file hides it before that.
        [TestMethod]
        public void AMissingFileThatWasWrittenGuardsEveryLoadedThreadForThisSessionOnly() {
            Settings.ApiThreadsFileWritten = true;
            WriteThreadList(ThreadLines(Url1), ThreadLines(Url3));

            WatchSession session = Load();

            Assert.IsTrue(session.ApiThreadsUnreadable);
            Assert.IsTrue(session.CanSaveApiThreadMarks);
            Assert.IsTrue(GetWatcher(session, "4chan/a/1").Guarded);
            Assert.IsTrue(GetWatcher(session, "4chan/b/3").Guarded);
            StringAssert.Contains(ReadLog(), Settings.ApiThreadsFileName + " is missing although it was written before (ApiThreadsFileWritten in settings.txt), so every loaded thread is treated as added through the local API for this session");
            Assert.IsTrue(session.AddThread(StoppedThread(Url2, false, "4chan/a/1")));
            Assert.IsTrue(GetWatcher(session, "4chan/a/2").Guarded);
            Assert.IsTrue(session.SaveThreadList());
            Assert.IsFalse(File.Exists(ApiThreadsPath), "no empty file hides the loss");
            Assert.IsTrue(session.AddThread(StoppedThread(Url5, true)));
            Assert.IsTrue(session.SaveThreadList());

            CollectionAssert.AreEqual(new[] { "1", "4chan/a/5" }, ReadApiThreads());
            WatchSession next = Load();
            Assert.IsFalse(next.ApiThreadsUnreadable);
            Assert.IsFalse(GetWatcher(next, "4chan/a/1").Guarded);
            Assert.IsTrue(GetWatcher(next, "4chan/a/5").Guarded);
        }

        // The first write of api-threads.txt sets ApiThreadsFileWritten=1 in settings.txt
        [TestMethod]
        public void TheFirstWriteOfTheFileSetsTheSetting() {
            WatchSession session = Load();
            Assert.IsFalse(Settings.ApiThreadsFileWritten == true);
            Assert.IsTrue(session.AddThread(StoppedThread(Url3, false)));
            Assert.IsTrue(session.SaveThreadList());
            Assert.IsFalse(File.Exists(Path.Combine(_dir, Settings.SettingsFileName)), "nothing written without an API thread");

            Assert.IsTrue(session.AddThread(StoppedThread(Url1, true)));
            Assert.IsTrue(session.SaveThreadList());

            Assert.IsTrue(Settings.ApiThreadsFileWritten == true);
            CollectionAssert.Contains(File.ReadAllLines(Path.Combine(_dir, Settings.SettingsFileName)), "ApiThreadsFileWritten=1");
        }

        // The settings save after the file's write may fail: logged, and the thread list is still saved
        [TestMethod]
        public void AFailedSettingsSaveAfterTheFileIsWrittenIsLogged() {
            Directory.CreateDirectory(Path.Combine(_dir, Settings.SettingsFileName));
            WatchSession session = Load();
            Assert.IsTrue(session.AddThread(StoppedThread(Url1, true)));

            Assert.IsTrue(session.SaveThreadList());

            CollectionAssert.AreEqual(new[] { "1", "4chan/a/1" }, ReadApiThreads());
            Assert.HasCount(1, ThreadListFile.Parse(File.ReadAllLines(ThreadListPath)).Threads);
            StringAssert.Contains(ReadLog(),"ApiThreadsFileWritten=1 could not be saved in settings.txt after api-threads.txt was written");
        }

        // Round 3: a missing file is null without the read's retries; a folder in its place is still a read failure
        [TestMethod]
        public void AMissingFileIsReadAsMissingAtOnce() {
            Assert.IsNull(SharedFile.ReadAllLinesIfPresent(ApiThreadsPath));
            Directory.CreateDirectory(ApiThreadsPath);
            Assert.Throws<Exception>(() => SharedFile.ReadAllLinesIfPresent(ApiThreadsPath));
        }

        // Only "1" is on
        [TestMethod]
        [DataRow("1", true)]
        [DataRow("0", false)]
        [DataRow("true", false)]
        [DataRow("", false)]
        public void TheSettingIsOnOnlyForExactlyOne(string value, bool on) {
            string path = Path.Combine(_dir, "strict.txt");
            File.WriteAllLines(path, new[] { "ApiThreadsFileWritten=" + value });
            Settings.Load(path);

            Assert.AreEqual(on, Settings.ApiThreadsFileWritten == true);
        }

        [TestMethod]
        public void WithoutTheFileNoThreadIsGuarded() {
            Assert.IsFalse(Settings.ApiThreadsFileWritten == true);
            WriteThreadList(ThreadLines(Url1), ThreadLines(Url2, "4chan/a/1"));

            WatchSession session = Load();

            Assert.IsFalse(GetWatcher(session, "4chan/a/1").Guarded);
            Assert.IsFalse(GetWatcher(session, "4chan/a/2").Guarded);
            Assert.IsTrue(session.SaveThreadList());
            Assert.IsFalse(File.Exists(ApiThreadsPath));
        }

        // D3 (maintainer, 2026-10-05): a file that can't be read (or has a version this one does not know) is copied
        // aside, and every loaded thread, and every thread followed from one, is guarded for this session only. The
        // next save writes only the marks this session made (threads added through the API), so after a restart the
        // loaded threads are unguarded and the API thread is still guarded.
        [TestMethod]
        [DataRow("2|4chan/a/1")]
        [DataRow("")]
        [DataRow("x")]
        public void AnUnreadableFileGuardsEveryLoadedThreadForThisSessionOnly(string apiThreadsLines) {
            WriteThreadList(ThreadLines(Url1), ThreadLines(Url3));
            WriteApiThreads(apiThreadsLines.Length != 0 ? apiThreadsLines.Split('|') : new string[0]);
            byte[] original = File.ReadAllBytes(ApiThreadsPath);

            WatchSession session = Load();

            Assert.IsTrue(session.ApiThreadsUnreadable);
            Assert.IsTrue(GetWatcher(session, "4chan/a/1").Guarded);
            Assert.IsTrue(GetWatcher(session, "4chan/b/3").Guarded);
            string[] copies = Directory.GetFiles(_dir, TextFile.GetCopySearchPattern(ApiThreadsPath));
            Assert.HasCount(1, copies);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(copies[0]));
            StringAssert.Contains(ReadLog(), Settings.ApiThreadsFileName + " is not valid or has a version this one does not know, so every loaded thread is treated as added through the local API for this session");
            // Followed from a loaded thread: guarded for the session too, not saved
            Assert.IsTrue(session.AddThread(StoppedThread(Url2, false, "4chan/a/1")));
            Assert.IsTrue(GetWatcher(session, "4chan/a/2").Guarded);
            // Added through the API this session: saved
            Assert.IsTrue(session.AddThread(StoppedThread(Url5, true)));

            Assert.IsTrue(session.SaveThreadList());

            CollectionAssert.AreEqual(new[] { "1", "4chan/a/5" }, ReadApiThreads());
            WatchSession next = Load();
            Assert.IsFalse(next.ApiThreadsUnreadable);
            Assert.IsFalse(GetWatcher(next, "4chan/a/1").Guarded);
            Assert.IsFalse(GetWatcher(next, "4chan/a/2").Guarded);
            Assert.IsFalse(GetWatcher(next, "4chan/b/3").Guarded);
            Assert.IsTrue(GetWatcher(next, "4chan/a/5").Guarded);
        }

        // What this test logged (the log is shared by every test and run)
        private string ReadLog() {
            return ReadWholeLog().Substring(_logStart);
        }

        private static string ReadWholeLog() {
            string logPath = Path.Combine(Settings.GetSettingsDirectory(), Settings.LogFileName);
            if (!File.Exists(logPath)) return String.Empty;
            using (FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader sr = new StreamReader(fs)) {
                return sr.ReadToEnd();
            }
        }

        // Item 2 (maintainer, 2026-10-05): api-threads.txt.bak is written with threads.txt.bak, from the same state
        [TestMethod]
        public void TheThreadListBackupTakesTheMarksAlong() {
            WatchSession session = Load();
            Assert.IsTrue(session.AddThread(StoppedThread(Url1, true)));
            Assert.IsTrue(session.AddThread(StoppedThread(Url3, false)));
            Assert.IsTrue(session.SaveThreadList());

            Backup();

            Assert.IsTrue(File.Exists(ThreadListPath + ".bak"));
            CollectionAssert.AreEqual(File.ReadAllBytes(ApiThreadsPath), File.ReadAllBytes(ApiThreadsPath + ".bak"));
        }

        // Without api-threads.txt (and with the setting off) no backup of it is made, and one made earlier is replaced
        // by one without marks
        [TestMethod]
        public void WithoutTheFileTheBackupHasNoMarks() {
            Assert.IsFalse(Settings.ApiThreadsFileWritten == true);
            WatchSession session = Load();
            Assert.IsTrue(session.AddThread(StoppedThread(Url3, false)));
            Assert.IsTrue(session.SaveThreadList());

            Backup();
            Assert.IsFalse(File.Exists(ApiThreadsPath + ".bak"));

            File.WriteAllLines(ApiThreadsPath + ".bak", new[] { "1", "4chan/b/3" });
            Backup();
            CollectionAssert.AreEqual(new[] { "1" }, File.ReadAllLines(ApiThreadsPath + ".bak"));
        }

        // Rounds 1 and 4: a sidecar that is there but can't be used (not valid, or a folder in its place) refreshes
        // neither backup, so the pair made earlier stays together, and the log says so
        [TestMethod]
        [DataRow("invalid")]
        [DataRow("folder")]
        public void AnUnusableFileKeepsBothBackupsMadeEarlier(string kind) {
            WatchSession session = Load();
            Assert.IsTrue(session.AddThread(StoppedThread(Url1, true)));
            Assert.IsTrue(session.SaveThreadList());
            Backup();
            byte[] threadsBackup = File.ReadAllBytes(ThreadListPath + ".bak");
            byte[] marksBackup = File.ReadAllBytes(ApiThreadsPath + ".bak");
            Assert.IsTrue(session.AddThread(StoppedThread(Url3, false)));
            Assert.IsTrue(session.SaveThreadList());
            if (kind == "invalid") {
                WriteApiThreads("2", "4chan/z/1");
            }
            else {
                File.Delete(ApiThreadsPath);
                Directory.CreateDirectory(ApiThreadsPath);
            }

            Backup();

            CollectionAssert.AreEqual(threadsBackup, File.ReadAllBytes(ThreadListPath + ".bak"));
            CollectionAssert.AreEqual(marksBackup, File.ReadAllBytes(ApiThreadsPath + ".bak"));
            StringAssert.Contains(ReadLog(), Settings.ApiThreadsFileName + " can't be used (");
            StringAssert.Contains(ReadLog(), "so the thread list was not backed up; the backups made earlier stay as they are.");
        }

        // Round 3: api-threads.txt.bak is written first: a thread list backup that then fails leaves the marks backup
        // with marks to spare (fails closed)
        [TestMethod]
        public void TheMarksBackupIsWrittenFirst() {
            WatchSession session = Load();
            Assert.IsTrue(session.AddThread(StoppedThread(Url1, true)));
            Assert.IsTrue(session.SaveThreadList());
            Directory.CreateDirectory(ThreadListPath + ".bak");

            Backup();

            CollectionAssert.AreEqual(File.ReadAllBytes(ApiThreadsPath), File.ReadAllBytes(ApiThreadsPath + ".bak"));
        }

        // Round 3: with the setting on and api-threads.txt missing (lost), neither backup is refreshed, so the pair
        // made earlier stays together, and the log says so
        [TestMethod]
        public void AMissingFileThatWasWrittenLeavesBothBackups() {
            WatchSession session = Load();
            Assert.IsTrue(session.AddThread(StoppedThread(Url1, true)));
            Assert.IsTrue(session.SaveThreadList());
            Backup();
            byte[] threadsBackup = File.ReadAllBytes(ThreadListPath + ".bak");
            byte[] marksBackup = File.ReadAllBytes(ApiThreadsPath + ".bak");
            Assert.IsTrue(session.AddThread(StoppedThread(Url3, false)));
            Assert.IsTrue(session.SaveThreadList());
            File.Delete(ApiThreadsPath);
            Assert.IsTrue(Settings.ApiThreadsFileWritten == true);

            Backup();

            CollectionAssert.AreEqual(threadsBackup, File.ReadAllBytes(ThreadListPath + ".bak"));
            CollectionAssert.AreEqual(marksBackup, File.ReadAllBytes(ApiThreadsPath + ".bak"));
            StringAssert.Contains(ReadLog(), Settings.ApiThreadsFileName + " is missing although it was written before, so the thread list was not backed up");
        }

        // A guarded thread removed after the backup comes back guarded when the backup is restored (both files, as the
        // README says; no code path restores a backup by itself)
        [TestMethod]
        public void ARemovedGuardedThreadRestoredFromTheBackupComesBackGuarded() {
            WatchSession session = Load();
            Assert.IsTrue(session.AddThread(StoppedThread(Url1, true)));
            Assert.IsTrue(session.AddThread(StoppedThread(Url3, false)));
            Assert.IsTrue(session.SaveThreadList());
            Backup();
            session.RemoveThreads(new[] { GetWatcher(session, "4chan/a/1") });
            Assert.IsTrue(session.SaveThreadList());
            CollectionAssert.AreEqual(new[] { "1" }, ReadApiThreads());

            File.Copy(ThreadListPath + ".bak", ThreadListPath, true);
            File.Copy(ApiThreadsPath + ".bak", ApiThreadsPath, true);
            WatchSession restored = Load();

            Assert.IsTrue(GetWatcher(restored, "4chan/a/1").Guarded);
            Assert.IsFalse(GetWatcher(restored, "4chan/b/3").Guarded);
        }

        // General.BackupThreadList backs up the settings folder's files
        private void Backup() {
            Settings.SettingsDirectoryOverride = _dir;
            try {
                General.BackupThreadList();
            }
            finally {
                Settings.SettingsDirectoryOverride = null;
            }
        }

        // A file that can't be read (here a folder) is left as it is for the session: no copy aside, not written, and
        // the API can't add threads (it could not save their marks)
        [TestMethod]
        public void AFileThatCannotBeReadIsLeftAsItIs() {
            WriteThreadList(ThreadLines(Url1));
            Directory.CreateDirectory(ApiThreadsPath);

            WatchSession session = Load();

            Assert.IsTrue(GetWatcher(session, "4chan/a/1").Guarded);
            Assert.IsFalse(session.CanSaveApiThreadMarks);
            Assert.IsTrue(session.SaveThreadList());
            Assert.IsTrue(Directory.Exists(ApiThreadsPath));
            Assert.IsEmpty(Directory.GetFileSystemEntries(_dir, TextFile.GetCopySearchPattern(ApiThreadsPath)));
        }

        // Fix 1: a file another program holds locked past the read's retries is a read failure, not an invalid file:
        // every loaded thread is guarded for the session, and the file is never copied aside or overwritten
        [TestMethod]
        public void AFileHeldLockedIsLeftUntouched() {
            WriteThreadList(ThreadLines(Url1), ThreadLines(Url3));
            WriteApiThreads("1", "4chan/a/1");
            byte[] before = File.ReadAllBytes(ApiThreadsPath);
            WatchSession session;
            using (new FileStream(ApiThreadsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                session = Load();
                Assert.IsTrue(GetWatcher(session, "4chan/a/1").Guarded);
                Assert.IsTrue(GetWatcher(session, "4chan/b/3").Guarded);
                Assert.IsFalse(session.CanSaveApiThreadMarks);
            }
            Assert.IsTrue(session.AddThread(StoppedThread(Url5, true)));

            Assert.IsTrue(session.SaveThreadList());

            CollectionAssert.AreEqual(before, File.ReadAllBytes(ApiThreadsPath));
            Assert.IsEmpty(Directory.GetFiles(_dir, TextFile.GetCopySearchPattern(ApiThreadsPath)));
            StringAssert.Contains(ReadLog(), Settings.ApiThreadsFileName + " could not be read, so every loaded thread is treated as added through the local API for this session (it never connects to a local or private address). The file is left as it is");
        }

        // A lock that ends within the read's retries is read as usual
        [TestMethod]
        public void AFileLockedForAMomentIsRead() {
            WriteThreadList(ThreadLines(Url1), ThreadLines(Url3));
            WriteApiThreads("1", "4chan/a/1");
            FileStream locked = new FileStream(ApiThreadsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            System.Threading.Tasks.Task release = System.Threading.Tasks.Task.Run(() => { System.Threading.Thread.Sleep(60); locked.Dispose(); });

            WatchSession session = Load();
            release.Wait();

            Assert.IsTrue(session.CanSaveApiThreadMarks);
            Assert.IsTrue(GetWatcher(session, "4chan/a/1").Guarded);
            Assert.IsFalse(GetWatcher(session, "4chan/b/3").Guarded);
        }

        // Fix 3: the mark of a removed thread stays in the file until threads.txt without it is saved, so a failed
        // save of threads.txt never leaves the thread there without its mark
        [TestMethod]
        public void ARemovedThreadKeepsItsMarkUntilTheThreadListIsSaved() {
            WatchSession session = Load();
            Assert.IsTrue(session.AddThread(StoppedThread(Url1, true)));
            Assert.IsTrue(session.SaveThreadList());
            byte[] threadList = File.ReadAllBytes(ThreadListPath);
            session.RemoveThreads(new[] { GetWatcher(session, "4chan/a/1") });
            File.Delete(ThreadListPath);
            Directory.CreateDirectory(ThreadListPath);

            Assert.IsFalse(session.SaveThreadList());

            CollectionAssert.AreEqual(new[] { "1", "4chan/a/1" }, ReadApiThreads());
            // The thread list as the failed save left it
            Directory.Delete(ThreadListPath);
            File.WriteAllBytes(ThreadListPath, threadList);
            WatchSession next = Load();
            Assert.IsTrue(GetWatcher(next, "4chan/a/1").Guarded);
            // Once threads.txt without the thread is saved, its mark goes
            Assert.IsTrue(session.SaveThreadList());
            CollectionAssert.AreEqual(new[] { "1" }, ReadApiThreads());
        }

        // Fix 4: a thread added through the API while a load with an unreadable file runs is not from the file, so
        // its mark is real and saved
        [TestMethod]
        public void AnAPIAddDuringALoadWithAnUnreadableFileKeepsARealMark() {
            WriteThreadList(ThreadLines(Url1));
            WriteApiThreads("2");
            WatchSession session = CreateSession();
            session.ThreadListLoadStarting += () => Assert.IsTrue(session.AddThread(StoppedThread(Url5, true)));

            session.LoadThreadList();

            Assert.IsTrue(GetWatcher(session, "4chan/a/5").Guarded);
            Assert.IsTrue(session.SaveThreadList());
            CollectionAssert.AreEqual(new[] { "1", "4chan/a/5" }, ReadApiThreads());
        }

        // A guarded request never takes over an unguarded watcher, and an unguarded one never unmarks a guarded watcher
        [TestMethod]
        public void AddingAgainNeverChangesTheMark() {
            WatchSession session = Load();
            Assert.IsTrue(session.AddThread(StoppedThread(Url1, false)));
            Assert.IsTrue(session.AddThread(StoppedThread(Url2, true)));
            ThreadWatcher unguarded = GetWatcher(session, "4chan/a/1");
            ThreadWatcher guarded = GetWatcher(session, "4chan/a/2");

            Assert.IsFalse(session.AddThread(StoppedThread(Url1, true)));
            Assert.IsTrue(session.AddThread(StoppedThread(Url2, false)));

            Assert.AreSame(unguarded, GetWatcher(session, "4chan/a/1"));
            Assert.IsFalse(unguarded.Guarded);
            Assert.AreSame(guarded, GetWatcher(session, "4chan/a/2"));
            Assert.IsTrue(guarded.Guarded);
        }

        // The mark is part of the thread info a save makes, never of the file
        [TestMethod]
        public void TheThreadListFileFormatHasNoMark() {
            ThreadInfo thread = StoppedThread(Url1, true);
            string[] guardedLines = ThreadListFile.Serialize(new[] { thread });
            thread.Guarded = false;

            CollectionAssert.AreEqual(ThreadListFile.Serialize(new[] { thread }), guardedLines);
            Assert.IsFalse(ThreadListFile.Parse(guardedLines).Threads[0].Guarded);
        }

        // v1.39.0 globs its settings folder for recovery copies ("threads.txt.corrupt-*", "settings.txt.corrupt-*") and
        // uses fixed names for the rest; neither api-threads.txt nor its own copies and temporary files match them, so
        // that release never reads, rewrites or deletes it (the searches run on the real file system, short names included)
        [TestMethod]
        public void TheFileMatchesNoneOfTheFilesOfThePreviousRelease() {
            string apiThreads = ApiThreadsPath;
            File.WriteAllText(apiThreads, "1");
            File.WriteAllText(apiThreads + ".corrupt-20261005-120000-000", "1");
            File.WriteAllText(Path.Combine(_dir, "~" + Settings.ApiThreadsFileName + "." + Guid.NewGuid().ToString("N") + ".tmp"), "1");
            File.WriteAllText(apiThreads + ".tmp", "1");
            // The backup (General.BackupThreadList) and the temporary file of its byte-for-byte write
            File.WriteAllText(apiThreads + ".bak", "1");
            File.WriteAllText(Path.Combine(_dir, "~" + Settings.ApiThreadsFileName + ".bak.tmp"), "1");

            foreach (string pattern in new[] { "threads.txt.corrupt-*", "settings.txt.corrupt-*", "threads.txt*", "settings.txt*", "blacklist.txt*", "log.txt*", "threads.txt.bak*" }) {
                Assert.IsEmpty(Directory.GetFiles(_dir, pattern), pattern);
            }
            foreach (string name in new[] { Settings.ThreadsFileName, Settings.SettingsFileName, Settings.BlacklistFileName, Settings.LogFileName }) {
                Assert.IsFalse(Settings.ApiThreadsFileName.StartsWith(name, StringComparison.OrdinalIgnoreCase), name);
            }
            Assert.IsEmpty(Directory.GetFiles(_dir, TextFile.GetCopySearchPattern(ThreadListPath)));
        }
    }
}
