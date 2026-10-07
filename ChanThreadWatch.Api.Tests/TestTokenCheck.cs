using System;
using System.Security.Cryptography;

namespace JDP.Api {
    // The tests' check that a token is the one api-token.txt holds: its hash against ReadHash, as the server's check
    // (ApiCredentials) compares it. The library has no such call of its own; the tests of the API, ctw api-token and
    // the app's settings folder move share this one.
    internal static class TestTokenCheck {
        public static bool Verify(this ApiTokenStore store, string token) {
            byte[] expected = store.ReadHash();
            return !String.IsNullOrEmpty(token) && expected != null && CryptographicOperations.FixedTimeEquals(ApiTokenStore.Hash(token), expected);
        }
    }
}
