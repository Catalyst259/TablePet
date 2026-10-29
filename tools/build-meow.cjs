// Complete RGBA poses -> paw-registered keys -> dual-matte RIFE with authored head motion.
// Default is a staged candidate only. --install promotes an already built candidate.
// node tools/build-meow.cjs --selection reference/meow-v3-selection.json [--normalize-only|--install]
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');
const { interpolate } = require('./motion-interpolate.cjs');
const { correctWinkEndpoint } = require('./wink-endpoint.cjs');
const { makePhaseSampler, timingSamples } = require('./build-wink-candidate.cjs');
const { findPawTranslation, pawResidual, cleanLowAlphaNoise, visibleBounds } = require('./normalize-idle-v7.cjs');
const repo = path.resolve(__dirname, '..');
const production = path.join(repo, 'assets/strawberry-cat');
const manifestPath = path.join(production, 'manifest.json');
const canvas = { width: 512, height: 512, channels: 4 };
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const json = value => JSON.stringify(value, null, 2) + '\n';
const duration = clip => clip.frames.reduce((sum, frame) => sum + frame.durationMs, 0);
const defaults = {
  order: ['idle', 'prepare', 'peak', 'recovery', 'idle'],
  knots: [[0, 0], [180, 1], [300, 2], [650, 2], [1050, 3], [1400, 4]],
  outAndBackKnots: [[0, 0], [180, 1], [300, 1.45], [650, 1.45], [1050, 1], [1400, 0]],
  bounceKnots: [[0, 0], [1400, 0]],
};

function inside(root, relative) {
  const result = path.resolve(root, relative);
  const tail = path.relative(root, result);
  if (!tail || tail.startsWith('..') || path.isAbsolute(tail)) throw Error('Path escapes candidate directory: ' + relative);
  return result;
}

async function snapshot(manifest, excludeMeow = false) {
  const clips = Object.fromEntries(Object.entries(manifest.clips).filter(([id]) => !excludeMeow || id !== 'meow'));
  const files = [...new Set(Object.values(clips).flatMap(clip => clip.frames.map(frame => frame.file)))].sort();
  const digest = crypto.createHash('sha256');
  for (const file of files) digest.update(file + '\0' + hash(await fs.readFile(path.join(production, file))) + '\n');
  return { timelinesSHA256: hash(JSON.stringify(clips)), pngAggregateSHA256: digest.digest('hex'), pngCount: files.length };
}

async function load(selection) {
  const selectionPath = path.resolve(repo, selection), bytes = await fs.readFile(selectionPath);
  const config = JSON.parse(bytes);
  if (!/^[a-z0-9][a-z0-9_-]{0,63}$/.test(config.name || '')) throw Error('A safe candidate name is required.');
  if (!Array.isArray(config.keys) || config.keys.length !== 3 || config.keys.some((key, index) => key.id !== defaults.order[index + 1] || typeof key.source !== 'string'))
    throw Error('Supply three complete RGBA poses in order: prepare, peak, recovery.');
  const revision = config.revision || 'meow-v2', playbackMode = config.playbackMode || 'forward';
  if (!/^meow-v[1-9][0-9]*$/.test(revision)) throw Error('revision must be meow-vN.');
  if (!['forward', 'out-and-back'].includes(playbackMode)) throw Error('playbackMode must be forward or out-and-back.');
  const outputRoot = path.join(repo, 'artifacts', revision, config.name);
  const factor = config.factor ?? 128, sampleFps = config.sampleFps ?? 240, endpointWindow = config.endpointWindow ?? 12;
  const frameSampling = config.frameSampling || 'nearest', blendWeightSteps = config.blendWeightSteps ?? 256;
  if (!['nearest', 'premultiplied-linear'].includes(frameSampling) || !Number.isInteger(blendWeightSteps) || blendWeightSteps < 64 || blendWeightSteps > 65536)
    throw Error('Invalid frameSampling or blendWeightSteps (integer 64–65536).');
  if (config.cacheReuse && (typeof config.cacheReuse.directory !== 'string' || !/^[0-9a-f]{64}$/.test(config.cacheReuse.pngAggregateSHA256 || '')))
    throw Error('cacheReuse needs a directory and expected PNG aggregate SHA-256.');
  if (!Number.isInteger(factor) || factor < 16 || factor > 256 || ![60, 120, 240].includes(sampleFps) || !Number.isInteger(endpointWindow) || endpointWindow < 2 || endpointWindow > factor / 2)
    throw Error('Invalid factor, sampleFps or endpointWindow.');
  const knots = config.knots || (playbackMode === 'out-and-back' ? defaults.outAndBackKnots : defaults.knots), bounceKnots = config.bounceKnots || defaults.bounceKnots;
  for (const curve of [knots, bounceKnots]) {
    if (!Array.isArray(curve) || curve.length < 2 || curve[0][0] !== 0 || curve.at(-1)[0] !== 1400 || curve.some((key, i) => !Array.isArray(key) || key.length !== 2 || !key.every(Number.isFinite) || !Number.isInteger(key[0]) || (i && key[0] <= curve[i - 1][0])))
      throw Error('Curves must span exactly 0–1400 ms with strictly increasing whole-millisecond times.');
  }
  if (playbackMode === 'out-and-back') {
    const peak = Math.max(...knots.map(key => key[1])), turn = knots.findIndex(key => key[1] === peak);
    if (knots[0][1] !== 0 || knots.at(-1)[1] !== 0 || peak <= 1 || knots.some((key, i) => key[1] < 0 || key[1] > 2 || (i && (i <= turn ? key[1] < knots[i - 1][1] : key[1] > knots[i - 1][1]))))
      throw Error('Out-and-back phases must rise once from idle (0), beyond prepare (1), up to at most peak (2), then return to idle (0).');
  } else if (knots[0][1] !== 0 || knots.at(-1)[1] !== 4 || knots.some((key, i) => key[1] < 0 || key[1] > 4 || (i && key[1] < knots[i - 1][1]))) throw Error('Forward pose phases must advance from idle (0) to idle (4).');
  if (bounceKnots[0][1] !== 0 || bounceKnots.at(-1)[1] !== 0 || bounceKnots.some(key => key[1] > 0 || key[1] < -12)) throw Error('The hop must start/end at zero and stay within 12 source pixels upward.');
  if (revision !== 'meow-v1' && bounceKnots.some(key => key[1] !== 0)) throw Error('Head-only meow revisions must keep every whole-frame displacement at zero. Only an explicit meow-v1 recipe may replay the legacy body hop.');
  return { config, revision, playbackMode, selectionPath, selectionSHA256: hash(bytes), outputRoot, factor, sampleFps, endpointWindow, knots, bounceKnots, frameSampling, blendWeightSteps };
}

