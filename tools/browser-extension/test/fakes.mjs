// In-memory stand-ins for the browser's extension API and for fetch, plus a fake Chan Thread Watch that answers the
// pairing and proof routes as the real one does (with node:crypto, apart from the extension's WebCrypto code). Every
// secret here is a fixed fake value from ChanThreadWatch.Api.Tests/PairingVectors.json.
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const repoRoot = path.resolve(root, '..', '..');
export const vectors = JSON.parse(fs.readFileSync(path.join(repoRoot, 'ChanThreadWatch.Api.Tests', 'PairingVectors.json'), 'utf8'));
export const chrome = vectors.vectors.find((vector) => vector.name === 'chrome');
export const knownHosts = JSON.parse(fs.readFileSync(path.join(root, 'known-hosts.json'), 'utf8'));
export const hostPatterns = knownHosts.map((domain) => '*://*.' + domain + '/*');

// One fake extension API for the whole test file; reset() puts it back to its first state before each test
export function installBrowser() {
    const fake = {
        runtime: {
            id: 'test-extension',
            getURL: (file) => chrome.origin + '/' + file,
            getManifest: () => ({ optional_host_permissions: hostPatterns }),
            onInstalled: listeners(),
            onStartup: listeners(),
            onMessage: listeners(),
            sendMessage: (message) => sendMessage(fake, message),
            openOptionsPage: async () => {},
            // Counts the reads, as Chrome only stops logging a failed create once its callback read lastError
            get lastError() {
                fake.lastErrorReads++;
                return undefined;
            }
        },
        storage: { local: storageArea(() => fake) },
        contextMenus: {
            onClicked: listeners(),
            // Removes at once and answers on a later turn, as the browser does, so builds that are not serialized
            // interleave (remove, remove, create, create, create, create). Every create adds an item.
            removeAll: async () => {
                if (fake.removeAllFailures > 0) {
                    fake.removeAllFailures--;
                    throw new Error('Test removeAll failure');
                }
                fake.menus = [];
                await new Promise((resolve) => setImmediate(resolve));
            },
            create: (item, callback) => {
                fake.menus.push(item);
                if (typeof callback === 'function') setImmediate(callback);
            }
        },
        action: { setBadgeText: async ({ text }) => { fake.badges.push(text); } },
        permissions: {
            request: async (request) => {
                fake.permissionRequests.push(request);
                return fake.grant;
            }
        },
        tabs: {
            query: async (query) => {
                fake.tabQueries.push(query);
                return fake.openTabs;
            }
        },
        reset() {
            fake.store = {};
            fake.menus = [];
            fake.badges = [];
            fake.permissionRequests = [];
            fake.tabQueries = [];
            fake.openTabs = [];
            fake.grant = true;
            fake.failStorage = false;
            fake.failSet = () => false;
            fake.removeAllFailures = 0;
            fake.storageSets = [];
            fake.lastErrorReads = 0;
        }
    };
    fake.reset();
    globalThis.chrome = fake;
    return fake;
}

function listeners() {
    const list = [];
    return { list, addListener: (listener) => list.push(listener) };
}

// With failStorage set, every call fails, as a browser does with a broken or full profile; failSet(items) picks single
// writes to fail
function storageArea(fakeOf) {
    const store = () => {
        if (fakeOf().failStorage) throw new Error('Test storage failure');
        return fakeOf().store;
    };
    return {
        get: async (keys) => Object.fromEntries([].concat(keys).filter((key) => key in store()).map((key) => [key, store()[key]])),
        set: async (items) => {
            if (fakeOf().failSet(items)) throw new Error('Test storage write failure');
            Object.assign(store(), structuredClone(items));
            fakeOf().storageSets.push(structuredClone(items));
        },
        remove: async (keys) => { for (const key of [].concat(keys)) delete store()[key]; }
    };
}

// As the browser does: the first listener that returns true answers through sendResponse
function sendMessage(fake, message) {
    return new Promise((resolve, reject) => {
        const answered = fake.runtime.onMessage.list.some((listener) => listener(structuredClone(message), { id: fake.runtime.id }, resolve) === true);
        if (!answered) reject(new Error('No listener answered'));
    });
}

