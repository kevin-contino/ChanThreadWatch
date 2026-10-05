using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // MP-4a: General.GetRelativeDirectoryPath moved from Uri.MakeRelativeUri to Path.GetRelativePath.
    // OldGetRelativeDirectoryPath is the earlier version, kept as an oracle: every case must give the
    // same result, except the listed deliberate fixes. The paths are made up and never touched on disk.
    [TestClass]
    public class RelativeDirectoryPathTests {
        // dir, baseDir; the old and new versions agree
        private static readonly string[][] WindowsKeptCases = {
            new[] { @"C:\base\sub", @"C:\base" },
            new[] { @"C:\base\a\b\c", @"C:\base" },
            new[] { @"C:\base", @"C:\base" },
            new[] { @"C:\base\", @"C:\base" },
            new[] { @"C:\base", @"C:\base\" },
            new[] { @"C:\base\sub\", @"C:\base" },
            new[] { @"C:\base\sub", @"C:\base\" },
            new[] { @"C:\base\sub\", @"C:\base\" },
            new[] { @"C:\", @"C:\base" },
            new[] { @"C:\other", @"C:\base" },
            new[] { @"C:\base", @"C:\base\sub" },
            new[] { @"C:\base", @"C:\base\a\b" },
            new[] { @"C:\x\y", @"C:\base\a" },
            new[] { @"C:\base\%", @"C:\base" },
            new[] { @"C:\base\100%", @"C:\base" },
            new[] { @"C:\base\%2F", @"C:\base" },
            new[] { @"C:\base\%25", @"C:\base" },
            new[] { @"C:\base\a#b", @"C:\base" },
            new[] { @"C:\base\#", @"C:\base" },
            new[] { @"C:\base\a?b", @"C:\base" },
            new[] { @"C:\base\with space", @"C:\base" },
            new[] { @"C:\base dir\sub", @"C:\base dir" },
            new[] { "C:\\base\\caf\u00E9", @"C:\base" },
            new[] { "C:\\base\\\u65E5\u672C", @"C:\base" },
            new[] { @"C:\base\%41", @"C:\base\%41" },
            new[] { @"C:\%41\sub", @"C:\%41" },
            new[] { @"C:\base#\sub", @"C:\base#" },
            new[] { @"C:\BASE\sub", @"C:\base" },
            new[] { @"c:\base\sub", @"C:\base" },
            new[] { @"C:/base/sub", @"C:\base" },
            new[] { @"D:\base\sub", @"C:\base" },
            new[] { @"D:\", @"C:\base" },
            new[] { @"\\server\share\sub", @"\\server\share" },
            new[] { @"\\server\share", @"\\server\share\a" },
            new[] { @"C:\base\..\base\sub", @"C:\base" },
            new[] { @"C:\base\.\sub", @"C:\base" },
            new[] { @"relative\sub", @"C:\base" },
            new[] { "", @"C:\base" }
        };

        // dir, baseDir, old result, new result: the deliberate fixes
        private static readonly string[][] WindowsFixedCases = {
            // The old version unescaped the folder name as if it were a URL
            new[] { @"C:\base\%41", @"C:\base", "A", "%41" },
            // Worst case of the unescaping: a thread folder named %2e%2e was saved as the parent of the
            // download folder, so "Delete folder" could delete that parent
            new[] { @"C:\base\%2e%2e", @"C:\base", "..", "%2e%2e" },
            new[] { @"C:\base\%7e", @"C:\base", "~", "%7e" },
            new[] { @"C:\base\a%C3%A9", @"C:\base", "a\u00E9", "a%C3%A9" },
            new[] { @"C:\base\%41%42", @"C:\base", "AB", "%41%42" },
            // A doubled separator became a rooted \sub; Path.GetRelativePath normalizes it
            new[] { @"C:\base\\sub", @"C:\base", @"\sub", "sub" },
            // A trailing space or dot is dropped by Windows path normalization; app-made folder names
            // never end in one, because CleanFileName trims them
            new[] { @"C:\base\sub.", @"C:\base", "sub.", "sub" },
            new[] { @"C:\base\sub ", @"C:\base", "sub ", "sub" },
            // A UNC folder under a local base became a broken file: path; it now stays absolute
            new[] { @"\\server\share\sub", @"C:\base", @"file:\server\share\sub", @"\\server\share\sub" },
            // A local folder under a UNC base became a broken file: path; it now stays absolute
            new[] { @"C:\base\sub", @"\\server\share", @"file:\C:\base\sub", @"C:\base\sub" },
            // Uri saw two shares on one server as one tree; a different share now stays absolute
            new[] { @"\\server\other\sub", @"\\server\share", @"..\other\sub", @"\\server\other\sub" }
        };

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void KeptCasesMatchTheOldImplementation() {
            foreach (string[] c in WindowsKeptCases) {
                string old = OldGetRelativeDirectoryPath(c[0], c[1]);
                Assert.AreEqual(old, General.GetRelativeDirectoryPath(c[0], c[1]), c[0] + " vs " + c[1]);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void FixedCasesDifferFromTheOldImplementationAsListed() {
            foreach (string[] c in WindowsFixedCases) {
                Assert.AreEqual(c[2], OldGetRelativeDirectoryPath(c[0], c[1]), "old: " + c[0] + " vs " + c[1]);
                Assert.AreEqual(c[3], General.GetRelativeDirectoryPath(c[0], c[1]), "new: " + c[0] + " vs " + c[1]);
            }
        }

        // A drive-relative folder (rooted, no drive letter) made the old version throw; it now resolves
        // against the current drive, so the base uses the same drive
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void DriveRelativeFolderNoLongerThrows() {
            string baseDir = Path.GetPathRoot(Environment.CurrentDirectory) + "base";

            Assert.ThrowsExactly<UriFormatException>(() => OldGetRelativeDirectoryPath(@"\base\sub", baseDir));
            Assert.AreEqual("sub", General.GetRelativeDirectoryPath(@"\base\sub", baseDir));
        }

        // dir, baseDir, new result: Uri could not parse the device path or the relative base, so the
        // old version threw; a device path is another root and stays absolute
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [DataRow(@"\\?\C:\base\sub", @"C:\base", @"\\?\C:\base\sub")]
        [DataRow(@"\\.\C:\base\sub", @"C:\base", @"\\.\C:\base\sub")]
        [DataRow(@"C:\base\sub", @"\\?\C:\base", @"C:\base\sub")]
        public void DevicePathsNoLongerThrow(string dir, string baseDir, string expected) {
            Assert.ThrowsExactly<UriFormatException>(() => OldGetRelativeDirectoryPath(dir, baseDir));
            Assert.AreEqual(expected, General.GetRelativeDirectoryPath(dir, baseDir));
        }

        // A relative base made the old version throw; it now resolves against the current folder
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void RelativeBaseNoLongerThrows() {
            const string dir = @"C:\base\sub";

            Assert.ThrowsExactly<UriFormatException>(() => OldGetRelativeDirectoryPath(dir, "base"));
            string relative = General.GetRelativeDirectoryPath(dir, "base");
            Assert.AreEqual(dir, Path.GetFullPath(Path.Combine(Path.GetFullPath("base"), relative)));
        }

        // Rooted on every OS (drive-relative on Windows, so the result uses that OS's separator)
        [TestMethod]
        [DataRow("/home/u/base/sub", "/home/u/base", "sub")]
        [DataRow("/home/u/base/sub/", "/home/u/base", "sub")]
        [DataRow("/home/u/base/a/b", "/home/u/base/", "a/b")]
        [DataRow("/home/u/base", "/home/u/base", ".")]
        [DataRow("/home/u/other", "/home/u/base", "../other")]
        [DataRow("/home/u", "/home/u/base", "..")]
        [DataRow("/home/u/base/%41", "/home/u/base", "%41")]
        [DataRow("/home/u/base/%2e%2e", "/home/u/base", "%2e%2e")]
        [DataRow("/home/u/base/%7e", "/home/u/base", "%7e")]
        [DataRow("/home/u/base/a%C3%A9", "/home/u/base", "a%C3%A9")]
        [DataRow("/home/u/base/%41%42", "/home/u/base", "%41%42")]
        [DataRow("/home/u/base//sub", "/home/u/base", "sub")]
        [DataRow("/home/u/base/a#b c", "/home/u/base", "a#b c")]
        [DataRow("relative/sub", "/home/u/base", "relative/sub")]
        public void RootedPathsGiveTheRelativeFolder(string dir, string baseDir, string expected) {
            Assert.AreEqual(expected.Replace('/', Path.DirectorySeparatorChar), General.GetRelativeDirectoryPath(dir.Replace('/', Path.DirectorySeparatorChar), baseDir.Replace('/', Path.DirectorySeparatorChar)));
        }

        // A SaveDir written to the thread list loads back to the same folder (WatchSession.TryAddLoadedThread)
        [TestMethod]
        [DataRow("%41")]
        [DataRow("%2e%2e")]
        [DataRow("cat/%41 #2")]
        public void RelativeFolderLoadsBackToTheSameFolder(string name) {
            string baseDir = Path.Combine(Path.GetTempPath(), "ctw-base");
            string dir = Path.Combine(baseDir, name.Replace('/', Path.DirectorySeparatorChar));

            string saved = General.GetRelativeDirectoryPath(dir, baseDir);

            Assert.AreEqual(name.Replace('/', Path.DirectorySeparatorChar), saved);
            Assert.AreEqual(dir, General.GetAbsoluteDirectoryPath(saved, baseDir));
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void NetworkShareFolderUnderALocalBaseLoadsBackToTheSameFolder() {
            string saved = General.GetRelativeDirectoryPath(@"\\server\share\threads\%41", @"C:\base");

            Assert.AreEqual(@"\\server\share\threads\%41", saved);
            Assert.AreEqual(@"\\server\share\threads\%41", General.GetAbsoluteDirectoryPath(saved, @"C:\base"));
        }

        // The version before MP-4a, unchanged
        private static string OldGetRelativeDirectoryPath(string dir, string baseDir) {
            if (dir.Length != 0 && Path.IsPathRooted(dir)) {
                Uri baseDirUri = new Uri(Path.Combine(baseDir, "dummy.txt"));
                Uri targetDirUri = new Uri(Path.Combine(dir, "dummy.txt"));
                try {
                    dir = Uri.UnescapeDataString(baseDirUri.MakeRelativeUri(targetDirUri).ToString());
                }
                catch (UriFormatException) {
                    return dir;
                }
                dir = (dir.Length == 0) ? "." : Path.GetDirectoryName(dir.Replace('/', Path.DirectorySeparatorChar));
            }
            return dir;
        }
    }
}
