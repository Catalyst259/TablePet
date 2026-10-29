// The visually approved 0.3.2 release uses existing PNGs sampled at 120 Hz.
// Called by freeze-release.cjs; collection and validation never write files.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');

const repo = path.resolve(__dirname, '..');
const manifestPath = 'assets/strawberry-cat/manifest.json';
const sourceManifestPath = 'reference/motion-120hz/source-manifest-240hz.json';
const projectPath = 'src/TablePet.App/TablePet.App.csproj';
const sourceManifestSHA256 = '6c567e7e3c7b76ec2bf759fb406d3d00a9db53aa154e2ef59bf27032d9739ba2';
const approvedClipsSHA256 = '0a644763b1732fe6cfbe827f8574fb91f759ec9fb15e46f5b5b5239f00b9ba92';
const approvedPngAggregate = 'fb5a6739ff4184bfa051d0cba29dbf5a5af60ba2ae2d71e2c80ed14d7158186c';
const approvedSourcePngAggregate = '544c20a896cb42053ca76897d4e9e73802df527310cd4f8a720ff87c92c4b995';
const supportHashes = {
  'fallback.png': '3edd6f98d0ef5d7ec5121f59e241302cad4e8ee5b205cf3052621244f440f9ee',
  'tray.ico': '0c64952e32a606cafeb419d37622b94121e6cf1341df639b206b304566e2c3dc'
};
const expectedClips = {
  idle:        { loop: true,  durationMs: 1000, slots: 1,   uniquePngs: 1 },
  blink:       { loop: false, durationMs: 375,  slots: 45,  uniquePngs: 25 },
  fall_asleep: { loop: false, durationMs: 1750, slots: 210, uniquePngs: 208 },
  sleep_loop:  { loop: true,  durationMs: 3600, slots: 432, uniquePngs: 257 },
  wake:        { loop: false, durationMs: 1050, slots: 126, uniquePngs: 126 },
  rise:        { loop: false, durationMs: 800,  slots: 96,  uniquePngs: 95 },
  hold:        { loop: true,  durationMs: 1000, slots: 1,   uniquePngs: 1 },
  settle:      { loop: false, durationMs: 1000, slots: 120, uniquePngs: 120 },
  yawn:        { loop: false, durationMs: 4400, slots: 528, uniquePngs: 270 },
  wink:        { loop: false, durationMs: 1500, slots: 180, uniquePngs: 137 },
  meow:        { loop: false, durationMs: 1400, slots: 168, uniquePngs: 85 }
};
const sha256 = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const json = bytes => JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
const assert = (condition, message) => { if (!condition) throw Error(message); };
const same = (actual, expected) => JSON.stringify(actual) === JSON.stringify(expected);
const aggregateMethod = 'SHA-256 of sorted asset-relative path + NUL + lowercase PNG SHA-256 + LF, using UTF-8.';

async function safeRead(root, relative) {
  assert(typeof relative === 'string' && relative.length > 0 && !path.isAbsolute(relative) && !relative.includes('\\'), 'Expected a relative forward-slash path.');
  const prefix = root.toLowerCase() + path.sep;
  const resolved = path.resolve(root, relative);
  assert(resolved.toLowerCase().startsWith(prefix), 'Path escapes its expected root: ' + relative);
  const real = await fs.realpath(resolved);
  assert(real.toLowerCase().startsWith(prefix), 'Source link escapes its expected root: ' + relative);
  return fs.readFile(real);
}

function validateManifest(manifest, source) {
  assert(manifest.motion?.revision === '0.3.2', 'Runtime manifest revision must be 0.3.2.');
  assert(manifest.motion.sampleFps === 120 && source.motion?.sampleFps === 240, 'Expected 240 Hz source and 120 Hz release sampling.');
  assert(!Object.hasOwn(manifest, 'previewSampling'), 'Release manifest must not contain previewSampling.');
  assert(same(Object.keys(manifest.clips || {}).sort(), Object.keys(expectedClips).sort()), 'Expected the approved 11 runtime clips, including meow.');
  assert(same(manifest.idleActions, [{ clip: 'blink', weight: 4 }, { clip: 'yawn', weight: 1 }]), 'Idle actions must retain blink/yawn weights 4:1.');
  assert(same(manifest.motion.clickReactions?.choices, ['rise', 'wink']), 'Click choices must retain rise and wink.');
  assert(manifest.motion.clickReactions.selection === 'equal probability; rise includes hold/settle', 'Click selection metadata changed.');
  const clips = {}, files = new Set();
  let slots = 0;
  for (const [name, expected] of Object.entries(expectedClips)) {
    const clip = manifest.clips[name], original = source.clips?.[name];
    assert(Array.isArray(clip.frames) && clip.frames.length > 0 && original?.frames?.length, 'Invalid or missing clip: ' + name);
    let durationMs = 0;
    for (const frame of clip.frames) {
      assert(typeof frame.file === 'string' && frame.file.startsWith('frames/') && frame.file.endsWith('.png') && Number.isInteger(frame.durationMs) && frame.durationMs > 0, 'Invalid frame in: ' + name);
      assert(expected.slots === 1 || frame.durationMs === 8 || frame.durationMs === 9, 'Non-120 Hz frame duration in: ' + name);
      files.add(frame.file);
      durationMs += frame.durationMs;
    }
    const actual = { loop: clip.loop, durationMs, slots: clip.frames.length, uniquePngs: new Set(clip.frames.map(frame => frame.file)).size };
    assert(same(actual, expected), 'Approved clip statistics changed: ' + name);
    assert(clip.frames[0].file === original.frames[0].file && clip.frames.at(-1).file === original.frames.at(-1).file, 'Original first/last pose changed: ' + name);
    assert(durationMs === original.frames.reduce((sum, frame) => sum + frame.durationMs, 0) && clip.loop === original.loop, 'Original duration or loop changed: ' + name);
    slots += clip.frames.length;
    clips[name] = { ...actual, timelineSHA256: sha256(JSON.stringify(clip)), firstFrame: clip.frames[0].file, lastFrame: clip.frames.at(-1).file };
  }
  assert(files.size === 1275 && slots === 1907, 'Expected 1275 referenced PNGs and 1907 time slots.');
  assert(sha256(JSON.stringify(manifest.clips)) === approvedClipsSHA256, 'Clip timelines differ from the visually approved 120 Hz preview.');
  const expected = require('./resample-motion-120hz.cjs').buildManifest(source, sourceManifestSHA256, '0.3.2');
  assert(same(manifest, expected), 'Release manifest differs from deterministic 120 Hz resampling, including sampling/click metadata.');
  return { clips, files, statistics: { clips: 11, frameSlots: slots, uniquePngs: files.size } };
}

