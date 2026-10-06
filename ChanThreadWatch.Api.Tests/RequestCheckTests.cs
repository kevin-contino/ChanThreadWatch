using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Api.Tests {
    // ApiSecurity: Host, query string, Origin and Sec-Fetch-Site, in that order and ahead of the token and routing
    [TestClass]
    public class RequestCheckTests : ApiTestBase {
        private string P {
            get { return Port.ToString(CultureInfo.InvariantCulture); }
        }

        [TestMethod]
        public void Host_SelfFormsAreAccepted() {
            foreach (string host in new[] { "127.0.0.1:" + P, "localhost:" + P, "LocalHost:" + P, "LOCALHOST:" + P }) {
                RawResponse response = SendRaw(RawGet(ThreadsEndpoints.ThreadsPath, host));
                Assert.AreEqual(200, response.Status, host + ": " + response);
            }
        }

        [TestMethod]
        public void Host_OtherFormsAreRefusedBeforeTheToken() {
            int otherPort = Port == 65535 ? Port - 1 : Port + 1;
            string[] hosts = {
                "127.0.0.1", "localhost", "127.0.0.1:" + otherPort, "localhost:" + otherPort, "localhost.:" + P, "[::1]:" + P,
                "127.1:" + P, "0x7f.0.0.1:" + P, "0x7f000001:" + P, "2130706433:" + P, "127.0.0.2:" + P, "0.0.0.0:" + P,
                "evil.example:" + P, "localhost.evil.example:" + P, "127.0.0.1:" + P + ".evil.example", "127.0.0.1:0" + P, " 127.0.0.1:" + P + "x"
            };
            List<string> refusedByKestrel = new List<string>();
            foreach (string host in hosts) {
                RawResponse response = SendRaw(RawGet(ThreadsEndpoints.ThreadsPath, host));
                Assert.AreEqual(400, response.Status, host + ": " + response);
                if (!response.Text.Contains("\"code\":\"invalid_host\"", StringComparison.Ordinal)) refusedByKestrel.Add(host);
            }
            // Kestrel refuses a Host whose port is not a number itself; ApiSecurity refuses every other row
            CollectionAssert.AreEquivalent(new[] { "127.0.0.1:" + P + ".evil.example", " 127.0.0.1:" + P + "x" }, refusedByKestrel, String.Join(" | ", refusedByKestrel));
        }

        [TestMethod]
        public void Host_WrongHostWithAValidTokenIsTheHostError() {
            RawResponse response = SendRaw(RawGet(ThreadsEndpoints.ThreadsPath, "evil.example:" + P));
            Assert.AreEqual(400, response.Status, response.ToString());
            StringAssert.Contains(response.Text, "\"code\":\"invalid_host\"");
            StringAssert.Contains(response.Head, "Cache-Control: no-store");
        }

        // HTTP/1.1 without Host is refused by Kestrel itself; HTTP/1.0 allows it, and ApiSecurity refuses it
        [TestMethod]
        public void Host_MissingIsRefused() {
            Assert.AreEqual(400, SendRaw(RawGet(ThreadsEndpoints.ThreadsPath, null)).Status);
            RawResponse http10 = SendRaw(RawGet(ThreadsEndpoints.ThreadsPath, null, "", "HTTP/1.0"));
            Assert.AreEqual(400, http10.Status, http10.ToString());
            StringAssert.Contains(http10.Text, "\"code\":\"invalid_host\"");
        }

        [TestMethod]
        public void Host_TwoHostHeadersAreRefused() {
            RawResponse response = SendRaw(RawGet(ThreadsEndpoints.ThreadsPath, "127.0.0.1:" + P, "Host: 127.0.0.1:" + P + "\r\n"));
            Assert.AreEqual(400, response.Status, response.ToString());
        }

        // U+017F upper-cases to S, so a case-insensitive compare that is not ASCII-only would take "localhoſt"
        [TestMethod]
        public void Host_NonAsciiLookAlikeIsRefused() {
            Assert.IsFalse(ApiSecurity.EqualsAsciiIgnoreCase("localhoſt:1", "localhost:1"));
            Assert.IsFalse(ApiSecurity.EqualsAsciiIgnoreCase("LOCALHOſT:1", "localhost:1"));
            Assert.IsTrue(ApiSecurity.EqualsAsciiIgnoreCase("LOCALHOST:1", "localhost:1"));
        }

        [TestMethod]
        public void Origin_SelfOriginsAreAccepted() {
            foreach (string origin in new[] { "http://127.0.0.1:" + P, "http://localhost:" + P }) {
                HttpResponseMessage response = Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.Add("Origin", origin));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, origin);
                AssertNoCors(response);
            }
        }

        [TestMethod]
        public void Origin_ForeignOriginsAreRefused() {
            int otherPort = Port == 65535 ? Port - 1 : Port + 1;
            string[] origins = {
                "null", "http://evil.example", "https://evil.example", "chrome-extension://abcdefghijklmnopabcdefghijklmnop",
                "moz-extension://0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5f", "http://127.0.0.1:" + otherPort, "https://127.0.0.1:" + P,
                "http://localhost:" + P + "/", "http://localhost.:" + P, "http://[::1]:" + P, "http://127.0.0.1", ""
            };
            foreach (string origin in origins) {
                HttpResponseMessage response = Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.TryAddWithoutValidation("Origin", origin));
                AssertProblem(response, HttpStatusCode.Forbidden, "forbidden_origin");
            }
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Origin_ForeignOriginCannotAdd() {
            HttpResponseMessage response = PostJson("{\"url\":\"" + ThreadUrl + "\"}", request => request.Headers.Add("Origin", "http://evil.example"));
            AssertProblem(response, HttpStatusCode.Forbidden, "forbidden_origin");
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Origin_IsCheckedBeforeTheToken() {
            Client.DefaultRequestHeaders.Authorization = null;
            HttpResponseMessage response = Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.Add("Origin", "http://evil.example"));
            AssertProblem(response, HttpStatusCode.Forbidden, "forbidden_origin");
        }

        [TestMethod]
        public void FetchSite_CrossSiteWithoutOriginIsRefused() {
            foreach (string site in new[] { "cross-site", "same-site", "CROSS-SITE", "" }) {
                HttpResponseMessage response = Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site));
                AssertProblem(response, HttpStatusCode.Forbidden, "forbidden_origin");
            }
            HttpResponseMessage add = PostJson("{\"url\":\"" + ThreadUrl + "\"}", request => request.Headers.Add("Sec-Fetch-Site", "cross-site"));
            AssertProblem(add, HttpStatusCode.Forbidden, "forbidden_origin");
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void FetchSite_NoneAndSameOriginAreAccepted() {
            foreach (string site in new[] { "none", "same-origin" }) {
                HttpResponseMessage response = Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.Add("Sec-Fetch-Site", site));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, site);
            }
        }

        [TestMethod]
        public void Query_AnyQueryStringIsRefused() {
            foreach (string query in new[] { "?", "?a=1", "?token=" + Token, "?access_token=" + Token, "?url=" + Uri.EscapeDataString(ThreadUrl) }) {
                AssertProblem(Get(ThreadsEndpoints.ThreadsPath + query), HttpStatusCode.BadRequest, "query_not_allowed");
                AssertProblem(Get(ThreadsEndpoints.OpenApiPath + query), HttpStatusCode.BadRequest, "query_not_allowed");
            }
            AssertProblem(PostJson("{\"url\":\"" + ThreadUrl + "\"}", request => request.RequestUri = new Uri(ThreadsEndpoints.ThreadsPath + "?x=1", UriKind.Relative)), HttpStatusCode.BadRequest, "query_not_allowed");
            Assert.AreEqual(0, ThreadCount());
        }

        // The token in the query string never authenticates, even with no header
        [TestMethod]
        public void Query_TokenInQueryDoesNotAuthenticate() {
            Client.DefaultRequestHeaders.Authorization = null;
            AssertProblem(Get(ThreadsEndpoints.ThreadsPath + "?token=" + Token), HttpStatusCode.BadRequest, "query_not_allowed");
            AssertProblem(Get(ThreadsEndpoints.ThreadsPath), HttpStatusCode.Unauthorized, "unauthorized");
        }

        [TestMethod]
        public void Routing_UnknownPathIs404AfterTheToken() {
            AssertProblem(Get("/api/v1/thread"), HttpStatusCode.NotFound, "not_found");
            AssertProblem(Get("/"), HttpStatusCode.NotFound, "not_found");
            AssertProblem(Get("/api/v2/threads"), HttpStatusCode.NotFound, "not_found");
            Client.DefaultRequestHeaders.Authorization = null;
            AssertProblem(Get("/api/v1/thread"), HttpStatusCode.Unauthorized, "unauthorized");
        }

        [TestMethod]
        public void Methods_OtherMethodsAre405WithoutCors() {
            foreach (HttpMethod method in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch, new HttpMethod("TRACE"), new HttpMethod("PROPFIND") }) {
                HttpResponseMessage response = Send(method, ThreadsEndpoints.ThreadsPath, "{\"url\":\"" + ThreadUrl + "\"}");
                AssertProblem(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
            }
            AssertProblem(Send(HttpMethod.Post, ThreadsEndpoints.OpenApiPath, "{}"), HttpStatusCode.MethodNotAllowed, "method_not_allowed");
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Methods_HeadIs405() {
            HttpResponseMessage response = Send(HttpMethod.Head, ThreadsEndpoints.ThreadsPath);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            AssertCommonHeaders(response);
        }

        // A CORS preflight gets no CORS headers, from this origin or another
        [TestMethod]
        public void Methods_OptionsPreflightIs405WithoutCors() {
            HttpResponseMessage self = Send(HttpMethod.Options, ThreadsEndpoints.ThreadsPath, null, request => {
                request.Headers.Add("Origin", "http://127.0.0.1:" + P);
                request.Headers.Add("Access-Control-Request-Method", "POST");
                request.Headers.Add("Access-Control-Request-Headers", "authorization, content-type");
            });
            AssertProblem(self, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
            HttpResponseMessage foreign = Send(HttpMethod.Options, ThreadsEndpoints.ThreadsPath, null, request => {
                request.Headers.Add("Origin", "http://evil.example");
                request.Headers.Add("Access-Control-Request-Method", "POST");
            });
            AssertProblem(foreign, HttpStatusCode.Forbidden, "forbidden_origin");
        }

        // A browser's preflight never carries the token, so it ends at the token check, still without CORS headers
        [TestMethod]
        public void Methods_OptionsPreflightWithoutTokenIs401WithoutCors() {
            Client.DefaultRequestHeaders.Authorization = null;
            HttpResponseMessage response = Send(HttpMethod.Options, ThreadsEndpoints.ThreadsPath, null, request => {
                request.Headers.Add("Origin", "http://127.0.0.1:" + P);
                request.Headers.Add("Access-Control-Request-Method", "POST");
                request.Headers.Add("Access-Control-Request-Headers", "authorization, content-type");
            });
            AssertProblem(response, HttpStatusCode.Unauthorized, "unauthorized");
        }

        // A request refused by the security checks gets its connection closed by the server, so such clients cannot
        // keep the 16 connections open and lock out the real client
        [TestMethod]
        public void Rejections_CloseTheConnection() {
            List<TcpClient> clients = new List<TcpClient>();
            try {
                for (int i = 0; i < 16; i++) {
                    TcpClient client = new TcpClient();
                    clients.Add(client);
                    client.Connect(IPAddress.Loopback, Port);
                    client.ReceiveTimeout = 10000;
                    byte[] request = Encoding.ASCII.GetBytes("GET " + ThreadsEndpoints.ThreadsPath + " HTTP/1.1\r\nHost: evil.example:" + P + "\r\n\r\n");
                    client.GetStream().Write(request, 0, request.Length);
                }
                foreach (TcpClient client in clients) {
                    string response = ReadUntilClosed(client.GetStream());
                    StringAssert.StartsWith(response, "HTTP/1.1 400");
                    StringAssert.Contains(response, "Connection: close");
                }
                Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode);
            }
            finally {
                foreach (TcpClient client in clients) client.Dispose();
            }
        }

        // Fails (IOException from the receive timeout) if the server keeps the connection open
        private static string ReadUntilClosed(NetworkStream stream) {
            using (MemoryStream received = new MemoryStream()) {
                stream.CopyTo(received);
                return Encoding.ASCII.GetString(received.ToArray());
            }
        }

        [TestMethod]
        public void Methods_MethodOverrideHeadersAreIgnored() {
            foreach (string header in new[] { "X-HTTP-Method-Override", "X-HTTP-Method", "X-Method-Override" }) {
                HttpResponseMessage get = Send(HttpMethod.Get, ThreadsEndpoints.ThreadsPath, "{\"url\":\"" + ThreadUrl + "\"}", request => request.Headers.Add(header, "POST"));
                Assert.AreEqual(HttpStatusCode.OK, get.StatusCode, header);
                AssertProblem(Send(HttpMethod.Post, ThreadsEndpoints.OpenApiPath, "{}", request => request.Headers.Add(header, "GET")), HttpStatusCode.MethodNotAllowed, "method_not_allowed");
                AssertProblem(Send(HttpMethod.Put, ThreadsEndpoints.ThreadsPath, "{\"url\":\"" + ThreadUrl + "\"}", request => request.Headers.Add(header, "POST")), HttpStatusCode.MethodNotAllowed, "method_not_allowed");
            }
            Assert.AreEqual(0, ThreadCount());
        }

        // GET never changes anything: no thread, nothing marked for saving, files unchanged
        [TestMethod]
        public void Get_HasNoSideEffects() {
            // The hashes can only change if the API writes these files; SourceTests.Library_NeverWritesTheThreadListOrSettings
            // checks that it never references their writers
            WriteSettingsFiles();
            string threadsHash = FileHash(ThreadListPath);
            string settingsHash = FileHash(SettingsPath);
            HttpResponseMessage response = Send(HttpMethod.Get, ThreadsEndpoints.ThreadsPath, "{\"url\":\"" + ThreadUrl + "\"}");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.OpenApiPath).StatusCode);
            Assert.AreEqual(0, ThreadCount());
            Assert.IsFalse(Owner.Invoke(() => Session.SaveThreadListPending));
            Assert.AreEqual(threadsHash, FileHash(ThreadListPath));
            Assert.AreEqual(settingsHash, FileHash(SettingsPath));
        }

        [TestMethod]
        public void Responses_HaveNoStoreNosniffAndNoServerHeader() {
            AssertCommonHeaders(Get(ThreadsEndpoints.ThreadsPath));
            AssertCommonHeaders(Get(ThreadsEndpoints.OpenApiPath));
            AssertCommonHeaders(AddThread(ThreadUrl));
            Client.DefaultRequestHeaders.Authorization = null;
            AssertCommonHeaders(Get(ThreadsEndpoints.ThreadsPath));
        }
    }
}
