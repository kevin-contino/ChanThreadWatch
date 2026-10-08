import assert from 'node:assert/strict';
import { afterEach, beforeEach, mock, test } from 'node:test';
import { authorizationOf, chrome, fakeServer, installBrowser, installFetch, pairedStore, problem } from './fakes.mjs';

const fake = installBrowser();
const api = await import('../src/api.js');
const background = await import('../src/background.js');

const THREAD = 'https://boards.4chan.org/g/thread/1';
const UNREACHABLE = 'Chan Thread Watch is not reachable at 127.0.0.1:47710. Start the app or ctw watch with the local API on, or check the port in the options.';

// The badge timer runs on mock time, so no test waits for it
beforeEach(() => {
    fake.reset();
    mock.timers.enable({ apis: ['setTimeout'] });
});

// The badge timer fires before the mock ends: in Node 24, clearTimeout with an unfired timer of an earlier mock
// instance cancels the next timer of the new one
afterEach(() => {
    mock.timers.runAll();
    mock.timers.reset();
});

// With a squatter on the port, the token never leaves the browser
test('a proof that does not match means the token is never sent', async () => {
    pairedStore(fake);
    const server = fakeServer({ wrongUseProof: true });
    const requests = installFetch(server.handle);
    const result = await background.watchOne(THREAD);
    assert.equal(result.text, 'The program at 127.0.0.1:47710 is not the Chan Thread Watch this browser is paired with. Nothing was sent.');
    assert.deepEqual(requests.map((r) => new URL(r.url).pathname), ['/api/v1/proof']);
    assert.equal(requests.some(authorizationOf), false);
    assert.deepEqual(Object.keys(requests[0].body), ['clientNonce']);
});

test('a proof from a program that holds another token does not match', async () => {
    pairedStore(fake);
    const requests = installFetch(fakeServer({ token: 'ctwe_' + 'A'.repeat(43) }).handle);
    await assert.rejects(api.checkServer(await api.loadPairing()), (error) => error.kind === 'mismatch');
    assert.equal(requests.some(authorizationOf), false);
});

test('a proof answer that is not the API\'s is a mismatch, also a 200 in another form', async () => {
    pairedStore(fake);
    for (const answer of [{ status: 200, body: { proof: 'short' } }, { status: 200, body: { proof: chrome.useProof, more: 'x' } }, { status: 200, body: null }, { status: 404, body: null }, problem(500, 'internal_error')]) {
        const requests = installFetch(() => answer);
        const result = await background.watchOne(THREAD);
        assert.match(result.text, /is not the Chan Thread Watch this browser is paired with/, JSON.stringify(answer));
        assert.equal(requests.length, 1);
        assert.equal(requests.some(authorizationOf), false);
    }
});

test('after a matching proof the add carries the token', async () => {
    pairedStore(fake);
    const requests = installFetch(fakeServer().handle);
    const result = await background.watchOne(THREAD + '#p2');
    assert.equal(result.text, 'Added.');
    assert.deepEqual(requests.map((r) => new URL(r.url).pathname), ['/api/v1/proof', '/api/v1/threads']);
    assert.equal(requests[0].headers.Authorization, undefined);
    assert.equal(requests[1].headers.Authorization, 'Bearer ' + chrome.token);
    assert.deepEqual(requests[1].body, { url: THREAD });
    assert.equal(fake.store.lastResult, 'Added.');
    assert.deepEqual(fake.badges, ['+']);
});

test('every request goes to 127.0.0.1 on the port, as a POST without cookies, cache or redirects, with a timeout', async () => {
    pairedStore(fake);
    const requests = installFetch(fakeServer().handle);
    await background.watchOne(THREAD);
    assert.equal(requests.length, 2);
    for (const request of requests) {
        const url = new URL(request.url);
        assert.equal(url.protocol, 'http:');
        assert.equal(url.hostname, '127.0.0.1');
        assert.equal(url.port, String(chrome.port));
        assert.equal(url.search, '');
        assert.equal(request.init.method, 'POST');
        assert.equal(request.init.credentials, 'omit');
        assert.equal(request.init.cache, 'no-store');
        assert.equal(request.init.redirect, 'error');
        assert.ok(request.init.signal instanceof AbortSignal);
        assert.equal(request.headers['Content-Type'], 'application/json');
    }
    assert.equal(api.TIMEOUT_MS, 10000);
});

