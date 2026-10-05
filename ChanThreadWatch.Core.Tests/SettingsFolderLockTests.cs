using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The lock that lets one program at a time (the window or the command line) use a settings
    // folder. The other program is a real second process (ChildProcess).
    [TestClass]
    public class SettingsFolderLockTests {
        private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(60);
        private const string Cli = SettingsFolderLockHolder.Cli;
        private const string WinForms = SettingsFolderLockHolder.WinForms;
        private string _dir;
        private readonly List<Process> _children = new List<Process>();

        [TestInitialize]
        public void CreateTempDirectory() {
            _dir = Path.Combine(Path.GetTempPath(), "ctw-lock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void DeleteTempDirectory() {
            foreach (Process child in _children) {
                ChildProcess.KillIfRunning(child);
                child.WaitForExit((int)ChildTimeout.TotalMilliseconds);
                child.Dispose();
            }
            Directory.Delete(_dir, true);
        }

        private string LockPath {
            get { return SettingsFolderLock.GetLockPath(_dir); }
        }

        private static SettingsFolderLock Acquire(string folder, string hostKind = Cli) {
            SettingsFolderLock folderLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(folder, hostKind, out folderLock));
            Assert.IsNotNull(folderLock);
            return folderLock;
        }

        private Process StartChild(string hostKind) {
            Process child = ChildProcess.Start(ChildProcess.HoldLockCommand, _dir, hostKind);
            _children.Add(child);
            return child;
        }

        // Starts a child that takes the lock and waits until it reports it holds it
        private Process StartHolder(string hostKind = WinForms) {
            Process holder = StartChild(hostKind);
            Assert.AreEqual(ChildProcess.Acquired, ChildProcess.ReadLine(holder, ChildTimeout));
            return holder;
        }

        [TestMethod]
        public void LockFileIsCreatedInTheFolderAndKeptAfterRelease() {
            using (SettingsFolderLock folderLock = Acquire(_dir)) {
                Assert.AreEqual(_dir, folderLock.FolderPath);
                Assert.IsTrue(File.Exists(LockPath));
            }

            Assert.IsTrue(File.Exists(LockPath));
        }

        // With the window running, the command line (or a second window) can't take the folder
        [TestMethod]
        public void LockHeldByAnotherProcessIsRefusedUntilItExits() {
            Process holder = StartHolder();

            SettingsFolderLock folderLock;
            Assert.IsFalse(SettingsFolderLock.TryAcquire(_dir, Cli, out folderLock));
            Assert.IsNull(folderLock);

            holder.StandardInput.Close();
            Assert.AreEqual(0, ChildProcess.WaitForExit(holder, ChildTimeout));
            Acquire(_dir).Dispose();
        }

        // The reverse: with this process holding the folder, another one is refused
        [TestMethod]
        public void AnotherProcessIsRefusedWhileThisOneHoldsTheLock() {
            using (Acquire(_dir, WinForms)) {
                Process other = StartChild(Cli);
                Assert.AreEqual(ChildProcess.Held, ChildProcess.ReadLine(other, ChildTimeout));
                Assert.AreEqual(ChildProcess.HeldExitCode, ChildProcess.WaitForExit(other, ChildTimeout));
            }

            Process later = StartHolder(Cli);
            later.StandardInput.Close();
            Assert.AreEqual(0, ChildProcess.WaitForExit(later, ChildTimeout));
        }

        // The holder's record can be read while it holds the lock, and still no other open for
        // writing gets in (a sharing violation on Windows, flock on Unix)
        [TestMethod]
        public void RecordOfAnotherProcessIsReadableWhileItHoldsTheLock() {
            Process holder = StartHolder(WinForms);

            SettingsFolderLockHolder record = SettingsFolderLock.ReadHolder(_dir);

            Assert.IsNotNull(record);
            Assert.AreEqual(WinForms, record.Kind);
            Assert.AreEqual(SettingsFolderLockHolder.GetThisMachineName(), record.MachineName);
            Assert.AreEqual(holder.Id, record.ProcessId);
            // Linux computes a start time from the boot time, which can differ slightly per read
            Assert.IsLessThan(TimeSpan.FromSeconds(5), (holder.StartTime.ToUniversalTime() - record.StartedUtc).Duration());
            IOException ex = Assert.ThrowsExactly<IOException>(() => new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite).Dispose());
            Assert.IsTrue(SettingsFolderLock.IsHeldByAnother(ex));
        }

        [TestMethod]
        public void RecordIsReplacedByTheNextHolder() {
            File.WriteAllText(LockPath, new SettingsFolderLockHolder { Kind = WinForms, MachineName = "OTHER-PC", ProcessId = 1, StartedUtc = DateTime.UtcNow }.Format() + "padding to be cut off");

            using (Acquire(_dir, Cli)) {
                SettingsFolderLockHolder record = SettingsFolderLock.ReadHolder(_dir);
                Assert.AreEqual(Cli, record.Kind);
                Assert.AreEqual(Environment.ProcessId, record.ProcessId);
            }
        }

        // A crash releases the lock; the file it leaves behind is taken by the next program
        [TestMethod]
        public void LockOfACrashedProcessIsTakenByTheNextOne() {
            Process holder = StartHolder();
            holder.Kill(true);
            Assert.IsTrue(holder.WaitForExit((int)ChildTimeout.TotalMilliseconds));

            Assert.IsTrue(File.Exists(LockPath));
            Acquire(_dir).Dispose();
        }

        [TestMethod]
        public void LeftoverLockFileThatIsNotLockedIsTaken() {
            File.WriteAllText(LockPath, "left by an earlier run");

            Acquire(_dir).Dispose();
        }

        // Two opens in one process exclude each other too (Windows share mode; flock is per
        // open file on Unix), so a second window or command in the same process is refused
        [TestMethod]
        public void SecondAcquireInTheSameProcessIsRefusedUntilTheFirstIsReleased() {
            SettingsFolderLock first = Acquire(_dir);
            try {
                SettingsFolderLock second;
                Assert.IsFalse(SettingsFolderLock.TryAcquire(_dir, Cli, out second));
            }
            finally {
                first.Dispose();
            }

            Acquire(_dir).Dispose();
        }

        // A holder that has just taken the lock may not have written its record yet
        [TestMethod]
        public void RecordWrittenShortlyAfterTheFirstReadIsFound() {
            File.WriteAllText(LockPath, "");
            string record = new SettingsFolderLockHolder { Kind = WinForms, MachineName = "OTHER-PC", ProcessId = 1, StartedUtc = DateTime.UtcNow }.Format();
            Task write = Task.Delay(50).ContinueWith(t => File.WriteAllText(LockPath, record));

            SettingsFolderLockHolder holder = SettingsFolderLock.ReadHolderAfterRecordIsWritten(_dir);

            write.Wait();
            Assert.IsNotNull(holder);
            Assert.AreEqual("OTHER-PC", holder.MachineName);
        }

        [TestMethod]
        public void MissingRecordIsStillNullAfterTheSecondRead() {
            File.WriteAllText(LockPath, "");

            Assert.IsNull(SettingsFolderLock.ReadHolderAfterRecordIsWritten(_dir));
        }

        // A program that is closing releases the lock while the next one waits for it
        [TestMethod]
        public void WaitingAcquireGetsALockReleasedDuringTheWait() {
            SettingsFolderLock first = Acquire(_dir);
            Task release = Task.Delay(300).ContinueWith(t => first.Dispose());
            SettingsFolderLock second;

            Assert.IsTrue(SettingsFolderLock.TryAcquire(_dir, WinForms, TimeSpan.FromSeconds(10), out second));

            second.Dispose();
            release.Wait();
        }

        [TestMethod]
        public void WaitingAcquireGivesUpAfterTheWait() {
            using (Acquire(_dir)) {
                Stopwatch elapsed = Stopwatch.StartNew();
                SettingsFolderLock second;

                Assert.IsFalse(SettingsFolderLock.TryAcquire(_dir, WinForms, TimeSpan.FromMilliseconds(300), out second));

                Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300), elapsed.Elapsed);
            }
        }

        // Any other failure is an error, not "held by another program"
        [TestMethod]
        public void MissingFolderThrows() {
            SettingsFolderLock folderLock;
            Assert.ThrowsExactly<DirectoryNotFoundException>(() => SettingsFolderLock.TryAcquire(Path.Combine(_dir, "missing"), Cli, out folderLock));
        }

        [TestMethod]
        public void EmptyFolderOrHostKindThrows() {
            SettingsFolderLock folderLock;
            Assert.ThrowsExactly<ArgumentException>(() => SettingsFolderLock.TryAcquire("", Cli, out folderLock));
            Assert.ThrowsExactly<ArgumentException>(() => SettingsFolderLock.TryAcquire(_dir, "", out folderLock));
        }

        [TestMethod]
        public void ReadHolderIsNullWithoutALockFileOrARecord() {
            Assert.IsNull(SettingsFolderLock.ReadHolder(_dir));
            File.WriteAllText(LockPath, "");
            Assert.IsNull(SettingsFolderLock.ReadHolder(_dir));
        }
    }

    [TestClass]
    public class SettingsFolderLockHolderTests {
        private const string Cli = SettingsFolderLockHolder.Cli;
        private static readonly DateTime Started = new DateTime(2026, 10, 5, 12, 30, 15, 123, DateTimeKind.Utc).AddTicks(4567);

        private static SettingsFolderLockHolder Holder(string kind, string machine) {
            return new SettingsFolderLockHolder { Kind = kind, MachineName = machine, ProcessId = 4242, StartedUtc = Started };
        }

        [TestMethod]
        public void RecordRoundTrips() {
            SettingsFolderLockHolder parsed = SettingsFolderLockHolder.Parse(Holder("winforms", "NAS-PC").Format());

            Assert.AreEqual("winforms", parsed.Kind);
            Assert.AreEqual("NAS-PC", parsed.MachineName);
            Assert.AreEqual(4242, parsed.ProcessId);
            Assert.AreEqual(Started, parsed.StartedUtc);
            Assert.AreEqual(DateTimeKind.Utc, parsed.StartedUtc.Kind);
        }

        // The host name, not the NetBIOS name cut to 15 characters
        [TestMethod]
        public void ThisMachineIsNamedByItsHostName() {
            Assert.AreEqual(System.Net.Dns.GetHostName(), SettingsFolderLockHolder.GetThisMachineName());
            Assert.AreEqual(SettingsFolderLockHolder.GetThisMachineName(), SettingsFolderLockHolder.ForThisProcess(Cli).MachineName);
        }

        [TestMethod]
        public void RecordIsOneKeyValuePerLine() {
            Assert.AreEqual("kind=cli\nmachine=PC\npid=4242\nstarted=2026-10-05T12:30:15.1234567Z\n", Holder("cli", "PC").Format());
        }

        [TestMethod]
        public void RecordWithWindowsLineBreaksAndUnknownKeysParses() {
            SettingsFolderLockHolder parsed = SettingsFolderLockHolder.Parse("version=2\r\nkind=service\r\nmachine=PC\r\npid=7\r\nstarted=2026-10-05T12:30:15.0000000Z\r\n");

            Assert.AreEqual("service", parsed.Kind);
            Assert.AreEqual(7, parsed.ProcessId);
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("garbage")]
        [DataRow("kind=winforms\nmachine=PC\npid=7\n")]
        [DataRow("kind=\nmachine=PC\npid=7\nstarted=2026-10-05T12:30:15.0000000Z\n")]
        [DataRow("kind=winforms\nmachine=PC\npid=-7\nstarted=2026-10-05T12:30:15.0000000Z\n")]
        [DataRow("kind=winforms\nmachine=PC\npid=7\nstarted=yesterday\n")]
        [DataRow("kind=winforms\nmachine=PC\npid=7\nstarted=2026-10-05T12:30\n")]
        public void IncompleteOrInvalidRecordIsNull(string text) {
            Assert.IsNull(SettingsFolderLockHolder.Parse(text));
        }

        // Only a window held by another window on another computer may start anyway, and a window
        // held by the command line (on any computer) waits longer for it; a null machine stands
        // for no record
        [TestMethod]
        [DataRow("winforms", "winforms", "OTHER-PC", HeldLockAction.AskToStartAnyway)]
        [DataRow("winforms", "winforms", "other-pc", HeldLockAction.AskToStartAnyway)]
        [DataRow("winforms", "winforms", "THIS-PC", HeldLockAction.Refuse)]
        [DataRow("winforms", "winforms", null, HeldLockAction.Refuse)]
        [DataRow("winforms", "cli", "OTHER-PC", HeldLockAction.WaitForCommandLine)]
        [DataRow("winforms", "cli", "THIS-PC", HeldLockAction.WaitForCommandLine)]
        [DataRow("winforms", "future", "OTHER-PC", HeldLockAction.Refuse)]
        [DataRow("winforms", "service", "OTHER-PC", HeldLockAction.Refuse)]
        [DataRow("cli", "winforms", "OTHER-PC", HeldLockAction.Refuse)]
        [DataRow("cli", "cli", "OTHER-PC", HeldLockAction.Refuse)]
        public void DecisionDependsOnTheHolderAndTheMachine(string hostKind, string holderKind, string holderMachine, HeldLockAction expected) {
            SettingsFolderLockHolder holder = holderMachine != null ? Holder(holderKind, holderMachine) : null;

            Assert.AreEqual(expected, SettingsFolderLockHolder.Decide(holder, hostKind, "THIS-PC"));
        }

        // A record that doesn't parse (e.g. one a holder is still writing) is refused
        [TestMethod]
        public void UnreadableRecordIsRefused() {
            Assert.AreEqual(HeldLockAction.Refuse, SettingsFolderLockHolder.Decide(SettingsFolderLockHolder.Parse("kind=winforms"), "winforms", "THIS-PC"));
            Assert.AreEqual(HeldLockAction.Refuse, SettingsFolderLockHolder.Decide(SettingsFolderLockHolder.Parse("kind=cli"), "winforms", "THIS-PC"));
        }

        // The window waits about 10 seconds in all for the command line
        [TestMethod]
        public void CommandLineWaitAddsUpToAboutTenSeconds() {
            Assert.AreEqual(TimeSpan.FromSeconds(10), SettingsFolderLock.DefaultWait + SettingsFolderLock.CommandLineWait);
        }

        // The same names as RuntimeMigrationTests.MutexNameIsTheSameAsOnNetFramework (recorded from the .NET
        // Framework 4.8 build), now computed in Core so the command line checks the window's mutex. Windows
        // only: the mutex exists only there, and the names rely on NLS casing (UseNls), which other systems ignore.
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [DataRow(@"C:\Users\runneradmin\AppData\Roaming\ChanThreadWatch", "56D4B3D8F6E35CC8")]
        [DataRow("C:\\Users\\J\u00FCrgen \u00C5ngstr\u00F6m\\AppData\\Roaming\\ChanThreadWatch", "BF9348385C7B5D86")]
        [DataRow(@"D:\Tools\ChanThreadWatch", "63C0998D1948D86E")]
        [DataRow("\\\\nas\\share\\\u00DF\u01C5\u10D0\u0131\u03C2\\ChanThreadWatch", "9C085E7C9CD1843F")]
        public void AppMutexNameIsTheOldWindowsName(string settingsFolder, string net48Hash) {
            Assert.AreEqual(@"Global\ChanThreadWatch_" + net48Hash, SettingsFolderLock.GetAppMutexName(settingsFolder));
        }
    }
}
