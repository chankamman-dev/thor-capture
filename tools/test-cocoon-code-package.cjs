'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { spawnSync } = require('node:child_process');
const { pack, verify, readSpec } = require('./cocoon-code-package.cjs');
function git(root, ...args) {
  const result = spawnSync('git', ['-c', 'safe.directory=' + root.replace(/\\/g, '/'), '-C', root, ...args], { encoding: 'utf8', windowsHide: true });
  assert.equal(result.status, 0, 'isolated fixture git succeeds'); return result.stdout.trim();
}
function fixture(t) {
  const owned = fs.mkdtempSync(path.join(os.tmpdir(), 'thor-source-package-')), source = path.join(owned, 'source'), candidate = path.join(owned, 'candidate'); fs.mkdirSync(source);
  t.after(() => { assert.ok(path.resolve(owned).startsWith(path.resolve(os.tmpdir()) + path.sep)); fs.rmSync(owned, { recursive: true, force: true }); });
  for (const file of readSpec().files) {
    const full = path.join(source, file); fs.mkdirSync(path.dirname(full), { recursive: true });
    fs.writeFileSync(full, file.endsWith('.json') ? '{}\n' : file.endsWith('.html') ? '<!doctype html><html></html>\n' : "'use strict';\n");
  }
  fs.writeFileSync(path.join(source, '.gitignore'), 'config.json\n_data/\n_cache/\nnode.exe\n');
  git(source, 'init', '--quiet'); git(source, 'config', 'user.name', 'Offline Fixture'); git(source, 'config', 'user.email', 'offline@example.invalid');
  function commit() { git(source, 'add', '.'); git(source, 'commit', '--quiet', '-m', 'offline fixture'); return git(source, 'rev-parse', 'HEAD'); }
  return { owned, source, candidate, commit, hash: commit() };
}
const rejected = (action, code) => assert.throws(action, error => error.code === code);
test('pure-code package is portable, relative, frozen, complete and never includes ignored data or runtime', t => {
  const f = fixture(t); fs.writeFileSync(path.join(f.source, 'config.json'), '{"password":"private-test"}'); fs.writeFileSync(path.join(f.source, 'node.exe'), 'runtime-test');
  fs.mkdirSync(path.join(f.source, '_data')); fs.writeFileSync(path.join(f.source, '_data', 'cocoon_db'), 'library-test');
  const result = pack(f.source, f.candidate, f.hash); assert.equal(result.verified, true); assert.equal(result.publicReleaseReady, false);
  const manifest = JSON.parse(fs.readFileSync(path.join(f.candidate, 'cocoon-code-manifest.json')));
  assert.equal(manifest.sourceCommit, f.hash); assert.equal(manifest.files.length, readSpec().files.length);
  assert.ok(manifest.files.every(x => x.path.startsWith('cocoon/') && !path.isAbsolute(x.path)));
  assert.ok(!fs.existsSync(path.join(f.candidate, 'cocoon', 'config.json')) && !fs.existsSync(path.join(f.candidate, 'cocoon', 'node.exe')) && !fs.existsSync(path.join(f.candidate, 'cocoon', '_data')));
  assert.equal(git(f.source, 'status', '--porcelain'), ''); assert.equal(verify(f.candidate, { expectedCommit: f.hash, expectedManifestSha256: result.manifestSha256 }).verified, true);
});
test('package refuses an existing empty destination without touching it', t => { const f = fixture(t); fs.mkdirSync(f.candidate); rejected(() => pack(f.source, f.candidate, f.hash), 'CANDIDATE_ALREADY_EXISTS'); assert.deepEqual(fs.readdirSync(f.candidate), []); });
test('package refuses dirty source and missing expected code revision', t => { const f = fixture(t); rejected(() => pack(f.source, f.candidate), 'EXPECTED_COMMIT_REQUIRED'); fs.appendFileSync(path.join(f.source, 'server.js'), '// dirty'); rejected(() => pack(f.source, f.candidate, f.hash), 'SOURCE_NOT_FROZEN'); assert.equal(fs.existsSync(f.candidate), false); });
test('package refuses a different frozen revision', t => { const f = fixture(t); rejected(() => pack(f.source, f.candidate, '0'.repeat(40)), 'SOURCE_COMMIT_MISMATCH'); });
test('package refuses destination inside original repository', t => { const f = fixture(t); rejected(() => pack(f.source, path.join(f.source, 'new-candidate'), f.hash), 'CANDIDATE_INSIDE_SOURCE'); });
test('package refuses unknown required relative dependency', t => { const f = fixture(t); fs.writeFileSync(path.join(f.source, 'server.js'), "require('./unknown-required');\n"); fs.writeFileSync(path.join(f.source, 'unknown-required.js'), '// not reviewed'); f.hash = f.commit(); rejected(() => pack(f.source, f.candidate, f.hash), 'REQUIRED_FILE_NOT_ALLOWLISTED'); });
test('package refuses external and computed require dependencies', t => { const f = fixture(t); fs.writeFileSync(path.join(f.source, 'server.js'), "require('unreviewed-package');\n"); f.hash = f.commit(); rejected(() => pack(f.source, f.candidate, f.hash), 'EXTERNAL_DEPENDENCY_NOT_ALLOWED'); fs.writeFileSync(path.join(f.source, 'server.js'), 'require(process.env.SOMETHING);\n'); f.hash = f.commit(); rejected(() => pack(f.source, f.candidate, f.hash), 'DYNAMIC_DEPENDENCY_REVIEW_REQUIRED'); });
test('package refuses personal paths and literal secrets before creating destination', t => { const f = fixture(t); fs.writeFileSync(path.join(f.source, 'server.js'), "const location = 'G:\\\\private\\\\library';\n"); f.hash = f.commit(); rejected(() => pack(f.source, f.candidate, f.hash), 'PERSONAL_ABSOLUTE_PATH'); fs.writeFileSync(path.join(f.source, 'server.js'), "const password = 'sensitive-fixture';\n"); f.hash = f.commit(); rejected(() => pack(f.source, f.candidate, f.hash), 'CREDENTIAL_LITERAL'); assert.equal(fs.existsSync(f.candidate), false); });
test('package does not interpret ordinary HTTPS URLs as drive paths', t => { const f = fixture(t); fs.writeFileSync(path.join(f.source, 'server.js'), "const url = 'https://example.invalid';\n"); f.hash = f.commit(); assert.equal(pack(f.source, f.candidate, f.hash).verified, true); });
test('only the exact reviewed key placeholder in the tracked entry page is exempt', t => {
  const f = fixture(t), placeholder = 'C:\\\\...\\\\prod.keys'; fs.writeFileSync(path.join(f.source, 'ui/index.html'), '<input placeholder="' + placeholder + '">'); f.hash = f.commit(); assert.equal(pack(f.source, f.candidate, f.hash).verified, true);
  fs.writeFileSync(path.join(f.source, 'server.js'), "const example = '" + placeholder + "';"); f.hash = f.commit(); rejected(() => pack(f.source, path.join(f.owned, 'different-candidate'), f.hash), 'PERSONAL_ABSOLUTE_PATH');
});
test('dependency review rejects runtime ES imports but accepts existing method and JSDoc import names', t => {
  const f = fixture(t); fs.writeFileSync(path.join(f.source, 'server.js'), "const loaded = import('node:fs');\n"); f.hash = f.commit(); rejected(() => pack(f.source, f.candidate, f.hash), 'DYNAMIC_DEPENDENCY_REVIEW_REQUIRED');
  fs.writeFileSync(path.join(f.source, 'server.js'), "/** @type {import('node:sqlite').DatabaseSync} */\nowned.assets.import(input);\n"); fs.writeFileSync(path.join(f.source, 'lib/application/nativeAssetService.js'), 'class Example { import(input, bytes, signal) {} }'); f.hash = f.commit(); assert.equal(pack(f.source, f.candidate, f.hash).verified, true);
});
test('source hardlinks are rejected even when content and git status match', t => { const f = fixture(t); fs.linkSync(path.join(f.source, 'server.js'), path.join(f.owned, 'source-alias')); rejected(() => pack(f.source, f.candidate, f.hash), 'LINK_REJECTED'); });
test('candidate hardlink is rejected even when content hashes still match', t => { const f = fixture(t); pack(f.source, f.candidate, f.hash); fs.linkSync(path.join(f.candidate, 'cocoon', 'server.js'), path.join(f.owned, 'candidate-alias')); rejected(() => verify(f.candidate), 'LINK_REJECTED'); });
test('source ancestor junction or symlink is rejected', t => { const f = fixture(t); const alias = path.join(f.owned, 'linked-source'); fs.symlinkSync(f.source, alias, process.platform === 'win32' ? 'junction' : 'dir'); rejected(() => pack(alias, f.candidate, f.hash), 'LINK_REJECTED'); });
test('candidate ancestor junction or symlink is rejected', t => { const f = fixture(t); pack(f.source, f.candidate, f.hash); const alias = path.join(f.owned, 'linked-candidate'); fs.symlinkSync(f.candidate, alias, process.platform === 'win32' ? 'junction' : 'dir'); rejected(() => verify(alias), 'LINK_REJECTED'); });
test('verify refuses changed missing extra files and extra empty data directory', t => {
  const f = fixture(t); pack(f.source, f.candidate, f.hash); const server = path.join(f.candidate, 'cocoon', 'server.js'), bytes = fs.readFileSync(server);
  fs.appendFileSync(server, '// tampered'); rejected(() => verify(f.candidate), 'PACKAGE_HASH_MISMATCH'); fs.writeFileSync(server, bytes);
  fs.unlinkSync(server); rejected(() => verify(f.candidate), 'SOURCE_FILE_MISSING'); fs.writeFileSync(server, bytes);
  const extra = path.join(f.candidate, 'cocoon', 'config.json'); fs.writeFileSync(extra, '{}'); rejected(() => verify(f.candidate), 'PACKAGE_UNEXPECTED_FILE'); fs.unlinkSync(extra);
  fs.mkdirSync(path.join(f.candidate, 'cocoon', '_data')); rejected(() => verify(f.candidate), 'PACKAGE_UNEXPECTED_DIRECTORY');
});
test('verify binds external manifest digest and expected revision; malformed manifest fails closed', t => {
  const f = fixture(t), result = pack(f.source, f.candidate, f.hash);
  rejected(() => verify(f.candidate, { expectedCommit: '0'.repeat(40) }), 'PACKAGE_COMMIT_MISMATCH'); rejected(() => verify(f.candidate, { expectedManifestSha256: '0'.repeat(64) }), 'PACKAGE_MANIFEST_MISMATCH');
  const file = path.join(f.candidate, 'cocoon-code-manifest.json'), bytes = fs.readFileSync(file); fs.writeFileSync(file, '{broken'); rejected(() => verify(f.candidate), 'MANIFEST_INVALID'); fs.writeFileSync(file, bytes);
  const manifest = JSON.parse(bytes); manifest.files[0].sha256 = '0'.repeat(64); fs.writeFileSync(file, JSON.stringify(manifest)); rejected(() => verify(f.candidate), 'MANIFEST_CHECKSUM_MISMATCH'); assert.ok(result.manifestSha256);
});
