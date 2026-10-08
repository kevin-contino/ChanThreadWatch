// Every request to the local API goes through this module: POST only, to http://127.0.0.1:<port> only, without
// cookies, cache or redirects, with a 10 second timeout. The token is sent only after POST /api/v1/proof answered a
// proof that only the Chan Thread Watch holding its hash can make; it lives in storage.local only and is never shown or
// put in a URL.
import { ext, extensionOrigin } from './browser.js';
import { ADD_TIMEOUT_TEXT, CODE_FORMAT_TEXT, CODE_MISMATCH_TEXT, NOT_PAIRED_TEXT, notTheServerText, problemText, unreachableText } from './errors.js';
import * as protocol from './pairing.js';

const DEFAULT_PORT = 47710;
export const TIMEOUT_MS = 10000;
const DEFAULT_RETRY_SECONDS = 60;
const PAIRING_PATH = '/api/v1/pairing';
const PROOF_PATH = '/api/v1/proof';
const THREADS_PATH = '/api/v1/threads';
const PAIRING_KEYS = ['token', 'serverName', 'pairedAt'];
// The problems of the proof route that come from Chan Thread Watch itself; any other answer is not its proof
const PROOF_PROBLEMS = ['not_paired', 'forbidden_origin'];

// A failure the user is told about: kind is unreachable, timeout, mismatch, problem (an API answer) or local
export class ApiFailure extends Error {
    constructor(message, kind, status = 0, code = '') {
        super(message);
        this.kind = kind;
        this.status = status;
        this.code = code;
    }
}

export function isValidPort(port) {
    return Number.isInteger(port) && port >= 1024 && port <= 65535;
}

function apiUrl(port, path) {
    if (!isValidPort(port)) throw new RangeError('The port must be from 1024 to 65535');
    return 'http://127.0.0.1:' + port + path;
}

// The status, Retry-After in seconds and the JSON body (null when the body is not JSON, and never read for a 201, an
// added thread). A network error or refusal is an ApiFailure of kind unreachable, a timeout one of kind timeout.
export async function postJson(port, path, body, token) {
    const response = await send(apiUrl(port, path), requestInit(body, token), port);
    const json = response.status === 201 ? null : await readJson(response, port);
    return { status: response.status, retryAfter: retryAfterSeconds(response.headers.get('Retry-After')), body: json };
}

function requestInit(body, token) {
    const headers = { 'Content-Type': 'application/json' };
    if (token) headers.Authorization = 'Bearer ' + token;
    return { method: 'POST', headers, body: JSON.stringify(body), credentials: 'omit', cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(TIMEOUT_MS) };
}

// fetch rejects with the signal's TimeoutError DOMException when AbortSignal.timeout ends the request, and with a
// TypeError when the connection is refused or fails
async function send(url, init, port) {
    try {
        return await fetch(url, init);
    } catch (error) {
        throw unreachable(error, port);
    }
}

// A body that is not JSON is null; a timeout or abort while it is read is the same failure as one before the answer
async function readJson(response, port) {
    try {
        return await response.json();
    } catch (error) {
        if (isAbort(error)) throw unreachable(error, port);
        return null;
    }
}

function unreachable(error, port) {
    return new ApiFailure(unreachableText(port), isAbort(error) ? 'timeout' : 'unreachable');
}

function isAbort(error) {
    return error instanceof Error && (error.name === 'AbortError' || error.name === 'TimeoutError');
}

function retryAfterSeconds(header) {
    return /^\d{1,6}$/.test(header || '') ? Number(header) : DEFAULT_RETRY_SECONDS;
}

function failureOf(result) {
    const code = codeOf(result.body);
    return new ApiFailure(problemText(result.status, code, result.retryAfter), 'problem', result.status, code);
}

function codeOf(body) {
    return body && typeof body.code === 'string' ? body.code : '';
}

// The saved pairing, or null when this browser is not paired (or what is saved is not in its form)
export async function loadPairing() {
    const saved = await ext().storage.local.get(['port', 'token', 'serverName']);
    const valid = isValidPort(saved.port) && protocol.isToken(saved.token) && protocol.isValidServerName(saved.serverName);
    return valid ? { port: saved.port, token: saved.token, serverName: saved.serverName } : null;
}

export async function savedPort() {
    const saved = await ext().storage.local.get('port');
    return isValidPort(saved.port) ? saved.port : DEFAULT_PORT;
}

export async function forgetPairing() {
    await ext().storage.local.remove(PAIRING_KEYS);
}

// The proof before use: the server must prove it holds this browser's token, bound to the port and this Origin.
// Nothing else is sent before it matched.
export async function checkServer(pairing) {
    const clientNonce = protocol.randomNonce();
    const result = await postJson(pairing.port, PROOF_PATH, { clientNonce });
    if (result.status !== 200) throw proofFailure(result, pairing.port);
    const proof = protocol.hasMembers(result.body, ['proof']) ? protocol.fromBase64Url(result.body.proof, protocol.PROOF_BYTES) : null;
    const lines = protocol.useProofLines(pairing.port, extensionOrigin(), clientNonce);
    if (!(await protocol.verify(await protocol.tokenKey(pairing.token), lines, proof))) throw new ApiFailure(notTheServerText(pairing.port), 'mismatch');
}

