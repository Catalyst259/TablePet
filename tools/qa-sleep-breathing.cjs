// Read-only QA for the 0.2.2 sleep refinement. Images are decoded and arranged;
// no source image, manifest, application setting, or running pet is modified.
// node tools/qa-sleep-breathing.cjs [currentAssets] [0.2.1Assets] [reportDirectory]
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');
const repo = path.resolve(__dirname, '..');
const root = path.resolve(process.argv[2] || path.join(repo, 'assets/strawberry-cat'));
const baselineRoot = path.resolve(process.argv[3] || path.join(repo, 'dist/TablePet-0.2.1-win-x64/Assets/strawberry-cat'));
const reportRoot = path.resolve(process.argv[4] || path.join(repo, 'artifacts/sleep-v4-qa'));
const baselineManifest = path.join(repo, 'reference/motion-v3/manifest.json');
const sha = data => crypto.createHash('sha256').update(data).digest('hex');
const round = number => Math.round(number * 10000) / 10000;
const duration = clip => clip.frames.reduce((sum, frame) => sum + frame.durationMs, 0);
const rois = {
  nose: { x: 216, y: 381, width: 37, height: 38 },
  eyes: { x: 119, y: 373, width: 231, height: 42 },
  decoration: { x: 165, y: 180, width: 205, height: 145 },
  paws: { x: 104, y: 435, width: 232, height: 42 },
  tail: { x: 396, y: 343, width: 78, height: 103 }
};

function fileAt(clip, time) {
  const total = duration(clip);
  time = clip.loop ? ((time % total) + total) % total : Math.max(0, Math.min(time, total - 1));
  let end = 0;
  for (const frame of clip.frames) { end += frame.durationMs; if (time < end) return frame.file; }
  return clip.frames.at(-1).file;
}

function premultiply(input) {
  const data = Buffer.from(input);
  for (let i = 0; i < data.length; i += 4) for (let c = 0; c < 3; c++) data[i + c] = Math.round(data[i + c] * data[i + 3] / 255);
  return data;
}

function pixelStats(data, width, height) {
  let left = width, top = height, right = -1, bottom = -1, area = 0, sumX = 0, sumY = 0;
  let transparent = 0, partial = 0, edgeAlpha = 0;
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    const a = data[(y * width + x) * 4 + 3];
    if (!a) transparent++; else if (a < 255) partial++;
    if (!x || !y || x === width - 1 || y === height - 1) edgeAlpha = Math.max(edgeAlpha, a);
    if (a >= 128) { left = Math.min(left, x); top = Math.min(top, y); right = Math.max(right, x); bottom = Math.max(bottom, y); }
    area += a / 255; sumX += x * a / 255; sumY += y * a / 255;
  }
  return { width, height, bounds: { left, top, right, bottom }, transparentPixels: transparent, partialAlphaPixels: partial,
    maximumCanvasEdgeAlpha: edgeAlpha, alphaArea: round(area), alphaCentroid: { x: round(sumX / area), y: round(sumY / area) } };
}

// A small integer translation fit describes landmark motion. It is not a
// naturalness score; shape/colour changes remain visible in the residual cost.
function translation(first, second, box, minimumY, maximumY) {
  let best = { dx: 0, dy: 0, meanResidual: Infinity };
  for (let dy = minimumY; dy <= maximumY; dy++) for (let dx = -2; dx <= 2; dx++) {
    let sum = 0, pixels = 0;
    for (let y = box.y; y < box.y + box.height; y += 2) for (let x = box.x; x < box.x + box.width; x += 2) {
      const i = (y * 512 + x) * 4, j = ((y + dy) * 512 + x + dx) * 4;
      if (first[i + 3] < 32 && second[j + 3] < 32) continue;
      for (let channel = 0; channel < 4; channel++) sum += Math.abs(first[i + channel] - second[j + channel]);
      pixels++;
    }
    const meanResidual = sum / Math.max(1, pixels) / 4;
    if (meanResidual < best.meanResidual) best = { dx, dy, meanResidual };
  }
  return { ...best, meanResidual: round(best.meanResidual) };
}

function difference(a, b) {
  let sum = 0, pixels = 0, maximumChannelDifference = 0;
  for (let i = 0; i < a.length; i += 4) {
    if (a[i + 3] < 8 && b[i + 3] < 8) continue;
    pixels++;
    for (let channel = 0; channel < 4; channel++) {
      const d = Math.abs(a[i + channel] - b[i + channel]);
      sum += d; maximumChannelDifference = Math.max(maximumChannelDifference, d);
    }
  }
  return { meanPremultipliedDifference: round(sum / Math.max(1, pixels) / 4), maximumChannelDifference };
}

