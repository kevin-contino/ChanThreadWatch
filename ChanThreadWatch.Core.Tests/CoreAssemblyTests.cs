using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The Core part of the assembly tests; the tests that compare Core with the app are in ChanThreadWatch.Tests
    [TestClass]
    public class CoreAssemblyTests {
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