// Match the approved browser's additive premultiplied-alpha interpolation. Using
// straight RGB or source-over here would create dark/coloured transparent edges.
function blendRgba(left, right, weight) {
  if (left.length !== right.length || left.length % 4 || !Number.isFinite(weight) || weight < 0 || weight > 1) throw Error('RGBA blend requires equal buffers and a weight in [0, 1].');
  if (weight === 0) return Buffer.from(left);
  if (weight === 1) return Buffer.from(right);
  const result = Buffer.alloc(left.length), inverse = 1 - weight;
  for (let i = 0; i < left.length; i += 4) {
    const a = inverse * left[i + 3], b = weight * right[i + 3], alpha = a + b;
    result[i + 3] = Math.round(alpha);
    if (!result[i + 3]) continue;
    for (let channel = 0; channel < 3; channel++) result[i + channel] = Math.round((a * left[i + channel] + b * right[i + channel]) / alpha);
  }
  return result;
}

function samplingAt(phase, factor, frameSampling, blendWeightSteps) {
  const position = phase * factor;
  const indexPosition = frameSampling === 'nearest' ? Math.round(position) : Math.round(position * blendWeightSteps) / blendWeightSteps;
  const lowerIndex = Math.floor(indexPosition), upperIndex = Math.ceil(indexPosition);
  return { indexPosition, lowerIndex, upperIndex, weight: indexPosition - lowerIndex, quantizationError: indexPosition - position };
}

async function verifyInference(directory, ordered, factor, fingerprint, expectedAggregate) {
  if (path.basename(directory) !== 'inference-' + fingerprint) throw Error('Cached inference fingerprint does not match the normalized source keys.');
  const report = JSON.parse(await fs.readFile(path.join(directory, 'interpolation-report.json'), 'utf8'));
  if (report.outputFrames !== (ordered.length - 1) * factor + 1 || report.factor !== factor || report.width !== 512 || report.height !== 512 ||
      report.keyframeChecks?.length !== ordered.length || !report.keyframeChecks.every(key => key.exactRgbMatch)) throw Error('RIFE keyframe mapping failed.');
  const digest = crypto.createHash('sha256');
  for (let index = 0; index < report.outputFrames; index++) {
    const file = String(index).padStart(5, '0') + '.png', bytes = await fs.readFile(path.join(directory, file)), sha = hash(bytes);
    if (index % factor === 0 && sha !== ordered[index / factor].sha256) throw Error('Cached inference source key differs: ' + index);
    digest.update(file + '\0' + sha + '\n');
  }
  const pngAggregateSHA256 = digest.digest('hex');
  if (expectedAggregate && pngAggregateSHA256 !== expectedAggregate) throw Error('Cached inference PNG hashes do not match the reviewed source bank.');
  return { report, pngAggregateSHA256 };
}

