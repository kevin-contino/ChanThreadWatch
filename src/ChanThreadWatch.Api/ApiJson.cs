using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JDP.Api {
    // POST /api/v1/threads. Only "url" (maintainer decision D7, G11: never a folder); any other member, a member
    // in another case, a duplicate member or a value of another type fails the parse.
    internal sealed class AddThreadRequest {
        [JsonRequired]
        public string Url { get; set; }
    }

    internal sealed class ApiThread {
        public string Id { get; set; }
        public string Url { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        // running, waiting, stopped or notFound
        public string State { get; set; }
        // Null unless the thread is stopped
        public string StopReason { get; set; }
        public DateTimeOffset AddedOn { get; set; }
        // The id of the thread it was auto-followed from, or null
        public string AddedFrom { get; set; }
    }

    internal sealed class ApiThreadList {
        public List<ApiThread> Threads { get; set; }
    }

    // application/problem+json (RFC 9457) with a stable "code"; never an exception text
    internal sealed class ApiProblemBody {
        public string Type { get; set; }
        public string Title { get; set; }
        public int Status { get; set; }
        public string Code { get; set; }
    }

    // POST /api/v1/pairing: "step" is "hello" with "clientNonce" only, or "finish" with all four others. Any other
    // member, case, duplicate or type fails the parse; the step rules are checked after it. Each setter counts its
    // member, so a member that is present with the value null still counts (a hello with "proof": null is refused).
    internal sealed class PairingRequest {
        private string _step;
        private string _clientNonce;
        private string _pairingId;
        private string _serverNonce;
        private string _proof;

        [JsonRequired]
        public string Step {
            get { return _step; }
            set { _step = value; MemberCount++; }
        }

        public string ClientNonce {
            get { return _clientNonce; }
            set { _clientNonce = value; MemberCount++; }
        }

        public string PairingId {
            get { return _pairingId; }
            set { _pairingId = value; MemberCount++; }
        }

        public string ServerNonce {
            get { return _serverNonce; }
            set { _serverNonce = value; MemberCount++; }
        }

        public string Proof {
            get { return _proof; }
            set { _proof = value; MemberCount++; }
        }

        // Not a JSON member (internal)
        internal int MemberCount { get; private set; }
    }

    internal sealed class PairingHelloResponse {
        public string PairingId { get; set; }
        public string Salt { get; set; }
        public string ServerNonce { get; set; }
        public string ServerName { get; set; }
        public string Proof { get; set; }
    }

    internal sealed class PairingFinishResponse {
        public string Token { get; set; }
        public string Proof { get; set; }
    }

    // POST /api/v1/proof
    internal sealed class ProofRequest {
        [JsonRequired]
        public string ClientNonce { get; set; }
    }

    internal sealed class ProofResponse {
        public string Proof { get; set; }
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Disallow,
        MaxDepth = 4)]
    [JsonSerializable(typeof(AddThreadRequest))]
    [JsonSerializable(typeof(ApiThread))]
    [JsonSerializable(typeof(ApiThreadList))]
    [JsonSerializable(typeof(ApiProblemBody))]
    [JsonSerializable(typeof(PairingRequest))]
    [JsonSerializable(typeof(PairingHelloResponse))]
    [JsonSerializable(typeof(PairingFinishResponse))]
    [JsonSerializable(typeof(ProofRequest))]
    [JsonSerializable(typeof(ProofResponse))]
    internal sealed partial class ApiJsonContext : JsonSerializerContext {
    }
}
