using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace JDP.Api {
    // The routes that need the token (security item 19: nothing opens a folder or a URL): GET and POST
    // /api/v1/threads, and the OpenAPI document. They run after ApiSecurity. The only other routes are the browser
    // pairing's POST /api/v1/pairing and POST /api/v1/proof (PairingEndpoints; MP-7b maintainer decision Q1).
    internal sealed class ThreadsEndpoints {
        public const string ThreadsPath = "/api/v1/threads";
        public const string OpenApiPath = "/api/v1/openapi.json";

        private readonly ApiThreadService _threads;
        private readonly RateLimiter _adds;

        public ThreadsEndpoints(ApiThreadService threads, RateLimiter adds) {
            _threads = threads;
            _adds = adds;
        }

        public void Map(IEndpointRouteBuilder routes) {
            routes.MapGet(ThreadsPath, context => RunAsync(context, ListAsync));
            routes.MapPost(ThreadsPath, context => RunAsync(context, AddAsync));
            routes.MapGet(OpenApiPath, WriteOpenApiAsync);
        }

        // The embedded document, byte for byte
        internal static byte[] ReadOpenApiDocument() {
            using (Stream stream = typeof(ThreadsEndpoints).Assembly.GetManifestResourceStream("openapi.json") ?? throw new InvalidOperationException("The OpenAPI document is not embedded."))
            using (MemoryStream copy = new MemoryStream()) {
                stream.CopyTo(copy);
                return copy.ToArray();
            }
        }

        private static readonly Lazy<byte[]> _openApiDocument = new Lazy<byte[]>(ReadOpenApiDocument);

        private static Task WriteOpenApiAsync(HttpContext context) {
            context.Response.ContentType = "application/json";
            return context.Response.Body.WriteAsync(_openApiDocument.Value, context.RequestAborted).AsTask();
        }

        // A busy or exiting owner thread gives 503; a request the client gave up on ends quietly; any other failure
        // gives 500 and is logged as the exception's type only (its text can hold a URL with a login)
        internal static async Task RunAsync(HttpContext context, Func<HttpContext, Task> handler) {
            try {
                await handler(context).ConfigureAwait(false);
            }
            catch (ApiUnavailableException) {
                await ApiError.Unavailable.WriteAsync(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
            }
            catch (Exception ex) when (CanReportFailure(context, ex)) {
                Logger.Log("Local API: a request failed: " + ex.GetType().FullName);
                await ApiError.InternalError.WriteAsync(context).ConfigureAwait(false);
            }
        }

        private static bool CanReportFailure(HttpContext context, Exception ex) {
            return !context.Response.HasStarted && !(ex is OperationCanceledException);
        }

        private async Task ListAsync(HttpContext context) {
            ApiThreadList list = await _threads.ListAsync().ConfigureAwait(false);
            await WriteJsonAsync(context, StatusCodes.Status200OK, list, ApiJsonContext.Default.ApiThreadList).ConfigureAwait(false);
        }

        // Content type, body size, body, URL, add rate, then the add itself
        private async Task AddAsync(HttpContext context) {
            ApiJsonBody<AddThreadRequest> body = await ApiJsonBody<AddThreadRequest>.ReadAsync(context.Request, _threads.Policy.MaxBodyBytes, ApiJsonContext.Default.AddThreadRequest, ApiError.InvalidBody).ConfigureAwait(false);
            if (body.IsAborted) {
                context.Abort();
                return;
            }
            Uri uri;
            ApiError error = CheckAdd(body, out uri);
            await (error != null ? error.WriteAsync(context) : AddCheckedAsync(context, uri)).ConfigureAwait(false);
        }

        // One JSON object with one string member "url" (a null "url" is invalid), then the URL rules and the add rate
        private ApiError CheckAdd(ApiJsonBody<AddThreadRequest> body, out Uri uri) {
            uri = null;
            if (body.Error != null) return body.Error;
            return body.Value.Url == null ? ApiError.InvalidBody : _threads.ValidateUrl(body.Value.Url, out uri) ?? AcquireAdd();
        }

        private async Task AddCheckedAsync(HttpContext context, Uri uri) {
            ApiAddResult result = await _threads.AddAsync(uri, context.RequestAborted).ConfigureAwait(false);
            await (result.Error != null ? result.Error.WriteAsync(context) : WriteJsonAsync(context, StatusCodes.Status201Created, result.Thread, ApiJsonContext.Default.ApiThread)).ConfigureAwait(false);
        }

        private ApiError AcquireAdd() {
            using (RateLimitLease lease = _adds.AttemptAcquire()) {
                return lease.IsAcquired ? null : ApiSecurity.RateLimited(lease, ApiError.TooManyAdds);
            }
        }

        internal static Task WriteJsonAsync<T>(HttpContext context, int status, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            return JsonSerializer.SerializeAsync(context.Response.Body, value, typeInfo, context.RequestAborted);
        }
    }
}