function selfTest() {
  const assert = require('node:assert/strict');
  const left = Buffer.from([255, 0, 0, 0, 255, 0, 0, 128, 23, 45, 67, 255]);
  const right = Buffer.from([0, 0, 255, 255, 0, 0, 255, 64, 23, 45, 67, 255]);
  assert.deepEqual(blendRgba(left, right, 0), left);
  assert.deepEqual(blendRgba(left, right, 1), right);
  const midpoint = blendRgba(left, right, .5);
  assert.deepEqual([...midpoint.subarray(0, 4)], [0, 0, 255, 128]);
  assert.deepEqual([...midpoint.subarray(4, 8)], [170, 0, 85, 96]);
  assert.deepEqual([...midpoint.subarray(8, 12)], [23, 45, 67, 255]);
  assert.deepEqual(blendRgba(Buffer.from([255, 0, 0, 0]), Buffer.from([0, 255, 0, 0]), .5), Buffer.alloc(4));
  for (const weight of [.125, .25, .75, .875]) assert.deepEqual(blendRgba(left, right, weight), blendRgba(right, left, 1 - weight));
  // Compositing after interpolation must equal the weighted composites on both
  // light and dark backgrounds, within unavoidable byte quantization.
  for (const background of [0, 255]) for (let i = 0; i < left.length; i += 4) for (let channel = 0; channel < 3; channel++) {
    const over = (data, offset) => data[offset + channel] * data[offset + 3] / 255 + background * (1 - data[offset + 3] / 255);
    assert.ok(Math.abs(over(midpoint, i) - (over(left, i) + over(right, i)) / 2) <= 1);
  }
  const knots = [[0, 0], [240, 1], [700, 1.35], [1160, 1], [1400, 0]];
  const phase = makePhaseSampler(knots, 'pchip', { zeroEndpointVelocity: true });
  for (let time = 0; time <= 1400; time++) {
    const sample = samplingAt(phase(time), 128, 'premultiplied-linear', 256);
    assert.deepEqual(sample.indexPosition, samplingAt(phase(1400 - time), 128, 'premultiplied-linear', 256).indexPosition);
    assert.ok(Math.abs(sample.quantizationError) <= 1 / 512 + 1e-10);
  }
  return { passed: true, checks: ['exact blend endpoints', 'transparent colour cannot bleed', 'correct semi-transparent alpha weighting', 'opaque constant preserved', 'fully transparent RGB cleared', 'symmetric interpolation', 'light/dark matte equivalence', 'forward/backward quantized positions identical', 'maximum subframe quantization error bounded'] };
}

async function normalizeKey(spec, base, outputRoot) {
  const source = path.resolve(repo, spec.source), encoded = await fs.readFile(source), metadata = await sharp(encoded).metadata();
  if (!metadata.hasAlpha) throw Error('Transparent RGBA source required: ' + source);
  let pipeline = sharp(encoded).toColourspace('srgb').ensureAlpha();
  if (spec.crop) pipeline = pipeline.extract(spec.crop);
  let square = await pipeline.resize(512, 512, { fit: 'contain', background: '#00000000', kernel: 'lanczos3' }).png().toBuffer();
  const scale = spec.scale ?? 1;
  if (!Number.isFinite(scale) || scale < 0.85 || scale > 1.15) throw Error('Registration scale must be between 0.85 and 1.15.');
  const size = Math.round(512 * scale);
  if (size !== 512) {
    const scaled = await sharp(square).resize(size, size).png().toBuffer();
    const gap = Math.abs(size - 512), start = Math.floor(gap / 2);
    square = await (size > 512 ? sharp(scaled).extract({ left: start, top: start, width: 512, height: 512 }) : sharp(scaled).extend({ left: start, right: gap - start, top: start, bottom: gap - start, background: '#00000000' })).png().toBuffer();
  }
  const raw = await sharp(square).raw().toBuffer(), noise = cleanLowAlphaNoise(raw);
  const proposed = findPawTranslation(base, raw), translation = spec.translation || { x: proposed.dx, y: proposed.dy };
  if (![translation.x, translation.y].every(Number.isInteger) || Math.abs(translation.x) > 32 || Math.abs(translation.y) > 32) throw Error('Registration translation must be integer ±32 pixels.');
  const expanded = await sharp(raw, { raw: canvas }).extend({ left: 40, right: 40, top: 40, bottom: 40, background: '#00000000' }).png().toBuffer();
  const registered = await sharp(expanded).extract({ left: 40 - translation.x, top: 40 - translation.y, width: 512, height: 512 }).raw().toBuffer();
  const bounds = visibleBounds(registered), residual = pawResidual(base, registered, 0, 0);
  if (bounds.maximumEdgeAlpha || bounds.transparentPixels < 512 * 512 * .15 || bounds.right < bounds.left) throw Error('Pose is clipped, empty or lacks a transparent background: ' + spec.id);
  if (residual > (spec.maximumPawResidual ?? 35)) throw Error('Front-paw registration requires review for ' + spec.id + ': ' + residual.toFixed(3));
  const file = 'keyframes/' + spec.id + '.png';
  await sharp(registered, { raw: canvas }).png().toFile(path.join(outputRoot, file));
  return { id: spec.id, source, sourceSHA256: hash(encoded), sourceSize: { width: metadata.width, height: metadata.height }, file,
    sha256: hash(await fs.readFile(path.join(outputRoot, file))), scale, translation, proposedTranslation: proposed, pawResidual: residual,
    noise, bounds, completeRgbaPose: true, faceMaskUsed: false, poseWarpUsed: false };
}

