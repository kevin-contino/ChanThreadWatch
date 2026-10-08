// The pairing protocol v1: the code, the key stretched from it, the HMAC proofs and the
// checks of the server's answers. No requests here; api.js sends them. The values match the server's
// (src/ChanThreadWatch.Api/ApiPairing.cs), and the tests check both against ChanThreadWatch.Api.Tests/PairingVectors.json.

// A protocol constant, never a server value, so a program that is not Chan Thread Watch cannot ask for a larger
// derivation
export const PAIRING_ITERATIONS = 600000;
const NONCE_BYTES = 32;
export const PROOF_BYTES = 32;
const ID_BYTES = 16;
const SALT_BYTES = 16;
const MAX_SERVER_NAME_LENGTH = 80;
const PAIR_LABEL = 'ctw-pair-v1';
const PROOF_LABEL = 'ctw-proof-v1';
// Crockford base32: no I, L, O or U
const CODE = /^[0-9A-HJKMNP-TV-Z]{8}$/;
const TOKEN = /^ctwe_[A-Za-z0-9_-]{43}$/;
const BASE64URL = /^[A-Za-z0-9_-]*$/;
// What the server's name check refuses: control, format, separator, private-use and unassigned characters, and any
// surrogate (the server checks UTF-16 units, so a pair is refused too)
const UNPRINTABLE = /[\p{C}\p{Zl}\p{Zp}]/u;
const SURROGATE = /[\uD800-\uDFFF]/;
const encoder = new TextEncoder();

// The code as typed: spaces and hyphens dropped, upper case, O read as 0 and I, L as 1; null for any other length or
// symbol, so nothing is sent
export function normalizeCode(text) {
    if (typeof text !== 'string') return null;
    const code = text.replace(/[\s-]/g, '').toUpperCase().replace(/O/g, '0').replace(/[IL]/g, '1');
    return CODE.test(code) ? code : null;
}

export function isToken(text) {
    return typeof text === 'string' && TOKEN.test(text);
}

export function isValidServerName(name) {
    return typeof name === 'string' && name.length >= 1 && name.length <= MAX_SERVER_NAME_LENGTH && !UNPRINTABLE.test(name) && !SURROGATE.test(name);
}

export function base64Url(bytes) {
    let binary = '';
    for (const byte of bytes) binary += String.fromCharCode(byte);
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// The bytes of base64url text without padding that encodes exactly that many bytes, in its one canonical form, else null
export function fromBase64Url(text, byteCount) {
    if (typeof text !== 'string' || text.length !== Math.ceil(byteCount * 4 / 3) || !BASE64URL.test(text)) return null;
    const bytes = Uint8Array.from(atob(text.replace(/-/g, '+').replace(/_/g, '/')), (c) => c.charCodeAt(0));
    return base64Url(bytes) === text ? bytes : null;
}

export function randomNonce() {
    return base64Url(crypto.getRandomValues(new Uint8Array(NONCE_BYTES)));
}

// K = PBKDF2-HMAC-SHA256(code as ASCII, salt, 600000, 32 bytes)
export async function deriveKey(code, salt) {
    const material = await crypto.subtle.importKey('raw', encoder.encode(code), 'PBKDF2', false, ['deriveBits']);
    const bits = await crypto.subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-256', salt, iterations: PAIRING_ITERATIONS }, material, 256);
    return new Uint8Array(bits);
}

// The key of the proof before use: SHA-256 of the token, which is what the server keeps
export async function tokenKey(token) {
    return new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(token)));
}

function hmacKey(keyBytes) {
    return crypto.subtle.importKey('raw', keyBytes, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign', 'verify']);
}

// HMAC-SHA256 over the lines joined with "\n"
export async function sign(keyBytes, lines) {
    return new Uint8Array(await crypto.subtle.sign('HMAC', await hmacKey(keyBytes), encoder.encode(lines.join('\n'))));
}

// Compared in constant time by the browser; a missing proof never matches
export async function verify(keyBytes, lines, proof) {
    if (!proof) return false;
    return crypto.subtle.verify('HMAC', await hmacKey(keyBytes), proof, encoder.encode(lines.join('\n')));
}

function address(port) {
    return '127.0.0.1:' + port;
}

// P1; the session holds port, origin, pairingId, clientNonce, serverNonce and serverName
export function serverHelloLines(s) {
    return [PAIR_LABEL, 'server-hello', address(s.port), s.origin, s.pairingId, s.clientNonce, s.serverNonce, s.serverName];
}

// P2
export function clientFinishLines(s) {
    return [PAIR_LABEL, 'client-finish', address(s.port), s.origin, s.pairingId, s.clientNonce, s.serverNonce];
}

// P3
export function serverFinishLines(s, token) {
    return [PAIR_LABEL, 'server-finish', address(s.port), s.origin, s.pairingId, s.clientNonce, s.serverNonce, token];
}

// The proof before use
export function useProofLines(port, origin, clientNonce) {
    return [PROOF_LABEL, address(port), origin, clientNonce];
}

// A JSON object with exactly these members, each a string
export function hasMembers(body, names) {
    return isObject(body) && Object.keys(body).length === names.length && names.every((name) => typeof body[name] === 'string');
}

function isObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}

// The hello answer with its byte values, or null when it is not in the protocol's form
export function parseHello(body) {
    if (!hasMembers(body, ['pairingId', 'salt', 'serverNonce', 'serverName', 'proof'])) return null;
    const hello = { pairingId: body.pairingId, salt: fromBase64Url(body.salt, SALT_BYTES), serverNonce: body.serverNonce, serverName: body.serverName, proof: fromBase64Url(body.proof, PROOF_BYTES) };
    return isHelloForm(hello) ? hello : null;
}

function isHelloForm(hello) {
    return fromBase64Url(hello.pairingId, ID_BYTES) !== null && fromBase64Url(hello.serverNonce, NONCE_BYTES) !== null && hello.salt !== null && isValidServerName(hello.serverName);
}

// The finish answer, or null when it is not in the protocol's form
export function parseFinish(body) {
    if (!hasMembers(body, ['token', 'proof']) || !isToken(body.token)) return null;
    const proof = fromBase64Url(body.proof, PROOF_BYTES);
    return proof ? { token: body.token, proof } : null;
}
