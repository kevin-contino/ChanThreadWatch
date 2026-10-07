using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Api.Tests {
    // The hand-written OpenAPI document (D14) against the live server
    [TestClass]
    public class ContractTests : ApiTestBase {
        private static JsonElement Document {
            get { return JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "openapi.json"))).RootElement; }
        }

        [TestMethod]
        public void Contract_DocumentedRoutesAreTheLiveRoutes() {
            HashSet<string> documented = new HashSet<string>();
            foreach (JsonProperty path in Document.GetProperty("paths").EnumerateObject()) {
                foreach (JsonProperty operation in path.Value.EnumerateObject()) {
                    documented.Add(operation.Name.ToUpperInvariant() + " " + path.Name);
                }
            }
            HashSet<string> live = new HashSet<string>();
            foreach (RouteEndpoint endpoint in Server.GetRouteEndpoints()) {
                IHttpMethodMetadata methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>();
                Assert.IsNotNull(methods, endpoint.DisplayName + " has no method");
                foreach (string method in methods.HttpMethods) {
                    live.Add(method + " " + endpoint.RoutePattern.RawText);
                }
            }
            CollectionAssert.AreEquivalent(new[] { "GET /api/v1/threads", "POST /api/v1/threads", "GET /api/v1/openapi.json", "POST /api/v1/pairing", "POST /api/v1/proof" }, live.ToArray());
            Assert.IsTrue(documented.SetEquals(live), "documented: " + String.Join(", ", documented) + "; live: " + String.Join(", ", live));
        }

        [TestMethod]
        public void Contract_ServedDocumentIsTheFileByteForByte() {
            HttpResponseMessage response = Get(ThreadsEndpoints.OpenApiPath);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
            byte[] served = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "openapi.json")), served);
            CollectionAssert.AreEqual(ThreadsEndpoints.ReadOpenApiDocument(), served);
            AssertCommonHeaders(response);
        }

        [TestMethod]
        public void Contract_DocumentIsOpenApi31WithResolvableReferences() {
            JsonElement document = Document;
            Assert.AreEqual("3.1.0", document.GetProperty("openapi").GetString());
            foreach (string reference in References(document)) {
                Assert.IsTrue(SchemaValidator.TryResolve(document, reference, out _), reference);
            }
            Assert.AreEqual("bearer", document.GetProperty("components").GetProperty("securitySchemes").GetProperty("bearer").GetProperty("scheme").GetString());
        }

        // Each operation documents exactly the statuses the server can send for it: the request checks (400 Host or
        // query, 403, 401, 429), the operation's own results, and 500 from a failure in the handler. 404 and 405 come
        // only for undocumented paths and methods, so they are shared responses (NotFound, MethodNotAllowed with Allow).
        [TestMethod]
        public void Contract_EachOperationDocumentsTheStatusesItCanSend() {
            Dictionary<string, string[]> expected = new Dictionary<string, string[]> {
                ["GET /api/v1/threads"] = new[] { "200", "400", "401", "403", "429", "500", "503" },
                ["POST /api/v1/threads"] = new[] { "201", "400", "401", "403", "408", "409", "413", "415", "422", "429", "500", "503", "507" },
                ["GET /api/v1/openapi.json"] = new[] { "200", "400", "401", "403", "429", "500" },
                // The two routes without a token (MP-7b): no 401, and no 503 (they never wait for the owner thread)
                ["POST /api/v1/pairing"] = new[] { "200", "400", "403", "408", "409", "413", "415", "429", "500" },
                ["POST /api/v1/proof"] = new[] { "200", "400", "403", "408", "413", "415", "429", "500" }
            };
            foreach (JsonProperty path in Document.GetProperty("paths").EnumerateObject()) {
                foreach (JsonProperty operation in path.Value.EnumerateObject()) {
                    string key = operation.Name.ToUpperInvariant() + " " + path.Name;
                    string[] documented = operation.Value.GetProperty("responses").EnumerateObject().Select(response => response.Name).ToArray();
                    CollectionAssert.AreEquivalent(expected[key], documented, key);
                }
            }
            JsonElement responses = Document.GetProperty("components").GetProperty("responses");
            Assert.IsTrue(responses.TryGetProperty("NotFound", out _));
            Assert.IsTrue(responses.GetProperty("MethodNotAllowed").GetProperty("headers").TryGetProperty("Allow", out _));
            HttpResponseMessage notAllowed = Send(HttpMethod.Delete, ThreadsEndpoints.ThreadsPath);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, notAllowed.StatusCode);
            CollectionAssert.AreEquivalent(new[] { "GET", "POST" }, notAllowed.Content.Headers.Allow.ToArray());
        }

        // Every code the server can send is documented, and nothing else is (InvalidPairingBody shares invalid_body)
        [TestMethod]
        public void Contract_ProblemCodesMatchTheServer() {
            string[] documented = SchemaAt("#/components/schemas/Problem").GetProperty("properties").GetProperty("code").GetProperty("enum").EnumerateArray().Select(code => code.GetString()).ToArray();
            string[] sent = typeof(ApiError).GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.FieldType == typeof(ApiError)).Select(field => ((ApiError)field.GetValue(null)).Code).Distinct().ToArray();
            CollectionAssert.AreEquivalent(documented, sent);
        }

        [TestMethod]
        public void Contract_AddAndListExamplesAreReproduced() {
            JsonElement post = Document.GetProperty("paths").GetProperty("/api/v1/threads").GetProperty("post");
            JsonElement requestExample = post.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("examples").GetProperty("add").GetProperty("value");
            HttpResponseMessage added = PostJson(requestExample.GetRawText());
            Assert.AreEqual(HttpStatusCode.Created, added.StatusCode, Body(added));
            JsonElement addedExample = ExampleOf(post, "201", "application/json", "added");
            AssertMatchesExample(Json(added), addedExample, "#/components/schemas/Thread");

            JsonElement get = Document.GetProperty("paths").GetProperty("/api/v1/threads").GetProperty("get");
            JsonElement listExample = ExampleOf(get, "200", "application/json", "oneThread");
            JsonElement list = Json(Get(ThreadsEndpoints.ThreadsPath));
            AssertValid(list, "#/components/schemas/ThreadList");
            AssertMatchesExample(list.GetProperty("threads")[0], listExample.GetProperty("threads")[0], "#/components/schemas/Thread");

            JsonElement conflictExample = ExampleOf(post, "409", "application/problem+json", "alreadyWatched");
            AssertSameJson(conflictExample, Json(PostJson(requestExample.GetRawText())));
        }

        [TestMethod]
        public void Contract_ErrorExamplesAreReproduced() {
            JsonElement post = Document.GetProperty("paths").GetProperty("/api/v1/threads").GetProperty("post");
            AssertSameJson(ExampleOf(post, "422", "application/problem+json", "unknownHost"), Json(AddThread("https://example.com/thread/1")));
            JsonElement unauthorized = SchemaAt("#/components/responses/Unauthorized").GetProperty("content").GetProperty("application/problem+json").GetProperty("examples").GetProperty("unauthorized").GetProperty("value");
            Client.DefaultRequestHeaders.Authorization = null;
            AssertSameJson(unauthorized, Json(Get(ThreadsEndpoints.ThreadsPath)));
        }

        // Each kind of error the tests see is a valid Problem
        [TestMethod]
        public void Contract_ProblemResponsesMatchTheSchema() {
            List<HttpResponseMessage> responses = new List<HttpResponseMessage> {
                Get(ThreadsEndpoints.ThreadsPath + "?x"), Get("/nothing"), Send(HttpMethod.Put, ThreadsEndpoints.ThreadsPath, "{}"), PostJson("[]"),
                AddThread("ftp://x/y"), Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.Add("Origin", "http://evil.example"))
            };
            Client.DefaultRequestHeaders.Authorization = null;
            responses.Add(Get(ThreadsEndpoints.ThreadsPath));
            foreach (HttpResponseMessage response in responses) {
                Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                AssertValid(Json(response), "#/components/schemas/Problem");
            }
        }

        private static JsonElement ExampleOf(JsonElement operation, string status, string mediaType, string name) {
            return operation.GetProperty("responses").GetProperty(status).GetProperty("content").GetProperty(mediaType).GetProperty("examples").GetProperty(name).GetProperty("value");
        }

        private static JsonElement SchemaAt(string reference) {
            JsonElement schema;
            Assert.IsTrue(SchemaValidator.TryResolve(Document, reference, out schema), reference);
            return schema;
        }

        private static void AssertValid(JsonElement value, string schemaReference) {
            List<string> errors = SchemaValidator.Validate(Document, value, SchemaAt(schemaReference));
            Assert.AreEqual(0, errors.Count, String.Join("; ", errors) + " in " + value.GetRawText());
        }

        // The live value is valid, has the example's members, and equals it but for the run's time and state, and the
        // description, which the watcher sets on its first check (empty until then)
        private static void AssertMatchesExample(JsonElement live, JsonElement example, string schemaReference) {
            AssertValid(example, schemaReference);
            AssertValid(live, schemaReference);
            CollectionAssert.AreEqual(example.EnumerateObject().Select(member => member.Name).ToArray(), live.EnumerateObject().Select(member => member.Name).ToArray());
            foreach (JsonProperty member in example.EnumerateObject().Where(member => member.Name != "addedOn" && member.Name != "state" && member.Name != "description")) {
                Assert.AreEqual(member.Value.GetRawText(), live.GetProperty(member.Name).GetRawText(), member.Name);
            }
            CollectionAssert.Contains(new[] { "running", "waiting" }, live.GetProperty("state").GetString());
            CollectionAssert.Contains(new[] { "", example.GetProperty("description").GetString() }, live.GetProperty("description").GetString());
        }

        private static void AssertSameJson(JsonElement expected, JsonElement actual) {
            Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), JsonNode.Parse(actual.GetRawText())), expected.GetRawText() + " != " + actual.GetRawText());
        }

        private static IEnumerable<string> References(JsonElement element) {
            if (element.ValueKind == JsonValueKind.Object) {
                foreach (JsonProperty member in element.EnumerateObject()) {
                    if (member.Name == "$ref") yield return member.Value.GetString();
                    foreach (string reference in References(member.Value)) yield return reference;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array) {
                foreach (JsonElement item in element.EnumerateArray()) {
                    foreach (string reference in References(item)) yield return reference;
                }
            }
        }
    }

    // The part of JSON Schema 2020-12 that openapi.json uses: $ref, type (one or a list), required, properties,
    // additionalProperties false, items, enum, const, maxLength and format date-time. Anything else in a schema fails
    // the validation, so the document cannot use a keyword this validator would ignore.
    internal static class SchemaValidator {
        private static readonly HashSet<string> _keywords = new HashSet<string> { "$ref", "type", "required", "properties", "additionalProperties", "items", "enum", "const", "maxLength", "format", "description" };

        public static bool TryResolve(JsonElement document, string reference, out JsonElement target) {
            target = document;
            if (!reference.StartsWith("#/", StringComparison.Ordinal)) return false;
            foreach (string part in reference.Substring(2).Split('/')) {
                if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty(part, out target)) return false;
            }
            return true;
        }

        public static List<string> Validate(JsonElement document, JsonElement value, JsonElement schema) {
            List<string> errors = new List<string>();
            Validate(document, value, schema, "$", errors);
            return errors;
        }

        private static void Validate(JsonElement document, JsonElement value, JsonElement schema, string path, List<string> errors) {
            foreach (JsonProperty keyword in schema.EnumerateObject()) {
                if (!_keywords.Contains(keyword.Name)) errors.Add(path + ": unsupported keyword " + keyword.Name);
            }
            JsonElement reference;
            if (schema.TryGetProperty("$ref", out reference)) {
                JsonElement target;
                if (!TryResolve(document, reference.GetString(), out target)) errors.Add(path + ": unresolved " + reference.GetString());
                else Validate(document, value, target, path, errors);
                return;
            }
            CheckType(value, schema, path, errors);
            CheckValues(value, schema, path, errors);
            if (value.ValueKind == JsonValueKind.Object) CheckObject(document, value, schema, path, errors);
            if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items)) {
                int i = 0;
                foreach (JsonElement item in value.EnumerateArray()) Validate(document, item, items, path + "[" + i++ + "]", errors);
            }
        }

        private static void CheckType(JsonElement value, JsonElement schema, string path, List<string> errors) {
            if (!schema.TryGetProperty("type", out JsonElement type)) return;
            string[] types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()).ToArray() : new[] { type.GetString() };
            if (!types.Any(t => IsOfType(value, t))) errors.Add(path + ": not " + String.Join("|", types));
        }

        private static bool IsOfType(JsonElement value, string type) {
            switch (type) {
                case "object": return value.ValueKind == JsonValueKind.Object;
                case "array": return value.ValueKind == JsonValueKind.Array;
                case "string": return value.ValueKind == JsonValueKind.String;
                case "integer": return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _);
                case "number": return value.ValueKind == JsonValueKind.Number;
                case "boolean": return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False;
                case "null": return value.ValueKind == JsonValueKind.Null;
                default: return false;
            }
        }

        private static void CheckValues(JsonElement value, JsonElement schema, string path, List<string> errors) {
            if (schema.TryGetProperty("enum", out JsonElement allowed) && !allowed.EnumerateArray().Any(item => item.GetRawText() == value.GetRawText())) errors.Add(path + ": not in enum: " + value.GetRawText());
            if (schema.TryGetProperty("const", out JsonElement constant) && constant.GetRawText() != value.GetRawText()) errors.Add(path + ": not the const");
            if (value.ValueKind != JsonValueKind.String) return;
            if (schema.TryGetProperty("maxLength", out JsonElement maxLength) && value.GetString().Length > maxLength.GetInt32()) errors.Add(path + ": too long");
            if (schema.TryGetProperty("format", out JsonElement format) && format.GetString() == "date-time" && !IsDateTime(value.GetString())) errors.Add(path + ": not a date-time");
        }

        // RFC 3339: a date, "T", a time and an offset or Z
        private static bool IsDateTime(string text) {
            return text.Length > 19 && text[10] == 'T' && (text.EndsWith("Z", StringComparison.Ordinal) || text[text.Length - 6] == '+' || text[text.Length - 6] == '-') &&
                   DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        }

        private static void CheckObject(JsonElement document, JsonElement value, JsonElement schema, string path, List<string> errors) {
            if (schema.TryGetProperty("required", out JsonElement required)) {
                foreach (JsonElement name in required.EnumerateArray()) {
                    if (!value.TryGetProperty(name.GetString(), out _)) errors.Add(path + ": missing " + name.GetString());
                }
            }
            schema.TryGetProperty("properties", out JsonElement properties);
            bool closed = schema.TryGetProperty("additionalProperties", out JsonElement additional) && additional.ValueKind == JsonValueKind.False;
            foreach (JsonProperty member in value.EnumerateObject()) {
                JsonElement memberSchema = default;
                bool known = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(member.Name, out memberSchema);
                if (known) Validate(document, member.Value, memberSchema, path + "." + member.Name, errors);
                else if (closed) errors.Add(path + ": unexpected member " + member.Name);
            }
        }
    }

    [TestClass]
    public class SchemaValidatorTests {
        private static readonly JsonElement _document = JsonDocument.Parse(
            "{\"s\":{\"type\":\"object\",\"required\":[\"a\"],\"additionalProperties\":false,\"properties\":{\"a\":{\"type\":[\"string\",\"null\"],\"enum\":[\"x\",null]},\"d\":{\"type\":\"string\",\"format\":\"date-time\"}}}}").RootElement;

        // The validator must fail wrong values, or the contract tests would pass whatever the server sends
        [TestMethod]
        public void Validator_RejectsWhatTheSchemaForbids() {
            JsonElement schema = _document.GetProperty("s");
            Assert.AreEqual(0, SchemaValidator.Validate(_document, Parse("{\"a\":\"x\",\"d\":\"2026-10-05T12:30:00+02:00\"}"), schema).Count);
            Assert.AreEqual(0, SchemaValidator.Validate(_document, Parse("{\"a\":null}"), schema).Count);
            foreach (string bad in new[] { "{}", "{\"a\":\"y\"}", "{\"a\":1}", "{\"a\":\"x\",\"b\":1}", "{\"a\":\"x\",\"d\":\"yesterday\"}", "[]" }) {
                Assert.AreNotEqual(0, SchemaValidator.Validate(_document, Parse(bad), schema).Count, bad);
            }
        }

        private static JsonElement Parse(string json) {
            return JsonDocument.Parse(json).RootElement;
        }
    }
}
