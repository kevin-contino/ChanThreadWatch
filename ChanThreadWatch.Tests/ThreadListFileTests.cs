using System;
using System.Collections.Generic;
using System.IO;
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
