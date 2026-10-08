import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { after, before, test } from 'node:test';
import { pathToFileURL } from 'node:url';
import { mergeManifest } from '../scripts/package.mjs';
import { hostPatterns, repoRoot, root } from './fakes.mjs';

const CSP = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src http://127.0.0.1:*; base-uri 'none'; form-action 'none'";
let out;
const manifests = {};

// The generated output in a temporary folder, never in the repository
before(() => {
    out = fs.mkdtempSync(path.join(os.tmpdir(), 'ctw-extension-'));
    const result = spawnSync(process.execPath, [path.join(root, 'scripts', 'package.mjs'), '--out', out], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    for (const browser of ['chrome', 'firefox']) manifests[browser] = JSON.parse(fs.readFileSync(path.join(out, browser, 'manifest.json'), 'utf8'));
});

after(() => {
    fs.rmSync(out, { recursive: true, force: true });
});

function appVersion() {
    const text = fs.readFileSync(path.join(repoRoot, 'src', 'ChanThreadWatch', 'Properties', 'AssemblyInfo.cs'), 'utf8');
    const [, major, minor, , revision] = /\[assembly: AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.(\d+)"\)\]/.exec(text);
    return major + '.' + minor + '.' + revision;
}

// The pinned id the API accepts as the Chrome Origin
function pinnedChromeId() {
    const text = fs.readFileSync(path.join(repoRoot, 'src', 'ChanThreadWatch.Api', 'ApiPairing.cs'), 'utf8');
    return /ChromeExtensionId = "([a-p]{32})"/.exec(text)[1];
}

// Chrome's id: the first 32 hex digits of SHA-256 of the public key (DER), with 0-f written as a-p
function chromeIdOf(key) {
    const hex = crypto.createHash('sha256').update(Buffer.from(key, 'base64')).digest('hex').slice(0, 32);
    return [...hex].map((digit) => String.fromCharCode(97 + parseInt(digit, 16))).join('');
}

for (const browser of ['chrome', 'firefox']) {
    test(browser + ': the exact permissions, host permissions and CSP', () => {
        const manifest = manifests[browser];
        assert.equal(manifest.manifest_version, 3);
        assert.deepEqual(manifest.permissions, ['contextMenus', 'storage', 'activeTab']);
        assert.deepEqual(manifest.host_permissions, ['http://127.0.0.1/*']);
        assert.deepEqual(manifest.optional_host_permissions, hostPatterns);
        assert.equal(manifest.content_security_policy.extension_pages, CSP);
        assert.deepEqual(Object.keys(manifest.content_security_policy), ['extension_pages']);
        assert.deepEqual(manifest.options_ui, { page: 'options.html', open_in_tab: true });
        assert.equal(manifest.action.default_popup, 'popup.html');
    });

    test(browser + ': no content scripts, web-accessible resources or other keys that widen access', () => {
        const manifest = manifests[browser];
        for (const key of ['content_scripts', 'web_accessible_resources', 'optional_permissions']) assert.equal(key in manifest, false, key);
    });

    test(browser + ': the version is the app\'s Major.Minor.Revision', () => {
        assert.equal(manifests[browser].version, appVersion());
        assert.match(manifests[browser].version, /^\d+\.\d+\.\d+$/);
    });

    test(browser + ': every file the manifest names was copied', () => {
        const manifest = manifests[browser];
        const files = [manifest.action.default_popup, manifest.options_ui.page, ...Object.values(manifest.icons), ...Object.values(manifest.action.default_icon)];
        for (const file of files) assert.ok(fs.existsSync(path.join(out, browser, file)), file);
        assert.ok(fs.existsSync(path.join(out, browser, 'background.js')));
    });
}

test('chrome: a module service worker, the key of the pinned id, and no external connections', () => {
    const manifest = manifests.chrome;
    assert.deepEqual(manifest.background, { service_worker: 'background.js', type: 'module' });
    assert.equal(typeof manifest.key, 'string');
    assert.equal(chromeIdOf(manifest.key), pinnedChromeId());
    assert.equal(pinnedChromeId(), 'eifjifphdncjkkolefjepjhdlmcdbndh');
    assert.deepEqual(manifest.externally_connectable, { ids: [] });
    assert.equal('browser_specific_settings' in manifest, false);
});

test('firefox: a module background script and the gecko id', () => {
    const manifest = manifests.firefox;
    assert.deepEqual(manifest.background, { scripts: ['background.js'], type: 'module' });
    assert.deepEqual(manifest.browser_specific_settings, { gecko: { id: 'chanthreadwatch@kevin-contino.github.io', strict_min_version: '140.0' } });
    assert.equal('key' in manifest, false);
    assert.equal('externally_connectable' in manifest, false);
});

// Every file under src/, in subfolders too, relative to src/
function sourceFiles() {
    return fs.readdirSync(path.join(root, 'src'), { recursive: true }).filter((file) => fs.statSync(path.join(root, 'src', file)).isFile());
}

for (const browser of ['chrome', 'firefox']) {
    test(browser + ': the copied files are the source files, unchanged, and nothing else but the manifest', () => {
        const files = sourceFiles();
        assert.ok(files.includes(path.join('icons', 'icon-128.png')));
        for (const file of files) assert.deepEqual(fs.readFileSync(path.join(out, browser, file)), fs.readFileSync(path.join(root, 'src', file)), file);
        const copied = fs.readdirSync(path.join(out, browser), { recursive: true }).filter((file) => fs.statSync(path.join(out, browser, file)).isFile());
        assert.deepEqual(copied.sort(), [...files, 'manifest.json'].sort());
    });
}

function runPackage(args) {
    return spawnSync(process.execPath, [path.join(root, 'scripts', 'package.mjs'), ...args], { encoding: 'utf8' });
}

test('--out without a folder fails', () => {
    assert.notEqual(runPackage(['--out']).status, 0);
    assert.notEqual(runPackage(['--out', '--dry-run']).status, 0);
});

test('a build removes and writes only <out>/chrome and <out>/firefox', () => {
    const target = fs.mkdtempSync(path.join(os.tmpdir(), 'ctw-extension-'));
    try {
        fs.writeFileSync(path.join(target, 'keep.txt'), 'kept');
        fs.mkdirSync(path.join(target, 'chrome'));
        fs.writeFileSync(path.join(target, 'chrome', 'stale.txt'), 'stale');
        assert.equal(runPackage(['--out', target]).status, 0);
        assert.deepEqual(fs.readdirSync(target).sort(), ['chrome', 'firefox', 'keep.txt']);
        assert.equal(fs.readFileSync(path.join(target, 'keep.txt'), 'utf8'), 'kept');
        assert.equal(fs.existsSync(path.join(target, 'chrome', 'stale.txt')), false);
    } finally {
        fs.rmSync(target, { recursive: true, force: true });
    }
});

test('--dry-run prints what it would remove and write, and writes nothing', () => {
    const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'ctw-extension-'));
    const target = path.join(parent, 'out');
    try {
        const result = runPackage(['--out', target, '--dry-run']);
        assert.equal(result.status, 0, result.stderr);
        for (const browser of ['chrome', 'firefox']) {
            assert.ok(result.stdout.includes('Would remove ' + path.join(target, browser) + '\n'), browser);
            assert.ok(result.stdout.includes('Would write ' + path.join(target, browser) + ' (version ' + appVersion() + ')\n'), browser);
        }
        assert.equal(fs.existsSync(target), false);
    } finally {
        fs.rmSync(parent, { recursive: true, force: true });
    }
});

