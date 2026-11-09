'use strict';
// Read-only checks for the repaired 60 Hz yawn. No runtime or history is written.
// node tools/qa-yawn.cjs --verify
// node tools/qa-yawn.cjs --regression [--legacy]
// Verification follows runtime.revision. The selected v2 has an accepted,
// known grey/white cheek-boundary mismatch; it is not a whole-pose repair.
// --legacy alone runs the regression against the original, defective keys.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { isDeepStrictEqual } = require('node:util');
const sharp = require('sharp');
const { resampleClip } = require('./resample-motion-60hz.cjs');

const repo = path.resolve(__dirname, '..');
const assetRoot = path.join(repo, 'assets/strawberry-cat');
const runtimePath = path.join(repo, 'reference/yawn-repair/runtime.json');
const manifestPath = path.join(assetRoot, 'manifest.json');
const idleFile = 'frames/fall_asleep-00.png';
// These are the complete original drawings, before v5 nose registration and
// face compositing. Pinning their encoded hashes prevents a new collage from
// being relabelled as an original source. Whole-pose equality, not removal of
// dark cheek pixels, is the regression criterion.
const sourcePoses = [
  { id: 'half', source: 'frames/blink-01.png', sourceSHA256: '2bb99fa7d31ec47c7a2f4f3ab799162a8fabe7a4241ffbddd2bfb835299f5513', translation: { x: 0, y: 0 } },
  { id: 'low', source: 'frames/blink-02.png', sourceSHA256: 'bc4afa4bd39d5deb3cf3968c1341013f1042f2d5eb411908dc29e3a602cfabc0', translation: { x: 0, y: 0 } },
  { id: 'closed', source: 'frames/blink-03.png', sourceSHA256: '94ea75789d4bcb85d450543503dca954e7d6cb7660d6148c5e9f2cd3f09593a4', translation: { x: 0, y: 0 } },
  { id: 'start', source: 'sources/idle-v5/yawn-start-v1.png', sourceSHA256: '7b136ed3a33f43e7322518ac192c3c0e6296a2ce1129e76b8a987dd51c693845', translation: { x: 1, y: -1 } }
].map(pose => ({ ...pose, output: `keyframes-yawn-v3/yawn-${pose.id}.png`,
  resize: { width: 512, height: 512, fit: 'fill', kernel: 'lanczos3' }, transparentAlphaCutoff: 3, completePose: true }));
const v3Keys = [idleFile, ...sourcePoses.map(pose => pose.output),
  ...['middle', 'peak'].map(name => `keyframes-v7/yawn/yawn-${name}.png`)];
const v2Keys = [idleFile, ...['half', 'low', 'closed'].map(name => `keyframes-yawn-v2/yawn-${name}.png`),
  ...['start', 'middle', 'peak'].map(name => `keyframes-v7/yawn/yawn-${name}.png`)];
const v2ArchivePath = path.join(repo, 'reference/yawn-repair/contour-v2.json');
const v2KnownLimitation = 'User-selected yawn-v2 retains the known grey/white cheek-boundary mismatch. Archive identity and technical playback checks do not certify whole-pose continuity.';
const faceRegion = { left: 120, top: 315, right: 350, bottom: 400 };
const sha256 = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const parse = bytes => JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
// sourceClipSha256 uses compact JSON, independent of manifest indentation/CRLF.
const clipSha256 = clip => sha256(Buffer.from(JSON.stringify(clip)));

async function readPng(relative, require512 = true) {
  if (typeof relative !== 'string' || !relative || path.isAbsolute(relative) ||
      relative.includes('\\') || relative.split('/').some(part => part === '..' || !part))
    throw Error('Expected a contained asset path: ' + relative);
  const realRoot = await fs.realpath(assetRoot);
  const file = await fs.realpath(path.resolve(realRoot, relative));
  const tail = path.relative(realRoot, file);
  if (!tail || tail === '..' || tail.startsWith('..' + path.sep) || path.isAbsolute(tail))
    throw Error('Asset path escapes its root: ' + relative);
  const encoded = await fs.readFile(file);
  const metadata = await sharp(encoded).metadata();
  if (metadata.format !== 'png' || !metadata.hasAlpha || (require512 && (metadata.width !== 512 || metadata.height !== 512)))
    throw Error('Expected a transparent PNG' + (require512 ? ' at 512 x 512' : '') + ': ' + relative);
  const raw = await sharp(encoded).toColourspace('srgb').ensureAlpha().raw().toBuffer();
  return { encoded, raw };
}