async function contact(outputRoot, keys, filename = 'keyframes-contact.png') {
  const layers = [];
  for (let theme = 0; theme < 2; theme++) for (let i = 0; i < keys.length; i++) {
    const background = theme ? '#26313f' : '#fff8f3', ink = theme ? '#ffffff' : '#443943';
    const left = i * 256, top = theme * 282;
    layers.push({ input: await sharp(path.join(outputRoot, keys[i].file)).resize(256, 256).flatten({ background }).png().toBuffer(), left, top: top + 26 });
    layers.push({ input: Buffer.from(`<svg width="256" height="26"><rect width="100%" height="100%" fill="${background}"/><text x="8" y="19" font-size="14" fill="${ink}">${keys[i].id}</text></svg>`), left, top });
  }
  await sharp({ create: { width: keys.length * 256, height: 564, channels: 4, background: '#fff8f3' } }).composite(layers).png().toFile(path.join(outputRoot, filename));
}

function groundingMetrics(raw) {
  function bounds(left, top, right, bottom) {
    let minY = Infinity, maxY = -Infinity;
    for (let y = top; y < bottom; y++) for (let x = left; x < right; x++) if (raw[(y * 512 + x) * 4 + 3] >= 64) { minY = Math.min(minY, y); maxY = Math.max(maxY, y); }
    return { top: Number.isFinite(minY) ? minY : null, bottom: Number.isFinite(maxY) ? maxY : null };
  }
  return { leftPaw: bounds(102, 410, 210, 500), rightPaw: bounds(214, 410, 326, 500), headCrown: bounds(100, 0, 390, 300) };
}

// Subpixel translation of the entire authored frame. Padding keeps the transform
// away from the crop boundary; no facial parts are drawn, masked or warped.
async function translateWholePose(source, offsetY, destination) {
  if (Math.abs(offsetY) < .0001) { await fs.copyFile(source, destination); return; }
  const padded = await sharp(source).extend({ left: 16, top: 16, right: 16, bottom: 16, background: '#00000000' }).png().toBuffer();
  const shifted = await sharp(padded).affine([[1, 0], [0, 1]], { ody: offsetY, background: '#00000000', interpolator: sharp.interpolators.bicubic }).png().toBuffer();
  await sharp(shifted).extract({ left: 16, top: 16, width: 512, height: 512 }).png().toFile(destination);
}

async function qa(outputRoot, clip, idleSource) {
  if (clip.loop || duration(clip) !== 1400 || clip.frames.some(frame => !Number.isInteger(frame.durationMs) || frame.durationMs <= 0)) throw Error('Meow must be a nonlooping 1400 ms clip with positive integer frame durations.');
  const files = [...new Set(clip.frames.map(frame => frame.file))];
  let minimumTransparentPixels = 512 * 512, totalPngBytes = 0;
  const geometryRanges = { leftPawBottom: [Infinity, -Infinity], rightPawBottom: [Infinity, -Infinity], headCrownTop: [Infinity, -Infinity] };
  for (const file of files) {
    const full = inside(outputRoot, file), meta = await sharp(full).metadata();
    if (meta.width !== 512 || meta.height !== 512 || !meta.hasAlpha) throw Error('Invalid frame canvas/alpha: ' + file);
    const raw = await sharp(full).ensureAlpha().raw().toBuffer(), bounds = visibleBounds(raw), geometry = groundingMetrics(raw);
    if (bounds.maximumEdgeAlpha || bounds.transparentPixels < 512 * 512 * .15 || bounds.right < bounds.left) throw Error('Frame clipping/background/empty image: ' + file);
    minimumTransparentPixels = Math.min(minimumTransparentPixels, bounds.transparentPixels);
    totalPngBytes += (await fs.stat(full)).size;
    for (const [name, value] of Object.entries({ leftPawBottom: geometry.leftPaw.bottom, rightPawBottom: geometry.rightPaw.bottom, headCrownTop: geometry.headCrown.top }))
      geometryRanges[name] = [Math.min(geometryRanges[name][0], value), Math.max(geometryRanges[name][1], value)];
  }
  const idle = await fs.readFile(idleSource);
  const firstExact = (await fs.readFile(inside(outputRoot, clip.frames[0].file))).equals(idle);
  const lastExact = (await fs.readFile(inside(outputRoot, clip.frames.at(-1).file))).equals(idle);
  if (!firstExact || !lastExact) throw Error('Both meow endpoints must byte-match the original idle PNG.');
  const imageHashes = {};
  for (const file of files) imageHashes[file] = hash(await fs.readFile(inside(outputRoot, file)));
  const runs = []; let timeMs = 0;
  for (const frame of clip.frames) {
    const sha256 = imageHashes[frame.file], previous = runs.at(-1);
    if (previous?.sha256 === sha256) previous.durationMs += frame.durationMs;
    else runs.push({ timeMs, durationMs: frame.durationMs, file: frame.file, sha256 });
    timeMs += frame.durationMs;
  }
  return { passed: true, durationMs: duration(clip), slots: clip.frames.length, uniquePngs: files.length, firstExact, lastExact,
    minimumTransparentPixels, totalPngBytes, geometryRanges, longestIdenticalImageRun: runs.reduce((a, b) => a.durationMs >= b.durationMs ? a : b),
    imageHashes, limitations: ['Technical checks do not certify closed-eye expression, head-only anatomy, appeal or motion smoothness; inspect the preview at normal and slow speeds.'] };
}

