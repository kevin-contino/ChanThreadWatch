// The texts the extension shows. An API problem maps by its code, then by its status.

export const NOT_PAIRED_TEXT = 'This browser is not paired (any more). Pair it again in the options.';
export const EXTENSION_ERROR_TEXT = 'The request failed. See the extension\'s errors.';
export const NO_THREAD_TABS_TEXT = 'No thread tabs are open.';
export const CODE_MISMATCH_TEXT = 'The code does not match. Check the code and the port.';
export const CODE_FORMAT_TEXT = 'The code has 8 letters and digits, as XXXX-XXXX.';
export const PORT_TEXT = 'The port must be a number from 1024 to 65535.';
export const NOT_A_THREAD_TEXT = 'Not a thread address.';
export const ALREADY_WATCHED_TEXT = 'Already watched.';
export const ADDED_TEXT = 'Added.';
export const ADD_TIMEOUT_TEXT = 'The request timed out, so it is not known whether the thread was added.';

const PROBLEM_TEXTS = new Map([
    ['not_paired', NOT_PAIRED_TEXT],
    ['forbidden_origin', 'This browser\'s pairing does not match. Pair it again.'],
    ['pairing_unavailable', 'No pairing code is active. Make a new code in Chan Thread Watch.'],
    ['pairing_failed', 'Pairing failed and the code is no longer valid. Make a new code.'],
    ['already_watched', ALREADY_WATCHED_TEXT],
    ['blacklisted', 'The thread is on the blacklist.'],
    ['thread_limit', 'The thread list is full.'],
    ['unknown_host', 'This site is not supported.'],
    ['invalid_url', NOT_A_THREAD_TEXT],
    ['blocked_host', 'The address is local or private.'],
    ['unresolvable_host', 'The site name could not be found.'],
    ['unavailable', 'The program is busy or closing. Try again.'],
    ['marks_unavailable', 'The program cannot save new threads now (see its log).']
]);

const STATUS_TEXTS = new Map([
    [401, NOT_PAIRED_TEXT],
    [507, 'The download drive is almost full.']
]);

export function unreachableText(port) {
    return 'Chan Thread Watch is not reachable at 127.0.0.1:' + port + '. Start the app or ctw watch with the local API on, or check the port in the options.';
}

export function notTheServerText(port) {
    return 'The program at 127.0.0.1:' + port + ' is not the Chan Thread Watch this browser is paired with. Nothing was sent.';
}

export function problemText(status, code, retryAfterSeconds) {
    if (PROBLEM_TEXTS.has(code)) return PROBLEM_TEXTS.get(code);
    if (status === 429) return 'Too many requests. Try again in ' + retryAfterSeconds + ' seconds.';
    return STATUS_TEXTS.get(status) || 'The request failed (code ' + status + '). See log.txt.';
}