// Records every request; handler(request) gives { status, body, headers } or throws (a network error)
export function installFetch(handler) {
    const requests = [];
    globalThis.fetch = async (url, init) => {
        const request = { url, init, headers: init.headers, body: JSON.parse(init.body) };
        requests.push(request);
        const answer = await handler(request);
        // A body whose read fails, as fetch's does when the request's signal ends during the read
        if (answer.jsonError) return { status: answer.status, headers: new Headers(answer.headers), json: async () => { throw answer.jsonError; } };
        return new Response(answer.body === undefined ? '' : JSON.stringify(answer.body), { status: answer.status, headers: answer.headers || {} });
    };
    return requests;
}

export function mac(key, lines) {
    return crypto.createHmac('sha256', key).update(lines.join('\n'), 'utf8').digest('base64url');
}

export function tokenHash(token) {
    return crypto.createHash('sha256').update(token, 'utf8').digest();
}

export function problem(status, code, headers) {
    return { status, body: { type: 'about:blank', title: 'Test', status, code }, headers };
}

// A fake Chan Thread Watch: the pairing key of the chrome vector's code, and the vector's token once paired. It binds
// every proof to the port it was asked on, as the real one binds them to its own address. Options change single
// answers (a wrong P1 or P3, an add status per address, or a use proof made for another port, as a program that replays
// a proof it saw on the old port).
export function fakeServer(options = {}) {
    const port = chrome.port;
    const key = Buffer.from(options.keyHex || chrome.keyHex, 'hex');
    const state = { token: options.token || chrome.token, addStatuses: options.addStatuses || [], adds: 0 };
    const routes = {
        '/api/v1/proof': (request) => {
            const proofAddress = '127.0.0.1:' + (options.proofPort || new URL(request.url).port);
            const proof = mac(tokenHash(state.token), ['ctw-proof-v1', proofAddress, chrome.origin, request.body.clientNonce]);
            return { status: 200, body: { proof: options.wrongUseProof ? flip(proof) : proof } };
        },
        '/api/v1/pairing': (request) => (request.body.step === 'hello' ? hello(request) : finish(request)),
        '/api/v1/threads': () => addAnswer(state.addStatuses[state.adds++] || 201)
    };
    function hello(request) {
        const lines = ['ctw-pair-v1', 'server-hello', addressOf(request), chrome.origin, chrome.pairingId, request.body.clientNonce, chrome.serverNonce, chrome.serverName];
        return { status: 200, body: { pairingId: chrome.pairingId, salt: chrome.salt, serverNonce: chrome.serverNonce, serverName: chrome.serverName, proof: mac(key, lines) } };
    }
    function finish(request) {
        const lines = ['ctw-pair-v1', 'server-finish', addressOf(request), chrome.origin, chrome.pairingId, request.body.clientNonce, chrome.serverNonce, state.token];
        const proof = mac(key, lines);
        return { status: 200, body: { token: state.token, proof: options.wrongFinishProof ? flip(proof) : proof } };
    }
    return { port, handle: (request) => routes[new URL(request.url).pathname](request), state };
}

function addressOf(request) {
    return '127.0.0.1:' + new URL(request.url).port;
}

// An add status; 'network' for a request that fails as fetch does when the program is gone, 'timeout' for one that
// AbortSignal.timeout ended, and 'slow201' for a 201 whose body read times out
function addAnswer(status) {
    if (status === 'network') throw new TypeError('fetch failed');
    if (status === 'timeout') throw new DOMException('The operation timed out.', 'TimeoutError');
    if (status === 'slow201') return { status: 201, jsonError: new DOMException('The operation timed out.', 'TimeoutError') };
    if (status === 201) return { status: 201, body: { id: '1' } };
    if (status === 429) return problem(429, 'add_rate_limited', { 'Retry-After': '20' });
    return problem(status, { 401: 'unauthorized', 403: 'forbidden_origin', 409: 'already_watched', 422: 'unknown_host', 503: 'unavailable' }[status]);
}

// The same proof with its first character changed, so it is still in the right form but wrong
function flip(proof) {
    return (proof[0] === 'A' ? 'B' : 'A') + proof.slice(1);
}

export function pairedStore(fake) {
    fake.store = { port: chrome.port, token: chrome.token, serverName: chrome.serverName, pairedAt: '2026-10-08T00:00:00.000Z' };
}

export function authorizationOf(request) {
    return Object.keys(request.headers).find((name) => name.toLowerCase() === 'authorization');
}