async function preview(outputRoot, clip) {
  const data = JSON.stringify(clip).replace(/</g, '\\u003c');
  const html = `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>双击喵 · 动画候选</title>
<style>*{box-sizing:border-box}body{margin:0;background:#f6f1ec;color:#463e44;font:15px/1.7 "Segoe UI","Microsoft YaHei",sans-serif}main{max-width:800px;margin:24px auto;padding:24px}.stage{display:grid;place-items:center;height:400px;border:1px solid #dfd4ce;border-radius:18px;background:#fffaf6}.dark .stage{background:#26313f}.checker .stage{background:repeating-conic-gradient(#dfd9d4 0% 25%,#fffaf6 0% 50%) 50%/20px 20px}canvas{width:240px;height:240px}.controls{display:flex;flex-wrap:wrap;gap:10px;margin:18px 0}button,select{font:inherit;padding:7px 12px;border:1px solid #d8cbd0;border-radius:8px;background:#fffaf8;color:inherit}input{width:100%;accent-color:#b17790}h1{font-size:24px}a{color:#96617b}</style>
<main><h1>双击喵 · 1400 ms</h1><p>完整猫咪表情与生成素材中头部运动的预览。气泡由桌面程序单独呈现。</p><div class="stage"><canvas width="512" height="512"></canvas></div><div class="controls"><button id="play">播放</button><button id="replay">重播</button><select id="speed" aria-label="速度"><option value="1">正常速度</option><option value=".5">0.5×</option><option value=".25">0.25×</option></select><select id="bg" aria-label="背景"><option value="light">浅色背景</option><option value="dark">深色背景</option><option value="checker">透明格</option></select><select id="size" aria-label="大小"><option value="240">240 px</option><option value="360">360 px</option><option value="512">512 px</option></select></div><input id="scrub" type="range" min="0" max="1400" step="1" value="0" aria-label="时间轴"><p id="status">正在载入图片…</p><a href="keyframes-contact.png">查看关键姿态</a> · <a href="sampled-contact.png">查看实际播放姿态</a></main>
<script>const clip=${data},cache=new Map(),canvas=document.querySelector('canvas'),ctx=canvas.getContext('2d'),play=document.getElementById('play'),scrub=document.getElementById('scrub'),status=document.getElementById('status');let time=0,last=null,paused=true,ready=false;function fileAt(t){let end=0;for(const f of clip.frames){end+=f.durationMs;if(t<end)return f.file}return clip.frames.at(-1).file}function draw(){ctx.clearRect(0,0,512,512);const image=cache.get(fileAt(Math.min(time,1400)));if(image)ctx.drawImage(image,0,0);scrub.value=Math.min(time,1400);status.textContent=Math.round(Math.min(time,1400))+' / 1400 ms';play.textContent=paused?'播放':'暂停'}play.onclick=()=>{if(ready){paused=!paused;last=null;draw()}};document.getElementById('replay').onclick=()=>{if(ready){time=0;paused=false;last=null;draw()}};scrub.oninput=()=>{time=+scrub.value;paused=true;last=null;draw()};document.getElementById('bg').onchange=e=>document.body.className=e.target.value;document.getElementById('size').onchange=e=>{canvas.style.width=e.target.value+'px';canvas.style.height=e.target.value+'px'};function tick(now){const delta=last===null?0:Math.min(100,now-last);last=now;if(ready&&!paused&&!document.hidden){time+=delta*Number(document.getElementById('speed').value);if(time>=2100)time=0;draw()}requestAnimationFrame(tick)}document.addEventListener('visibilitychange',()=>last=null);(async()=>{try{const files=[...new Set(clip.frames.map(f=>f.file))];let cursor=0;await Promise.all(Array.from({length:4},async()=>{while(cursor<files.length){const f=files[cursor++],image=new Image();image.src=f;await image.decode();cache.set(f,image)}}));ready=true;paused=false;draw();requestAnimationFrame(tick)}catch(error){status.textContent='载入失败：'+error.message}})();</script></html>`;
  await fs.writeFile(path.join(outputRoot, 'preview.html'), html);
}

