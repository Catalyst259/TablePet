// Independent, read-only QA for complete-body v7 idle gestures.
// node tools/qa-idle-v7.cjs [assetRoot] [reportRoot] [baselineRoot]
// Numerical checks detect defects; they cannot certify appealing artwork.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');

const repo = path.resolve(__dirname, '..');
const assetRoot = path.resolve(process.argv[2] || path.join(repo, 'assets/strawberry-cat'));
const reportRoot = path.resolve(process.argv[3] || path.join(repo, 'artifacts/idle-v7-qa'));
const baselineRoot = path.resolve(process.argv[4] || path.join(repo, 'reference/idle-v7/baseline'));
const originalNames = ['idle', 'blink', 'fall_asleep', 'sleep_loop', 'wake', 'rise', 'hold', 'settle'];
const fresh = ['yawn', 'wink'];
const expected = { yawn: { durationMs: 4400, slots: 1056 }, wink: { durationMs: 1400, slots: 336 } };
const required = ['blink', ...fresh];
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const round = n => Math.round(n * 10000) / 10000;
const total = clip => clip.frames.reduce((sum, frame) => sum + frame.durationMs, 0);
const xml = value => String(value).replace(/[<>&"]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', '"': '&quot;' }[c]));
const backgrounds = ['#fff8f3', '#26313f'];
const pawRegions = { left: { x: 102, y: 430, width: 108, height: 60 }, right: { x: 214, y: 430, width: 112, height: 60 } };
const motionRegions = {
  decoration: { x: 178, y: 112, width: 210, height: 157 },
  leftHeadEdge: { x: 75, y: 230, width: 40, height: 133 },
  rightHeadEdge: { x: 361, y: 235, width: 41, height: 128 },
  shoulders: { x: 114, y: 405, width: 226, height: 35 },
  tail: { x: 395, y: 342, width: 94, height: 141 }
};
const eyeRegions = { left: { x: 116, y: 278, width: 104, height: 110 }, right: { x: 250, y: 288, width: 112, height: 116 } };
const faceCrop = { left: 104, top: 273, width: 264, height: 145 };

function safeFile(file) {
  const resolved = path.resolve(assetRoot, file);
  if (!resolved.startsWith(assetRoot + path.sep)) throw Error('Asset path leaves root: ' + file);
  return resolved;
}
function fileAt(clip, timeMs) {
  timeMs = Math.max(0, Math.min(timeMs, total(clip) - 0.0001));
  let end = 0;
  for (const frame of clip.frames) { end += frame.durationMs; if (timeMs < end) return frame.file; }
  return clip.frames.at(-1).file;
}
function premultiply(raw) {
  const output = Buffer.from(raw);
  for (let i = 0; i < output.length; i += 4) for (let c = 0; c < 3; c++) output[i + c] = Math.round(output[i + c] * output[i + 3] / 255);
  return output;
}
function pixelStats(raw, width, height, box = { x: 0, y: 0, width, height }) {
  let left = width, top = height, right = -1, bottom = -1, transparentPixels = 0, maximumCanvasEdgeAlpha = 0;
  let area = 0, sumX = 0, sumY = 0;
  for (let y = box.y; y < box.y + box.height; y++) for (let x = box.x; x < box.x + box.width; x++) {
    const a = raw[(y * width + x) * 4 + 3];
    if (!a) transparentPixels++;
    if (!x || !y || x === width - 1 || y === height - 1) maximumCanvasEdgeAlpha = Math.max(maximumCanvasEdgeAlpha, a);
    if (a >= 64) { left = Math.min(left, x); top = Math.min(top, y); right = Math.max(right, x); bottom = Math.max(bottom, y); }
    area += a; sumX += x * a; sumY += y * a;
  }
  return { bounds: { left, top, right, bottom }, transparentPixels, maximumCanvasEdgeAlpha,
    alphaCentroid: { x: round(sumX / Math.max(1, area)), y: round(sumY / Math.max(1, area)) } };
}
function difference(first, second, width, box = { x: 0, y: 0, width, height: first.length / 4 / width }) {
  let sum = 0, count = 0, maximum = 0, changed = 0, alphaSum = 0;
  for (let y = box.y; y < box.y + box.height; y++) for (let x = box.x; x < box.x + box.width; x++) {
    const offset = (y * width + x) * 4;
    if (first[offset + 3] < 8 && second[offset + 3] < 8) continue;
    count++;
    let pixelMax = 0;
    for (let c = 0; c < 4; c++) { const d = Math.abs(first[offset + c] - second[offset + c]); sum += d; maximum = Math.max(maximum, d); pixelMax = Math.max(pixelMax, d); }
    alphaSum += Math.abs(first[offset + 3] - second[offset + 3]);
    if (pixelMax > 16) changed++;
  }
  return { mean: round(sum / Math.max(1, count) / 4), meanAlphaDifference: round(alphaSum / Math.max(1, count)),
    maximumChannelDifference: maximum, pixelsChangedOver16: changed, visiblePixels: count, fractionChangedOver16: round(changed / Math.max(1, count)) };
}
function irisPixels(raw, box) {
  let count = 0;
  for (let y = box.y; y < box.y + box.height; y++) for (let x = box.x; x < box.x + box.width; x++) {
    const i = (y * 512 + x) * 4, r = raw[i], g = raw[i + 1], b = raw[i + 2];
    if (raw[i + 3] >= 128 && b >= 120 && b - r >= 50 && g - r >= 35) count++;
  }
  return count;
}
function contourDelta(a, b) {
  return Math.max(...['left', 'top', 'right', 'bottom'].map(key => Math.abs(a.bounds[key] - b.bounds[key])));
}
function distribution(values) {
  const sorted = [...values].sort((a, b) => a - b);
  return { minimum: sorted[0] ?? 0, median: sorted[Math.floor(sorted.length / 2)] ?? 0,
    p95: sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * 0.95))] ?? 0, maximum: sorted.at(-1) ?? 0 };
}

