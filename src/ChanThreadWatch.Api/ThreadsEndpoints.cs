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
    // The only routes (security item 19: nothing opens a folder or a URL): GET and POST /api/v1/threads, and the
    // OpenAPI document. They run after ApiSecurity.
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
        private static async Task RunAsync(HttpContext context, Func<HttpContext, Task> handler) {
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
            ApiBody body = await ApiBody.ReadAsync(context.Request, _threads.Policy.MaxBodyBytes).ConfigureAwait(false);
            if (body.IsAborted) {
                context.Abort();
                return;
            }
            Uri uri;
            ApiError error = CheckAdd(body, out uri);
            await (error != null ? error.WriteAsync(context) : AddCheckedAsync(context, uri)).ConfigureAwait(false);
        }

        private ApiError CheckAdd(ApiBody body, out Uri uri) {
            uri = null;
            return body.Error ?? _threads.ValidateUrl(body.Url, out uri) ?? AcquireAdd();
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

        private static Task WriteJsonAsync<T>(HttpContext context, int status, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            return JsonSerializer.SerializeAsync(context.Response.Body, value, typeInfo, context.RequestAborted);
        }
    }

    // The body of an add, read only after the request passed ApiSecurity: application/json (UTF-8), at most the limit
    // in bytes, one JSON object with one string member "url" and nothing after it
    internal sealed class ApiBody {
        private ApiBody(string url, ApiError error) {
            Url = url;
            Error = error;
        }

        public string Url { get; }
        public ApiError Error { get; }

        // The connection broke or the client gave up while the body was read: nothing to answer, nothing to log
        public bool IsAborted { get; private set; }

        public static Task<ApiBody> ReadAsync(HttpRequest request, int maxBytes) {
            ApiError error = CheckContentType(request.ContentType) ?? CheckLength(request.ContentLength, maxBytes);
            return error != null ? Task.FromResult(new ApiBody(null, error)) : ReadBodyAsync(request, maxBytes);
        }

        private static ApiError CheckLength(long? contentLength, int maxBytes) {
            return contentLength > maxBytes ? ApiError.BodyTooLarge : null;
        }

        private static async Task<ApiBody> ReadBodyAsync(HttpRequest request, int maxBytes) {
            try {
                byte[] bytes = await ReadLimitedAsync(request, maxBytes).ConfigureAwait(false);
                return bytes == null ? new ApiBody(null, ApiError.BodyTooLarge) : Parse(bytes);
            }
            catch (BadHttpRequestException ex) {
                return new ApiBody(null, ToError(ex));
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException) {
                return new ApiBody(null, null) { IsAborted = true };
            }
        }

        // Kestrel's own body errors: over its size limit, too slow, or malformed (a bad chunk)
        private static ApiError ToError(BadHttpRequestException ex) {
            if (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) return ApiError.BodyTooLarge;
            return ex.StatusCode == StatusCodes.Status408RequestTimeout ? ApiError.RequestTimeout : ApiError.InvalidBody;
        }

        private static ApiError CheckContentType(string contentType) {
            MediaTypeHeaderValue mediaType;
            if (!MediaTypeHeaderValue.TryParse(contentType, out mediaType) || !mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)) return ApiError.UnsupportedMediaType;
            return !mediaType.Charset.HasValue || mediaType.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ? null : ApiError.UnsupportedMediaType;
        }

        // Null when the body is over the limit
        private static async Task<byte[]> ReadLimitedAsync(HttpRequest request, int maxBytes) {
            byte[] buffer = new byte[maxBytes + 1];
            int total = 0;
            int read;
            while (total < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(total), request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0) {
                total += read;
            }
            return total > maxBytes ? null : buffer.AsSpan(0, total).ToArray();
        }

        private static ApiBody Parse(byte[] bytes) {
            try {
                AddThreadRequest body = JsonSerializer.Deserialize(bytes, ApiJsonContext.Default.AddThreadRequest);
                return body?.Url != null ? new ApiBody(body.Url, null) : new ApiBody(null, ApiError.InvalidBody);
            }
            catch (JsonException) {
                return new ApiBody(null, ApiError.InvalidBody);
            }
        }
    }
}