async function build(selection, options = {}) {
  const settings = await load(selection);
  const { config, revision, playbackMode, outputRoot, factor, sampleFps, endpointWindow, knots, bounceKnots, frameSampling, blendWeightSteps } = settings;
  const original = await fs.readFile(manifestPath), active = JSON.parse(original);
  const before = { manifestSHA256: hash(original), ...await snapshot(active) };
  const idleSource = path.join(production, active.clips.idle.frames[0].file);
  await fs.mkdir(path.join(outputRoot, 'keyframes'), { recursive: true });
  await fs.mkdir(path.join(outputRoot, 'frames'), { recursive: true });
  try {
    await fs.copyFile(idleSource, path.join(outputRoot, 'keyframes/idle.png'));
    const base = await sharp(idleSource).ensureAlpha().raw().toBuffer();
    const keys = [{ id: 'idle', file: 'keyframes/idle.png', sha256: hash(await fs.readFile(idleSource)) }];
    for (const spec of config.keys) keys.push(await normalizeKey(spec, base, outputRoot));
    await contact(outputRoot, keys);
    const ordered = defaults.order.map(id => keys.find(key => key.id === id));
    const normalization = { revision, playbackMode, name: config.name, keys, order: defaults.order, factor, sampleFps, endpointWindow, knots, bounceKnots, frameSampling, blendWeightSteps,
      durationMs: 1400, endpointMode: 'residual', method: 'Complete generated RGBA poses registered at paws, dual-matte RIFE with exact original idle endpoints. Head motion comes from source artwork; no facial mask, local redraw or head deformation. Whole-frame displacement defaults to zero.' };
    await fs.writeFile(path.join(outputRoot, 'normalization.json'), json(normalization));
    if (options.normalizeOnly) return { outputRoot, normalizedOnly: true, contact: path.join(outputRoot, 'keyframes-contact.png') };
    const fingerprint = hash(JSON.stringify({ keys: ordered.map(key => key.sha256), factor, engine: 'RIFE4.6-TTA' })).slice(0, 16);
    let inferenceRoot = path.join(outputRoot, 'inference-' + fingerprint), inferenceVerification, reusedFrom = null;
    if (config.cacheReuse) {
      const cache = inside(path.join(repo, 'artifacts'), path.relative(path.join(repo, 'artifacts'), path.resolve(repo, config.cacheReuse.directory)));
      try {
        inferenceVerification = await verifyInference(cache, ordered, factor, fingerprint, config.cacheReuse.pngAggregateSHA256);
        inferenceRoot = cache; reusedFrom = cache;
      } catch (error) { if (error.code !== 'ENOENT') throw error; }
    }
    if (!inferenceVerification) {
      try { inferenceVerification = await verifyInference(inferenceRoot, ordered, factor, fingerprint); }
      catch (error) {
        if (error.code !== 'ENOENT') throw error;
        await interpolate({ frames: ordered.map(key => path.join(outputRoot, key.file)), outputDirectory: inferenceRoot, factor, gpu: config.gpu ?? 0, temporalTta: true });
        inferenceVerification = await verifyInference(inferenceRoot, ordered, factor, fingerprint);
      }
    }
    const inferenceCache = { directory: inferenceRoot, reusedFrom, fingerprint, pngAggregateSHA256: inferenceVerification.pngAggregateSHA256, sourceKeysVerified: true };
    const materialized = new Map(), corrected = [];
    async function sourceAt(index) {
      if (materialized.has(index)) return materialized.get(index);
      if (index % factor === 0) return path.join(outputRoot, ordered[index / factor].file);
      const source = path.join(inferenceRoot, String(index).padStart(5, '0') + '.png');
      const nearest = Math.round(index / factor), distance = Math.abs(index - nearest * factor);
      let result = source;
      if (distance < endpointWindow) {
        result = path.join(outputRoot, 'frames', 'corrected-' + fingerprint + '-' + endpointWindow + '-' + String(index).padStart(5, '0') + '.png');
        const direction = Math.sign(index - nearest * factor), neighbor = step => path.join(inferenceRoot, String(nearest * factor + direction * step).padStart(5, '0') + '.png');
        await correctWinkEndpoint({ basePath: path.join(outputRoot, ordered[nearest].file), framePath: source, near1Path: neighbor(1), near2Path: neighbor(2), index: distance, window: endpointWindow, outputPath: result });
        corrected.push(index);
      }
      materialized.set(index, result); return result;
    }
    const rawCache = new Map();
    async function rawAt(index) {
      if (rawCache.has(index)) return rawCache.get(index);
      const raw = await sharp(await sourceAt(index)).toColourspace('srgb').ensureAlpha().raw().toBuffer();
      if (raw.length !== 512 * 512 * 4) throw Error('Unexpected RGBA length in source frame ' + index);
      rawCache.set(index, raw);
      if (rawCache.size > 24) rawCache.delete(rawCache.keys().next().value);
      return raw;
    }
    const samples = timingSamples(knots, sampleFps, 'pchip', { zeroEndpointVelocity: true });
    const bounce = makePhaseSampler(bounceKnots, 'pchip', { zeroEndpointVelocity: true }), transformed = new Map(), frames = [], motionSamples = [];
    for (let i = 0; i < samples.length; i++) {
      const sample = samples[i], sampling = samplingAt(sample.phase, factor, frameSampling, blendWeightSteps);
      const { indexPosition, lowerIndex, upperIndex, weight } = sampling;
      // Final idle slot is exact and grounded, independent of the sampling grid.
      const offsetY = i === samples.length - 1 ? 0 : Math.round(bounce(sample.timeMs) * 64) / 64;
      const key = frameSampling === 'nearest' ? lowerIndex + ':' + offsetY : frameSampling + ':' + blendWeightSteps + ':' + indexPosition + ':' + offsetY;
      let file = transformed.get(key);
      if (!file) {
        if ((indexPosition === 0 || indexPosition === 4 * factor) && offsetY === 0) file = 'keyframes/idle.png';
        else {
          file = 'frames/meow-' + fingerprint + '-' + hash(key).slice(0, 12) + '.png';
          if (weight !== 0) {
            const data = blendRgba(await rawAt(lowerIndex), await rawAt(upperIndex), weight);
            if (offsetY !== 0) throw Error('Fractional sampling cannot be combined with a whole-body translation.');
            await sharp(data, { raw: canvas }).png().toFile(path.join(outputRoot, file));
          } else if (revision === 'meow-v1') await translateWholePose(await sourceAt(lowerIndex), offsetY, path.join(outputRoot, file));
          else await fs.copyFile(await sourceAt(lowerIndex), path.join(outputRoot, file));
        }
        transformed.set(key, file);
      }
      frames.push({ file, durationMs: sample.durationMs });
      motionSamples.push({ timeMs: sample.timeMs, phase: sample.phase, index: Math.round(indexPosition), ...sampling, offsetY });
    }
    const clip = { loop: false, frames }, report = await qa(outputRoot, clip, idleSource);
    const sampled = [];
    for (const timeMs of [...new Set(knots.map(knot => knot[0]))]) {
      let elapsed = 0; const frame = frames.find(frame => (elapsed += frame.durationMs) > timeMs) || frames.at(-1);
      const raw = await sharp(path.join(outputRoot, frame.file)).ensureAlpha().raw().toBuffer();
      sampled.push({ id: timeMs + ' ms', timeMs, file: frame.file, ...groundingMetrics(raw), pawResidualVsIdle: pawResidual(base, raw, 0, 0) });
    }
    await contact(outputRoot, sampled, 'sampled-contact.png');
    const candidate = { candidateOnly: true, name: config.name, revision, playbackMode, selectionSHA256: settings.selectionSHA256,
      canvas: { width: 512, height: 512 }, clips: { meow: clip }, motion: { sampleFps, knots, bounceKnots, frameSampling, blendWeightSteps, durationMs: 1400, wholeFrameDisplacement: revision === 'meow-v1' ? 'explicit legacy recipe' : 'disabled' }, sourceKeys: keys };
    await fs.writeFile(path.join(outputRoot, 'candidate-manifest.json'), json(candidate));
    await fs.writeFile(path.join(outputRoot, 'qa.json'), json({ ...report, revision, playbackMode, frameSampling, blendWeightSteps, fingerprint, inferenceCache, corrected,
      maximumSubframeQuantizationError: Math.max(...motionSamples.map(sample => Math.abs(sample.quantizationError))),
      samplingSelfTest: frameSampling === 'premultiplied-linear' ? selfTest() : null,
      motionSamples, sampledGrounding: sampled, selection: settings.selectionPath, selectionSHA256: settings.selectionSHA256 }));
    await preview(outputRoot, clip);
    return { outputRoot, candidateOnly: true, manifest: path.join(outputRoot, 'candidate-manifest.json'), preview: path.join(outputRoot, 'preview.html'), durationMs: 1400, slots: report.slots, uniquePngs: report.uniquePngs };
  } finally {
    const latest = await fs.readFile(manifestPath), after = { manifestSHA256: hash(latest), ...await snapshot(JSON.parse(latest)) };
    const unchanged = JSON.stringify(before) === JSON.stringify(after);
    await fs.writeFile(path.join(outputRoot, 'production-protection.json'), json({ unchanged, before, after }));
    if (!unchanged) throw Error('Production assets changed during candidate generation; inspect production-protection.json.');
  }
}

