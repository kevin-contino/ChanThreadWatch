using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    [TestClass]
    public class ThreadListFileTests {
        private static readonly DateTime AddedOnUtc = new DateTime(2020, 1, 15, 12, 30, 0, DateTimeKind.Utc);
        private static readonly DateTime LastImageOnUtc = new DateTime(2020, 2, 20, 8, 0, 0, DateTimeKind.Utc);
        private string _dir;
        private string _path;

        [ClassInitialize]
        public static void UseExeDirectoryForLog(TestContext context) {
            // Logger writes next to the test binaries instead of AppData
            Settings.UseExeDirectoryForSettings = true;
        }

        [TestInitialize]
        public void CreateTempDirectory() {
            _dir = Path.Combine(Path.GetTempPath(), "ctw-threadlist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "threads.txt");
        }

        [TestCleanup]
        public void DeleteTempDirectory() {
            Directory.Delete(_dir, true);
        }

        private static string Ticks(DateTime utc) {
            return utc.Ticks.ToString();
        }

        private static string[] Version1Lines() {
            return new[] { "1",
                "https://boards.4chan.org/a/thread/1", "user:pass", "", "300", "1", "a_1" };
        }

        private static string[] Version2Lines() {
            return new[] { "2",
                "https://boards.4chan.org/a/thread/1", "", "img:auth", "60", "0", "", "3" };
        }

        private static string[] Version3Lines() {
            return new[] { "3",
                "https://boards.4chan.org/a/thread/1", "", "", "120", "1", "a_1", "", "Desc", Ticks(AddedOnUtc), Ticks(LastImageOnUtc) };
        }

        private static string[] Version4Lines() {
            return new[] { "4",
                "https://boards.4chan.org/a/thread/1", "u:p", "i:p", "600", "0", "a_1", "1", "Parent", Ticks(AddedOnUtc), "", "", "Cat", "1",
                "https://boards.4chan.org/a/thread/2", "", "", "0", "1", "", "", "Child", Ticks(AddedOnUtc), Ticks(LastImageOnUtc), "a_1", "", "0" };
        }

        private static void AssertSameThread(ThreadInfo expected, ThreadInfo actual) {
            Assert.AreEqual(expected.URL, actual.URL);
            Assert.AreEqual(expected.PageAuth, actual.PageAuth);
            Assert.AreEqual(expected.ImageAuth, actual.ImageAuth);
            Assert.AreEqual(expected.CheckIntervalSeconds, actual.CheckIntervalSeconds);
            Assert.AreEqual(expected.OneTimeDownload, actual.OneTimeDownload);
            Assert.AreEqual(expected.SaveDir, actual.SaveDir);
            Assert.AreEqual(expected.StopReason, actual.StopReason);
            Assert.AreEqual(expected.Description, actual.Description);
            Assert.AreEqual(expected.ExtraData.AddedOn.ToUniversalTime(), actual.ExtraData.AddedOn.ToUniversalTime());
            Assert.AreEqual(expected.ExtraData.LastImageOn, actual.ExtraData.LastImageOn);
            Assert.AreEqual(expected.ExtraData.AddedFrom, actual.ExtraData.AddedFrom);
            Assert.AreEqual(expected.Category, actual.Category);
            Assert.AreEqual(expected.AutoFollow, actual.AutoFollow);
        }

        // Writes the parsed threads in the current format and reads them back.
        private List<ThreadInfo> SaveAndReload(List<ThreadInfo> threads) {
            ThreadListStore store = new ThreadListStore();
            store.EndLoad(_path, true);
            Assert.IsTrue(store.Save(_path, threads));
            ThreadListData data = new ThreadListStore().Read(_path);
            Assert.AreEqual(ThreadListFile.CurrentVersion, data.FileVersion);
            Assert.AreEqual(0, data.TrailingLineCount);
            return data.Threads;
        }

        private void AssertSurvivesSaveAndReload(List<ThreadInfo> threads) {
            List<ThreadInfo> reloaded = SaveAndReload(threads);
            Assert.HasCount(threads.Count, reloaded);
            for (int i = 0; i < threads.Count; i++) {
                AssertSameThread(threads[i], reloaded[i]);
            }
        }

        [TestMethod]
        // PendingUnix: saving a login encrypts it with DPAPI, which exists only on Windows, see MP-4c
        [TestCategory("PendingUnix")]
        public void Version1FileLoadsAndRoundTrips() {
            List<ThreadInfo> threads = ThreadListFile.Parse(Version1Lines()).Threads;

            Assert.HasCount(1, threads);
            Assert.AreEqual("user:pass", threads[0].PageAuth);
            Assert.AreEqual(300, threads[0].CheckIntervalSeconds);
            Assert.IsTrue(threads[0].OneTimeDownload);
            Assert.AreEqual("a_1", threads[0].SaveDir);
            Assert.IsNull(threads[0].StopReason);
            Assert.AreEqual(String.Empty, threads[0].ExtraData.AddedFrom);
            AssertSurvivesSaveAndReload(threads);
        }

        [TestMethod]
        // PendingUnix: saving a login encrypts it with DPAPI, which exists only on Windows, see MP-4c
        [TestCategory("PendingUnix")]
        public void Version2FileLoadsAndRoundTrips() {
            List<ThreadInfo> threads = ThreadListFile.Parse(Version2Lines()).Threads;

            Assert.HasCount(1, threads);
            Assert.AreEqual("img:auth", threads[0].ImageAuth);
            Assert.AreEqual(StopReason.PageNotFound, threads[0].StopReason);
            Assert.AreEqual(String.Empty, threads[0].SaveDir);
            AssertSurvivesSaveAndReload(threads);
        }

        [TestMethod]
        public void Version3FileLoadsAndRoundTrips() {
            List<ThreadInfo> threads = ThreadListFile.Parse(Version3Lines()).Threads;

            Assert.HasCount(1, threads);
            Assert.AreEqual("Desc", threads[0].Description);
            Assert.AreEqual(AddedOnUtc, threads[0].ExtraData.AddedOn.ToUniversalTime());
            Assert.AreEqual(LastImageOnUtc, threads[0].ExtraData.LastImageOn.Value.ToUniversalTime());
            AssertSurvivesSaveAndReload(threads);
        }

        [TestMethod]
        // PendingUnix: saving a login encrypts it with DPAPI, which exists only on Windows, see MP-4c
        [TestCategory("PendingUnix")]
        public void Version4FileLoadsAndRoundTrips() {
            List<ThreadInfo> threads = ThreadListFile.Parse(Version4Lines()).Threads;

            Assert.HasCount(2, threads);
            Assert.AreEqual(StopReason.UserRequest, threads[0].StopReason);
            Assert.AreEqual("Cat", threads[0].Category);
            Assert.IsTrue(threads[0].AutoFollow);
            Assert.IsNull(threads[0].ExtraData.LastImageOn);
            Assert.AreEqual("a_1", threads[1].ExtraData.AddedFrom);
            Assert.IsFalse(threads[1].AutoFollow);
            AssertSurvivesSaveAndReload(threads);
        }

        [TestMethod]
        // PendingUnix: saving a login encrypts it with DPAPI, which exists only on Windows, see MP-4c
        [TestCategory("PendingUnix")]
        public void Version4FileIsWrittenLineForLine() {
            List<ThreadInfo> threads = ThreadListFile.Parse(Version4Lines()).Threads;

            string[] written = ThreadListFile.Serialize(threads);

            // The auth lines are written encrypted (see StoredAuthTests); compare their plaintext
            for (int i = 1; i < written.Length; i += ThreadListFile.GetLinesPerThread(4)) {
                written[i + 1] = StoredAuth.Unprotect(written[i + 1]);
                written[i + 2] = StoredAuth.Unprotect(written[i + 2]);
            }
            CollectionAssert.AreEqual(Version4Lines(), written);
        }

        [TestMethod]
        // PendingUnix: saving a login encrypts it with DPAPI, which exists only on Windows, see MP-4c
        [TestCategory("PendingUnix")]
        public void LineBreaksInValuesDoNotShiftTheFile() {
            List<ThreadInfo> threads = ThreadListFile.Parse(Version4Lines()).Threads;
            threads[0].Description = "two\r\nlines\nhere";
            threads[0].Category = "a\rb";

            List<ThreadInfo> reloaded = SaveAndReload(threads);

            Assert.HasCount(2, reloaded);
            Assert.AreEqual("two lines here", reloaded[0].Description);
            Assert.AreEqual("a b", reloaded[0].Category);
            Assert.AreEqual(threads[1].URL, reloaded[1].URL);
        }

        [TestMethod]
        public void NullValuesAreWrittenAsEmptyLines() {
            ThreadInfo thread = new ThreadInfo { URL = "https://boards.4chan.org/a/thread/1", ExtraData = new WatcherExtraData { AddedOn = AddedOnUtc } };

            List<ThreadInfo> reloaded = SaveAndReload(new List<ThreadInfo> { thread });

            Assert.AreEqual(String.Empty, reloaded[0].PageAuth);
            Assert.AreEqual(String.Empty, reloaded[0].Description);
            Assert.AreEqual(String.Empty, reloaded[0].ExtraData.AddedFrom);
        }

        [TestMethod]
        [DataRow(new[] { "5", "a", "b", "c", "d", "e", "f" }, DisplayName = "unknown version")]
        [DataRow(new[] { "x" }, DisplayName = "bad version")]
        [DataRow(new[] { "1", "https://boards.4chan.org/a/thread/1", "", "", "abc", "0", "" }, DisplayName = "bad interval")]
        [DataRow(new[] { "2", "https://boards.4chan.org/a/thread/1", "", "", "60", "0", "", "z" }, DisplayName = "bad stop reason")]
        [DataRow(new[] { "3", "https://boards.4chan.org/a/thread/1", "", "", "60", "0", "", "", "d", "99999999999999999999", "" }, DisplayName = "overflowing date")]
        [DataRow(new[] { "3", "https://boards.4chan.org/a/thread/1", "", "", "60", "0", "", "", "d", "-5", "" }, DisplayName = "negative date")]
        public void ParseRejectsMalformedFiles(string[] lines) {
            bool threw = false;
            try {
                ThreadListFile.Parse(lines);
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException) {
                threw = true;
            }
            Assert.IsTrue(threw);
            Assert.IsFalse(ThreadListFile.IsValid(lines));
        }

        [TestMethod]
        public void ParseReportsTrailingLinesOfATruncatedFile() {
            string[] lines = Version4Lines();
            Array.Resize(ref lines, lines.Length - 3);

            ThreadListData data = ThreadListFile.Parse(lines);

            Assert.HasCount(1, data.Threads);
            Assert.AreEqual(10, data.TrailingLineCount);
            Assert.IsFalse(ThreadListFile.IsValid(lines));
        }

        [TestMethod]
        public void HeaderOnlyFileIsAValidEmptyList() {
            ThreadListData data = ThreadListFile.Parse(new[] { "4" });

            Assert.HasCount(0, data.Threads);
            Assert.AreEqual(0, data.TrailingLineCount);
            Assert.IsTrue(ThreadListFile.IsValid(new[] { "4" }));
            Assert.IsFalse(ThreadListFile.IsValid(new string[0]));
        }

        // Mirrors LoadThreadList in the form: a load counts as full only if the file was
        // missing or parsed without leftover lines.
        private static void LoadLikeTheForm(ThreadListStore store, string path) {
            bool loadedFully = false;
            try {
                ThreadListData data = store.Read(path);
                loadedFully = data == null || data.TrailingLineCount == 0;
            }
            catch (Exception) {
                // Logged by the form
            }
            store.EndLoad(path, loadedFully);
        }

        private string[] CorruptCopies() {
            return Directory.GetFiles(_dir, "threads.txt.corrupt-*");
        }

        [TestMethod]
        [DataRow("5\nhttps://boards.4chan.org/a/thread/1\n\n\n60\n0\n", DisplayName = "unknown version")]
        [DataRow("4\nhttps://boards.4chan.org/a/thread/1\n\n\nabc\n0\n\n\n\n0\n\n\n\n0\n", DisplayName = "bad number")]
        [DataRow("4\nhttps://boards.4chan.org/a/thread/1\n\n\n60\n", DisplayName = "truncated")]
        [DataRow("\n", DisplayName = "blank version line")]
        public void MalformedFileIsPreservedBeforeTheNextSave(string content) {
            File.WriteAllText(_path, content);
            ThreadListStore store = new ThreadListStore();

            LoadLikeTheForm(store, _path);
            store.Save(_path, new List<ThreadInfo>());

            string[] copies = CorruptCopies();
            Assert.HasCount(1, copies);
            Assert.AreEqual(content, File.ReadAllText(copies[0]));
        }

        [TestMethod]
        public void ValidFileIsNotCopiedAside() {
            File.WriteAllLines(_path, Version4Lines());
            ThreadListStore store = new ThreadListStore();

            LoadLikeTheForm(store, _path);

            Assert.IsTrue(store.CanSave);
            Assert.HasCount(0, CorruptCopies());
        }

        [TestMethod]
        public void MissingFileAllowsSaving() {
            ThreadListStore store = new ThreadListStore();

            LoadLikeTheForm(store, _path);

            Assert.IsTrue(store.Save(_path, new List<ThreadInfo>()));
            CollectionAssert.AreEqual(new[] { "4" }, File.ReadAllLines(_path));
        }

        [TestMethod]
        public void SaveBeforeLoadFinishesDoesNotTouchTheFile() {
            File.WriteAllLines(_path, Version4Lines());
            ThreadListStore store = new ThreadListStore();

            Assert.IsFalse(store.Save(_path, new List<ThreadInfo>()));

            CollectionAssert.AreEqual(Version4Lines(), File.ReadAllLines(_path));
        }

        [TestMethod]
        public void UnreadableFileThatCannotBeCopiedIsNeverOverwritten() {
            File.WriteAllLines(_path, Version4Lines());
            ThreadListStore store = new ThreadListStore();

            using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None)) {
                LoadLikeTheForm(store, _path);
            }

            Assert.IsFalse(store.CanSave);
            Assert.IsFalse(store.Save(_path, new List<ThreadInfo>()));
            CollectionAssert.AreEqual(Version4Lines(), File.ReadAllLines(_path));
        }

        // A backup written before logins were encrypted is rewritten by the first save, with
        // its own threads (not the saved list) and encrypted logins
        [TestMethod]
        // PendingUnix: saving a login encrypts it with DPAPI, which exists only on Windows, see MP-4c
        [TestCategory("PendingUnix")]
        public void FirstSaveEncryptsPlaintextLoginsInTheBackup() {
            string backupPath = _path + ".bak";
            File.WriteAllLines(_path, Version4Lines());
            File.WriteAllLines(backupPath, Version1Lines());
            ThreadListStore store = new ThreadListStore();
            LoadLikeTheForm(store, _path);

            Assert.IsTrue(store.Save(_path, store.Read(_path).Threads));

            string[] backup = File.ReadAllLines(backupPath);
            CollectionAssert.DoesNotContain(backup, "user:pass");
            Assert.IsFalse(ThreadListFile.HasPlaintextAuth(backup));
            ThreadListData data = ThreadListFile.Parse(backup);
            Assert.HasCount(1, data.Threads);
            Assert.AreEqual("https://boards.4chan.org/a/thread/1", data.Threads[0].URL);
            Assert.AreEqual("user:pass", data.Threads[0].PageAuth);
        }

        // E.g. antivirus or a sync tool holding the backup during the first save
        [TestMethod]
        // PendingUnix: saving a login encrypts it with DPAPI, which exists only on Windows, see MP-4c
        [TestCategory("PendingUnix")]
        public void BackupInUseDuringTheFirstSaveIsEncryptedByALaterSave() {
            string backupPath = _path + ".bak";
            File.WriteAllLines(_path, Version4Lines());
            File.WriteAllLines(backupPath, Version1Lines());
            ThreadListStore store = new ThreadListStore();
            LoadLikeTheForm(store, _path);

            using (new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.None)) {
                Assert.IsTrue(store.Save(_path, new List<ThreadInfo>()));
            }
            Assert.IsTrue(ThreadListFile.HasPlaintextAuth(File.ReadAllLines(backupPath)));
            Assert.IsTrue(store.Save(_path, new List<ThreadInfo>()));

            string[] backup = File.ReadAllLines(backupPath);
            Assert.IsFalse(ThreadListFile.HasPlaintextAuth(backup));
            Assert.AreEqual("user:pass", ThreadListFile.Parse(backup).Threads[0].PageAuth);
        }

        // A backup that doesn't load fully can't be written again without losing data, so it is
        // only reported, once, without the login
        [TestMethod]
        public void IncompleteBackupWithPlaintextLoginsIsReportedOnceAndLeftAsItIs() {
            string backupPath = _path + ".bak";
            string[] truncated = new List<string>(Version1Lines()) { "https://boards.4chan.org/a/thread/2" }.ToArray();
            File.WriteAllLines(_path, Version4Lines());
            File.WriteAllLines(backupPath, truncated);
            ThreadListStore store = new ThreadListStore();
            LoadLikeTheForm(store, _path);
            string logPath = Path.Combine(Settings.GetSettingsDirectory(), Settings.LogFileName);
            Logger.Log("ThreadListFileTests marker");
            int start = ReadSharedFile(logPath).Length;

            store.Save(_path, new List<ThreadInfo>());
            store.Save(_path, new List<ThreadInfo>());

            CollectionAssert.AreEqual(truncated, File.ReadAllLines(backupPath));
            string log = ReadSharedFile(logPath).Substring(start);
            Assert.AreEqual(1, log.Split(new[] { "backup holds plaintext logins" }, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("user:pass", log);
        }

        // The logger keeps the log file open for appending
        private static string ReadSharedFile(string path) {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs)) {
                return sr.ReadToEnd();
            }
        }

        [TestMethod]
        // PendingUnix: saving a login encrypts it with DPAPI, which exists only on Windows, see MP-4c
        [TestCategory("PendingUnix")]
        public void BackupWithoutPlaintextLoginsIsLeftAsItIs() {
            string backupPath = _path + ".bak";
            string[] encrypted = ThreadListFile.GetBackupLines(Version1Lines());
            File.WriteAllLines(_path, Version4Lines());
            File.WriteAllLines(backupPath, encrypted);
            ThreadListStore store = new ThreadListStore();
            LoadLikeTheForm(store, _path);

            store.Save(_path, new List<ThreadInfo>());

            CollectionAssert.AreEqual(encrypted, File.ReadAllLines(backupPath));
        }

        // A version 4 file whose last thread is cut off after its first login, with a byte
        // order mark, mixed line breaks, no final line break and a byte that isn't valid UTF-8
        private static byte[] MixedThreadListBytes(string[] logins, string encrypted) {
            string[] lines = { "4",
                "https://boards.4chan.org/a/thread/1", logins[0], encrypted, "600", "0", "a_1", "1", "Desc \u00e9", Ticks(AddedOnUtc), "", "", "Cat", "1",
                "https://boards.4chan.org/a/thread/2", "", logins[1], "0", "1", "", "", "Child", Ticks(AddedOnUtc), "", "a_1", "", "0",
                "https://boards.4chan.org/a/thread/3", logins[2] };
            string[] breaks = { "\r\n", "\n", "\r" };
            List<byte> bytes = new List<byte>(Encoding.UTF8.GetPreamble());
            for (int i = 0; i < lines.Length; i++) {
                bytes.AddRange(Encoding.UTF8.GetBytes(lines[i]));
                if (i == 8) bytes.Add(0xFF);
                if (i < lines.Length - 1) bytes.AddRange(Encoding.ASCII.GetBytes(breaks[i % 3]));
            }
            return bytes.ToArray();
        }

        private static readonly string[] PlaintextLogins = { "user:pass", "img:pass", "late:pass" };
        private static readonly string[] NoLogins = { "", "", "" };

        [TestMethod]
        // PendingUnix: saving a login encrypts it with DPAPI, which exists only on Windows, see MP-4c
        [TestCategory("PendingUnix")]
        public void CopyOfAFileWithPlaintextLoginsHoldsNoneAndKeepsEveryOtherByte() {
            string encrypted = StoredAuth.Protect("enc:pass");
            File.WriteAllBytes(_path, MixedThreadListBytes(PlaintextLogins, encrypted));
            ThreadListStore store = new ThreadListStore();

            LoadLikeTheForm(store, _path);

            string[] copies = CorruptCopies();
            Assert.HasCount(1, copies);
            CollectionAssert.AreEqual(MixedThreadListBytes(NoLogins, encrypted), File.ReadAllBytes(copies[0]));
            Assert.IsFalse(ThreadListFile.HasPlaintextAuth(File.ReadAllLines(copies[0])));
            CollectionAssert.AreEqual(MixedThreadListBytes(PlaintextLogins, encrypted), File.ReadAllBytes(_path));
        }

        private string CopyPath(string stamp) {
            return _path + ".corrupt-" + stamp;
        }

        private static readonly string[] OldCopyLines = { "4", "https://boards.4chan.org/a/thread/1", "user:pass", "img:pass", "abc" };
        private static readonly string[] BlankedOldCopyLines = { "4", "https://boards.4chan.org/a/thread/1", "", "", "abc" };

        [TestMethod]
        // PendingUnix: saving a login encrypts it with DPAPI, which exists only on Windows, see MP-4c
        [TestCategory("PendingUnix")]
        public void FirstSaveBlanksPlaintextLoginsInExistingCopiesOnce() {
            string plainCopy = CopyPath("20200101-000000-000");
            string encryptedCopy = CopyPath("20200102-000000-000");
            string[] encryptedLines = { "4", "https://boards.4chan.org/a/thread/1", StoredAuth.Protect("u:p"), "", "abc" };
            string otherFile = Path.Combine(_dir, "settings.txt.corrupt-20200101-000000-000");
            File.WriteAllLines(_path, Version4Lines());
            File.WriteAllLines(plainCopy, OldCopyLines);
            File.WriteAllLines(encryptedCopy, encryptedLines);
            File.WriteAllLines(otherFile, OldCopyLines);
            DateTime encryptedWriteTime = File.GetLastWriteTimeUtc(encryptedCopy).AddDays(-1);
            File.SetLastWriteTimeUtc(encryptedCopy, encryptedWriteTime);
            ThreadListStore store = new ThreadListStore();
            LoadLikeTheForm(store, _path);
            string logPath = Path.Combine(Settings.GetSettingsDirectory(), Settings.LogFileName);
            Logger.Log("ThreadListFileTests marker");
            int start = ReadSharedFile(logPath).Length;

            Assert.IsTrue(store.Save(_path, new List<ThreadInfo>()));

            CollectionAssert.AreEqual(BlankedOldCopyLines, File.ReadAllLines(plainCopy));
            CollectionAssert.AreEqual(encryptedLines, File.ReadAllLines(encryptedCopy));
            Assert.AreEqual(encryptedWriteTime, File.GetLastWriteTimeUtc(encryptedCopy));
            CollectionAssert.AreEqual(OldCopyLines, File.ReadAllLines(otherFile));
            string log = ReadSharedFile(logPath).Substring(start);
            Assert.AreEqual(1, log.Split(new[] { "Plaintext logins were removed from " + Path.GetFileName(plainCopy) + Environment.NewLine }, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("user:pass", log);
            Assert.DoesNotContain(_dir, log);

            // Only once per session
            File.WriteAllLines(plainCopy, OldCopyLines);
            store.Save(_path, new List<ThreadInfo>());
            CollectionAssert.AreEqual(OldCopyLines, File.ReadAllLines(plainCopy));
        }

        // E.g. antivirus or a sync tool holding the copy: it is left as it was and the next
        // session tries again, while the other copies are still written. On Windows the reader's
        // sharing mode stops the replace; on Unix its lock stops the rewrite (TextFile locks the
        // copy first, since an open file doesn't stop a rename).
        [TestMethod]
        public void CopyThatCannotBeReplacedIsLeftUnchangedUntilTheNextSession() {
            string copy = CopyPath("20200101-000000-000");
            string otherCopy = CopyPath("20200102-000000-000");
            File.WriteAllLines(_path, Version4Lines());
            File.WriteAllLines(copy, OldCopyLines);
            File.WriteAllLines(otherCopy, OldCopyLines);
            byte[] original = File.ReadAllBytes(copy);
            ThreadListStore store = new ThreadListStore();
            LoadLikeTheForm(store, _path);

            using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                Assert.IsTrue(store.Save(_path, new List<ThreadInfo>()));
            }
            CollectionAssert.AreEqual(original, File.ReadAllBytes(copy));
            CollectionAssert.AreEqual(BlankedOldCopyLines, File.ReadAllLines(otherCopy));
            CollectionAssert.AreEquivalent(new[] { _path, copy, otherCopy }, Directory.GetFiles(_dir));
            store.Save(_path, new List<ThreadInfo>());
            CollectionAssert.AreEqual(original, File.ReadAllBytes(copy));

            ThreadListStore nextSession = new ThreadListStore();
            LoadLikeTheForm(nextSession, _path);
            Assert.IsTrue(nextSession.Save(_path, new List<ThreadInfo>()));
            CollectionAssert.AreEqual(BlankedOldCopyLines, File.ReadAllLines(copy));
        }

        // Threads are found by their URL line, so a broken block, a missing or unknown version
        // line or a version this one doesn't know still has its logins removed
        [TestMethod]
        [DataRow("4\nhttps://a/1\nuser:pass\n\nabc\n", "4\nhttps://a/1\n\n\nabc\n", DisplayName = "bad number, truncated")]
        [DataRow("4\r\nhttps://a/1\r\nuser:pass", "4\r\nhttps://a/1\r\n", DisplayName = "cut off in a login")]
        [DataRow("4\nhttps://a/1\nuser:pass\nhttps://a/2\nimg:pass\n", "4\nhttps://a/1\n\nhttps://a/2\n\n", DisplayName = "URL in a login position is kept")]
        [DataRow("1\nhttps://a/1\nuser:pass\nimg:pass\n300\n1\na_1\nhttps://a/2\nu2:p\n", "1\nhttps://a/1\n\n\n300\n1\na_1\nhttps://a/2\n\n", DisplayName = "version 1, second thread cut off")]
        [DataRow("4\nhttps://a/1\ndpapi:AAAA\nuser:pass\n", "4\nhttps://a/1\ndpapi:AAAA\n\n", DisplayName = "encrypted login kept")]
        [DataRow("5\nhttps://a/1\nuser:pass\n", "5\nhttps://a/1\n\n", DisplayName = "unknown version")]
        [DataRow("\nHTTPS://a/1\nuser:pass\n", "\nHTTPS://a/1\n\n", DisplayName = "blank version line")]
        [DataRow("x\nhttp://a/1\nuser:pass\n", "x\nhttp://a/1\n\n", DisplayName = "version not a number")]
        [DataRow("4\nuser:pass\nimg:pass\n", "4\nuser:pass\nimg:pass\n", DisplayName = "no URL line")]
        [DataRow("", "", DisplayName = "empty")]
        public void PlaintextLoginsAreBlankedInAMalformedFile(string content, string expected) {
            byte[] blanked = ThreadListFile.BlankPlaintextAuth(Encoding.UTF8.GetBytes(content));

            Assert.AreEqual(expected, Encoding.UTF8.GetString(blanked));
        }

        private static string BlankThreadListText(string[] lines) {
            byte[] content = Encoding.UTF8.GetBytes(String.Join("\r\n", lines) + "\r\n");
            return Encoding.UTF8.GetString(ThreadListFile.BlankPlaintextAuth(content));
        }

        private static string[] Version4LinesWithLogins() {
            return new[] { "4",
                "https://boards.4chan.org/a/thread/1", "u:p", "i:p", "600", "0", "a_1", "1", "Parent", Ticks(AddedOnUtc), "", "", "Cat", "1",
                "https://boards.4chan.org/a/thread/2", "u2:p", "", "0", "1", "", "", "Child", Ticks(AddedOnUtc), Ticks(LastImageOnUtc), "a_1", "", "0" };
        }

        // A missing or extra line shifts every later thread, which a position rule would get
        // wrong (removing a value that isn't a login and keeping a login)
        [TestMethod]
        [DataRow(4, -1, DisplayName = "missing line")]
        [DataRow(12, 1, DisplayName = "extra line")]
        public void LoginsAfterAMissingOrExtraLineAreBlanked(int index, int change) {
            List<string> lines = new List<string>(Version4LinesWithLogins());
            if (change < 0) lines.RemoveAt(index); else lines.Insert(index, "extra");
            List<string> expected = new List<string>(lines);
            foreach (string login in new[] { "u:p", "i:p", "u2:p" }) {
                expected[expected.IndexOf(login)] = "";
            }

            Assert.AreEqual(String.Join("\r\n", expected) + "\r\n", BlankThreadListText(lines.ToArray()));
        }

        // A description or category holding a URL is where the file version puts it, so the
        // two lines after it (dates, or the auto follow flag) are kept
        [TestMethod]
        [DataRow(8, DisplayName = "description")]
        [DataRow(12, DisplayName = "category")]
        public void UrlInAFreeTextFieldDoesNotStartAThread(int index) {
            string[] lines = Version4LinesWithLogins();
            lines[index] = "https://example.com/x";
            string[] expected = (string[])lines.Clone();
            expected[2] = expected[3] = expected[15] = "";

            Assert.AreEqual(String.Join("\r\n", expected) + "\r\n", BlankThreadListText(lines));
        }

        // Well-formed files of every version: the same lines a position rule would remove
        [TestMethod]
        public void WellFormedFilesOfEveryVersionHaveTheirLoginsBlankedByPosition() {
            foreach (string[] lines in new[] { Version1Lines(), Version2Lines(), Version3Lines(), Version4Lines(), Version4LinesWithLogins() }) {
                int linesPerThread = ThreadListFile.GetLinesPerThread(Int32.Parse(lines[0]));
                string[] expected = (string[])lines.Clone();
                for (int i = 2; i < expected.Length; i += linesPerThread) {
                    expected[i] = expected[i + 1] = "";
                }

                Assert.AreEqual(String.Join("\r\n", expected) + "\r\n", BlankThreadListText(lines), lines[0]);
            }
        }

        private static IEnumerable<object[]> Utf16And32Encodings() {
            yield return new object[] { new UnicodeEncoding(false, true) };
            yield return new object[] { new UnicodeEncoding(true, true) };
            yield return new object[] { new UTF32Encoding(false, true) };
            yield return new object[] { new UTF32Encoding(true, true) };
        }

        private static byte[] Encode(Encoding encoding, string text) {
            return System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(encoding.GetPreamble(), encoding.GetBytes(text)));
        }

        // Decoded, blanked and encoded again with the same byte order mark
        [TestMethod]
        [DynamicData(nameof(Utf16And32Encodings))]
        public void FileWithAUtf16Or32ByteOrderMarkIsBlankedInItsEncoding(Encoding encoding) {
            byte[] content = Encode(encoding, "9\r\nhttps://a/1\r\nuser:pass\r\ndpapi:AAAA\r\n60\r\n");
            byte[] noPlaintext = Encode(encoding, "9\r\nhttps://a/1\r\n\r\ndpapi:AAAA\r\n60\r\n");

            CollectionAssert.AreEqual(noPlaintext, ThreadListFile.BlankPlaintextAuth(content));
            Assert.AreSame(noPlaintext, ThreadListFile.BlankPlaintextAuth(noPlaintext));
        }

        // When the swap fails and the file is no longer there (as File.Replace can leave it),
        // the temporary file holds the only copy of the content, so it is kept
        [TestMethod]
        public void TempFileIsKeptWhenTheSwapFailsAndTheFileIsGone() {
            string target = Path.Combine(_dir, "target.txt");
            Directory.CreateDirectory(target);
            byte[] content = Encoding.ASCII.GetBytes("content");

            Assert.ThrowsExactly<IOException>(() => TextFile.WriteAllBytesAtomic(target, content));

            // On Unix the temporary file's name has a unique part
            string[] temps = Directory.GetFiles(_dir, "~target.txt*.tmp");
            Assert.HasCount(1, temps);
            if (OperatingSystem.IsWindows()) Assert.AreEqual(Path.Combine(_dir, "~target.txt.tmp"), temps[0]);
            CollectionAssert.AreEqual(content, File.ReadAllBytes(temps[0]));
        }

        private static IEnumerable<string> LinesWrittenAroundAnotherWrite(string path) {
            yield return "outer 1";
            TextFile.WriteAllLinesAtomic(path, new[] { "inner" });
            yield return "outer 2";
        }

        // A second write of the file starts while the first still writes its temporary file.
        // Each has its own (a shared one could be swapped in half written on Unix, where a
        // rename ignores locks, and made the second write fail on Windows), so both finish and
        // the last swap wins with complete content.
        [TestMethod]
        public void TwoWritesOfTheSameFileDoNotShareATempFile() {
            File.WriteAllLines(_path, new[] { "old" });

            TextFile.WriteAllLinesAtomic(_path, LinesWrittenAroundAnotherWrite(_path));

            CollectionAssert.AreEqual(new[] { "outer 1", "outer 2" }, File.ReadAllLines(_path));
            CollectionAssert.AreEqual(new[] { _path }, Directory.GetFiles(_dir));
        }

        // The name a write's temporary file has
        private string CrashedWriteTempPath(string fileName) {
            return Path.Combine(_dir, "~" + fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        }

        // Written a minute before (stale) or after (recent) a temporary file counts as left by a crash
        private static void WriteTempFile(string path, bool stale) {
            File.WriteAllText(path, "half writ");
            TimeSpan margin = TimeSpan.FromMinutes(stale ? 1 : -1);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TextFile.StaleTempFileAge - margin);
        }

        // A crash between creating the temporary file and swapping it in leaves it behind: the
        // next write deletes it. The backup's own temporary file is not taken for the thread
        // list's.
        [TestMethod]
        public void TempFileLeftByACrashIsGoneAfterTheNextWrite() {
            string stale = CrashedWriteTempPath("threads.txt");
            string backupStale = CrashedWriteTempPath("threads.txt.bak");
            File.WriteAllLines(_path, new[] { "old" });
            WriteTempFile(stale, true);
            WriteTempFile(backupStale, true);

            TextFile.WriteAllLinesAtomic(_path, new[] { "new" });

            CollectionAssert.AreEqual(new[] { "new" }, File.ReadAllLines(_path));
            CollectionAssert.AreEquivalent(new[] { _path, backupStale }, Directory.GetFiles(_dir));
        }

        // A temporary file that is locked or was written in the last 10 minutes may belong to a
        // write in progress
        [TestMethod]
        public void TempFileOfAWriteInProgressIsLeft() {
            string locked = Path.Combine(_dir, "~threads.txt." + Guid.NewGuid().ToString("N") + ".tmp");
            string recent = Path.Combine(_dir, "~threads.txt." + Guid.NewGuid().ToString("N") + ".tmp");
            WriteTempFile(locked, true);
            WriteTempFile(recent, false);

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) {
                TextFile.WriteAllLinesAtomic(_path, new[] { "new" });
            }

            CollectionAssert.AreEqual(new[] { "new" }, File.ReadAllLines(_path));
            CollectionAssert.AreEquivalent(new[] { _path, locked, recent }, Directory.GetFiles(_dir));
        }

        // Versions before 1.40 wrote "<name>.tmp" on Windows. One left by a crash is deleted
        // like the others; one that is locked or recent may be an older version's write in
        // progress (e.g. on another computer sharing the folder) and is left.
        [TestMethod]
        public void FixedNameTempFileOfAnOlderVersionIsDeletedWhenStale() {
            string legacy = _path + ".tmp";
            WriteTempFile(legacy, true);

            TextFile.WriteAllLinesAtomic(_path, new[] { "new" });

            CollectionAssert.AreEqual(new[] { _path }, Directory.GetFiles(_dir));
        }

        [TestMethod]
        public void FixedNameTempFileOfAnOlderVersionIsLeftWhenLockedOrRecent() {
            string legacy = _path + ".tmp";
            string backupPath = _path + ".bak";
            string backupLegacy = backupPath + ".tmp";
            WriteTempFile(legacy, true);
            WriteTempFile(backupLegacy, false);

            using (new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.None)) {
                TextFile.WriteAllLinesAtomic(_path, new[] { "new" });
            }
            TextFile.WriteAllLinesAtomic(backupPath, new[] { "backup" });

            CollectionAssert.AreEquivalent(new[] { _path, legacy, backupPath, backupLegacy }, Directory.GetFiles(_dir));
        }

        // Another program (e.g. a backup tool or an editor) has the file open. A rename over it
        // fails on Windows then, also with delete sharing, so the write falls back to
        // File.Replace, which delete sharing lets through.
        [TestMethod]
        public void WriteSucceedsWhileAnotherProgramHasTheFileOpen() {
            File.WriteAllLines(_path, new[] { "old" });

            using (FileStream open = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) {
                TextFile.WriteAllLinesAtomic(_path, new[] { "new" });
            }

            CollectionAssert.AreEqual(new[] { "new" }, File.ReadAllLines(_path));
            CollectionAssert.AreEqual(new[] { _path }, Directory.GetFiles(_dir));
        }

        // A read-only file can't be replaced (neither by a rename nor by File.Replace, as before):
        // the write fails without waiting for it, keeps the content and leaves no temporary file
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void WriteOverAReadOnlyFileFailsAndLeavesNoTempFile() {
            File.WriteAllLines(_path, new[] { "old" });
            File.SetAttributes(_path, FileAttributes.ReadOnly);
            try {
                Assert.ThrowsExactly<UnauthorizedAccessException>(() => TextFile.WriteAllLinesAtomic(_path, new[] { "new" }));

                CollectionAssert.AreEqual(new[] { "old" }, File.ReadAllLines(_path));
                CollectionAssert.AreEqual(new[] { _path }, Directory.GetFiles(_dir));
            }
            finally {
                File.SetAttributes(_path, FileAttributes.Normal);
            }
        }

        // A write that fails before the swap deletes its own temporary file
        [TestMethod]
        public void FailedAtomicWriteLeavesNoTempFile() {
            File.WriteAllLines(_path, new[] { "old" });

            Assert.ThrowsExactly<IOException>(() => TextFile.WriteAllLinesAtomic(_path, LinesThatFailAfter(10)));

            CollectionAssert.AreEqual(new[] { _path }, Directory.GetFiles(_dir));
        }

        private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        // The temporary file renamed over the file takes its permissions, so e.g. a settings file
        // readable by its owner only doesn't become readable by others
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [UnsupportedOSPlatform("windows")]
        public void WriteKeepsTheFilePermissionsOnUnix() {
            File.WriteAllLines(_path, new[] { "old" });
            File.SetUnixFileMode(_path, OwnerOnly);

            TextFile.WriteAllLinesAtomic(_path, new[] { "new" });

            Assert.AreEqual(OwnerOnly, File.GetUnixFileMode(_path));
            CollectionAssert.AreEqual(new[] { "new" }, File.ReadAllLines(_path));
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [UnsupportedOSPlatform("windows")]
        public void NewFileIsReadableByItsOwnerOnlyOnUnix() {
            TextFile.WriteAllBytesAtomic(_path, Encoding.ASCII.GetBytes("new"));

            Assert.AreEqual(OwnerOnly, File.GetUnixFileMode(_path));
        }

        // The file the link points to gets the content, and the link stays a link
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [UnsupportedOSPlatform("windows")]
        public void SymbolicLinkStaysALinkOnUnix() {
            string realDir = Path.Combine(_dir, "real");
            Directory.CreateDirectory(realDir);
            string realPath = Path.Combine(realDir, "threads.txt");
            File.WriteAllLines(realPath, new[] { "old" });
            File.CreateSymbolicLink(_path, realPath);

            TextFile.WriteAllLinesAtomic(_path, new[] { "new" });

            Assert.AreEqual(realPath, new FileInfo(_path).LinkTarget);
            CollectionAssert.AreEqual(new[] { "new" }, File.ReadAllLines(realPath));
            CollectionAssert.AreEqual(new[] { realPath }, Directory.GetFiles(realDir));
        }

        // A copy made on Windows (CRLF line breaks, the name PreserveCopy gives) is found and
        // blanked on every OS. One whose name differs in case is another file's on Unix
        // ("Threads.txt" can sit next to "threads.txt") and is left; Windows ignores case.
        [TestMethod]
        public void CopiesAreFoundByTheirExactNameOnUnix() {
            string copy = CopyPath("20200101-000000-000");
            string otherCase = Path.Combine(_dir, "Threads.txt.corrupt-20200102-000000-000");
            byte[] windowsCopy = Encoding.UTF8.GetBytes(String.Join("\r\n", OldCopyLines) + "\r\n");
            File.WriteAllBytes(copy, windowsCopy);
            File.WriteAllBytes(otherCase, windowsCopy);

            TextFile.RewriteCopies(_path, ThreadListFile.BlankPlaintextAuth);

            byte[] blanked = Encoding.UTF8.GetBytes(String.Join("\r\n", BlankedOldCopyLines) + "\r\n");
            CollectionAssert.AreEqual(blanked, File.ReadAllBytes(copy));
            CollectionAssert.AreEqual(OperatingSystem.IsWindows() ? blanked : windowsCopy, File.ReadAllBytes(otherCase));
            CollectionAssert.AreEquivalent(new[] { copy, otherCase }, Directory.GetFiles(_dir));
        }

        [TestMethod]
        public void CopyThatFailsToBeWrittenIsDeleted() {
            File.WriteAllLines(_path, Version4Lines());

            Assert.ThrowsExactly<InvalidOperationException>(() => TextFile.PreserveCopy(_path, content => { throw new InvalidOperationException(); }));

            CollectionAssert.AreEqual(new[] { _path }, Directory.GetFiles(_dir));
        }

        // Too large to have been written by this program, and not read again every session
        [TestMethod]
        public void CopyLargerThanTheLimitIsNotRewritten() {
            string copy = CopyPath("20200101-000000-000");
            File.WriteAllLines(_path, Version4Lines());
            File.WriteAllLines(copy, OldCopyLines);
            using (FileStream fs = new FileStream(copy, FileMode.Open, FileAccess.Write)) {
                fs.SetLength(TextFile.MaxRewrittenCopySize + 1);
            }
            ThreadListStore store = new ThreadListStore();
            LoadLikeTheForm(store, _path);
            string logPath = Path.Combine(Settings.GetSettingsDirectory(), Settings.LogFileName);
            Logger.Log("ThreadListFileTests marker");
            int start = ReadSharedFile(logPath).Length;

            Assert.IsTrue(store.Save(_path, new List<ThreadInfo>()));

            Assert.AreEqual(TextFile.MaxRewrittenCopySize + 1, new FileInfo(copy).Length);
            string log = ReadSharedFile(logPath).Substring(start);
            Assert.AreEqual(1, log.Split(new[] { Path.GetFileName(copy) + " was not checked for plaintext logins" }, StringSplitOptions.None).Length - 1);
        }

        private static IEnumerable<string> LinesThatFailAfter(int count) {
            for (int i = 0; i < count; i++) {
                yield return "new " + i;
            }
            throw new IOException("Simulated failure while writing");
        }

        [TestMethod]
        public void FailedAtomicWriteKeepsTheOldContent() {
            File.WriteAllLines(_path, new[] { "old 1", "old 2" });

            Assert.ThrowsExactly<IOException>(() => TextFile.WriteAllLinesAtomic(_path, LinesThatFailAfter(1000)));

            CollectionAssert.AreEqual(new[] { "old 1", "old 2" }, File.ReadAllLines(_path));
        }

        [TestMethod]
        public void AtomicWriteReplacesTheContentAndLeavesNoTempFile() {
            File.WriteAllLines(_path, new[] { "old 1", "old 2", "old 3" });

            TextFile.WriteAllLinesAtomic(_path, new[] { "new" });

            CollectionAssert.AreEqual(new[] { "new" }, File.ReadAllLines(_path));
            CollectionAssert.AreEqual(new[] { _path }, Directory.GetFiles(_dir));
        }

        [TestMethod]
        public void AtomicWriteCreatesAMissingFile() {
            TextFile.WriteAllLinesAtomic(_path, new[] { "a", "" });

            CollectionAssert.AreEqual(new[] { "a", "" }, File.ReadAllLines(_path));
        }
    }
}
