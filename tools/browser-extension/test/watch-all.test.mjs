import assert from 'node:assert/strict';
import { afterEach, beforeEach, mock, test } from 'node:test';
import { authorizationOf, fakeServer, hostPatterns, installBrowser, installFetch, pairedStore } from './fakes.mjs';

const fake = installBrowser();
// The background registers its listeners on the fake; the popup sends its messages to them
const background = await import('../src/background.js');
const api = await import('../src/api.js');
const { textOf, watchAllChanTabs, watchThisTab, PERMISSION_DENIED_TEXT } = await import('../src/popup.js');

const ERROR_TEXT = 'The request failed. See the extension\'s errors.';

// The badge timer runs on mock time, so no test waits for it
beforeEach(() => {
    fake.reset();
    pairedStore(fake);
    mock.timers.enable({ apis: ['setTimeout'] });
});

// The badge timer fires before the mock ends: in Node 24, clearTimeout with an unfired timer of an earlier mock
// instance cancels the next timer of the new one
afterEach(() => {
    mock.timers.runAll();
    mock.timers.reset();
});

function tabs(urls) {
    return urls.map((url, index) => ({ id: index + 1, url }));
}

function threads(count) {
    return Array.from({ length: count }, (_, index) => 'https://boards.4chan.org/g/thread/' + (index + 1));
}

function addedUrls(requests) {
    return requests.filter((r) => r.url.endsWith('/api/v1/threads')).map((r) => r.body.url);
}

test('permission denied: no tab is read and nothing is sent', async () => {
    fake.grant = false;
    fake.openTabs = tabs(['https://boards.4chan.org/g/thread/1']);
    const requests = installFetch(() => assert.fail('no request'));
    const result = await watchAllChanTabs();
    assert.equal(result.text, PERMISSION_DENIED_TEXT);
    assert.deepEqual(fake.permissionRequests, [{ origins: hostPatterns }]);
    assert.equal(fake.tabQueries.length, 0);
    assert.equal(requests.length, 0);
});

// The browser shows the permission prompt only for a call made in the click's user gesture, before any await
test('the permission request is made at once, in the click', async () => {
    installFetch(() => assert.fail('no request'));
    const pending = watchAllChanTabs();
    assert.deepEqual(fake.permissionRequests, [{ origins: hostPatterns }]);
    assert.equal(fake.tabQueries.length, 0);
    await pending;
});

test('the thread tabs are filtered, normalized and sent once each, in order, after one proof', async () => {
    fake.openTabs = tabs([
        'https://boards.4chan.org/g/thread/2#p3',
        'https://boards.4chan.org/g/',
        'https://warosu.org/g/thread/5',
        'https://boards.4chan.org/g/thread/2',
        'https://boards.4chan.org/g/catalog',
        'https://8ch.net/tech/res/7.html?x=1'
    ]);
    const requests = installFetch(fakeServer().handle);
    const result = await watchAllChanTabs();
    assert.deepEqual(fake.tabQueries, [{ url: hostPatterns }]);
    assert.deepEqual(addedUrls(requests), ['https://boards.4chan.org/g/thread/2', 'https://warosu.org/g/thread/5', 'https://8ch.net/tech/res/7.html']);
    assert.equal(requests.filter((r) => r.url.endsWith('/api/v1/proof')).length, 1);
    assert.equal(requests[0].url.endsWith('/api/v1/proof'), true);
    assert.equal(authorizationOf(requests[0]), undefined);
    assert.equal(result.text, 'Added 3. Already watched 0, skipped 2 (not a thread), failed 0.');
    assert.equal(fake.store.lastResult, result.text);
    assert.deepEqual(fake.badges, ['+']);
});

// Each write is an extension API call, which keeps a Chrome service worker from stopping as idle during the batch
test('the progress is saved after every add', async () => {
    fake.openTabs = tabs(threads(3));
    installFetch(fakeServer({ addStatuses: [201, 409, 422] }).handle);
    const result = await watchAllChanTabs();
    const saved = fake.storageSets.map((items) => items.lastResult);
    assert.deepEqual(saved, ['Adding 1 of 3.', 'Adding 2 of 3.', 'Adding 3 of 3.', result.text]);
});

test('the summary counts added, already watched, skipped and failed', async () => {
    fake.openTabs = tabs([...threads(4), 'https://boards.4chan.org/g/']);
    installFetch(fakeServer({ addStatuses: [201, 409, 422, 409] }).handle);
    const result = await watchAllChanTabs();
    assert.equal(result.text, 'Added 1. Already watched 2, skipped 1 (not a thread), failed 1.');
});

