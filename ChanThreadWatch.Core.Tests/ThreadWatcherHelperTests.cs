using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The helpers under test are private static members of ThreadWatcher, so they are
    // invoked through reflection.
    [TestClass]
    public class ThreadWatcherHelperTests {
        private static T Invoke<T>(string methodName, params object[] args) {
            MethodInfo method = typeof(ThreadWatcher).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Missing helper: " + methodName);
            try {
                return (T)method.Invoke(null, args);
            }
            catch (TargetInvocationException ex) {
                throw ex.InnerException;
            }
        }

        [TestMethod]
        [DataRow("image", 1, ".jpg", "image.jpg")]
        [DataRow("image", 2, ".jpg", "image_2.jpg")]
        [DataRow("image", 10, ".png", "image_10.png")]
        [DataRow("image", 3, "", "image_3")]
        public void GetNumberedFileNameOmitsSuffixOnlyForFirstNumber(string name, int number, string extension, string expected) {
            Assert.AreEqual(expected, Invoke<string>("GetNumberedFileName", name, number, extension));
        }

        [TestMethod]
        [DataRow(0, "12345.html")]
        [DataRow(1, "12345_2.html")]
        [DataRow(4, "12345_5.html")]
        public void GetPageFileNameNumbersPagesFromOne(int pageIndex, string expected) {
            Assert.AreEqual(expected, Invoke<string>("GetPageFileName", "12345", pageIndex));
        }

        [TestMethod]
        public void GetUnusedFileNameSkipsUsedNames() {
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.jpg", "A_2.JPG", "a_4.jpg" };
            Assert.AreEqual("a_3.jpg", Invoke<string>("GetUnusedFileName", "a", ".jpg", used));
            Assert.AreEqual("b.jpg", Invoke<string>("GetUnusedFileName", "b", ".jpg", used));
        }

        [TestMethod]
        public void ChooseSaveBaseFileNamePrefersOriginalOnlyWhenAllowed() {
            ImageInfo image = new ImageInfo { URL = "https://i.4cdn.org/g/1700000000000.jpg", OriginalFileName = "cat.jpg" };
            Assert.AreEqual("cat.jpg", Invoke<string>("ChooseSaveBaseFileName", image, true, false));
            Assert.AreEqual("1700000000000.jpg", Invoke<string>("ChooseSaveBaseFileName", image, false, false));
            Assert.AreEqual("1700000000000.jpg", Invoke<string>("ChooseSaveBaseFileName", image, true, true));

            ImageInfo noOriginal = new ImageInfo { URL = "https://i.4cdn.org/g/1700000000000.jpg", OriginalFileName = "" };
            Assert.AreEqual("1700000000000.jpg", Invoke<string>("ChooseSaveBaseFileName", noOriginal, true, false));
        }

        [TestMethod]
        public void CountNotSkippedIgnoresSkippedDownloads() {
            Dictionary<string, DownloadInfo> downloads = new Dictionary<string, DownloadInfo> {
                { "a", new DownloadInfo { Skipped = false } },
                { "b", new DownloadInfo { Skipped = true } },
                { "c", new DownloadInfo { Skipped = false } }
            };
            Assert.AreEqual(2, Invoke<int>("CountNotSkipped", downloads));
            Assert.AreEqual(0, Invoke<int>("CountNotSkipped", new Dictionary<string, DownloadInfo>()));
        }

        [TestMethod]
        public void IsIncompleteDownloadComparesAgainstTotalAndPreviousTry() {
            Assert.IsFalse(Invoke<bool>("IsIncompleteDownload", null, 5L, null), "unknown total size");
            Assert.IsFalse(Invoke<bool>("IsIncompleteDownload", (long?)10, 10L, null), "sizes match");
            Assert.IsTrue(Invoke<bool>("IsIncompleteDownload", (long?)10, 5L, null), "first short try");
            Assert.IsFalse(Invoke<bool>("IsIncompleteDownload", (long?)10, 5L, (long?)5), "same short size as previous try");
            Assert.IsTrue(Invoke<bool>("IsIncompleteDownload", (long?)10, 5L, (long?)4), "different short size than previous try");
        }

        [TestMethod]
        public void IsIncorrectHashComparesAgainstExpectedAndPreviousTry() {
            byte[] hash = { 1, 2, 3 };
            byte[] expected = { 9, 9, 9 };
            Assert.IsFalse(Invoke<bool>("IsIncorrectHash", HashType.None, null, expected, null), "no hash type");
            Assert.IsFalse(Invoke<bool>("IsIncorrectHash", HashType.MD5, hash, new byte[] { 1, 2, 3 }, null), "hash matches");
            Assert.IsTrue(Invoke<bool>("IsIncorrectHash", HashType.MD5, hash, expected, null), "first mismatch");
            Assert.IsFalse(Invoke<bool>("IsIncorrectHash", HashType.MD5, hash, expected, new byte[] { 1, 2, 3 }), "same mismatch as previous try");
            Assert.IsTrue(Invoke<bool>("IsIncorrectHash", HashType.MD5, hash, expected, new byte[] { 4, 5, 6 }), "different mismatch than previous try");
        }

        [TestMethod]
        public void GetDeadLinkInnerHTMLIncludesBoardOnlyForOtherBoards() {
            Assert.AreEqual(">>123", Invoke<string>("GetDeadLinkInnerHTML", new[] { "", "g", "123" }, "g"));
            Assert.AreEqual(">>>/v/456", Invoke<string>("GetDeadLinkInnerHTML", new[] { "", "v", "456" }, "g"));
        }
    }
}
