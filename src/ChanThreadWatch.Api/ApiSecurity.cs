using System;
using System.Globalization;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace JDP.Api {
    // The first middleware after routing (which only selects the endpoint; nothing runs before these checks), so every
    // route gets the same checks (security items 2, 3, 7, 13, 14, 15, 17), in this order: Host, Origin /
    // Sec-Fetch-Site, query string, then bearer token, Origin binding and global rate. Which routes take no token is
    // decided by server-side endpoint metadata only (OpenRouteMetadata on the two pairing routes, MP-7b design E1, E5),
    // never by request text. A request routed to one of them must also be POST on exactly that route's path (raw target
    // and decoded path): any other method, case, encoding, trailing slash or absolute-form target gets 401, and no
    // token is read. Then the route's Origin rule, its body head checks and its own rate limit run in place of the
    // token, the binding and the global rate. An endpoint without the marker (every other route, the 405 endpoint
    // routing picks for a wrong method) and a request no route matched need the token. The global rate limit
    // counts authenticated requests only, so requests that a web page or another program can make without the token
    // never use it up. A request refused by these security checks gets Connection: close, so such clients cannot hold
    // the 16 connections. Every response gets Cache-Control: no-store and X-Content-Type-Options: nosniff; no
    // response ever gets a CORS header. Forwarded headers are never read: the rate limits are global limits, not one
    // per client address.
    internal sealed class ApiSecurity {
        private readonly ApiCredentials _credentials;
        private readonly Func<int> _boundPort;
        private readonly RateLimiter _requests;
        private readonly RateLimiter _pairing;
        private readonly RateLimiter _proof;
        private readonly int _maxBodyBytes;

        public ApiSecurity(ApiCredentials credentials, Func<int> boundPort, RateLimiter requests, RateLimiter pairing, RateLimiter proof, int maxBodyBytes) {
            _maxBodyBytes = maxBodyBytes;
            _credentials = credentials;
            _boundPort = boundPort;
            _requests = requests;
            _pairing = pairing;
            _proof = proof;
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
            ApiError error = CheckForm(context);
            if (error != null) return error;
            OpenRouteMetadata openRoute = context.GetEndpoint()?.Metadata.GetMetadata<OpenRouteMetadata>();
            return openRoute != null ? CheckOpenRoute(context, openRoute) : CheckAuthenticated(context.Request);
        }

        // A route that takes no token: only its one exact spelling, then its own checks
        private ApiError CheckOpenRoute(HttpContext context, OpenRouteMetadata route) {
            if (!IsExactPost(context, route.Path)) return ApiError.Unauthorized;
            bool pairing = route.Route == OpenRoute.Pairing;
            return CheckPairingRoute(context.Request, pairing, pairing ? _pairing : _proof);
        }

        // Host, Origin / Sec-Fetch-Site and query string, for every route
        private ApiError CheckForm(HttpContext context) {
            int port = _boundPort();
            return CheckHost(context.Request, port) ?? CheckOrigin(context.Request, port) ?? CheckQuery(context);
        }

        // The method is exactly POST, and both the raw target and the decoded path are exactly the route's path
        private static bool IsExactPost(HttpContext context, string path) {
            string target = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
            return context.Request.Method == "POST" && target == path && context.Request.Path.Value == path;
        }

        // The pairing routes' Origin rule, then the body's content type and declared length, then their own rate, so a
        // request refused by any of these never uses a permit. Pairing needs an extension Origin; the proof takes one
        // or none (a script). A Chrome Origin must be the pinned extension id. A local program that sends a well-formed
        // JSON request can still use up a window (10 pairing or 60 proof requests a minute): that is denial of service
        // only, accepted in the design, and it gains no credential.
        private ApiError CheckPairingRoute(HttpRequest request, bool needsOrigin, RateLimiter limiter) {
            StringValues origin = request.Headers.Origin;
            bool allowed = origin.Count == 0 ? !needsOrigin : ApiPairing.IsPairingOrigin(origin.ToString());
            if (!allowed) return ApiError.ForbiddenOrigin;
            return ApiJsonBody.CheckHead(request, _maxBodyBytes) ?? CheckRate(limiter, ApiError.PairingRateLimited);
        }

        private ApiError CheckAuthenticated(HttpRequest request) {
            ApiCredential credential = Identify(request);
            if (credential == null) return ApiError.Unauthorized;
            return CheckBinding(credential, request.Headers.Origin.ToString()) ?? CheckRate(_requests, ApiError.TooManyRequests);
        }

        // After the token: a paired browser's token passes only with the Origin it paired with (so never without an
        // Origin, from this server's own origin or from another extension), and the scripts' token never with an
        // extension Origin
        internal static ApiError CheckBinding(ApiCredential credential, string origin) {
            bool bound = credential.IsExtension ? origin == credential.Origin : ApiPairing.FamilyOf(origin) == null;
            return bound ? null : ApiError.ForbiddenOrigin;
        }

        // Exactly 127.0.0.1:<port> or localhost:<port> (DNS rebinding); a missing Host, another port, "localhost.",
        // [::1], 127.1 and every other form of the address are refused
        private static ApiError CheckHost(HttpRequest request, int port) {
            StringValues host = request.Headers.Host;
            if (host.Count != 1 || port == 0) return ApiError.WrongHost;
            string suffix = ":" + port.ToString(CultureInfo.InvariantCulture);
            return IsSelf(host.ToString(), "127.0.0.1" + suffix, "localhost" + suffix) ? null : ApiError.WrongHost;
        }

        private static ApiError CheckRate(RateLimiter limiter, ApiError error) {
            using (RateLimitLease lease = limiter.AttemptAcquire()) {
                return lease.IsAcquired ? null : RateLimited(lease, error);
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

        // A browser page of another origin (including "null") is refused. This server's own origins and the two
        // extension forms (chrome-extension:// and 32 letters a-p, moz-extension:// and a lowercase UUID; one value,
        // exact) go on to the token or the pairing route's rule. Without an Origin, Sec-Fetch-Site tells a browser's
        // cross-site request apart from a script's, which sends neither.
        private static ApiError CheckOrigin(HttpRequest request, int port) {
            StringValues origin = request.Headers.Origin;
            if (origin.Count == 0) return CheckFetchSite(request.Headers["Sec-Fetch-Site"]);
            string suffix = ":" + port.ToString(CultureInfo.InvariantCulture);
            string value = origin.ToString();
            bool allowed = IsSelf(value, "http://127.0.0.1" + suffix, "http://localhost" + suffix) || ApiPairing.FamilyOf(value) != null;
            return origin.Count == 1 && allowed ? null : ApiError.ForbiddenOrigin;
        }

        private static ApiError CheckFetchSite(StringValues fetchSite) {
            if (fetchSite.Count == 0) return null;
            string value = fetchSite.ToString();
            return fetchSite.Count == 1 && (value == "none" || value == "same-origin") ? null : ApiError.ForbiddenOrigin;
        }

        // Authorization: Bearer <token>, compared by hash in constant time (ApiCredentials). A value over the limit is
        // refused before it is hashed. Null when no token passes.
        private ApiCredential Identify(HttpRequest request) {
            StringValues authorization = request.Headers.Authorization;
            if (authorization.Count != 1) return null;
            string value = authorization.ToString();
            const string scheme = "Bearer ";
            if (value.Length > ApiPolicy.MaxAuthorizationLength || !value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
            return _credentials.Identify(value.Substring(scheme.Length));
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
