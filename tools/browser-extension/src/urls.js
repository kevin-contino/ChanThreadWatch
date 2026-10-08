// The thread address filter. The menu patterns only decide where the menu shows; every
// address is checked here before it is sent.

// /<board>/thread/<n>[/<slug>][/] (4chan, warosu, FoolFuuka) and /<board>/res/<n>.html (8ch, endchan)
const THREAD_PATHS = [/^\/[^/]+\/thread\/\d+(\/[^/]*)?\/?$/, /^\/[^/]+\/res\/\d+\.html$/];
// Printable ASCII only: a host with fullwidth digits or a Unicode label is refused, as the API refuses a host that
// differs from its ASCII form (browsers give addresses already in ASCII)
const PRINTABLE_ASCII = /^[\x21-\x7e]+$/;
const HOST_PATTERN = /^\*:\/\/\*\.([a-z0-9.-]+)\/\*$/;

// The optional host permission of a known domain; "*." also matches the domain itself
export function hostPattern(domain) {
    return '*://*.' + domain + '/*';
}

export function domainsOf(patterns) {
    return patterns.map(domainOf);
}

function domainOf(pattern) {
    const match = HOST_PATTERN.exec(pattern);
    if (!match) throw new Error('Not a host pattern: ' + pattern);
    return match[1];
}

// The context menu's patterns: the two thread shapes on every known domain
export function threadPatterns(domains) {
    return domains.flatMap((domain) => ['*://*.' + domain + '/*/thread/*', '*://*.' + domain + '/*/res/*']);
}

// The thread address without its query and fragment, or null when the text is not a thread of a known domain
export function threadUrl(text, domains) {
    const url = parseUrl(text);
    if (!url || !isThreadAddress(url, domains)) return null;
    url.search = '';
    url.hash = '';
    return url.href;
}

function parseUrl(text) {
    if (typeof text !== 'string' || !PRINTABLE_ASCII.test(text)) return null;
    return URL.canParse(text) ? new URL(text) : null;
}

function isThreadAddress(url, domains) {
    return isWebScheme(url.protocol) && url.username === '' && url.password === '' && isKnownHost(url.hostname, domains) && isThreadPath(url.pathname);
}

function isWebScheme(protocol) {
    return protocol === 'http:' || protocol === 'https:';
}

// A known domain or a subdomain of one, by whole labels; a host with a trailing dot matches neither
function isKnownHost(host, domains) {
    return domains.some((domain) => host === domain || host.endsWith('.' + domain));
}

function isThreadPath(path) {
    return THREAD_PATHS.some((pattern) => pattern.test(path));
}

// The thread addresses among the texts, in order and each once, and how many texts were not a thread
export function threadUrlsOf(texts, domains) {
    const urls = [];
    let skipped = 0;
    for (const text of texts) {
        const url = threadUrl(text, domains);
        if (!url) skipped++;
        else if (!urls.includes(url)) urls.push(url);
    }
    return { urls, skipped };
}