function proofFailure(result, port) {
    const failure = failureOf(result);
    return PROOF_PROBLEMS.includes(failure.code) || result.status === 429 ? failure : new ApiFailure(notTheServerText(port), 'mismatch');
}

// Adds the threads in order after one proof. Stops at the first 429 (the API allows 30 adds a minute), at 503
// unavailable, when the program can no longer be reached, and at an answer that says the pairing no longer works (401,
// 403). An add that timed out is failed, as the program may have added it. Each outcome is added, already (watched),
// failed or stop; notSent counts the addresses not added, already watched or failed (the one that stopped the batch and
// those after it). After each add the progress is saved, which also keeps a Chrome service worker from stopping as idle
// during a long batch.
export async function watchUrls(urls) {
    const pairing = await loadPairing();
    if (!pairing) throw new ApiFailure(NOT_PAIRED_TEXT, 'local');
    await checkServer(pairing);
    const summary = { added: 0, already: 0, failed: 0, notSent: 0, stop: null, last: null };
    for (const [index, url] of urls.entries()) {
        summary.last = await addThread(pairing, url);
        if (summary.last.kind === 'stop') break;
        summary[summary.last.kind]++;
        await saveProgress('Adding ' + (index + 1) + ' of ' + urls.length + '.');
    }
    summary.notSent = urls.length - summary.added - summary.already - summary.failed;
    summary.stop = summary.notSent > 0 ? summary.last : null;
    return summary;
}

// Only there to keep the worker busy; a failed write changes nothing in the batch
async function saveProgress(text) {
    try {
        await ext().storage.local.set({ lastResult: text });
    } catch {
        // The result is saved at the end of the batch
    }
}

async function addThread(pairing, url) {
    let result;
    try {
        result = await postJson(pairing.port, THREADS_PATH, { url }, pairing.token);
    } catch (error) {
        if (!(error instanceof ApiFailure)) throw error;
        return networkOutcome(error);
    }
    return outcomeOf(result);
}

function networkOutcome(error) {
    if (error.kind === 'timeout') return { kind: 'failed', status: 0, text: ADD_TIMEOUT_TEXT };
    return { kind: 'stop', status: 0, text: error.message };
}

function outcomeOf(result) {
    if (result.status === 201) return { kind: 'added' };
    const failure = failureOf(result);
    return { kind: outcomeKind(result.status, failure.code), status: result.status, retryAfter: result.retryAfter, text: failure.message };
}

function outcomeKind(status, code) {
    if (status === 409 && code === 'already_watched') return 'already';
    return stopsBatch(status, code) ? 'stop' : 'failed';
}

function stopsBatch(status, code) {
    return [401, 403, 429].includes(status) || (status === 503 && code === 'unavailable');
}

// Pairing step 1 (in the options page): hello, then P1 checked with the key from the typed code. A wrong code or a
// program that does not know the code ends it here; nothing more is sent. The session is kept for the confirm.
export async function startPairing(port, codeText) {
    const code = protocol.normalizeCode(codeText);
    if (!code) throw new ApiFailure(CODE_FORMAT_TEXT, 'local');
    const session = { port, origin: extensionOrigin(), clientNonce: protocol.randomNonce() };
    const result = await postJson(port, PAIRING_PATH, { step: 'hello', clientNonce: session.clientNonce });
    if (result.status !== 200) throw failureOf(result);
    const hello = protocol.parseHello(result.body);
    if (!hello) throw new ApiFailure(CODE_MISMATCH_TEXT, 'mismatch');
    Object.assign(session, hello, { key: await protocol.deriveKey(code, hello.salt) });
    if (!(await protocol.verify(session.key, protocol.serverHelloLines(session), hello.proof))) throw new ApiFailure(CODE_MISMATCH_TEXT, 'mismatch');
    return session;
}

// Pairing step 3, after the user confirmed the server name: finish with P2, check P3, and only then save the token
export async function finishPairing(session) {
    const proof = protocol.base64Url(await protocol.sign(session.key, protocol.clientFinishLines(session)));
    const result = await postJson(session.port, PAIRING_PATH, { step: 'finish', pairingId: session.pairingId, clientNonce: session.clientNonce, serverNonce: session.serverNonce, proof });
    if (result.status !== 200) throw failureOf(result);
    const token = await finishedToken(session, result.body);
    await ext().storage.local.set({ port: session.port, token, serverName: session.serverName, pairedAt: new Date().toISOString() });
}

async function finishedToken(session, body) {
    const finish = protocol.parseFinish(body);
    const matches = finish !== null && (await protocol.verify(session.key, protocol.serverFinishLines(session, finish.token), finish.proof));
    if (!matches) throw new ApiFailure(CODE_MISMATCH_TEXT, 'mismatch');
    return finish.token;
}

// A new port is saved only when the program there proves it holds this browser's token (or nothing is paired)
export async function changePort(port) {
    if (!isValidPort(port)) throw new RangeError('The port must be from 1024 to 65535');
    const pairing = await loadPairing();
    if (pairing) await checkServer({ ...pairing, port });
    await ext().storage.local.set({ port });
}
