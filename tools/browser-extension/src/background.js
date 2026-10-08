// The background: the "Watch this thread" menu, the adds and the status for the popup. Thread adds and proofs run
// here, the context whose requests were observed against a test server. The popup sends its addresses here, where they
// are checked again.
import { ApiFailure, checkServer, loadPairing, watchUrls } from './api.js';
import { ext, manifestHostPatterns } from './browser.js';
import { ADDED_TEXT, ALREADY_WATCHED_TEXT, EXTENSION_ERROR_TEXT, NO_THREAD_TABS_TEXT, NOT_A_THREAD_TEXT } from './errors.js';
import { domainsOf, threadPatterns, threadUrl, threadUrlsOf } from './urls.js';

const MENU_TITLE = 'Watch this thread';
const LINK_ITEM = 'watch-thread-link';
const PAGE_ITEM = 'watch-thread-page';
const BADGE_MS = 4000;
let badgeTimer = null;
// The menu builds, one after the other, so an install and a start at the same moment never leave four items
let menuBuild = Promise.resolve();

function knownDomains() {
    return domainsOf(manifestHostPatterns());
}

// Made again at install and at each start: removeAll, then the link item (thread links on any page) and the page item
// (on a thread page)
export function createMenus() {
    menuBuild = menuBuild.then(buildMenus, buildMenus);
    return menuBuild;
}

async function buildMenus() {
    const menus = ext().contextMenus;
    const patterns = threadPatterns(knownDomains());
    await menus.removeAll();
    menus.create({ id: LINK_ITEM, title: MENU_TITLE, contexts: ['link'], targetUrlPatterns: patterns }, readLastError);
    menus.create({ id: PAGE_ITEM, title: MENU_TITLE, contexts: ['page'], documentUrlPatterns: patterns }, readLastError);
}

// Reading runtime.lastError in the callback marks a failed create as handled (Chrome logs it otherwise)
function readLastError() {
    return ext().runtime.lastError;
}

function menuUrl(info) {
    return info.menuItemId === LINK_ITEM ? info.linkUrl : info.pageUrl;
}

// A menu click has no page to show an error in; a failure shows "!" on the badge if it can
function onMenuClicked(info) {
    return watchOne(menuUrl(info)).catch(markFailed);
}

async function markFailed() {
    try {
        await ext().action.setBadgeText({ text: '!' });
    } catch {
        // Nothing left to show it in
    }
}

// One address from the menu or the popup's "Watch this tab"
export async function watchOne(text) {
    const url = threadUrl(text, knownDomains());
    if (!url) return report(NOT_A_THREAD_TEXT, '!');
    return runWatch([url], singleResult);
}

function singleResult(summary) {
    if (summary.added) return { text: ADDED_TEXT, badge: '+' };
    if (summary.already) return { text: ALREADY_WATCHED_TEXT, badge: '=' };
    return { text: summary.last.text, badge: '!' };
}

// The popup's "Watch all chan tabs": the addresses are filtered again, then added after one proof. Without a thread
// tab nothing is sent, saved or shown on the badge.
export async function watchAll(texts, skippedByPopup) {
    const { urls, skipped } = threadUrlsOf(texts, knownDomains());
    if (urls.length === 0) return { text: NO_THREAD_TABS_TEXT };
    const total = skipped + skippedByPopup;
    return runWatch(urls, (summary) => ({ text: summaryText(summary, total), badge: summaryBadge(summary) }));
}

// added + already + failed + not sent = the thread addresses
function summaryText(summary, skipped) {
    const stop = summary.stop ? ' ' + stopText(summary.stop, summary.notSent) : '';
    return 'Added ' + summary.added + '.' + stop + ' Already watched ' + summary.already + ', skipped ' + skipped + ' (not a thread), failed ' + summary.failed + '.';
}

function stopText(stop, notSent) {
    if (stop.status === 429) return 'Wait ' + stop.retryAfter + ' seconds for the rest (' + notSent + ' not sent).';
    return stop.text + ' ' + notSent + ' not sent.';
}

function summaryBadge(summary) {
    if (summary.added) return '+';
    return summary.stop || summary.failed ? '!' : '=';
}

// A failure before any add (not paired, not reachable, proof mismatch) is shown as it is; any other error is a bug
// and is thrown
async function runWatch(urls, resultOf) {
    try {
        const result = resultOf(await watchUrls(urls));
        return report(result.text, result.badge);
    } catch (error) {
        if (!(error instanceof ApiFailure)) throw error;
        return report(error.message, '!');
    }
}

// The last result for the popup, and the badge for 4 seconds
async function report(text, badge) {
    await ext().storage.local.set({ lastResult: text });
    await ext().action.setBadgeText({ text: badge });
    clearTimeout(badgeTimer);
    badgeTimer = setTimeout(clearBadge, BADGE_MS);
    return { text };
}

function clearBadge() {
    ext().action.setBadgeText({ text: '' });
}

// The popup's status line
export async function status() {
    const pairing = await loadPairing();
    if (!pairing) return { text: 'Not paired' };
    try {
        await checkServer(pairing);
        return { text: 'Paired with ' + pairing.serverName };
    } catch (error) {
        if (!(error instanceof ApiFailure)) throw error;
        return { text: ['unreachable', 'timeout'].includes(error.kind) ? 'Not reachable on port ' + pairing.port : error.message };
    }
}

const HANDLERS = new Map([
    ['status', () => status()],
    ['watchOne', (message) => watchOne(message.url)],
    ['watchAll', (message) => watchAll(stringsOf(message.urls), countOf(message.skipped))]
]);

function stringsOf(value) {
    return Array.isArray(value) ? value.filter((item) => typeof item === 'string') : [];
}

function countOf(value) {
    return Number.isInteger(value) && value >= 0 ? value : 0;
}

// Only this extension's own pages; nothing listens to other extensions or web pages. A handler that fails still
// answers, so the popup never waits for nothing.
function onMessage(message, sender, sendResponse) {
    const handler = sender.id === ext().runtime.id && message !== null && typeof message === 'object' ? HANDLERS.get(message.type) : undefined;
    if (!handler) return false;
    handler(message).then(sendResponse, () => sendResponse({ text: EXTENSION_ERROR_TEXT }));
    return true;
}

ext().runtime.onInstalled.addListener(createMenus);
ext().runtime.onStartup.addListener(createMenus);
ext().contextMenus.onClicked.addListener(onMenuClicked);
ext().runtime.onMessage.addListener(onMessage);
