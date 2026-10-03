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
        // The OP holds the replies
        opId: '7770000004',
        posts: 'article',
        site: 'foolfuuka',
        post: (doc, id) => doc.getElementById(id),
        prefix: '',
        thumbs: 'a.thread_image_link img',
        quotes: [['7770000005', '7770000004'], ['7770000006', '7770000004']],
        serverBacklinks: true
    },
    'foolfuuka-archivedmoe': {
        // The OP holds the replies
        opId: '7770000004',
        posts: 'article',
        site: 'foolfuuka',
        post: (doc, id) => doc.getElementById(id),
        prefix: '',
        thumbs: 'a.thread_image_link img',
        quotes: [['7770000008', '7770000004'], ['7770000009', '7770000004']],
        serverBacklinks: true
    },
    'foolfuuka-desuarchive': {
        // The OP holds the replies
        opId: '7770000002',
        posts: 'article',
        site: 'foolfuuka',
        post: (doc, id) => doc.getElementById(id),
        prefix: '',
        thumbs: 'a.thread_image_link img',
        quotes: [['7770000007', '7770000002'], ['7770000008', '7770000002']],
        serverBacklinks: true
    },
    lynxchan: {
        // The OP holds the replies
        opId: '7770000001',
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

// prepare changes the saved page before the script runs; scripts is how often the page holds it
function savedPage(name, scriptSite, { prepare = null, scripts = 1 } = {}) {
    const config = SITES[name];
    const pageURL = BASE_URL + MANIFEST[name].pagePath;
    const dom = new JSDOM(readFixture(name), { url: pageURL });
    rewriteSamePageLinks(dom.window.document, pageURL);
    rewriteThumbnailLinks(dom.window.document, pageURL, config.thumbs);
    if (prepare) prepare(dom.window.document, config);
    const html = dom.serialize();
    const script = ('<script data-site="' + scriptSite + '">' + SCRIPT + '</script>').repeat(scripts);
    return html.replace('<head>', () => '<head>' + script);
}

// Resolves once the page is parsed and the script, which waits for DOMContentLoaded, has run
async function load(name, scriptSite = SITES[name].site, options = {}) {
    const errors = [];
    const virtualConsole = new VirtualConsole();
    virtualConsole.on('jsdomError', (error) => errors.push(error));
    const dom = new JSDOM(savedPage(name, scriptSite, options), { url: 'file:///C:/threads/thread.html', runScripts: 'dangerously', virtualConsole });
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
    return findQuoteLink(page.doc, page.config, fromId, toId);
}

function findQuoteLink(doc, config, fromId, toId) {
    const links = [...config.post(doc, fromId).querySelectorAll('a[href="#' + config.prefix + toId + '"]')];
    return links.find((link) => !link.hasAttribute('data-ctw-backlink'));
}

// Points the first listed quote at another post before the script runs
function requote(toId) {
    return (doc, config) => {
        const [fromId, quotedId] = config.quotes[0];
        findQuoteLink(doc, config, fromId, quotedId).setAttribute('href', '#' + config.prefix + toId(fromId, config));
    };
}

function backlinkCount(page) {
    return [...page.doc.querySelectorAll('[data-ctw-backlink]')].filter((link) => !link.closest('[data-ctw-preview]')).length;
}

function mouse(page, element, type, related = null, clientX = 20, clientY = 30) {
    element.dispatchEvent(new page.window.MouseEvent(type, { bubbles: true, cancelable: true, clientX, clientY, relatedTarget: related }));
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

        // The quote is changed to quote its own post, so only the check for the link's own post
        // keeps the preview and the backlink away
        test.it('a quote of its own post shows no preview and adds no backlink', async () => {
            const page = await load(name, undefined, { prepare: requote((fromId) => fromId) });
            const [fromId] = page.config.quotes[0];
            const post = page.config.post(page.doc, fromId);
            const link = quoteLink(page, fromId, fromId);
            assert.ok(link, 'self quote of ' + fromId);

            mouse(page, link, 'mouseover');

            assert.equal(previews(page).length, 0);
            assert.equal(backlinkIds(post).includes(fromId), false);
        });

        test.it('moving between a quote and the elements inside it keeps one preview', async () => {
            const addChild = (doc, config) => {
                const [fromId, toId] = config.quotes[0];
                const child = doc.createElement('span');
                child.setAttribute('data-test-child', '');
                child.textContent = 'x';
                findQuoteLink(doc, config, fromId, toId).appendChild(child);
            };
            const page = await load(name, undefined, { prepare: addChild });
            const [fromId, toId] = page.config.quotes[0];
            const link = quoteLink(page, fromId, toId);
            const child = link.querySelector('[data-test-child]');

            mouse(page, link, 'mouseover', page.doc.body);
            const shown = previews(page)[0];
            mouse(page, link, 'mouseout', child);
            mouse(page, child, 'mouseover', link);
            mouse(page, child, 'mouseout', link);
            mouse(page, link, 'mouseover', child);

            assert.equal(previews(page).length, 1);
            assert.equal(previews(page)[0], shown, 'the same preview, not a new one');

            mouse(page, child, 'mouseout', page.doc.body);

            assert.equal(previews(page).length, 0);
        });

        test.it('hovering again or hovering another quote leaves one preview', async () => {
            const page = await load(name);
            const links = page.config.quotes.map(([fromId, toId]) => quoteLink(page, fromId, toId));
            const backlinks = backlinkCount(page);

            mouse(page, links[0], 'mouseover');
            const shown = previews(page)[0];
            mouse(page, links[0], 'mouseover');
            mouse(page, links[0], 'mouseover');

            assert.equal(previews(page).length, 1);
            assert.equal(previews(page)[0], shown);

            mouse(page, links[links.length - 1], 'mouseover');

            assert.equal(previews(page).length, 1);
            assert.equal(backlinkCount(page), backlinks);
        });

        test.it('a page that holds the script twice runs it once', async () => {
            const once = await load(name);
            const twice = await load(name, undefined, { scripts: 2 });
            const [fromId, toId] = twice.config.quotes[0];

            mouse(twice, quoteLink(twice, fromId, toId), 'mouseover');

            assert.equal(backlinkCount(twice), backlinkCount(once));
            assert.equal(previews(twice).length, 1);
        });

        test.it('a preview stays inside the window', async () => {
            const page = await load(name);
            const [fromId, toId] = page.config.quotes[0];

            mouse(page, quoteLink(page, fromId, toId), 'mouseover', null, 5000, 5000);

            const style = previews(page)[0].style;
            assert.ok(parseFloat(style.left) <= page.window.innerWidth, style.left);
            assert.ok(parseFloat(style.top) <= page.window.innerHeight, style.top);
            assert.ok(parseFloat(style.left) >= 0 && parseFloat(style.top) >= 0);
        });

        // A preview moved under the cursor by the clamp would otherwise take the mouse from the
        // link, hide, and show again
        test.it('a preview does not take the mouse from the link', async () => {
            const page = await load(name);
            const [fromId, toId] = page.config.quotes[0];

            mouse(page, quoteLink(page, fromId, toId), 'mouseover', null, 5000, 5000);

            assert.equal(previews(page)[0].style.pointerEvents, 'none');
        });

        // In a browser, elements named like document properties shadow them, and a form's fields
        // shadow the form's properties. jsdom does not shadow them, so this only shows that such
        // markup does not stop the script; a browser is needed to see the shadowing itself.
        test.it('works when the markup shadows document and form properties', async () => {
            const clobber = (doc, config) => {
                for (const [tag, name] of [['img', 'createElement'], ['form', 'body'], ['img', 'getElementById'], ['img', 'querySelectorAll'],
                    ['img', 'readyState'], ['img', 'documentElement'], ['img', 'addEventListener'], ['img', 'URL']]) {
                    const element = doc.createElement(tag);
                    element.setAttribute('name', name);
                    doc.body.appendChild(element);
                }
                const [fromId] = config.quotes[0];
                const post = config.post(doc, fromId);
                const form = doc.createElement('form');
                for (const name of ['parentElement', 'querySelectorAll', 'contains', 'closest', 'matches', 'remove', 'getAttribute', 'appendChild', 'insertBefore']) {
                    const input = doc.createElement('input');
                    input.setAttribute('name', name);
                    form.appendChild(input);
                }
                post.parentNode.insertBefore(form, post);
                form.appendChild(post);
            };
            const reference = await load(name);
            const page = await load(name, undefined, { prepare: clobber });
            const [fromId, toId] = page.config.quotes[0];
            markPost(page, toId);

            mouse(page, quoteLink(page, fromId, toId), 'mouseover');

            assert.equal(previews(page).length, 1);
            assert.equal(previews(page)[0].firstElementChild.getAttribute('data-test-post'), toId);
            assert.equal(backlinkCount(page), backlinkCount(reference));
            const thumb = [...page.doc.querySelectorAll(page.config.thumbs)].find((image) => isLocalImage(image.closest('a').getAttribute('href')));
            assert.equal(click(page, thumb), true);
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

        if (SITES[name] && SITES[name].opId) {
            // The OP's element holds the replies; a reply's quote is the reply's, not the OP's
            test.it('a reply inside the OP that quotes the OP previews it and is listed by it', async () => {
                const opId = SITES[name].opId;
                const page = await load(name, undefined, { prepare: requote(() => opId) });
                const [fromId] = page.config.quotes[0];
                for (const post of page.doc.querySelectorAll(page.config.posts)) post.setAttribute('data-test-nested', '');
                const op = page.config.post(page.doc, opId);
                assert.ok(op.contains(page.config.post(page.doc, fromId)), 'the reply is inside the OP');

                mouse(page, quoteLink(page, fromId, opId), 'mouseover');

                assert.equal(previews(page).length, 1);
                const copy = previews(page)[0].firstElementChild;
                assert.equal(copy.querySelectorAll('[data-test-nested]').length, 0);
                if (!page.config.serverBacklinks) {
                    assert.ok(backlinkIds(op).includes(fromId));
                    assert.equal(backlinkIds(op).includes(opId), false);
                }
            });
        }

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
            for (const href of ['file-1.webm', 'file-1.mp4', 'http://media.test/file-1.jpg', '//media.test/file-1.jpg', '\t//host/a.png', ' //host/a.png',
                'java\tscript:x.png', 'javascript:x.png', 'data:image/png;base64,AAAA', 'file://host/a.png', 'file-1.png\n']) {
                link.setAttribute('href', href);
                assert.equal(click(page, thumb), false, href);
                assert.equal(link.querySelector('[data-ctw-full]'), null, href);
            }
        });

        test.it('a thumbnail of an image in a folder next to the page expands', async () => {
            const page = await load(name);
            const thumb = page.doc.querySelector(page.config.thumbs);
            const link = thumb.closest('a');
            for (const href of ['images/file-1.png', '../file-1.jpeg', 'file%201.webp', 'file-1.GIF']) {
                link.setAttribute('href', href);
                assert.equal(click(page, thumb), true, href);
                assert.equal(click(page, link.querySelector('[data-ctw-full]')), true, href);
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
