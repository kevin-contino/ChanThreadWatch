using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Api.Tests {
    // ApiPairingFollow, the follow of a code shared by ctw api-pair and the app's Local API dialog: it makes a code and
    // reads api-pairing.txt on each look. The API's side (marking the file paired, deleting it, saving a paired browser)
    // is done here through the same library calls the server makes, between two looks. Every code is made at run time
    // in the test's temporary folder; a code never goes into an assertion's message.
    [TestClass]
    public class ApiPairingFollowTests {
        private const string ChromeOrigin = ApiPairing.ChromeScheme + ApiPairing.ChromeExtensionId;
        private const string FirefoxOrigin = "moz-extension://0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5f";

        private string _folder;
        private DateTimeOffset _now;

        [TestInitialize]
        public void SetUp() {
            _folder = Path.Combine(Path.GetTempPath(), "ctw-pairing-follow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            // A whole second, as the code's expiry is
            _now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        [TestCleanup]
        public void TearDown() {
            OwnerOnlyFile.NewFileCheckForTesting = null;
            ApiTokenStore.OpeningForTesting = null;
            Directory.Delete(_folder, true);
        }

        private ApiPairingFile PairingFile {
            get { return new ApiPairingFile(_folder); }
        }

        private ApiClientStore Clients {
            get { return new ApiClientStore(_folder); }
        }

        private ApiPairingFollow MakeCode() {
            ApiPairingFollow follow = new ApiPairingFollow(_folder, () => _now);
            follow.MakeCode();
            return follow;
        }

        private ApiPendingPairing ReadPending() {
            bool refused;
            return PairingFile.Read(out refused);
        }

        // What the API does on a good finish: it saves the browser's token, then marks the code paired
        private void SimulateFinish(string origin) {
            ApiPendingPairing pending = ReadPending();
            Clients.Pair(origin, _now);
            Assert.IsTrue(PairingFile.MarkPaired(pending, ApiPairing.FamilyOf(origin)));
        }

        [TestMethod]
        [DataRow(ChromeOrigin, "chrome")]
        [DataRow(FirefoxOrigin, "firefox")]
        public void APendingCodeIsFollowedUntilABrowserPairs(string origin, string family) {
            ApiPairingFollow follow = MakeCode();

            Assert.AreEqual(follow.Code.Id, ReadPending().Id);
            Assert.AreEqual(ApiPolicy.PairingCodeLifetime, follow.Remaining, "The clock is the one given.");
            Assert.IsNull(follow.Look());
            Assert.IsNull(follow.Look());

            SimulateFinish(origin);

            Assert.AreEqual(ApiPairingEnd.Paired, follow.Look());
            Assert.AreEqual(family, follow.PairedFamily);
            Assert.IsTrue(File.Exists(PairingFile.Path), "A look deleted the file.");
            Assert.IsTrue(follow.End());
            Assert.IsFalse(File.Exists(PairingFile.Path), "End did not delete the code's file.");
        }

        // The API can save the browser's token and then find the file gone; a browser that paired since the code was
        // made is a pairing
        [TestMethod]
        public void AFileGoneAfterANewPairingIsAPairing() {
            ApiPairingFollow follow = MakeCode();
            Clients.Pair(FirefoxOrigin, _now);
            File.Delete(PairingFile.Path);

            Assert.AreEqual(ApiPairingEnd.Paired, follow.Look());
            Assert.AreEqual("firefox", follow.PairedFamily);
        }

        // A browser paired before the code was made is not this code's pairing: the code was burned
        [TestMethod]
        public void AFileGoneWithoutANewPairingEndedTheCode() {
            Clients.Pair(ChromeOrigin, _now);
            ApiPairingFollow follow = MakeCode();
            File.Delete(PairingFile.Path);

            Assert.AreEqual(ApiPairingEnd.Ended, follow.Look());
            Assert.IsNull(follow.PairedFamily);
        }

        [TestMethod]
        public void TheCodeExpiresAfterItsLifetime() {
            ApiPairingFollow follow = MakeCode();
            _now += ApiPolicy.PairingCodeLifetime - TimeSpan.FromSeconds(1);
            Assert.IsNull(follow.Look());
            Assert.AreEqual(TimeSpan.FromSeconds(1), follow.Remaining);

            _now += TimeSpan.FromSeconds(1);

            Assert.AreEqual(ApiPairingEnd.Expired, follow.Look());
            Assert.IsTrue(follow.End());
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        // A finish can save the browser's token in the code's last moment and mark the file a moment too late: a
        // pending code past its expiry with a browser paired since the code was made is a pairing, not an expiry
        [TestMethod]
        public void APendingCodePastItsExpiryWithANewPairingIsAPairing() {
            ApiPairingFollow follow = MakeCode();
            Clients.Pair(ChromeOrigin, _now);
            _now += ApiPolicy.PairingCodeLifetime;

            Assert.IsNotNull(ReadPending(), "The file still holds the pending code.");
            Assert.AreEqual(ApiPairingEnd.Paired, follow.Look());
            Assert.AreEqual("chrome", follow.PairedFamily);
        }

        // The API deletes a code it meets expired; that is an expiry, not a burned code
        [TestMethod]
        public void AFileGoneAfterTheExpiryIsAnExpiry() {
            ApiPairingFollow follow = MakeCode();
            _now += ApiPolicy.PairingCodeLifetime;
            File.Delete(PairingFile.Path);

            Assert.AreEqual(ApiPairingEnd.Expired, follow.Look());
        }

        // A code the file says is paired is a pairing, also once the clock is past its expiry
        [TestMethod]
        public void ThePairedStateWinsOverTheExpiry() {
            ApiPairingFollow follow = MakeCode();
            SimulateFinish(ChromeOrigin);
            _now += ApiPolicy.PairingCodeLifetime + TimeSpan.FromMinutes(1);

            Assert.AreEqual(ApiPairingEnd.Paired, follow.Look());
        }

        // Another creator's code is reported and left in place
        [TestMethod]
        public void AReplacedCodeEndsAndLeavesTheNewerFile() {
            ApiPairingFollow follow = MakeCode();
            string newerId = PairingFile.Create(_now).Id;

            Assert.AreEqual(ApiPairingEnd.Replaced, follow.Look());
            Assert.IsTrue(follow.End());
            Assert.AreEqual(newerId, ReadPending().Id);
        }

        [TestMethod]
        public void AFileChangedByAnotherProgramEndsTheCodeAndIsLeft() {
            ApiPairingFollow follow = MakeCode();
            ApiTokenStore.WriteOwnerOnlyText(PairingFile.Path, "1\nnot a code\n", "test: ");

            Assert.AreEqual(ApiPairingEnd.Changed, follow.Look());
            follow.End();
            Assert.AreEqual("1\nnot a code\n", File.ReadAllText(PairingFile.Path), "Another program's file was deleted or changed.");
        }

        // A file that can't be read is waited for; at the expiry, a browser that paired since the code is a pairing
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void AnUnreadableFileIsWaitedForUntilTheExpiry(bool pairedMeanwhile) {
            ApiPairingFollow follow = MakeCode();
            if (pairedMeanwhile) Clients.Pair(FirefoxOrigin, _now);
            string pairingPath = PairingFile.Path;
            ApiTokenStore.OpeningForTesting = path => {
                if (path == pairingPath) throw new IOException("test: access denied", unchecked((int)0x80070005));
            };

            Assert.IsNull(follow.Look());
            _now += ApiPolicy.PairingCodeLifetime;

            Assert.AreEqual(pairedMeanwhile ? ApiPairingEnd.Paired : ApiPairingEnd.Expired, follow.Look());
        }

        // With the pairing file gone and api-clients.txt not usable just now, the follow goes on until the expiry
        // rather than reporting a burned code
        [TestMethod]
        public void AFileGoneWhileThePairedBrowsersCannotBeReadIsWaitedFor() {
            ApiPairingFollow follow = MakeCode();
            ApiTokenStore.WriteOwnerOnlyText(Clients.Path, "1\nnot a line\n", "test: ");
            File.Delete(PairingFile.Path);

            Assert.IsNull(follow.Look());
            _now += ApiPolicy.PairingCodeLifetime;
            Assert.AreEqual(ApiPairingEnd.Expired, follow.Look());
        }

        // The paired browsers are taken once the code is made, not before: a pairing saved while the code was made
        // can't be one with this code, which nobody knew yet
        [TestMethod]
        public void APairingSavedWhileTheCodeWasMadeIsNotThisCodes() {
            bool paired = false;
            OwnerOnlyFile.NewFileCheckForTesting = stream => {
                if (!paired) {
                    paired = true;
                    Clients.Pair(ChromeOrigin, _now);
                }
                return true;
            };
            ApiPairingFollow follow;
            try {
                follow = MakeCode();
            }
            finally {
                OwnerOnlyFile.NewFileCheckForTesting = null;
            }
            File.Delete(PairingFile.Path);

            Assert.IsTrue(paired);
            Assert.AreEqual(ApiPairingEnd.Ended, follow.Look());
        }

        // An end before any code does nothing; a second end finds the file gone
        [TestMethod]
        public void EndBeforeTheCodeAndTwiceIsSafe() {
            ApiPairingFollow follow = new ApiPairingFollow(_folder, () => _now);
            Assert.IsTrue(follow.End());
            Assert.IsNull(follow.Code);

            follow.MakeCode();
            Assert.IsTrue(follow.End());
            Assert.IsTrue(follow.End());
            Assert.IsFalse(File.Exists(PairingFile.Path));
        }

        // A code that can't be written owner-only is an error, and leaves no file
        [TestMethod]
        public void ACodeThatCannotBeOwnerOnlyIsAnError() {
            ApiPairingFollow follow = new ApiPairingFollow(_folder, () => _now);
            OwnerOnlyFile.NewFileCheckForTesting = stream => false;
            try {
                Assert.ThrowsExactly<ApiTokenException>(() => follow.MakeCode());
            }
            finally {
                OwnerOnlyFile.NewFileCheckForTesting = null;
            }

            Assert.IsNull(follow.Code);
            Assert.IsEmpty(Directory.GetFiles(_folder));
        }

        [TestMethod]
        public void EachEndHasItsText() {
            const string again = " Again.";
            Assert.AreEqual("Paired with the Chrome extension", ApiPairingFollow.DescribeEnd(ApiPairingEnd.Paired, "chrome", again, "X"));
            Assert.AreEqual("Paired with the Firefox extension", ApiPairingFollow.DescribeEnd(ApiPairingEnd.Paired, "firefox", again, "X"));
            Assert.AreEqual("The code expired before a browser extension paired with it. Again.", ApiPairingFollow.DescribeEnd(ApiPairingEnd.Expired, null, again, "X"));
            Assert.AreEqual("The code no longer works, and no browser extension paired with it: wrong codes used up its tries, or api-pairing.txt was deleted. Again.",
                ApiPairingFollow.DescribeEnd(ApiPairingEnd.Ended, null, again, "X"));
            Assert.AreEqual("A newer code from another program that makes codes, such as X, replaced this one. Use the newer code.",
                ApiPairingFollow.DescribeEnd(ApiPairingEnd.Replaced, null, again, "X"));
            Assert.AreEqual("api-pairing.txt was changed by another program, so the code no longer works. Again.", ApiPairingFollow.DescribeEnd(ApiPairingEnd.Changed, null, again, "X"));
            Assert.AreEqual("Cancelled. The code no longer works.", ApiPairingFollow.DescribeEnd(ApiPairingEnd.Cancelled, null, again, "X"));
        }
    }
}
