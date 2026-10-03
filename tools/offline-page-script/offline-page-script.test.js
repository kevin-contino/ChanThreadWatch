'use strict';

// Runs Resources/OfflinePageScript.js in jsdom against each site fixture in
// ChanThreadWatch.Tests/Fixtures/sites. Each fixture is first changed the way the app saves a
// page: same-page links become fragments and thumbnail links point at local image files.
// jsdom does not enforce the Content-Security-Policy; the C# tests check the policy's hash.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM, VirtualConsole } = require('jsdom');

const ROOT = path.join(__dirname, '..', '..');
const FIXTURES = path.join(ROOT, 'ChanThreadWatch.Tests', 'Fixtures', 'sites');
const SCRIPT = fs.readFileSync(path.join(ROOT, 'Resources', 'OfflinePageScript.js'), 'utf8');
const MANIFEST = JSON.parse(fs.readFileSync(path.join(FIXTURES, 'manifest.json'), 'utf8'));
const BASE_URL = 'http://fixture.test';
const MEDIA_URL = 'http://media.test';

// The markup of each fixture, read from the fixture itself: the script's site, how to find a
// post by id, where its thumbnails are, and posts that quote another post on the page
const SITES = {
    '4chan': {
        posts: 'div.post',
        site: '4chan',
        post: (doc, id) => doc.getElementById('p' + id),
        prefix: 'p',
        thumbs: 'a.fileThumb img',
        quotes: [['7770000006', '7770000004'], ['7770000016', '7770000014'], ['7770000017', '7770000016']],
        serverBacklinks: false
    },
    '8ch': {
        posts: 'div.post',
        site: 'vichan',
        // The fixture's OP has no id; its posts are known by the quote number link
        post: (doc, id) => doc.getElementById('reply_' + id) || (id === '7770000004' ? doc.querySelector('div.post.op') : null),
        prefix: '',
        thumbs: 'div.file a img',
        quotes: [['7770000007', '7770000006']],
        serverBacklinks: false
    },
    fuuka: {
        posts: 'td.comment, div.comment',
        site: 'fuuka',
        post: (doc, id) => doc.getElementById('p' + id),
        prefix: 'p',
        thumbs: 'a img.thumb',
        quotes: [['7770000006', '7770000005'], ['7770000007', '7770000006'], ['7770000008', '7770000007']],
        serverBacklinks: false
    },
    foolfuuka: {
        posts: 'article',
        site: 'foolfuuka',
        post: (doc, id) => doc.getElementById(id),
        prefix: '',
        thumbs: 'a.thread_image_link img',
        quotes: [['7770000005', '7770000004'], ['7770000006', '7770000004']],
        serverBacklinks: true
    },
    'foolfuuka-archivedmoe': {
        posts: 'article',
        site: 'foolfuuka',
        post: (doc, id) => doc.getElementById(id),
        prefix: '',
        thumbs: 'a.thread_image_link img',
        quotes: [['7770000008', '7770000004'], ['7770000009', '7770000004']],
        serverBacklinks: true
    },
    'foolfuuka-desuarchive': {
        posts: 'article',
        site: 'foolfuuka',
        post: (doc, id) => doc.getElementById(id),
        prefix: '',
        thumbs: 'a.thread_image_link img',
        quotes: [['7770000007', '7770000002'], ['7770000008', '7770000002']],
        serverBacklinks: true
    },
    lynxchan: {
        posts: 'div.postCell, div.opCell',
        site: 'lynxchan',
        post: (doc, id) => doc.getElementById(id),
        prefix: '',
        thumbs: 'a.imgLink img',
        quotes: [['7770000005', '7770000004'], ['7770000006', '7770000004'], ['7770000007', '7770000005']],
        serverBacklinks: false
    }
};

function readFixture(name) {
    return fs.readFileSync(path.join(FIXTURES, name + '.html'), 'utf8')
        .replaceAll('{{base}}', BASE_URL)
        .replaceAll('{{media}}', MEDIA_URL)
        .replace(/\{\{md5u?_\d+\}\}/g, 'AAAAAAAAAAAAAAAAAAAAAA==');
}

// Like General.GetReplacementURL: a link to an anchor on the page becomes just the fragment
function rewriteSamePageLinks(doc, pageURL) {
    for (const link of doc.querySelectorAll('a[href]')) {
        const url = new URL(link.getAttribute('href'), pageURL);
        if (url.hash && url.href.startsWith(pageURL + '#')) link.setAttribute('href', url.hash);
    }
}

// Like ThreadWatcher.ApplyDownloadPathReplace: a thumbnail links the saved image file
function rewriteThumbnailLinks(doc, pageURL, thumbs) {
    for (const image of doc.querySelectorAll(thumbs)) {
        const link = image.closest('a');
        link.setAttribute('href', path.posix.basename(new URL(link.getAttribute('href'), pageURL).pathname));
    }
}

