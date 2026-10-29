// Reproduce the approved 120 Hz timelines from the archived 240 Hz manifest.
// Only --write changes the runtime manifest; no mode changes PNG files.
// node tools/resample-motion-120hz.cjs --write|--verify [revision]
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const { isDeepStrictEqual } = require('node:util');

const repo = path.resolve(__dirname, '..');
const sourceManifestPath = 'reference/motion-120hz/source-manifest-240hz.json';
const runtimeManifestPath = 'assets/strawberry-cat/manifest.json';
const approvedSourceSha256 = '6c567e7e3c7b76ec2bf759fb406d3d00a9db53aa154e2ef59bf27032d9739ba2';
const expectedClipNames = ['idle', 'blink', 'fall_asleep', 'sleep_loop', 'wake', 'rise', 'hold', 'settle', 'yawn', 'wink', 'meow'].sort();
const targetFps = 120;
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const parse = bytes => JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
const serialize = manifest => JSON.stringify(manifest, null, 2) + '\n';
const duration = clip => clip.frames.reduce((sum, frame) => sum + frame.durationMs, 0);

function validateClip(clip) {
  assert(clip && typeof clip.loop === 'boolean' && Array.isArray(clip.frames) && clip.frames.length > 0,
    'Each clip needs a loop flag and at least one frame.');
  assert(clip.frames.every(frame => typeof frame.file === 'string' && frame.file.length > 0
    && Number.isSafeInteger(frame.durationMs) && frame.durationMs > 0), 'Invalid animation frame.');
  assert(Number.isSafeInteger(duration(clip)), 'Clip duration exceeds the safe integer range.');
}

function resampleClip(clip) {
  validateClip(clip);
  // Keeping idle/hold as one frame avoids enabling continuous WPF rendering.
  if (clip.frames.length === 1) return structuredClone(clip);
  const total = duration(clip), frames = [];
  let index = 0, sourceEnd = clip.frames[0].durationMs;
  for (let slot = 0, start = 0; start < total; slot++, start = Math.round(slot * 1000 / targetFps)) {
    const end = Math.min(total, Math.round((slot + 1) * 1000 / targetFps));
    while (start >= sourceEnd && index < clip.frames.length - 1)
      sourceEnd += clip.frames[++index].durationMs;
    frames.push({
      file: end === total ? clip.frames.at(-1).file : clip.frames[index].file,
      durationMs: end - start
    });
  }
  // Do not combine repeated slots: interrupted reactions use individual frame durations.
  const sampled = { ...structuredClone(clip), frames };
  assert.equal(duration(sampled), total, 'Sampling must preserve the complete clip duration.');
  assert.equal(sampled.frames[0].file, clip.frames[0].file, 'Sampling must preserve the first pose.');
  assert.equal(sampled.frames.at(-1).file, clip.frames.at(-1).file, 'Sampling must preserve the final pose.');
  return sampled;
}

function buildManifest(source, sourceManifestSha256, revision = '0.3.2') {
  assert(/^\d+\.\d+\.\d+$/.test(revision), 'Revision must be numeric x.y.z.');
  assert.equal(sourceManifestSha256, approvedSourceSha256, 'The archived 240 Hz source manifest changed.');
  assert.equal(source.motion?.sampleFps, 240, 'Expected a 240 Hz source manifest.');
  assert.deepEqual(Object.keys(source.clips || {}).sort(), expectedClipNames, 'Expected all 11 approved clips.');
  const manifest = structuredClone(source);
  for (const [name, clip] of Object.entries(source.clips)) manifest.clips[name] = resampleClip(clip);
  manifest.motion.revision = revision;
  manifest.motion.sampleFps = targetFps;
  for (const [name, clip] of Object.entries(manifest.clips)) {
    const metadata = name === 'wink' ? manifest.motion.clickReactions?.wink : manifest.motion.idleExpressions?.clips?.[name];
    if (metadata) Object.assign(metadata, {
      frameSlots: clip.frames.length,
      uniquePngs: new Set(clip.frames.map(frame => frame.file)).size
    });
  }
  // These claims describe the original authoring timeline, retained in the archived source.
  for (const key of ['blinkPreserved', 'blinkKeyPosesPreserved', 'blinkTransitionFramesAdded']) delete manifest.motion[key];
  delete manifest.previewSampling;
  manifest.sampling = {
    sourceManifest: sourceManifestPath,
    sourceManifestSha256,
    sourceFps: 240,
    targetFps,
    method: 'Original-time frame selection at rounded 120 Hz boundaries; original first/last pose retained; repeated slots kept; static clips unchanged.',
    originalPngBytesPreserved: true,
    visualApproval: { status: 'approved', date: '2026-10-30', reviewer: 'user' }
  };
  return manifest;
}

