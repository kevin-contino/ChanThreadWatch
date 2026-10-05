using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Folder paths and file name limits on Windows, Linux and macOS (MP-4b). The rules of both OS
    // families are checked on every OS through the isWindows and limitUTF8Bytes parameters.
    [TestClass]
    public class PortablePathTests {
        [TestMethod]
        [DataRow(@"Cat\a_1", "Cat/a_1")]
        [DataRow(@"Cat\Sub\c_3", "Cat/Sub/c_3")]
        [DataRow(@"..\Elsewhere\d_4", "../Elsewhere/d_4")]
        [DataRow("Cat/a_1", "Cat/a_1")]
        [DataRow("/home/user/Threads", "/home/user/Threads")]
        [DataRow("Q: help", "Q: help")]
        [DataRow("A: misc/4chan_a_1", "A: misc/4chan_a_1")]
        [DataRow("c:/Threads", "c:/Threads")]
        [DataRow("C:Threads", "C:Threads")]
        public void UnixReadsBackslashesAsSeparators(string path, string expected) {
            Assert.AreEqual(expected, General.ToLocalDirectoryPath(path, "SaveDir", false));
        }

        [TestMethod]
        [DataRow(@"Cat\a_1")]
        [DataRow(@"C:\Threads")]
        [DataRow(@"\\server\share\Threads")]
        [DataRow("//server/share/Threads")]
        [DataRow(@"\Threads")]
        [DataRow("Cat/a_1")]
        public void WindowsKeepsItsOwnPathsUnchanged(string path) {
            Assert.AreEqual(path, General.ToLocalDirectoryPath(path, "SaveDir", true));
        }

        [TestMethod]
        [DataRow(null, false)]
        [DataRow("", false)]
        [DataRow(null, true)]
        [DataRow("", true)]
        public void AnEmptyPathIsKept(string path, bool isWindows) {
            Assert.AreEqual(path, General.ToLocalDirectoryPath(path, "SaveDir", isWindows));
        }

        [TestMethod]
        [DataRow(@"C:\Threads")]
        [DataRow(@"c:\Threads")]
        [DataRow(@"\\server\share\Threads")]
        [DataRow(@"\Threads")]
        public void AWindowsAbsolutePathFailsOnUnixWithTheSettingName(string path) {
            FormatException ex = Assert.ThrowsExactly<FormatException>(() => General.ToLocalDirectoryPath(path, "DownloadFolder", false));
            Assert.Contains("DownloadFolder", ex.Message);
            Assert.Contains(path, ex.Message);
        }

        [TestMethod]
        [DataRow("/home/user/Threads")]
        [DataRow("/")]
        public void AUnixAbsolutePathFailsOnWindowsWithTheSettingName(string path) {
            FormatException ex = Assert.ThrowsExactly<FormatException>(() => General.ToLocalDirectoryPath(path, "CompletedFolder", true));
            Assert.Contains("CompletedFolder", ex.Message);
        }

        // 100 CJK characters plus ".jpg" are 104 characters but 304 UTF-8 bytes
        [TestMethod]
        public void TheByteLimitOfOnePathPartAppliesOnlyOffWindows() {
            string cjkName = new string('\u65E5', 100) + ".jpg";
            Assert.IsTrue(General.IsFileNameLongerThan(cjkName, 200, true));
            Assert.IsFalse(General.IsFileNameLongerThan(cjkName, 200, false));
        }

        [TestMethod]
        public void TheByteLimitIs255UTF8Bytes() {
            // 127 two-byte characters plus "a" are 255 bytes, plus "ab" 256
            string atLimit = new string('\u00E9', 127) + "a";
            string overLimit = new string('\u00E9', 127) + "ab";
            Assert.IsFalse(General.IsFileNameLongerThan(atLimit, 300, true));
            Assert.IsTrue(General.IsFileNameLongerThan(overLimit, 300, true));
            Assert.IsFalse(General.IsFileNameLongerThan(new string('a', 255), 300, true));
            Assert.IsTrue(General.IsFileNameLongerThan(new string('a', 256), 300, true));
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void TheCharacterLimitAppliesOnEveryOS(bool limitUTF8Bytes) {
            Assert.IsFalse(General.IsFileNameLongerThan(new string('a', 200), 200, limitUTF8Bytes));
            Assert.IsTrue(General.IsFileNameLongerThan(new string('a', 201), 200, limitUTF8Bytes));
        }

        [TestMethod]
        [DataRow("a%b.jpg", "a%25b.jpg")]
        [DataRow("a?b.jpg", "a%3Fb.jpg")]
        [DataRow("a#b.jpg", "a%23b.jpg")]
        [DataRow("a b.jpg", "a%20b.jpg")]
        [DataRow("a%23b.jpg", "a%2523b.jpg")]
        public void ALinkPathEncodesEachURLCharacter(string name, string expected) {
            Assert.AreEqual(expected, General.ToLinkPath(name));
        }

        [TestMethod]
        public void ALinkPathEncodesControlCharacters() {
            for (int c = 0; c <= 0x1F; c++) {
                Assert.AreEqual("a%" + c.ToString("X2") + "b.jpg", General.ToLinkPath("a" + (char)c + "b.jpg"), "U+" + c.ToString("X4"));
            }
            Assert.AreEqual("a%7Fb.jpg", General.ToLinkPath("a\u007Fb.jpg"));
        }

        [TestMethod]
        [DataRow("1700000000001.jpg")]
        [DataRow("mountain&lake_(1).png")]
        [DataRow("../thumbs/1700000000001s.jpg")]
        public void ALinkPathKeepsANormalName(string path) {
            Assert.AreEqual(path, General.ToLinkPath(path));
        }

        // Every name in the path is encoded, and this OS's separator becomes '/'
        [TestMethod]
        public void ALinkPathEncodesEveryNameAndUsesSlashes() {
            string path = String.Join(Path.DirectorySeparatorChar, "..", "Cat #1", "Poster?", "100%.jpg");
            Assert.AreEqual("../Cat%20%231/Poster%3F/100%25.jpg", General.ToLinkPath(path));
        }

        // 300 two-byte characters are 600 bytes, cut to 127 characters (254 bytes) off Windows
        [TestMethod]
        public void AFolderNameIsCutTo255BytesOffWindows() {
            string name = new string('é', 300);
            Assert.AreEqual(new string('é', 127), General.CleanFolderName(name, true));
            Assert.AreEqual(name, General.CleanFolderName(name, false));
        }

        [TestMethod]
        public void CuttingAFolderNameKeepsSurrogatePairsWhole() {
            // 64 emoji are 256 bytes; 63 fit in 252
            string name = String.Concat(System.Linq.Enumerable.Repeat("\U0001F600", 64));
            Assert.AreEqual(String.Concat(System.Linq.Enumerable.Repeat("\U0001F600", 63)), General.CleanFolderName(name, true));
        }

        // A cut that ends in a dot or space is trimmed, as CleanFileName trims any name
        [TestMethod]
        public void ACutFolderNameDoesNotEndInADotOrSpace() {
            string name = new string('a', 253) + " .b";
            Assert.AreEqual(new string('a', 253), General.CleanFolderName(name, true));
        }

        [TestMethod]
        public void AShortFolderNameIsOnlyCleaned() {
            Assert.AreEqual("Cat_CON", General.CleanFolderName("Cat/_CON", true));
            Assert.AreEqual("_CON", General.CleanFolderName("CON", true));
        }

        // A missing folder that no folder matches, even ignoring case, is returned as written
        [TestMethod]
        public void AFolderLookupWithoutAMatchKeepsThePath() {
            string dir = Path.Combine(Path.GetTempPath(), "ctw-case-" + Guid.NewGuid().ToString("N"), "missing", "x");
            Assert.AreEqual(dir, General.FindDirectoryIgnoringCase(dir));
        }

        // A Windows-written anime\x finds the existing Anime/x on a case-sensitive file system
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void AFolderIsFoundIgnoringCaseOnLinux() {
            string baseDir = Path.Combine(Path.GetTempPath(), "ctw-case-" + Guid.NewGuid().ToString("N"));
            string existing = Path.Combine(baseDir, "Anime", "Thread_1");
            Directory.CreateDirectory(existing);
            try {
                Assert.AreEqual(existing, General.FindDirectoryIgnoringCase(Path.Combine(baseDir, "anime", "thread_1")));
                Assert.AreEqual(existing, General.FindDirectoryIgnoringCase(existing));
            }
            finally {
                Directory.Delete(baseDir, true);
            }
        }

        // Folders that differ only in case are both candidates, so neither is chosen
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void AnAmbiguousFolderMatchKeepsThePathOnLinux() {
            string baseDir = Path.Combine(Path.GetTempPath(), "ctw-case-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(baseDir, "Anime", "x"));
            Directory.CreateDirectory(Path.Combine(baseDir, "ANIME", "x"));
            try {
                string written = Path.Combine(baseDir, "anime", "x");
                Assert.AreEqual(written, General.FindDirectoryIgnoringCase(written));
            }
            finally {
                Directory.Delete(baseDir, true);
            }
        }

        // G5 with the real runtime: off Windows the default folder is $HOME/Documents/Watched Threads, or
        // $HOME/Watched Threads without a Documents folder
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void TheDefaultFolderFollowsHomeOffWindows() {
            string originalHome = Environment.GetEnvironmentVariable("HOME");
            string home = Path.Combine(Path.GetTempPath(), "ctw-home-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            // The real rule, under a temporary HOME (the run's redirect is put back below)
            string redirected = WatchSession.DefaultFoldersParentForTesting;
            WatchSession.DefaultFoldersParentForTesting = null;
            try {
                Environment.SetEnvironmentVariable("HOME", home);
                Assert.AreEqual(home, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "HOME could not be set for the process");
                Assert.AreEqual(Path.Combine(home, "Watched Threads"), WatchSession.GetDefaultFolder("Watched Threads"));
                Directory.CreateDirectory(Path.Combine(home, "Documents"));
                Assert.AreEqual(Path.Combine(home, "Documents", "Watched Threads"), WatchSession.GetDefaultFolder("Watched Threads"));
            }
            finally {
                Environment.SetEnvironmentVariable("HOME", originalHome);
                WatchSession.DefaultFoldersParentForTesting = redirected;
                Directory.Delete(home, true);
            }
        }

        [TestMethod]
        public void TheDefaultFoldersGoInDocumentsWhenItExists() {
            string documents = Path.GetTempPath();
            Assert.AreEqual(documents, WatchSession.GetDefaultFoldersParent(documents, "/home/user", false));
        }

        // G5: Linux and macOS fall back to the home folder (~/Watched Threads)
        [TestMethod]
        public void TheDefaultFoldersGoInTheHomeFolderWithoutDocumentsOffWindows() {
            string missing = Path.Combine(Path.GetTempPath(), "ctw-missing-" + Guid.NewGuid().ToString("N"));
            Assert.AreEqual("/home/user", WatchSession.GetDefaultFoldersParent(String.Empty, "/home/user", false));
            Assert.AreEqual("/home/user", WatchSession.GetDefaultFoldersParent(missing, "/home/user", false));
        }

        // Windows uses Documents as it did before, whatever GetFolderPath returns
        [TestMethod]
        public void WindowsAlwaysUsesDocuments() {
            Assert.AreEqual(String.Empty, WatchSession.GetDefaultFoldersParent(String.Empty, @"C:\Users\user", true));
        }

        [TestMethod]
        public void NoDocumentsAndNoHomeFolderFails() {
            Assert.ThrowsExactly<InvalidOperationException>(() => WatchSession.GetDefaultFoldersParent(String.Empty, String.Empty, false));
        }
    }
}
