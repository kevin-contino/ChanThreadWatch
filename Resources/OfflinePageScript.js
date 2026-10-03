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

    var LOCAL_IMAGE = /^(?![a-z][a-z0-9+.\-]*:)(?![\/\\])[^?#]*\.(?:jpe?g|png|gif|webp)$/i;
    var QUOTE_NUMBER = /^#q(\d+)$/;

    var site = null;
    var quoteHref = null;
    var postsById = Object.create(null);
    var postIds = new Map();
    var preview = null;

    function each(list, action) {
        Array.prototype.forEach.call(list, action);
    }

    function findSite() {
        var script = document.currentScript;
        var name = script ? script.getAttribute('data-site') : null;
        return name && Object.prototype.hasOwnProperty.call(SITES, name) ? SITES[name] : null;
    }

    // Posts

    function readPostId(post) {
        var match = site.postId.exec(post.id);
        if (match) return match[1];
        return site.idFromQuoteNumber ? readQuoteNumberId(post) : null;
    }

    // vichan's OP has no post id, only the link that quotes its number
    function readQuoteNumberId(post) {
        var link = findQuoteNumberLink(post, null);
        return link ? QUOTE_NUMBER.exec(link.getAttribute('href'))[1] : null;
    }

    function findQuoteNumberLink(post, id) {
        var links = post.querySelectorAll('a[href^="#q"]');
        return Array.prototype.find.call(links, function (link) {
            var match = QUOTE_NUMBER.exec(link.getAttribute('href'));
            return match !== null && (id === null || match[1] === id);
        }) || null;
    }

    // The first element with an id is the post; for example a Fuuka post is a td that can hold
    // a div with the same id
    function registerPost(post) {
        var id = readPostId(post);
        if (id === null || postsById[id]) return;
        postsById[id] = post;
        postIds.set(post, id);
    }

    function closestPost(element) {
        var node = element;
        while (node && !postIds.has(node)) {
            node = node.parentElement;
        }
        return node;
    }

    // Links inside a post nested in this one (a LynxChan or FoolFuuka OP holds its replies)
    // belong to the nested post
    function ownElements(post, selector) {
        return Array.prototype.filter.call(post.querySelectorAll(selector), function (element) {
            return closestPost(element) === post;
        });
    }

    // Quotes

    // Returns the post the link quotes, or null if it is not on the page or is the link's own post
    function quotedPost(link) {
        var match = quoteHref.exec(link.getAttribute('href') || '');
        var post = match ? postsById[match[1]] : null;
        return post && post !== closestPost(link) ? post : null;
    }

    function previewTarget(link) {
        if (!link.hasAttribute('data-ctw-backlink') && !link.matches(site.quotes)) return null;
        return quotedPost(link);
    }

    // Preview

    function pageBackground() {
        var color = window.getComputedStyle(document.body).backgroundColor;
        return !color || color === 'transparent' || color === 'rgba(0, 0, 0, 0)' ? '#fff' : color;
    }

    // A copy of the post without the posts nested in it and without ids, so the page keeps
    // unique ids
    function clonePost(post) {
        var id = postIds.get(post);
        var copy = post.cloneNode(true);
        each(copy.querySelectorAll(site.posts), function (element) {
            var nestedId = readPostId(element);
            if (nestedId !== null && nestedId !== id) element.remove();
        });
        copy.removeAttribute('id');
        each(copy.querySelectorAll('[id]'), function (element) {
            element.removeAttribute('id');
        });
        return copy;
    }

    function createPreview(post, event) {
        var box = document.createElement('div');
        var style = box.style;
        box.setAttribute('data-ctw-preview', '');
        style.position = 'absolute';
        style.zIndex = '2147483647';
        style.left = (event.clientX + window.pageXOffset + 12) + 'px';
        style.top = (event.clientY + window.pageYOffset + 12) + 'px';
        style.maxWidth = '60%';
        style.padding = '4px';
        style.border = '1px solid #888';
        style.background = pageBackground();
        box.appendChild(clonePost(post));
        return box;
    }

    function hidePreview() {
        if (preview) preview.remove();
        preview = null;
    }

    function onMouseOver(event) {
        var link = event.target.closest ? event.target.closest('a') : null;
        var post = link ? previewTarget(link) : null;
        if (!post) return;
        hidePreview();
        preview = createPreview(post, event);
        document.body.appendChild(preview);
        link.addEventListener('mouseout', hidePreview, { once: true });
    }

    // Backlinks

    function insertion(parent, before) {
        return parent ? { parent: parent, before: before } : null;
    }

    function fourChanBacklinks(post, id) {
        return insertion(document.getElementById('pi' + id), null);
    }

    function afterQuoteNumber(post, id) {
        var link = findQuoteNumberLink(post, id);
        return link ? insertion(link.parentNode, link.nextSibling) : null;
    }

    function beforeMessage(post) {
        var message = ownElements(post, 'blockquote')[0];
        return message ? insertion(message.parentNode, message) : insertion(post, null);
    }

    function lynxChanBacklinks(post, id) {
        var panel = ownElements(post, '.panelBacklinks')[0];
        return panel ? insertion(panel, null) : afterQuoteNumber(post, id);
    }

    function createBacklink(id) {
        var link = document.createElement('a');
        link.setAttribute('href', '#' + site.prefix + id);
        link.setAttribute('data-ctw-backlink', '');
        link.textContent = '>>' + id;
        link.style.marginLeft = '4px';
        return link;
    }

    function createBacklinkList(ids) {
        var list = document.createElement('span');
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
            var post = postsById[id];
            var place = site.backlinks(post, id);
            if (place) place.parent.insertBefore(createBacklinkList(quotedBy[id]), place.before);
        });
    }

    // Images

    function expandImage(thumb, href) {
        var full = document.createElement('img');
        full.setAttribute('data-ctw-full', '');
        full.setAttribute('src', href);
        full.style.maxWidth = '100%';
        thumb.style.display = 'none';
        thumb.parentNode.insertBefore(full, thumb.nextSibling);
    }

    function collapseImage(full) {
        var thumb = full.previousElementSibling;
        full.remove();
        if (thumb) thumb.style.removeProperty('display');
    }

    function hasModifierKey(event) {
        return event.ctrlKey || event.metaKey || event.shiftKey || event.altKey;
    }

    // Middle and modified clicks open the link as usual
    function isPlainClick(event) {
        return event.button === 0 && !event.defaultPrevented && !hasModifierKey(event);
    }

    // Returns the link of a thumbnail whose image is a file on disk, or null. A video, or an
    // image that was not downloaded and is linked online, opens as usual.
    function localImageHref(thumb) {
        var link = thumb.closest('a');
        var href = link ? link.getAttribute('href') : null;
        return href && thumb.matches(site.thumbs) && LOCAL_IMAGE.test(href) ? href : null;
    }

    // Returns true if the click expanded or collapsed an image
    function toggleImage(image) {
        if (image.hasAttribute('data-ctw-full')) {
            collapseImage(image);
            return true;
        }
        var href = localImageHref(image);
        if (href === null) return false;
        expandImage(image, href);
        return true;
    }

    function onClick(event) {
        var image = isPlainClick(event) && event.target.closest ? event.target.closest('img') : null;
        if (image && toggleImage(image)) event.preventDefault();
    }

    function start() {
        each(document.querySelectorAll(site.posts), registerPost);
        if (site.backlinks) addBacklinks();
        document.addEventListener('mouseover', onMouseOver);
        document.addEventListener('click', onClick);
    }

    // The script runs in the head, before the posts are parsed
    function main() {
        site = findSite();
        if (!site) return;
        quoteHref = new RegExp('^#' + site.prefix + '(\\d+)$');
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', start);
        }
        else {
            start();
        }
    }

    main();
})();
