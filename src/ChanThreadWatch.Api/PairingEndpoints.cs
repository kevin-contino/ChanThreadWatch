using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace JDP.Api {
    internal enum OpenRoute {
        Pairing,
        Proof
    }

    // Endpoint metadata of a route that takes no token (ApiSecurity): which one it is, and its one exact path. Only the
    // two pairing routes carry it; a route without it needs the token.
    internal sealed class OpenRouteMetadata {
        public OpenRouteMetadata(OpenRoute route, string path) {
            Route = route;
            Path = path;
        }

        public OpenRoute Route { get; }
        public string Path { get; }
    }

    // The browser pairing (MP-7b design, section 3): POST /api/v1/pairing (steps hello and finish) and POST
    // /api/v1/proof, the two routes without a token. ApiSecurity has already checked the Host, the Origin rule of the
    // route, the query string and the route's own rate. The state of the one pending code (answered hellos, burned,
    // used) is kept in memory per pairingId, under one lock, and dropped when the file holds another code. A refusal
    // closes the connection. The log never holds the code, key, token, nonces or proofs.
    internal sealed class PairingEndpoints {
        public const string PairingPath = "/api/v1/pairing";
        public const string ProofPath = "/api/v1/proof";
        private const string HelloStep = "hello";
        private const string FinishStep = "finish";
        // A code that would expire later than its lifetime (and this margin) from now was not written by a creator of
        // this version; the file is not trusted
        private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(1);

        private readonly ApiPolicy _policy;
        private readonly ApiCredentials _credentials;
        private readonly ApiPairingFile _pairingFile;
        private readonly Func<int> _boundPort;
        private readonly object _sync = new object();
        // The pending code's state, under _sync; one code at a time, so the state is bounded
        private PairingSession _session;
        // The ids of the codes that ended in this process (the last few), under _sync: such a code never gets a new
        // state, even if its file comes back
        private readonly Queue<string> _endedIds = new Queue<string>();
        private const int EndedIdsKept = 8;
        // Set once a refused api-pairing.txt was logged, under _sync; cleared by an accepted read, so the log says so
        // once per spell
        private bool _refusalLogged;

        // Test only: run at the start of a finish (before the lock), and when a finish starts to complete (in the lock)
        internal static Action FinishArrivingForTesting { get; set; }
        internal static Action CompletingForTesting { get; set; }
        // Test only: run in a hello or a finish after the file was read, before the lock
        internal static Action HelloReadForTesting { get; set; }
        internal static Action FinishReadForTesting { get; set; }

        public PairingEndpoints(ApiPolicy policy, ApiCredentials credentials, ApiPairingFile pairingFile, Func<int> boundPort) {
            _policy = policy;
            _credentials = credentials;
            _pairingFile = pairingFile;
            _boundPort = boundPort;
        }

        public void Map(IEndpointRouteBuilder routes) {
            routes.MapPost(PairingPath, context => ThreadsEndpoints.RunAsync(context, PairAsync)).WithMetadata(new OpenRouteMetadata(OpenRoute.Pairing, PairingPath));
            routes.MapPost(ProofPath, context => ThreadsEndpoints.RunAsync(context, ProveAsync)).WithMetadata(new OpenRouteMetadata(OpenRoute.Proof, ProofPath));
        }

        // Content type, body size, body, step rules, then the step
        private async Task PairAsync(HttpContext context) {
            ApiJsonBody<PairingRequest> body = await ApiJsonBody<PairingRequest>.ReadAsync(context.Request, _policy.MaxBodyBytes, ApiJsonContext.Default.PairingRequest, ApiError.InvalidPairingBody).ConfigureAwait(false);
            if (body.IsAborted) {
                context.Abort();
                return;
            }
            Func<HttpContext, Task> reply = body.Error != null ? Fail(body.Error) : Pair(body.Value, context.Request.Headers.Origin.ToString());
            await reply(context).ConfigureAwait(false);
        }

        // ApiSecurity checked the Origin, and refused any other spelling of the path; checked again here as a second
        // line
        private Func<HttpContext, Task> Pair(PairingRequest request, string origin) {
            if (!ApiPairing.IsPairingOrigin(origin)) return Fail(ApiError.ForbiddenOrigin);
            if (IsHello(request)) return Hello(request.ClientNonce, origin);
            return IsFinish(request) ? Finish(request, origin) : Fail(ApiError.InvalidPairingBody);
        }

        // Exactly "step" and "clientNonce": another member, also one that is null, is refused
        private static bool IsHello(PairingRequest request) {
            return request.Step == HelloStep && request.MemberCount == 2 && IsBytes(request.ClientNonce, ApiPairing.NonceBytes);
        }

        private static bool IsFinish(PairingRequest request) {
            return request.Step == FinishStep && IsBytes(request.PairingId, ApiPairing.IdBytes) && IsBytes(request.ClientNonce, ApiPairing.NonceBytes) &&
                   IsBytes(request.ServerNonce, ApiPairing.NonceBytes) && IsBytes(request.Proof, ApiPairing.ProofBytes);
        }

        private static bool IsBytes(string text, int byteCount) {
            return ApiPairing.FromBase64Url(text, byteCount) != null;
        }

        // Step 1: answers P1 with a new server nonce, up to MaxHellosPerCode times per code
        private Func<HttpContext, Task> Hello(string clientNonce, string origin) {
            PendingFile file = ReadFile();
            HelloReadForTesting?.Invoke();
            lock (_sync) {
                PairingSession session = OpenSession(file);
                ApiError error = session == null ? ApiError.PairingUnavailable : CheckHello(session);
                return error != null ? Fail(error) : AnswerHello(session, clientNonce, origin);
            }
        }

        // An expired code ends with 409 (no code); the hello after the last one answered burns it
        private ApiError CheckHello(PairingSession session) {
            if (IsExpired(session.Pending)) {
                Burn(session, "expired");
                return ApiError.PairingUnavailable;
            }
            if (++session.HelloCount <= ApiPolicy.MaxHellosPerCode) return null;
            Burn(session, "too many hellos");
            return ApiError.PairingFailed;
        }

        private Func<HttpContext, Task> AnswerHello(PairingSession session, string clientNonce, string origin) {
            ApiPendingPairing pending = session.Pending;
            string serverNonce = ApiPairing.NewNonce();
            string name = _policy.ServerName;
            session.Answered.Add((origin, clientNonce, serverNonce));
            byte[] proof = ApiPairing.ServerHelloProof(pending.Key, _boundPort(), origin, pending.Id, clientNonce, serverNonce, name);
            PairingHelloResponse response = new PairingHelloResponse { PairingId = pending.Id, Salt = pending.Salt, ServerNonce = serverNonce, ServerName = name, Proof = ApiPairing.Base64Url(proof) };
            return context => ThreadsEndpoints.WriteJsonAsync(context, StatusCodes.Status200OK, response, ApiJsonContext.Default.PairingHelloResponse);
        }

        // Step 3: checks P2 with this request's Origin; any failure burns the code. Under the lock, so of two finishes
        // at once exactly one can succeed. The file work in the lock (the clients file, marking or deleting the code)
        // is short, and the pairing rate limit (10 a minute) bounds how often it runs.
        private Func<HttpContext, Task> Finish(PairingRequest request, string origin) {
            FinishArrivingForTesting?.Invoke();
            PendingFile file = ReadFile();
            FinishReadForTesting?.Invoke();
            lock (_sync) {
                PairingSession session = OpenSession(file);
                if (session == null || session.Pending.Id != request.PairingId) return Fail(ApiError.PairingUnavailable);
                return FinishCode(session, request, origin);
            }
        }

        // In the lock: a failed check burns the code; a code that is no longer in the file ends without a token
        private Func<HttpContext, Task> FinishCode(PairingSession session, PairingRequest request, string origin) {
            string failure = FinishFailure(session, request, origin);
            if (failure == null) return IsStillHeld(session) ? Complete(session, request, origin) : Fail(ApiError.PairingUnavailable);
            Burn(session, failure);
            return Fail(ApiError.PairingFailed);
        }

        // The creator may have ended the code (its dialog closed, Ctrl+C) or made a new one since the file was read: no
        // token is made unless the file still holds this code just now. Otherwise the code ends here (409, no token);
        // that is also so when the file cannot be read. A creator write after this check is the window that stays.
        private bool IsStillHeld(PairingSession session) {
            CodeInFile check = _pairingFile.Check(session.Pending.Id);
            if (check == CodeInFile.Holds) return true;
            Burn(session, check == CodeInFile.Unknown ? ApiPairingFile.FileName + " could not be read" : "ended or replaced by its creator");
            return false;
        }

        // Why the finish fails, or null: the code expired, the two nonces are not a pair that a hello from this same
        // Origin was answered with, or the proof is not P2 (compared in constant time)
        private string FinishFailure(PairingSession session, PairingRequest request, string origin) {
            if (IsExpired(session.Pending)) return "expired";
            if (!session.Answered.Contains((origin, request.ClientNonce, request.ServerNonce))) return "unknown nonces";
            byte[] expected = ApiPairing.ClientFinishProof(session.Pending.Key, _boundPort(), origin, session.Pending.Id, request.ClientNonce, request.ServerNonce);
            return CryptographicOperations.FixedTimeEquals(expected, ApiPairing.FromBase64Url(request.Proof, ApiPairing.ProofBytes)) ? null : "wrong proof";
        }

        // Saves the family's new credential in place of its older one, marks the code used, and answers the token with
        // P3. A credential that cannot be saved ends the code too.
        private Func<HttpContext, Task> Complete(PairingSession session, PairingRequest request, string origin) {
            CompletingForTesting?.Invoke();
            EndSession(session);
            string family = ApiPairing.FamilyOf(origin);
            string token;
            try {
                token = _credentials.Clients.Pair(origin, _policy.UtcNow());
            }
            catch (ApiTokenException ex) {
                EndCode(session.Pending.Id);
                Logger.Log("Local API: a pairing code was burned (" + ApiClientStore.FileName + " could not be saved: " + CauseType(ex) + ")");
                return Fail(ApiError.InternalError);
            }
            MarkPaired(session.Pending, family);
            Logger.Log("Local API: paired a " + family + " extension");
            byte[] proof = ApiPairing.ServerFinishProof(session.Pending.Key, _boundPort(), origin, session.Pending.Id, request.ClientNonce, request.ServerNonce, token);
            PairingFinishResponse response = new PairingFinishResponse { Token = token, Proof = ApiPairing.Base64Url(proof) };
            return context => ThreadsEndpoints.WriteJsonAsync(context, StatusCodes.Status200OK, response, ApiJsonContext.Default.PairingFinishResponse);
        }

        // The creator sees the result in the file. Only a file that still holds this code is marked (the check narrows the
        // window for a newer code; a file the creator removed is not made again); a file that cannot be read again or
        // marked is deleted, so the code cannot be used again after a restart.
        private void MarkPaired(ApiPendingPairing pending, string family) {
            try {
                if (!_pairingFile.MarkPaired(pending, family)) Logger.Log("Local API: " + ApiPairingFile.FileName + " no longer holds the code, so it was not marked as paired.");
            }
            catch (ApiTokenException ex) {
                EndCode(pending.Id);
                Logger.Log("Local API: " + ApiPairingFile.FileName + " could not be marked as paired: " + CauseType(ex));
            }
        }

        // Deletes the file unless it holds another code (see ApiPairingFile.Delete); a file that cannot be deleted is
        // logged (the state in memory still ends the code)
        private void EndCode(string id) {
            if (!_pairingFile.Delete(id)) Logger.Log("Local API: " + ApiPairingFile.FileName + " could not be deleted.");
        }

        private static string CauseType(Exception ex) {
            return (ex.InnerException ?? ex).GetType().FullName;
        }

        // The file as read before the lock, so a file another program holds usually does not hold the lock
        private PendingFile ReadFile() {
            bool refused;
            bool unreadable;
            ApiPendingPairing pending = _pairingFile.Read(out refused, out unreadable);
            return new PendingFile(pending, refused, unreadable);
        }

        // The state of the code in the file, or null when there is none, it is used, not trusted or unreadable, or it
        // ended in this process. A code that ended never replaces the live state (an outside writer may bring its file
        // back).
        private PairingSession OpenSession(PendingFile file) {
            ApiPendingPairing pending = CheckPending(Fresh(file));
            if (pending == null || _endedIds.Contains(pending.Id)) return null;
            if (!IsSessionCode(pending.Id)) _session = new PairingSession(pending);
            return _session.Ended ? null : _session;
        }

        // The read before the lock is used only when it holds the code of the state in memory. Otherwise (another code,
        // no code, or a read that failed) the file is read again under the lock, so a read a moment old never brings an
        // older code back. That second read can hold the lock while it retries a file in use (about 90 ms at most);
        // the pairing rate limit bounds how often.
        private PendingFile Fresh(PendingFile file) {
            return IsSessionCode(file.Pending?.Id) ? file : ReadFile();
        }

        private bool IsSessionCode(string id) {
            return _session != null && _session.Pending.Id == id;
        }

        // Ends the state and keeps its id (the last EndedIdsKept ids, each once)
        private void EndSession(PairingSession session) {
            session.Ended = true;
            if (_endedIds.Contains(session.Pending.Id)) return;
            _endedIds.Enqueue(session.Pending.Id);
            if (_endedIds.Count > EndedIdsKept) _endedIds.Dequeue();
        }

        // The pending code, or null. A file that is a link, open to others, does not parse, or expires too far ahead is
        // not trusted, and the log says so (once until the file is trusted again).
        private ApiPendingPairing CheckPending(PendingFile file) {
            if (file.Refused || IsTooFarAhead(file.Pending)) {
                LogRefused(file.Unreadable);
                return null;
            }
            _refusalLogged = false;
            return file.Pending != null && file.Pending.PairedFamily != null ? DropUsed(file.Pending) : file.Pending;
        }

        // A used code is no code. Its file is left to the creator, which shows the result and then removes it, until the
        // code's own expiry; then the server removes it.
        private ApiPendingPairing DropUsed(ApiPendingPairing used) {
            if (IsExpired(used)) EndCode(used.Id);
            return null;
        }

        private void LogRefused(bool unreadable) {
            if (!_refusalLogged) Logger.Log("Local API: " + ApiPairingFile.FileName + (unreadable ? " could not be read" : " is not trusted") + ", so no pairing code is active.");
            _refusalLogged = true;
        }

        private bool IsTooFarAhead(ApiPendingPairing pending) {
            return pending != null && pending.Expires - _policy.UtcNow() > ApiPolicy.PairingCodeLifetime + ExpiryMargin;
        }

        private bool IsExpired(ApiPendingPairing pending) {
            return _policy.UtcNow() >= pending.Expires;
        }

        // Ends the code for good: its file is deleted (the creator sees that), and a file that cannot be deleted stays
        // ended in memory
        private void Burn(PairingSession session, string reason) {
            EndSession(session);
            EndCode(session.Pending.Id);
            Logger.Log("Local API: a pairing code was burned (" + reason + ")");
        }

        // The proof before use: content type, body size, body, then the proof keyed with the credential of the Origin
        // (the scripts' token without one)
        private async Task ProveAsync(HttpContext context) {
            ApiJsonBody<ProofRequest> body = await ApiJsonBody<ProofRequest>.ReadAsync(context.Request, _policy.MaxBodyBytes, ApiJsonContext.Default.ProofRequest, ApiError.InvalidPairingBody).ConfigureAwait(false);
            if (body.IsAborted) {
                context.Abort();
                return;
            }
            string origin = context.Request.Headers.Origin.ToString();
            ApiError error = body.Error ?? CheckProofRequest(body.Value, origin);
            await (error != null ? WriteErrorAsync(context, error) : WriteProofAsync(context, body.Value.ClientNonce, origin)).ConfigureAwait(false);
        }

        private static ApiError CheckProofRequest(ProofRequest request, string origin) {
            if (origin.Length != 0 && !ApiPairing.IsPairingOrigin(origin)) return ApiError.ForbiddenOrigin;
            return IsBytes(request.ClientNonce, ApiPairing.NonceBytes) ? null : ApiError.InvalidPairingBody;
        }

        private Task WriteProofAsync(HttpContext context, string clientNonce, string origin) {
            byte[] key = _credentials.ProofKey(origin);
            if (key == null) return WriteErrorAsync(context, ApiError.NotPaired);
            ProofResponse response = new ProofResponse { Proof = ApiPairing.Base64Url(ApiPairing.UseProof(key, _boundPort(), origin, clientNonce)) };
            return ThreadsEndpoints.WriteJsonAsync(context, StatusCodes.Status200OK, response, ApiJsonContext.Default.ProofResponse);
        }

        private static Func<HttpContext, Task> Fail(ApiError error) {
            return context => WriteErrorAsync(context, error);
        }

        private static Task WriteErrorAsync(HttpContext context, ApiError error) {
            context.Response.Headers.Connection = "close";
            return error.WriteAsync(context);
        }

        private sealed class PendingFile {
            public PendingFile(ApiPendingPairing pending, bool refused, bool unreadable) {
                Pending = pending;
                Refused = refused;
                Unreadable = unreadable;
            }

            public ApiPendingPairing Pending { get; }
            public bool Refused { get; }
            public bool Unreadable { get; }
        }

        private sealed class PairingSession {
            public PairingSession(ApiPendingPairing pending) {
                Pending = pending;
            }

            public ApiPendingPairing Pending { get; }
            // Every hello, answered or not
            public int HelloCount { get; set; }
            // Burned, expired or used
            public bool Ended { get; set; }
            // The Origin and nonces of each answered hello (at most MaxHellosPerCode)
            public List<(string Origin, string ClientNonce, string ServerNonce)> Answered { get; } = new List<(string, string, string)>();
        }
    }
}
