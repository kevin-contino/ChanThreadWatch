import assert from 'node:assert/strict';
import { beforeEach, test } from 'node:test';
import { authorizationOf, chrome, fakeServer, installBrowser, installFetch, problem, vectors } from './fakes.mjs';

const fake = installBrowser();
const { ApiFailure, finishPairing, startPairing } = await import('../src/api.js');
const protocol = await import('../src/pairing.js');

beforeEach(() => fake.reset());

function hex(bytes) {
    return Buffer.from(bytes).toString('hex');
}

// The shared vectors: the extension's WebCrypto code makes every value the C# code makes
test('the pairing vectors reproduce', async () => {
    assert.equal(vectors.pbkdf2Iterations, protocol.PAIRING_ITERATIONS);
    assert.equal(vectors.vectors.length, 2);
    for (const v of vectors.vectors) {
        assert.equal(protocol.normalizeCode(v.displayCode), v.code);
        const key = await protocol.deriveKey(v.code, protocol.fromBase64Url(v.salt, 16));
        assert.equal(hex(key), v.keyHex, v.name);
        const session = { port: v.port, origin: v.origin, pairingId: v.pairingId, clientNonce: v.clientNonce, serverNonce: v.serverNonce, serverName: v.serverName };
        assert.equal(protocol.base64Url(await protocol.sign(key, protocol.serverHelloLines(session))), v.serverHelloProof, v.name);
        assert.equal(protocol.base64Url(await protocol.sign(key, protocol.clientFinishLines(session))), v.clientFinishProof, v.name);
        assert.equal(protocol.base64Url(await protocol.sign(key, protocol.serverFinishLines(session, v.token))), v.serverFinishProof, v.name);
        const tokenKey = await protocol.tokenKey(v.token);
        assert.equal(hex(tokenKey), v.tokenHashHex, v.name);
        assert.equal(protocol.base64Url(await protocol.sign(tokenKey, protocol.useProofLines(v.port, v.origin, v.clientNonce))), v.useProof, v.name);
        assert.ok(await protocol.verify(key, protocol.serverHelloLines(session), protocol.fromBase64Url(v.serverHelloProof, 32)), v.name);
        assert.ok(protocol.isToken(v.token), v.name);
        assert.ok(protocol.isValidServerName(v.serverName), v.name);
    }
    for (const s of vectors.scriptProofs) {
        const tokenKey = await protocol.tokenKey(s.token);
        assert.equal(hex(tokenKey), s.tokenHashHex);
        assert.equal(protocol.base64Url(await protocol.sign(tokenKey, protocol.useProofLines(s.port, s.origin, s.clientNonce))), s.useProof);
    }
});

test('a proof for another port, Origin or name does not verify', async () => {
    const key = Buffer.from(chrome.keyHex, 'hex');
    const session = { port: chrome.port, origin: chrome.origin, pairingId: chrome.pairingId, clientNonce: chrome.clientNonce, serverNonce: chrome.serverNonce, serverName: chrome.serverName };
    const proof = protocol.fromBase64Url(chrome.serverHelloProof, 32);
    for (const change of [{ port: chrome.port + 1 }, { origin: 'moz-extension://00000000-0000-4000-8000-000000000001' }, { serverName: 'Other' }]) {
        assert.equal(await protocol.verify(key, protocol.serverHelloLines({ ...session, ...change }), proof), false, JSON.stringify(change));
    }
    assert.equal(await protocol.verify(key, protocol.serverHelloLines(session), null), false);
});

test('the code is normalized as typed, and anything else is refused', () => {
    assert.equal(protocol.normalizeCode('TEST-2345'), 'TEST2345');
    assert.equal(protocol.normalizeCode(' test 2345 '), 'TEST2345');
    assert.equal(protocol.normalizeCode('0abc-defg'), '0ABCDEFG');
    assert.equal(protocol.normalizeCode('oabc-defg'), '0ABCDEFG');
    assert.equal(protocol.normalizeCode('i1L1-0o00'), '11110000');
    for (const text of ['TEST234', 'TEST23456', 'TESU2345', 'TEST_2345', 'TEST.2345', 'TÉST2345', '', null, 12345678]) {
        assert.equal(protocol.normalizeCode(text), null, String(text));
    }
});

test('base64url is read in its one canonical form and length only', () => {
    assert.equal(hex(protocol.fromBase64Url(chrome.salt, 16)), '000102030405060708090a0b0c0d0e0f');
    assert.equal(protocol.fromBase64Url(chrome.salt + 'A', 16), null);
    assert.equal(protocol.fromBase64Url(chrome.salt.slice(0, -1) + 'x', 16), null, 'non-canonical last symbol');
    assert.equal(protocol.fromBase64Url(chrome.salt.slice(0, -1) + '=', 16), null);
    assert.equal(protocol.fromBase64Url('AAECAwQFBgcICQoLDA0OD+', 16), null);
});

test('server names are checked as the server checks them', () => {
    assert.equal(protocol.isValidServerName('a'.repeat(80)), true);
    for (const name of ['', 'a'.repeat(81), 'line\nbreak', 'tab\there', 'rtl\u202eoverride', 'emoji 😀', 'private \ue000', 'para\u2029', null]) {
        assert.equal(protocol.isValidServerName(name), false, JSON.stringify(name));
    }
});