async function hashPngs(assetRoot, files, cache) {
  const digest = crypto.createHash('sha256');
  let bytes = 0;
  for (const file of [...files].sort()) {
    if (!cache.has(file)) {
      const data = await safeRead(assetRoot, file);
      assert(data.length >= 8 && data.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])), 'Not a PNG: ' + file);
      cache.set(file, { sha256: sha256(data), bytes: data.length });
    }
    const value = cache.get(file);
    bytes += value.bytes;
    digest.update(file + '\0' + value.sha256 + '\n');
  }
  return { sha256: digest.digest('hex'), method: aggregateMethod, uniquePngs: files.size, bytes };
}

async function collect(version = '0.3.2', root = repo) {
  root = path.resolve(root);
  const projectBytes = await safeRead(root, projectPath);
  const sourceVersion = /<Version>([^<]+)<\/Version>/.exec(projectBytes.toString('utf8'))?.[1];
  assert(version === '0.3.2' && sourceVersion === version, 'The 120 Hz freeze requires project version 0.3.2.');
  const sourceBytes = await safeRead(root, sourceManifestPath);
  assert(sha256(sourceBytes) === sourceManifestSHA256, 'The approved 240 Hz source manifest changed.');
  const source = json(sourceBytes), manifestBytes = await safeRead(root, manifestPath), manifest = json(manifestBytes);
  const { clips, files, statistics } = validateManifest(manifest, source);
  const assetRoot = path.join(root, 'assets/strawberry-cat'), pngHashes = new Map();
  const pngAggregate = await hashPngs(assetRoot, files, pngHashes);
  assert(pngAggregate.sha256 === approvedPngAggregate, 'Runtime PNG bytes differ from the visually approved 120 Hz preview.');
  const sourceFiles = new Set(Object.values(source.clips).flatMap(clip => clip.frames.map(frame => frame.file)));
  const sourcePngAggregate = await hashPngs(assetRoot, sourceFiles, pngHashes);
  assert(sourceFiles.size === 2030 && sourcePngAggregate.sha256 === approvedSourcePngAggregate, 'The original 240 Hz runtime PNG set changed.');
  const supportAssets = [];
  for (const [file, expectedHash] of Object.entries(supportHashes)) {
    const bytes = await safeRead(assetRoot, file), hash = sha256(bytes);
    assert(hash === expectedHash, 'Approved support asset changed: ' + file);
    supportAssets.push({ path: 'assets/strawberry-cat/' + file, sha256: hash, bytes: bytes.length });
  }
  const inputs = [{ path: projectPath, sha256: sha256(projectBytes) }, { path: sourceManifestPath, sha256: sha256(sourceBytes) }];
  for (const file of ['tools/resample-motion-120hz.cjs', 'tools/freeze-release.cjs', 'tools/freeze-120hz-release.cjs'])
    inputs.push({ path: file, sha256: sha256(await safeRead(root, file)) });
  return {
    schemaVersion: 2,
    version,
    frozenDate: '2026-10-30',
    sampleFps: 120,
    manifest: { path: manifestPath, sha256: sha256(manifestBytes), revision: manifest.motion.revision },
    sourceManifest: { path: sourceManifestPath, sha256: sourceManifestSHA256, sampleFps: 240 },
    sampling: manifest.sampling,
    idleActions: manifest.idleActions,
    clickReactions: manifest.motion.clickReactions,
    statistics,
    clipsSHA256: approvedClipsSHA256,
    clips,
    pngAggregate,
    sourcePngAggregate,
    supportAssets,
    inputs,
    verificationScope: 'Approved 120 Hz runtime manifest, all 11 clip timelines including meow, original durations/loop/endpoints, idle/click metadata, 1275 runtime PNG bytes, fallback/tray, the 240 Hz source manifest and its 2030 PNG bytes, source version and resampling/freeze tools. Historical authoring candidates, build, installer and local upgrade results are outside this freeze.'
  };
}

module.exports = { collect, validateManifest };
