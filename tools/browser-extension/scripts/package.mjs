// Builds the extension for each browser: <out>/chrome and <out>/firefox, each a copy of src/ with its manifest.json
// (manifest.base.json plus the browser's overlay, the optional host permissions from known-hosts.json, and the version
// Major.Minor.Revision from src/ChanThreadWatch/Properties/AssemblyInfo.cs, the one version source of every app, as
// .github/scripts/publish-cli.ps1 reads it). No bundler: the shipped files are the reviewed files. Only <out>/chrome
// and <out>/firefox are removed and written again; nothing else in <out> is touched.
//
// Usage: node scripts/package.mjs [--out <dir>] [--dry-run]   (default: dist next to this script's folder)
// --dry-run prints what would be removed and written, and writes nothing.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { hostPattern } from '../src/urls.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const assemblyInfo = path.resolve(root, '..', '..', 'src', 'ChanThreadWatch', 'Properties', 'AssemblyInfo.cs');
const BROWSERS = ['chrome', 'firefox'];
const VERSION = /^\[assembly: AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.(\d+)"\)\]/m;
const DOMAIN = /^[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)+$/;
// Set by this script; an overlay that set them would replace the generated values
const GENERATED_KEYS = ['version', 'optional_host_permissions'];

function readJson(file) {
    return JSON.parse(fs.readFileSync(path.join(root, file), 'utf8'));
}

function readVersion() {
    const match = VERSION.exec(fs.readFileSync(assemblyInfo, 'utf8'));
    if (!match) throw new Error('AssemblyVersion not found in ' + assemblyInfo);
    return match[1] + '.' + match[2] + '.' + match[4];
}

function readDomains() {
    const domains = readJson('known-hosts.json');
    const valid = Array.isArray(domains) && domains.length > 0 && domains.every((domain) => DOMAIN.test(domain));
    if (!valid || new Set(domains).size !== domains.length) throw new Error('known-hosts.json must list unique lowercase domains');
    return domains;
}

// The base and the overlay never set the same key, and the overlay never sets a generated key, so nothing silently
// replaces anything else
export function mergeManifest(base, overlay, version, domains, overlayName) {
    const shared = Object.keys(overlay).filter((key) => key in base || GENERATED_KEYS.includes(key));
    if (shared.length > 0) throw new Error(overlayName + ' sets keys of the base or generated keys: ' + shared.join(', '));
    return { ...base, version, optional_host_permissions: domains.map(hostPattern), ...overlay };
}

function manifestFor(browser, version, domains) {
    const overlayName = 'manifest.' + browser + '.json';
    return mergeManifest(readJson('manifest.base.json'), readJson(overlayName), version, domains, overlayName);
}

function outDir(args) {
    const index = args.indexOf('--out');
    if (index < 0) return path.join(root, 'dist');
    if (!args[index + 1] || args[index + 1].startsWith('--')) throw new Error('--out needs a folder');
    return path.resolve(args[index + 1]);
}

function build(out, dryRun) {
    const version = readVersion();
    const domains = readDomains();
    for (const browser of BROWSERS) {
        const target = path.join(out, browser);
        const manifest = JSON.stringify(manifestFor(browser, version, domains), null, 2) + '\n';
        if (dryRun) {
            process.stdout.write('Would remove ' + target + '\nWould write ' + target + ' (version ' + version + ')\n');
            continue;
        }
        fs.rmSync(target, { recursive: true, force: true });
        fs.cpSync(path.join(root, 'src'), target, { recursive: true });
        fs.writeFileSync(path.join(target, 'manifest.json'), manifest);
        process.stdout.write('Built ' + target + ' (version ' + version + ')\n');
    }
}

// Run as a script only; the tests import mergeManifest without a build. Both sides are real paths, so a run through a
// symbolic link or junction still builds.
function isMain() {
    try {
        return fs.realpathSync.native(process.argv[1]) === fs.realpathSync.native(fileURLToPath(import.meta.url));
    } catch {
        return false;
    }
}

if (isMain()) {
    const args = process.argv.slice(2);
    build(outDir(args), args.includes('--dry-run'));
}
