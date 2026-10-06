using System;
using System.Globalization;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace JDP.Api {
    // The first middleware, ahead of routing, so every route gets the same checks (security items 2, 3, 7, 13, 14,
    // 15, 17), in this order: Host, Origin / Sec-Fetch-Site, query string, bearer token, global rate. The rate limit
    // counts authenticated requests only, so requests that a web page or another program can make without the token
    // never use it up. A request refused by these security checks gets Connection: close, so such clients cannot hold
    // the 16 connections. Every response gets Cache-Control: no-store and X-Content-Type-Options: nosniff; no
    // response ever gets a CORS header. Forwarded headers are never read: the rate limit is one global limit, not one
    // per client address.
    internal sealed class ApiSecurity {
        private readonly ApiTokenStore _tokens;
        private readonly Func<int> _boundPort;
        private readonly RateLimiter _requests;

        public ApiSecurity(ApiTokenStore tokens, Func<int> boundPort, RateLimiter requests) {
            _tokens = tokens;
            _boundPort = boundPort;
            _requests = requests;
        }

        public Task InvokeAsync(HttpContext context, RequestDelegate next) {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            ApiError error = Check(context);
            if (error == null) return next(context);
            context.Response.Headers.Connection = "close";
            return error.WriteAsync(context);
        }

        private ApiError Check(HttpContext context) {
            int port = _boundPort();
            ApiError error = CheckHost(context.Request, port) ?? CheckOrigin(context.Request, port);
            error = error ?? CheckQuery(context) ?? CheckToken(context.Request);
            return error ?? CheckRate();
        }

        // Exactly 127.0.0.1:<port> or localhost:<port> (DNS rebinding); a missing Host, another port, "localhost.",
        // [::1], 127.1 and every other form of the address are refused
        private static ApiError CheckHost(HttpRequest request, int port) {
            StringValues host = request.Headers.Host;
            if (host.Count != 1 || port == 0) return ApiError.WrongHost;
            string suffix = ":" + port.ToString(CultureInfo.InvariantCulture);
            return IsSelf(host.ToString(), "127.0.0.1" + suffix, "localhost" + suffix) ? null : ApiError.WrongHost;
        }

        private ApiError CheckRate() {
            using (RateLimitLease lease = _requests.AttemptAcquire()) {
                return lease.IsAcquired ? null : RateLimited(lease, ApiError.TooManyRequests);
            }
        }

        internal static ApiError RateLimited(RateLimitLease lease, ApiError error) {
            TimeSpan retryAfter;
            return lease.TryGetMetadata(MetadataName.RetryAfter, out retryAfter) ? error.WithRetryAfter(retryAfter) : error.WithRetryAfter(TimeSpan.FromMinutes(1));
        }

        // No query string at all (security item 7): the token never goes in one, and no route takes parameters. The
        // raw request target is checked, so even an empty "?" is refused.
        private static ApiError CheckQuery(HttpContext context) {
            IHttpRequestFeature requestFeature = context.Features.Get<IHttpRequestFeature>();
            string target = requestFeature?.RawTarget ?? String.Empty;
            return target.Contains('?') || context.Request.QueryString.HasValue ? ApiError.QueryNotAllowed : null;
        }

        // A browser page of another origin (including "null") is refused. Without an Origin, Sec-Fetch-Site tells a
        // browser's cross-site request apart from a script's, which sends neither.
        private static ApiError CheckOrigin(HttpRequest request, int port) {
            StringValues origin = request.Headers.Origin;
            if (origin.Count == 0) return CheckFetchSite(request.Headers["Sec-Fetch-Site"]);
            string suffix = ":" + port.ToString(CultureInfo.InvariantCulture);
            return origin.Count == 1 && IsSelf(origin.ToString(), "http://127.0.0.1" + suffix, "http://localhost" + suffix) ? null : ApiError.ForbiddenOrigin;
        }

        private static ApiError CheckFetchSite(StringValues fetchSite) {
            if (fetchSite.Count == 0) return null;
            string value = fetchSite.ToString();
            return fetchSite.Count == 1 && (value == "none" || value == "same-origin") ? null : ApiError.ForbiddenOrigin;
        }

        // Authorization: Bearer <token>, compared by hash in constant time. A value over the limit is refused before
        // it is hashed.
        private ApiError CheckToken(HttpRequest request) {
            StringValues authorization = request.Headers.Authorization;
            if (authorization.Count != 1) return ApiError.Unauthorized;
            string value = authorization.ToString();
            const string scheme = "Bearer ";
            if (value.Length > ApiPolicy.MaxAuthorizationLength || !value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return ApiError.Unauthorized;
            return _tokens.Verify(value.Substring(scheme.Length)) ? null : ApiError.Unauthorized;
        }

        // The IP form exactly, or "localhost" in any ASCII case
        private static bool IsSelf(string value, string ipForm, string localhostForm) {
            return value == ipForm || EqualsAsciiIgnoreCase(value, localhostForm);
        }

        // Only A-Z and a-z match across case, so no other character (such as U+017F, which upper-cases to S) can
        // stand in for a letter
        internal static bool EqualsAsciiIgnoreCase(string value, string expected) {
            if (value.Length != expected.Length) return false;
            for (int i = 0; i < value.Length; i++) {
                if (ToAsciiLower(value[i]) != ToAsciiLower(expected[i])) return false;
            }
            return true;
        }

        private static char ToAsciiLower(char c) {
            return c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
        }
    }
}
