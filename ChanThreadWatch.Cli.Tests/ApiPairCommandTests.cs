using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using JDP.Api;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Cli.Tests {
    // ctw api-pair (MP-7b L2): the code is printed once on stdout, only its salt and stretched key are saved in an
    // owner-only api-pairing.txt, and the command follows that file until a browser pairs or the code ends. The API's
    // side (marking the file paired, deleting it, saving a paired browser) is done here through the same library calls
    // the server makes, between two looks at the file. Every code and token is made at run time in the test's
    // temporary folder; a code never goes into an assertion's message.
    [TestClass]
    public class ApiPairCommandTests : CliTestBase {
        internal const string ChromeOrigin = ApiPairing.ChromeScheme + ApiPairing.ChromeExtensionId;
        internal const string FirefoxOrigin = "moz-extension://0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5f";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        // XXXX-XXXX in Crockford base32 (no I, L, O or U)
        private static readonly Regex _codeLine = new Regex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", RegexOptions.CultureInvariant);

        private ApiPairingFile PairingFile {
            get { return new ApiPairingFile(Folder); }
        }

        private ApiClientStore Clients {
            get { return new ApiClientStore(Folder); }
        }

        [TestInitialize]
        public void SetUpPairing() {
            OwnerOnlyFile.IsRootOnUnix = () => false;
            ApiPairCommand.PollInterval = TimeSpan.FromMilliseconds(20);
            File.WriteAllLines(Path.Combine(Folder, Settings.SettingsFileName), new[] { "ApiEnabled=1" });
            new ApiTokenStore(Folder).Generate();
            // The command never loads the settings; a test checks that these stay
            Settings.Load(Path.Combine(Folder, "missing-settings.txt"));
        }

        [TestCleanup]
        public void ResetPairingHooks() {
            OwnerOnlyFile.IsRootOnUnix = () => !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess;
            OwnerOnlyFile.NewFileCheckForTesting = null;
            ApiTokenStore.OpeningForTesting = null;
            ApiPairCommand.PollInterval = ApiPairCommand.DefaultPollInterval;
            ApiPairCommand.UtcNow = () => DateTimeOffset.UtcNow;
            ApiPairCommand.PollingForTesting = null;
            // A test loads the settings to compare; the other tests start from none
            Settings.Load(Path.Combine(Folder, "missing-settings.txt"));
        }

        [TestMethod]
        [DataRow(ChromeOrigin, "Chrome")]
        [DataRow(FirefoxOrigin, "Firefox")]
        public void ApiPair_PrintsTheCodeOnceAndReportsThePairing(string origin, string name) {
            string fileText = null;
            byte[] expectedKey = null;
            CliResult result = RunPair(poll => {
                fileText = File.ReadAllText(PairingFile.Path, Encoding.ASCII);
                ApiPendingPairing pending = ReadPending();
                expectedKey = ApiPairing.DeriveKey(CodeOf(poll.Output).Replace("-", ""), ApiPairing.FromBase64Url(pending.Salt, ApiPairing.SaltBytes));
                CollectionAssert.AreEqual(expectedKey, pending.Key, "The key in the file is not PBKDF2 of the printed code.");
                AssertOwnerOnly(PairingFile.Path);
                SimulateFinish(origin);
            });

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, Describe(result));
            string[] lines = Lines(result.Output);
            Assert.HasCount(2, lines, Describe(result));
            Assert.IsTrue(_codeLine.IsMatch(lines[0]), "Not a code: " + lines[0].Length + " characters");
            Assert.AreEqual("Paired with the " + name + " extension", lines[1]);
            Assert.AreEqual("ctw: note: Enter this code in the extension's options within 5 minutes. Press Ctrl+C to cancel." + Environment.NewLine, result.Error);
            AssertCodeNowhereElse(lines[0], result.Error, fileText);
            StringAssert.Contains(fileText, "key:" + Convert.ToHexStringLower(expectedKey) + "\n");
            Assert.IsFalse(File.Exists(PairingFile.Path), "The pairing file was not deleted.");
            Assert.AreEqual(ApiPairing.FamilyOf(origin), Clients.Read().Single().Family);
        }

        // The API can save the browser's token and then find the file gone; a browser that paired since the code was
        // made is a pairing
        [TestMethod]
        public void ApiPair_FileGoneAfterANewPairingReportsThePairing() {
            CliResult result = RunPair(poll => {
                Clients.Pair(FirefoxOrigin, DateTimeOffset.UtcNow);
                File.Delete(PairingFile.Path);
            });

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, Describe(result));
            Assert.AreEqual("Paired with the Firefox extension", Lines(result.Output)[1]);
        }

        // A browser paired before the code was made is not this code's pairing: the code was burned (wrong tries)
        [TestMethod]
        public void ApiPair_FileGoneWithoutANewPairingSaysTheCodeEnded() {
            Clients.Pair(ChromeOrigin, DateTimeOffset.UtcNow);

            CliResult result = RunPair(poll => File.Delete(PairingFile.Path));

            AssertEnded(result, "The code no longer works, and no browser extension paired with it: wrong codes used up its tries, or api-pairing.txt was deleted.");
        }

        [TestMethod]
        public void ApiPair_SaysWhenTheCodeExpired() {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ApiPairCommand.UtcNow = () => now;

            CliResult result = RunPair(poll => {
                if (poll.Number == 2) now += ApiPolicy.PairingCodeLifetime;
            });

            AssertEnded(result, "The code expired before a browser extension paired with it. Run 'ctw api-pair' again for a new code.");
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        // The API deletes a code it meets expired; that is an expiry, not a burned code
        [TestMethod]
        public void ApiPair_FileGoneAfterTheExpirySaysTheCodeExpired() {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ApiPairCommand.UtcNow = () => now;

            CliResult result = RunPair(poll => {
                now += ApiPolicy.PairingCodeLifetime;
                File.Delete(PairingFile.Path);
            });

            AssertEnded(result, "The code expired");
        }

        // Another creator's code is left in place
        [TestMethod]
        public void ApiPair_ReplacedCodeEndsAndLeavesTheNewerFile() {
            string newerId = null;

            CliResult result = RunPair(poll => {
                if (newerId == null) newerId = PairingFile.Create(DateTimeOffset.UtcNow).Id;
            });

            AssertEnded(result, "A newer code from another program that makes codes, such as the app or another 'ctw api-pair', replaced this one. Use the newer code.");
            Assert.AreEqual(newerId, ReadPending().Id);
        }

        [TestMethod]
        public void ApiPair_FileChangedByAnotherProgramEndsTheCode() {
            CliResult result = RunPair(poll => File.WriteAllText(PairingFile.Path, "1\nnot a code\n"));

            AssertEnded(result, "api-pairing.txt was changed by another program, so the code no longer works.");
            Assert.AreEqual("1\nnot a code\n", File.ReadAllText(PairingFile.Path), "Another program's file was deleted or changed.");
        }

        // With the pairing file gone and api-clients.txt not usable just now, the wait goes on (here until the expiry)
        // rather than reporting a burned code
        [TestMethod]
        public void ApiPair_FileGoneWhileThePairedBrowsersCannotBeReadKeepsWaiting() {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ApiPairCommand.UtcNow = () => now;
            int lastPoll = 0;

            CliResult result = RunPair(poll => {
                lastPoll = poll.Number;
                if (poll.Number == 1) {
                    File.WriteAllText(Clients.Path, "1\nnot a line\n");
                    File.Delete(PairingFile.Path);
                }
                if (poll.Number == 3) now += ApiPolicy.PairingCodeLifetime;
            });

            AssertEnded(result, "The code expired");
            Assert.AreEqual(3, lastPoll, "The wait did not go on while api-clients.txt could not be used.");
        }

        // A browser seen in api-clients.txt is new only against the paired browsers read before the code; without
        // them (the file was not usable then) none counts as this code's pairing
        [TestMethod]
        public void ApiPair_FileGoneWithoutTheEarlierPairedBrowsersSaysTheCodeEnded() {
            File.WriteAllText(Clients.Path, "1\nnot a line\n");

            CliResult result = RunPair(poll => {
                Clients.Pair(ChromeOrigin, DateTimeOffset.UtcNow);
                File.Delete(PairingFile.Path);
            });

            AssertEnded(result, "The code no longer works");
        }

        // The first signal stops the wait and ends the code; a second one is not canceled, so it ends the process
        [TestMethod]
        public void PairingStop_FirstSignalCancelsAndEndsTheCodeSecondEndsTheProcess() {
            PairingWait wait = new PairingWait(Folder);
            wait.MakeCode();
            using (CancellationTokenSource source = new CancellationTokenSource())
            using (PairingStop stop = new PairingStop(source, wait)) {
                Assert.IsTrue(File.Exists(PairingFile.Path));

                Assert.IsTrue(stop.Handle(), "The first signal was not canceled.");
                Assert.IsTrue(stop.Token.IsCancellationRequested);
                Assert.IsFalse(File.Exists(PairingFile.Path), "The handler did not end the code.");
                Assert.IsFalse(stop.Handle(), "A second signal was canceled.");
            }
        }

        // A signal before the code exists only stops; there is no file to end yet
        [TestMethod]
        public void PairingStop_SignalBeforeTheCodeIsMadeOnlyStops() {
            PairingWait wait = new PairingWait(Folder);
            using (CancellationTokenSource source = new CancellationTokenSource())
            using (PairingStop stop = new PairingStop(source, wait)) {
                Assert.IsTrue(stop.Handle());
                Assert.IsTrue(stop.Token.IsCancellationRequested);
                Assert.IsNull(wait.Code);
                Assert.IsFalse(File.Exists(PairingFile.Path));
            }
        }

        // A signal that arrives after the stop is disposed does nothing: no throw, no cancel, the code file stays
        [TestMethod]
        public void PairingStop_SignalAfterDisposeDoesNothing() {
            PairingWait wait = new PairingWait(Folder);
            wait.MakeCode();
            CancellationTokenSource source = new CancellationTokenSource();
            PairingStop stop = new PairingStop(source, wait);
            stop.Dispose();

            Assert.IsFalse(stop.Handle());
            Assert.IsTrue(File.Exists(PairingFile.Path), "A late signal ended the code.");
            wait.End();
        }

        // A stop that comes before the code is made: no code is made or printed
        [TestMethod]
        public void ApiPair_StoppedBeforeTheCodeMakesNone() {
            using (CancellationTokenSource stop = new CancellationTokenSource()) {
                stop.Cancel();

                CliResult result = RunWithToken(stop.Token);

                AssertFailed(result, CliApp.ExitFailure, "Cancelled. The code no longer works.");
                Assert.IsFalse(File.Exists(PairingFile.Path));
            }
        }

        // A stop while the code is made (here while its file is written): the code is never printed, and its file goes
        [TestMethod]
        public void ApiPair_StoppedWhileTheCodeIsMadeNeverShowsIt() {
            using (CancellationTokenSource stop = new CancellationTokenSource()) {
                bool written = false;
                OwnerOnlyFile.NewFileCheckForTesting = stream => {
                    written = true;
                    stop.Cancel();
                    return true;
                };
                ApiPairCommand.PollingForTesting = () => Assert.Fail("The command waited after the stop.");

                CliResult result = RunWithToken(stop.Token);

                Assert.IsTrue(written, "The pairing file was not written.");
                AssertFailed(result, CliApp.ExitFailure, "Cancelled. The code no longer works.");
                Assert.IsFalse(File.Exists(PairingFile.Path), "The code's file was left behind.");
            }
        }

        // The stop deletes the file before the wait looks again; that look must not report the code as ended
        [TestMethod]
        public void ApiPair_StopThatDeletesTheFileBeforeALookIsReportedAsCancelled() {
            CliResult result = RunPair(poll => {
                File.Delete(PairingFile.Path);
                poll.Stop.Cancel();
            });

            AssertEnded(result, "Cancelled. The code no longer works.");
        }

        // The paired browsers are taken once the code is made, not before: a pairing saved while the code was made
        // can't be one with this code, which nobody knew yet
        [TestMethod]
        public void ApiPair_PairingSavedWhileTheCodeWasMadeIsNotThisCodes() {
            bool paired = false;
            OwnerOnlyFile.NewFileCheckForTesting = stream => {
                if (!paired) {
                    paired = true;
                    Clients.Pair(ChromeOrigin, DateTimeOffset.UtcNow);
                }
                return true;
            };

            CliResult result = RunPair(poll => File.Delete(PairingFile.Path));

            Assert.IsTrue(paired);
            AssertEnded(result, "The code no longer works");
        }

        // A file that can't be read is waited for; at the expiry, a browser that paired since the code is a pairing
        [TestMethod]
        [DataRow(false, "The code expired")]
        [DataRow(true, null)]
        public void ApiPair_UnreadableFileUntilTheExpiry(bool pairedMeanwhile, string message) {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ApiPairCommand.UtcNow = () => now;
            string pairingPath = PairingFile.Path;
            int lastPoll = 0;

            CliResult result = RunPair(poll => {
                lastPoll = poll.Number;
                if (poll.Number == 1) {
                    if (pairedMeanwhile) Clients.Pair(FirefoxOrigin, DateTimeOffset.UtcNow);
                    ApiTokenStore.OpeningForTesting = path => {
                        if (path == pairingPath) throw new IOException("test: access denied", unchecked((int)0x80070005));
                    };
                }
                if (poll.Number == 3) now += ApiPolicy.PairingCodeLifetime;
            });

            ApiTokenStore.OpeningForTesting = null;
            Assert.AreEqual(3, lastPoll, "The wait did not go on while the file could not be read.");
            if (message != null) {
                AssertEnded(result, message);
            }
            else {
                Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, Describe(result));
                Assert.AreEqual("Paired with the Firefox extension", Lines(result.Output)[1]);
            }
        }

        // A code the file says is paired is reported paired, also once the clock is past its expiry
        [TestMethod]
        public void ApiPair_PairedStateWinsOverTheExpiry() {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ApiPairCommand.UtcNow = () => now;

            CliResult result = RunPair(poll => {
                SimulateFinish(ChromeOrigin);
                now += ApiPolicy.PairingCodeLifetime + TimeSpan.FromMinutes(1);
            });

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, Describe(result));
            Assert.AreEqual("Paired with the Chrome extension", Lines(result.Output)[1]);
        }

        // The file with its own code, but that others can read: not trusted, so the code ends and the file is left
        [TestMethod]
        public void ApiPair_FileThatOthersCanReadEndsTheCode() {
            byte[] content = null;

            CliResult result = RunPair(poll => {
                content = File.ReadAllBytes(PairingFile.Path);
                File.Delete(PairingFile.Path);
                File.WriteAllBytes(PairingFile.Path, content);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(PairingFile.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                using (FileStream stream = new FileStream(PairingFile.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    if (OwnerOnlyFile.IsOwnerOnly(stream)) Assert.Inconclusive("A new file in the temporary folder is owner-only here.");
                }
            });

            AssertEnded(result, "api-pairing.txt was changed by another program");
            CollectionAssert.AreEqual(content, File.ReadAllBytes(PairingFile.Path), "The file was deleted or changed.");
        }

        // A missing token is named: the API does not start without one. The code is made anyway.
        [TestMethod]
        public void ApiPair_WarnsWhenThereIsNoToken() {
            File.Delete(Path.Combine(Folder, ApiTokenStore.FileName));

            CliResult result = RunPair(poll => SimulateFinish(ChromeOrigin));

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, Describe(result));
            StringAssert.StartsWith(result.Error, "ctw: warning: There is no api-token.txt, and ctw watch does not start the local API without a token. " +
                "Run 'ctw api-token' first." + Environment.NewLine);
        }

        // ApiEnabled is read as Settings reads it (name in any case, the first line wins, only "1" is on), and the
        // command never loads the settings into the process
        [TestMethod]
        [DataRow(new[] { "apienabled=1" }, false)]
        [DataRow(new[] { "ApiEnabled=1", "ApiEnabled=0" }, false)]
        [DataRow(new[] { "ApiEnabled=0", "ApiEnabled=1" }, true)]
        [DataRow(new[] { "ApiEnabled= 1" }, true)]
        [DataRow(new[] { "ApiEnabled=true" }, true)]
        [DataRow(new[] { "ApiEnabled" }, true)]
        public void ApiPair_ReadsApiEnabledAsTheSettingsDo(string[] lines, bool warns) {
            File.WriteAllLines(Path.Combine(Folder, Settings.SettingsFileName), lines);

            CliResult result = RunPair(poll => SimulateFinish(ChromeOrigin));

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, Describe(result));
            Assert.AreEqual(warns, result.Error.StartsWith("ctw: warning: The local API is off", StringComparison.Ordinal), Describe(result));
            Assert.IsFalse(Settings.ApiEnabled == true, "The command loaded the settings.");
            Settings.Load(Path.Combine(Folder, Settings.SettingsFileName));
            Assert.AreEqual(!warns, Settings.ApiEnabled == true, "The command and Settings read ApiEnabled differently.");
        }

        // As Ctrl+C: the code ends and its file goes
        [TestMethod]
        public void ApiPair_CancelDeletesItsOwnFile() {
            bool seen = false;

            CliResult result = RunPair(poll => {
                seen = ReadPending() != null;
                poll.Stop.Cancel();
            });

            Assert.IsTrue(seen, "The pairing file was not there while the command waited.");
            AssertEnded(result, "Cancelled. The code no longer works.");
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        // A file that holds another code when the cancel comes is never deleted
        [TestMethod]
        public void ApiPair_CancelLeavesAFileWithAnotherCode() {
            string newerId = null;

            CliResult result = RunPair(poll => {
                newerId = PairingFile.Create(DateTimeOffset.UtcNow).Id;
                poll.Stop.Cancel();
            });

            AssertEnded(result, "Cancelled.");
            Assert.AreEqual(newerId, ReadPending().Id);
        }

        // The code is made anyway, since the API may be turned on within its 5 minutes
        [TestMethod]
        [DataRow(null)]
        [DataRow("0")]
        public void ApiPair_WarnsWhenTheApiIsOff(string enabled) {
            File.WriteAllLines(Path.Combine(Folder, Settings.SettingsFileName), enabled != null ? new[] { "ApiEnabled=" + enabled } : new string[0]);

            CliResult result = RunPair(poll => SimulateFinish(ChromeOrigin));

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, Describe(result));
            StringAssert.StartsWith(result.Error, "ctw: warning: The local API is off: settings.txt does not have ApiEnabled=1. The code works only while the app or " +
                "ctw watch runs the local API." + Environment.NewLine);
            Assert.IsTrue(_codeLine.IsMatch(Lines(result.Output)[0]));
        }

        // A code that can't be shown is ended at once
        [TestMethod]
        public void ApiPair_FailsAndEndsTheCodeWhenItCannotBeWritten() {
            StringWriter error = new StringWriter(CultureInfo.InvariantCulture);
            ApiPairCommand.PollingForTesting = () => Assert.Fail("The command waited for a code it could not show.");

            int exitCode = CliApp.Run(new[] { "api-pair" }, new ApiTokenCommandTests.FailingWriter(), error, Folder, CancellationToken.None);

            Assert.AreEqual(CliApp.ExitFailure, exitCode, error.ToString());
            Assert.AreEqual("ctw: The pairing code could not be written to stdout, so it was cancelled." + Environment.NewLine, error.ToString());
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        [TestMethod]
        [DataRow(new[] { "api-pair" })]
        [DataRow(new[] { "api-pair", "--list" })]
        [DataRow(new[] { "api-pair", "--remove", "chrome" })]
        public void ApiPair_RefusesToRunAsRoot(string[] args) {
            Clients.Pair(ChromeOrigin, DateTimeOffset.UtcNow);
            OwnerOnlyFile.IsRootOnUnix = () => true;

            CliResult result = Run(args);

            AssertFailed(result, CliApp.ExitFailure, "ctw api-pair does not run as root");
            OwnerOnlyFile.IsRootOnUnix = () => false;
            Assert.IsFalse(File.Exists(PairingFile.Path));
            Assert.HasCount(1, Clients.Read());
        }

        // Families and dates only, never a hash, and no code is made
        [TestMethod]
        public void ApiPairList_PrintsThePairedBrowsersAndDates() {
            string chromeToken = Clients.Pair(ChromeOrigin, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
            Clients.Pair(FirefoxOrigin, new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.FromHours(2)));

            CliResult result = Run("api-pair", "--list");

            AssertSucceeded(result);
            Assert.AreEqual("chrome\t2026-01-02T03:04:05Z" + Environment.NewLine + "firefox\t2026-02-03T02:05:06Z" + Environment.NewLine, result.Output);
            Assert.IsFalse(result.Output.Contains(chromeToken.Substring(5), StringComparison.Ordinal));
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        [TestMethod]
        public void ApiPairList_WithNoPairedBrowserPrintsNothing() {
            CliResult result = Run("api-pair", "--list");

            AssertSucceeded(result);
            Assert.AreEqual(String.Empty, result.Output);
            Assert.IsFalse(File.Exists(PairingFile.Path));
            Assert.IsFalse(File.Exists(Clients.Path));
        }

        [TestMethod]
        public void ApiPairList_WithADamagedFileFails() {
            File.WriteAllText(Clients.Path, "1\nnot a line\n");

            CliResult result = Run("api-pair", "--list");

            AssertFailed(result, CliApp.ExitFailure, "api-clients.txt could not be read, or it is damaged, a link, or others can read it, so no browser extension can connect.");
            // The store can't tell an I/O failure from content that is not trusted, so no advice for one of them
            Assert.IsFalse(result.Error.Contains("Pair a browser again", StringComparison.Ordinal), result.Error);
        }

        [TestMethod]
        public void ApiPairRemove_WithADamagedFileFailsAndChangesNothing() {
            File.WriteAllText(Clients.Path, "1\nnot a line\n");

            AssertFailed(Run("api-pair", "--remove", "chrome"), CliApp.ExitFailure, "api-clients.txt could not be read, or it is damaged");
            Assert.AreEqual("1\nnot a line\n", File.ReadAllText(Clients.Path));
        }

        [TestMethod]
        public void ApiPairRemove_UnpairsOneBrowserAndKeepsTheOther() {
            Clients.Pair(ChromeOrigin, DateTimeOffset.UtcNow);
            Clients.Pair(FirefoxOrigin, DateTimeOffset.UtcNow);

            CliResult result = Run("api-pair", "--remove", "firefox");

            AssertSucceeded(result);
            Assert.AreEqual("Unpaired the Firefox extension. Its token no longer works." + Environment.NewLine, result.Output);
            CollectionAssert.AreEqual(new[] { "chrome" }, Clients.Read().Select(client => client.Family).ToArray());
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        [TestMethod]
        public void ApiPairRemove_OfABrowserThatIsNotPairedFails() {
            Clients.Pair(ChromeOrigin, DateTimeOffset.UtcNow);
            byte[] before = File.ReadAllBytes(Clients.Path);

            AssertFailed(Run("api-pair", "--remove", "firefox"), CliApp.ExitFailure, "The Firefox extension is not paired.");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Clients.Path));
        }

        [TestMethod]
        public void ApiPairRemove_WithoutAFileFails() {
            AssertFailed(Run("api-pair", "--remove", "chrome"), CliApp.ExitFailure, "The Chrome extension is not paired.");
            Assert.IsFalse(File.Exists(Clients.Path));
        }

        [TestMethod]
        [DataRow("safari")]
        [DataRow("Chrome")]
        public void ApiPairRemove_OfAnUnknownBrowserIsAUsageError(string family) {
            Clients.Pair(ChromeOrigin, DateTimeOffset.UtcNow);

            AssertFailed(Run("api-pair", "--remove", family), CliApp.ExitUsage,
                "Option '--remove' takes chrome or firefox, not '" + family + "'. Usage: ctw api-pair [--list | --remove <chrome|firefox>]");
            Assert.HasCount(1, Clients.Read());
        }

        // The options are named in a fixed order, whatever the order given
        [TestMethod]
        [DataRow(new[] { "api-pair", "--list", "--remove", "chrome" }, "Give only one of '--list' and '--remove'. Usage: ctw api-pair [--list | --remove <chrome|firefox>]")]
        [DataRow(new[] { "api-pair", "--remove", "chrome", "--list" }, "Give only one of '--list' and '--remove'. Usage: ctw api-pair")]
        [DataRow(new[] { "api-pair", "--list", "--list" }, "Option '--list' is given more than once.")]
        [DataRow(new[] { "api-pair", "--remove" }, "Option '--remove' needs a value")]
        [DataRow(new[] { "api-pair", "--list", "extra" }, "Wrong arguments. Usage: ctw api-pair")]
        [DataRow(new[] { "api-pair", "--force" }, "Unknown option '--force'. Usage: ctw api-pair")]
        [DataRow(new[] { "api-token", "--list" }, "Unknown option '--list'. Usage: ctw api-token")]
        public void ApiPair_InvalidCommandLineMakesNoCode(string[] args, string message) {
            AssertFailed(Run(args), CliApp.ExitUsage, message);
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        [TestMethod]
        public void ApiPair_HelpPrintsItsUsage() {
            CliResult help = Run("api-pair", "--help");

            AssertSucceeded(help);
            Assert.AreEqual("Usage: ctw api-pair [--list | --remove <chrome|firefox>]" + Environment.NewLine, help.Output);
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        // What the API does on a good finish: it saves the browser's token, then marks the code paired
        private void SimulateFinish(string origin) {
            ApiPendingPairing pending = ReadPending();
            Clients.Pair(origin, DateTimeOffset.UtcNow);
            Assert.IsTrue(PairingFile.MarkPaired(pending, ApiPairing.FamilyOf(origin)));
        }

        private ApiPendingPairing ReadPending() {
            bool refused;
            return PairingFile.Read(out refused);
        }

        // Runs ctw api-pair in this thread; onPoll runs before each look at the file. The stop token stands in for
        // Ctrl+C, and ends a run that would otherwise wait.
        private CliResult RunPair(Action<PairPoll> onPoll) {
            StringWriter output = new StringWriter(CultureInfo.InvariantCulture);
            StringWriter error = new StringWriter(CultureInfo.InvariantCulture);
            using (CancellationTokenSource stop = new CancellationTokenSource(Timeout)) {
                int polls = 0;
                ApiPairCommand.PollingForTesting = () => onPoll(new PairPoll { Number = ++polls, Output = output.ToString(), Stop = stop });
                int exitCode = CliApp.Run(new[] { "api-pair" }, output, error, Folder, stop.Token);
                return new CliResult { ExitCode = exitCode, Output = output.ToString(), Error = error.ToString() };
            }
        }

        private CliResult RunWithToken(CancellationToken stop) {
            StringWriter output = new StringWriter(CultureInfo.InvariantCulture);
            StringWriter error = new StringWriter(CultureInfo.InvariantCulture);
            int exitCode = CliApp.Run(new[] { "api-pair" }, output, error, Folder, stop);
            return new CliResult { ExitCode = exitCode, Output = output.ToString(), Error = error.ToString() };
        }

        // The code from stdout, which is the code alone on its first line
        private static string CodeOf(string output) {
            string code = Lines(output)[0];
            Assert.IsTrue(_codeLine.IsMatch(code), "stdout does not start with a code.");
            return code;
        }

        // A pairing that ended without a browser: an error on stderr after the note, and the code printed once
        private static void AssertEnded(CliResult result, string messagePart) {
            Assert.AreEqual(CliApp.ExitFailure, result.ExitCode, Describe(result));
            string code = CodeOf(result.Output);
            Assert.AreEqual(code + Environment.NewLine, result.Output, "More than the code on stdout.");
            string[] errors = Lines(result.Error);
            Assert.HasCount(2, errors, Describe(result));
            StringAssert.StartsWith(errors[1], "ctw: " + messagePart);
            AssertCodeNowhereElse(code, result.Error, "");
        }

        // Not on stderr, not in the file, and not in the log, with or without its hyphen
        private static void AssertCodeNowhereElse(string code, string error, string fileText) {
            string bare = code.Replace("-", "");
            string log = ReadLog();
            foreach (string form in new[] { code, bare }) {
                Assert.IsFalse(error.Contains(form, StringComparison.Ordinal), "The code is on stderr.");
                Assert.IsFalse(fileText.Contains(form, StringComparison.Ordinal), "The code is in the pairing file.");
                Assert.IsFalse(log.Contains(form, StringComparison.Ordinal), "The code is in the log.");
            }
        }

        // Exit code and stderr only
        private static string Describe(CliResult result) {
            return "exit " + result.ExitCode + ", stderr: " + result.Error;
        }

        // Only the current user can read or write the file, as ApiTokenCommandTests checks api-token.txt
        private static void AssertOwnerOnly(string path) {
            if (OperatingSystem.IsWindows()) {
                ApiTokenCommandTests.AssertOwnerOnlyOnWindows(path);
            }
            else {
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }

        // The log that every test's runs write to (CliTestHost opens it first)
        private static string ReadLog() {
            using (FileStream stream = new FileStream(Path.Combine(Settings.GetSettingsDirectory(), Settings.LogFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream)) {
                return reader.ReadToEnd();
            }
        }

        private sealed class PairPoll {
            public int Number { get; set; }
            public string Output { get; set; }
            public CancellationTokenSource Stop { get; set; }
        }
    }
}
