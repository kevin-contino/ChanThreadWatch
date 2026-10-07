using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace JDP.Api {
    // A paired browser extension: its family (chrome or firefox), the Origin it paired with, its token's SHA-256 hash and
    // when it paired
    internal sealed class ApiClient {
        public ApiClient(string family, string origin, byte[] hash, DateTimeOffset pairedAt) {
            Family = family;
            Origin = origin;
            Hash = hash;
            PairedAt = pairedAt;
        }

        public string Family { get; }
        public string Origin { get; }
        public byte[] Hash { get; }
        public DateTimeOffset PairedAt { get; }
    }

    // The paired browsers (MP-7b design E2): api-clients.txt in the settings folder, owner-only and written as
    // api-token.txt is (ApiTokenStore), re-read on each check. Line 1 is the format version "1"; then at most one line
    // per family, "<family> <origin> sha256:<64 hex> <yyyy-MM-ddTHH:mm:ssZ>". A link, access for others, more than
    // 1 KiB, or any line that does not parse, a family twice or an Origin that is not its family's (a Chrome Origin
    // other than the pinned id) makes the whole file untrusted: no paired browser's token passes. Only hashes are
    // kept; a token is shown once, in the pairing's answer.
    internal sealed class ApiClientStore {
        public const string FileName = "api-clients.txt";
        public const string TokenPrefix = "ctwe_";
        private const string FormatVersion = "1";
        private const int MaxFileBytes = 1024;
        private const int TokenBytes = 32;
        private const string HashPrefix = "sha256:";
        private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
        private const string WriteFailure = "The paired browsers file could not be written: ";
        private const string ReadFailure = "The paired browsers file could not be read: ";

        // Pair and Remove read, then write the whole file; in one process they run one at a time (another process,
        // such as ctw, can still interleave; the design accepts that)
        private static readonly object _writeLock = new object();

        private readonly string _path;

        public ApiClientStore(string settingsFolder) {
            if (String.IsNullOrEmpty(settingsFolder)) throw new ArgumentException("No settings folder.", nameof(settingsFolder));
            _path = System.IO.Path.Combine(settingsFolder, FileName);
        }

        public string Path {
            get { return _path; }
        }

        // The paired browsers: an empty list when there is no file, null when the file is not trusted or could not be
        // read (no paired browser passes then)
        public IReadOnlyList<ApiClient> Read() {
            TrustedRead status;
            string text = ApiTokenStore.ReadTrustedText(_path, MaxFileBytes, out status);
            if (text == null) return status == TrustedRead.Missing ? Array.Empty<ApiClient>() : null;
            return Parse(text);
        }

        // Makes a token for the extension of the Origin and puts its line in place of its family's older one, whose
        // token stops working at once. A file with untrusted content (a link, access for others, a line that does not
        // parse) is replaced by this one line, and the log says so: the other family pairs again, and a damaged file
        // never blocks pairing. A file that could not be read (in use, access denied) is never replaced: that throws,
        // so a passing failure cannot drop the other family's line. Returns the token, which is never saved. Throws
        // ApiTokenException when the file cannot be read, or written with owner-only access.
        public string Pair(string origin, DateTimeOffset pairedAt) {
            if (!ApiPairing.IsPairingOrigin(origin)) throw new ArgumentException("Not an Origin that can pair.", nameof(origin));
            string family = ApiPairing.FamilyOf(origin);
            string token = TokenPrefix + ApiPairing.Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));
            ApiClient client = new ApiClient(family, origin, ApiTokenStore.Hash(token), pairedAt.ToUniversalTime());
            lock (_writeLock) {
                IReadOnlyList<ApiClient> clients = ReadForWrite();
                Write((clients ?? Array.Empty<ApiClient>()).Where(other => other.Family != family).Append(client));
                if (clients == null) Logger.Log("Local API: an untrusted " + FileName + " was replaced by a new pairing.");
            }
            return token;
        }

        // Removes the family's line (unpair); its token stops working at once. False, and nothing written, when the
        // family is not paired or the file is not trusted. Throws ApiTokenException when the file cannot be read or
        // written.
        public bool Remove(string family) {
            lock (_writeLock) {
                IReadOnlyList<ApiClient> clients = ReadForWrite();
                if (clients == null || !clients.Any(client => client.Family == family)) return false;
                Write(clients.Where(client => client.Family != family));
                return true;
            }
        }

        // The lines, an empty list for no file, null for untrusted content; throws when the file could not be read
        private IReadOnlyList<ApiClient> ReadForWrite() {
            TrustedRead status;
            string text = ApiTokenStore.ReadTrustedText(_path, MaxFileBytes, out status);
            if (status == TrustedRead.Failed) throw new ApiTokenException(ReadFailure + _path);
            if (text == null) return status == TrustedRead.Missing ? Array.Empty<ApiClient>() : null;
            return Parse(text);
        }

        private void Write(IEnumerable<ApiClient> clients) {
            IEnumerable<string> lines = clients.OrderBy(client => Array.IndexOf(ApiPairing.Families, client.Family)).Select(FormatLine);
            ApiTokenStore.WriteOwnerOnlyText(_path, String.Concat(new[] { FormatVersion }.Concat(lines).Select(line => line + "\n")), WriteFailure);
        }

        private static string FormatLine(ApiClient client) {
            return client.Family + " " + client.Origin + " " + HashPrefix + Convert.ToHexStringLower(client.Hash) + " " + client.PairedAt.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);
        }

        // Null unless every line is valid: the version, then at most one valid line per family, each ending with "\n"
        internal static IReadOnlyList<ApiClient> Parse(string text) {
            if (!text.EndsWith("\n", StringComparison.Ordinal)) return null;
            string[] lines = text.Substring(0, text.Length - 1).Split('\n');
            if (lines[0] != FormatVersion || lines.Length > ApiPairing.Families.Length + 1) return null;
            List<ApiClient> clients = lines.Skip(1).Select(ParseLine).ToList();
            return IsValidSet(clients) ? clients : null;
        }

        private static bool IsValidSet(List<ApiClient> clients) {
            return !clients.Contains(null) && clients.Select(client => client.Family).Distinct().Count() == clients.Count;
        }

        private static ApiClient ParseLine(string line) {
            string[] parts = line.Split(' ');
            if (parts.Length != 4 || !IsOriginOfFamily(parts[1], parts[0])) return null;
            byte[] hash = ApiTokenStore.ParseHash(parts[2]);
            DateTimeOffset pairedAt;
            bool dated = DateTimeOffset.TryParseExact(parts[3], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out pairedAt);
            return hash != null && dated ? new ApiClient(parts[0], parts[1], hash, pairedAt) : null;
        }

        private static bool IsOriginOfFamily(string origin, string family) {
            return ApiPairing.IsPairingOrigin(origin) && ApiPairing.FamilyOf(origin) == family;
        }
    }
}
