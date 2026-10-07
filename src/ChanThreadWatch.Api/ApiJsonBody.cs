using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace JDP.Api {
    // The checks of a request body that need no reading: application/json (UTF-8 if a charset is given) and a declared
    // length within the limit. ApiSecurity also runs them before a pairing route takes a rate permit.
    internal static class ApiJsonBody {
        public static ApiError CheckHead(HttpRequest request, int maxBytes) {
            return CheckContentType(request.ContentType) ?? CheckLength(request.ContentLength, maxBytes);
        }

        private static ApiError CheckLength(long? contentLength, int maxBytes) {
            return contentLength > maxBytes ? ApiError.BodyTooLarge : null;
        }

        private static ApiError CheckContentType(string contentType) {
            MediaTypeHeaderValue mediaType;
            if (!MediaTypeHeaderValue.TryParse(contentType, out mediaType) || !mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)) return ApiError.UnsupportedMediaType;
            return !mediaType.Charset.HasValue || mediaType.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ? null : ApiError.UnsupportedMediaType;
        }

        // Null when the body is over the limit
        internal static async Task<byte[]> ReadLimitedAsync(HttpRequest request, int maxBytes) {
            byte[] buffer = new byte[maxBytes + 1];
            int total = 0;
            int read;
            while (total < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(total), request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0) {
                total += read;
            }
            return total > maxBytes ? null : buffer.AsSpan(0, total).ToArray();
        }

        // Kestrel's own body errors: over its size limit, too slow, or malformed (a bad chunk), which is the route's
        // invalid-body error
        internal static ApiError ToError(BadHttpRequestException ex, ApiError invalid) {
            if (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) return ApiError.BodyTooLarge;
            return ex.StatusCode == StatusCodes.Status408RequestTimeout ? ApiError.RequestTimeout : invalid;
        }
    }

    // A request body, read only after the request passed ApiSecurity: the head checks, at most the limit in bytes, then
    // one JSON value of the type with no other member and nothing after it. A body that is not that, a JSON null, or a
    // malformed chunk gets the route's own invalid-body error (POST /api/v1/threads and the pairing routes differ in
    // its title).
    internal sealed class ApiJsonBody<T> where T : class {
        private ApiJsonBody(T value, ApiError error, bool isAborted) {
            Value = value;
            Error = error;
            IsAborted = isAborted;
        }

        public T Value { get; }
        public ApiError Error { get; }

        // The connection broke or the client gave up while the body was read: nothing to answer, nothing to log
        public bool IsAborted { get; }

        public static Task<ApiJsonBody<T>> ReadAsync(HttpRequest request, int maxBytes, JsonTypeInfo<T> typeInfo, ApiError invalid) {
            ApiError error = ApiJsonBody.CheckHead(request, maxBytes);
            return error != null ? Task.FromResult(new ApiJsonBody<T>(null, error, false)) : ReadBodyAsync(request, maxBytes, typeInfo, invalid);
        }

        private static async Task<ApiJsonBody<T>> ReadBodyAsync(HttpRequest request, int maxBytes, JsonTypeInfo<T> typeInfo, ApiError invalid) {
            try {
                byte[] bytes = await ApiJsonBody.ReadLimitedAsync(request, maxBytes).ConfigureAwait(false);
                return bytes == null ? new ApiJsonBody<T>(null, ApiError.BodyTooLarge, false) : Parse(bytes, typeInfo, invalid);
            }
            catch (BadHttpRequestException ex) {
                return new ApiJsonBody<T>(null, ApiJsonBody.ToError(ex, invalid), false);
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException) {
                return new ApiJsonBody<T>(null, null, true);
            }
        }

        private static ApiJsonBody<T> Parse(byte[] bytes, JsonTypeInfo<T> typeInfo, ApiError invalid) {
            try {
                T value = JsonSerializer.Deserialize(bytes, typeInfo);
                return new ApiJsonBody<T>(value, value == null ? invalid : null, false);
            }
            catch (JsonException) {
                return new ApiJsonBody<T>(null, invalid, false);
            }
        }
    }
}
