using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // IsKnownHost is the local API's check that a thread's site has a helper of its own (G12)
    [TestClass]
    public class SiteHelpersKnownHostTests {
        private static IEnumerable<string> BuiltInDomains() {
            FieldInfo field = typeof(SiteHelpers).GetField("_siteHelpers", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field);
            Dictionary<string, Type> helpers = (Dictionary<string, Type>)field.GetValue(null);
            Assert.IsGreaterThan(0, helpers.Count);
            return helpers.Keys;
        }

        // Every registered domain, a subdomain of it and any case, as GetInstance gives its helper for them
        [TestMethod]
        public void EveryBuiltInSiteHostIsKnown() {
            foreach (string domain in BuiltInDomains()) {
                foreach (string host in new[] { domain, "boards." + domain, domain.ToUpperInvariant(), "Sys." + domain }) {
                    Assert.IsTrue(SiteHelpers.IsKnownHost(host), host);
                    Assert.AreNotEqual(typeof(SiteHelper), SiteHelpers.GetInstance(host).GetType(), host);
                }
            }
        }

        [TestMethod]
        [DataRow("boards.4chan.org")]
        [DataRow("boards.4channel.org")]
        [DataRow("8ch.net")]
        [DataRow("warosu.org")]
        [DataRow("archive.4plebs.org")]
        [DataRow("archive.alice.al")]
        [DataRow("desuarchive.org")]
        [DataRow("arch.b4k.dev")]
        [DataRow("arch.b4k.co")]
        [DataRow("archived.moe")]
        [DataRow("thebarchive.com")]
        [DataRow("archiveofsins.com")]
        [DataRow("archive.rebeccablacktech.com")]
        [DataRow("rbt.asia")]
        [DataRow("endchan.net")]
        [DataRow("endchan.org")]
        [DataRow("BOARDS.4CHAN.ORG")]
        [DataRow("xn--bcher-kva.4chan.org", DisplayName = "punycode subdomain")]
        public void KnownHosts(string host) {
            Assert.IsTrue(SiteHelpers.IsKnownHost(host));
        }

        [TestMethod]
        [DataRow("example.com")]
        [DataRow("krautchan.net")]
        [DataRow("localhost")]
        [DataRow("127.0.0.1")]
        [DataRow("[::1]")]
        [DataRow("org")]
        [DataRow("chan.org", DisplayName = "a shorter name is not a suffix match")]
        [DataRow("not4chan.org", DisplayName = "matched by whole labels only")]
        [DataRow("4chan.org.example.com", DisplayName = "a known name in front of another domain")]
        [DataRow("4chan.org.", DisplayName = "trailing dot")]
        [DataRow("boards.4chan.org.", DisplayName = "subdomain with a trailing dot")]
        [DataRow("４chan.org", DisplayName = "fullwidth digit, not normalized")]
        [DataRow("")]
        [DataRow(null)]
        public void UnknownHosts(string host) {
            Assert.IsFalse(SiteHelpers.IsKnownHost(host));
        }

        // The watcher picks its helper by Uri.Host. For an ASCII host, Uri.Host equals Uri.IdnHost and is known
        [TestMethod]
        [DataRow("http://BOARDS.4chan.ORG/a/thread/1", "boards.4chan.org")]
        public void AsciiHostOfAUrlIsKnown(string url, string host) {
            Uri uri = new Uri(url);

            Assert.AreEqual(host, uri.Host);
            Assert.AreEqual(uri.Host, uri.IdnHost);
            Assert.IsTrue(SiteHelpers.IsKnownHost(uri.Host));
        }

        // A non-ASCII host has a Uri.Host that differs from its Uri.IdnHost, so a caller must refuse such a URL
        [TestMethod]
        [DataRow("http://４chan.org/a/thread/1")]
        [DataRow("http://bücher.4chan.org/a/thread/1")]
        public void NonAsciiHostDiffersFromItsIdnHost(string url) {
            Uri uri = new Uri(url);

            Assert.AreNotEqual(uri.IdnHost, uri.Host);
        }

        // Why: the IdnHost of a fullwidth host is known, but the watcher picks its helper by Host and gets the generic one
        [TestMethod]
        public void FullwidthHostIsKnownOnlyByItsIdnHost() {
            Uri uri = new Uri("http://４chan.org/a/thread/1");

            Assert.IsTrue(SiteHelpers.IsKnownHost(uri.IdnHost));
            Assert.IsFalse(SiteHelpers.IsKnownHost(uri.Host));
            Assert.AreEqual(typeof(SiteHelper), SiteHelpers.GetInstance(uri.Host).GetType());
        }

        [TestMethod]
        public void TestRegisteredHostIsKnownUntilUnregistered() {
            const string host = "known-host-test.invalid";
            Assert.IsFalse(SiteHelpers.IsKnownHost(host));
            SiteHelpers.RegisterHostForTesting(host, typeof(FourChanSiteHelper));
            try {
                Assert.IsTrue(SiteHelpers.IsKnownHost(host));
                Assert.IsTrue(SiteHelpers.IsKnownHost(host.ToUpperInvariant()));
            }
            finally {
                SiteHelpers.UnregisterHostForTesting(host);
            }
            Assert.IsFalse(SiteHelpers.IsKnownHost(host));
        }

        // A registration of a type that is not a site helper gives the generic helper, so the host is not known
        [TestMethod]
        public void TestRegisteredNonHelperTypeIsNotKnown() {
            const string host = "not-a-helper-test.invalid";
            SiteHelpers.RegisterHostForTesting(host, typeof(string));
            try {
                Assert.IsFalse(SiteHelpers.IsKnownHost(host));
                Assert.AreEqual(typeof(SiteHelper), SiteHelpers.GetInstance(host).GetType());
            }
            finally {
                SiteHelpers.UnregisterHostForTesting(host);
            }
        }
    }
}