function savedPage(name, scriptSite) {
    const config = SITES[name];
    const pageURL = BASE_URL + MANIFEST[name].pagePath;
    const dom = new JSDOM(readFixture(name), { url: pageURL });
    rewriteSamePageLinks(dom.window.document, pageURL);
    rewriteThumbnailLinks(dom.window.document, pageURL, config.thumbs);
    const html = dom.serialize();
    const script = '<script data-site="' + scriptSite + '">' + SCRIPT + '</script>';
    return html.replace('<head>', () => '<head>' + script);
}

// Resolves once the page is parsed and the script, which waits for DOMContentLoaded, has run
async function load(name, scriptSite = SITES[name].site) {
    const errors = [];
    const virtualConsole = new VirtualConsole();
    virtualConsole.on('jsdomError', (error) => errors.push(error));
    const dom = new JSDOM(savedPage(name, scriptSite), { url: 'file:///C:/threads/thread.html', runScripts: 'dangerously', virtualConsole });
    const page = { window: dom.window, doc: dom.window.document, config: SITES[name], errors };
    if (page.doc.readyState === 'loading') {
        await new Promise((resolve) => page.doc.addEventListener('DOMContentLoaded', resolve));
    }
    assertNoErrors(page);
    return page;
}

function assertNoErrors(page) {
    assert.deepEqual(page.errors.map((error) => error.message), []);
}

function quoteLink(page, fromId, toId) {
    const links = [...page.config.post(page.doc, fromId).querySelectorAll('a[href="#' + page.config.prefix + toId + '"]')];
    return links.find((link) => !link.hasAttribute('data-ctw-backlink'));
}

function mouse(page, element, type, related = null) {
    element.dispatchEvent(new page.window.MouseEvent(type, { bubbles: true, cancelable: true, clientX: 20, clientY: 30, relatedTarget: related }));
    assertNoErrors(page);
}

function click(page, element, options = {}) {
    const event = new page.window.MouseEvent('click', { bubbles: true, cancelable: true, button: 0, ...options });
    element.dispatchEvent(event);
    assertNoErrors(page);
    return event.defaultPrevented;
}

function previews(page) {
    return page.doc.querySelectorAll('[data-ctw-preview]');
}

// Marks the post so its copy in a preview can be recognized; cloning keeps the attribute
function markPost(page, id) {
    const post = page.config.post(page.doc, id);
    post.setAttribute('data-test-post', id);
    return post;
}

function backlinkIds(post) {
    return [...post.querySelectorAll('[data-ctw-backlink]')].map((link) => link.textContent.slice(2));
}

function isLocalImage(href) {
    return /\.(?:jpe?g|png|gif|webp)$/i.test(href);
}

