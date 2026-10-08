import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { root } from './fakes.mjs';

function filesIn(folder, extensions) {
    return fs.readdirSync(path.join(root, folder), { recursive: true })
        .filter((file) => extensions.includes(path.extname(file)))
        .map((file) => path.join(root, folder, file));
}

const sources = filesIn('src', ['.js', '.html']);

// No remote code, no code from strings, no HTML from strings, no logging, no synced storage, no external messaging
const BANNED = [
    [/\beval\s*\(/, 'eval'],
    [/\bnew\s+Function\b/, 'new Function'],
    [/\bFunction\s*\(/, 'Function()'],
    [/\b(setTimeout|setInterval)\s*\(\s*['"`]/, 'a string timer'],
    [/\binnerHTML\b/, 'innerHTML'],
    [/\bouterHTML\b/, 'outerHTML'],
    [/\binsertAdjacentHTML\b/, 'insertAdjacentHTML'],
    [/\bdocument\.write/, 'document.write'],
    [/\bconsole\./, 'console'],
    [/\bstorage\.sync\b/, 'storage.sync'],
    [/\bsync\s*:/, 'a sync member'],
    [/\bonMessageExternal\b|\bonConnectExternal\b/, 'an external listener'],
    [/\bimport\s*\(/, 'a dynamic import'],
    [/\bimportScripts\b/, 'importScripts'],
    [/\bXMLHttpRequest\b|\bWebSocket\b|\bEventSource\b|\bsendBeacon\b/, 'another network API'],
    [/\bon[a-z]+\s*=\s*["']/i, 'an inline event handler'],
    [/\bstyle\s*=/i, 'an inline style'],
    [/javascript:/i, 'a javascript: address']
];

test('the source has no banned construct', () => {
    assert.ok(sources.length >= 9);
    for (const file of sources) {
        const text = fs.readFileSync(file, 'utf8');
        for (const [pattern, name] of BANNED) assert.doesNotMatch(text, pattern, path.basename(file) + ' uses ' + name);
    }
});

test('the only address in the source is http://127.0.0.1', () => {
    for (const file of sources) {
        const addresses = fs.readFileSync(file, 'utf8').match(/[a-z][a-z0-9+.-]*:\/\/[^\s'"`)]*/gi) || [];
        for (const address of addresses) {
            const allowed = address.startsWith('http://127.0.0.1') || address.startsWith('*://*.');
            assert.ok(allowed, path.basename(file) + ' names ' + address);
        }
    }
});

test('the pages load only their own module script', () => {
    for (const file of filesIn('src', ['.html'])) {
        const text = fs.readFileSync(file, 'utf8');
        const scripts = text.match(/<script\b[^>]*>/gi);
        assert.deepEqual(scripts, ['<script type="module" src="' + path.basename(file, '.html') + '.js">'], path.basename(file));
        assert.doesNotMatch(text, /<script[^>]*>[^<]/i, path.basename(file) + ' has an inline script');
        assert.doesNotMatch(text, /<(iframe|object|embed|link|base|form)\b/i, path.basename(file));
    }
});

test('every script parses (node --check)', () => {
    const scripts = [...filesIn('src', ['.js']), ...filesIn('scripts', ['.mjs']), ...filesIn('test', ['.mjs'])];
    assert.ok(scripts.length >= 13);
    for (const file of scripts) {
        const result = spawnSync(process.execPath, ['--check', file], { encoding: 'utf8' });
        assert.equal(result.status, 0, path.basename(file) + ': ' + result.stderr);
    }
});

test('the package has no dependencies and its lock file has no packages', () => {
    const pkg = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
    for (const key of ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies', 'bundleDependencies']) assert.equal(key in pkg, false, key);
    const lock = JSON.parse(fs.readFileSync(path.join(root, 'package-lock.json'), 'utf8'));
    assert.equal(lock.lockfileVersion, 3);
    assert.deepEqual(Object.keys(lock.packages), ['']);
});
