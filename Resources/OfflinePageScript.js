// Inserted by ChanThreadWatch into saved thread pages, which keep none of the site's own
// scripts. The page's Content-Security-Policy allows only this exact text, by its SHA-256 hash.
// Adds quote previews, backlinks and inline image expansion that work offline. The page is
// untrusted: it is only read, cloned and added to. Nothing is requested, stored or navigated.
// The text holds no less-than sign, so no HTML parser can read markup in it.
(function () {
    'use strict';

    var SITES = {
        '4chan': {
            posts: 'div.post',
            postId: /^p(\d+)$/,
            prefix: 'p',
            quotes: 'a.quotelink',
            thumbs: 'a.fileThumb img',
            backlinks: fourChanBacklinks
        },
        vichan: {
            posts: 'div.post',
            postId: /^(?:op|reply)_(\d+)$/,
            idFromQuoteNumber: true,
            prefix: '',
            quotes: 'div.body a',
            thumbs: 'div.file a img',
            backlinks: afterQuoteNumber
        },
        fuuka: {
            posts: 'td[id], div[id]',
            postId: /^p(\d+)$/,
            prefix: 'p',
            quotes: 'blockquote a',
            thumbs: 'a img.thumb',
            backlinks: beforeMessage
        },
        foolfuuka: {
            posts: 'article.post, article.thread',
            postId: /^(\d+)$/,
            prefix: '',
            quotes: 'a',
            thumbs: 'a.thread_image_link img',
            // The site lists backlinks in the page already
            backlinks: null
        },
        lynxchan: {
            posts: 'div.postCell, div.opCell',
            postId: /^(\d+)$/,
            prefix: '',
            quotes: 'a',
            thumbs: 'a.imgLink img',
            backlinks: lynxChanBacklinks
        }
    };

    var IMAGE_PATH = /\.(?:jpe?g|png|gif|webp)$/i;
    var CONTROL_OR_SPACE = /[\x00-\x20\x7f]/;
    var QUOTE_NUMBER = /^#q(\d+)$/;
    var READY = 'data-ctw-ready';

    // The page's markup can shadow document properties by name (an element named "body" or
    // "createElement"), and a form's properties by its fields' names, so page nodes are only
    // used through the functions captured here
    var doc = document;
    var win = window;
    var getComputedStyle = win.getComputedStyle;

    function getter(proto, name) {
        return Object.getOwnPropertyDescriptor(proto, name).get;
    }

    var dom = {
        currentScript: getter(Document.prototype, 'currentScript'),
        documentElement: getter(Document.prototype, 'documentElement'),
        body: getter(Document.prototype, 'body'),
        readyState: getter(Document.prototype, 'readyState'),
        url: getter(Document.prototype, 'URL'),
        createElement: Document.prototype.createElement,
        getElementById: Document.prototype.getElementById,
        queryDocument: Document.prototype.querySelectorAll,
        addEventListener: EventTarget.prototype.addEventListener,
        parentElement: getter(Node.prototype, 'parentElement'),
        contains: Node.prototype.contains,
        cloneNode: Node.prototype.cloneNode,
        appendChild: Node.prototype.appendChild,
        insertBefore: Node.prototype.insertBefore,
        queryElement: Element.prototype.querySelectorAll,
        closest: Element.prototype.closest,
        matches: Element.prototype.matches,
        getAttribute: Element.prototype.getAttribute,
        setAttribute: Element.prototype.setAttribute,
        hasAttribute: Element.prototype.hasAttribute,
        removeAttribute: Element.prototype.removeAttribute,
        remove: Element.prototype.remove
    };

    var site = null;
    var quoteHref = null;
    var postsById = Object.create(null);
    var postIds = new Map();
    var preview = null;
    var previewLink = null;

    function each(list, action) {
        Array.prototype.forEach.call(list, action);
    }

    function queryAll(root, selector) {
        return root === doc ? dom.queryDocument.call(doc, selector) : dom.queryElement.call(root, selector);
    }

    function attribute(element, name) {
        return dom.getAttribute.call(element, name);
    }

    function hasAttribute(element, name) {
        return dom.hasAttribute.call(element, name);
    }

    function matches(element, selector) {
        return dom.matches.call(element, selector);
    }

    function closest(element, selector) {
        return dom.closest.call(element, selector);
    }

    function parentOf(node) {
        return dom.parentElement.call(node);
    }

    function create(tagName) {
        return dom.createElement.call(doc, tagName);
    }

    function listen(target, type, listener) {
        dom.addEventListener.call(target, type, listener);
    }

    function findSite() {
        var script = dom.currentScript.call(doc);
        var name = script ? attribute(script, 'data-site') : null;
        return name && Object.prototype.hasOwnProperty.call(SITES, name) ? SITES[name] : null;
    }

    // Posts

    function idAttribute(element) {
        var match = site.postId.exec(attribute(element, 'id') || '');
        return match ? match[1] : null;
    }

    // A post inside another one (a LynxChan or FoolFuuka OP holds its replies) has an id of its
    // own. A Fuuka post is a td that can hold a div with the same id.
    function isNestedPost(element, postId) {
        var id = matches(element, site.posts) ? idAttribute(element) : null;
        return id !== null && id !== postId;
    }

    function isOwnElement(element, post, postId) {
        var node = parentOf(element);
        while (node && node !== post && !isNestedPost(node, postId)) {
            node = parentOf(node);
        }
        return node === post;
    }

    // The post's elements, without those of the posts nested in it
    function ownElements(post, selector) {
        var postId = idAttribute(post);
        return Array.prototype.filter.call(queryAll(post, selector), function (element) {
            return isOwnElement(element, post, postId);
        });
    }

    function readPostId(post) {
        var id = idAttribute(post);
        if (id !== null) return id;
        return site.idFromQuoteNumber ? readQuoteNumberId(post) : null;
    }

    // vichan's OP has no post id, only the link that quotes its number
    function readQuoteNumberId(post) {
        var link = findQuoteNumberLink(post, null);
        return link ? QUOTE_NUMBER.exec(attribute(link, 'href'))[1] : null;
    }

    function findQuoteNumberLink(post, id) {
        return ownElements(post, 'a[href^="#q"]').find(function (link) {
            var match = QUOTE_NUMBER.exec(attribute(link, 'href'));
            return match !== null && (id === null || match[1] === id);
        }) || null;
    }

    // The first post with an id keeps it
    function registerPost(post) {
        var id = readPostId(post);
        if (id === null || postsById[id]) return;
        postsById[id] = post;
        postIds.set(post, id);
    }

    function closestPost(element) {
        var node = element;
        while (node && !postIds.has(node)) {
            node = parentOf(node);
        }
        return node;
    }

    // Quotes

    // Returns the post the link quotes, or null if it is not on the page or is the link's own post
    function quotedPost(link) {
        var match = quoteHref.exec(attribute(link, 'href') || '');
        var post = match ? postsById[match[1]] : null;
        return post && post !== closestPost(link) ? post : null;
    }

    function previewTarget(link) {
        if (!hasAttribute(link, 'data-ctw-backlink') && !matches(link, site.quotes)) return null;
        return quotedPost(link);
    }

    // Preview

    function pageBackground() {
        var color = getComputedStyle.call(win, dom.body.call(doc)).backgroundColor;
        return !color || color === 'transparent' || color === 'rgba(0, 0, 0, 0)' ? '#fff' : color;
    }

    // A copy of the post without the posts nested in it and without ids, so the page keeps
    // unique ids
    function clonePost(post) {
        var postId = idAttribute(post);
        var copy = dom.cloneNode.call(post, true);
        each(queryAll(copy, site.posts), function (element) {
            if (isNestedPost(element, postId)) dom.remove.call(element);
        });
        dom.removeAttribute.call(copy, 'id');
        each(queryAll(copy, '[id]'), function (element) {
            dom.removeAttribute.call(element, 'id');
        });
        return copy;
    }

    function createPreview(post) {
        var box = create('div');
        var style = box.style;
        box.setAttribute('data-ctw-preview', '');
        style.position = 'absolute';
        style.zIndex = '2147483647';
        style.maxWidth = '60%';
        style.padding = '4px';
        style.border = '1px solid #888';
        // A preview under the cursor must not take the mouse from the link it shows
        style.pointerEvents = 'none';
        style.background = pageBackground();
        box.appendChild(clonePost(post));
        return box;
    }

    function clamp(value, min, max) {
        return Math.max(min, Math.min(value, max));
    }

    // Next to the cursor, inside the window: above the cursor when there is no room below it
    function placePreview(box, event) {
        var size = box.getBoundingClientRect();
        var below = event.clientY + 12;
        var top = below + size.height > win.innerHeight ? event.clientY - 12 - size.height : below;
        var left = event.clientX + 12;
        box.style.left = (clamp(left, 0, win.innerWidth - size.width - 4) + win.pageXOffset) + 'px';
        box.style.top = (clamp(top, 0, win.innerHeight - size.height - 4) + win.pageYOffset) + 'px';
    }

    function showPreview(post, link, event) {
        hidePreview();
        preview = createPreview(post);
        previewLink = link;
        dom.appendChild.call(dom.body.call(doc), preview);
        placePreview(preview, event);
    }

    function hidePreview() {
        if (preview) dom.remove.call(preview);
        preview = null;
        previewLink = null;
    }

    function eventLink(event) {
        return event.target instanceof Element ? closest(event.target, 'a') : null;
    }

    // Moving between the link and the elements inside it keeps the preview
    function onMouseOver(event) {
        var link = eventLink(event);
        var post = link && link !== previewLink ? previewTarget(link) : null;
        if (post) showPreview(post, link, event);
    }

    function isInPreviewLink(node) {
        return node instanceof Node && dom.contains.call(previewLink, node);
    }

    // Only leaving the link hides the preview, not moving between it and the elements inside it
    function onMouseOut(event) {
        if (previewLink && isInPreviewLink(event.target) && !isInPreviewLink(event.relatedTarget)) hidePreview();
    }

    // Backlinks

    function insertion(parent, before) {
        return parent ? { parent: parent, before: before } : null;
    }

    function fourChanBacklinks(post, id) {
        return insertion(dom.getElementById.call(doc, 'pi' + id), null);
    }

    function afterQuoteNumber(post, id) {
        var link = findQuoteNumberLink(post, id);
        return link ? insertion(parentOf(link), link.nextSibling) : null;
    }

    function beforeMessage(post) {
        var message = ownElements(post, 'blockquote')[0];
        return message ? insertion(parentOf(message), message) : insertion(post, null);
    }

    function lynxChanBacklinks(post, id) {
        var panel = ownElements(post, '.panelBacklinks')[0];
        return panel ? insertion(panel, null) : afterQuoteNumber(post, id);
    }

    function createBacklink(id) {
        var link = create('a');
        link.setAttribute('href', '#' + site.prefix + id);
        link.setAttribute('data-ctw-backlink', '');
        link.textContent = '>>' + id;
        link.style.marginLeft = '4px';
        return link;
    }

    function createBacklinkList(ids) {
        var list = create('span');
        list.setAttribute('data-ctw-backlinks', '');
        list.style.fontSize = 'smaller';
        ids.forEach(function (id) {
            list.appendChild(createBacklink(id));
        });
        return list;
    }

    // Returns the ids of the posts that quote each post, by quoted post id, in page order
    function collectQuotes() {
        var quotedBy = Object.create(null);
        postIds.forEach(function (id, post) {
            ownElements(post, site.quotes).forEach(function (link) {
                var quoted = quotedPost(link);
                var quotedId = quoted ? postIds.get(quoted) : null;
                if (quotedId === null) return;
                quotedBy[quotedId] = quotedBy[quotedId] || [];
                if (quotedBy[quotedId].indexOf(id) === -1) quotedBy[quotedId].push(id);
            });
        });
        return quotedBy;
    }

    function addBacklinks() {
        var quotedBy = collectQuotes();
        Object.keys(quotedBy).forEach(function (id) {
            var place = site.backlinks(postsById[id], id);
            if (place) dom.insertBefore.call(place.parent, createBacklinkList(quotedBy[id]), place.before);
        });
    }

    // Images

    function parseURL(href, base) {
        try {
            return new URL(href, base);
        }
        catch (error) {
            return null;
        }
    }

    function isSameFileHost(url, page) {
        return url !== null && url.protocol === 'file:' && page.protocol === 'file:' && url.host === page.host;
    }

    // True if the link is an image file on the same disk as the page. A video, an image that
    // was not downloaded and is linked online, or anything else opens as usual.
    function isLocalImage(href) {
        if (!href || CONTROL_OR_SPACE.test(href)) return false;
        var page = new URL(dom.url.call(doc));
        var url = parseURL(href, page);
        return isSameFileHost(url, page) && IMAGE_PATH.test(url.pathname);
    }

    function expandImage(thumb, href) {
        var full = create('img');
        full.setAttribute('data-ctw-full', '');
        full.setAttribute('src', href);
        full.style.maxWidth = '100%';
        thumb.style.display = 'none';
        dom.insertBefore.call(parentOf(thumb), full, thumb.nextSibling);
    }

    function collapseImage(full) {
        var thumb = full.previousElementSibling;
        dom.remove.call(full);
        if (thumb) thumb.style.removeProperty('display');
    }

    function hasModifierKey(event) {
        return event.ctrlKey || event.metaKey || event.shiftKey || event.altKey;
    }

    // Middle and modified clicks open the link as usual
    function isPlainClick(event) {
        return event.button === 0 && !event.defaultPrevented && !hasModifierKey(event);
    }

    function thumbnailHref(thumb) {
        var link = matches(thumb, site.thumbs) ? closest(thumb, 'a') : null;
        var href = link ? attribute(link, 'href') : null;
        return isLocalImage(href) ? href : null;
    }

    // Returns true if the click expanded or collapsed an image
    function toggleImage(image) {
        if (hasAttribute(image, 'data-ctw-full')) {
            collapseImage(image);
            return true;
        }
        var href = thumbnailHref(image);
        if (href === null) return false;
        expandImage(image, href);
        return true;
    }

    function onClick(event) {
        var image = isPlainClick(event) && event.target instanceof Element ? closest(event.target, 'img') : null;
        if (image && toggleImage(image)) event.preventDefault();
    }

    // Listeners come first, so a failure while adding backlinks leaves the rest working
    function start() {
        listen(doc, 'mouseover', onMouseOver);
        listen(doc, 'mouseout', onMouseOut);
        listen(doc, 'click', onClick);
        each(queryAll(doc, site.posts), registerPost);
        if (site.backlinks) addBacklinks();
    }

    // Runs once per page, also when the page holds the script twice
    function claimPage() {
        var root = dom.documentElement.call(doc);
        if (!root || hasAttribute(root, READY)) return false;
        dom.setAttribute.call(root, READY, '');
        return true;
    }

    // The script runs in the head, before the posts are parsed
    function main() {
        site = findSite();
        if (!site || !claimPage()) return;
        quoteHref = new RegExp('^#' + site.prefix + '(\\d+)$');
        if (dom.readyState.call(doc) === 'loading') {
            listen(doc, 'DOMContentLoaded', start);
        }
        else {
            start();
        }
    }

    main();
})();
