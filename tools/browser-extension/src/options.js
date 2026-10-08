// The options page: the port, pairing with a code, and "Forget pairing". Pairing runs here, not in the background,
// since a Chrome service worker can stop between the hello and the user's confirm.
import { ApiFailure, changePort, finishPairing, forgetPairing, isValidPort, loadPairing, savedPort, startPairing } from './api.js';
import { PORT_TEXT } from './errors.js';

let session = null;

function byId(id) {
    return document.getElementById(id);
}

function setStatus(text) {
    byId('status').textContent = text;
}

async function showPairing() {
    const pairing = await loadPairing();
    setStatus(pairing ? 'Paired with ' + pairing.serverName : 'Not paired');
}

// The port typed in the field, or null when it is not a port from 1024 to 65535
export function parsePort(text) {
    const port = /^\d{1,5}$/.test(String(text).trim()) ? Number(text) : NaN;
    return isValidPort(port) ? port : null;
}

function readPort() {
    return parsePort(byId('port').value);
}

function setBusy(busy) {
    for (const id of ['pair', 'confirm-yes', 'confirm-no', 'forget', 'port']) byId(id).disabled = busy;
}

function endSession() {
    session = null;
    byId('confirm').hidden = true;
}

// Runs an action with the controls disabled; an ApiFailure is shown, any other error is a bug and is thrown
async function run(action) {
    setBusy(true);
    try {
        await action();
    } catch (error) {
        if (!(error instanceof ApiFailure)) throw error;
        setStatus(error.message);
    } finally {
        setBusy(false);
    }
}

// Hello and P1; the server name is shown for the user to confirm before anything is saved
async function pair() {
    endSession();
    const port = readPort();
    if (port === null) return setStatus(PORT_TEXT);
    setStatus('');
    session = await startPairing(port, byId('code').value);
    byId('confirm-text').textContent = 'Pair with ' + session.serverName + ' at 127.0.0.1:' + port + '?';
    byId('confirm').hidden = false;
}

async function confirmPairing() {
    const pending = session;
    endSession();
    byId('code').value = '';
    await finishPairing(pending);
    await showPairing();
}

async function cancelPairing() {
    endSession();
    await showPairing();
}

async function forget() {
    endSession();
    await forgetPairing();
    await showPairing();
}

// A new port is saved once the program there proves it holds this browser's token. A refused port gives the saved
// port back with the reason, so the field never shows a port that is not in use.
export async function portChange(text) {
    const port = parsePort(text);
    if (port === null) return { port: await savedPort(), text: PORT_TEXT };
    try {
        await changePort(port);
        return { port, text: null };
    } catch (error) {
        if (!(error instanceof ApiFailure)) throw error;
        return { port: await savedPort(), text: error.message };
    }
}

async function portChanged() {
    const result = await portChange(byId('port').value);
    byId('port').value = String(result.port);
    if (result.text !== null) return setStatus(result.text);
    await showPairing();
}

async function init() {
    byId('pair').addEventListener('click', () => run(pair));
    byId('confirm-yes').addEventListener('click', () => run(confirmPairing));
    byId('confirm-no').addEventListener('click', () => run(cancelPairing));
    byId('forget').addEventListener('click', () => run(forget));
    byId('port').addEventListener('change', () => run(portChanged));
    byId('port').value = String(await savedPort());
    await showPairing();
}

// The tests import this module without a page
if (globalThis.document) init();
