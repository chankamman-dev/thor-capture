'use strict';

// Offline source-only candidate. No downloader, publisher or dependency installer.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawn, spawnSync } = require('node:child_process');
const { builtinModules } = require('node:module');
const SPEC = path.resolve(__dirname, '../docs/release/cocoon-code-allowlist.json');
const CONTRACT = 'thor.cocoon-code-package/1';
const NAME = 'cocoon-code-manifest.json';
const fail = code => { throw Object.assign(new Error(code), { code }); };
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const exactKeys = (value, keys) => value && !Array.isArray(value) && Object.keys(value).sort().join('|') === [...keys].sort().join('|');
const samePath = (a, b) => process.platform === 'win32' ? a.toLowerCase() === b.toLowerCase() : a === b;
function safeRelative(value) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_.\/-]+$/.test(value) || value.includes('//') || value.split('/').some(x => !x || x === '.' || x === '..') || path.isAbsolute(value)) fail('RELATIVE_PATH_INVALID');
  return value;
}
function noLinks(full, fileRequired = false) {
  full = path.resolve(full);
  let current = full;
  while (true) {
    let info;
    try { info = fs.lstatSync(current); } catch (error) { if (error.code !== 'ENOENT' && error.code !== 'ENOTDIR') fail('PATH_INSPECTION_FAILED'); }
    if (info) {
      if (info.isSymbolicLink() || (info.isFile() && info.nlink !== 1)) fail('LINK_REJECTED');
      if (!info.isFile() && !info.isDirectory()) fail('FILE_KIND_REJECTED');
    } else if (current === full && fileRequired) fail('SOURCE_FILE_MISSING');
    const parent = path.dirname(current); if (parent === current) break; current = parent;
  }
  if (fileRequired && !fs.lstatSync(full).isFile()) fail('SOURCE_FILE_MISSING');
}
function readSpec() {
  noLinks(SPEC, true); const spec = JSON.parse(fs.readFileSync(SPEC, 'utf8'));
  if (!exactKeys(spec, ['contract', 'profile', 'files']) || spec.contract !== 'thor.cocoon-code-allowlist/1' || !Array.isArray(spec.files) || spec.files.length < 3 || spec.files.length > 256) fail('ALLOWLIST_INVALID');
  const files = spec.files.map(safeRelative);
  if (new Set(files).size !== files.length || files.join('|') !== [...files].sort().join('|')) fail('ALLOWLIST_INVALID');
  for (const file of files) if (!/^(backend\.cjs|server\.js|package\.json|lib\/[A-Za-z0-9_./-]+\.js|ui\/index\.html|contracts\/fixtures\/cocoon-beta-3\.04-candidate-profile-v1\.json)$/.test(file)) fail('ALLOWLIST_FORBIDDEN_FILE');
  return spec;
}
function git(root, args) {
  const result = spawnSync('git', ['-c', 'safe.directory=' + root.replace(/\\/g, '/'), '-C', root, ...args], { env: { ...process.env, GIT_OPTIONAL_LOCKS: '0' }, encoding: 'utf8', windowsHide: true, timeout: 15000, maxBuffer: 4 * 1024 * 1024 });
  if (result.status !== 0 || result.error) fail('GIT_VALIDATION_FAILED');
  return result.stdout.trim();
}
function privateCode(bytes, relative) {
  let text = bytes.toString('utf8');
  // Reviewed non-personal placeholder at ui/index.html:2246; never exempts real paths.
  if (relative === 'ui/index.html') text = text.replaceAll('C:\\\\...\\\\prod.keys', 'NON_PERSONAL_KEY_PATH_PLACEHOLDER');
  // Absolute workstation paths, secrets and key files cannot pass this package gate.
  if (/(?:\b[A-Za-z]:[\\/]|\/Users\/[^/\s]+\/|\/home\/[^/\s]+\/)/.test(text)) fail('PERSONAL_ABSOLUTE_PATH');
  if (/gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16}|-----BEGIN (?:RSA |OPENSSH |EC )?PRIVATE KEY-----/.test(text)) fail('SECRET_CONTENT');
  if (/(?:password|access_token|api_key|pairingCode)\s*[:=]\s*['"][^'"\r\n]{4,}['"]/i.test(text)) fail('CREDENTIAL_LITERAL');
  if (bytes.includes(0)) fail('BINARY_CONTENT');
}
function checkDependencies(blobs, files) {
  const allowed = new Set(files), builtins = new Set(builtinModules.map(x => x.replace(/^node:/, '')));
  for (const [file, bytes] of blobs) {
    if (!/\.(?:js|cjs)$/.test(file)) continue;
    const text = bytes.toString('utf8');
    let dependencyText = text.replace(/\/\*[\s\S]*?\*\//g, '');
    // Existing native asset class has a method named import; it is not an ES import.
    if (file === 'lib/application/nativeAssetService.js') dependencyText = dependencyText.replace('import(input, bytes, signal) {', 'assetImportMethod(input, bytes, signal) {');
    if (/\brequire\s*\(\s*(?!['"\s])/.test(dependencyText) || /(?<![\w.])import\s*\(/.test(dependencyText)) fail('DYNAMIC_DEPENDENCY_REVIEW_REQUIRED');
    for (const match of text.matchAll(/\brequire\s*\(\s*(['"])([^'"\r\n]+)\1\s*\)/g)) {
      const request = match[2];
      if (!request.startsWith('.')) { if (!builtins.has(request.replace(/^node:/, ''))) fail('EXTERNAL_DEPENDENCY_NOT_ALLOWED'); continue; }
      const base = path.posix.normalize(path.posix.join(path.posix.dirname(file), request));
      const resolved = [base, base + '.js', base + '.json', base + '/index.js'].find(p => allowed.has(p));
      if (!resolved) fail('REQUIRED_FILE_NOT_ALLOWLISTED');
    }
    for (const match of dependencyText.matchAll(/new Worker\(path\.join\(__dirname,\s*(['"])([^'"]+)\1\)/g)) {
      if (!allowed.has(path.posix.join(path.posix.dirname(file), match[2]))) fail('REQUIRED_WORKER_MISSING');
    }
  }
  // These workers are loaded via path.join, not literal require.
  for (const worker of ['lib/assets/thumbnailWorker.js', 'lib/application/nativeAssetWorker.js']) if (!allowed.has(worker)) fail('REQUIRED_WORKER_MISSING');
}
function capture(source, expectedCommit) {
  source = path.resolve(source); noLinks(source); const spec = readSpec();
  if (!/^[0-9a-f]{40}$/.test(expectedCommit || '')) fail('EXPECTED_COMMIT_REQUIRED');
  if (!samePath(path.resolve(git(source, ['rev-parse', '--show-toplevel'])), source)) fail('SOURCE_NOT_REPOSITORY_ROOT');
  const commit = git(source, ['rev-parse', 'HEAD']); if (commit !== expectedCommit) fail('SOURCE_COMMIT_MISMATCH');
  if (git(source, ['status', '--porcelain', '--untracked-files=normal'])) fail('SOURCE_NOT_FROZEN');
  const tracked = new Set(git(source, ['ls-files']).split(/\r?\n/));
  const blobs = new Map(); let total = 0;
  for (const relative of spec.files) {
    if (!tracked.has(relative)) fail('ALLOWLIST_INPUT_NOT_TRACKED');
    const full = path.join(source, relative); noLinks(full, true); const before = fs.statSync(full);
    if (before.size > 2 * 1024 * 1024 || (total += before.size) > 16 * 1024 * 1024) fail('SOURCE_SIZE_LIMIT');
    const bytes = fs.readFileSync(full); privateCode(bytes, relative); noLinks(full, true); const after = fs.statSync(full);
    if (bytes.length !== before.size || before.size !== after.size || before.mtimeMs !== after.mtimeMs || before.ino !== after.ino) fail('SOURCE_CHANGED');
    blobs.set(relative, bytes);
  }
  checkDependencies(blobs, spec.files);
  return { source, commit, spec, blobs };
}
function pack(source, candidate, expectedCommit) {
  candidate = path.resolve(candidate); noLinks(candidate);
  if (fs.existsSync(candidate)) fail('CANDIDATE_ALREADY_EXISTS');
  const state = capture(source, expectedCommit);
  const inside = path.relative(state.source, candidate); if (!inside.startsWith('..' + path.sep) && inside !== '..' && !path.isAbsolute(inside)) fail('CANDIDATE_INSIDE_SOURCE');
  // A newly created destination is required; even an empty pre-existing folder is rejected.
  fs.mkdirSync(candidate, { recursive: false });
  try {
    const entries = [];
    for (const [relative, bytes] of state.blobs) {
      const full = path.join(candidate, 'cocoon', relative); noLinks(full); fs.mkdirSync(path.dirname(full), { recursive: true }); noLinks(path.dirname(full));
      fs.writeFileSync(full, bytes, { flag: 'wx' }); entries.push({ path: 'cocoon/' + relative, bytes: bytes.length, sha256: sha(bytes) });
    }
    // Re-check every source, commit and dirty status after the copy before publishing manifest.
    const after = capture(state.source, expectedCommit);
    for (const [file, bytes] of state.blobs) if (!bytes.equals(after.blobs.get(file))) fail('SOURCE_CHANGED');
    const payload = { contract: CONTRACT, sourceCommit: state.commit, profile: state.spec.profile, ownership: 'NOT_REVIEWED_NOT_FOR_PUBLIC_RELEASE', files: entries };
    const manifest = { ...payload, manifestSha256: sha(Buffer.from(JSON.stringify(payload))) };
    fs.writeFileSync(path.join(candidate, NAME), JSON.stringify(manifest, null, 2) + '\n', { flag: 'wx' });
    return verify(candidate, { expectedCommit, expectedManifestSha256: manifest.manifestSha256 });
  } catch (error) {
    // Keep incomplete evidence for review; never recursively delete a computed destination.
    throw error;
  }
}
function verify(candidate, { expectedCommit = null, expectedManifestSha256 = null } = {}) {
  candidate = path.resolve(candidate); noLinks(candidate); const spec = readSpec(), file = path.join(candidate, NAME); noLinks(file, true);
  if (fs.statSync(file).size > 256 * 1024) fail('MANIFEST_SIZE_LIMIT');
  let manifest; try { manifest = JSON.parse(fs.readFileSync(file, 'utf8')); } catch { fail('MANIFEST_INVALID'); }
  if (!exactKeys(manifest, ['contract', 'sourceCommit', 'profile', 'ownership', 'files', 'manifestSha256']) || manifest.contract !== CONTRACT || !/^[0-9a-f]{40}$/.test(manifest.sourceCommit) || manifest.profile !== spec.profile || manifest.ownership !== 'NOT_REVIEWED_NOT_FOR_PUBLIC_RELEASE' || !Array.isArray(manifest.files) || manifest.files.length !== spec.files.length || !/^[0-9a-f]{64}$/.test(manifest.manifestSha256)) fail('MANIFEST_INVALID');
  const { manifestSha256, ...payload } = manifest;
  if (sha(Buffer.from(JSON.stringify(payload))) !== manifestSha256) fail('MANIFEST_CHECKSUM_MISMATCH');
  if (expectedCommit && expectedCommit !== manifest.sourceCommit) fail('PACKAGE_COMMIT_MISMATCH');
  if (expectedManifestSha256 && expectedManifestSha256 !== manifestSha256) fail('PACKAGE_MANIFEST_MISMATCH');
  const allowedFiles = new Set([NAME]); const allowedDirs = new Set(); const blobs = new Map();
  for (let i = 0; i < manifest.files.length; i++) {
    const entry = manifest.files[i], relative = 'cocoon/' + spec.files[i];
    if (!exactKeys(entry, ['path', 'bytes', 'sha256']) || entry.path !== relative || !Number.isSafeInteger(entry.bytes) || entry.bytes < 0 || entry.bytes > 2 * 1024 * 1024 || !/^[0-9a-f]{64}$/.test(entry.sha256)) fail('MANIFEST_ENTRY_INVALID');
    safeRelative(entry.path); const full = path.join(candidate, relative); noLinks(full, true); const bytes = fs.readFileSync(full);
    if (bytes.length !== entry.bytes || sha(bytes) !== entry.sha256) fail('PACKAGE_HASH_MISMATCH');
    privateCode(bytes, spec.files[i]); blobs.set(spec.files[i], bytes); allowedFiles.add(relative);
    let dir = path.posix.dirname(relative); while (dir !== '.') { allowedDirs.add(dir); dir = path.posix.dirname(dir); }
  }
  function walk(directory, prefix = '') {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const relative = prefix + entry.name, full = path.join(directory, entry.name); noLinks(full);
      if (entry.isDirectory()) { if (!allowedDirs.has(relative)) fail('PACKAGE_UNEXPECTED_DIRECTORY'); walk(full, relative + '/'); }
      else if (!entry.isFile() || !allowedFiles.has(relative)) fail('PACKAGE_UNEXPECTED_FILE');
    }
  }
  walk(candidate); checkDependencies(blobs, spec.files);
  return { contract: CONTRACT, sourceCommit: manifest.sourceCommit, manifestSha256, files: manifest.files.length, bytes: manifest.files.reduce((a, x) => a + x.bytes, 0), verified: true, publicReleaseReady: false };
}
async function smoke(candidate, dataRoot, { expectedCommit, expectedManifestSha256, node = process.execPath } = {}) {
  const verified = verify(candidate, { expectedCommit, expectedManifestSha256 });
  if (!path.isAbsolute(dataRoot || '')) fail('EMPTY_DATA_ROOT_REQUIRED');
  dataRoot = path.resolve(dataRoot); noLinks(dataRoot);
  if (fs.existsSync(dataRoot)) fail('DATA_ROOT_ALREADY_EXISTS');
  const codeRoot = path.join(path.resolve(candidate), 'cocoon'); const relative = path.relative(path.resolve(candidate), dataRoot);
  if (!relative.startsWith('..' + path.sep) && relative !== '..' && !path.isAbsolute(relative)) fail('DATA_ROOT_INSIDE_CODE');
  noLinks(node, true); fs.mkdirSync(dataRoot, { recursive: false });
  const env = { ...process.env, COCOON_DATA_ROOT: dataRoot, PORT: '0', COCOON_DEVICE_ACCESS: '0', COCOON_LEGACY_APPLY: '0', COCOON_NO_OPEN: '1', COCOON_ADB: 'not-used-in-managed-mode' };
  // Do not inherit provider credentials or user import locations into the isolated smoke.
  for (const key of Object.keys(env)) if (/^(?:COCOON_(?!DATA_ROOT|DEVICE_ACCESS|LEGACY_APPLY|NO_OPEN|ADB)|STEAMGRIDDB|IGDB|SCREENSCRAPER|RETROACHIEVEMENTS|OPENAI_API_KEY)/i.test(key)) delete env[key];
  for (const key of ['NODE_OPTIONS', 'NODE_PATH', 'NODE_REPL_HISTORY']) delete env[key];
  const child = spawn(node, ['backend.cjs'], { cwd: codeRoot, env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let stopped = false, output = '', exitCode = null, exitSignal = null, readyResolve, readyReject;
  const ready = new Promise((resolve, reject) => { readyResolve = resolve; readyReject = reject; });
  const exit = new Promise(resolve => child.once('exit', (code, signal) => { exitCode = code; exitSignal = signal; resolve(); readyReject(Object.assign(new Error(), { code: 'OWNED_NODE_EXITED_EARLY' })); }));
  child.on('error', () => readyReject(Object.assign(new Error(), { code: 'OWNED_NODE_START_FAILED' })));
  child.stderr.on('data', () => {});
  child.stdout.on('data', bytes => {
    output += bytes.toString('utf8'); if (output.length > 65536) { output = ''; readyReject(Object.assign(new Error(), { code: 'OWNED_NODE_OUTPUT_LIMIT' })); return; }
    let end; while ((end = output.indexOf('\n')) >= 0) {
      const line = output.slice(0, end); output = output.slice(end + 1); if (!line.startsWith('COCOON_BACKEND_EVENT ')) continue;
      try { const value = JSON.parse(line.slice(21)); if (value.event === 'ready') readyResolve(value); else if (value.event === 'stopped') stopped = true; else if (value.event === 'failed') readyReject(Object.assign(new Error(), { code: 'OWNED_NODE_REPORTED_FAILURE' })); } catch {}
    }
  });
  const timer = setTimeout(() => readyReject(Object.assign(new Error(), { code: 'OWNED_NODE_READY_TIMEOUT' })), 12000);
  const request = async (base, route, headers = {}) => {
    const response = await fetch(base + route, { headers, signal: AbortSignal.timeout(5000), redirect: 'error' });
    return { status: response.status, text: await response.text() };
  };
  const checks = [];
  try {
    const info = await ready; clearTimeout(timer);
    if (info.contract !== 'cocoon.backend/1' || !/^http:\/\/127\.0\.0\.1:\d+\/$/.test(info.endpoint) || info.capabilities.deviceAccessEnabled !== false || info.capabilities.legacyApplyEnabled !== false) fail('OWNED_NODE_CONTRACT_INVALID');
    const unauth = await request(info.endpoint, 'api/games'); if (unauth.status !== 401) fail('AUTH_GUARD_FAILED'); checks.push('unauthenticated-games-rejected');
    const page = await request(info.endpoint, ''); const token = /<meta name="cocoon-art-token" content="([^"]+)"/.exec(page.text)?.[1];
    if (page.status !== 200 || !token || token.length < 22) fail('SESSION_PAGE_FAILED'); checks.push('tracked-inline-session-page');
    const headers = { 'x-cocoon-art-token': token, Origin: new URL(info.endpoint).origin };
    const games = await request(info.endpoint, 'api/games', headers); let catalog;
    try { catalog = JSON.parse(games.text); } catch { fail('GAMES_JSON_INVALID'); }
    if (games.status !== 200 || !Array.isArray(catalog) || catalog.length) fail('EMPTY_GAMES_FAILED'); checks.push('authenticated-empty-games');
    const identity = await request(info.endpoint, 'api/backend/library/identity', headers), library = JSON.parse(identity.text);
    if (identity.status !== 200 || library.contract !== 'cocoon.library-identity/1' || library.provider !== 'cocoon' || library.libraryId !== null) fail('EMPTY_LIBRARY_IDENTITY_FAILED'); checks.push('empty-library-stable-identity');
    const diagnostics = await request(info.endpoint, 'api/backend/diagnostics', headers);
    if (diagnostics.status !== 200 || JSON.parse(diagnostics.text).contract !== 'cocoon.diagnostics/1' || diagnostics.text.includes(token) || diagnostics.text.includes(dataRoot)) fail('DIAGNOSTICS_FAILED'); checks.push('redacted-diagnostics');
    const native = await request(info.endpoint, 'api/native-editor', headers);
    if (native.status !== 409 || JSON.parse(native.text).code !== 'EDITOR_LIBRARY_REQUIRED') fail('EMPTY_NATIVE_EDITOR_FAILED'); checks.push('native-editor-empty-library-typed');
    const state = await request(info.endpoint, 'api/backend/library', headers);
    if (state.status !== 200) fail('LIBRARY_STATE_FAILED'); checks.push('library-state');
    child.stdin.write(JSON.stringify({ contract: info.contract, instanceId: info.instanceId, command: 'shutdown' }) + '\n'); child.stdin.end();
    await Promise.race([exit, new Promise((_, reject) => { const timeout = setTimeout(() => reject(Object.assign(new Error(), { code: 'OWNED_NODE_SHUTDOWN_TIMEOUT' })), 10000); timeout.unref(); })]);
    if (!stopped || exitCode !== 0 || exitSignal) fail('OWNED_NODE_SHUTDOWN_FAILED'); checks.push('owned-stdin-clean-shutdown');
    verify(candidate, { expectedCommit, expectedManifestSha256 });
    return { ...verified, smoke: 'PASS', checks, deviceAccess: false, sourceCodeUnchanged: true };
  } finally {
    clearTimeout(timer); if (child.exitCode === null && child.signalCode === null) { child.stdin.destroy(); child.kill(); await Promise.race([exit, new Promise(resolve => setTimeout(resolve, 3000))]); }
  }
}
module.exports = { pack, verify, smoke, readSpec, capture };
if (require.main === module) {
  (async () => {
    const [command, first, second, commit, manifestHash] = process.argv.slice(2);
    if (command === 'pack') return pack(first, second, commit);
    if (command === 'verify') return verify(first, { expectedCommit: second, expectedManifestSha256: commit });
    if (command === 'smoke') return smoke(first, second, { expectedCommit: commit, expectedManifestSha256: manifestHash });
    fail('COMMAND_INVALID');
  })().then(result => process.stdout.write(JSON.stringify(result) + '\n'), error => { process.stderr.write((error.code || 'PACKAGE_FAILED') + '\n'); process.exitCode = 1; });
}
