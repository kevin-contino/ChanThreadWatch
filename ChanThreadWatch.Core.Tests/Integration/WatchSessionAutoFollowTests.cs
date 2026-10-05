using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    // Auto-follow through WatchSession: watchers raise AddThread on their own threads, and the
    // session posts each add to its owner's thread. Here the owner's thread is the test thread,
    // which drains the posted work from a queue. The added threads start against a loopback
    // server that answers 404, so they stop without downloading anything.
    [TestClass]
    public class WatchSessionAutoFollowTests : ThreadWatcherIntegrationTestBase {
        private static readonly MethodInfo _onAddThread = typeof(ThreadWatcher).GetMethod("OnAddThread", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _reservedDescendantSlots = typeof(ThreadWatcher).GetField("_reservedDescendantSlots", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly ConcurrentQueue<Action> _posted = new ConcurrentQueue<Action>();

        private WatchSession CreateSession() {
            string settingsDir = Path.Combine(DownloadDir, "settings");
            Directory.CreateDirectory(settingsDir);
            return new WatchSession(a => a(), _posted.Enqueue, settingsDir);
        }

        // A parent that is registered but never started, so only the test raises its events
        private static ThreadWatcher AddStoppedParent(WatchSession session, string url) {
            ThreadInfo thread = new ThreadInfo {
                URL = url,
                PageAuth = String.Empty,
                ImageAuth = String.Empty,
                CheckIntervalSeconds = 60,
                OneTimeDownload = true,
                Description = String.Empty,
                StopReason = StopReason.UserRequest,
                Category = "Cat",
                AutoFollow = true
            };
            Assert.IsTrue(session.AddThread(thread));
            ThreadWatcher watcher;
            Assert.IsTrue(session.TryGetThreadWatcher(new ThreadWatcher(url).PageID, out watcher));
            return watcher;
        }

        // Raises AddThread the way a check does: a slot is reserved under the root first
        private static void RaiseAddThread(ThreadWatcher watcher, string childURL) {
            Assert.IsTrue(watcher.RootThread.TryReserveDescendantSlot());
            _onAddThread.Invoke(watcher, new object[] { new AddThreadEventArgs(childURL) });
        }

        private int DrainPosted() {
            int count = 0;
            Action action;
            while (_posted.TryDequeue(out action)) {
                action();
                count++;
            }
            return count;
        }

        [TestMethod]
        public void ConcurrentAutoFollowAddsEachChildOnceUnderItsParent() {
            LoopbackHttpServer server = StartServer();
            WatchSession session = CreateSession();
            ThreadWatcher parentA = AddStoppedParent(session, server.URL("/wg/thread/100"));
            ThreadWatcher parentB = AddStoppedParent(session, server.URL("/wg/thread/110"));
            string blacklistedURL = server.URL("/wg/thread/199");
            session.AddToBlacklist(new[] { new ThreadWatcher(blacklistedURL) });
            string[] childrenOfA = { server.URL("/wg/thread/201"), server.URL("/wg/thread/202"), server.URL("/wg/thread/203") };
            string[] childrenOfB = { server.URL("/wg/thread/204"), server.URL("/wg/thread/205") };
            int testThreadID = Thread.CurrentThread.ManagedThreadId;
            List<int> createdOnThreads = new List<int>();
            session.ThreadWatcherCreated += w => createdOnThreads.Add(Thread.CurrentThread.ManagedThreadId);
            session.SaveThreadListPending = false;

            // Two threads per parent raise the same links (and the blacklisted one) at the same time
            var workers = new List<Tuple<ThreadWatcher, string[]>> {
                Tuple.Create(parentA, childrenOfA), Tuple.Create(parentA, childrenOfA),
                Tuple.Create(parentB, childrenOfB), Tuple.Create(parentB, childrenOfB)
            };
            var exceptions = new ConcurrentQueue<Exception>();
            using (var barrier = new Barrier(workers.Count)) {
                List<Thread> threads = new List<Thread>();
                foreach (var worker in workers) {
                    Thread thread = new Thread(() => {
                        try {
                            barrier.SignalAndWait();
                            foreach (string childURL in worker.Item2) RaiseAddThread(worker.Item1, childURL);
                            RaiseAddThread(worker.Item1, blacklistedURL);
                        }
                        catch (Exception ex) {
                            exceptions.Enqueue(ex);
                        }
                    });
                    threads.Add(thread);
                    thread.Start();
                }
                foreach (Thread thread in threads) Assert.IsTrue(thread.Join(RunTimeout));
            }
            Assert.HasCount(0, exceptions);
            // Nothing is added on the watchers' threads; every add waits for the owner's thread
            Assert.HasCount(2, session.ThreadWatchers);
            Assert.HasCount(0, createdOnThreads);

            try {
                Assert.AreEqual(2 * (childrenOfA.Length + 1) + 2 * (childrenOfB.Length + 1), DrainPosted());
                Assert.HasCount(2 + childrenOfA.Length + childrenOfB.Length, session.ThreadWatchers);
                AssertChildren(session, parentA, childrenOfA);
                AssertChildren(session, parentB, childrenOfB);
                Assert.IsFalse(session.IsThreadWatched(blacklistedURL));
                Assert.HasCount(childrenOfA.Length + childrenOfB.Length, createdOnThreads);
                foreach (int threadID in createdOnThreads) Assert.AreEqual(testThreadID, threadID);
                Assert.IsTrue(session.SaveThreadListPending);
                // Every reservation was released, whether the thread was added or rejected
                Assert.AreEqual(0, (int)_reservedDescendantSlots.GetValue(parentA));
                Assert.AreEqual(0, (int)_reservedDescendantSlots.GetValue(parentB));

                // A link to a thread that is already watched neither adds nor restarts it
                ThreadWatcher child = session.ThreadWatchers.Find(w => w.PageURL == childrenOfA[0]);
                // WaitUntilStopped alone returns at once while the first check is still queued (its
                // event starts set), so wait until the 404 has stopped the child
                Assert.IsTrue(SpinWait.SpinUntil(() => !child.IsRunning, RunTimeout), "Check did not finish");
                session.SaveThreadListPending = false;
                RaiseAddThread(parentA, childrenOfA[0]);
                Assert.AreEqual(1, DrainPosted());
                Assert.IsFalse(child.IsRunning);
                Assert.IsFalse(session.SaveThreadListPending);
                Assert.AreEqual(0, (int)_reservedDescendantSlots.GetValue(parentA));
            }
            finally {
                foreach (ThreadWatcher watcher in session.ThreadWatchers) {
                    Assert.IsTrue(watcher.WaitUntilStopped((int)RunTimeout.TotalMilliseconds), "Check did not finish");
                }
            }
        }

        private static void AssertChildren(WatchSession session, ThreadWatcher parent, string[] childURLs) {
            Assert.HasCount(childURLs.Length, parent.ChildThreads);
            foreach (string childURL in childURLs) {
                List<ThreadWatcher> matches = session.ThreadWatchers.FindAll(w => w.PageURL == childURL);
                Assert.HasCount(1, matches, "Watchers for " + childURL);
                ThreadWatcher child = matches[0];
                Assert.AreSame(parent, child.ParentThread);
                Assert.AreSame(child, parent.ChildThreads[child.PageID]);
                Assert.AreEqual(parent.PageID, ((WatcherExtraData)child.Tag).AddedFrom);
                Assert.AreEqual("Cat", child.Category);
            }
        }
    }
}
