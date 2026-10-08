// The popup: status line, "Watch this tab", "Watch all chan tabs", the last result and a link to the options. The
// adds run in the background; the popup only reads the tab addresses.
import { ext, manifestHostPatterns } from './browser.js';
import { EXTENSION_ERROR_TEXT } from './errors.js';
import { domainsOf, threadUrlsOf } from './urls.js';

export const PERMISSION_DENIED_TEXT = 'Chan Thread Watch may not read the tabs of the chan sites, so nothing was sent.';

// The active tab's address, readable through activeTab after the user opened the popup
export async function watchThisTab() {
    const [tab] = await ext().tabs.query({ active: true, currentWindow: true });
    return ext().runtime.sendMessage({ type: 'watchOne', url: tab ? tab.url : '' });
}

// permissions.request is the first call (before any await), as it needs the click's user gesture
export async function watchAllChanTabs() {
    const origins = manifestHostPatterns();
    const granted = await ext().permissions.request({ origins });
    if (!granted) return { text: PERMISSION_DENIED_TEXT };
    const tabs = await ext().tabs.query({ url: origins });
    const { urls, skipped } = threadUrlsOf(tabs.map((tab) => tab.url), domainsOf(origins));
    return ext().runtime.sendMessage({ type: 'watchAll', urls, skipped });
}

function byId(id) {
    return document.getElementById(id);
}

// The text of an answer; a failed call or an empty answer shows the general error text instead of nothing
export async function textOf(pending) {
    try {
        const result = await pending;
        return result && typeof result.text === 'string' ? result.text : EXTENSION_ERROR_TEXT;
    } catch {
        return EXTENSION_ERROR_TEXT;
    }
}

async function show(pending) {
    byId('result').textContent = await textOf(pending);
}

async function showStatus() {
    byId('status').textContent = await textOf(ext().runtime.sendMessage({ type: 'status' }));
}

async function showLastResult() {
    const saved = await ext().storage.local.get('lastResult');
    if (typeof saved.lastResult === 'string') byId('result').textContent = saved.lastResult;
}

function init() {
    byId('watch-tab').addEventListener('click', () => show(watchThisTab()));
    byId('watch-all').addEventListener('click', () => show(watchAllChanTabs()));
    byId('options').addEventListener('click', () => ext().runtime.openOptionsPage());
    showLastResult().catch(() => undefined);
    showStatus();
}

// The tests import this module without a page
if (globalThis.document) init();