test('the first 429 stops the batch and says how long to wait and how many were not sent', async () => {
    fake.openTabs = tabs(threads(4));
    const requests = installFetch(fakeServer({ addStatuses: [201, 409, 429, 201] }).handle);
    const result = await watchAllChanTabs();
    assert.deepEqual(addedUrls(requests), threads(3));
    assert.equal(result.text, 'Added 1. Wait 20 seconds for the rest (2 not sent). Already watched 1, skipped 0 (not a thread), failed 0.');
    assert.deepEqual(fake.badges, ['+']);
});

const STOPS = [
    [401, 'This browser is not paired (any more). Pair it again in the options.'],
    [403, 'This browser\'s pairing does not match. Pair it again.'],
    [503, 'The program is busy or closing. Try again.'],
    ['network', 'Chan Thread Watch is not reachable at 127.0.0.1:47710. Start the app or ctw watch with the local API on, or check the port in the options.']
];

for (const [status, text] of STOPS) {
    test('a ' + status + ' stops the batch with its text and the number not sent', async () => {
        fake.openTabs = tabs(threads(4));
        const requests = installFetch(fakeServer({ addStatuses: [409, status, 201, 201] }).handle);
        const result = await watchAllChanTabs();
        assert.deepEqual(addedUrls(requests), threads(2));
        assert.equal(result.text, 'Added 0. ' + text + ' 3 not sent. Already watched 1, skipped 0 (not a thread), failed 0.');
        assert.deepEqual(fake.badges, ['!']);
    });
}

test('a 503 that is not unavailable fails that thread and goes on', async () => {
    const server = fakeServer();
    const requests = installFetch((request) => (request.url.endsWith('/threads') && server.state.adds++ === 0 ? { status: 503, body: { code: 'marks_unavailable' } } : server.handle(request)));
    fake.openTabs = tabs(threads(2));
    const result = await watchAllChanTabs();
    assert.equal(addedUrls(requests).length, 2);
    assert.equal(result.text, 'Added 1. Already watched 0, skipped 0 (not a thread), failed 1.');
});

// The program may have added a thread whose request timed out, so it is failed and the batch goes on; a refused
// connection still stops it (see the stops above)
test('an add that times out is failed with its own text and the batch goes on', async () => {
    fake.openTabs = tabs(threads(3));
    const requests = installFetch(fakeServer({ addStatuses: [201, 'timeout', 409] }).handle);
    const result = await watchAllChanTabs();
    assert.deepEqual(addedUrls(requests), threads(3));
    assert.equal(result.text, 'Added 1. Already watched 1, skipped 0 (not a thread), failed 1.');
});

test('a single add that times out says it is not known whether it was added', async () => {
    installFetch(fakeServer({ addStatuses: ['timeout'] }).handle);
    const result = await background.watchOne('https://boards.4chan.org/g/thread/1');
    assert.equal(result.text, 'The request timed out, so it is not known whether the thread was added.');
    assert.deepEqual(fake.badges, ['!']);
});

test('a 201 is added without reading its body', async () => {
    fake.openTabs = tabs(threads(2));
    installFetch(fakeServer({ addStatuses: ['slow201', 201] }).handle);
    const result = await watchAllChanTabs();
    assert.equal(result.text, 'Added 2. Already watched 0, skipped 0 (not a thread), failed 0.');
});

test('a failed progress write changes nothing in the batch', async () => {
    fake.failSet = (items) => String(items.lastResult).startsWith('Adding');
    installFetch(fakeServer({ addStatuses: [201, 409, 422] }).handle);
    const summary = await api.watchUrls(threads(3));
    assert.deepEqual([summary.added, summary.already, summary.failed, summary.notSent, summary.stop], [1, 1, 1, 0, null]);
});

test('added, already watched, failed and not sent add up to the thread addresses', async () => {
    const sequences = [[], [201, 201], [409, 422, 201], [429], [201, 401], [422, 503], [201, 409, 'network'], [422, 422, 422], ['timeout', 'slow201', 429], ['timeout', 'timeout', 'timeout']];
    for (const statuses of sequences) {
        fake.reset();
        pairedStore(fake);
        installFetch(fakeServer({ addStatuses: statuses }).handle);
        const summary = await api.watchUrls(threads(3));
        const stopped = statuses.some((status) => [401, 429, 503, 'network'].includes(status));
        assert.equal(summary.added + summary.already + summary.failed + summary.notSent, 3, JSON.stringify(statuses));
        assert.equal(summary.stop !== null, stopped, JSON.stringify(statuses));
    }
});

test('no thread tabs: the result says so, with no request, no badge and nothing saved', async () => {
    fake.openTabs = tabs(['https://boards.4chan.org/g/']);
    const requests = installFetch(() => assert.fail('no request'));
    const result = await watchAllChanTabs();
    assert.equal(result.text, 'No thread tabs are open.');
    assert.equal(requests.length, 0);
    assert.deepEqual(fake.badges, []);
    assert.equal(fake.store.lastResult, undefined);
});

