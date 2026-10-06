using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Api.Tests {
    // MP-7a L2b: a thread added through the API is guarded. Its mark is saved in api-threads.txt (threads.txt keeps
    // the previous release's format), and its watcher's own connections are checked by the SSRF guard when they are
    // made, whatever the add-time lookup said. Service mode stays off.
    [TestClass]
    public class GuardedThreadTests : ApiTestBase {
        private string ApiThreadsPath {
            get { return Path.Combine(Folder, Settings.ApiThreadsFileName); }
        }

        [TestMethod]
        public void Add_SavesTheMarkBesideTheThreadList() {
            // As the hosts do before the server starts; the thread list store saves only after a load
            Session.LoadThreadList();
            HttpResponseMessage response = AddThread(ThreadUrl);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));

            Assert.IsTrue(Owner.Invoke(() => Session.ThreadWatchers[0].Guarded));
            Assert.IsTrue(Owner.Invoke(() => Session.SaveThreadList()));

            CollectionAssert.AreEqual(new[] { "1", "4chan/wg/100" }, File.ReadAllLines(ApiThreadsPath));
            string[] threadLines = File.ReadAllLines(ThreadListPath);
            Assert.HasCount(1 + ThreadListFile.GetLinesPerThread(ThreadListFile.CurrentVersion), threadLines);
            Assert.AreEqual(ThreadUrl, ThreadListFile.Parse(threadLines).Threads[0].URL);
        }

        // The add-time lookup says public; when the watcher connects, the name resolves to a private address, which the
        // guard refuses before any socket is made (the test host's BeforeConnect would fail the check otherwise)
        [TestMethod]
        public void TheWatchersConnectionToAPrivateAddressIsBlocked() {
            List<string> connectLookups = new List<string>();
            SSRFGuard.ResolveHost = (host, cancellationToken) => {
                lock (connectLookups) connectLookups.Add(host);
                return Task.FromResult(new[] { IPAddress.Parse("10.0.0.1") });
            };
            Assert.IsFalse(SSRFGuard.ServiceMode);

            HttpResponseMessage response = AddThread(ThreadUrl);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
            CollectionAssert.Contains(ResolvedHosts, "boards.4chan.org");
            ThreadWatcher watcher = Owner.Invoke(() => Session.ThreadWatchers[0]);

            string checkError = WaitForCheckError(watcher);

            Assert.AreEqual("requests to boards.4chan.org are blocked: it is a local or private address", checkError);
            lock (connectLookups) CollectionAssert.Contains(connectLookups, "boards.4chan.org");
        }

        // Fix 2: while api-threads.txt can't be written this session (here it can't be read: a folder is in its place),
        // a new thread would lose its mark at the next start, so the add is refused and nothing is added
        [TestMethod]
        public void Add_IsRefusedWhileTheMarksCannotBeSaved() {
            Directory.CreateDirectory(ApiThreadsPath);
            Session.LoadThreadList();
            Assert.IsFalse(Session.CanSaveApiThreadMarks);

            AssertProblem(AddThread(ThreadUrl), HttpStatusCode.ServiceUnavailable, "marks_unavailable");

            Assert.AreEqual(0, ThreadCount());
            Assert.IsTrue(Owner.Invoke(() => Session.SaveThreadList()));
            Assert.IsEmpty(ThreadListFile.Parse(File.ReadAllLines(ThreadListPath)).Threads);
        }

        private static string WaitForCheckError(ThreadWatcher watcher) {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(30)) {
                string checkError = watcher.CheckError;
                if (checkError != null) return checkError;
                Thread.Sleep(20);
            }
            Assert.Fail("The watcher's check did not end with an error");
            return null;
        }
    }
}
