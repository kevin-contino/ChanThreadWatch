using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

namespace JDP.Api {
    // Who a presented token belongs to: the scripts' token (api-token.txt), or a paired browser (api-clients.txt) with
    // its family and the Origin it paired with
    internal sealed class ApiCredential {
        public static readonly ApiCredential Script = new ApiCredential(null, null);

        public ApiCredential(string family, string origin) {
            Family = family;
            Origin = origin;
        }

        // Null for the scripts' token
        public string Family { get; }
        public string Origin { get; }

        public bool IsExtension {
            get { return Family != null; }
        }
    }

    // Identifies a token against both files, each re-read on every check, comparing hashes in constant time. An
    // untrusted api-clients.txt lets no paired browser's token pass and leaves the scripts' token working; the log says
    // so once (the file's name only), and again after it was trusted in between.
    internal sealed class ApiCredentials {
        private readonly ApiTokenStore _tokens;
        private readonly ApiClientStore _clients;
        private int _refusalLogged;

        public ApiCredentials(ApiTokenStore tokens, ApiClientStore clients) {
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        }

        public ApiClientStore Clients {
            get { return _clients; }
        }

        // The credential of the token, or null. A value longer than the limit is refused before it is hashed. Every
        // stored hash is compared, also after a match.
        public ApiCredential Identify(string presented) {
            if (String.IsNullOrEmpty(presented) || presented.Length > ApiPolicy.MaxAuthorizationLength) return null;
            byte[] hash = ApiTokenStore.Hash(presented);
            ApiCredential found = Matches(_tokens.ReadHash(), hash) ? ApiCredential.Script : null;
            foreach (ApiClient client in ReadClients()) {
                if (Matches(client.Hash, hash)) found = new ApiCredential(client.Family, client.Origin);
            }
            return found;
        }

        // The key of the proof before use (the stored hash of the token): without an Origin the scripts' token's; with
        // an extension Origin the line of its family, only if the recorded Origin is the same; else null
        public byte[] ProofKey(string origin) {
            if (origin.Length == 0) return _tokens.ReadHash();
            string family = ApiPairing.FamilyOf(origin);
            foreach (ApiClient client in ReadClients()) {
                if (client.Family == family && client.Origin == origin) return client.Hash;
            }
            return null;
        }

        // The paired browsers, or none when the file is not trusted
        public IReadOnlyList<ApiClient> ReadClients() {
            IReadOnlyList<ApiClient> clients = _clients.Read();
            if (clients != null) {
                Volatile.Write(ref _refusalLogged, 0);
                return clients;
            }
            if (Interlocked.Exchange(ref _refusalLogged, 1) == 0) Logger.Log("Local API: " + ApiClientStore.FileName + " is not trusted, so no paired browser can connect.");
            return Array.Empty<ApiClient>();
        }

        private static bool Matches(byte[] stored, byte[] presented) {
            return stored != null && CryptographicOperations.FixedTimeEquals(stored, presented);
        }
    }
}