// The output folder is named on the command line, so an import with those arguments would write there if it built
test('importing the package script builds nothing', () => {
    const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'ctw-extension-'));
    const target = path.join(parent, 'out');
    try {
        const code = 'await import(' + JSON.stringify(pathToFileURL(path.join(root, 'scripts', 'package.mjs')).href) + ');';
        const result = spawnSync(process.execPath, ['--input-type=module', '-e', code, '--', '--out', target], { encoding: 'utf8' });
        assert.equal(result.status, 0, result.stderr);
        assert.equal(result.stdout, '');
        assert.equal(fs.existsSync(target), false);
    } finally {
        fs.rmSync(parent, { recursive: true, force: true });
    }
});

// A junction on Windows, a symbolic link elsewhere; Node runs the script from its real path
test('the package script runs through a linked folder', (t) => {
    const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'ctw-extension-'));
    const link = path.join(parent, 'linked-scripts');
    try {
        fs.symlinkSync(path.join(root, 'scripts'), link, 'junction');
    } catch (error) {
        fs.rmSync(parent, { recursive: true, force: true });
        t.skip('no link: ' + error.code);
        return;
    }
    try {
        const result = spawnSync(process.execPath, [path.join(link, 'package.mjs'), '--out', path.join(parent, 'out'), '--dry-run'], { encoding: 'utf8' });
        assert.equal(result.status, 0, result.stderr);
        assert.match(result.stdout, /Would write .*chrome \(version /);
        assert.equal(fs.existsSync(path.join(parent, 'out')), false);
    } finally {
        // The link goes first, so removing the folder can never reach the real scripts folder
        fs.unlinkSync(link);
        fs.rmdirSync(parent);
    }
});

test('an overlay may not set a key of the base, the version or the optional host permissions', () => {
    const base = { name: 'x', permissions: [] };
    for (const key of ['name', 'version', 'optional_host_permissions']) {
        assert.throws(() => mergeManifest(base, { [key]: 'y' }, '1.2.3', ['4chan.org'], 'manifest.test.json'), new RegExp('manifest.test.json sets keys .*' + key), key);
    }
    assert.deepEqual(mergeManifest(base, { key: 'k' }, '1.2.3', ['4chan.org'], 'manifest.test.json'), { name: 'x', permissions: [], version: '1.2.3', optional_host_permissions: ['*://*.4chan.org/*'], key: 'k' });
});
