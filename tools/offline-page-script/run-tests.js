'use strict';

// Runs offline-page-script.test.js once per site fixture, each in its own Node process. A
// fixture's pages are large in jsdom; run together in one process, all fixtures exceed the heap
// of a CI runner.

const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const MANIFEST_PATH = path.join(__dirname, '..', '..', 'ChanThreadWatch.Tests', 'Fixtures', 'sites', 'manifest.json');
const names = Object.keys(JSON.parse(fs.readFileSync(MANIFEST_PATH, 'utf8')));
if (names.length === 0) throw new Error('No site fixtures in ' + MANIFEST_PATH);

const failed = names.filter((name) => {
    const result = spawnSync(process.execPath, ['--test', path.join(__dirname, 'offline-page-script.test.js')], {
        env: Object.assign({}, process.env, { FIXTURE: name }),
        stdio: 'inherit'
    });
    return result.status !== 0;
});

console.log(names.length - failed.length + ' of ' + names.length + ' fixtures passed' + (failed.length ? '; failed: ' + failed.join(', ') : ''));
process.exitCode = failed.length ? 1 : 0;
