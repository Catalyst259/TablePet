// Register complete model-drawn poses by their grounded front paws. Sharp only
// resizes/translates existing RGBA artwork; it never draws or warps expressions.
// node tools/normalize-idle-v7.cjs [selection.json] | --verify-baseline
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');

const repo = path.resolve(__dirname, '..');
const root = path.join(repo, 'assets/strawberry-cat');
const work = path.join(repo, 'artifacts/idle-v7');
const baselineRoot = path.join(repo, 'reference/idle-v7/baseline');
const idleFile = 'frames/fall_asleep-00.png';
const selectionPath = path.join(root, 'sources/idle-v7/selection.json');
const protectedNames = ['idle', 'blink', 'fall_asleep', 'sleep_loop', 'wake', 'rise', 'hold', 'settle'];
const canvas = { width: 512, height: 512, channels: 4 };
const pawRegions = [{ x: 102, y: 436, width: 108, height: 43 }, { x: 214, y: 436, width: 112, height: 43 }];
const sha = data => crypto.createHash('sha256').update(data).digest('hex');
const xml = value => String(value).replace(/[<>&"]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', '"': '&quot;' }[c]));

async function verifyBaseline(manifest) {
  const baseline = JSON.parse(await fs.readFile(path.join(baselineRoot, 'baseline-before-v7.json'), 'utf8'));
  const frozen = await fs.readFile(path.join(baselineRoot, 'manifest-before-v7.json'));
  if (sha(frozen) !== baseline.manifestSHA256) throw Error('Frozen v7 manifest checksum changed.');
  const current = manifest || JSON.parse(await fs.readFile(path.join(root, 'manifest.json'), 'utf8'));
  if (JSON.stringify(baseline.names) !== JSON.stringify(protectedNames)) throw Error('The v7 baseline must protect the original eight clips.');
  for (const name of protectedNames) if (JSON.stringify(current.clips[name]) !== JSON.stringify(baseline.clips[name]))
    throw Error('Protected clip changed: ' + name);
  for (const frame of baseline.frames) if (sha(await fs.readFile(path.join(root, frame.file))) !== frame.encodedSHA256)
    throw Error('Protected PNG changed: ' + frame.file);
  return { protectedClips: protectedNames, protectedPngs: baseline.frames.length, manifestSHA256: baseline.manifestSHA256 };
}

async function loadSelection(input = selectionPath) {
  const config = typeof input === 'string' ? JSON.parse(await fs.readFile(path.resolve(input), 'utf8')) : input;
  if (!config || !Array.isArray(config.keys) || !config.keys.length) throw Error('selection.json needs a nonempty keys array.');
  const ids = new Set();
  for (const key of config.keys) {
    if (!/^[a-z][a-z0-9-]*$/.test(key.id || '') || key.id === 'idle' || ids.has(key.id)) throw Error('Invalid or duplicate key id: ' + key.id);
    if (!['yawn', 'wink'].includes(key.action) || typeof key.source !== 'string') throw Error('Frozen 0.3.0 keys must belong to yawn or wink and name an existing source.');
    ids.add(key.id);
  }
  if (Object.keys(config.clips || {}).some(name => !['yawn', 'wink'].includes(name)))
    throw Error('Frozen 0.3.0 selection may contain only yawn and wink recipes. Historical candidates must remain outside the production selection.');
  return config;
}

function visibleBounds(raw) {
  let left = 512, top = 512, right = -1, bottom = -1, maximumEdgeAlpha = 0, transparentPixels = 0;
  for (let y = 0; y < 512; y++) for (let x = 0; x < 512; x++) {
    const a = raw[(y * 512 + x) * 4 + 3];
    if (a === 0) transparentPixels++;
    if (x === 0 || y === 0 || x === 511 || y === 511) maximumEdgeAlpha = Math.max(maximumEdgeAlpha, a);
    if (a < 64) continue;
    left = Math.min(left, x); top = Math.min(top, y); right = Math.max(right, x); bottom = Math.max(bottom, y);
  }
  return { left, top, right, bottom, maximumEdgeAlpha, transparentPixels };
}

function cleanLowAlphaNoise(raw) {
  // Match motion-interpolate.cjs: alpha <= 3 becomes transparent. Generated
  // cutouts can contain isolated alpha=1 background specks far below the paws.
  // This removes only near-transparent noise, never a visible silhouette.
  let removedPixels = 0, maximumRemovedAlpha = 0;
  for (let i = 0; i < raw.length; i += 4) {
    const alpha = raw[i + 3];
    if (alpha > 3) continue;
    if (alpha > 0) { removedPixels++; maximumRemovedAlpha = Math.max(maximumRemovedAlpha, alpha); }
    raw.fill(0, i, i + 4);
  }
  return { alphaThreshold: 3, removedNonzeroAlphaPixels: removedPixels, maximumRemovedAlpha,
    method: 'Same alpha <= 3 transparency cutoff as existing dual-matte RIFE pipeline; no visible contour cropping.' };
}

function pawResidual(base, source, dx, dy) {
  let sum = 0, samples = 0;
  for (const box of pawRegions) for (let y = box.y; y < box.y + box.height; y += 2) for (let x = box.x; x < box.x + box.width; x += 2) {
    const sx = x - dx, sy = y - dy;
    const a = (y * 512 + x) * 4, b = (sy * 512 + sx) * 4;
    const alphaA = base[a + 3], alphaB = sx < 0 || sx >= 512 || sy < 0 || sy >= 512 ? 0 : source[b + 3];
    sum += Math.abs(alphaA - alphaB);
    for (let c = 0; c < 3; c++) sum += Math.abs(base[a + c] * alphaA / 255 - (alphaB ? source[b + c] * alphaB / 255 : 0));
    samples += 4;
  }
  return sum / samples;
}

function findPawTranslation(base, source, radius = 20) {
  let best = { dx: 0, dy: 0, residual: Infinity };
  function consider(dx, dy) {
    if (Math.abs(dx) > radius || Math.abs(dy) > radius) return;
    const residual = pawResidual(base, source, dx, dy);
    if (residual < best.residual - 1e-9 || (Math.abs(residual - best.residual) < 1e-9 && Math.abs(dx) + Math.abs(dy) < Math.abs(best.dx) + Math.abs(best.dy)))
      best = { dx, dy, residual };
  }
  for (let dy = -radius; dy <= radius; dy += 2) for (let dx = -radius; dx <= radius; dx += 2) consider(dx, dy);
  const coarse = { ...best };
  for (let dy = coarse.dy - 2; dy <= coarse.dy + 2; dy++) for (let dx = coarse.dx - 2; dx <= coarse.dx + 2; dx++) consider(dx, dy);
  return best;
}

async function readSource(spec) {
  const file = path.isAbsolute(spec.source) ? spec.source : path.join(root, spec.source);
  const encoded = await fs.readFile(file), meta = await sharp(encoded).metadata();
  if (!meta.hasAlpha) throw Error('Transparent RGBA source required: ' + file);
  let pipeline = sharp(encoded).toColourspace('srgb').ensureAlpha();
  if (spec.crop) pipeline = pipeline.extract(spec.crop);
  const square = await pipeline.resize(512, 512, { fit: 'contain', background: '#00000000', kernel: 'lanczos3' }).png().toBuffer();
  const scale = spec.scale ?? 1;
  if (!Number.isFinite(scale) || scale < 0.85 || scale > 1.15) throw Error('Uniform registration scale must be between 0.85 and 1.15.');
  const size = Math.round(512 * scale);
  let resized = sharp(square);
  if (size !== 512) {
    const scaled = await resized.resize(size, size, { kernel: 'lanczos3' }).png().toBuffer();
    const gap = Math.abs(size - 512), start = Math.floor(gap / 2);
    resized = size > 512 ? sharp(scaled).extract({ left: start, top: start, width: 512, height: 512 }) :
      sharp(scaled).extend({ left: start, right: gap - start, top: start, bottom: gap - start, background: '#00000000' });
  }
  const data = await resized.ensureAlpha().raw().toBuffer();
  const transparentNoiseCleanup = cleanLowAlphaNoise(data);
  const bounds = visibleBounds(data);
  if (bounds.transparentPixels < 512 * 512 * 0.15 || bounds.maximumEdgeAlpha > 0) throw Error('Source background/crop needs review before registration: ' + spec.id);
  return { data, file, sourceSHA256: sha(encoded), sourceSize: { width: meta.width, height: meta.height }, scale, bounds, transparentNoiseCleanup };
}

async function registerPose(spec, base) {
  const source = await readSource(spec), proposed = findPawTranslation(base, source.data);
  const translation = spec.translation || { x: proposed.dx, y: proposed.dy };
  if (![translation.x, translation.y].every(Number.isInteger) || Math.abs(translation.x) > 32 || Math.abs(translation.y) > 32)
    throw Error('Paw registration requires integer translation within 32 pixels: ' + spec.id);
  const expanded = await sharp(source.data, { raw: canvas }).extend({ left: 40, right: 40, top: 40, bottom: 40, background: '#00000000' }).png().toBuffer();
  const data = await sharp(expanded).extract({ left: 40 - translation.x, top: 40 - translation.y, width: 512, height: 512 }).raw().toBuffer();
  const bounds = visibleBounds(data), residual = pawResidual(base, data, 0, 0);
  if (bounds.maximumEdgeAlpha > 0) throw Error('Registered pose would touch the canvas edge: ' + spec.id);
  if (residual > (spec.maximumPawResidual ?? 35)) throw Error('Front-paw registration residual is too high; review source ' + spec.id + ': ' + residual.toFixed(3));
  const output = 'keyframes-v7/' + spec.action + '/' + spec.id + '.png';
  await fs.mkdir(path.dirname(path.join(root, output)), { recursive: true });
  await sharp(data, { raw: canvas }).png().toFile(path.join(root, output));
  return { id: spec.id, action: spec.action, label: spec.label || spec.id, source: spec.source, sourceSHA256: source.sourceSHA256,
    sourceSize: source.sourceSize, crop: spec.crop || null, output, outputSHA256: sha(await fs.readFile(path.join(root, output))), bounds,
    registration: { basis: 'grounded front paws only; never nose/head', regions: pawRegions, scale: source.scale,
      proposedTranslation: proposed, appliedTranslation: translation, pawResidual: residual, manualTranslation: Boolean(spec.translation) },
    transparentNoiseCleanup: source.transparentNoiseCleanup,
    completeRgbaPose: true, faceMaskUsed: false, poseWarpUsed: false };
}

async function makeContact(results) {
  const entries = [{ output: idleFile, label: 'Approved idle' }, ...results];
  const cell = 256, rowHeight = 282, columns = Math.min(4, entries.length), rows = Math.ceil(entries.length / columns), layers = [];
  for (let theme = 0; theme < 2; theme++) for (let i = 0; i < entries.length; i++) {
    const entry = entries[i], left = i % columns * cell, top = (theme * rows + Math.floor(i / columns)) * rowHeight;
    const background = theme ? '#26313f' : '#fff8f3', ink = theme ? '#fff' : '#443943';
    layers.push({ input: await sharp(path.join(root, entry.output)).resize(cell, cell).flatten({ background }).png().toBuffer(), left, top: top + 26 });
    layers.push({ input: Buffer.from(`<svg width="${cell}" height="26"><rect width="100%" height="100%" fill="${background}"/><text x="8" y="19" font-size="14" fill="${ink}">${xml(entry.label)}</text></svg>`), left, top });
  }
  const file = path.join(root, 'keyframes-v7/candidate-contact.png');
  await sharp({ create: { width: columns * cell, height: rows * rowHeight * 2, channels: 4, background: '#fff8f3' } }).composite(layers).png().toFile(file);
  return file;
}

async function normalize(input = selectionPath, options = {}) {
  const baseline = await verifyBaseline(), config = await loadSelection(input);
  const only = options.only ? new Set(options.only) : null;
  const specs = config.keys.filter(key => !only || only.has(key.action));
  if (!specs.length) throw Error('No keys selected for normalization.');
  const base = await sharp(path.join(root, idleFile)).ensureAlpha().raw().toBuffer();
  const results = [];
  await fs.mkdir(path.join(root, 'keyframes-v7'), { recursive: true });
  await fs.mkdir(work, { recursive: true });
  for (const spec of specs) results.push(await registerPose(spec, base));
  const contact = await makeContact(results);
  const report = { createdAt: new Date().toISOString(), revision: '0.3.0', base: idleFile, baseline,
    method: 'Complete model-drawn RGBA poses, Sharp canvas normalization and integer registration using front-paw pixels only. No facial matte, nose alignment, drawn eye lines or pose deformation.',
    requiresVisualReview: true, contact, results };
  await fs.writeFile(path.join(root, 'keyframes-v7/normalization.json'), JSON.stringify(report, null, 2) + '\n');
  await fs.writeFile(path.join(work, 'registration-report.json'), JSON.stringify(report, null, 2) + '\n');
  return report;
}

module.exports = { normalize, loadSelection, verifyBaseline, registerPose, findPawTranslation, pawResidual, visibleBounds, cleanLowAlphaNoise,
  root, repo, work, idleFile, selectionPath, protectedNames, pawRegions };
if (require.main === module) (async () => {
  if (process.argv.includes('--verify-baseline')) return console.log(JSON.stringify(await verifyBaseline(), null, 2));
  const report = await normalize(process.argv[2] || selectionPath);
  console.log(JSON.stringify({ candidates: report.results.length, contact: report.contact, wholeRgbaPoses: true }, null, 2));
})().catch(error => { console.error(error); process.exitCode = 1; });