async function normalizedSource(encoded, pose) {
  // Independently reconstruct the declared, whole-image transform. No target
  // key, idle image, facial mask or local repair enters this reconstruction.
  const data = await sharp(encoded).toColourspace('srgb').resize(512, 512, { fit: 'fill', kernel: 'lanczos3' })
    .ensureAlpha().raw().toBuffer();
  for (let i = 0; i < data.length; i += 4) if (data[i + 3] <= 3) data.fill(0, i, i + 4);
  const expanded = await sharp(data, { raw: { width: 512, height: 512, channels: 4 } })
    .extend({ left: 40, right: 40, top: 40, bottom: 40, background: '#00000000' }).png().toBuffer();
  return sharp(expanded).extract({ left: 40 - pose.translation.x, top: 40 - pose.translation.y, width: 512, height: 512 })
    .ensureAlpha().raw().toBuffer();
}

function pixelDifference(expected, actual) {
  if (expected.length !== actual.length) throw Error('Whole-pose comparison dimensions differ.');
  let changedPixels = 0, maximumChannelDifference = 0;
  for (let i = 0; i < expected.length; i += 4) {
    let changed = false;
    for (let c = 0; c < 4; c++) {
      const delta = Math.abs(expected[i + c] - actual[i + c]);
      if (delta) changed = true;
      maximumChannelDifference = Math.max(maximumChannelDifference, delta);
    }
    if (changed) changedPixels++;
  }
  return { changedPixels, maximumChannelDifference };
}

function alphaStatistics(raw) {
  let edgeMaximum = 0, transparentPixels = 0, visiblePixels = 0;
  for (let y = 0; y < 512; y++) for (let x = 0; x < 512; x++) {
    const alpha = raw[(y * 512 + x) * 4 + 3];
    if (alpha === 0) transparentPixels++;
    if (alpha >= 128) visiblePixels++;
    if (x === 0 || y === 0 || x === 511 || y === 511) edgeMaximum = Math.max(edgeMaximum, alpha);
  }
  let faceMinimum = 255, faceBelow250 = 0;
  for (let y = faceRegion.top; y <= faceRegion.bottom; y++)
    for (let x = faceRegion.left; x <= faceRegion.right; x++) {
      const alpha = raw[(y * 512 + x) * 4 + 3];
      faceMinimum = Math.min(faceMinimum, alpha);
      if (alpha < 250) faceBelow250++;
    }
  return { edgeMaximum, transparentPixels, visiblePixels, faceMinimum, faceBelow250 };
}

async function morphologySignal(check) {
  // Sharp/libvips morphology expands dark pixels: dilate shrinks the white
  // opaque-interior mask. This guards alpha correction only, not pose quality.
  const dot = Buffer.alloc(25 * 25);
  for (let y = 10; y < 15; y++) for (let x = 10; x < 15; x++) dot[y * 25 + x] = 255;
  const counts = {};
  for (const operation of ['dilate', 'erode']) {
    const raw = await sharp(dot, { raw: { width: 25, height: 25, channels: 1 } })
      [operation](3).toColourspace('b-w').raw().toBuffer();
    counts[operation] = raw.reduce((sum, value) => sum + (value === 255 ? 1 : 0), 0);
  }
  check('Morphology polarity matches the opaque-interior mask', counts.dilate === 0 && counts.erode === 121,
    { inputWhitePixels: 25, outputWhitePixels: counts });
}

async function verifyInteriorProtection(keys, check) {
  const { opaqueInterior } = require('./build-yawn.cjs');
  const mask = await opaqueInterior(keys);
  if (mask.length !== 512 * 512) throw Error('Unexpected opaque-interior mask dimensions.');
  const failures = [];
  for (const file of keys) {
    const { raw } = await readPng(file);
    let unsafePixels = 0;
    for (let p = 0; p < mask.length; p++) if (raw[p * 4 + 3] < 250 && mask[p] !== 0) unsafePixels++;
    if (unsafePixels) failures.push({ file, unsafePixels });
  }
  check('Opacity repair excludes every source silhouette', failures.length === 0, { failures });
  let faceMinimum = 255;
  for (let y = faceRegion.top; y <= faceRegion.bottom; y++)
    for (let x = faceRegion.left; x <= faceRegion.right; x++) faceMinimum = Math.min(faceMinimum, mask[y * 512 + x]);
  check('Opacity repair fully covers the stable inner face', faceMinimum === 255, { faceMinimum });
}