// These approximate stroke statistics are intentionally diagnostic-only. The
// mask can include lashes or open-eye outlines; a human must inspect the crops.
function darkStrokeDiagnostic(raw, box, side) {
  const columns = [];
  for (let x = box.x; x < box.x + box.width; x++) {
    let count = 0, sumY = 0;
    for (let y = box.y; y < box.y + box.height; y++) {
      const i = (y * 512 + x) * 4, r = raw[i], g = raw[i + 1], b = raw[i + 2];
      if (raw[i + 3] >= 200 && Math.max(r, g, b) < 175 && r - g >= 5 && r >= b - 30) { count++; sumY += y; }
    }
    if (count) columns.push({ x, pixels: count, y: round(sumY / count) });
  }
  if (!columns.length) return { columns: [], note: 'No dark warm stroke isolated by diagnostic color mask.' };
  const inner = side === 'left' ? columns.slice(-Math.ceil(columns.length / 4)) : columns.slice(0, Math.ceil(columns.length / 4));
  const outer = side === 'left' ? columns.slice(0, Math.ceil(columns.length / 4)) : columns.slice(-Math.ceil(columns.length / 4));
  const average = (items, property) => round(items.reduce((sum, item) => sum + item[property], 0) / Math.max(1, items.length));
  return { innerMeanThickness: average(inner, 'pixels'), outerMeanThickness: average(outer, 'pixels'),
    innerMeanY: average(inner, 'y'), outerMeanY: average(outer, 'y'), columns,
    note: 'Not an anatomy test. Inspect thick nose-side start, thin ear-side tail, slightly raised outer tail, no extra lashes.' };
}

async function contactSheet(items, target, options = {}) {
  if (!items.length) return;
  const crop = options.crop, cellWidth = options.width || 240, cellHeight = crop ? Math.round(cellWidth * crop.height / crop.width) : cellWidth;
  const columns = Math.min(options.columns || 5, items.length), rows = Math.ceil(items.length / columns), header = 37, layers = [];
  for (let theme = 0; theme < backgrounds.length; theme++) for (let i = 0; i < items.length; i++) {
    const item = items[i], left = i % columns * cellWidth, top = (theme * rows + Math.floor(i / columns)) * (cellHeight + header);
    const background = backgrounds[theme], ink = theme ? '#fff' : '#44363c';
    let pipeline = sharp(safeFile(item.file));
    if (crop) pipeline = pipeline.extract(crop);
    layers.push({ input: await pipeline.resize(cellWidth, cellHeight).flatten({ background }).png().toBuffer(), left, top: top + header });
    layers.push({ input: Buffer.from(`<svg width="${cellWidth}" height="${header}"><rect width="100%" height="100%" fill="${background}"/><text x="8" y="16" font-size="12" fill="${ink}">${xml(item.label)}</text><text x="8" y="31" font-size="11" fill="${ink}">${xml(item.detail || '')}</text></svg>`), left, top });
  }
  await sharp({ create: { width: columns * cellWidth, height: backgrounds.length * rows * (cellHeight + header), channels: 4, background: backgrounds[0] } }).composite(layers).png().toFile(target);
}

