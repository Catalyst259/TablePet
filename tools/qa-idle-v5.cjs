// Independent, read-only checks for generated idle expressions.
// node tools/qa-idle-v5.cjs [assetRoot] [reportRoot] [baselineGitRef]
// Metrics flag defects for review; they do not assert that an animation looks natural.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const sharp = require('sharp');
const exec = promisify(execFile);
const repo = path.resolve(__dirname, '..');
const assetRoot = path.resolve(process.argv[2] || path.join(repo, 'assets/strawberry-cat'));
const reportRoot = path.resolve(process.argv[3] || path.join(repo, 'artifacts/idle-v5-qa'));
// Keep the approved version fixed even after the new expressions are committed later.
const baselineRef = process.argv[4] || 'v0.2.2';
const required = ['blink', 'yawn', 'happy', 'wink'];
const fresh = required.filter(name => name !== 'blink');
const baseAssetPath = 'assets/strawberry-cat/';
const sha256 = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const gitBlob = bytes => crypto.createHash('sha1').update(Buffer.from('blob ' + bytes.length + '\0')).update(bytes).digest('hex');
const round = number => Math.round(number * 10000) / 10000;
const total = clip => clip.frames.reduce((sum, frame) => sum + frame.durationMs, 0);
const xml = value => String(value).replace(/[<>&"]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', '"': '&quot;' }[c]));
const regions = {
  paws: { x: 102, y: 432, width: 234, height: 45 },
  tail: { x: 402, y: 352, width: 76, height: 121 },
  decoration: { x: 180, y: 132, width: 202, height: 143 },
  leftInnerEar: { x: 98, y: 242, width: 35, height: 58 },
  rightInnerEar: { x: 358, y: 248, width: 28, height: 64 },
  noseBridge: { x: 212, y: 299, width: 40, height: 36 }
};
const eyeRegions = { left: { x: 116, y: 282, width: 100, height: 101 }, right: { x: 252, y: 297, width: 108, height: 106 } };
const muzzleRegion = { x: 218, y: 342, width: 32, height: 49 };

function fileAt(clip, time) {
  time = Math.max(0, Math.min(time, total(clip) - 0.0001));
  let end = 0;
  for (const frame of clip.frames) { end += frame.durationMs; if (time < end) return frame.file; }
  return clip.frames.at(-1).file;
}
function premultiply(raw) {
  const bytes = Buffer.from(raw);
  for (let i = 0; i < bytes.length; i += 4) for (let c = 0; c < 3; c++) bytes[i + c] = Math.round(bytes[i + c] * bytes[i + 3] / 255);
  return bytes;
}
function pixelStats(raw, width, height) {
  let left = width, top = height, right = -1, bottom = -1, transparent = 0, partial = 0, edgeAlpha = 0;
  let area = 0, sumX = 0, sumY = 0;
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    const a = raw[(y * width + x) * 4 + 3];
    if (a === 0) transparent++; else if (a < 255) partial++;
    if (x === 0 || y === 0 || x === width - 1 || y === height - 1) edgeAlpha = Math.max(edgeAlpha, a);
    if (a >= 128) { left = Math.min(left, x); top = Math.min(top, y); right = Math.max(right, x); bottom = Math.max(bottom, y); }
    area += a; sumX += x * a; sumY += y * a;
  }
  return { width, height, bounds: { left, top, right, bottom }, transparentPixels: transparent, partialAlphaPixels: partial,
    maximumCanvasEdgeAlpha: edgeAlpha, alphaCentroid: { x: round(sumX / area), y: round(sumY / area) } };
}
function difference(first, second, width, region = null) {
  const bounds = region || { x: 0, y: 0, width, height: first.length / 4 / width };
  let sum = 0, count = 0, maximum = 0, changed = 0;
  for (let y = bounds.y; y < bounds.y + bounds.height; y++) for (let x = bounds.x; x < bounds.x + bounds.width; x++) {
    const i = (y * width + x) * 4;
    if (first[i + 3] < 8 && second[i + 3] < 8) continue;
    count++;
    let pixelMaximum = 0;
    for (let c = 0; c < 4; c++) { const d = Math.abs(first[i + c] - second[i + c]); sum += d; maximum = Math.max(maximum, d); pixelMaximum = Math.max(pixelMaximum, d); }
    if (pixelMaximum > 16) changed++;
  }
  return { mean: round(sum / Math.max(1, count) / 4), maximumChannelDifference: maximum,
    pixelsChangedOver16: changed, visiblePixels: count, fractionChangedOver16: round(changed / Math.max(1, count)) };
}
function translation(first, second, box) {
  let best = { dx: 0, dy: 0, meanResidual: Infinity };
  for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
    let sum = 0, count = 0;
    for (let y = box.y; y < box.y + box.height; y += 2) for (let x = box.x; x < box.x + box.width; x += 2) {
      const a = (y * 512 + x) * 4, b = ((y + dy) * 512 + x + dx) * 4;
      if (first[a + 3] < 8 && second[b + 3] < 8) continue;
      count++;
      for (let c = 0; c < 4; c++) sum += Math.abs(first[a + c] - second[b + c]);
    }
    const cost = sum / Math.max(1, count) / 4;
    // Prefer zero displacement when flat regions have equal costs.
    if (cost < best.meanResidual || (cost === best.meanResidual && Math.abs(dx) + Math.abs(dy) < Math.abs(best.dx) + Math.abs(best.dy)))
      best = { dx, dy, meanResidual: cost };
  }
  return { ...best, meanResidual: round(best.meanResidual) };
}
function outsideDifference(first, second, face) {
  let changedPixels = 0, maximumChannelDifference = 0;
  for (let y = 0; y < 512; y++) for (let x = 0; x < 512; x++) {
    if (x >= face.left && x <= face.right && y >= face.top && y <= face.bottom) continue;
    const i = (y * 512 + x) * 4;
    let maximum = 0;
    for (let c = 0; c < 4; c++) maximum = Math.max(maximum, Math.abs(first[i + c] - second[i + c]));
    if (maximum) changedPixels++;
    maximumChannelDifference = Math.max(maximumChannelDifference, maximum);
  }
  return { changedPixels, maximumChannelDifference };
}
function cyanIrisPixels(raw, box) {
  let count = 0;
  for (let y = box.y; y < box.y + box.height; y++) for (let x = box.x; x < box.x + box.width; x++) {
    const i = (y * 512 + x) * 4, r = raw[i], g = raw[i + 1], b = raw[i + 2], a = raw[i + 3];
    if (a >= 128 && b >= 120 && b - r >= 50 && g - r >= 35) count++;
  }
  return count;
}
function openMouthPixels(raw, reference) {
  let count = 0;
  for (let y = 354; y < 417; y++) for (let x = 208; x < 256; x++) {
    const i = (y * 512 + x) * 4;
    // Count a new dark mouth cavity, not the original cat-mouth stroke or
    // 1-channel reconstruction noise. The approved idle contains 119 dark
    // stroke pixels already; an absolute dark-pixel count mislabeled 121.
    const darkening = (reference[i] - raw[i] + reference[i + 1] - raw[i + 1] + reference[i + 2] - raw[i + 2]) / 3;
    if (raw[i + 3] >= 200 && raw[i] < 150 && raw[i + 1] < 100 && raw[i + 2] < 105 && darkening >= 30) count++;
  }
  return count;
}
function safeFrame(file) {
  const resolved = path.resolve(assetRoot, file);
  if (!resolved.startsWith(assetRoot + path.sep)) throw new Error('Frame leaves the asset root: ' + file);
  return resolved;
}
async function git(args) {
  return (await exec('git', ['-C', repo, ...args], { windowsHide: true, maxBuffer: 20 * 1024 * 1024 })).stdout;
}
async function contactSheet(items, target, backgrounds = ['#f8f2ed', '#26313f'], cell = 240) {
  if (!items.length) return;
  const header = 36, layers = [];
  for (let row = 0; row < backgrounds.length; row++) for (let col = 0; col < items.length; col++) {
    const item = items[col], bg = backgrounds[row];
    layers.push({ input: await sharp(safeFrame(item.file)).resize(cell, cell).flatten({ background: bg }).png().toBuffer(), left: col * cell, top: row * (cell + header) + header });
    layers.push({ input: Buffer.from(`<svg width="${cell}" height="${header}"><rect width="100%" height="100%" fill="${bg}"/><text x="8" y="16" font-size="12" fill="${row ? '#fff' : '#44363c'}">${xml(item.label)}</text><text x="8" y="31" font-size="11" fill="${row ? '#ddd' : '#68565e'}">${xml(item.detail || '')}</text></svg>`), left: col * cell, top: row * (cell + header) });
  }
  await sharp({ create: { width: items.length * cell, height: backgrounds.length * (cell + header), channels: 4, background: backgrounds[0] } }).composite(layers).png().toFile(target);
}