test('the background checks the addresses again and ignores other senders', async () => {
    const requests = installFetch(fakeServer().handle);
    const result = await fake.runtime.sendMessage({ type: 'watchAll', urls: ['https://evil.example/g/thread/1', 'https://boards.4chan.org/g/thread/1', 5], skipped: -3 });
    assert.equal(result.text, 'Added 1. Already watched 0, skipped 1 (not a thread), failed 0.');
    assert.deepEqual(addedUrls(requests), ['https://boards.4chan.org/g/thread/1']);
    const listener = fake.runtime.onMessage.list[0];
    assert.equal(listener({ type: 'watchAll', urls: [] }, { id: 'another-extension' }, () => assert.fail('no answer')), false);
    assert.equal(listener({ type: 'unknown' }, { id: fake.runtime.id }, () => assert.fail('no answer')), false);
    assert.equal(listener(null, { id: fake.runtime.id }, () => assert.fail('no answer')), false);
});

test('the menus are made again with the thread patterns of every known domain', async () => {
    fake.menus = [{ id: 'stale-item' }];
    await background.createMenus();
    assert.deepEqual(fake.menus.map((item) => item.id), ['watch-thread-link', 'watch-thread-page']);
    const [link, page] = fake.menus;
    assert.deepEqual(link.contexts, ['link']);
    assert.deepEqual(page.contexts, ['page']);
    assert.equal(link.title, 'Watch this thread');
    assert.ok(link.targetUrlPatterns.includes('*://*.4chan.org/*/thread/*'));
    assert.ok(page.documentUrlPatterns.includes('*://*.endchan.org/*/res/*'));
    assert.equal(link.targetUrlPatterns.length, hostPatterns.length * 2);
    assert.deepEqual(fake.runtime.onInstalled.list, [background.createMenus]);
    assert.deepEqual(fake.runtime.onStartup.list, [background.createMenus]);
});

// An install and a start at the same moment: the second build waits for the first
test('two builds at the same time leave exactly two menu items', async () => {
    await Promise.all([background.createMenus(), background.createMenus()]);
    assert.deepEqual(fake.menus.map((item) => item.id), ['watch-thread-link', 'watch-thread-page']);
});

test('after a build that failed, the next build still leaves exactly two menu items', async () => {
    fake.removeAllFailures = 1;
    fake.menus = [{ id: 'stale-item' }];
    await assert.rejects(background.createMenus(), /Test removeAll failure/);
    await background.createMenus();
    assert.deepEqual(fake.menus.map((item) => item.id), ['watch-thread-link', 'watch-thread-page']);
});

test('every menu create reads runtime.lastError in its callback', async () => {
    // The callbacks of the builds in other tests come first
    await new Promise((resolve) => setImmediate(resolve));
    fake.lastErrorReads = 0;
    await background.createMenus();
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(fake.lastErrorReads, 2);
});

test('a menu click checks the address and adds it', async () => {
    const requests = installFetch(fakeServer().handle);
    const onClicked = fake.contextMenus.onClicked.list[0];
    await onClicked({ menuItemId: 'watch-thread-link', linkUrl: 'https://boards.4chan.org/g/thread/9#p1', pageUrl: 'https://boards.4chan.org/g/' });
    assert.deepEqual(addedUrls(requests), ['https://boards.4chan.org/g/thread/9']);
    await onClicked({ menuItemId: 'watch-thread-page', pageUrl: 'https://boards.4chan.org/g/catalog' });
    assert.equal(fake.store.lastResult, 'Not a thread address.');
});

test('"Watch this tab" sends the active tab\'s address', async () => {
    fake.openTabs = tabs(['https://boards.4chan.org/g/thread/4#p5']);
    const requests = installFetch(fakeServer().handle);
    assert.equal((await watchThisTab()).text, 'Added.');
    assert.deepEqual(fake.tabQueries, [{ active: true, currentWindow: true }]);
    assert.deepEqual(addedUrls(requests), ['https://boards.4chan.org/g/thread/4']);
});

test('"Watch this tab" without a tab sends nothing', async () => {
    const requests = installFetch(() => assert.fail('no request'));
    assert.equal((await watchThisTab()).text, 'Not a thread address.');
    assert.equal(requests.length, 0);
});

test('the popup shows the error text for a failed or empty answer', async () => {
    assert.equal(await textOf(Promise.resolve({ text: 'Added.' })), 'Added.');
    assert.equal(await textOf(Promise.reject(new Error('Could not establish connection'))), ERROR_TEXT);
    assert.equal(await textOf(Promise.resolve(undefined)), ERROR_TEXT);
    fake.failStorage = true;
    assert.equal(await textOf(fake.runtime.sendMessage({ type: 'status' })), ERROR_TEXT);
});
