using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    [TestClass]
    public class OtherWorkSchedulerTests {
        private const int RunTimeoutMs = 5000;

        [TestMethod]
        public void RunsItemThatIsAlreadyDue() {
            WorkScheduler scheduler = new WorkScheduler();
            using (ManualResetEvent ran = new ManualResetEvent(false)) {
                scheduler.AddItem(TickCount.Now, () => ran.Set(), "test-due");

                Assert.IsTrue(ran.WaitOne(RunTimeoutMs));
            }
        }

        [TestMethod]
        public void RunsFutureItemNoEarlierThanItsRunTime() {
            WorkScheduler scheduler = new WorkScheduler();
            long runAtTicks = TickCount.Now + 300;
            long ranAtTicks = 0;
            using (ManualResetEvent ran = new ManualResetEvent(false)) {
                scheduler.AddItem(runAtTicks, () => {
                    Interlocked.Exchange(ref ranAtTicks, TickCount.Now);
                    ran.Set();
                }, "test-future");

                Assert.IsTrue(ran.WaitOne(RunTimeoutMs));
                Assert.IsGreaterThanOrEqualTo(runAtTicks, Interlocked.Read(ref ranAtTicks));
            }
        }

        [TestMethod]
        public void RemovedItemDoesNotRun() {
            WorkScheduler scheduler = new WorkScheduler();
            using (ManualResetEvent removedRan = new ManualResetEvent(false))
            using (ManualResetEvent laterRan = new ManualResetEvent(false)) {
                WorkScheduler.WorkItem removed = scheduler.AddItem(TickCount.Now + 200, () => removedRan.Set(), "test-remove");
                scheduler.AddItem(TickCount.Now + 600, () => laterRan.Set(), "test-remove");

                Assert.IsTrue(scheduler.RemoveItem(removed));
                Assert.IsFalse(scheduler.RemoveItem(removed));

                Assert.IsTrue(laterRan.WaitOne(RunTimeoutMs));
                Assert.IsFalse(removedRan.WaitOne(0));
            }
        }

        [TestMethod]
        public void ChangingRunAtTicksReschedulesItem() {
            WorkScheduler scheduler = new WorkScheduler();
            long ranAtTicks = 0;
            using (ManualResetEvent ran = new ManualResetEvent(false)) {
                WorkScheduler.WorkItem item = scheduler.AddItem(TickCount.Now + 60000, () => {
                    Interlocked.Exchange(ref ranAtTicks, TickCount.Now);
                    ran.Set();
                }, "test-reschedule");

                long newRunAtTicks = TickCount.Now + 200;
                item.RunAtTicks = newRunAtTicks;

                Assert.IsTrue(ran.WaitOne(RunTimeoutMs));
                Assert.IsGreaterThanOrEqualTo(newRunAtTicks, Interlocked.Read(ref ranAtTicks));
            }
        }

        [TestMethod]
        public void StartedItemIgnoresRunAtTicksChange() {
            WorkScheduler scheduler = new WorkScheduler();
            using (ManualResetEvent ran = new ManualResetEvent(false)) {
                WorkScheduler.WorkItem item = scheduler.AddItem(TickCount.Now, () => ran.Set(), "test-started");
                Assert.IsTrue(ran.WaitOne(RunTimeoutMs));

                long runAtTicks = item.RunAtTicks;
                item.RunAtTicks = runAtTicks + 60000;

                Assert.AreEqual(runAtTicks, item.RunAtTicks);
            }
        }
    }
}