async function regression(legacy, check) {
  const results = [];
  for (const pose of sourcePoses) {
    const file = legacy ? `keyframes-v7/yawn/yawn-${pose.id}.png` : pose.output;
    const source = await readPng(pose.source, false);
    const sourceHash = sha256(source.encoded);
    check('Original full-source PNG is unchanged: ' + pose.id, sourceHash === pose.sourceSHA256,
      { file: pose.source, expected: pose.sourceSHA256, actual: sourceHash });
    const key = await readPng(file);
    const expected = await normalizedSource(source.encoded, pose);
    const result = { file, source: pose.source, translation: pose.translation,
      sourcePNGsha256: sourceHash, targetPNGsha256: sha256(key.encoded),
      expectedRGBAsha256: sha256(expected), actualRGBAsha256: sha256(key.raw),
      ...pixelDifference(expected, key.raw) };
    results.push(result);
    check('Entire pose matches its unspliced source: ' + pose.id, result.changedPixels === 0,
      { changedPixels: result.changedPixels, maximumChannelDifference: result.maximumChannelDifference });
  }
  return { results,
    criterion: 'All RGBA pixels must equal the complete original source after only whole-canvas resize, declared integer paw registration and alpha <= 3 cleanup.',
    note: 'This rejects local cheek, ear or fur composites. Visual review of the full interpolated gesture is still required; pixel identity does not prove appealing motion.' };
}

async function verifyV2Archive(runtime, check) {
  const archiveBytes = await fs.readFile(v2ArchivePath), archive = parse(archiveBytes);
  check('Accepted contour archive is yawn-v2', archive.revision === 'yawn-v2');
  for (const field of ['clip', 'keys', 'sourceHashes', 'frames'])
    check('Selected v2 matches its accepted archive: ' + field, isDeepStrictEqual(runtime[field], archive[field]));
  check('Selected v2 uses its original preparation keys', isDeepStrictEqual(runtime.keys, v2Keys));
  check('Accepted v2 archive stayed unchanged during verification', archiveBytes.equals(await fs.readFile(v2ArchivePath)));
  return { archive: 'reference/yawn-repair/contour-v2.json', knownLimitation: v2KnownLimitation };
}

function verifyTimeline(clip, check) {
  if (!clip || !Array.isArray(clip.frames) || !clip.frames.length) throw Error('The yawn clip is missing.');
  check('Yawn does not loop', clip.loop === false);
  check('Exactly 264 frame slots', clip.frames.length === 264, { actual: clip.frames.length });
  const durations = clip.frames.map(frame => frame.durationMs);
  const durationMs = durations.reduce((sum, value) => sum + value, 0);
  check('Rounded 60 Hz frame durations', durations.every((value, index) =>
    Number.isSafeInteger(value) && value === Math.round((index + 1) * 1000 / 60) - Math.round(index * 1000 / 60)));
  check('Exactly 4400 ms', durationMs === 4400, { actual: durationMs });
  check('Exact shared idle at both boundaries', clip.frames[0].file === idleFile && clip.frames.at(-1).file === idleFile);
  return { frameSlots: clip.frames.length, durationMs };
}

async function verifyProvenance(runtime, check) {
  const archived = parse(await fs.readFile(path.join(repo, 'reference/motion-60hz/source-manifest-0.3.3.json')));
  const sourceClip = resampleClip(archived.clips.yawn);
  check('Original 60 Hz yawn provenance', runtime.sourceClipSha256 === clipSha256(sourceClip),
    { expected: clipSha256(sourceClip), actual: runtime.sourceClipSha256 });
  if (runtime.revision === 'yawn-v3') {
    check('Recorded source poses match the v3 yawn recipe', isDeepStrictEqual(runtime.keys, v3Keys));
    const normalization = runtime.normalization;
    check('All four whole-pose normalization records are present', Array.isArray(normalization) &&
      isDeepStrictEqual(normalization.map(pose => pose.id).sort(), sourcePoses.map(pose => pose.id).sort()));
    for (const pose of sourcePoses) {
      const recorded = Array.isArray(normalization) ? normalization.find(item => item.id === pose.id) : undefined;
      const fieldsMatch = recorded && Object.keys(pose).every(field => isDeepStrictEqual(recorded[field], pose[field]));
      check('Original source and whole-canvas transform are recorded: ' + pose.id, fieldsMatch,
        { expected: pose, actual: recorded });
    }
  }
}