async function containedFile(root, relative) {
  assert(typeof relative === 'string' && relative.length > 0 && !path.isAbsolute(relative)
    && !relative.includes('\\') && !relative.split('/').includes('..'), 'Expected a contained forward-slash path.');
  const realRoot = await fs.realpath(root), resolved = path.resolve(realRoot, relative);
  const inside = candidate => {
    const tail = path.relative(realRoot, candidate);
    return tail && tail !== '..' && !tail.startsWith('..' + path.sep) && !path.isAbsolute(tail);
  };
  assert(inside(resolved), 'Path escapes its expected root: ' + relative);
  const realFile = await fs.realpath(resolved);
  assert(inside(realFile), 'Source link escapes its expected root: ' + relative);
  const stat = await fs.stat(realFile);
  assert(stat.isFile() && stat.size > 0, 'Expected a nonempty file: ' + relative);
  await fs.access(realFile, fs.constants.R_OK);
  return { path: realFile, bytes: stat.size };
}

// Read-only API for release tooling. Requires tracked sources/assets only, never dist/artifacts.
async function collect(root = repo, { revision = '0.3.2' } = {}) {
  const sourceFile = await containedFile(root, sourceManifestPath);
  const sourceBytes = await fs.readFile(sourceFile.path), sourceManifestSha256 = hash(sourceBytes);
  const manifest = buildManifest(parse(sourceBytes), sourceManifestSha256, revision);
  const referencedFiles = [...new Set(Object.values(manifest.clips).flatMap(clip => clip.frames.map(frame => frame.file)))].sort();
  let pngBytes = 0;
  for (const relative of referencedFiles) {
    assert(relative.startsWith('frames/') && relative.endsWith('.png'), 'Expected an animation PNG: ' + relative);
    const file = await containedFile(path.join(root, 'assets/strawberry-cat'), relative);
    pngBytes += file.bytes;
  }
  const clipStats = Object.fromEntries(Object.entries(manifest.clips).map(([name, clip]) => [name, {
    durationMs: duration(clip), frameSlots: clip.frames.length,
    uniquePngs: new Set(clip.frames.map(frame => frame.file)).size
  }]));
  return { manifest, sourceManifestSha256, referencedFiles, clipStats, pngBytes };
}

async function main(args) {
  assert(args.length <= 2 && ['--write', '--verify'].includes(args[0] || '--verify'),
    'Usage: node tools/resample-motion-120hz.cjs --write|--verify [revision]');
  const mode = args[0] || '--verify', result = await collect(repo, { revision: args[1] || '0.3.2' });
  const output = serialize(result.manifest), destination = await containedFile(repo, runtimeManifestPath);
  if (mode === '--write') await fs.writeFile(destination.path, output);
  else {
    const current = await fs.readFile(destination.path);
    assert(isDeepStrictEqual(parse(current), result.manifest),
      'Runtime manifest differs from the approved 120 Hz sampling; run --write to reproduce it.');
    assert.equal(current.toString('utf8'), output, 'Runtime manifest formatting differs from the reproducible output.');
  }
  return {
    passed: true, mode: mode.slice(2), revision: result.manifest.motion.revision,
    sourceManifest: sourceManifestPath, sourceManifestSha256: result.sourceManifestSha256,
    runtimeManifest: runtimeManifestPath, runtimeManifestSha256: hash(output), targetFps,
    uniquePngs: result.referencedFiles.length, pngBytes: result.pngBytes, clips: result.clipStats
  };
}

module.exports = { resampleClip, buildManifest, collect };
if (require.main === module) main(process.argv.slice(2)).then(result => console.log(JSON.stringify(result, null, 2)))
  .catch(error => { console.error(error.message); process.exitCode = 1; });
