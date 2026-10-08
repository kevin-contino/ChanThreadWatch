import assert from 'node:assert/strict';
import { beforeEach, test } from 'node:test';
import { chrome, fakeServer, installBrowser, installFetch, pairedStore } from './fakes.mjs';

const fake = installBrowser();
const { parsePort, portChange } = await import('../src/options.js');

const PORT_TEXT = 'The port must be a number from 1024 to 65535.';

beforeEach(() => fake.reset());

test('a port is a whole number from 1024 to 65535 as typed', () => {
    assert.equal(parsePort('47710'), 47710);
    assert.equal(parsePort(' 1024 '), 1024);
    assert.equal(parsePort('65535'), 65535);
    for (const text of ['', '1023', '65536', '1e4', '47710.5', '0x1000', 'abc', '-2000']) assert.equal(parsePort(text), null, text);
});

// The field is set back to the saved port, so it never shows a port that is not in use
test('a refused port gives the saved port back with the reason', async () => {
    pairedStore(fake);
    installFetch(fakeServer({ proofPort: chrome.port }).handle);
    const result = await portChange('50000');
    assert.deepEqual(result, { port: chrome.port, text: 'The program at 127.0.0.1:50000 is not the Chan Thread Watch this browser is paired with. Nothing was sent.' });
    assert.equal(fake.store.port, chrome.port);
});

test('a port that is not a port gives the saved port back', async () => {
    pairedStore(fake);
    const requests = installFetch(() => assert.fail('no request'));
    assert.deepEqual(await portChange('80'), { port: chrome.port, text: PORT_TEXT });
    assert.equal(requests.length, 0);
});

test('an accepted port is kept', async () => {
    pairedStore(fake);
    installFetch(fakeServer().handle);
    assert.deepEqual(await portChange('50000'), { port: 50000, text: null });
    assert.equal(fake.store.port, 50000);
});

test('not paired: the port is saved without a check', async () => {
    const requests = installFetch(() => assert.fail('no request'));
    assert.deepEqual(await portChange('50000'), { port: 50000, text: null });
    assert.equal(fake.store.port, 50000);
    assert.equal(requests.length, 0);
});
