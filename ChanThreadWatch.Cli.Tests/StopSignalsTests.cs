using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Cli.Tests {
    // The signal handling of ctw watch, without registering for the process's signals (Handle is called directly)
    [TestClass]
    public class StopSignalsTests {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private static StopSignals Create(TimeSpan saveWait) {
            return new StopSignals(new WatchStatusOutput(new StringWriter(CultureInfo.InvariantCulture)), saveWait);
        }

        // SIGTERM and SIGHUP (on Windows a shutdown or the console window closing, after which Windows ends the
        // process when the handler returns) wait for the final save
        [TestMethod]
        [DataRow(PosixSignal.SIGTERM)]
        [DataRow(PosixSignal.SIGHUP)]
        public void TerminatingSignal_StopsTheWatchAndWaitsForTheFinalSave(PosixSignal signal) {
            using (StopSignals signals = Create(Timeout)) {
                Task<bool> handler = Task.Run(() => signals.Handle(signal));

                Assert.IsTrue(signals.Token.WaitHandle.WaitOne(Timeout), "The watch was not told to stop");
                Assert.IsFalse(handler.Wait(TimeSpan.FromMilliseconds(300)), "The handler returned before the final save");
                signals.Finish();
                Assert.IsTrue(handler.Wait(Timeout), "The handler did not return after the final save");
                Assert.IsTrue(handler.Result);
            }
        }

        [TestMethod]
        public void TerminatingSignal_EndsTheProcessAfterTheSaveWaitWhenTheSaveNeverFinishes() {
            TimeSpan saveWait = TimeSpan.FromMilliseconds(300);
            using (StopSignals signals = Create(saveWait)) {
                Stopwatch elapsed = Stopwatch.StartNew();

                // Not canceled: the process ends, with threads.txt as it was saved last
                Assert.IsFalse(signals.Handle(PosixSignal.SIGTERM));

                Assert.IsGreaterThanOrEqualTo(saveWait - TimeSpan.FromMilliseconds(50), elapsed.Elapsed);
                Assert.IsLessThan(Timeout, elapsed.Elapsed);
            }
        }

        // Ctrl+C does not end the process when its handler returns, so it doesn't wait; a second one is not canceled,
        // which ends the process at once
        [TestMethod]
        public void CtrlC_DoesNotWait_AndASecondSignalEndsTheProcess() {
            using (StopSignals signals = Create(Timeout)) {
                Stopwatch elapsed = Stopwatch.StartNew();

                Assert.IsTrue(signals.Handle(PosixSignal.SIGINT));
                Assert.IsLessThan(TimeSpan.FromSeconds(5), elapsed.Elapsed);
                Assert.IsTrue(signals.Token.IsCancellationRequested);
                Assert.IsFalse(signals.Handle(PosixSignal.SIGINT));
                Assert.IsFalse(signals.Handle(PosixSignal.SIGTERM));
            }
        }

        // A handler that runs after the watch has ended neither throws nor waits
        [TestMethod]
        public void SignalAfterDispose_IsNotCanceledAndDoesNotThrow() {
            StopSignals signals = Create(Timeout);
            signals.Dispose();

            Assert.IsFalse(signals.Handle(PosixSignal.SIGTERM));
        }

        // A closed stdout (for example a pipe whose reader has gone) never fails a watcher's thread or the final save
        [TestMethod]
        public void StatusOutput_DropsLinesThatCannotBeWritten() {
            WatchStatusOutput output = new WatchStatusOutput(new ClosedWriter());

            output.WriteLine("line");
            output.WriteStopped(true);
            using (StopSignals signals = new StopSignals(output, Timeout)) {
                Assert.IsTrue(signals.Handle(PosixSignal.SIGINT));
            }
        }
    }

    // A writer whose output is gone, as stdout or stderr after the pipe's reader has ended
    internal sealed class ClosedWriter : TextWriter {
        public override Encoding Encoding {
            get { return Encoding.UTF8; }
        }

        public override void Write(char value) {
            throw new IOException("The pipe has been ended.");
        }

        public override void WriteLine(string value) {
            throw new IOException("The pipe has been ended.");
        }
    }
}
