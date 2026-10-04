using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The non-UI classes live in ChanThreadWatch.Core, a separate assembly from the WinForms app
    [TestClass]
    public class CoreAssemblyTests {
        [TestMethod]
        public void OfflinePageScriptIsEmbeddedInCore() {
            var core = typeof(OfflinePageScript).Assembly;
            Assert.AreEqual("ChanThreadWatch.Core", core.GetName().Name);
            CollectionAssert.Contains(core.GetManifestResourceNames(), "OfflinePageScript.js");
            using (Stream stream = core.GetManifestResourceStream("OfflinePageScript.js")) {
                Assert.IsGreaterThan(0L, stream.Length);
            }
            CollectionAssert.DoesNotContain(typeof(frmChanThreadWatch).Assembly.GetManifestResourceNames(), "OfflinePageScript.js");
        }

        [TestMethod]
        public void VersionIsTheAppVersionOnceTheAppSetsIt() {
            Version app = typeof(frmChanThreadWatch).Assembly.GetName().Version;
            Assert.AreNotEqual(typeof(General).Assembly.GetName().Version, app, "Core must not share the app version, or this test proves nothing");
            Version saved = General.HostVersion;
            try {
                Program.SetHostVersion();
                Assert.AreEqual(app.Major + "." + app.Minor + "." + app.Revision, General.Version);
            }
            finally {
                General.HostVersion = saved;
            }
        }

        [TestMethod]
        public void VersionFailsWhenTheHostVersionIsNotSet() {
            Version saved = General.HostVersion;
            try {
                General.HostVersion = null;
                Assert.ThrowsExactly<InvalidOperationException>(() => General.Version);
            }
            finally {
                General.HostVersion = saved;
            }
        }
    }
}
