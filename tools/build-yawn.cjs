'use strict';
// Rebuild only yawn from complete source poses, with no face/edge compositing.
// Original artwork, historical manifests and every other animation stay intact.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const sharp = require('sharp');
const { interpolate } = require('./motion-interpolate.cjs');
const { correctSleepEndpoint } = require('./sleep-endpoint.cjs');
const { resampleClip } = require('./resample-motion-60hz.cjs');
const { applyYawnRepair } = require('./yawn-runtime.cjs');
const repo = path.resolve(__dirname, '..');
const root = path.join(repo, 'assets/strawberry-cat');
const work = path.join(repo, 'artifacts/yawn-repair');
const recordPath = path.join(repo, 'reference/yawn-repair/runtime.json');
const candidatePath = path.join(repo, 'reference/yawn-repair/yawn-v3.json');
const previousPath = path.join(repo, 'reference/yawn-repair/contour-v2.json');
const idle = 'frames/fall_asleep-00.png';
const canvas = { width: 512, height: 512, channels: 4 };
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const json = value => JSON.stringify(value, null, 2) + '\n';
const readRaw = file => sharp(path.join(root, file)).ensureAlpha().raw().toBuffer();

async function prepareKeys() {
  const keys = [idle], normalization = [];
  await fs.mkdir(path.join(root, 'keyframes-yawn-v3'), { recursive: true });
  const specs = [
    ['half', 'frames/blink-01.png', 0, 0],
    ['low', 'frames/blink-02.png', 0, 0],
    ['closed', 'frames/blink-03.png', 0, 0],
    ['start', 'sources/idle-v5/yawn-start-v1.png', 1, -1]
  ];
  for (const [name, source, x, y] of specs) {
    const encoded = await fs.readFile(path.join(root, source));
    const resize = { width: 512, height: 512, fit: 'fill', kernel: 'lanczos3' };
    const data = await sharp(encoded).toColourspace('srgb').resize(resize).ensureAlpha().raw().toBuffer();
    // Retain the complete drawn pose. Nose, cheeks, grey/white border and fur
    // tufts all move together; only the grounded paw anchor is registered.
    for (let i = 0; i < data.length; i += 4) if (data[i + 3] <= 3) data.fill(0, i, i + 4);
    const expanded = await sharp(data, { raw: canvas })
      .extend({ left: 40, right: 40, top: 40, bottom: 40, background: '#00000000' }).png().toBuffer();
    const registered = await sharp(expanded).extract({ left: 40 - x, top: 40 - y, width: 512, height: 512 }).ensureAlpha().raw().toBuffer();
    const file = `keyframes-yawn-v3/yawn-${name}.png`;
    await sharp(registered, { raw: canvas }).png().toFile(path.join(root, file));
    normalization.push({ id: name, source, sourceSHA256: hash(encoded), output: file,
      translation: { x, y }, resize, transparentAlphaCutoff: 3, completePose: true });
    keys.push(file);
  }
  keys.push(...['middle', 'peak'].map(name => `keyframes-v7/yawn/yawn-${name}.png`));
  return { keys, normalization };
}

async function opaqueInterior(keys) {
  const mask = Buffer.alloc(512 * 512, 255);
  for (const file of keys) {
    const data = await readRaw(file);
    for (let p = 0; p < mask.length; p++) if (data[p * 4 + 3] < 250) mask[p] = 0;
  }
  // Only the stable interior common to every pose is protected. The moving
  // silhouette and its antialiasing remain the original RIFE reconstruction.
  return sharp(mask, { raw: { width: 512, height: 512, channels: 1 } })
    .dilate(12).blur(3).toColourspace('b-w').raw().toBuffer();
}