async function verifyPngInventory(runtime, keys, check) {
  const keyHashFailures = [];
  for (const file of keys) {
    const png = await readPng(file), actual = sha256(png.encoded), expected = runtime.sourceHashes?.[file];
    if (actual !== expected) keyHashFailures.push({ file, expected, actual });
  }
  check('Source-pose SHA-256 values match', keyHashFailures.length === 0, { failures: keyHashFailures });
  const files = [...new Set(runtime.clip.frames.map(frame => frame.file))].sort();
  const recordedFiles = runtime.frames.map(frame => frame.file).sort();
  check('Record lists each referenced PNG exactly once', isDeepStrictEqual(files, recordedFiles));
  const expectedHashes = new Map(runtime.frames.map(frame => [frame.file, frame.sha256]));
  const failures = [], hashFailures = [];
  let lowestFaceAlpha = 255;
  for (const file of files) {
    const png = await readPng(file), expected = expectedHashes.get(file), actual = sha256(png.encoded);
    if (typeof expected !== 'string' || !/^[a-f0-9]{64}$/.test(expected) || actual !== expected)
      hashFailures.push({ file, expected, actual });
    const alpha = alphaStatistics(png.raw);
    lowestFaceAlpha = Math.min(lowestFaceAlpha, alpha.faceMinimum);
    if (alpha.edgeMaximum !== 0 || alpha.transparentPixels === 0 || alpha.visiblePixels === 0 || alpha.faceBelow250 !== 0)
      failures.push({ file, ...alpha });
  }
  check('All PNG SHA-256 values match', hashFailures.length === 0, { failures: hashFailures });
  check('Transparent canvas edges and solid inner face', failures.length === 0,
    { faceRegion, minimumAcceptedFaceAlpha: 250, lowestFaceAlpha, failures });
  return { uniquePngs: files.length, lowestFaceAlpha };
}

async function verify(check, runtime, recordBytes, keys) {
  const manifestBytes = await fs.readFile(manifestPath), manifest = parse(manifestBytes);
  const clip = manifest.clips?.yawn;
  check('Runtime clip matches the selected repair record', isDeepStrictEqual(clip, runtime.clip));
  check('Canonical idle is unchanged', manifest.clips?.idle?.frames?.[0]?.file === idleFile);
  const timeline = verifyTimeline(clip, check);
  await verifyProvenance(runtime, check);
  const images = await verifyPngInventory(runtime, keys, check);
  // v2 acceptance is the user's archived selection, not a claim that the
  // current full-pose generator could reproduce its earlier cheek composite.
  if (runtime.revision === 'yawn-v3') await verifyInteriorProtection(keys, check);
  check('Manifest and repair record stayed unchanged during verification',
    manifestBytes.equals(await fs.readFile(manifestPath)) && recordBytes.equals(await fs.readFile(runtimePath)));
  return { ...timeline, ...images };
}

async function main(args = process.argv.slice(2)) {
  if (args.some(arg => !['--verify', '--regression', '--legacy'].includes(arg)) ||
      (args.includes('--verify') && args.includes('--regression')) ||
      (args.includes('--verify') && args.includes('--legacy')))
    throw Error('Usage: node tools/qa-yawn.cjs --verify | --regression [--legacy] | --legacy');
  const legacy = args.includes('--legacy');
  const mode = args.includes('--regression') || legacy ? 'regression' : 'verify';
  const checks = [], check = (name, passed, detail) => checks.push({ name, passed: Boolean(passed), ...(detail ? { detail } : {}) });
  if (legacy) {
    const completePoses = await regression(true, check);
    return { passed: checks.every(item => item.passed), mode, legacy, revision: 'historical-v7', completePoses, checks };
  }
  const recordBytes = await fs.readFile(runtimePath), runtime = parse(recordBytes);
  if (!['yawn-v2', 'yawn-v3'].includes(runtime.revision)) throw Error('Unsupported selected yawn revision: ' + runtime.revision);
  if (!runtime.clip || !Array.isArray(runtime.frames)) throw Error('runtime.json must contain clip and frames.');
  const isV2 = runtime.revision === 'yawn-v2', keys = isV2 ? v2Keys : v3Keys;
  const acceptedV2 = isV2 ? await verifyV2Archive(runtime, check) : undefined;
  if (!isV2) await morphologySignal(check);
  let playback;
  if (mode === 'verify') playback = await verify(check, runtime, recordBytes, keys);
  else if (isV2) {
    const timeline = verifyTimeline(runtime.clip, check);
    await verifyProvenance(runtime, check);
    playback = { ...timeline, ...await verifyPngInventory(runtime, keys, check) };
  }
  const completePoses = isV2 ? undefined : await regression(false, check);
  check('Selected repair record stayed unchanged throughout this check', recordBytes.equals(await fs.readFile(runtimePath)));
  return { passed: checks.every(item => item.passed), mode, legacy, revision: runtime.revision,
    ...(playback ? { playback } : {}), ...(acceptedV2 ? { acceptedV2 } : {}),
    ...(completePoses ? { completePoses } : {}), checks };
}

module.exports = { alphaStatistics, normalizedSource, pixelDifference, clipSha256, morphologySignal, main };
if (require.main === module) main().then(result => {
  console.log(JSON.stringify(result, null, 2));
  if (!result.passed) process.exitCode = 1;
}).catch(error => { console.error(error.message); process.exitCode = 1; });
