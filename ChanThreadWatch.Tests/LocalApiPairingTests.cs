using System;
using System.Collections.Generic;
using System.IO;
using JDP.Api;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The Local API dialog's pairing: LocalApiPairing makes a code off the UI thread and follows it (ApiPairingFollow,
    // whose looks the Api tests cover), ends a code the dialog dropped while it was made, and the texts the dialog
    // shows. Every code is made at run time in the test's temporary folder; a code never goes into an assertion's
    // message.
    [TestClass]
    public class LocalApiPairingTests {
        private const string ChromeOrigin = ApiPairing.ChromeScheme + ApiPairing.ChromeExtensionId;
        private const string FirefoxOrigin = "moz-extension://0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5f";

        private string _folder;
        private DateTimeOffset _now;

        [TestInitialize]
        public void SetUp() {
            _folder = Path.Combine(Path.GetTempPath(), "ctw-app-pairing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            // A whole second, as the code's expiry is
            _now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        [TestCleanup]
        public void TearDown() {
            OwnerOnlyFile.NewFileCheckForTesting = null;
            Directory.Delete(_folder, true);
        }

        private ApiPairingFile PairingFile {
            get { return new ApiPairingFile(_folder); }
        }

        private ApiClientStore Clients {
            get { return new ApiClientStore(_folder); }
        }

        // The wrapper follows the code with the clock it was given, and End deletes the code's file
        [TestMethod]
        public void ACodeIsFollowedWithTheGivenClockAndEnded() {
            LocalApiPairing pairing = new LocalApiPairing(_folder, () => _now);
            pairing.MakeCode();

            Assert.IsTrue(File.Exists(PairingFile.Path));
            Assert.AreEqual(ApiPolicy.PairingCodeLifetime, pairing.Remaining);
            Assert.IsNull(pairing.Look());
            _now += ApiPolicy.PairingCodeLifetime;
            Assert.AreEqual(ApiPairingEnd.Expired, pairing.Look());

            pairing.End();
            Assert.IsFalse(File.Exists(PairingFile.Path), "End did not delete the code's file.");
        }

        // The dialog closed while the code was made (here while its file is written): the code is deleted once made
        [TestMethod]
        public void ACodeEndedWhileItIsMadeIsDeletedOnceMade() {
            LocalApiPairing pairing = new LocalApiPairing(_folder, () => _now);
            bool written = false;
            OwnerOnlyFile.NewFileCheckForTesting = stream => {
                written = true;
                pairing.End();
                return true;
            };
            try {
                pairing.MakeCode();
            }
            finally {
                OwnerOnlyFile.NewFileCheckForTesting = null;
            }

            Assert.IsTrue(written);
            Assert.IsNotNull(pairing.Code);
            Assert.IsFalse(File.Exists(PairingFile.Path), "The ended code's file was left.");
        }

        // An end before the code is made deletes it once made; a second end does nothing more
        [TestMethod]
        public void EndBeforeTheCodeAndTwiceIsSafe() {
            LocalApiPairing pairing = new LocalApiPairing(_folder, () => _now);
            pairing.End();
            Assert.IsNull(pairing.Code);

            pairing.MakeCode();
            Assert.IsFalse(File.Exists(PairingFile.Path), "A code ended before it was made was left.");
            pairing.End();
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        // A code that can't be written owner-only is an error, and leaves no file
        [TestMethod]
        public void ACodeThatCannotBeOwnerOnlyIsAnError() {
            LocalApiPairing pairing = new LocalApiPairing(_folder, () => _now);
            OwnerOnlyFile.NewFileCheckForTesting = stream => false;
            try {
                Assert.ThrowsExactly<ApiTokenException>(() => pairing.MakeCode());
            }
            finally {
                OwnerOnlyFile.NewFileCheckForTesting = null;
            }

            Assert.IsNull(pairing.Code);
            Assert.IsEmpty(Directory.GetFiles(_folder));
            pairing.End();
        }

        [TestMethod]
        [DataRow(300.0, "5:00")]
        [DataRow(299.2, "5:00")]
        [DataRow(299.0, "4:59")]
        [DataRow(61.0, "1:01")]
        [DataRow(60.0, "1:00")]
        [DataRow(9.5, "0:10")]
        [DataRow(0.1, "0:01")]
        [DataRow(0.0, "0:00")]
        [DataRow(-30.0, "0:00")]
        public void TheCountdownIsMinutesAndSecondsRoundedUp(double seconds, string shown) {
            Assert.AreEqual("Enter this code in the extension's options within " + shown + ".", LocalApiPairing.DescribeCountdown(TimeSpan.FromSeconds(seconds)));
        }

        // The dialog's advice and the other program that makes codes
        [TestMethod]
        public void EachEndHasTheDialogsText() {
            const string again = " Click \"Pair extension...\" for a new code.";
            Assert.AreEqual("Paired with the Chrome extension", LocalApiPairing.DescribeEnd(ApiPairingEnd.Paired, "chrome"));
            Assert.AreEqual("The code expired before a browser extension paired with it." + again, LocalApiPairing.DescribeEnd(ApiPairingEnd.Expired, null));
            Assert.AreEqual("A newer code from another program that makes codes, such as 'ctw api-pair', replaced this one. Use the newer code.",
                LocalApiPairing.DescribeEnd(ApiPairingEnd.Replaced, null));
            Assert.AreEqual("api-pairing.txt was changed by another program, so the code no longer works." + again, LocalApiPairing.DescribeEnd(ApiPairingEnd.Changed, null));
        }

        // A code works only while the API listens: Off and Failed end it, Starting (a restart) and Listening do not
        [TestMethod]
        public void AStoppedApiEndsTheCode() {
            const string stopped = "The local API stopped, so the code was cancelled.";
            Assert.AreEqual(stopped, LocalApiPairing.DescribeStop(LocalApiState.Off));
            Assert.AreEqual(stopped, LocalApiPairing.DescribeStop(LocalApiState.Failed));
            Assert.IsNull(LocalApiPairing.DescribeStop(LocalApiState.Starting));
            Assert.IsNull(LocalApiPairing.DescribeStop(LocalApiState.Listening));
        }

        [TestMethod]
        public void AFailureIsShownWithItsMessage() {
            Assert.AreEqual("The code was cancelled: test: disk full", LocalApiPairing.DescribeFailure(new IOException("test: disk full")));
        }

        // Each row names its own browser's date in the zone given, never the other's. The zone is two hours east of
        // UTC, so a date in UTC (or in the machine's zone) would not match.
        [TestMethod]
        public void TheRowsShowThePairedDateNotPairedOrUnknown() {
            TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");
            Clients.Pair(FirefoxOrigin, new DateTimeOffset(2026, 2, 3, 23, 5, 6, TimeSpan.Zero));
            IReadOnlyList<ApiClient> onlyFirefox = Clients.Read();
            Clients.Pair(ChromeOrigin, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
            IReadOnlyList<ApiClient> both = Clients.Read();

            Assert.AreEqual("Not paired", LocalApiPairing.DescribeClient(onlyFirefox, "chrome", zone));
            Assert.AreEqual("Paired on 2026/02/04 01:05", LocalApiPairing.DescribeClient(onlyFirefox, "firefox", zone));
            Assert.AreEqual("Paired on 2026/01/02 05:04", LocalApiPairing.DescribeClient(both, "chrome", zone));
            Assert.AreEqual("Paired on 2026/02/04 01:05", LocalApiPairing.DescribeClient(both, "firefox", zone));
            Assert.AreEqual("Paired on 2026/02/03 23:05", LocalApiPairing.DescribeClient(both, "firefox", TimeZoneInfo.Utc));
            Assert.AreEqual("Not paired", LocalApiPairing.DescribeClient(new ApiClient[0], "firefox", zone));
            Assert.AreEqual("Unknown", LocalApiPairing.DescribeClient(null, "chrome", zone));
        }

        [TestMethod]
        public void FindClientFindsOnlyThatFamily() {
            Clients.Pair(FirefoxOrigin, _now);
            IReadOnlyList<ApiClient> clients = Clients.Read();

            Assert.AreEqual(FirefoxOrigin, LocalApiPairing.FindClient(clients, "firefox").Origin);
            Assert.IsNull(LocalApiPairing.FindClient(clients, "chrome"));
            Assert.IsNull(LocalApiPairing.FindClient(null, "firefox"));
        }

        // Why an unpair removed nothing, from the file as it is after the attempt
        [TestMethod]
        public void AnUnpairThatRemovedNothingSaysWhy() {
            Clients.Pair(ChromeOrigin, _now);

            Assert.AreEqual("api-clients.txt could not be read, or it is damaged, a link, or others can read it, so nothing was unpaired.",
                LocalApiPairing.DescribeUnpairFailure(null, "chrome"));
            Assert.AreEqual("The Firefox extension is not paired.", LocalApiPairing.DescribeUnpairFailure(Clients.Read(), "firefox"));
            Assert.AreEqual("The Chrome extension was paired again meanwhile, so it was not unpaired. Check the date and try again.",
                LocalApiPairing.DescribeUnpairFailure(Clients.Read(), "chrome"));
        }

        // The dialog's unpair: the line it showed is removed only while it is still the same pairing
        [TestMethod]
        public void AnUnpairRemovesOnlyThePairingTheDialogShowed() {
            Clients.Pair(ChromeOrigin, _now);
            ApiClient shown = LocalApiPairing.FindClient(Clients.Read(), "chrome");
            Clients.Pair(ChromeOrigin, _now);

            Assert.IsFalse(Clients.Remove("chrome", shown.Hash));
            Assert.IsNotNull(LocalApiPairing.FindClient(Clients.Read(), "chrome"), "The newer pairing was removed.");

            shown = LocalApiPairing.FindClient(Clients.Read(), "chrome");
            Assert.IsTrue(Clients.Remove("chrome", shown.Hash));
            Assert.IsNull(LocalApiPairing.FindClient(Clients.Read(), "chrome"));
        }
    }
}