async function build() {
  await fs.mkdir(work, { recursive: true });
  const archived = JSON.parse(await fs.readFile(path.join(repo, 'reference/motion-60hz/source-manifest-0.3.3.json'), 'utf8'));
  const sourceClip = resampleClip(archived.clips.yawn);
  const { keys, normalization } = await prepareKeys();
  if (process.argv.includes('--keys-only')) return { keys, normalization };
  const sourceHashes = Object.fromEntries(await Promise.all(keys.map(async file => [file, hash(await fs.readFile(path.join(root, file)))])));
  const fingerprint = hash(Buffer.from(JSON.stringify({ sourceHashes, factor: 64, temporalTta: true, completeSourcePoses: true, opaqueInterior: [250,12,3], endpointWindow: 4 })));
  const output = path.join(work, 'rife-' + fingerprint.slice(0, 12));
  let report;
  try { report = JSON.parse(await fs.readFile(path.join(output, 'interpolation-report.json'), 'utf8')); }
  catch (error) {
    if (error.code !== 'ENOENT') throw error;
    report = await interpolate({ frames: keys.map(file => path.join(root, file)), outputDirectory: output, factor: 64, temporalTta: true, gpu: 0 });
  }
  assert.equal(report.outputFrames, 385);
  assert.equal(report.keyframeChecks.length, keys.length);
  assert(report.keyframeChecks.every(check => check.exactRgbMatch));
  const interior = await opaqueInterior(keys), files = new Map([[0, idle]]);
  const directory = 'frames/yawn-v3/' + fingerprint.slice(0, 12);
  await fs.mkdir(path.join(root, directory), { recursive: true });
  for (const frame of sourceClip.frames) {
    const match = /yawn-[a-f0-9]+-(\d+)\.png$/.exec(frame.file);
    const index = match ? Number(match[1]) : 0;
    if (files.has(index)) continue;
    const file = `${directory}/${String(index).padStart(5, '0')}.png`;
    if (index % 64 === 0) await fs.copyFile(path.join(root, keys[index / 64]), path.join(root, file));
    else {
      const inferred = path.join(output, String(index).padStart(5, '0') + '.png');
      const data = await sharp(inferred).ensureAlpha().raw().toBuffer();
      for (let p = 0; p < interior.length; p++) {
        const i = p * 4, alpha = data[i + 3];
        if (!interior[p] || alpha === 255) continue;
        const fixedAlpha = Math.round(alpha + (255 - alpha) * interior[p] / 255);
        // Undo dual-matte RGB unpremultiplication as opacity is restored.
        // Raising alpha alone would retain the bright seam.
        for (let c = 0; c < 3; c++) data[i + c] = fixedAlpha ? Math.round(data[i + c] * alpha / fixedAlpha) : 0;
        data[i + 3] = fixedAlpha;
      }
      const corrected = path.join(output, 'opaque-' + String(index).padStart(5, '0') + '.png');
      await sharp(data, { raw: canvas }).png().toFile(corrected);
      const nearest = Math.round(index / 64), distance = Math.abs(index - nearest * 64);
      await correctSleepEndpoint({ basePath: path.join(root, keys[nearest]), framePath: corrected,
        index: distance, window: 4, outputPath: path.join(root, file) });
    }
    files.set(index, file);
  }
  const clip = { ...sourceClip, frames: sourceClip.frames.map(frame => ({ ...frame,
    file: files.get(Number(/yawn-[a-f0-9]+-(\d+)\.png$/.exec(frame.file)?.[1] || 0)) })) };
  const frames = await Promise.all([...new Set(clip.frames.map(frame => frame.file))].sort().map(async file => ({ file, sha256: hash(await fs.readFile(path.join(root, file))) })));
  let previous;
  try { previous = JSON.parse(await fs.readFile(previousPath, 'utf8')); }
  catch (error) {
    if (error.code !== 'ENOENT') throw error;
    const bytes = await fs.readFile(recordPath), installed = JSON.parse(bytes);
    assert.equal(installed.revision, 'yawn-v2', 'The previous repair needs an explicit history record.');
    await fs.writeFile(previousPath, bytes, { flag: 'wx' }); previous = installed;
  }
  const runtime = { revision: 'yawn-v3', sampleFps: 60, durationMs: 4400, fingerprint,
    sourceClipSha256: hash(Buffer.from(JSON.stringify(sourceClip))),
    previousClipSha256: hash(Buffer.from(JSON.stringify(previous.clip))),
    previousRecord: 'reference/yawn-repair/contour-v2.json',
    sourceManifest: 'reference/motion-60hz/source-manifest-0.3.3.json',
    reason: 'Replace four face-composited keys with complete original poses, so cheek markings and edge fur move together. Protect the stable opaque interior during interpolation.',
    keys, normalization, sourceHashes, clip, frames };
  await fs.mkdir(path.dirname(candidatePath), { recursive: true });
  await fs.writeFile(candidatePath, json(runtime));
  await fs.writeFile(path.join(work, 'before-clip.json'), json(sourceClip));
  if (process.argv.includes('--install')) await install(runtime);
  return { prepared: true, installed: process.argv.includes('--install'), frameSlots: clip.frames.length, uniquePngs: frames.length, candidatePath };
}

