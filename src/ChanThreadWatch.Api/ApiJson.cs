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
    internal sealed partial class ApiJsonContext : JsonSerializerContext {
    }
}