async function decoded(filename) {
  const png = await fs.readFile(filename);
  const { data, info } = await sharp(png).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  return { encodedSHA256: sha(png), pixelSHA256: sha(data), raw: data, premultiplied: premultiply(data), stats: pixelStats(data, info.width, info.height) };
}

async function main() {
  const current = JSON.parse(await fs.readFile(path.join(root, 'manifest.json'), 'utf8'));
  const baseline = JSON.parse(await fs.readFile(baselineManifest, 'utf8'));
  const clip = current.clips.sleep_loop, oldClip = baseline.clips.sleep_loop;
  const checks = [];
  const check = (name, passed, detail) => checks.push({ name, passed: !!passed, detail });
  await fs.mkdir(reportRoot, { recursive: true });
  check('New sleep revision', current.version >= 4 && current.motion?.revision === '0.2.2', { version: current.version, revision: current.motion?.revision });
  check('Loop and timing', clip.loop && duration(clip) === 3600 && clip.frames.length === 864 && clip.frames.every(frame => Number.isInteger(frame.durationMs) && frame.durationMs > 0), { durationMs: duration(clip), frameSlots: clip.frames.length });
  check('Canvas and anchor preserved', JSON.stringify(current.canvas) === JSON.stringify(baseline.canvas) && JSON.stringify(current.anchor) === JSON.stringify(baseline.anchor), { canvas: current.canvas, anchor: current.anchor });
  const otherNames = Object.keys(baseline.clips).filter(name => name !== 'sleep_loop');
  check('Other seven clip timelines preserved', otherNames.length === 7 && otherNames.every(name => JSON.stringify(current.clips[name]) === JSON.stringify(baseline.clips[name])), { clips: otherNames });
  const otherFiles = [...new Set(otherNames.flatMap(name => baseline.clips[name].frames.map(frame => frame.file)))];
  const changedOtherFiles = [];
  for (const file of otherFiles) {
    const [before, after] = await Promise.all([fs.readFile(path.join(baselineRoot, file)), fs.readFile(path.join(root, file))]);
    if (sha(before) !== sha(after)) changedOtherFiles.push(file);
  }
  check('Other clip PNG bytes preserved', changedOtherFiles.length === 0, { comparedFiles: otherFiles.length, changedOtherFiles, baselineRoot });

  const restFile = clip.frames[0].file, rest = await decoded(path.join(root, restFile));
  const baselineRest = await decoded(path.join(baselineRoot, oldClip.frames[0].file));
  check('Original resting sprite preserved', rest.pixelSHA256 === baselineRest.pixelSHA256 && rest.encodedSHA256 === baselineRest.encodedSHA256, { restFile });
  const shared = [current.clips.fall_asleep.frames.at(-1).file, clip.frames[0].file, clip.frames.at(-1).file, current.clips.wake.frames[0].file];
  const sharedHashes = await Promise.all(shared.map(async file => (await decoded(path.join(root, file))).pixelSHA256));
  check('Sleep/wake and loop endpoints are pixel identical', sharedHashes.every(hash => hash === rest.pixelSHA256), { files: shared });

  const files = [...new Set(clip.frames.map(frame => frame.file))], frames = [], images240 = new Map();
  for (const file of files) {
    const filename = path.resolve(root, file);
    if (!filename.startsWith(root + path.sep)) throw Error('Frame escapes asset root: ' + file);
    const d = await decoded(filename), stats = d.stats;
    const paws = translation(rest.premultiplied, d.premultiplied, rois.paws, -2, 2);
    const tail = translation(rest.premultiplied, d.premultiplied, rois.tail, -2, 2);
    frames.push({ file, encodedSHA256: d.encodedSHA256, pixelSHA256: d.pixelSHA256, ...stats, paws, tail });
    images240.set(file, premultiply(await sharp(filename).resize(240, 240).ensureAlpha().raw().toBuffer()));
  }
  check('Every sleep PNG has valid unclipped transparent canvas', frames.every(frame => frame.width === 512 && frame.height === 512 && frame.transparentPixels > 0 && frame.alphaArea > 0 && frame.maximumCanvasEdgeAlpha === 0), { checkedFiles: frames.length });
  const bottoms = frames.map(frame => frame.bounds.bottom), groundline = rest.stats.bounds.bottom;
  check('Every groundline stays within one source pixel', bottoms.every(bottom => Math.abs(bottom - groundline) <= 1), { baseline: groundline, minimum: Math.min(...bottoms), maximum: Math.max(...bottoms) });
  const movementMaximum = region => Math.max(...frames.map(frame => Math.max(Math.abs(frame[region].dx), Math.abs(frame[region].dy))));
  check('Paws remain planted across every sleep PNG', movementMaximum('paws') <= 1, { maximumIntegerTemplateDisplacement: movementMaximum('paws') });
  check('Tail position stays within one source pixel', movementMaximum('tail') <= 1, { maximumIntegerTemplateDisplacement: movementMaximum('tail') });

  async function amplitude(assets, animation) {
    const base = await decoded(path.join(assets, animation.frames[0].file));
    const peakFile = fileAt(animation, duration(animation) / 2), peak = await decoded(path.join(assets, peakFile));
    const landmarks = Object.fromEntries(Object.entries(rois).map(([name, box]) => [name, translation(base.premultiplied, peak.premultiplied, box, -12, 3)]));
    return { peakFile, restBounds: base.stats.bounds, peakBounds: peak.stats.bounds, landmarks,
      eyeRiseSourcePixels: -landmarks.eyes.dy, decorationRiseSourcePixels: -landmarks.decoration.dy,
      eyeRiseAt240DIP: round(-landmarks.eyes.dy * 240 / 512), decorationRiseAt240DIP: round(-landmarks.decoration.dy * 240 / 512),
      alphaCentroidChange: { x: round(peak.stats.alphaCentroid.x - base.stats.alphaCentroid.x), y: round(peak.stats.alphaCentroid.y - base.stats.alphaCentroid.y) } };
  }
  const oldAmplitude = await amplitude(baselineRoot, oldClip), newAmplitude = await amplitude(root, clip);
  check('Peak rise matches the restrained 5–7 source-pixel target', newAmplitude.eyeRiseSourcePixels >= 5 && newAmplitude.eyeRiseSourcePixels <= 7 && newAmplitude.decorationRiseSourcePixels >= 5 && newAmplitude.decorationRiseSourcePixels <= 7, newAmplitude);
  check('Eyes and head decoration move together at the peak', Math.abs(newAmplitude.eyeRiseSourcePixels - newAmplitude.decorationRiseSourcePixels) <= 1 && Math.abs(newAmplitude.landmarks.eyes.dx - newAmplitude.landmarks.decoration.dx) <= 1, { eyes: newAmplitude.landmarks.eyes, decoration: newAmplitude.landmarks.decoration });

  const hz = 120, samplesPerCycle = Math.round(duration(clip) * hz / 1000), samples = [], pairs = [];
  for (let index = 0; index <= samplesPerCycle * 2; index++) {
    const timeMs = index * 1000 / hz, file = fileAt(clip, timeMs);
    samples.push({ index, timeMs: round(timeMs), file });
    if (index) pairs.push({ from: index - 1, to: index, timeMs: round(timeMs), cycleBoundary: index % samplesPerCycle === 0,
      ...difference(images240.get(samples[index - 1].file), images240.get(file)) });
  }
  check('Two complete 120Hz cycles repeat the same image sequence', samples.slice(0, samplesPerCycle).every((sample, index) => sample.file === samples[index + samplesPerCycle].file), { sampleHz: hz, samplesPerCycle, totalSamples: samples.length });
  const seams = pairs.filter(pair => pair.cycleBoundary);
  check('Both sampled repeat boundaries have zero pixel jump', seams.length === 2 && seams.every(pair => pair.maximumChannelDifference === 0), seams);
  const sortedDifferences = pairs.map(pair => pair.meanPremultipliedDifference).sort((a, b) => a - b);
  const pairSummary = { sampleHz: hz, displaySizeDIP: 240, pairCount: pairs.length,
    mean: round(sortedDifferences.reduce((a, b) => a + b, 0) / pairs.length),
    median: sortedDifferences[Math.floor(sortedDifferences.length / 2)], p95: sortedDifferences[Math.floor(sortedDifferences.length * .95)],
    maximum: sortedDifferences.at(-1), identicalPairs: pairs.filter(pair => pair.maximumChannelDifference === 0).length,
    largestChanges: [...pairs].sort((a, b) => b.meanPremultipliedDifference - a.meanPremultipliedDifference).slice(0, 12), seams };

  // An exact last/first image alone can hide a flash just after leaving rest.
  // At an eased endpoint, permit at most three times the largest interior step,
  // with a conservative absolute floor of 1/255 mean channel value.
  const restTransitions = pairs.filter(pair => samples[pair.from].file !== samples[pair.to].file &&
    (samples[pair.from].file === restFile || samples[pair.to].file === restFile));
  const interiorPairs = pairs.filter(pair => samples[pair.from].file !== restFile && samples[pair.to].file !== restFile);
  const interiorMaximum = Math.max(0, ...interiorPairs.map(pair => pair.meanPremultipliedDifference));
  const endpointDifferenceLimit = Math.max(1, interiorMaximum * 3);
  check('No isolated appearance jump when leaving or returning to rest', restTransitions.every(pair => pair.meanPremultipliedDifference <= endpointDifferenceLimit),
    { endpointDifferenceLimit: round(endpointDifferenceLimit), largestInteriorStep: interiorMaximum, transitions: restTransitions });

  const backgrounds = ['#f8f2ed', '#26313f'];
  const contact = [];
  for (let row = 0; row < 2; row++) for (let col = 0; col < 8; col++) {
    const time = duration(clip) * col / 8, file = fileAt(clip, time), background = backgrounds[row];
    contact.push({ input: await sharp(path.join(root, file)).resize(240, 240).flatten({ background }).png().toBuffer(), left: col * 240, top: row * 264 + 24 });
    contact.push({ input: Buffer.from(`<svg width="240" height="24"><rect width="240" height="24" fill="${background}"/><text x="8" y="17" font-size="13" fill="${row ? 'white' : '#413437'}">${time} ms</text></svg>`), left: col * 240, top: row * 264 });
  }
  await sharp({ create: { width: 1920, height: 528, channels: 4, background: backgrounds[0] } }).composite(contact).png().toFile(path.join(reportRoot, 'cycle-light-dark-contact.png'));
  const comparisons = [{ name: 'Rest', file: path.join(root, restFile) }, { name: 'Old peak', file: path.join(baselineRoot, oldAmplitude.peakFile) }, { name: 'New peak', file: path.join(root, newAmplitude.peakFile) }], peakContact = [];
  for (let row = 0; row < 2; row++) for (let col = 0; col < comparisons.length; col++) {
    const item = comparisons[col], background = backgrounds[row];
    peakContact.push({ input: await sharp(item.file).resize(240, 240).flatten({ background }).png().toBuffer(), left: col * 240, top: row * 264 + 24 });
    peakContact.push({ input: Buffer.from(`<svg width="240" height="24"><rect width="240" height="24" fill="${background}"/><text x="8" y="17" font-size="13" fill="${row ? 'white' : '#413437'}">${item.name}</text></svg>`), left: col * 240, top: row * 264 });
  }
  await sharp({ create: { width: 720, height: 528, channels: 4, background: backgrounds[0] } }).composite(peakContact).png().toFile(path.join(reportRoot, 'amplitude-light-dark-contact.png'));
  if (restTransitions.length) {
    const transition = restTransitions[0], endpointContact = [];
    const transitionFiles = [samples[transition.from].file, samples[transition.to].file];
    for (let row = 0; row < 2; row++) for (let col = 0; col < 2; col++) endpointContact.push({
      input: await sharp(path.join(root, transitionFiles[col])).flatten({ background: backgrounds[row] }).png().toBuffer(), left: col * 512, top: row * 512 });
    await sharp({ create: { width: 1024, height: 1024, channels: 4, background: backgrounds[0] } }).composite(endpointContact).png().toFile(path.join(reportRoot, 'rest-transition-light-dark-contact.png'));
  }

  const result = { checkedAt: new Date().toISOString(), passed: checks.every(item => item.passed), root, baselineRoot, baselineManifest,
    checks, sleepUniqueFiles: files.length, oldAmplitude, newAmplitude, adjacentDifference120Hz: pairSummary,
    visualReviewRequired: 'Pixel/landmark checks cannot establish naturalness. Review the light/dark contact sheets and loop preview, particularly fur outlines, tail details, decoration rigidity and wake interruption.' };
  await fs.writeFile(path.join(reportRoot, 'report.json'), JSON.stringify(result, null, 2) + '\n');
  await fs.writeFile(path.join(reportRoot, 'frames.json'), JSON.stringify(frames, null, 2) + '\n');
  await fs.writeFile(path.join(reportRoot, 'sampled-two-cycles.json'), JSON.stringify({ samples, pairs }, null, 2) + '\n');
  console.log(JSON.stringify(result, null, 2));
  if (!result.passed) process.exitCode = 2;
}
main().catch(error => { console.error(error); process.exitCode = 1; });
