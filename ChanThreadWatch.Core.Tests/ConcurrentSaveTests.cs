using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Two programs saving the same thread list at once (TextFile.WriteAllLinesAtomic). Each
    // save swaps in a complete list, so the file always holds one writer's whole list (which
    // one wins is not defined) and no temporary file is left.
    [TestClass]
    public class ConcurrentSaveTests {
        private const int SavesPerWriter = 150;
        private static readonly TimeSpan ChildTimeout = TimeSpan.FromMinutes(3);
        private string _dir;
        private string _path;

        [TestInitialize]
        public void CreateTempDirectory() {
            _dir = Path.Combine(Path.GetTempPath(), "ctw-concurrent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "threads.txt");
        }

        [TestCleanup]
        public void DeleteTempDirectory() {
            Directory.Delete(_dir, true);
        }

        private static List<string> Urls(string writer) {
            return ChildProcess.ThreadList(writer).ConvertAll(thread => thread.URL);
        }

        private static List<string> Urls(ThreadListData data) {
            return data.Threads.ConvertAll(thread => thread.URL);
        }

        // Shares delete, which File.Replace needs. A rename over the file fails while this has
        // it open (on Windows, even with delete sharing), so the writer tries again and then
        // falls back to File.Replace, which can leave the file missing for a moment. Returns
        // null if the file can't be read at this moment.
        private string[] TryReadLines() {
            try {
                using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8)) {
                    return sr.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[..^1];
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                return null;
            }
        }

        // The file is exactly one writer's complete list: no thread missing, none from the other
        private static void AssertOneWritersCompleteList(string[] lines, List<string> a, List<string> b) {
            ThreadListData data = ThreadListFile.Parse(lines);
            Assert.AreEqual(0, data.TrailingLineCount);
            List<string> urls = Urls(data);
            CollectionAssert.AreEqual(urls.Count == a.Count ? a : b, urls);
        }

        // Checks the file until both writers have exited; returns how many reads were checked
        private int ReadWhileWriting(Process writerA, Process writerB, List<string> a, List<string> b) {
            int reads = 0;
            Stopwatch elapsed = Stopwatch.StartNew();
            while ((!writerA.HasExited || !writerB.HasExited) && elapsed.Elapsed < ChildTimeout) {
                string[] lines = TryReadLines();
                if (lines != null) {
                    AssertOneWritersCompleteList(lines, a, b);
                    reads++;
                }
                Thread.Sleep(1);
            }
            return reads;
        }

        [TestMethod]
        public void TwoProcessesSavingAtOnceLoseNoThreadAndLeaveNoTempFile() {
            TextFile.WriteAllLinesAtomic(_path, ThreadListFile.Serialize(ChildProcess.ThreadList("a")));
            List<string> a = Urls("a");
            List<string> b = Urls("b");
            string count = SavesPerWriter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Process writerA = ChildProcess.Start(ChildProcess.SaveThreadListCommand, _path, "a", count);
            Process writerB = null;
            int reads = 0;
            try {
                writerB = ChildProcess.Start(ChildProcess.SaveThreadListCommand, _path, "b", count);
                reads = ReadWhileWriting(writerA, writerB, a, b);
                Assert.AreEqual(0, ChildProcess.WaitForExit(writerA, ChildTimeout));
                Assert.AreEqual(0, ChildProcess.WaitForExit(writerB, ChildTimeout));
                // How many saves each writer had to try again, for the test log
                Console.WriteLine("Retries: a " + writerA.StandardOutput.ReadToEnd().Trim() + ", b " + writerB.StandardOutput.ReadToEnd().Trim() + "; reads " + reads);
            }
            finally {
                ChildProcess.KillIfRunning(writerA);
                if (writerB != null) ChildProcess.KillIfRunning(writerB);
            }
            Assert.IsGreaterThan(0, reads);
            AssertOneWritersCompleteList(File.ReadAllLines(_path), a, b);
            CollectionAssert.AreEqual(new[] { _path }, Directory.GetFiles(_dir), String.Join(", ", Directory.GetFiles(_dir)));
        }
    }
}
