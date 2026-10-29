// Freeze approved runtime inputs without changing any artwork.
// node tools/freeze-release.cjs --write [version]
// node tools/freeze-release.cjs --verify [version]
// Verification is read-only; redirect its JSON output to artifacts if desired.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');

const repo = path.resolve(__dirname, '..');
const assetRoot = path.join(repo, 'assets/strawberry-cat');
const manifestPath = 'assets/strawberry-cat/manifest.json';
const projectPath = 'src/TablePet.App/TablePet.App.csproj';
const approvedKeysPath = 'reference/idle-v9-approved-keys.json';
const recipePath = 'reference/idle-v9-fluid-selection.json';
const recipeTools = [
  'tools/build-wink-candidate.cjs',
  'tools/wink-endpoint.cjs',
  'tools/motion-interpolate.cjs',
  'tools/install-wink-v9-smooth.cjs'
];
const expectedClipNames = ['idle', 'blink', 'fall_asleep', 'sleep_loop', 'wake', 'rise', 'hold', 'settle', 'yawn', 'wink'].sort();
const expectedKeys = ['idle', 'prepare', 'half', 'low', 'peak', 'reopening', 'linger', 'relax'];
const expectedKnots = [[0, 0], [140, 1], [245, 2], [335, 3], [440, 4], [790, 4], [900, 5], [1120, 6], [1290, 7], [1500, 8]];
const sha256 = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const json = bytes => JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
const assert = (condition, message) => { if (!condition) throw Error(message); };
const same = (actual, expected) => JSON.stringify(actual) === JSON.stringify(expected);

async function safeRead(root, relative) {
  assert(typeof relative === 'string' && relative.length > 0 && !path.isAbsolute(relative) && !relative.includes('\\'), 'Expected a relative forward-slash path.');
  const resolved = path.resolve(root, relative);
  const prefix = root.toLowerCase() + path.sep;
  assert(resolved.toLowerCase().startsWith(prefix), 'Path escapes its expected root: ' + relative);
  const real = await fs.realpath(resolved);
  assert(real.toLowerCase().startsWith(prefix), 'Source link escapes its expected root: ' + relative);
  return fs.readFile(real);
}

function fileAt(clip, timeMs) {
  let end = 0;
  for (const frame of clip.frames) { end += frame.durationMs; if (timeMs < end) return frame.file; }
  return clip.frames.at(-1).file;
}

async function collectLegacy(version) {
  const projectBytes = await safeRead(repo, projectPath);
  const sourceVersion = /<Version>([^<]+)<\/Version>/.exec(projectBytes.toString('utf8'))?.[1];
  assert(/^\d+\.\d+\.\d+$/.test(sourceVersion || ''), 'Project must declare a numeric release version.');
  version ??= sourceVersion;
  assert(version === sourceVersion, `Requested version ${version} differs from project version ${sourceVersion}.`);
  assert(/^\d+\.\d+\.\d+$/.test(version), 'Version must be numeric x.y.z.');

  const manifestBytes = await safeRead(repo, manifestPath), manifest = json(manifestBytes);
  const approvedBytes = await safeRead(repo, approvedKeysPath), approved = json(approvedBytes);
  const recipeBytes = await safeRead(repo, recipePath), recipe = json(recipeBytes);
  assert(manifest.motion?.revision === version, 'Runtime manifest revision must match the project version.');
  assert(same(manifest.idleActions, [{ clip: 'blink', weight: 4 }, { clip: 'yawn', weight: 1 }]), 'Idle actions must be blink and yawn with weights 4:1.');
  assert(same(manifest.motion?.clickReactions?.choices, ['rise', 'wink']), 'Click choices must be rise and wink.');
  assert(manifest.motion.clickReactions.selection === 'equal probability; rise includes hold/settle', 'Click metadata must retain equal probability.');
  const winkMetadata = manifest.motion.clickReactions.wink;
  for (const [key, expected] of Object.entries({ durationMs: 1500, closingMs: 440, peakHoldMs: 350, openingMs: 330 }))
    assert(winkMetadata?.[key] === expected, 'Unexpected Wink metadata: ' + key);
  assert(winkMetadata.recipe === recipePath, 'Runtime Wink recipe differs from the approved recipe.');
  assert(same(Object.keys(manifest.clips || {}).sort(), expectedClipNames), 'Expected the approved 10 runtime clips.');
  assert(same(recipe.knots, expectedKnots) && same(recipe.keysOrder, [...expectedKeys, 'idle']), 'Wink recipe timing or key order changed.');
  assert(approved.keys?.length === 8 && same(approved.keys.map(k => k.id), expectedKeys), 'Expected the 8 approved V9 keys.');
  assert(approved.keys.every(k => /^[a-f0-9]{64}$/.test(k.sha256)), 'Invalid approved V9 key hash.');

  const clips = {}, files = new Set();
  let slots = 0;
  for (const name of expectedClipNames) {
    const clip = manifest.clips[name];
    assert(typeof clip.loop === 'boolean' && Array.isArray(clip.frames) && clip.frames.length > 0, 'Invalid clip: ' + name);
    let durationMs = 0;
    for (const frame of clip.frames) {
      assert(typeof frame.file === 'string' && frame.file.endsWith('.png') && Number.isInteger(frame.durationMs) && frame.durationMs > 0, 'Invalid frame in: ' + name);
      files.add(frame.file); durationMs += frame.durationMs;
    }
    slots += clip.frames.length;
    clips[name] = { timelineSHA256: sha256(JSON.stringify(clip)), loop: clip.loop, durationMs, slots: clip.frames.length, uniquePngs: new Set(clip.frames.map(f => f.file)).size };
  }
  assert(files.size === 1833 && slots === 3485, 'Expected 1833 referenced PNGs and 3485 time slots.');
  assert(clips.wink.durationMs === 1500 && clips.wink.slots === 367 && clips.wink.uniquePngs === 277 && !clips.wink.loop, 'Wink runtime timeline differs from the approved candidate.');
  assert(clips.blink.durationMs === 375 && clips.yawn.durationMs === 4400, 'Blink or yawn duration changed.');
  const wink = manifest.clips.wink, idle = manifest.clips.idle.frames[0].file;
  assert(wink.frames[0].file === idle && wink.frames.at(-1).file === idle, 'Wink must share exact idle start and end frame paths.');

  const pngHashes = new Map(), digest = crypto.createHash('sha256');
  for (const file of [...files].sort()) {
    const hash = sha256(await safeRead(assetRoot, file));
    pngHashes.set(file, hash);
    digest.update(file + '\0' + hash + '\n');
  }
  const keyHashById = new Map(approved.keys.map(key => [key.id, key.sha256]));
  const keyTimings = recipe.knots.map(([timeMs, phase]) => {
    const id = recipe.keysOrder[phase], file = fileAt(wink, timeMs), hash = pngHashes.get(file);
    assert(hash === keyHashById.get(id), `V9 key mismatch at ${timeMs}ms (${id}).`);
    return { timeMs, id, file, sha256: hash };
  });
  let elapsed = 0;
  for (const frame of wink.frames) {
    const end = elapsed + frame.durationMs;
    if (end > 440 && elapsed < 790) assert(pngHashes.get(frame.file) === keyHashById.get('peak'), 'The 350ms peak hold was interrupted.');
    elapsed = end;
  }

  const inputs = [{ path: projectPath, sha256: sha256(projectBytes) }, { path: approvedKeysPath, sha256: sha256(approvedBytes) }, { path: recipePath, sha256: sha256(recipeBytes) }];
  for (const file of recipeTools) inputs.push({ path: file, sha256: sha256(await safeRead(repo, file)) });
  return {
    schemaVersion: 1,
    version,
    frozenDate: '2026-10-29',
    manifest: { path: manifestPath, sha256: sha256(manifestBytes), revision: manifest.motion.revision },
    idleActions: manifest.idleActions,
    clickReactions: manifest.motion.clickReactions,
    statistics: { clips: expectedClipNames.length, frameSlots: slots, uniquePngs: files.size },
    pngAggregate: { sha256: digest.digest('hex'), method: 'SHA-256 of sorted asset-relative path + NUL + lowercase PNG SHA-256 + LF, using UTF-8.' },
    clips,
    approvedV9Keys: approved.keys,
    winkKeyTimings: keyTimings,
    preservedEndpoints: { file: idle, sha256: pngHashes.get(idle) },
    inputs,
    verificationScope: 'Runtime manifest, clip timelines, referenced PNG bytes, approved V9 keys, source version and rebuild inputs only. Build, installer and local upgrade results are recorded separately.'
  };
}

