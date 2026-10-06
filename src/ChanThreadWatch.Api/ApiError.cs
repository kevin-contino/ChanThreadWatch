using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace JDP.Api {
    // An error response: its status, a stable code for clients, and a fixed title (never an exception text)
    internal sealed class ApiError {
        public ApiError(int status, string code, string title) {
            Status = status;
            Code = code;
            Title = title;
        }

        public int Status { get; }
        public string Code { get; }
        public string Title { get; }

        // Sent as Retry-After on a 429
        public int? RetryAfterSeconds { get; private set; }

        // Sends WWW-Authenticate: Bearer (a 401)
        public bool IsBearerChallenge { get; private set; }

        public ApiError WithRetryAfter(TimeSpan retryAfter) {
            return new ApiError(Status, Code, Title) { RetryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) };
        }

        public static readonly ApiError WrongHost = new ApiError(StatusCodes.Status400BadRequest, "invalid_host", "The Host header is not this server's address.");
        public static readonly ApiError QueryNotAllowed = new ApiError(StatusCodes.Status400BadRequest, "query_not_allowed", "Query strings are not accepted.");
        public static readonly ApiError ForbiddenOrigin = new ApiError(StatusCodes.Status403Forbidden, "forbidden_origin", "Requests from other origins are not accepted.");
        public static readonly ApiError Unauthorized = new ApiError(StatusCodes.Status401Unauthorized, "unauthorized", "A valid bearer token is required.") { IsBearerChallenge = true };
        public static readonly ApiError NotFound = new ApiError(StatusCodes.Status404NotFound, "not_found", "No such endpoint.");
        public static readonly ApiError MethodNotAllowed = new ApiError(StatusCodes.Status405MethodNotAllowed, "method_not_allowed", "The method is not allowed for this endpoint.");
        public static readonly ApiError UnsupportedMediaType = new ApiError(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", "The body must be application/json.");
        public static readonly ApiError BodyTooLarge = new ApiError(StatusCodes.Status413PayloadTooLarge, "body_too_large", "The body is too large.");
        public static readonly ApiError RequestTimeout = new ApiError(StatusCodes.Status408RequestTimeout, "request_timeout", "The body was not sent in time.");
        public static readonly ApiError InvalidBody = new ApiError(StatusCodes.Status400BadRequest, "invalid_body", "The body must be a JSON object with one string member, \"url\".");
        public static readonly ApiError InvalidUrl = new ApiError(StatusCodes.Status422UnprocessableEntity, "invalid_url", "The URL must be an absolute http or https address without a login, of at most 2048 characters.");
        public static readonly ApiError UnknownHost = new ApiError(StatusCodes.Status422UnprocessableEntity, "unknown_host", "The site is not a known site, and unknown sites are not allowed.");
        public static readonly ApiError BlockedHost = new ApiError(StatusCodes.Status422UnprocessableEntity, "blocked_host", "The host is a local or private address.");
        public static readonly ApiError UnresolvableHost = new ApiError(StatusCodes.Status422UnprocessableEntity, "unresolvable_host", "The host name could not be resolved.");
        public static readonly ApiError TooManyRequests = new ApiError(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests.");
        public static readonly ApiError TooManyAdds = new ApiError(StatusCodes.Status429TooManyRequests, "add_rate_limited", "Too many threads added.");
        public static readonly ApiError AlreadyWatched = new ApiError(StatusCodes.Status409Conflict, "already_watched", "The thread is already in the list.");
        public static readonly ApiError Blacklisted = new ApiError(StatusCodes.Status409Conflict, "blacklisted", "The thread is blacklisted.");
        public static readonly ApiError ThreadLimit = new ApiError(StatusCodes.Status409Conflict, "thread_limit", "The thread list is full.");
        public static readonly ApiError InsufficientStorage = new ApiError(StatusCodes.Status507InsufficientStorage, "insufficient_storage", "The download folder's drive is almost full.");
        public static readonly ApiError Unavailable = new ApiError(StatusCodes.Status503ServiceUnavailable, "unavailable", "The program is busy or exiting.");
        public static readonly ApiError InternalError = new ApiError(StatusCodes.Status500InternalServerError, "internal_error", "The request failed.");

        public Task WriteAsync(HttpContext context) {
            HttpResponse response = context.Response;
            response.StatusCode = Status;
            if (RetryAfterSeconds.HasValue) response.Headers.RetryAfter = RetryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
            if (IsBearerChallenge) response.Headers.WWWAuthenticate = "Bearer";
            response.ContentType = "application/problem+json";
            ApiProblemBody body = new ApiProblemBody { Type = "about:blank", Title = Title, Status = Status, Code = Code };
            return JsonSerializer.SerializeAsync(response.Body, body, ApiJsonContext.Default.ApiProblemBody, context.RequestAborted);
        }
    }
}