async function main() {
  await fs.mkdir(reportRoot, { recursive: true });
  const manifest = JSON.parse(await fs.readFile(path.join(assetRoot, 'manifest.json'), 'utf8'));
  const baseline = JSON.parse(await fs.readFile(path.join(baselineRoot, 'baseline-before-v7.json'), 'utf8'));
  const frozenBytes = await fs.readFile(path.join(baselineRoot, 'manifest-before-v7.json'));
  const frozen = JSON.parse(frozenBytes);
  const checks = [], check = (name, passed, detail) => checks.push({ name, passed: !!passed, detail });
  check('Frozen manifest checksum matches the captured baseline', sha(frozenBytes) === baseline.manifestSHA256, { expected: baseline.manifestSHA256, actual: sha(frozenBytes) });
  check('Baseline protects the original eight clips and 1216 distinct PNGs', JSON.stringify(baseline.names) === JSON.stringify(originalNames)
    && baseline.frames.length === 1216 && new Set(baseline.frames.map(frame => frame.file)).size === 1216,
  { names: baseline.names, pngs: baseline.frames.length });
  const missing = required.filter(name => !manifest.clips[name]?.frames?.length);
  check('All three frozen idle actions exist', !missing.length, { required, missing });
  check('Current manifest uses the frozen 0.3.0 revision', manifest.motion?.revision === '0.3.0', { revision: manifest.motion?.revision });
  check('Removed happy action is absent from the current manifest', !Object.hasOwn(manifest.clips, 'happy')
    && !(manifest.idleActions || []).some(action => action.clip === 'happy')
    && !Object.hasOwn(manifest.motion?.idleExpressions?.clips || {}, 'happy'),
  { clipPresent: Object.hasOwn(manifest.clips, 'happy'), pool: manifest.idleActions, motionMetadataPresent: Object.hasOwn(manifest.motion?.idleExpressions?.clips || {}, 'happy') });
  const recipeRecord = await fs.readFile(path.join(assetRoot, 'keyframes-v7/recipe.json'), 'utf8').then(JSON.parse).catch(error => { if (error.code === 'ENOENT') return null; throw error; });
  const notFinal = fresh.filter(name => manifest.motion?.idleExpressions?.clips?.[name]?.revision !== 'v7' || !recipeRecord?.recipes?.[name]);
  check('Manifest and recipes contain all final v7 actions', !notFinal.length, { notFinal });
  if (missing.length || notFinal.length) {
    const pending = { checkedAt: new Date().toISOString(), passed: false, pending: 'The frozen yawn and wink clips and their complete recipes are required alongside the unchanged blink; no visual verdict was attempted.', checks };
    await fs.writeFile(path.join(reportRoot, 'report.json'), JSON.stringify(pending, null, 2) + '\n');
    console.log(JSON.stringify(pending, null, 2)); process.exitCode = 2; return;
  }
  check('Canvas, ground anchor and hit region remain unchanged', ['canvas', 'anchor', 'hitRegion'].every(key => JSON.stringify(manifest[key]) === JSON.stringify(frozen[key])),
    { canvas: manifest.canvas, anchor: manifest.anchor, hitRegion: manifest.hitRegion });
  const changedClips = originalNames.filter(name => JSON.stringify(manifest.clips[name]) !== JSON.stringify(baseline.clips[name]) || JSON.stringify(baseline.clips[name]) !== JSON.stringify(frozen.clips[name]));
  check('All original clip timelines remain byte-for-byte equivalent JSON values', !changedClips.length, { changedClips });
  const originals = new Map(baseline.frames.map(frame => [frame.file, frame.encodedSHA256]));
  const originalReferenced = new Set(originalNames.flatMap(name => baseline.clips[name].frames.map(frame => frame.file)));
  check('Baseline contains every original referenced PNG exactly once', originalReferenced.size === originals.size && [...originalReferenced].every(file => originals.has(file)), { originalReferenced: originalReferenced.size, capturedPngs: originals.size });
  const normalization = JSON.parse(await fs.readFile(path.join(assetRoot, 'keyframes-v7/normalization.json'), 'utf8'));
  const currentPoses = normalization.results.filter(key => fresh.includes(key.action));
  check('Current key poses use complete RGBA artwork with no facial cutout or pose warp', fresh.every(name => currentPoses.some(key => key.action === name))
    && currentPoses.every(key => key.completeRgbaPose === true && key.faceMaskUsed === false && key.poseWarpUsed === false),
  { method: normalization.method, poseCount: currentPoses.length, archivedCandidateCount: normalization.results.length - currentPoses.length });
  const restFile = manifest.clips.idle.frames[0].file, restRaw = await sharp(safeFile(restFile)).ensureAlpha().raw().toBuffer();
  const restHash = sha(restRaw), restPremultiplied = premultiply(restRaw);
  const restPaws = Object.fromEntries(Object.entries(pawRegions).map(([name, box]) => [name, pixelStats(restRaw, 512, 512, box)]));
  const allFiles = [...new Set([...originals.keys(), ...Object.values(manifest.clips).flatMap(clip => clip.frames.map(frame => frame.file))])];
  const newFiles = new Set(fresh.flatMap(name => manifest.clips[name].frames.map(frame => frame.file)));
  const viewedFiles = new Set(required.flatMap(name => manifest.clips[name].frames.map(frame => frame.file)));
  check('Runtime PNGs stay inside the published frames directory', allFiles.every(file => file.startsWith('frames/') && !file.includes('..') && !file.includes('\\')), { pngs: allFiles.length });
  check('All new action images use the v7 namespace or exact shared idle', [...newFiles].every(file => file === restFile || file.startsWith('frames/v7/')), { pngs: newFiles.size });
  const frames = new Map(), images240 = new Map(), changedOriginals = [], invalid = [];
  for (const file of allFiles) {
    const encoded = await fs.readFile(safeFile(file)), metadata = await sharp(encoded).metadata();
    const { data, info } = await sharp(encoded).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
    const stats = pixelStats(data, info.width, info.height);
    const frame = { file, encodedSHA256: sha(encoded), pixelSHA256: sha(data), width: info.width, height: info.height, channels: metadata.channels, ...stats };
    if (originals.has(file) && frame.encodedSHA256 !== originals.get(file)) changedOriginals.push(file);
    if (metadata.format !== 'png' || info.width !== 512 || info.height !== 512 || metadata.channels !== 4 || !metadata.hasAlpha || stats.transparentPixels === 0 || stats.bounds.right < 0 || stats.maximumCanvasEdgeAlpha !== 0) invalid.push(file);
    if (newFiles.has(file) && info.width === 512 && info.height === 512) {
      const premultiplied = premultiply(data);
      frame.paws = Object.fromEntries(Object.entries(pawRegions).map(([name, box]) => [name, { ...pixelStats(data, 512, 512, box), differenceFromIdle: difference(restPremultiplied, premultiplied, 512, box) }]));
      frame.motionRegions = Object.fromEntries(Object.entries(motionRegions).map(([name, box]) => [name, difference(restPremultiplied, premultiplied, 512, box)]));
      frame.eyes = Object.fromEntries(Object.entries(eyeRegions).map(([name, box]) => [name, { cyanIrisPixels: irisPixels(data, box), differenceFromIdle: difference(restPremultiplied, premultiplied, 512, box) }]));
    }
    frames.set(file, frame);
    if (viewedFiles.has(file)) images240.set(file, premultiply(await sharp(encoded).resize(240, 240).ensureAlpha().raw().toBuffer()));
  }
  check('All 1216 original PNG byte hashes remain unchanged', !changedOriginals.length && originals.size === 1216, { comparedPngs: originals.size, changedOriginals, method: 'SHA-256 of original encoded PNG bytes against the frozen pre-v7 snapshot.' });
  check('All referenced PNGs are 512px transparent RGBA with clear canvas edges', !invalid.length, { checkedPngs: allFiles.length, invalid });
  const pool = manifest.idleActions || [];
  check('Random pool contains each requested action once with positive weight', pool.length === required.length && required.every(name => pool.filter(item => item.clip === name && Number.isInteger(item.weight) && item.weight > 0).length === 1), pool);
  const clips = {};
  for (const name of required) {
    const clip = manifest.clips[name], files = [...new Set(clip.frames.map(frame => frame.file))], all = files.map(file => frames.get(file));
    const uniquePixels = new Set(all.map(frame => frame.pixelSHA256)).size;
    check(name + ': exact common idle at both action boundaries', !clip.loop && clip.frames[0].file === restFile && clip.frames.at(-1).file === restFile
      && frames.get(clip.frames[0].file).pixelSHA256 === restHash && frames.get(clip.frames.at(-1).file).pixelSHA256 === restHash,
    { first: clip.frames[0].file, last: clip.frames.at(-1).file });
    if (fresh.includes(name)) {
      check(name + ': required duration and 240 Hz frame slots', total(clip) === expected[name].durationMs && clip.frames.length === expected[name].slots
        && clip.frames.every(frame => Number.isInteger(frame.durationMs) && frame.durationMs >= 4 && frame.durationMs <= 5),
      { actualDurationMs: total(clip), actualSlots: clip.frames.length, expected: expected[name], nominalHz: round(1000 * clip.frames.length / total(clip)) });
      check(name + ': contains distinct intermediate poses', uniquePixels >= 32, { uniqueFiles: files.length, uniqueDecodedImages: uniquePixels });
      const pawSummary = Object.fromEntries(Object.keys(pawRegions).map(paw => [paw, {
        idleGroundY: restPaws[paw].bounds.bottom,
        groundRange: [Math.min(...all.map(frame => frame.paws[paw].bounds.bottom)), Math.max(...all.map(frame => frame.paws[paw].bounds.bottom))],
        maximumGroundOffset: Math.max(...all.map(frame => Math.abs(frame.paws[paw].bounds.bottom - restPaws[paw].bounds.bottom)))
      }]));
      check(name + ': front paws stay grounded within two source pixels', Object.values(pawSummary).every(paw => paw.maximumGroundOffset <= 2 && paw.groundRange[0] >= 475 && paw.groundRange[1] <= 479), { alphaThreshold: 64, pawRegions, paws: pawSummary });
      const motion = Object.fromEntries(Object.keys(motionRegions).map(region => [region, {
        maximumMeanDifference: Math.max(...all.map(frame => frame.motionRegions[region].mean)),
        maximumChangedPixelsOver16: Math.max(...all.map(frame => frame.motionRegions[region].pixelsChangedOver16)),
        maximumMeanAlphaDifference: Math.max(...all.map(frame => frame.motionRegions[region].meanAlphaDifference))
      }]));
      const responds = region => motion[region].maximumMeanDifference > 0.2 && motion[region].maximumChangedPixelsOver16 >= 8;
      check(name + ': measurable non-eye response in head, decoration and shoulders', responds('decoration') && (responds('leftHeadEdge') || responds('rightHeadEdge')) && responds('shoulders'),
        { regions: motionRegions, motion, note: 'Pixel changes distinguish this from face-only animation; visual review must distinguish coordinated motion from texture drift.' });
      const peakAtMs = manifest.motion?.idleExpressions?.clips?.[name]?.peakAtMs;
      check(name + ': recipe timing and recorded peak agree with manifest', recipeRecord.recipes[name].durationMs === total(clip) && recipeRecord.recipes[name].peakAtMs === peakAtMs && peakAtMs > 0 && peakAtMs < total(clip), { peakAtMs, recipe: recipeRecord.recipes[name] });
      if (Number.isFinite(peakAtMs) && peakAtMs >= 0 && peakAtMs <= total(clip)) {
        const peakFile = fileAt(clip, peakAtMs), peak = frames.get(peakFile), peakRaw = await sharp(safeFile(peakFile)).ensureAlpha().raw().toBuffer();
        const eyeDiagnostics = Object.fromEntries(Object.entries(eyeRegions).map(([eye, box]) => [eye, { ...peak.eyes[eye], stroke: darkStrokeDiagnostic(peakRaw, box, eye) }]));
        await fs.writeFile(path.join(reportRoot, name + '-peak-eyes.json'), JSON.stringify({ peakAtMs, peakFile, eyeDiagnostics }, null, 2) + '\n');
        await contactSheet([{ file: peakFile, label: name + ' peak · ' + peakAtMs + ' ms', detail: '512 px source artwork · verify eye anatomy and whole pose' }], path.join(reportRoot, name + '-peak-512.png'), { width: 512 });
        if (name === 'wink') {
          const restingRightIris = irisPixels(restRaw, eyeRegions.right);
          check('wink: closed eye has no iris fragment and companion eye responds while open', peak.eyes.left.cyanIrisPixels <= 8
            && peak.eyes.right.cyanIrisPixels >= restingRightIris * 0.25 && peak.eyes.right.differenceFromIdle.mean > 1,
          { peakFile, eyes: peak.eyes, restingRightIris, note: 'Visual review still must reject displacement, distorted eye shape or unnatural gaze.' });
          let closedMs = 0, longestClosedMs = 0;
          for (const frame of clip.frames) { closedMs = frames.get(frame.file).eyes.left.cyanIrisPixels <= 8 ? closedMs + frame.durationMs : 0; longestClosedMs = Math.max(longestClosedMs, closedMs); }
          check('wink: fully closed eye retains a comfortable hold', longestClosedMs >= 580 && longestClosedMs <= 1000, { longestClosedMs, expectedRangeMs: [580, 1000] });
        } else {
          check(name + ': both peak eyes close without residual cyan fragments', peak.eyes.left.cyanIrisPixels <= 8 && peak.eyes.right.cyanIrisPixels <= 8, { peakFile, eyes: peak.eyes });
        }
      }
    }
    const samples = [], pairs = [], sampleCount = Math.ceil(total(clip) * 120 / 1000);
    for (let index = 0; index <= sampleCount; index++) {
      const timeMs = Math.min(total(clip), index * 1000 / 120), file = fileAt(clip, timeMs);
      const sample = { index, timeMs: round(timeMs), file }; samples.push(sample);
      if (index) pairs.push({ from: samples[index - 1], to: sample, ...difference(images240.get(samples[index - 1].file), images240.get(file), 240) });
    }
    const changes = [], ordered = clip.frames.filter((frame, i) => !i || frame.file !== clip.frames[i - 1].file);
    for (let i = 1; i < ordered.length; i++) changes.push({ from: ordered[i - 1].file, to: ordered[i].file,
      ...difference(images240.get(ordered[i - 1].file), images240.get(ordered[i].file), 240),
      maximumContourBoundStep512: contourDelta(frames.get(ordered[i - 1].file), frames.get(ordered[i].file)) });
    const joins = changes.filter(pair => pair.from === restFile || pair.to === restFile);
    const interiorDistribution = distribution(changes.filter(pair => pair.from !== restFile && pair.to !== restFile).map(pair => pair.mean));
    const endpointLimit = Math.max(1, interiorDistribution.p95 * 3);
    check(name + ': no isolated first or last appearance jump', joins.length >= 2 && joins.every(pair => pair.mean <= endpointLimit), { endpointLimit: round(endpointLimit), interiorDistribution, joins });
    if (fresh.includes(name)) check(name + ': no abrupt visible contour-bound step', changes.every(pair => pair.maximumContourBoundStep512 <= 3), {
      allowedStepAt512: 3, alphaThreshold: 64, maximum: Math.max(0, ...changes.map(pair => pair.maximumContourBoundStep512)),
      suspiciousSteps: changes.filter(pair => pair.maximumContourBoundStep512 > 3), note: 'Contour bounds are only an alarm for jumps; inspect full contours for rubbery interpolation and flicker.' });
    const worst = [...pairs].sort((a, b) => b.mean - a.mean).slice(0, 8);
    clips[name] = { durationMs: total(clip), frameSlots: clip.frames.length, uniqueFiles: files.length, uniqueDecodedImages: uniquePixels,
      sampleHz: 120, displaySize: 240, adjacentDifference: distribution(pairs.map(pair => pair.mean)), joins, largestChanges: worst };
    await fs.writeFile(path.join(reportRoot, name + '-samples.json'), JSON.stringify({ samples120Hz: samples, pairs120Hz: pairs, allDistinctPoseSteps: changes }, null, 2) + '\n');
    const knots = recipeRecord.recipes[name]?.knots || [];
    const times = [...new Set([...Array.from({ length: 9 }, (_, i) => Math.round(total(clip) * i / 8)), ...knots.map(knot => knot[0])])].sort((a, b) => a - b);
    const timing = times.map(time => ({ file: fileAt(clip, time), label: name + ' · ' + time + ' ms', detail: '240 px full character · complete timeline' }));
    await contactSheet(timing, path.join(reportRoot, name + '-timing-contact.png'));
    if (fresh.includes(name)) await contactSheet(timing.map(item => ({ ...item, detail: 'Eye/muzzle crop · inspect contour, taper and transitions' })), path.join(reportRoot, name + '-eye-contact.png'), { crop: faceCrop, width: 528, columns: 3 });
    const transitions = [];
    for (let i = 1; i < knots.length; i++) {
      const [start, from] = knots[i - 1], [end, to] = knots[i];
      if (from === to) continue;
      const filename = name + '-transition-' + i + '.png';
      const items = Array.from({ length: 7 }, (_, sample) => { const time = Math.round(start + (end - start) * sample / 6); return { file: fileAt(clip, time), label: name + ' · ' + time + ' ms', detail: 'Movement segment ' + i + ' · ' + Math.round(sample * 100 / 6) + '%' }; });
      await contactSheet(items, path.join(reportRoot, filename), { columns: 7 });
      transitions.push({ file: filename, startMs: start, endMs: end, fromPhase: from, toPhase: to });
    }
    clips[name].transitionSheets = transitions;
    const joinItems = joins.flatMap((pair, i) => [{ file: pair.from, label: 'Idle join ' + (i + 1) + ' · before', detail: 'mean difference ' + pair.mean }, { file: pair.to, label: 'Idle join ' + (i + 1) + ' · after' }]);
    if (worst.length) joinItems.push({ file: worst[0].from.file, label: 'Largest 120 Hz change · before', detail: worst[0].from.timeMs + ' ms' }, { file: worst[0].to.file, label: 'Largest 120 Hz change · after', detail: worst[0].to.timeMs + ' ms' });
    await contactSheet(joinItems, path.join(reportRoot, name + '-joins-contact.png'));
  }
  const visualChecklist = [
    'Watch complete gestures at normal speed and slow speed on light and dark backgrounds at 240px.',
    'Follow the planted paws, moving head, shoulders and decorations; reject floating, contour jumps, texture boiling and rubbery shape changes.',
    'Wink: nose-side eye start is thick, ear-side tail tapers and ends slightly higher; no extra eyelashes or doubled lines.',
    'Wink: held closure, responsive companion eye and a restrained coordinated gesture; mouth changes only if they belong to the expression.',
    'Yawn: preserve the approved slow expression, close both eyes, inhale/lift through chest and head, then ease back to idle.',
    'Compare the first/last distinct poses with exact idle; inspect intermediate eye shapes and 512px peak crops.',
    'The measurements above cannot establish natural movement or artistic quality. Record independent visual review separately.'
  ];
  const report = { checkedAt: new Date().toISOString(), passed: checks.every(item => item.passed), assetRoot, baselineRoot,
    baselineManifestSHA256: baseline.manifestSHA256, originalPngsVerified: originals.size, checks, clips,
    visualReview: { status: 'required', numericalPassIsNotArtisticApproval: true, checklist: visualChecklist } };
  await fs.writeFile(path.join(reportRoot, 'report.json'), JSON.stringify(report, null, 2) + '\n');
  await fs.writeFile(path.join(reportRoot, 'frames.json'), JSON.stringify([...frames.values()], null, 2) + '\n');
  console.log(JSON.stringify({ passed: report.passed, checkedPngs: allFiles.length, originalPngsVerified: originals.size,
    checks: checks.length, failed: checks.filter(item => !item.passed), clips: Object.fromEntries(Object.entries(clips).map(([name, clip]) => [name,
      { durationMs: clip.durationMs, frameSlots: clip.frameSlots, uniqueDecodedImages: clip.uniqueDecodedImages, adjacentDifference120Hz: clip.adjacentDifference }])),
    visualReviewRequired: true, report: path.join(reportRoot, 'report.json') }, null, 2));
  if (!report.passed) process.exitCode = 2;
}
main().catch(error => { console.error(error); process.exitCode = 1; });
