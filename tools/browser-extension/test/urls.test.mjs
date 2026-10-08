import assert from 'node:assert/strict';
import { test } from 'node:test';
import { domainsOf, hostPattern, threadPatterns, threadUrl, threadUrlsOf } from '../src/urls.js';
import { knownHosts } from './fakes.mjs';

test('each thread shape of each site is a thread', () => {
    const threads = [
        'https://boards.4chan.org/wg/thread/8143532',
        'https://boards.4chan.org/wg/thread/8143532/japan-papes-continued',
        'https://boards.4channel.org/g/thread/1/',
        'https://8ch.net/tech/res/100.html',
        'https://warosu.org/g/thread/123',
        'https://archive.4plebs.org/tg/thread/123/',
        'https://archive.alice.al/c/thread/5/',
        'https://desuarchive.org/a/thread/5/',
        'https://arch.b4k.dev/v/thread/5/',
        'https://arch.b4k.co/v/thread/5/',
        'https://archived.moe/a/thread/5/',
        'https://thebarchive.com/b/thread/5/',
        'https://archiveofsins.com/h/thread/5/',
        'https://archive.rebeccablacktech.com/g/thread/5/',
        'https://rbt.asia/g/thread/5/',
        'https://endchan.net/b/res/5.html',
        'https://endchan.org/b/res/5.html',
        'http://boards.4chan.org/g/thread/1'
    ];
    for (const url of threads) assert.equal(threadUrl(url, knownHosts), url, url);
});

test('a domain itself and an upper-case host are known', () => {
    assert.equal(threadUrl('https://4chan.org/g/thread/1', knownHosts), 'https://4chan.org/g/thread/1');
    assert.equal(threadUrl('https://BOARDS.4CHAN.ORG/g/thread/1', knownHosts), 'https://boards.4chan.org/g/thread/1');
});

test('the query and the fragment are dropped', () => {
    assert.equal(threadUrl('https://boards.4chan.org/wg/thread/8143532#p1', knownHosts), 'https://boards.4chan.org/wg/thread/8143532');
    assert.equal(threadUrl('https://boards.4chan.org/wg/thread/8143532?page=2#p9', knownHosts), 'https://boards.4chan.org/wg/thread/8143532');
    assert.equal(threadUrl('https://8ch.net/g/res/300.html?x=1#301', knownHosts), 'https://8ch.net/g/res/300.html');
});

test('boards, catalogs, images and other paths are not threads', () => {
    for (const url of [
        'https://boards.4chan.org/wg/',
        'https://boards.4chan.org/wg/catalog',
        'https://boards.4chan.org/',
        'https://boards.4chan.org/wg/thread/1.jpg',
        'https://boards.4chan.org/wg/thread/',
        'https://boards.4chan.org/wg/thread/abc',
        'https://boards.4chan.org/wg/thread/1/slug/more',
        'https://boards.4chan.org//thread/1',
        'https://i.warosu.org/data/g/img/2.jpg',
        'https://8ch.net/file_store/2.jpg',
        'https://8ch.net/tech/res/100.htm',
        'https://endchan.org/.media/2.png'
    ]) assert.equal(threadUrl(url, knownHosts), null, url);
});

test('other hosts and look-alike hosts are refused', () => {
    for (const url of [
        'https://example.com/g/thread/1',
        'https://4chan.org.evil.com/g/thread/1',
        'https://evil4chan.org/g/thread/1',
        'https://not4chan.org/g/thread/1',
        'https://chan.org/g/thread/1',
        'https://127.0.0.1/g/thread/1',
        'https://localhost/g/thread/1',
        'https://boards.4chan.org./g/thread/1'
    ]) assert.equal(threadUrl(url, knownHosts), null, url);
});

test('fullwidth digits, Unicode labels, logins and other schemes are refused', () => {
    for (const url of [
        'https://boards.４chan.org/g/thread/1',
        'https://bücher.4chan.org/g/thread/1',
        'https://boards.4chan.org/g/thread/１２',
        'https://user:secret@boards.4chan.org/g/thread/1',
        'https://user@boards.4chan.org/g/thread/1',
        'ftp://boards.4chan.org/g/thread/1',
        'javascript:alert(1)',
        ' https://boards.4chan.org/g/thread/1',
        'not a url',
        '',
        null,
        undefined,
        42
    ]) assert.equal(threadUrl(url, knownHosts), null, String(url));
});

test('a punycode label as the browser gives it is allowed, as the API allows it', () => {
    assert.equal(threadUrl('https://xn--bcher-kva.4chan.org/g/thread/1', knownHosts), 'https://xn--bcher-kva.4chan.org/g/thread/1');
});

test('threadUrlsOf keeps the order, drops duplicates and counts what is not a thread', () => {
    const result = threadUrlsOf([
        'https://boards.4chan.org/g/thread/2',
        'https://boards.4chan.org/g/',
        'https://boards.4chan.org/g/thread/1#p5',
        'https://boards.4chan.org/g/thread/2?x=1',
        'https://boards.4chan.org/g/thread/1',
        undefined
    ], knownHosts);
    assert.deepEqual(result, { urls: ['https://boards.4chan.org/g/thread/2', 'https://boards.4chan.org/g/thread/1'], skipped: 2 });
});

test('host patterns and menu patterns come from the domains, and back', () => {
    const patterns = knownHosts.map(hostPattern);
    assert.equal(patterns[0], '*://*.4chan.org/*');
    assert.deepEqual(domainsOf(patterns), knownHosts);
    assert.throws(() => domainsOf(['<all_urls>']));
    assert.deepEqual(threadPatterns(['4chan.org']), ['*://*.4chan.org/*/thread/*', '*://*.4chan.org/*/res/*']);
});