test('a port outside 1024 to 65535 is never contacted', async () => {
    const requests = installFetch(() => assert.fail('no request'));
    for (const port of [0, 80, 1023, 65536, 47710.5, '47710']) {
        await assert.rejects(api.postJson(port, '/api/v1/proof', {}), RangeError, String(port));
    }
    assert.equal(requests.length, 0);
});

test('a network error, refusal or timeout says the program is not reachable', async () => {
    pairedStore(fake);
    for (const error of [new TypeError('fetch failed'), new DOMException('timed out', 'TimeoutError')]) {
        installFetch(() => { throw error; });
        const result = await background.watchOne(THREAD);
        assert.equal(result.text, UNREACHABLE);
    }
    assert.deepEqual(await background.status(), { text: 'Not reachable on port 47710' });
});

// A Response whose body read fails as fetch's does when the request's signal ends during the read
function answerThatFails(error) {
    return async () => ({ status: 200, headers: new Headers(), json: async () => { throw error; } });
}

test('a timeout or abort while the body is read says the program is not reachable', async () => {
    for (const error of [new DOMException('timed out', 'TimeoutError'), new DOMException('aborted', 'AbortError')]) {
        globalThis.fetch = answerThatFails(error);
        await assert.rejects(api.postJson(chrome.port, '/api/v1/proof', {}), (failure) => failure instanceof api.ApiFailure && failure.kind === 'timeout' && failure.message === UNREACHABLE, error.name);
    }
    globalThis.fetch = answerThatFails(new SyntaxError('Unexpected token'));
    assert.equal((await api.postJson(chrome.port, '/api/v1/proof', {})).body, null, 'a body that is not JSON is null');
});

test('the badge clears 4 seconds after a result', async () => {
    pairedStore(fake);
    installFetch(fakeServer().handle);
    await background.watchOne(THREAD);
    assert.deepEqual(fake.badges, ['+']);
    mock.timers.tick(3999);
    assert.deepEqual(fake.badges, ['+']);
    mock.timers.tick(1);
    assert.deepEqual(fake.badges, ['+', '']);
});

// A second result after the first one's timer fired gets its own 4 seconds
test('the badge clears again after a badge timer has fired', async () => {
    pairedStore(fake);
    installFetch(fakeServer().handle);
    await background.watchOne(THREAD);
    mock.timers.runAll();
    mock.timers.reset();
    mock.timers.enable({ apis: ['setTimeout'] });
    await background.watchOne(THREAD);
    mock.timers.tick(4000);
    assert.deepEqual(fake.badges, ['+', '', '+', '']);
});

// The proof route's own problems are shown as they are; any other answer means another program
test('a proof answer of not_paired, forbidden_origin or 429 is shown as the API problem', async () => {
    pairedStore(fake);
    const cases = [
        [problem(403, 'not_paired'), 'This browser is not paired (any more). Pair it again in the options.'],
        [problem(403, 'forbidden_origin'), 'This browser\'s pairing does not match. Pair it again.'],
        [problem(429, 'rate_limited', { 'Retry-After': '9' }), 'Too many requests. Try again in 9 seconds.']
    ];
    for (const [answer, text] of cases) {
        const requests = installFetch(() => answer);
        await assert.rejects(api.checkServer(await api.loadPairing()), (error) => error.kind === 'problem' && error.message === text, text);
        assert.equal(requests.length, 1);
        assert.equal(requests.some(authorizationOf), false);
    }
});

test('a failing handler still answers the popup', async () => {
    pairedStore(fake);
    fake.failStorage = true;
    assert.deepEqual(await fake.runtime.sendMessage({ type: 'status' }), { text: 'The request failed. See the extension\'s errors.' });
    assert.deepEqual(await fake.runtime.sendMessage({ type: 'watchOne', url: THREAD }), { text: 'The request failed. See the extension\'s errors.' });
});

test('a failing menu click is handled and shows "!"', async () => {
    pairedStore(fake);
    fake.failStorage = true;
    installFetch(() => assert.fail('no request'));
    const onClicked = fake.contextMenus.onClicked.list[0];
    await assert.doesNotReject(onClicked({ menuItemId: 'watch-thread-link', linkUrl: THREAD }));
    assert.deepEqual(fake.badges, ['!']);
});

