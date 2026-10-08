// The browser's extension API: browser.* in Firefox, chrome.* in Chrome (both return promises in Manifest V3)
export function ext() {
    const api = globalThis.browser || globalThis.chrome;
    if (!api) throw new Error('No extension API');
    return api;
}

// The Origin the browser sends with this extension's POSTs: the chrome-extension scheme and the pinned id, or the
// moz-extension scheme and this install's UUID. Built from the parts, since URL.origin of a scheme that is not special
// is "null" in some engines.
export function extensionOrigin() {
    const url = new URL(ext().runtime.getURL(''));
    return url.protocol + '//' + url.host;
}

// The known domains, from the optional host permissions that scripts/package.mjs wrote from known-hosts.json
export function manifestHostPatterns() {
    return ext().runtime.getManifest().optional_host_permissions;
}