async function install(runtime, options = {}) {
  // Validate staged bytes before changing the runtime reference. A rebuild
  // interrupted between keys, frames and record must never be installable.
  const { alphaStatistics } = require('./qa-yawn.cjs');
  for (const frame of runtime.frames) {
    assert(/^frames\/(?:fall_asleep-00\.png|yawn-v[23]\/[a-f0-9]{12}\/\d{5}\.png)$/.test(frame.file), 'Unexpected yawn output path.');
    const encoded = await fs.readFile(path.join(root, frame.file));
    assert.equal(hash(encoded), frame.sha256, 'Staged PNG changed: ' + frame.file);
    const image = await sharp(encoded).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
    assert.equal(image.info.width, 512); assert.equal(image.info.height, 512);
    const alpha = alphaStatistics(image.data);
    assert.equal(alpha.edgeMaximum, 0); assert.equal(alpha.faceBelow250, 0);
  }
  for (const [file, expected] of Object.entries(runtime.sourceHashes))
    assert.equal(hash(await fs.readFile(path.join(root, file))), expected, 'Key changed since interpolation: ' + file);
  const file = path.join(root, 'manifest.json');
  // Reload at installation time so concurrent work on unrelated actions survives.
  const before = await fs.readFile(file), manifest = applyYawnRepair(JSON.parse(before), runtime, options);
  const pending = file + '.yawn-pending';
  await fs.writeFile(pending, json(manifest));
  assert((await fs.readFile(file)).equals(before), 'Manifest changed concurrently; retry installation.');
  await fs.rename(pending, file);
  await fs.writeFile(recordPath, json(runtime));
}

if (require.main === module) {
  const args = process.argv.slice(2);
  assert(args.every(arg => ['--keys-only', '--install', '--install-only', '--install-selected', '--full-pose'].includes(arg)),
    'Usage: node tools/build-yawn.cjs --install-selected | [--full-pose] [--keys-only|--install|--install-only]');
  assert(!args.includes('--install') || args.includes('--full-pose'), 'Use --install-selected for the accepted version; --full-pose --install explicitly selects the experimental full-pose rebuild.');
  const selectedInstall = args.includes('--install-selected') || (args.includes('--install-only') && !args.includes('--full-pose'));
  const run = selectedInstall ? fs.readFile(recordPath, 'utf8').then(JSON.parse).then(record => install(record)).then(() => ({ installed: true, selected: true }))
    : args.includes('--install-only') ? fs.readFile(candidatePath, 'utf8').then(JSON.parse).then(record => install(record)).then(() => ({ installed: true })) : build();
  run.then(value => console.log(JSON.stringify(value))).catch(error => { console.error(error); process.exitCode = 1; });
}
module.exports = { prepareKeys, opaqueInterior, install };