const TABLE = [
    [problem(403, 'not_paired'), 'This browser is not paired (any more). Pair it again in the options.'],
    [{ status: 401, body: null }, 'This browser is not paired (any more). Pair it again in the options.'],
    [problem(403, 'forbidden_origin'), 'This browser\'s pairing does not match. Pair it again.'],
    [problem(409, 'already_watched'), 'Already watched.'],
    [problem(409, 'blacklisted'), 'The thread is on the blacklist.'],
    [problem(409, 'thread_limit'), 'The thread list is full.'],
    [problem(422, 'unknown_host'), 'This site is not supported.'],
    [problem(422, 'invalid_url'), 'Not a thread address.'],
    [problem(422, 'blocked_host'), 'The address is local or private.'],
    [problem(422, 'unresolvable_host'), 'The site name could not be found.'],
    [problem(429, 'add_rate_limited', { 'Retry-After': '17' }), 'Too many requests. Try again in 17 seconds.'],
    [problem(429, 'rate_limited'), 'Too many requests. Try again in 60 seconds.'],
    [problem(503, 'unavailable'), 'The program is busy or closing. Try again.'],
    [problem(503, 'marks_unavailable'), 'The program cannot save new threads now (see its log).'],
    [problem(507, 'insufficient_storage'), 'The download drive is almost full.'],
    [problem(400, 'invalid_body'), 'The request failed (code 400). See log.txt.'],
    [problem(413, 'body_too_large'), 'The request failed (code 413). See log.txt.'],
    [problem(415, 'unsupported_media_type'), 'The request failed (code 415). See log.txt.'],
    [problem(500, 'internal_error'), 'The request failed (code 500). See log.txt.'],
    [{ status: 418, body: null }, 'The request failed (code 418). See log.txt.'],
    [problem(400, 'constructor'), 'The request failed (code 400). See log.txt.']
];

test('an add\'s problem maps to its text, by code and then by status', async () => {
    pairedStore(fake);
    const server = fakeServer();
    for (const [answer, text] of TABLE) {
        installFetch((request) => (request.url.endsWith('/threads') ? answer : server.handle(request)));
        const result = await background.watchOne(THREAD);
        assert.equal(result.text, text, JSON.stringify(answer));
    }
});

test('a Retry-After that is not a number of seconds counts as a minute', async () => {
    pairedStore(fake);
    const server = fakeServer();
    installFetch((request) => (request.url.endsWith('/threads') ? problem(429, 'rate_limited', { 'Retry-After': 'Wed, 21 Oct 2026 07:28:00 GMT' }) : server.handle(request)));
    assert.equal((await background.watchOne(THREAD)).text, 'Too many requests. Try again in 60 seconds.');
});

test('not paired: nothing is sent', async () => {
    const requests = installFetch(() => assert.fail('no request'));
    assert.equal((await background.watchOne(THREAD)).text, 'This browser is not paired (any more). Pair it again in the options.');
    assert.deepEqual(await background.status(), { text: 'Not paired' });
    assert.equal(requests.length, 0);
});

test('a saved token in another form counts as not paired', async () => {
    pairedStore(fake);
    fake.store.token = 'ctw_' + 'A'.repeat(43);
    assert.equal(await api.loadPairing(), null);
});

test('an address that is not a thread is refused before any request', async () => {
    pairedStore(fake);
    const requests = installFetch(() => assert.fail('no request'));
    assert.equal((await background.watchOne('https://boards.4chan.org/g/')).text, 'Not a thread address.');
    assert.equal(requests.length, 0);
    assert.deepEqual(fake.badges, ['!']);
});

test('the status line names the paired program after its proof', async () => {
    pairedStore(fake);
    installFetch(fakeServer().handle);
    assert.deepEqual(await background.status(), { text: 'Paired with ' + chrome.serverName });
});

// The proof is bound to the new port: a proof made for the old port, replayed on the new one, does not match
test('a new port is saved only after a proof for that port matches', async () => {
    pairedStore(fake);
    const replayed = installFetch(fakeServer({ proofPort: chrome.port }).handle);
    await assert.rejects(api.changePort(50000), (error) => error.kind === 'mismatch');
    assert.equal(fake.store.port, chrome.port);
    assert.equal(new URL(replayed[0].url).port, '50000');
    const requests = installFetch(fakeServer().handle);
    await api.changePort(50000);
    assert.equal(fake.store.port, 50000);
    assert.deepEqual(requests.map((r) => new URL(r.url).port + new URL(r.url).pathname), ['50000/api/v1/proof']);
});

test('not paired: a new port is saved without a request', async () => {
    const requests = installFetch(() => assert.fail('no request'));
    await api.changePort(50000);
    assert.equal(fake.store.port, 50000);
    assert.equal(requests.length, 0);
});

test('forget pairing removes the token but keeps the port', async () => {
    pairedStore(fake);
    await api.forgetPairing();
    assert.deepEqual(fake.store, { port: chrome.port });
});