test('pairing with the right code saves the port, token and name only after P3 checks', async () => {
    const server = fakeServer();
    const requests = installFetch(server.handle);
    const session = await startPairing(server.port, chrome.displayCode);
    assert.equal(session.serverName, chrome.serverName);
    assert.deepEqual(fake.store, {}, 'nothing is saved before the confirm');
    await finishPairing(session);
    assert.equal(fake.store.token, chrome.token);
    assert.equal(fake.store.port, chrome.port);
    assert.equal(fake.store.serverName, chrome.serverName);
    assert.match(fake.store.pairedAt, /^\d{4}-\d{2}-\d{2}T/);
    assert.deepEqual(requests.map((r) => r.body.step), ['hello', 'finish']);
    assert.equal(requests.some(authorizationOf), false);
    const hello = requests[0].body;
    assert.deepEqual(hello, { step: 'hello', clientNonce: hello.clientNonce });
    assert.equal(protocol.fromBase64Url(hello.clientNonce, 32).length, 32);
    const finish = requests[1].body;
    assert.equal(finish.pairingId, chrome.pairingId);
    assert.equal(finish.serverNonce, chrome.serverNonce);
    assert.equal(finish.clientNonce, hello.clientNonce);
    const key = Buffer.from(chrome.keyHex, 'hex');
    const expected = protocol.base64Url(await protocol.sign(key, protocol.clientFinishLines({ ...session, origin: chrome.origin })));
    assert.equal(finish.proof, expected);
    assert.deepEqual(Object.keys(finish).sort(), ['clientNonce', 'pairingId', 'proof', 'serverNonce', 'step']);
});

// A wrong code ends at P1; no finish is sent
test('a wrong code fails at P1 and sends no finish', async () => {
    const server = fakeServer();
    const requests = installFetch(server.handle);
    await assert.rejects(startPairing(server.port, 'TEST-2346'), (error) => error instanceof ApiFailure && error.message === 'The code does not match. Check the code and the port.');
    assert.deepEqual(requests.map((r) => r.body.step), ['hello']);
    assert.deepEqual(fake.store, {});
});

test('a program that does not know the code (another key) fails at P1', async () => {
    const server = fakeServer({ keyHex: vectors.vectors[1].keyHex });
    const requests = installFetch(server.handle);
    await assert.rejects(startPairing(server.port, chrome.code), /The code does not match/);
    assert.equal(requests.length, 1);
});

test('a code in the wrong form sends nothing', async () => {
    const requests = installFetch(() => assert.fail('no request'));
    await assert.rejects(startPairing(chrome.port, 'TEST-23'), /The code has 8 letters and digits/);
    assert.equal(requests.length, 0);
});

test('a valid P1 but a bad P3 saves nothing', async () => {
    const server = fakeServer({ wrongFinishProof: true });
    installFetch(server.handle);
    const session = await startPairing(server.port, chrome.code);
    await assert.rejects(finishPairing(session), /The code does not match/);
    assert.deepEqual(fake.store, {});
});

test('a finish answer in another form saves nothing', async () => {
    const server = fakeServer();
    installFetch((request) => (request.body.step === 'finish' ? { status: 200, body: { token: chrome.token, proof: 'x', extra: '' } } : server.handle(request)));
    const session = await startPairing(server.port, chrome.code);
    await assert.rejects(finishPairing(session), /The code does not match/);
    assert.deepEqual(fake.store, {});
});

test('a hello answer in another form is a mismatch', async () => {
    for (const body of [null, [], { pairingId: chrome.pairingId }, { pairingId: 1, salt: chrome.salt, serverNonce: chrome.serverNonce, serverName: 'x', proof: chrome.serverHelloProof }]) {
        installFetch(() => ({ status: 200, body }));
        await assert.rejects(startPairing(chrome.port, chrome.code), /The code does not match/, JSON.stringify(body));
    }
});

test('pairing problems map to their texts', async () => {
    const cases = [
        [problem(409, 'pairing_unavailable'), 'No pairing code is active. Make a new code in Chan Thread Watch.'],
        [problem(403, 'pairing_failed'), 'Pairing failed and the code is no longer valid. Make a new code.'],
        [problem(403, 'forbidden_origin'), 'This browser\'s pairing does not match. Pair it again.'],
        [problem(429, 'pairing_rate_limited', { 'Retry-After': '7' }), 'Too many requests. Try again in 7 seconds.']
    ];
    for (const [answer, text] of cases) {
        installFetch(() => answer);
        await assert.rejects(startPairing(chrome.port, chrome.code), (error) => error.message === text, text);
    }
});

test('a finish refused by the server saves nothing', async () => {
    const server = fakeServer();
    installFetch((request) => (request.body.step === 'finish' ? problem(409, 'pairing_unavailable') : server.handle(request)));
    const session = await startPairing(server.port, chrome.code);
    await assert.rejects(finishPairing(session), /No pairing code is active/);
    assert.deepEqual(fake.store, {});
});
