using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JDP.Api {
    // The pairing protocol v1 of the browser extension (MP-7b design, section 3): the extension Origins, the code, the
    // key stretched from it, and the HMAC proofs. A proof is HMAC-SHA256 over its lines joined with "\n"; every line is
    // a fixed label, the address, a checked Origin, base64url text or the checked server name, so no line can hold a
    // line break and two different messages can never join to the same bytes. The tests check every value against
    // PairingVectors.json, which the extension's tests read too.
    internal static class ApiPairing {
        // The Chrome extension's id, fixed by the public key in its manifest; any other Chrome id is refused
        public const string ChromeExtensionId = "eifjifphdncjkkolefjepjhdlmcdbndh";
        public const string ChromeFamily = "chrome";
        public const string FirefoxFamily = "firefox";
        // In the order api-clients.txt lists them
        public static readonly string[] Families = { ChromeFamily, FirefoxFamily };
        public const string ChromeScheme = "chrome-extension://";
        public const string FirefoxScheme = "moz-extension://";
        // Crockford base32: no I, L, O or U
        public const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public const int CodeLength = 8;
        public const int KeyBytes = 32;
        public const int NonceBytes = 32;
        public const int ProofBytes = 32;
        public const int IdBytes = 16;
        public const int SaltBytes = 16;
        public const int MaxServerNameLength = 80;
        private const int ChromeIdLength = 32;
        private const string UuidPattern = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx";
        private const string PairLabel = "ctw-pair-v1";
        private const string ProofLabel = "ctw-proof-v1";

        // "chrome" or "firefox" for an Origin of either extension form, else null. The Chrome form is any id of 32
        // letters a-p here (IsPairingOrigin pins it); the Firefox form is a lowercase UUID. No trailing slash, no other
        // case.
        public static string FamilyOf(string origin) {
            if (origin == null) return null;
            if (IsForm(origin, ChromeScheme, IsChromeId)) return ChromeFamily;
            return IsForm(origin, FirefoxScheme, IsLowercaseUuid) ? FirefoxFamily : null;
        }

        private static bool IsForm(string origin, string scheme, Func<string, bool> isId) {
            return origin.StartsWith(scheme, StringComparison.Ordinal) && isId(origin.Substring(scheme.Length));
        }

        // An Origin that may pair and ask for a proof: Firefox's form, or exactly the pinned Chrome id
        public static bool IsPairingOrigin(string origin) {
            string family = FamilyOf(origin);
            return family == FirefoxFamily || (family == ChromeFamily && origin == ChromeScheme + ChromeExtensionId);
        }

        private static bool IsChromeId(string id) {
            if (id.Length != ChromeIdLength) return false;
            foreach (char c in id) {
                if (c < 'a' || c > 'p') return false;
            }
            return true;
        }

        // 8-4-4-4-12 lowercase hex digits
        private static bool IsLowercaseUuid(string text) {
            if (text.Length != UuidPattern.Length) return false;
            for (int i = 0; i < text.Length; i++) {
                if (UuidPattern[i] == '-' ? text[i] != '-' : !Char.IsAsciiHexDigitLower(text[i])) return false;
            }
            return true;
        }

        // 1 to 80 characters, none of them a control, format (such as a direction override), line or paragraph
        // separator, surrogate, private-use or unassigned character
        public static bool IsValidServerName(string name) {
            if (String.IsNullOrEmpty(name) || name.Length > MaxServerNameLength) return false;
            foreach (char c in name) {
                if (!IsPrintable(CharUnicodeInfo.GetUnicodeCategory(c))) return false;
            }
            return true;
        }

        private static readonly UnicodeCategory[] _unprintable = {
            UnicodeCategory.Control, UnicodeCategory.Format, UnicodeCategory.LineSeparator, UnicodeCategory.ParagraphSeparator,
            UnicodeCategory.Surrogate, UnicodeCategory.PrivateUse, UnicodeCategory.OtherNotAssigned
        };

        private static bool IsPrintable(UnicodeCategory category) {
            return Array.IndexOf(_unprintable, category) < 0;
        }

        // 8 symbols of the alphabet, each from the system's cryptographic random source
        public static string NewCode() {
            char[] code = new char[CodeLength];
            for (int i = 0; i < code.Length; i++) {
                code[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
            }
            return new string(code);
        }

        // XXXX-XXXX, as the creator shows it
        public static string FormatCode(string code) {
            return code.Substring(0, 4) + "-" + code.Substring(4);
        }

        // K = PBKDF2-HMAC-SHA256(code as ASCII, salt, 600000, 32 bytes). Slow by design; the server never runs it (it
        // reads the derived key from api-pairing.txt).
        public static byte[] DeriveKey(string code, byte[] salt) {
            return Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(code), salt, ApiPolicy.PairingIterations, HashAlgorithmName.SHA256, KeyBytes);
        }

        public static string NewNonce() {
            return Base64Url(RandomNumberGenerator.GetBytes(NonceBytes));
        }

        public static string Address(int port) {
            return "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        }

        // P1, which the extension checks before it sends anything more
        public static byte[] ServerHelloProof(byte[] key, int port, string origin, string pairingId, string clientNonce, string serverNonce, string serverName) {
            return Mac(key, PairLabel, "server-hello", Address(port), origin, pairingId, clientNonce, serverNonce, serverName);
        }

        // P2, which the server checks with the Origin of the finish
        public static byte[] ClientFinishProof(byte[] key, int port, string origin, string pairingId, string clientNonce, string serverNonce) {
            return Mac(key, PairLabel, "client-finish", Address(port), origin, pairingId, clientNonce, serverNonce);
        }

        // P3, which the extension checks before it saves the token
        public static byte[] ServerFinishProof(byte[] key, int port, string origin, string pairingId, string clientNonce, string serverNonce, string token) {
            return Mac(key, PairLabel, "server-finish", Address(port), origin, pairingId, clientNonce, serverNonce, token);
        }

        // The proof before use, keyed with SHA-256 of the token (the stored hash); the origin is "" without an Origin
        public static byte[] UseProof(byte[] tokenHash, int port, string origin, string clientNonce) {
            return Mac(tokenHash, ProofLabel, Address(port), origin, clientNonce);
        }

        private static byte[] Mac(byte[] key, params string[] lines) {
            return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(String.Join("\n", lines)));
        }

        public static string Base64Url(byte[] bytes) {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // The bytes of base64url text without padding that encodes exactly that many bytes in its one canonical form,
        // else null
        public static byte[] FromBase64Url(string text, int byteCount) {
            if (!IsBase64UrlText(text, (byteCount * 4 + 2) / 3)) return null;
            byte[] bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
            return Base64Url(bytes) == text ? bytes : null;
        }

        private static bool IsBase64UrlText(string text, int length) {
            if (text == null || text.Length != length) return false;
            foreach (char c in text) {
                if (!IsBase64UrlChar(c)) return false;
            }
            return true;
        }

        private static bool IsBase64UrlChar(char c) {
            return Char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_';
        }
    }
}