async function collect(version) {
  const projectBytes = await safeRead(repo, projectPath);
  version ??= /<Version>([^<]+)<\/Version>/.exec(projectBytes.toString('utf8'))?.[1];
  if (version === '0.3.2') return require('./freeze-120hz-release.cjs').collect(version);
  return collectLegacy(version);
}

function differences(expected, actual, prefix = '') {
  if (expected === actual) return [];
  if (!expected || !actual || typeof expected !== 'object' || typeof actual !== 'object' || Array.isArray(expected) !== Array.isArray(actual)) return [prefix || '(root)'];
  const changed = [];
  for (const key of [...new Set([...Object.keys(expected), ...Object.keys(actual)])].sort()) changed.push(...differences(expected[key], actual[key], prefix ? prefix + '.' + key : key));
  return changed;
}

async function main(args) {
  assert(args.length >= 1 && args.length <= 2 && ['--write', '--verify'].includes(args[0]), 'Usage: node tools/freeze-release.cjs --write|--verify [version]');
  const snapshot = await collect(args[1]);
  const relative = `reference/releases/frozen-${snapshot.version}.json`, destination = path.join(repo, relative);
  if (args[0] === '--write') {
    await fs.mkdir(path.dirname(destination), { recursive: true });
    await fs.writeFile(destination, JSON.stringify(snapshot, null, 2) + '\n', { flag: 'wx' });
  } else {
    const frozen = json(await safeRead(repo, relative));
    const changed = differences(frozen, snapshot);
    assert(changed.length === 0, 'Frozen inputs differ: ' + changed.slice(0, 20).join(', ') + (changed.length > 20 ? ` (and ${changed.length - 20} more)` : ''));
  }
  return { passed: true, mode: args[0].slice(2), version: snapshot.version, record: relative, frozenDate: snapshot.frozenDate, statistics: snapshot.statistics, manifestSHA256: snapshot.manifest.sha256, pngAggregateSHA256: snapshot.pngAggregate.sha256, ...(snapshot.approvedV9Keys ? { approvedV9Keys: snapshot.approvedV9Keys.length } : { sampleFps: snapshot.sampleFps, sourceManifestSHA256: snapshot.sourceManifest.sha256 }), scope: snapshot.verificationScope };
}

module.exports = { collect, differences, fileAt };
if (require.main === module) main(process.argv.slice(2)).then(result => console.log(JSON.stringify(result, null, 2))).catch(error => { console.error(error.message); process.exitCode = 1; });