for (const name of Object.keys(MANIFEST)) {
    test.describe(name, () => {
        test.it('has a test mapping', () => {
            assert.ok(SITES[name], 'no mapping for fixture ' + name);
        });

        test.it('finds the quote links and the posts they quote', async () => {
            const page = await load(name);
            for (const [fromId, toId] of page.config.quotes) {
                assert.ok(page.config.post(page.doc, fromId), fromId);
                assert.ok(page.config.post(page.doc, toId), toId);
                assert.ok(quoteLink(page, fromId, toId), fromId + ' quotes ' + toId);
            }
        });

        test.it('hovering a quote shows the quoted post and leaving hides it', async () => {
            const page = await load(name);
            for (const [fromId, toId] of page.config.quotes) {
                const link = quoteLink(page, fromId, toId);
                markPost(page, toId);

                mouse(page, link, 'mouseover');

                assert.equal(previews(page).length, 1, fromId + ' -> ' + toId);
                const copy = previews(page)[0].firstElementChild;
                assert.equal(copy.getAttribute('data-test-post'), toId);
                assert.equal(copy.hasAttribute('id'), false, 'the copy keeps no id');
                assert.equal(copy.querySelector('[id]'), null, 'the copy keeps no ids');

                mouse(page, link, 'mouseout', page.doc.body);

                assert.equal(previews(page).length, 0);
            }
        });

        test.it('a quote of a post that is not on the page does nothing', async () => {
            const page = await load(name);
            const [fromId, toId] = page.config.quotes[0];
            const link = quoteLink(page, fromId, toId);
            link.setAttribute('href', '#' + page.config.prefix + '7779999999');

            mouse(page, link, 'mouseover');

            assert.equal(previews(page).length, 0);
        });

        // Each fixture's quoting post has a link to its own number in its header
        test.it('a link to its own post shows no preview', async () => {
            const page = await load(name);
            const [fromId] = page.config.quotes[0];
            const post = page.config.post(page.doc, fromId);
            const link = [...post.querySelectorAll('a[href="#' + page.config.prefix + fromId + '"]')].find((l) => !l.hasAttribute('data-ctw-backlink'));
            assert.ok(link, 'self link of ' + fromId);

            mouse(page, link, 'mouseover');

            assert.equal(previews(page).length, 0);
            assert.equal(backlinkIds(post).includes(fromId), false);
        });

        test.it('a preview copies the post without the replies nested in it', async () => {
            const page = await load(name);
            for (const post of page.doc.querySelectorAll(page.config.posts)) post.setAttribute('data-test-nested', '');
            for (const [fromId, toId] of page.config.quotes) {
                mouse(page, quoteLink(page, fromId, toId), 'mouseover');

                const copy = previews(page)[0].firstElementChild;
                assert.equal(copy.querySelectorAll('[data-test-nested]').length, 0, toId);
            }
        });

        if (SITES[name] && SITES[name].serverBacklinks) {
            test.it('adds no backlinks, because the site lists them in the page', async () => {
                const page = await load(name);
                assert.equal(page.doc.querySelectorAll('[data-ctw-backlink]').length, 0);
            });
        }
        else {
            test.it('lists backlinks on each quoted post', async () => {
                const page = await load(name);
                for (const [fromId, toId] of page.config.quotes) {
                    const quoted = page.config.post(page.doc, toId);
                    assert.ok(backlinkIds(quoted).includes(fromId), toId + ' lists ' + fromId + ', has ' + backlinkIds(quoted));
                    const link = [...quoted.querySelectorAll('[data-ctw-backlink]')].find((l) => l.textContent === '>>' + fromId);
                    assert.equal(link.getAttribute('href'), '#' + page.config.prefix + fromId);
                }
            });

            test.it('a backlink is listed once per quoting post and previews it', async () => {
                const page = await load(name);
                const [fromId, toId] = page.config.quotes[0];
                const quoted = page.config.post(page.doc, toId);
                const ids = backlinkIds(quoted);
                assert.equal(new Set(ids).size, ids.length);
                const link = [...quoted.querySelectorAll('[data-ctw-backlink]')].find((l) => l.textContent === '>>' + fromId);
                markPost(page, fromId);

                mouse(page, link, 'mouseover');

                assert.equal(previews(page)[0].firstElementChild.getAttribute('data-test-post'), fromId);
            });
        }

        test.it('clicking a thumbnail shows the full image and clicking it again shows the thumbnail', async () => {
            const page = await load(name);
            const thumbs = [...page.doc.querySelectorAll(page.config.thumbs)];
            assert.equal(thumbs.length, MANIFEST[name].thumbnails);
            const thumb = thumbs.find((image) => isLocalImage(image.closest('a').getAttribute('href')));
            const link = thumb.closest('a');

            assert.equal(click(page, thumb), true);

            const full = link.querySelector('[data-ctw-full]');
            assert.equal(full.getAttribute('src'), link.getAttribute('href'));
            assert.equal(thumb.style.display, 'none');

            assert.equal(click(page, full), true);

            assert.equal(link.querySelector('[data-ctw-full]'), null);
            assert.equal(thumb.style.display, '');
        });

        test.it('a modified click opens the link as usual', async () => {
            const page = await load(name);
            const thumb = page.doc.querySelector(page.config.thumbs);
            for (const options of [{ ctrlKey: true }, { metaKey: true }, { shiftKey: true }, { button: 1 }]) {
                assert.equal(click(page, thumb, options), false, JSON.stringify(options));
                assert.equal(thumb.closest('a').querySelector('[data-ctw-full]'), null);
            }
        });

        test.it('a thumbnail of a video or of a file that is not on disk opens the link as usual', async () => {
            const page = await load(name);
            const thumb = page.doc.querySelector(page.config.thumbs);
            const link = thumb.closest('a');
            for (const href of ['file-1.webm', 'file-1.mp4', 'http://media.test/file-1.jpg', '//media.test/file-1.jpg', '/file-1.jpg']) {
                link.setAttribute('href', href);
                assert.equal(click(page, thumb), false, href);
                assert.equal(link.querySelector('[data-ctw-full]'), null, href);
            }
        });

        test.it('does nothing for a page without a known site', async () => {
            const page = await load(name, 'generic');
            const [fromId, toId] = page.config.quotes[0];

            mouse(page, quoteLink(page, fromId, toId), 'mouseover');

            assert.equal(previews(page).length, 0);
            assert.equal(page.doc.querySelectorAll('[data-ctw-backlink]').length, 0);
            assert.equal(click(page, page.doc.querySelector(page.config.thumbs)), false);
        });
    });
}