async function install(selection) {
  const settings = await load(selection), { outputRoot, config } = settings;
  const candidate = JSON.parse(await fs.readFile(path.join(outputRoot, 'candidate-manifest.json'), 'utf8'));
  const report = JSON.parse(await fs.readFile(path.join(outputRoot, 'qa.json'), 'utf8'));
  if (!candidate.candidateOnly || candidate.name !== config.name || candidate.revision !== settings.revision || candidate.playbackMode !== settings.playbackMode || candidate.selectionSHA256 !== settings.selectionSHA256 || report.selectionSHA256 !== settings.selectionSHA256 || !report.passed || !report.firstExact || !report.lastExact)
    throw Error('Build and review this exact selection before installing.');
  for (const key of candidate.sourceKeys) {
    if (hash(await fs.readFile(inside(outputRoot, key.file))) !== key.sha256) throw Error('Normalized key changed: ' + key.id);
    if (key.source && hash(await fs.readFile(key.source)) !== key.sourceSHA256) throw Error('Source key changed: ' + key.id);
  }
  for (const [file, sha] of Object.entries(report.imageHashes)) if (hash(await fs.readFile(inside(outputRoot, file))) !== sha) throw Error('Candidate frame changed: ' + file);
  const original = await fs.readFile(manifestPath), manifest = JSON.parse(original), before = await snapshot(manifest, true);
  const idleFile = manifest.clips.idle.frames[0].file, idleSource = path.join(production, idleFile), idleHash = hash(await fs.readFile(idleSource));
  await qa(outputRoot, candidate.clips.meow, idleSource);
  const mapping = new Map(), frameFolder = 'frames/' + settings.revision, targetRoot = path.join(production, frameFolder);
  await fs.mkdir(targetRoot, { recursive: true });
  for (const file of new Set(candidate.clips.meow.frames.map(frame => frame.file))) {
    const bytes = await fs.readFile(inside(outputRoot, file)), sha = hash(bytes);
    const target = sha === idleHash ? idleFile : frameFolder + '/' + sha + '.png';
    if (target !== idleFile) await fs.writeFile(path.join(production, target), bytes);
    mapping.set(file, target);
  }
  manifest.clips.meow = { loop: false, frames: candidate.clips.meow.frames.map(frame => ({ file: mapping.get(frame.file), durationMs: frame.durationMs })) };
  const after = await snapshot(manifest, true);
  if (JSON.stringify(before) !== JSON.stringify(after)) throw Error('Another clip changed during installation.');
  try { await fs.writeFile(path.join(outputRoot, 'runtime-manifest-before.json'), original, { flag: 'wx' }); } catch (error) { if (error.code !== 'EEXIST') throw error; }
  // Do not overwrite a concurrent manifest update from another worker.
  if (!(await fs.readFile(manifestPath)).equals(original)) throw Error('Runtime manifest changed concurrently; rerun installation.');
  await fs.writeFile(manifestPath, json(manifest));
  const installed = { updated: manifestPath, candidate: outputRoot, durationMs: 1400, uniquePngs: new Set(mapping.values()).size, otherClipsUnchanged: true, before, after };
  await fs.writeFile(path.join(outputRoot, 'runtime-install.json'), json(installed));
  return installed;
}

module.exports = { build, install, load, qa, translateWholePose, groundingMetrics, blendRgba, samplingAt, verifyInference, selfTest, defaults };
if (require.main === module) {
  const args = process.argv.slice(2), index = args.indexOf('--selection'), selection = index >= 0 ? args[index + 1] : null;
  if (args.includes('--self-test')) {
    try { console.log(json(selfTest())); } catch (error) { console.error(error); process.exitCode = 1; }
  } else if (!selection || (args.includes('--normalize-only') && args.includes('--install'))) {
    console.error('Usage: node tools/build-meow.cjs --selection selection.json [--normalize-only|--install]'); process.exitCode = 1;
  } else (args.includes('--install') ? install(selection) : build(selection, { normalizeOnly: args.includes('--normalize-only') }))
    .then(result => console.log(json(result))).catch(error => { console.error(error); process.exitCode = 1; });
}
