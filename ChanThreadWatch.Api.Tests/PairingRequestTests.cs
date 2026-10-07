using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Api.Tests {
    // The pairing routes' place in ApiSecurity (MP-7b design E5): the Origin forms, which requests skip the token, the
    // body rules, and the binding of each token to its Origin after the token check
    [TestClass]
    public class PairingRequestTests : PairingTestBase {
        private string P {
            get { return Port.ToString(CultureInfo.InvariantCulture); }
        }

        // A web page, this server's own page, a script, another Chrome extension and malformed extension Origins
        // cannot pair, and nothing about the code changes: it still answers its three hellos
        [TestMethod]
        public void Origins_OnlyExtensionOriginsMayPair() {
            NewCode();
            string[] origins = {
                "https://example.com", "http://example.com", "null", "", "http://127.0.0.1:" + P, "http://localhost:" + P, OtherChromeOrigin,
                "chrome-extension://" + ApiPairing.ChromeExtensionId.ToUpperInvariant(), ChromeOrigin + "/", "Chrome-Extension://" + ApiPairing.ChromeExtensionId,
                "chrome-extension://" + ApiPairing.ChromeExtensionId.Substring(1), "chrome-extension://" + ApiPairing.ChromeExtensionId + "q",
                FirefoxOrigin.ToUpperInvariant(), "moz-extension://" + FirefoxOrigin.Substring(16).ToUpperInvariant(), FirefoxOrigin + "/", "moz-extension://{0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5f}",
                "moz-extension://0d4f2a8c5b1e4c7a9f3d2e6b8a1c4d5f", "moz-extension://0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5g", "moz-extension://0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5"
            };
            foreach (string origin in origins) {
                AssertProblem(PostHello(origin), HttpStatusCode.Forbidden, "forbidden_origin");
            }
            AssertProblem(PostHello(null), HttpStatusCode.Forbidden, "forbidden_origin");
            for (int i = 0; i < ApiPolicy.MaxHellosPerCode; i++) {
                Assert.AreEqual(HttpStatusCode.OK, PostHello(i == 0 ? FirefoxOrigin : ChromeOrigin).StatusCode, "hello " + i);
            }
        }

        [TestMethod]
        public void Origins_TwoOriginHeadersAreRefused() {
            NewCode();
            string body = JsonSerializer.Serialize(new { step = "hello", clientNonce = ApiPairing.NewNonce() });
            RawResponse response = SendRaw(RawPost(PairingEndpoints.PairingPath, "Origin: " + ChromeOrigin + "\r\nOrigin: " + ChromeOrigin + "\r\n", body));
            Assert.AreEqual(403, response.Status, response.ToString());
            StringAssert.Contains(response.Text, "\"code\":\"forbidden_origin\"");
            StringAssert.Contains(response.Head, "Connection: close");
        }

        // Only POST on exactly these paths skips the token; any other method, case, encoding or trailing slash gets
        // 401 first
        [TestMethod]
        public void Scope_OnlyExactPostSkipsTheToken() {
            foreach (string path in new[] { PairingEndpoints.PairingPath, PairingEndpoints.ProofPath }) {
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, path)) {
                    AssertProblem(Anonymous.SendAsync(request).GetAwaiter().GetResult(), HttpStatusCode.Unauthorized, "unauthorized");
                }
                foreach (string variant in new[] { path.ToUpperInvariant(), path + "/", path.Replace("/api", "/API", StringComparison.Ordinal), "/api/v1/" + path.Substring(8).ToUpperInvariant() }) {
                    AssertProblem(PostAnonymous(variant, "{}", ChromeOrigin), HttpStatusCode.Unauthorized, "unauthorized");
                }
                foreach (HttpMethod method in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch, HttpMethod.Options }) {
                    using (HttpRequestMessage request = new HttpRequestMessage(method, path)) {
                        AssertProblem(Anonymous.SendAsync(request).GetAwaiter().GetResult(), HttpStatusCode.Unauthorized, "unauthorized");
                    }
                }
                string encoded = path.Substring(0, path.Length - 1) + "%" + ((int)path[path.Length - 1]).ToString("x2", CultureInfo.InvariantCulture);
                foreach (string raw in new[] { RawPost(encoded, "Origin: " + ChromeOrigin + "\r\n"), RawPost("/" + path, "Origin: " + ChromeOrigin + "\r\n"), RawPost(path, "Origin: " + ChromeOrigin + "\r\n", "{}", "post") }) {
                    RawResponse response = SendRaw(raw);
                    Assert.AreEqual(401, response.Status, raw + " => " + response);
                }
            }
        }

        [TestMethod]
        public void Scope_QueryStringIsRefused() {
            NewCode();
            foreach (string path in new[] { PairingEndpoints.PairingPath, PairingEndpoints.ProofPath }) {
                AssertProblem(PostAnonymous(path + "?x=1", JsonSerializer.Serialize(new { step = "hello", clientNonce = ApiPairing.NewNonce() }), ChromeOrigin), HttpStatusCode.BadRequest, "query_not_allowed");
                AssertProblem(PostAnonymous(path + "?", "{}", ChromeOrigin), HttpStatusCode.BadRequest, "query_not_allowed");
            }
        }

        // A path that routing also matches (another case) reaches the handler only with a token; the handler still
        // needs an extension Origin, so the scripts' token cannot pair without one
        [TestMethod]
        public void Scope_OtherCaseWithTheScriptTokenStillNeedsAnExtensionOrigin() {
            NewCode();
            string body = JsonSerializer.Serialize(new { step = "hello", clientNonce = ApiPairing.NewNonce() });
            AssertProblem(Send(HttpMethod.Post, PairingEndpoints.PairingPath.ToUpperInvariant(), body), HttpStatusCode.Forbidden, "forbidden_origin");
            AssertProblem(Send(HttpMethod.Get, PairingEndpoints.PairingPath), HttpStatusCode.MethodNotAllowed, "method_not_allowed");
            Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode, "the code was not used");
        }

        // Size, content type and JSON shape: refused before any state changes, so a malformed request never burns the
        // code or uses up a hello
        [TestMethod]
        public void Body_PairingRules() {
            ApiPairingCode code = NewCode();
            string nonce = ApiPairing.NewNonce();
            AssertDeclaredOversizeRefused(PairingEndpoints.PairingPath, ChromeOrigin);
            AssertProblem(PostAnonymous(PairingEndpoints.PairingPath, "{\"step\":\"hello\",\"clientNonce\":\"" + nonce + "\"}", ChromeOrigin, "text/plain"), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
            string id = PairingFile.Read(out _).Id;
            string[] bodies = {
                "{\"step\":\"hello\",\"clientNonce\":\"" + nonce + "\",\"extra\":1}", "{\"step\":\"hello\",\"clientNonce\":\"" + nonce + "\",\"pairingId\":\"" + id + "\"}",
                "{\"step\":\"hello\",\"clientNonce\":\"" + nonce + "\",\"proof\":\"" + nonce + "\"}", "{\"step\":\"HELLO\",\"clientNonce\":\"" + nonce + "\"}",
                "{\"Step\":\"hello\",\"clientNonce\":\"" + nonce + "\"}", "{\"clientNonce\":\"" + nonce + "\"}", "{\"step\":\"hello\"}", "{\"step\":\"hello\",\"clientNonce\":1}",
                "{\"step\":\"hello\",\"clientNonce\":\"" + nonce + "\",\"clientNonce\":\"" + nonce + "\"}", "null", "[]", "", "{\"step\":\"hello\",\"clientNonce\":\"" + nonce + "\"} {}",
                "{\"step\":\"finish\",\"pairingId\":\"" + id + "\",\"clientNonce\":\"" + nonce + "\",\"serverNonce\":\"" + nonce + "\"}",
                "{\"step\":\"finish\",\"pairingId\":\"" + id + "x\",\"clientNonce\":\"" + nonce + "\",\"serverNonce\":\"" + nonce + "\",\"proof\":\"" + nonce + "\"}",
                "{\"step\":\"finish\",\"pairingId\":\"" + id + "\",\"clientNonce\":\"" + nonce + "\",\"serverNonce\":\"" + nonce + "\",\"proof\":\"" + nonce.Substring(1) + "\"}",
                "{\"step\":\"other\",\"clientNonce\":\"" + nonce + "\"}", "{\"step\":\"hello\",\"clientNonce\":\"" + nonce + "\",\"pairingId\":null}",
                "{\"step\":\"hello\",\"clientNonce\":\"" + nonce + "\",\"serverNonce\":null}", "{\"step\":\"hello\",\"clientNonce\":\"" + nonce + "\",\"proof\":null}",
                "{\"step\":\"hello\",\"clientNonce\":null}", "{\"step\":null,\"clientNonce\":\"" + nonce + "\"}"
            };
            foreach (string body in bodies.Concat(BadNonces(nonce).Select(bad => "{\"step\":\"hello\",\"clientNonce\":\"" + bad + "\"}"))) {
                AssertProblem(PostAnonymous(PairingEndpoints.PairingPath, body, ChromeOrigin), HttpStatusCode.BadRequest, "invalid_body");
            }
            Assert.IsTrue(File.Exists(PairingFile.Path), "not burned");
            for (int i = 0; i < ApiPolicy.MaxHellosPerCode; i++) {
                Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode, "hello " + i);
            }
            Assert.AreEqual(code.Id, id);
        }

        // A chunked body over the limit (no Content-Length) is read up to the limit and refused: a 413, or, since the
        // refusal closes the connection while the client may still be sending, a reset; both are allowed
        [TestMethod]
        public void Body_OversizeChunkedBodyIsRefused() {
            NewCode();
            string chunk = new string(' ', 5000);
            foreach (string path in new[] { PairingEndpoints.PairingPath, PairingEndpoints.ProofPath }) {
                string request = "POST " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + P + "\r\nOrigin: " + ChromeOrigin + "\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n" +
                    chunk.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n" + chunk + "\r\n0\r\n\r\n";
                try {
                    RawResponse response = SendRaw(request);
                    Assert.AreEqual(413, response.Status, response.ToString());
                    StringAssert.Contains(response.Text, "\"code\":\"body_too_large\"");
                }
                catch (IOException) {
                }
                catch (System.Net.Sockets.SocketException) {
                }
            }
            Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode, "the code was not used");
        }

        // A malformed chunk on a pairing route is the pairing body error (its own title), not the add's
        [TestMethod]
        public void Body_MalformedChunkGetsThePairingTitle() {
            NewCode();
            foreach (string path in new[] { PairingEndpoints.PairingPath, PairingEndpoints.ProofPath }) {
                RawResponse response = SendRaw("POST " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + P + "\r\nOrigin: " + ChromeOrigin + "\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\nZZ\r\n{}\r\n0\r\n\r\n");
                Assert.AreEqual(400, response.Status, response.ToString());
                StringAssert.Contains(response.Text, "\"code\":\"invalid_body\"");
                StringAssert.Contains(response.Text, ApiError.InvalidPairingBody.Title);
            }
            Assert.IsTrue(File.Exists(PairingFile.Path), "not burned");
        }

        // Not 43 base64url characters in the one form that decodes to 32 bytes
        private static IEnumerable<string> BadNonces(string nonce) {
            return new[] {
                nonce.Substring(1), nonce + "A", nonce.Substring(0, 42) + "B", nonce + "=", nonce.Substring(0, 42) + "+", nonce.Substring(0, 42) + "/", nonce.Substring(0, 42) + " ",
                nonce.Substring(0, 42) + "é", ""
            };
        }

        [TestMethod]
        public void Body_ProofRules() {
            Pair(ChromeOrigin);
            string nonce = ApiPairing.NewNonce();
            AssertDeclaredOversizeRefused(PairingEndpoints.ProofPath, ChromeOrigin);
            AssertProblem(PostAnonymous(PairingEndpoints.ProofPath, "{\"clientNonce\":\"" + nonce + "\"}", ChromeOrigin, "text/plain"), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
            string[] bodies = { "{\"clientNonce\":\"" + nonce + "\",\"origin\":\"x\"}", "{}", "null", "{\"clientnonce\":\"" + nonce + "\"}" };
            foreach (string body in bodies.Concat(BadNonces(nonce).Select(bad => "{\"clientNonce\":\"" + bad + "\"}"))) {
                AssertProblem(PostAnonymous(PairingEndpoints.ProofPath, body, ChromeOrigin), HttpStatusCode.BadRequest, "invalid_body");
            }
        }

        // A paired browser's token passes only with its recorded Origin: not with the other family's, another
        // extension's, this server's own Origin, or none (so an extension token cannot list threads from a GET)
        [TestMethod]
        public void Binding_ExtensionTokenNeedsItsOwnOrigin() {
            string chrome = Pair(ChromeOrigin);
            string firefox = Pair(FirefoxOrigin);
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(chrome, ChromeOrigin).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, SendWithToken(HttpMethod.Get, ThreadsEndpoints.ThreadsPath, chrome, ChromeOrigin).StatusCode);
            foreach (string origin in new[] { FirefoxOrigin, OtherChromeOrigin, "http://127.0.0.1:" + P, "http://localhost:" + P, null }) {
                AssertProblem(AddWithToken(chrome, origin, "https://boards.4chan.org/wg/thread/102"), HttpStatusCode.Forbidden, "forbidden_origin");
                AssertProblem(SendWithToken(HttpMethod.Get, ThreadsEndpoints.ThreadsPath, chrome, origin), HttpStatusCode.Forbidden, "forbidden_origin");
                AssertProblem(SendWithToken(HttpMethod.Get, ThreadsEndpoints.OpenApiPath, chrome, origin), HttpStatusCode.Forbidden, "forbidden_origin");
            }
            foreach (string origin in new[] { ChromeOrigin, OtherFirefoxOrigin, null }) {
                AssertProblem(AddWithToken(firefox, origin, "https://boards.4chan.org/wg/thread/102"), HttpStatusCode.Forbidden, "forbidden_origin");
            }
            Assert.AreEqual(1, ThreadCount());
        }

        // The scripts' token never passes with an extension Origin, so a browser extension cannot use a script's token;
        // without an Origin or with this server's own it works as before
        [TestMethod]
        public void Binding_ScriptTokenIsRefusedWithAnExtensionOrigin() {
            Pair(ChromeOrigin);
            foreach (string origin in new[] { ChromeOrigin, OtherChromeOrigin, FirefoxOrigin }) {
                AssertProblem(AddWithToken(Token, origin), HttpStatusCode.Forbidden, "forbidden_origin");
                AssertProblem(SendWithToken(HttpMethod.Get, ThreadsEndpoints.ThreadsPath, Token, origin), HttpStatusCode.Forbidden, "forbidden_origin");
            }
            Assert.AreEqual(0, ThreadCount());
            Assert.AreEqual(HttpStatusCode.OK, SendWithToken(HttpMethod.Get, ThreadsEndpoints.ThreadsPath, Token, "http://127.0.0.1:" + P).StatusCode);
            Assert.AreEqual(HttpStatusCode.Created, AddWithToken(Token, null).StatusCode);
        }

        // An Origin that only looks like an extension's is refused at the Origin step, whatever the token
        [TestMethod]
        public void Origins_MalformedExtensionOriginsAreRefusedWithAnyToken() {
            string chrome = Pair(ChromeOrigin);
            string[] origins = { "chrome-extension://x", ChromeOrigin.ToUpperInvariant(), ChromeOrigin + "/", FirefoxOrigin.ToUpperInvariant(), "moz-extension://x", "safari-web-extension://" + FirefoxOrigin.Substring(16) };
            foreach (string origin in origins) {
                AssertProblem(SendWithToken(HttpMethod.Get, ThreadsEndpoints.ThreadsPath, Token, origin), HttpStatusCode.Forbidden, "forbidden_origin");
                AssertProblem(AddWithToken(chrome, origin), HttpStatusCode.Forbidden, "forbidden_origin");
                AssertProblem(SendWithToken(HttpMethod.Get, ThreadsEndpoints.ThreadsPath, "wrong", origin), HttpStatusCode.Forbidden, "forbidden_origin");
            }
        }

        // A wrong token with an extension Origin is a token failure (401), checked before the binding
        [TestMethod]
        public void Binding_IsCheckedAfterTheToken() {
            Pair(ChromeOrigin);
            AssertProblem(AddWithToken("ctwe_" + new string('A', 43), ChromeOrigin), HttpStatusCode.Unauthorized, "unauthorized");
            AssertProblem(PostAnonymous(ThreadsEndpoints.ThreadsPath, JsonSerializer.Serialize(new { url = ThreadUrl }), ChromeOrigin), HttpStatusCode.Unauthorized, "unauthorized");
        }
    }

    // The two pairing operations of openapi.json against the live server
    [TestClass]
    public class PairingContractTests : PairingTestBase {
        private static JsonElement Document {
            get { return JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "openapi.json"))).RootElement; }
        }

        private static void AssertValid(JsonElement value, string schemaReference) {
            JsonElement schema;
            Assert.IsTrue(SchemaValidator.TryResolve(Document, schemaReference, out schema), schemaReference);
            List<string> errors = SchemaValidator.Validate(Document, value, schema);
            Assert.AreEqual(0, errors.Count, String.Join("; ", errors) + " in " + value.GetRawText());
        }

        [TestMethod]
        public void Contract_PairingOperationsTakeNoTokenAndMatchTheServer() {
            JsonElement paths = Document.GetProperty("paths");
            foreach (string path in new[] { PairingEndpoints.PairingPath, PairingEndpoints.ProofPath }) {
                Assert.AreEqual(0, paths.GetProperty(path).GetProperty("post").GetProperty("security").GetArrayLength(), path);
            }
            JsonElement pairing = paths.GetProperty(PairingEndpoints.PairingPath).GetProperty("post");
            JsonElement helloExample = pairing.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("examples").GetProperty("hello").GetProperty("value");
            AssertValid(helloExample, "#/components/schemas/PairingRequest");
            JsonElement unavailable = pairing.GetProperty("responses").GetProperty("409").GetProperty("content").GetProperty("application/problem+json").GetProperty("examples").GetProperty("pairingUnavailable").GetProperty("value");
            Assert.AreEqual(unavailable.GetRawText().Replace(" ", ""), Body(PostAnonymous(PairingEndpoints.PairingPath, helloExample.GetRawText(), ChromeOrigin)).Replace(" ", ""));

            ApiPairingCode code = NewCode();
            HttpResponseMessage hello = PostAnonymous(PairingEndpoints.PairingPath, helloExample.GetRawText(), ChromeOrigin);
            Assert.AreEqual(HttpStatusCode.OK, hello.StatusCode, Body(hello));
            Assert.AreEqual("application/json", hello.Content.Headers.ContentType?.MediaType);
            AssertValid(Json(hello), "#/components/schemas/PairingHelloResponse");
            AssertValid(Json(hello), "#/components/schemas/PairingResponse");
            AssertCommonHeaders(hello);

            TestHello test = SendHello(code.Code, FirefoxOrigin);
            HttpResponseMessage finish = SendFinish(test);
            AssertValid(Json(finish), "#/components/schemas/PairingFinishResponse");
            AssertValid(Json(finish), "#/components/schemas/PairingResponse");
            AssertCommonHeaders(finish);

            JsonElement proofExample = paths.GetProperty(PairingEndpoints.ProofPath).GetProperty("post").GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("examples").GetProperty("proof").GetProperty("value");
            HttpResponseMessage proof = PostAnonymous(PairingEndpoints.ProofPath, proofExample.GetRawText(), FirefoxOrigin);
            Assert.AreEqual(HttpStatusCode.OK, proof.StatusCode, Body(proof));
            AssertValid(Json(proof), "#/components/schemas/ProofResponse");
        }

        // Every refusal of the two routes is a Problem and closes the connection
        [TestMethod]
        public void Contract_PairingRefusalsAreProblemsAndCloseTheConnection() {
            List<HttpResponseMessage> responses = new List<HttpResponseMessage> {
                PostHello(ChromeOrigin), PostHello(OtherChromeOrigin), PostAnonymous(PairingEndpoints.PairingPath, "[]", ChromeOrigin), PostProof(ChromeOrigin, ApiPairing.NewNonce()),
                PostProof(ChromeOrigin, "x")
            };
            NewCode();
            for (int i = 0; i <= ApiPolicy.MaxHellosPerCode; i++) responses.Add(PostHello(FirefoxOrigin));
            foreach (HttpResponseMessage response in responses.Where(response => response.StatusCode != HttpStatusCode.OK)) {
                Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                AssertValid(Json(response), "#/components/schemas/Problem");
                Assert.IsTrue(response.Headers.ConnectionClose == true, Body(response));
            }
            Assert.AreEqual(5 + 1, responses.Count(response => response.StatusCode != HttpStatusCode.OK));
        }
    }

    // The two routes' own rate limits, apart from the authenticated window both ways
    [TestClass]
    public class PairingLimitTests : PairingTestBase {
        internal override void ConfigurePolicy(ApiPolicy policy) {
            base.ConfigurePolicy(policy);
            policy.PairingRequestsPerMinute = ApiPolicy.DefaultPairingRequestsPerMinute;
            policy.ProofRequestsPerMinute = ApiPolicy.DefaultProofRequestsPerMinute;
            policy.RequestsPerMinute = 3;
            policy.RateWindow = TimeSpan.FromHours(1);
        }

        [TestMethod]
        public void Limits_PairingRouteHasItsOwnWindow() {
            for (int i = 0; i < 10; i++) {
                AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
            }
            HttpResponseMessage limited = PostHello(ChromeOrigin);
            AssertProblem(limited, HttpStatusCode.TooManyRequests, "pairing_rate_limited");
            Assert.IsTrue(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero, "Retry-After");
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode, "the authenticated window is not used");
            Assert.AreEqual(HttpStatusCode.OK, PostProof(null, ApiPairing.NewNonce()).StatusCode, "the proof window is not used");
        }

        [TestMethod]
        public void Limits_ProofRouteHasItsOwnWindow() {
            for (int i = 0; i < 60; i++) {
                Assert.AreEqual(HttpStatusCode.OK, PostProof(null, ApiPairing.NewNonce()).StatusCode, "proof " + i);
            }
            AssertProblem(PostProof(null, ApiPairing.NewNonce()), HttpStatusCode.TooManyRequests, "pairing_rate_limited");
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode);
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
        }

        [TestMethod]
        public void Limits_AuthenticatedWindowDoesNotStopPairing() {
            for (int i = 0; i < 3; i++) {
                Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode);
            }
            AssertProblem(Get(ThreadsEndpoints.ThreadsPath), HttpStatusCode.TooManyRequests, "rate_limited");
            NewCode();
            Assert.AreEqual(HttpStatusCode.OK, PostHello(ChromeOrigin).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, PostProof(null, ApiPairing.NewNonce()).StatusCode);
        }

        // The content type and declared length come before the route's rate, so a bare text/plain POST (or an oversized
        // one) cannot use up the proof or pairing window
        [TestMethod]
        public void Limits_RefusedBodiesDoNotUseTheWindows() {
            for (int i = 0; i < 70; i++) {
                AssertProblem(PostAnonymous(PairingEndpoints.ProofPath, "{}", null, "text/plain"), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
                if (i < 15) AssertProblem(PostAnonymous(PairingEndpoints.PairingPath, "{}", ChromeOrigin, "text/plain"), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
            }
            for (int i = 0; i < 15; i++) {
                AssertDeclaredOversizeRefused(PairingEndpoints.ProofPath, null);
            }
            Assert.AreEqual(HttpStatusCode.OK, PostProof(null, ApiPairing.NewNonce()).StatusCode);
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
        }

        // The route's Origin rule comes before its rate, so another Chrome extension or a page cannot use up the
        // pairing window
        [TestMethod]
        public void Limits_RefusedOriginsDoNotUseThePairingWindow() {
            for (int i = 0; i < 20; i++) {
                AssertProblem(PostHello(OtherChromeOrigin), HttpStatusCode.Forbidden, "forbidden_origin");
                AssertProblem(PostHello(null), HttpStatusCode.Forbidden, "forbidden_origin");
            }
            AssertProblem(PostHello(ChromeOrigin), HttpStatusCode.Conflict, "pairing_unavailable");
        }
    }
}