async function main() {
  await fs.mkdir(reportRoot, { recursive: true });
  const manifest = JSON.parse(await fs.readFile(path.join(assetRoot, 'manifest.json'), 'utf8'));
  const baselineCommit = (await git(['rev-parse', '--verify', baselineRef + '^{commit}'])).trim();
  const baseline = JSON.parse(await git(['show', baselineCommit + ':' + baseAssetPath + 'manifest.json']));
  const checkList = [];
  const check = (name, passed, detail) => checkList.push({ name, passed: !!passed, detail });
  const missing = required.filter(name => !manifest.clips[name]?.frames?.length);
  check('All four requested idle actions exist', missing.length === 0, { required, missing });
  if (missing.length) {
    const pending = { passed: false, checkedAt: new Date().toISOString(), baselineCommit, checks: checkList, pending: 'Final expression PNGs/manifest have not all been supplied.' };
    await fs.writeFile(path.join(reportRoot, 'report.json'), JSON.stringify(pending, null, 2) + '\n');
    console.log(JSON.stringify(pending, null, 2)); process.exitCode = 2; return;
  }
  check('Canvas and ground anchor retained', JSON.stringify(manifest.canvas) === JSON.stringify(baseline.canvas) && JSON.stringify(manifest.anchor) === JSON.stringify(baseline.anchor), { canvas: manifest.canvas, anchor: manifest.anchor });
  const normalization = JSON.parse(await fs.readFile(path.join(assetRoot, 'keyframes-v5/normalization.json'), 'utf8'));
  const face = normalization.faceMaskBounds;
  check('Face-only compositing bounds leave body and ornaments protected', face && Object.values(face).every(Number.isInteger) && face.left >= 80 && face.right <= 390 && face.top >= 260 && face.bottom <= 431 && face.left < face.right && face.top < face.bottom, face);
  if (!face) throw new Error('Missing faceMaskBounds needed for independent spatial checks.');
  const recipeRecord = JSON.parse(await fs.readFile(path.join(assetRoot, 'keyframes-v5/recipe.json'), 'utf8'));
  const revisedExpressions = ['happy', 'wink'].filter(name => recipeRecord.recipes[name].keys.some(file => file.startsWith('keyframes-v6/')));
  let revisedFace = null;
  if (revisedExpressions.length) {
    const revisedNormalization = JSON.parse(await fs.readFile(path.join(assetRoot, 'keyframes-v6/normalization.json'), 'utf8'));
    revisedFace = revisedNormalization.faceMaskBounds;
    check('Revised expression compositing bounds protect the body', revisedFace && Object.values(revisedFace).every(Number.isInteger)
      && revisedFace.left >= 80 && revisedFace.right <= 390 && revisedFace.top >= 260 && revisedFace.bottom <= 431
      && revisedFace.left < revisedFace.right && revisedFace.top < revisedFace.bottom, revisedFace);
    if (!revisedFace) throw new Error('Missing revised faceMaskBounds needed for spatial checks.');
  }
  const originalNames = Object.keys(baseline.clips);
  const changedOriginalClips = originalNames.filter(name => JSON.stringify(manifest.clips[name]) !== JSON.stringify(baseline.clips[name]));
  check('All original clip timelines remain unchanged', originalNames.length === 8 && changedOriginalClips.length === 0, { comparedClips: originalNames, changedOriginalClips });
  const originals = new Set(originalNames.flatMap(name => baseline.clips[name].frames.map(frame => frame.file)));
  const oldTree = new Map((await git(['ls-tree', '-r', '--format=%(objectname)%x09%(path)', baselineCommit, '--', baseAssetPath + 'frames'])).trim().split(/\r?\n/).map(line => {
    const tab = line.indexOf('\t'); return [line.slice(tab + 1).slice(baseAssetPath.length), line.slice(0, tab)];
  }));
  const restFile = manifest.clips.idle.frames[0].file;
  const restRaw = await sharp(safeFrame(restFile)).ensureAlpha().raw().toBuffer();
  const restPremultiplied = premultiply(restRaw), restHash = sha256(restRaw), restStats = pixelStats(restRaw, 512, 512);
  const allFiles = [...new Set(Object.values(manifest.clips).flatMap(clip => clip.frames.map(frame => frame.file)))];
  check('Every runtime image is inside the published frames directory', allFiles.every(file => file.startsWith('frames/') && !file.includes('..') && !file.includes('\\')), { frameFiles: allFiles.length });
  const newFiles = new Set(fresh.flatMap(name => manifest.clips[name].frames.map(frame => frame.file)));
  const viewedFiles = new Set(required.flatMap(name => manifest.clips[name].frames.map(frame => frame.file)));
  const frames = new Map(), images240 = new Map(), changedOldPngs = [], invalidPngs = [];
  for (const file of allFiles) {
    const encoded = await fs.readFile(safeFrame(file));
    const metadata = await sharp(encoded).metadata();
    const { data, info } = await sharp(encoded).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
    const stats = pixelStats(data, info.width, info.height);
    const frame = { file, encodedSHA256: sha256(encoded), pixelSHA256: sha256(data), ...stats, hasAlpha: metadata.hasAlpha === true };
    if (originals.has(file) && gitBlob(encoded) !== oldTree.get(file)) changedOldPngs.push(file);
    if (info.width !== 512 || info.height !== 512 || !metadata.hasAlpha || stats.transparentPixels === 0 || stats.bounds.right < 0 || stats.maximumCanvasEdgeAlpha !== 0) invalidPngs.push(file);
    if (newFiles.has(file) && info.width === 512 && info.height === 512) {
      const premultiplied = premultiply(data);
      frame.outsideFace = outsideDifference(restRaw, data, face);
      if (revisedFace) frame.outsideRevisedFace = outsideDifference(restRaw, data, revisedFace);
      frame.cyanIrisPixels = Object.fromEntries(Object.entries(eyeRegions).map(([name, box]) => [name, cyanIrisPixels(data, box)]));
      frame.eyeDifferenceFromIdle = Object.fromEntries(Object.entries(eyeRegions).map(([name, box]) => [name, difference(restPremultiplied, premultiplied, 512, box)]));
      frame.muzzleDifferenceFromIdle = difference(restPremultiplied, premultiplied, 512, muzzleRegion);
      frame.newMouthCavityPixels = openMouthPixels(data, restRaw);
      frame.fixedRegions = Object.fromEntries(Object.entries(regions).map(([name, box]) => [name, {
        ...difference(restPremultiplied, premultiplied, 512, box), ...translation(restPremultiplied, premultiplied, box)
      }]));
    }
    frames.set(file, frame);
    if (viewedFiles.has(file)) images240.set(file, premultiply(await sharp(encoded).resize(240, 240).ensureAlpha().raw().toBuffer()));
  }
  check('Original PNG bytes equal the Git baseline', changedOldPngs.length === 0, { baselineCommit, comparedFiles: originals.size, changedOldPngs, method: 'SHA-1 of exact Git blob header and PNG bytes against Git tree object IDs.' });
  check('Every referenced PNG is valid 512px RGBA with clear canvas edges', invalidPngs.length === 0, { checkedFiles: allFiles.length, invalidPngs });
  const requested = manifest.idleActions || [];
  check('Random pool includes every action exactly once with positive weight', required.every(name => requested.filter(item => item.clip === name && Number.isInteger(item.weight) && item.weight > 0 && item.weight <= 1000).length === 1) && new Set(requested.map(item => item.clip)).size === requested.length, requested);
  const clips = {};
  for (const name of required) {
    const clip = manifest.clips[name], files = [...new Set(clip.frames.map(frame => frame.file))], first = frames.get(clip.frames[0].file), last = frames.get(clip.frames.at(-1).file);
    check(name + ': starts and ends on the exact common resting image', !clip.loop && clip.frames[0].file === restFile && clip.frames.at(-1).file === restFile && first.pixelSHA256 === restHash && last.pixelSHA256 === restHash, { first: clip.frames[0].file, last: clip.frames.at(-1).file });
    check(name + ': positive integer timings on the retained 240Hz timeline', clip.frames.every(frame => Number.isInteger(frame.durationMs) && frame.durationMs >= 1 && frame.durationMs <= 5) && total(clip) > 0, { durationMs: total(clip), frameSlots: clip.frames.length, nominalSlotsPerSecond: round(1000 * clip.frames.length / total(clip)) });
    const uniquePixels = new Set(files.map(file => frames.get(file).pixelSHA256)).size;
    check(name + ': contains actual intermediate poses', uniquePixels >= 8, { uniqueFiles: files.length, uniqueDecodedImages: uniquePixels });
    if (name !== 'blink') {
      const all = files.map(file => frames.get(file));
      const revised = revisedExpressions.includes(name), activeFace = revised ? revisedFace : face;
      check(name + ': grounded throughout', all.every(frame => Math.abs(frame.bounds.bottom - restStats.bounds.bottom) <= 1), { restBottom: restStats.bounds.bottom, bottomRange: [Math.min(...all.map(f => f.bounds.bottom)), Math.max(...all.map(f => f.bounds.bottom))] });
      const outside = frame => revised ? frame.outsideRevisedFace : frame.outsideFace;
      check(name + ': all RGBA pixels outside the face remain exact', all.every(frame => outside(frame)?.changedPixels === 0), { faceMaskBounds: activeFace, maximumChangedPixels: Math.max(...all.map(frame => outside(frame)?.changedPixels ?? 262144)) });
      if (revised) check(name + ': original nose and mouth remain exact throughout', all.every(frame => frame.muzzleDifferenceFromIdle.maximumChannelDifference === 0), {
        region: muzzleRegion, maximumChannelDifference: Math.max(...all.map(frame => frame.muzzleDifferenceFromIdle.maximumChannelDifference))
      });
      const regionSummary = Object.fromEntries(Object.keys(regions).map(region => [region, {
        maximumMeanDifference: Math.max(...all.map(f => f.fixedRegions[region].mean)),
        maximumIntegerDisplacement: Math.max(...all.map(f => Math.max(Math.abs(f.fixedRegions[region].dx), Math.abs(f.fixedRegions[region].dy)))),
        largestChangedFraction: Math.max(...all.map(f => f.fixedRegions[region].fractionChangedOver16))
      }]));
      check(name + ': paws, tail, decoration and facial landmarks remain stable', Object.values(regionSummary).every(s => s.maximumIntegerDisplacement <= 1 && s.maximumMeanDifference <= 0.75 && s.largestChangedFraction <= 0.01), { regions, summary: regionSummary });
      if (name === 'yawn') {
        const yawnRecipe = recipeRecord.recipes.yawn;
        const originalKnots = [[0, 0], [280, 3], [1120, 6], [1400, 6], [1950, 3], [2200, 0]];
        const originalKeys = ['frames/fall_asleep-00.png', 'keyframes-v5/shared/half-eyes.png', 'keyframes-v5/shared/low-eyes.png',
          'keyframes-v5/shared/closed-eyes.png', 'keyframes-v5/yawn/yawn-start-v1.png', 'keyframes-v5/yawn/yawn-middle-v1.png', 'keyframes-v5/yawn/yawn-peak-v1.png'];
        check('yawn: exact half-speed timing retains the original key poses', total(clip) === 4400 && yawnRecipe.durationMs === 4400
          && yawnRecipe.peakAtMs === 2480 && JSON.stringify(yawnRecipe.keys) === JSON.stringify(originalKeys)
          && JSON.stringify(yawnRecipe.knots) === JSON.stringify(originalKnots.map(([time, phase]) => [time * 2, phase])),
        { durationMs: total(clip), recipeDurationMs: yawnRecipe.durationMs, peakAtMs: yawnRecipe.peakAtMs, keys: yawnRecipe.keys, knots: yawnRecipe.knots });
        const sourceSnapshotPath = path.join(reportRoot, 'yawn-keys-before-v6.json');
        const sourceSnapshotText = await fs.readFile(sourceSnapshotPath, 'utf8').catch(error => { if (error.code === 'ENOENT') return null; throw error; });
        if (sourceSnapshotText !== null) {
          const sourceSnapshot = JSON.parse(sourceSnapshotText);
          const changedKeys = [];
          for (const key of sourceSnapshot.keys) {
            const png = await fs.readFile(safeFrame(key.file));
            const raw = await sharp(png).ensureAlpha().raw().toBuffer();
            if (sha256(png) !== key.encodedSHA256 || sha256(raw) !== key.pixelSHA256) changedKeys.push(key.file);
          }
          check('yawn: original source-key bytes and pixels are unchanged', sourceSnapshot.keys.length === 7 && changedKeys.length === 0,
            { sourceSnapshotPath, comparedKeys: sourceSnapshot.keys.length, changedKeys });
        }
        // The closed-mouth preparation keys have at most 34 newly dark pixels
        // from small stroke changes; the first generated open-mouth key has 175.
        const open = all.filter(frame => frame.newMouthCavityPixels > 64);
        const stareFrames = open.filter(frame => frame.cyanIrisPixels.left > 12 || frame.cyanIrisPixels.right > 12);
        check('yawn: eyes stay closed throughout the open-mouth gesture', open.length > 0 && stareFrames.length === 0, { openMouthFrames: open.length, method: 'New dark mouth-cavity area relative to idle; ignores the original 119-pixel mouth stroke and small reconstruction noise.', minimumNewCavityPixels: 65, stareFrames: stareFrames.map(frame => ({ file: frame.file, cyanIrisPixels: frame.cyanIrisPixels, newMouthCavityPixels: frame.newMouthCavityPixels })) });
      }
      const peakAtMs = manifest.motion?.idleExpressions?.clips?.[name]?.peakAtMs;
      const peakTimingValid = Number.isFinite(peakAtMs) && peakAtMs > 0 && peakAtMs < total(clip);
      check(name + ': peak timing is recorded for visual inspection', peakTimingValid, { peakAtMs });
      if (peakTimingValid) {
        const peakFile = fileAt(clip, peakAtMs), peak = frames.get(peakFile);
        const closedEyes = name === 'wink' ? ['left'] : name === 'happy' && revised ? [] : ['left', 'right'];
        if (closedEyes.length) check(name + ': closed peak eyelids have no residual cyan iris fragments', closedEyes.every(eye => peak.cyanIrisPixels[eye] <= 8), { peakAtMs, peakFile, expectedClosedEyes: closedEyes, cyanIrisPixels: peak.cyanIrisPixels, eyeRegions });
        if (revised && name === 'happy') {
          const restingIris = Object.fromEntries(Object.entries(eyeRegions).map(([eye, box]) => [eye, cyanIrisPixels(restRaw, box)]));
          check('happy: both eyes soften, with small eye slits allowed', ['left', 'right'].every(eye => peak.cyanIrisPixels[eye] < restingIris[eye] * 0.75 && peak.eyeDifferenceFromIdle[eye].mean > 1),
            { peakFile, cyanIrisPixels: peak.cyanIrisPixels, restingIris, eyeDifferenceFromIdle: peak.eyeDifferenceFromIdle,
              note: 'A narrow remaining iris is intentional; this check does not establish appealing eyelid anatomy.' });
        }
        if (revised && name === 'wink') {
          const restingRightIris = cyanIrisPixels(restRaw, eyeRegions.right);
          check('wink: companion eye visibly responds and stays open', peak.eyeDifferenceFromIdle.right.mean > 1
            && peak.cyanIrisPixels.right >= restingRightIris * 0.25,
          { peakFile, rightEyeDifference: peak.eyeDifferenceFromIdle.right, restingRightIris, peakRightIris: peak.cyanIrisPixels.right,
            note: 'Review visually that the change is a small relaxation, not eye displacement or recoloring.' });
          let longestClosedMs = 0, closedMs = 0;
          for (const frame of clip.frames) {
            if (frames.get(frame.file).cyanIrisPixels.left <= 8) closedMs += frame.durationMs;
            else closedMs = 0;
            longestClosedMs = Math.max(longestClosedMs, closedMs);
          }
          check('wink: closed eye holds comfortably for about 600 ms', longestClosedMs >= 580 && longestClosedMs <= 950,
            { longestClosedMs, requiredRangeMs: [580, 950], note: 'Allows the few nearly closed frames around the deliberate hold.' });
        }
        await contactSheet([{ file: peakFile, label: name + ' peak at source resolution', detail: peakAtMs + ' ms · check eyelids, inner ears and white nose bridge' }], path.join(reportRoot, name + '-peak-512.png'), ['#f8f2ed', '#26313f'], 512);
      }
    }
    const samples = [], pairs = [], sampleCount = Math.ceil(total(clip) * 120 / 1000);
    for (let index = 0; index <= sampleCount; index++) {
      const timeMs = Math.min(total(clip), index * 1000 / 120), file = fileAt(clip, timeMs);
      samples.push({ index, timeMs: round(timeMs), file });
      if (index) pairs.push({ from: samples[index - 1], to: samples[index], ...difference(images240.get(samples[index - 1].file), images240.get(file), 240) });
    }
    // Inspect unique-pose departures too; a 120 Hz screen can skip a bad 240 Hz frame.
    const orderedUnique = clip.frames.filter((f, i) => i === 0 || f.file !== clip.frames[i - 1].file);
    const restJoins = [];
    for (let i = 1; i < orderedUnique.length; i++) if (orderedUnique[i - 1].file === restFile || orderedUnique[i].file === restFile)
      restJoins.push({ from: orderedUnique[i - 1].file, to: orderedUnique[i].file, ...difference(images240.get(orderedUnique[i - 1].file), images240.get(orderedUnique[i].file), 240) });
    const interior = pairs.filter(pair => pair.from.file !== restFile && pair.to.file !== restFile);
    const interiorMax = Math.max(0, ...interior.map(p => p.mean)), endpointLimit = Math.max(1, interiorMax * 3);
    check(name + ': no isolated first/last appearance jump', restJoins.length >= 2 && restJoins.every(pair => pair.mean <= endpointLimit), { endpointLimit: round(endpointLimit), largestInteriorStep: interiorMax, restJoins });
    const sorted = pairs.map(p => p.mean).sort((a, b) => a - b), worst = [...pairs].sort((a, b) => b.mean - a.mean).slice(0, 8);
    clips[name] = { durationMs: total(clip), frameSlots: clip.frames.length, uniqueFiles: files.length, uniqueDecodedImages: uniquePixels, sampleHz: 120, displaySize: 240,
      adjacentDifference: { median: sorted[Math.floor(sorted.length / 2)], p95: sorted[Math.floor(sorted.length * 0.95)], maximum: sorted.at(-1) }, restJoins, largestChanges: worst };
    await fs.writeFile(path.join(reportRoot, name + '-120hz-samples.json'), JSON.stringify({ samples, pairs }, null, 2) + '\n');
    const timingItems = Array.from({ length: 9 }, (_, i) => { const time = total(clip) * i / 8; return { file: fileAt(clip, time), label: name + ' · ' + Math.round(time) + ' ms', detail: '240 px · 1× timeline' }; });
    await contactSheet(timingItems, path.join(reportRoot, name + '-timing-contact.png'));
    if (recipeRecord.recipes[name]) {
      const knots = recipeRecord.recipes[name].knots, transitionSheets = [];
      for (let i = 1; i < knots.length; i++) {
        const [start, from] = knots[i - 1], [end, to] = knots[i];
        if (from === to) continue;
        const filename = name + '-transition-' + i + '.png';
        const items = Array.from({ length: 5 }, (_, sample) => {
          const time = start + (end - start) * sample / 4;
          return { file: fileAt(clip, time), label: name + ' · ' + Math.round(time) + ' ms', detail: 'Movement span ' + i + ' · ' + sample * 25 + '%' };
        });
        await contactSheet(items, path.join(reportRoot, filename));
        transitionSheets.push({ file: filename, startMs: start, endMs: end, fromPhase: from, toPhase: to });
      }
      clips[name].transitionSheets = transitionSheets;
    }
    const joinItems = restJoins.flatMap((pair, i) => [{ file: pair.from, label: 'Rest join ' + (i + 1) + ' · before', detail: 'mean difference ' + pair.mean }, { file: pair.to, label: 'Rest join ' + (i + 1) + ' · after' }]);
    if (worst.length) joinItems.push({ file: worst[0].from.file, label: 'Largest 120 Hz step · before', detail: worst[0].from.timeMs + ' ms' }, { file: worst[0].to.file, label: 'Largest 120 Hz step · after', detail: worst[0].to.timeMs + ' ms · mean ' + worst[0].mean });
    await contactSheet(joinItems.slice(0, 10), path.join(reportRoot, name + '-joins-contact.png'));
  }
  const report = { checkedAt: new Date().toISOString(), passed: checkList.every(c => c.passed), assetRoot, baselineCommit, checks: checkList, clips,
    visualReviewRequired: 'Inspect normal-speed and slow previews at 240px on both backgrounds, and the timing/join sheets. Pixel continuity and stable landmarks cannot establish appealing expression, natural eyelids, mouth anatomy or good timing.' };
  await fs.writeFile(path.join(reportRoot, 'report.json'), JSON.stringify(report, null, 2) + '\n');
  await fs.writeFile(path.join(reportRoot, 'frames.json'), JSON.stringify([...frames.values()], null, 2) + '\n');
  const clipSummary = Object.fromEntries(Object.entries(clips).map(([name, clip]) => [name, { durationMs: clip.durationMs, frameSlots: clip.frameSlots, uniqueDecodedImages: clip.uniqueDecodedImages, adjacentDifference120Hz: clip.adjacentDifference }]));
  console.log(JSON.stringify({ passed: report.passed, checks: checkList.length, checkedPngs: allFiles.length, originalPngsVerified: originals.size, failed: checkList.filter(c => !c.passed), clips: clipSummary, report: path.join(reportRoot, 'report.json') }, null, 2));
  if (!report.passed) process.exitCode = 2;
}
main().catch(error => { console.error(error); process.exitCode = 1; });
