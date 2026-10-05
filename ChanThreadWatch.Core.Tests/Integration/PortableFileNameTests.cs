using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    // Image file names that depend on the OS's file system rules (MP-4b)
    [TestClass]
    public class PortableFileNameTests : ThreadWatcherIntegrationTestBase {
        // The fixture's original file names of images 1 and 2, which these tests replace
        private const string FirstOriginalName = "mountain &amp; lake.jpg";
        private const string SecondOriginalName = "title=\"a very long file name.png\"";

        private ThreadWatcher RunWithOriginalNames(FourChanThreadFixture fixture, string firstName, string secondName) {
            LoopbackHttpServer server = StartServer();
            string html = fixture.Html(server.BaseURL(), server.BaseURL());
            Assert.Contains(FirstOriginalName, html);
            Assert.Contains(SecondOriginalName, html);
            html = html.Replace(FirstOriginalName, firstName).Replace(SecondOriginalName, "title=\"" + secondName + "\"");
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Html(html));
            fixture.RouteImages(server);
            fixture.RouteThumbs(server);
            Settings.UseOriginalFileNames = true;
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            Assert.AreEqual(StopReason.DownloadComplete, RunToStop(watcher));
            return watcher;
        }

        private static string[] FileNamesIn(string dir) {
            return Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
        }

        // Names that differ only in case are one file on Windows and macOS (case-insensitive) but two on
        // Linux. The used names are compared ignoring case on every OS, so the second image is numbered:
        // it neither overwrites the first on Windows and macOS nor gets a name that only Linux keeps apart,
        // and a folder moved between OSes finds the same files.
        [TestMethod]
        public void OriginalNamesThatDifferOnlyInCaseAreSavedAsTwoFiles() {
            var fixture = new FourChanThreadFixture();

            ThreadWatcher watcher = RunWithOriginalNames(fixture, "Photo.jpg", "photo.jpg");

            string threadDir = watcher.ThreadDownloadDirectory;
            string[] names = FileNamesIn(threadDir);
            CollectionAssert.Contains(names, "Photo.jpg");
            CollectionAssert.Contains(names, "photo_2.jpg");
            CollectionAssert.DoesNotContain(names, "photo.jpg");
            CollectionAssert.AreEqual(fixture.Images[FourChanThreadFixture.ImagePaths[0]], File.ReadAllBytes(Path.Combine(threadDir, "Photo.jpg")));
            CollectionAssert.AreEqual(fixture.Images[FourChanThreadFixture.ImagePaths[1]], File.ReadAllBytes(Path.Combine(threadDir, "photo_2.jpg")));
        }

        // A description that gives no folder name ("..." cleans to "") leaves the folder where it is,
        // rather than renaming it to the download folder itself
        [TestMethod]
        [DataRow("...")]
        [DataRow("/ \\")]
        public void ADescriptionWithoutAFolderNameDoesNotRename(string description) {
            Settings.RenameDownloadFolderWithDescription = true;
            ThreadWatcher watcher = new ThreadWatcher("http://127.0.0.1:1/wg/thread/100");
            string threadDir = Path.Combine(DownloadDir, "Thread");
            Directory.CreateDirectory(threadDir);
            watcher.ThreadDownloadDirectory = threadDir;

            watcher.Description = description;

            Assert.AreEqual(threadDir, watcher.ThreadDownloadDirectory);
            Assert.IsTrue(Directory.Exists(threadDir));
        }

        // The saved page links to the file by its name with '#', '%' and space percent-encoded, so a browser
        // opens that file rather than reading a fragment or an escape
        [TestMethod]
        public void TheSavedPageLinksToAFileWhoseNameHoldsURLCharacters() {
            var fixture = new FourChanThreadFixture();

            ThreadWatcher watcher = RunWithOriginalNames(fixture, "a#b%c d.jpg", "plain.png");

            Assert.IsTrue(File.Exists(Path.Combine(watcher.ThreadDownloadDirectory, "a#b%c d.jpg")));
            string html = File.ReadAllText(SavedPagePath(watcher));
            Assert.Contains("href=\"a%23b%25c%20d.jpg\"", html);
            Assert.Contains("href=\"plain.png\"", html);
        }

        // 100 CJK characters are 300 UTF-8 bytes: within the 259-character path limit, but over the
        // 255-byte limit of one path part on Linux and macOS, where the URL file name is used instead
        [TestMethod]
        public void AnOriginalNameOverTheByteLimitFallsBackToTheURLNameOffWindows() {
            var fixture = new FourChanThreadFixture();
            string longName = new string('日', 100) + ".jpg";
            string encodedName = String.Concat(Enumerable.Repeat("&#x65E5;", 100)) + ".jpg";
            Assert.IsGreaterThan(255, Encoding.UTF8.GetByteCount(longName));

            ThreadWatcher watcher = RunWithOriginalNames(fixture, encodedName, "second.png");

            string expectedName = OperatingSystem.IsWindows() ? longName : FourChanThreadFixture.FileName(FourChanThreadFixture.ImagePaths[0]);
            string[] names = FileNamesIn(watcher.ThreadDownloadDirectory);
            CollectionAssert.Contains(names, expectedName);
            CollectionAssert.AreEqual(fixture.Images[FourChanThreadFixture.ImagePaths[0]], File.ReadAllBytes(Path.Combine(watcher.ThreadDownloadDirectory, expectedName)));
            CollectionAssert.Contains(names, "second.png");
        }
    }
}
