// Isolated wink review pipeline. Never publishes or writes production assets.
// node tools/build-wink-candidate.cjs --selection artifacts/wink-v8/selection.json
// Optional: --normalize-only, --preview-only, --stage-only (all builds are staged), --no-gif.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');
const { interpolate } = require('./motion-interpolate.cjs');
const { correctSleepEndpoint } = require('./sleep-endpoint.cjs');
const { correctWinkEndpoint } = require('./wink-endpoint.cjs');
const { findPawTranslation, pawResidual, cleanLowAlphaNoise, visibleBounds } = require('./normalize-idle-v7.cjs');
const repo = path.resolve(__dirname, '..');
const production = path.join(repo, 'assets/strawberry-cat');
const idleSource = path.join(production, 'frames/fall_asleep-00.png');
const canvas = { width: 512, height: 512, channels: 4 };
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const slash = value => value.split(path.sep).join('/');
const esc = value => String(value).replace(/[<>&\"]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', '"': '&quot;' }[c]));
const duration = clip => clip.frames.reduce((sum, frame) => sum + frame.durationMs, 0);
function fileAt(clip, time) { let end = 0; for (const f of clip.frames) { end += f.durationMs; if (time < end) return f.file; } return clip.frames.at(-1).file; }

async function protectedHashes() {
  const manifestFile = path.join(production, 'manifest.json');
  const bytes = await fs.readFile(manifestFile), manifest = JSON.parse(bytes);
  const files = [...new Set(Object.values(manifest.clips).flatMap(c => c.frames.map(f => f.file)))].sort();
  const hasher = crypto.createHash('sha256');
  for (const file of files) hasher.update(file + '\0' + sha(await fs.readFile(path.join(production, file))) + '\n');
  return { manifestSHA256: sha(bytes), pngCount: files.length, pngAggregateSHA256: hasher.digest('hex') };
}

function phaseAt(time, knots) {
  if (time >= knots.at(-1)[0]) return knots.at(-1)[1];
  let i = 0; while (i < knots.length - 2 && time >= knots[i + 1][0]) i++;
  const [start, from] = knots[i], [end, to] = knots[i + 1];
  const t = (time - start) / (end - start), eased = t * t * (3 - 2 * t);
  return from + (to - from) * eased;
}

// Shape-preserving cubic Hermite timing (PCHIP). Interior slopes are weighted
// harmonic means, so increasing poses flow through a key instead of stopping.
// Plateaus and direction reversals still stop; no segment can overshoot its keys.
function makePhaseSampler(knots, mode = 'smoothstep', options = {}) {
  if (!['smoothstep', 'pchip', 'linear'].includes(mode)) throw Error('timing must be smoothstep, pchip or linear.');
  if (!Array.isArray(knots) || knots.length < 2 || knots.some((k, i) => !Array.isArray(k) || k.length !== 2 || !k.every(Number.isFinite) || (i && k[0] <= knots[i - 1][0]))) throw Error('Timing knots need finite values and strictly increasing times.');
  if (mode === 'smoothstep') return time => phaseAt(time, knots);
  // The approved production blink advances linearly inside each authored
  // pose interval. Keep that option explicit instead of approximating it with easing.
  if (mode === 'linear') return time => {
    if (time <= knots[0][0]) return knots[0][1];
    if (time >= knots.at(-1)[0]) return knots.at(-1)[1];
    let i = 0; while (i < knots.length - 2 && time >= knots[i + 1][0]) i++;
    const [start, from] = knots[i], [end, to] = knots[i + 1];
    return from + (to - from) * (time - start) / (end - start);
  };
  const n = knots.length, h = [], delta = [], slopes = Array(n).fill(0);
  for (let i = 0; i < n - 1; i++) { h.push(knots[i + 1][0] - knots[i][0]); delta.push((knots[i + 1][1] - knots[i][1]) / h[i]); }
  for (let i = 1; i < n - 1; i++) {
    if (!delta[i - 1] || !delta[i] || Math.sign(delta[i - 1]) !== Math.sign(delta[i])) continue;
    const w1 = 2 * h[i] + h[i - 1], w2 = h[i] + 2 * h[i - 1];
    slopes[i] = (w1 + w2) / (w1 / delta[i - 1] + w2 / delta[i]);
  }
  function endpoint(h0, h1, d0, d1) {
    let m = ((2 * h0 + h1) * d0 - h0 * d1) / (h0 + h1);
    if (Math.sign(m) !== Math.sign(d0)) m = 0;
    else if (Math.sign(d0) !== Math.sign(d1) && Math.abs(m) > 3 * Math.abs(d0)) m = 3 * d0;
    return m;
  }
  if (options.zeroEndpointVelocity === false) {
    slopes[0] = n === 2 ? delta[0] : endpoint(h[0], h[1], delta[0], delta[1]);
    slopes[n - 1] = n === 2 ? delta[0] : endpoint(h[n - 2], h[n - 3], delta[n - 2], delta[n - 3]);
  }
  return time => {
    if (time <= knots[0][0]) return knots[0][1];
    if (time >= knots[n - 1][0]) return knots[n - 1][1];
    let i = 0; while (i < n - 2 && time >= knots[i + 1][0]) i++;
    const t = (time - knots[i][0]) / h[i], t2 = t * t, t3 = t2 * t;
    const value = (2 * t3 - 3 * t2 + 1) * knots[i][1] + (t3 - 2 * t2 + t) * h[i] * slopes[i]
      + (-2 * t3 + 3 * t2) * knots[i + 1][1] + (t3 - t2) * h[i] * slopes[i + 1];
    return Math.max(Math.min(knots[i][1], knots[i + 1][1]), Math.min(Math.max(knots[i][1], knots[i + 1][1]), value));
  };
}

function timingSamples(knots, sampleFps, mode = 'smoothstep', options = {}) {
  const durationMs = knots.at(-1)[0], count = Math.round(durationMs * sampleFps / 1000), sample = makePhaseSampler(knots, mode, options);
  if (count < 2) throw Error('Clip must have at least two frame slots.');
  if (mode === 'smoothstep') return Array.from({ length: count }, (_, i) => ({ timeMs: i * durationMs / (count - 1), phase: sample(i * durationMs / (count - 1)), durationMs: Math.round((i + 1) * durationMs / count) - Math.round(i * durationMs / count) }));
  if (knots.some(k => !Number.isInteger(k[0]))) throw Error('Key timestamps must be whole milliseconds.');
  // Include exact art-key timestamps in the regular sampling grid. PCHIP may
  // therefore add a few slots beyond the nominal 240 Hz count, without changing duration.
  const edges = [...new Set([...Array.from({ length: count + 1 }, (_, i) => Math.round(i * durationMs / count)), ...knots.map(k => k[0])])].sort((a, b) => a - b);
  return edges.slice(0, -1).map((timeMs, i) => ({ timeMs, phase: i === edges.length - 2 ? knots.at(-1)[1] : sample(timeMs), durationMs: edges[i + 1] - timeMs }));
}

function candidateLocation(config) {
  const outputGroup = config.outputGroup || 'wink-v8', version = config.version ?? Number(/^wink-v(\d+)$/.exec(outputGroup)?.[1]);
  if (!/^wink-v[1-9][0-9]*$/.test(outputGroup) || !Number.isInteger(version) || version < 1) throw Error('outputGroup must be wink-vN; version must be a positive integer.');
  if (!/^[a-z0-9][a-z0-9_-]{0,63}$/.test(config.name || '') || ['references', 'sources'].includes(config.name)) throw Error('Supply a safe, unique candidate name.');
  return { outputGroup, version, revision: config.revision || outputGroup + '-candidate', outputRoot: path.join(repo, 'artifacts', outputGroup, config.name) };
}

async function normalizeKey(spec, base, outputRoot) {
  const input = path.isAbsolute(spec.source) ? spec.source : path.resolve(repo, spec.source);
  const encoded = await fs.readFile(input), metadata = await sharp(encoded).metadata();
  if (!metadata.hasAlpha) throw Error('RGBA input required: ' + input);
  const scale = spec.scale ?? 1;
  if (!Number.isFinite(scale) || scale < .85 || scale > 1.15) throw Error('scale must be 0.85–1.15');
  let image = sharp(encoded).toColourspace('srgb').ensureAlpha();
  if (spec.crop) image = image.extract(spec.crop);
  let square = await image.resize(512, 512, { fit: 'contain', background: '#00000000', kernel: 'lanczos3' }).png().toBuffer();
  const size = Math.round(512 * scale);
  if (size !== 512) {
    const scaled = await sharp(square).resize(size, size).png().toBuffer();
    const gap = Math.abs(size - 512), start = Math.floor(gap / 2);
    square = await (size > 512 ? sharp(scaled).extract({ left: start, top: start, width: 512, height: 512 }) : sharp(scaled).extend({ left: start, right: gap - start, top: start, bottom: gap - start, background: '#00000000' })).png().toBuffer();
  }
  const raw = await sharp(square).raw().toBuffer(), noise = cleanLowAlphaNoise(raw);
  const proposed = findPawTranslation(base, raw), translation = spec.translation || { x: proposed.dx, y: proposed.dy };
  if (![translation.x, translation.y].every(Number.isInteger) || Math.abs(translation.x) > 32 || Math.abs(translation.y) > 32) throw Error('Translation must be integer ±32 px.');
  const expanded = await sharp(raw, { raw: canvas }).extend({ left: 40, right: 40, top: 40, bottom: 40, background: '#00000000' }).png().toBuffer();
  const data = await sharp(expanded).extract({ left: 40 - translation.x, top: 40 - translation.y, width: 512, height: 512 }).raw().toBuffer();
  const bounds = visibleBounds(data), residual = pawResidual(base, data, 0, 0);
  if (bounds.maximumEdgeAlpha > 0 || bounds.transparentPixels < 512 * 512 * .15) throw Error('Clipped or nontransparent input: ' + spec.id);
  if (residual > (spec.maximumPawResidual ?? 35)) throw Error('Paw registration needs review: ' + spec.id + ', residual ' + residual.toFixed(3));
  const file = 'keyframes/' + spec.id + '.png', output = path.join(outputRoot, file);
  if (path.resolve(input).toLowerCase() === path.resolve(output).toLowerCase()) throw Error('Output must differ from input.');
  await sharp(data, { raw: canvas }).png().toFile(output);
  return { id: spec.id, source: input, sourceSHA256: sha(encoded), sourceSize: { width: metadata.width, height: metadata.height }, file,
    sha256: sha(await fs.readFile(output)), scale, translation, proposed, pawResidual: residual, bounds, noise,
    completeRgbaPose: true, faceMaskUsed: false, poseWarpUsed: false };
}

async function contact(outputRoot, keys) {
  const cell = 256, h = 286, cols = Math.min(keys.length, 4), rows = Math.ceil(keys.length / cols), layers = [];
  for (let i = 0; i < keys.length; i++) {
    const x = i % cols * cell, y = Math.floor(i / cols) * h;
    layers.push({ input: await sharp(path.join(outputRoot, keys[i].file)).resize(cell, cell).png().toBuffer(), left: x, top: y + 28 });
    layers.push({ input: Buffer.from('<svg width="256" height="28"><text x="10" y="20" font-size="15" fill="#463e44">' + esc(keys[i].id) + '</text></svg>'), left: x, top: y });
  }
  await sharp({ create: { width: cols * cell, height: rows * h, channels: 4, background: '#fff8f3' } }).composite(layers).png().toFile(path.join(outputRoot, 'keyframes-contact.png'));
}

async function qa(outputRoot, clip) {
  const files = [...new Set(clip.frames.map(f => f.file))], frames = [];
  for (const file of files) {
    const meta = await sharp(path.join(outputRoot, file)).metadata();
    if (meta.width !== 512 || meta.height !== 512 || !meta.hasAlpha) throw Error('Invalid final canvas/alpha: ' + file);
    const raw = await sharp(path.join(outputRoot, file)).ensureAlpha().raw().toBuffer(), bounds = visibleBounds(raw);
    if (bounds.maximumEdgeAlpha > 0 || bounds.transparentPixels < 512 * 512 * .15) throw Error('Final frame clipping/background: ' + file);
    frames.push({ file, bounds });
  }
  const idle = await fs.readFile(idleSource);
  const firstExact = (await fs.readFile(path.join(outputRoot, clip.frames[0].file))).equals(idle);
  const lastExact = (await fs.readFile(path.join(outputRoot, clip.frames.at(-1).file))).equals(idle);
  if (!firstExact || !lastExact) throw Error('Candidate endpoints must exactly match original idle PNG.');
  const runs = []; let at = 0;
  for (const f of clip.frames) { const last = runs.at(-1); if (last?.file === f.file) last.durationMs += f.durationMs; else runs.push({ file: f.file, atMs: at, durationMs: f.durationMs }); at += f.durationMs; }
  return { passed: true, uniquePngs: files.length, slots: clip.frames.length, durationMs: duration(clip), firstExact, lastExact,
    longestSameImageRun: runs.reduce((a, b) => a.durationMs >= b.durationMs ? a : b), frames,
    limitations: ['Checks canvas, transparency, clipping, timing and exact endpoints only. Eyelid anatomy, expression appeal and coordinated motion require visual review.'] };
}

async function preview(outputRoot, manifest, before, config) {
  let beforeRoot = production, beforeLabel = config.beforeLabel || '当前版';
  if (config.compareTo) {
    const comparison = typeof config.compareTo === 'string' ? { manifest: config.compareTo } : config.compareTo;
    if (typeof comparison.manifest !== 'string') throw Error('compareTo needs a candidate manifest path.');
    const comparisonFile = path.resolve(repo, comparison.manifest), previous = JSON.parse(await fs.readFile(comparisonFile, 'utf8'));
    if (!previous.clips?.wink?.frames?.length) throw Error('Comparison manifest has no wink clip.');
    before = previous.clips.wink; beforeRoot = path.dirname(comparisonFile); beforeLabel = comparison.label || previous.name || '对照版';
  }
  const labels = [beforeLabel, config.label || '候选版'];
  const beforeClip = { ...before, frames: before.frames.map(f => ({ ...f, file: slash(path.relative(outputRoot, path.join(beforeRoot, f.file))) })) };
  const clips = [beforeClip, manifest.clips.wink], data = JSON.stringify({ clips, name: config.name }).replace(/</g, '\\u003c');
  clips.labels = labels;
  const html = `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>单眼眨候选对照</title>
<style>*{box-sizing:border-box}body{margin:0;background:#f6f1ec;color:#463e44;font:15px/1.6 "Segoe UI","Microsoft YaHei",sans-serif}main{max-width:1100px;padding:28px;margin:auto}h1{font-size:26px;margin:0 0 8px}p{color:#786873}.cards{display:grid;grid-template-columns:1fr 1fr;gap:18px;margin:20px 0}.card{border:1px solid #dfd4ce;border-radius:18px;background:#fffaf6;padding:18px;text-align:center}.stage{min-height:340px;display:grid;place-items:center}.dark .stage{background:#26313f}.checker .stage{background:repeating-conic-gradient(#dfd9d4 0% 25%,#fffaf6 0% 50%) 50%/20px 20px}canvas{width:240px;height:240px;max-width:100%}button,select{font:inherit;padding:8px 12px;border:1px solid #d8cbd0;border-radius:8px;background:#fffaf8;color:inherit}button{cursor:pointer}.controls{display:flex;gap:10px;flex-wrap:wrap;align-items:center}input{width:100%;accent-color:#b17790}#status{min-height:28px}small{color:#786873}a{color:#96617b}@media(max-width:640px){.cards{grid-template-columns:1fr}main{padding:18px}}</style>
<main><h1>单眼眨 · ${esc(labels[0])}与${esc(labels[1])}</h1><p>${esc(config.name)} · 两侧使用各自原始时长，同步开始；结束后回到待机。</p><div class="cards"><section class="card"><strong>${esc(labels[0])} · ${duration(before)} ms</strong><div class="stage"><canvas width="512" height="512"></canvas></div><small id="leftTime"></small></section><section class="card"><strong>${esc(labels[1])} · ${duration(manifest.clips.wink)} ms</strong><div class="stage"><canvas width="512" height="512"></canvas></div><small id="rightTime"></small></section></div>
<div class="controls"><button id="play">播放</button><button id="replay">重播</button><label>速度 <select id="speed"><option value="1">正常</option><option value="0.5">0.5×</option><option value="0.25">0.25×</option></select></label><label>背景 <select id="bg"><option value="light">浅色</option><option value="dark">深色</option><option value="checker">透明格</option></select></label><label>大小 <select id="size"><option value="240">240 px</option><option value="360">360 px</option><option value="512">512 px</option></select></label><label><input type="checkbox" id="loop" checked style="width:auto">循环</label></div><input type="range" id="scrub" min="0" max="1400" step="1" value="0" aria-label="逐帧时间"><div id="status">正在载入图片…</div><small>拖动可暂停查看同一时刻。GIF为50fps预览，透明PNG为正式候选素材。<a href="keyframes-contact.png">查看关键姿态</a></small></main>
<script>'use strict';const data=${data};const canvases=[...document.querySelectorAll('canvas')],contexts=canvases.map(c=>c.getContext('2d')),cache=new Map();const lengths=data.clips.map(c=>c.frames.reduce((s,f)=>s+f.durationMs,0)),max=Math.max(...lengths),scrub=document.getElementById('scrub'),status=document.getElementById('status'),play=document.getElementById('play');scrub.max=max;let elapsed=0,paused=true,ready=false,last=null;function at(clip,t){let e=0;for(const f of clip.frames){e+=f.durationMs;if(t<e)return f.file;}return clip.frames.at(-1).file;}function draw(){data.clips.forEach((clip,i)=>{const ctx=contexts[i];ctx.clearRect(0,0,512,512);const img=cache.get(at(clip,elapsed));if(img)ctx.drawImage(img,0,0,512,512);});document.getElementById('leftTime').textContent=Math.round(Math.min(elapsed,lengths[0]))+' / '+lengths[0]+' ms';document.getElementById('rightTime').textContent=Math.round(Math.min(elapsed,lengths[1]))+' / '+lengths[1]+' ms';scrub.value=Math.min(elapsed,max);play.textContent=paused?'播放':'暂停';}play.onclick=()=>{if(!ready)return;if(elapsed>=max)elapsed=0;paused=!paused;last=null;draw();};document.getElementById('replay').onclick=()=>{elapsed=0;paused=false;last=null;draw();};scrub.oninput=()=>{elapsed=+scrub.value;paused=true;last=null;draw();};document.getElementById('bg').onchange=e=>{document.body.className=e.target.value;};document.getElementById('size').onchange=e=>canvases.forEach(c=>{c.style.width=e.target.value+'px';c.style.height=e.target.value+'px';});function tick(now){const d=last===null?0:Math.min(100,now-last);last=now;if(ready&&!paused&&!document.hidden){elapsed+=d*Number(document.getElementById('speed').value);if(elapsed>=max+(document.getElementById('loop').checked?650:0)){if(document.getElementById('loop').checked)elapsed=0;else{elapsed=max;paused=true;}}draw();}requestAnimationFrame(tick);}document.addEventListener('visibilitychange',()=>last=null);(async()=>{try{const files=[...new Set(data.clips.flatMap(c=>c.frames.map(f=>f.file)))];let cursor=0;await Promise.all(Array.from({length:4},async()=>{while(cursor<files.length){const file=files[cursor++],img=new Image();img.src=file;await img.decode();cache.set(file,img);}}));ready=true;paused=false;status.textContent='已载入。请在正常速度和慢速下比较眼睑闭合、另一只眼、头部与尾部。';draw();requestAnimationFrame(tick);}catch(e){status.textContent='载入失败：'+e.message;}})();</script></html>`;
  await fs.writeFile(path.join(outputRoot, 'before-after.html'), html);
  return clips;
}

async function gif(outputRoot, clips) {
  const cell = 240, width = 480, height = 284, total = Math.ceil((Math.max(...clips.map(duration)) + 1000) / 20) * 20;
  const images = new Map(), composed = new Map(), rawFrames = [];
  const names = clips.labels || ['当前版', '候选版'];
  const labels = Buffer.from('<svg width="480" height="284"><style>text{font-family:Microsoft YaHei,Segoe UI;font-size:14px;fill:#71515f}</style><text text-anchor="middle" x="120" y="22">' + esc(names[0]) + '</text><text text-anchor="middle" x="360" y="22">' + esc(names[1]) + '</text><text x="175" y="280" style="font-size:10px">50fps · 正常动作速度</text></svg>');
  for (let time = 0; time < total; time += 20) {
    const files = clips.map(c => fileAt(c, Math.max(0, time - 400))), key = files.join('|');
    if (!composed.has(key)) {
      const layers = [];
      for (let i = 0; i < files.length; i++) {
        if (!images.has(files[i])) images.set(files[i], await sharp(path.resolve(outputRoot, files[i])).resize(cell, cell).png().toBuffer());
        layers.push({ input: images.get(files[i]), left: i * cell, top: 28 });
      }
      layers.push({ input: labels, left: 0, top: 0 });
      composed.set(key, await sharp({ create: { width, height, channels: 4, background: '#fff8f3' } }).composite(layers).raw().toBuffer());
    }
    rawFrames.push(composed.get(key));
  }
  const file = path.join(outputRoot, 'before-after.gif');
  await sharp(Buffer.concat(rawFrames), { raw: { width, height: height * rawFrames.length, channels: 4, pageHeight: height } }).gif({ loop: 0, delay: Array(rawFrames.length).fill(20), colours: 256, dither: 0 }).toFile(file);
  const metadata = await sharp(file, { animated: true }).metadata(), encodedDurationMs = metadata.delay.reduce((s, d) => s + d, 0);
  if (encodedDurationMs !== total) throw Error('GIF duration mismatch.');
  return { file, sampleFps: 50, encodedDurationMs, pages: metadata.pages };
}

async function build(selection, options = {}) {
  const before = await protectedHashes(), config = JSON.parse(await fs.readFile(path.resolve(selection), 'utf8'));
  const { outputRoot, outputGroup, revision, version } = candidateLocation(config);
  const factor = config.factor ?? 32, sampleFps = config.sampleFps ?? 240, endpointWindow = config.endpointWindow ?? 4;
  const timing = config.timing || 'smoothstep', zeroEndpointVelocity = config.zeroEndpointVelocity !== false;
  const endpointMode = config.endpointMode || 'blend', refinementDivisions = config.refinementDivisions || 1;
  if (!['blend', 'residual'].includes(endpointMode) || !Number.isInteger(refinementDivisions) || refinementDivisions < 1 || factor % refinementDivisions || factor / refinementDivisions < 2) throw Error('Invalid endpoint mode or refinement divisions.');
  if (!['smoothstep', 'pchip', 'linear'].includes(timing)) throw Error('timing must be smoothstep, pchip or linear.');
  if (!Number.isInteger(factor) || factor < 2 || factor > 256 || ![60, 120, 240].includes(sampleFps) || !Number.isInteger(endpointWindow) || endpointWindow < 0 || endpointWindow > factor / 2) throw Error('Invalid factor, sampleFps or endpointWindow.');
  if (!options.previewOnly && (!Array.isArray(config.keys) || !config.keys.length)) throw Error('keys are required.');
  const ids = new Set(['idle']);
  for (const key of config.keys || []) { if (!/^[a-z][a-z0-9-]*$/.test(key.id || '') || ids.has(key.id) || typeof key.source !== 'string') throw Error('Invalid key id/source.'); ids.add(key.id); }
  await fs.mkdir(path.join(outputRoot, 'keyframes'), { recursive: true });
  await fs.mkdir(path.join(outputRoot, 'frames'), { recursive: true });
  try {
    if (options.previewOnly) {
      const manifest = JSON.parse(await fs.readFile(path.join(outputRoot, 'candidate-manifest.json'), 'utf8'));
      if (!manifest.candidateOnly || manifest.name !== config.name || !manifest.clips?.wink?.frames?.length) throw Error('Preview-only needs this candidate\'s existing manifest.');
      const active = JSON.parse(await fs.readFile(path.join(production, 'manifest.json'), 'utf8'));
      const clips = await preview(outputRoot, manifest, active.clips.wink, config);
      const gifReport = options.noGif ? null : await gif(outputRoot, clips);
      const result = { outputRoot, previewOnly: true, preview: path.join(outputRoot, 'before-after.html'), gif: gifReport?.file, labels: clips.labels, comparedTo: config.compareTo || 'production', durationMs: duration(manifest.clips.wink) };
      await fs.writeFile(path.join(outputRoot, 'preview-report.json'), JSON.stringify(result, null, 2) + '\n');
      return result;
    }
    await fs.copyFile(idleSource, path.join(outputRoot, 'keyframes/idle.png'));
    const base = await sharp(idleSource).ensureAlpha().raw().toBuffer(), keys = [{ id: 'idle', file: 'keyframes/idle.png', sha256: sha(await fs.readFile(idleSource)) }];
    for (const spec of config.keys) keys.push(await normalizeKey(spec, base, outputRoot));
    await contact(outputRoot, keys);
    const keyMap = new Map(keys.map(k => [k.id, k])), order = config.keysOrder || keys.map(k => k.id);
    if (!Array.isArray(order) || order.length < 2 || order[0] !== 'idle' || order.some(id => !keyMap.has(id))) throw Error('keysOrder must begin with idle and use known keys.');
    const ordered = order.map(id => keyMap.get(id)), peakIndex = config.peakIndex ?? (order.at(-1) === 'idle' ? order.length - 2 : order.length - 1);
    const endPhase = order.at(-1) === 'idle' ? order.length - 1 : 0;
    const knots = config.knots || [[0, 0], [160, peakIndex], [360, peakIndex], [650, endPhase]];
    const durationMs = knots.at(-1)?.[0];
    if (knots.length < 3 || knots[0][0] !== 0 || knots[0][1] !== 0 || !Number.isInteger(durationMs) || durationMs <= 0) throw Error('Invalid timing knots.');
    for (let i = 0; i < knots.length; i++) if (knots[i].length !== 2 || !knots[i].every(Number.isFinite) || knots[i][1] < 0 || knots[i][1] > order.length - 1 || (i && knots[i][0] <= knots[i - 1][0])) throw Error('Timing knots must increase and reference valid phases.');
    if (order[knots.at(-1)[1]] !== 'idle') throw Error('Final knot must refer to idle.');
    const samples = timingSamples(knots, sampleFps, timing, { zeroEndpointVelocity });
    const normalization = { candidate: config.name, outputGroup, revision, keys, order, factor, sampleFps, endpointWindow, endpointMode, refinementDivisions, timing, zeroEndpointVelocity, knots, durationMs, scope: 'Candidate only. Complete RGBA poses registered at front paws, no local redraw or deformation.' };
    await fs.writeFile(path.join(outputRoot, 'normalization.json'), JSON.stringify(normalization, null, 2) + '\n');
    if (options.normalizeOnly) return { outputRoot, normalizedOnly: true, contact: path.join(outputRoot, 'keyframes-contact.png') };
    const sourceFingerprint = sha(Buffer.from(JSON.stringify({ keys: ordered.map(k => k.sha256), factor, endpointWindow, engine: 'RIFE4.6-TTA' }))).slice(0, 16);
    const fingerprint = endpointMode === 'blend' && refinementDivisions === 1 ? sourceFingerprint : sha(Buffer.from(JSON.stringify({ sourceFingerprint, endpointMode, refinementDivisions }))).slice(0, 16);
    let inferenceRoot = path.join(outputRoot, 'inference-' + sourceFingerprint), inference;
    try { inference = JSON.parse(await fs.readFile(path.join(inferenceRoot, 'interpolation-report.json'), 'utf8')); }
    catch (error) { if (error.code !== 'ENOENT') throw error; inference = await interpolate({ frames: ordered.map(k => path.join(outputRoot, k.file)), outputDirectory: inferenceRoot, factor, gpu: config.gpu ?? 0, temporalTta: true }); }
    if (inference.outputFrames !== (order.length - 1) * factor + 1 || inference.keyframeChecks?.length !== ordered.length || !inference.keyframeChecks.every(k => k.exactRgbMatch)) throw Error('Interpolation source mapping failed.');
    if (refinementDivisions > 1) {
      // Short motion intervals keep the second inference pass local. The V9
      // artwork is still present verbatim at every original key position.
      const step = factor / refinementDivisions, anchors = [];
      for (let index = 0; index < inference.outputFrames; index += step) anchors.push(path.join(inferenceRoot, String(index).padStart(5, '0') + '.png'));
      const refinedRoot = path.join(outputRoot, 'refined-' + fingerprint); let refined;
      try { refined = JSON.parse(await fs.readFile(path.join(refinedRoot, 'interpolation-report.json'), 'utf8')); }
      catch (error) { if (error.code !== 'ENOENT') throw error; refined = await interpolate({ frames: anchors, outputDirectory: refinedRoot, factor: step, gpu: config.gpu ?? 0, temporalTta: true }); }
      if (refined.outputFrames !== inference.outputFrames || refined.keyframeChecks?.length !== anchors.length || !refined.keyframeChecks.every(k => k.exactRgbMatch)) throw Error('Refined interpolation source mapping failed.');
      inferenceRoot = refinedRoot;
    }
    const materialized = new Map(), corrected = [];
    async function materialize(index) {
      if (materialized.has(index)) return materialized.get(index);
      if (index % factor === 0) { const file = ordered[index / factor].file; materialized.set(index, file); return file; }
      const file = 'frames/wink-' + fingerprint + '-' + String(index).padStart(5, '0') + '.png', target = path.join(outputRoot, file), source = path.join(inferenceRoot, String(index).padStart(5, '0') + '.png');
      const nearest = Math.round(index / factor), distance = Math.abs(index - nearest * factor);
      if (endpointWindow >= 2 && distance < endpointWindow) {
        const args = { basePath: path.join(outputRoot, ordered[nearest].file), framePath: source, index: distance, window: endpointWindow, outputPath: target };
        if (endpointMode === 'residual') {
          const direction = Math.sign(index - nearest * factor), neighbor = d => path.join(inferenceRoot, String(nearest * factor + direction * d).padStart(5, '0') + '.png');
          await correctWinkEndpoint({ ...args, near1Path: neighbor(1), near2Path: neighbor(2) });
        } else await correctSleepEndpoint(args);
        corrected.push(index);
      }
      else await fs.copyFile(source, target);
      materialized.set(index, file); return file;
    }
    let sampledIndices = samples.map(sample => ({ ...sample, index: Math.round(sample.phase * factor) }));
    if (config.motionSpacing === 'wink-eye') {
      // RIFE's time parameter is not uniform eyelid travel. Measure the actual
      // local path, then sample a monotone timing curve in distance space.
      // This only changes which existing pose is shown when; it does not warp
      // the eye, invent gaze motion, or change any authored key timestamp.
      const distances = [0]; let previous;
      for (let index = 0; index <= (order.length - 1) * factor; index++) {
        const file = await materialize(index);
        const pixels = await sharp(path.join(outputRoot, file)).extract({ left: 110, top: 285, width: 130, height: 90 }).flatten({ background: '#ffffff' }).removeAlpha().raw().toBuffer();
        if (previous) {
          let difference = 0;
          for (let p = 0; p < pixels.length; p++) difference += Math.abs(pixels[p] - previous[p]);
          distances.push(distances.at(-1) + difference / pixels.length);
        }
        previous = pixels;
      }
      const distanceKnots = knots.map(([time, phase]) => [time, distances[Math.round(phase * factor)]]);
      const distanceSamples = timingSamples(distanceKnots, sampleFps, timing, { zeroEndpointVelocity });
      function nearestDistance(value) {
        let lo = 0, hi = distances.length - 1;
        while (lo < hi) { const mid = (lo + hi) >>> 1; if (distances[mid] < value) lo = mid + 1; else hi = mid; }
        return lo && value - distances[lo - 1] < distances[lo] - value ? lo - 1 : lo;
      }
      sampledIndices = distanceSamples.map(sample => {
        const key = knots.find(([time]) => time === sample.timeMs);
        return { ...sample, index: key ? Math.round(key[1] * factor) : nearestDistance(sample.phase) };
      });
      normalization.motionSpacing = config.motionSpacing;
      normalization.distanceKnots = distanceKnots;
      normalization.segmentDistances = order.slice(1).map((id, i) => ({ from: order[i], to: id, distance: distances[(i + 1) * factor] - distances[i * factor] }));
      await fs.writeFile(path.join(outputRoot, 'normalization.json'), JSON.stringify(normalization, null, 2) + '\n');
    } else if (config.motionSpacing) throw Error('Unknown motion spacing.');
    const frames = [];
    for (const sample of sampledIndices) frames.push({ file: await materialize(sample.index), durationMs: sample.durationMs });
    if (frames.some(f => f.durationMs <= 0) || duration({ frames }) !== durationMs) throw Error('Invalid sampled timeline duration.');
    const manifest = { version, candidateOnly: true, name: config.name, canvas: { width: 512, height: 512 }, clips: { idle: { loop: true, frames: [{ file: 'keyframes/idle.png', durationMs: 1000 }] }, wink: { loop: false, frames } }, motion: { revision, sampleFps, timing } };
    const report = await qa(outputRoot, manifest.clips.wink);
    await fs.writeFile(path.join(outputRoot, 'candidate-manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
    const active = JSON.parse(await fs.readFile(path.join(production, 'manifest.json'), 'utf8'));
    const clips = await preview(outputRoot, manifest, active.clips.wink, config);
    const gifReport = options.noGif ? null : await gif(outputRoot, clips);
    await fs.writeFile(path.join(outputRoot, 'qa.json'), JSON.stringify({ ...report, fingerprint, corrected, timing, zeroEndpointVelocity, knots,
      nominalSampleFps: sampleFps, exactTimestampSlotsAdded: samples.length - Math.round(durationMs * sampleFps / 1000),
      forwardPhasePath: knots.every((k, i) => i === 0 || k[1] >= knots[i - 1][1]),
      gif: gifReport, previewLabels: clips.labels, comparedTo: config.compareTo || 'production', selection: path.resolve(selection) }, null, 2) + '\n');
    return { outputRoot, candidateOnly: true, manifest: path.join(outputRoot, 'candidate-manifest.json'), preview: path.join(outputRoot, 'before-after.html'), gif: gifReport?.file, durationMs, uniquePngs: report.uniquePngs };
  } finally {
    const after = await protectedHashes(), unchanged = JSON.stringify(before) === JSON.stringify(after);
    await fs.writeFile(path.join(outputRoot, 'production-protection.json'), JSON.stringify({ checkedAt: new Date().toISOString(), unchanged, before, after }, null, 2) + '\n');
    if (!unchanged) throw Error('Production assets changed during candidate build. Inspect production-protection.json.');
  }
}

module.exports = { build, phaseAt, makePhaseSampler, timingSamples, candidateLocation, fileAt, preview, gif };
if (require.main === module) {
  const args = process.argv.slice(2), index = args.indexOf('--selection');
  const selection = index >= 0 ? args[index + 1] : args.find(a => !a.startsWith('--'));
  if (!selection) { console.error('Usage: node tools/build-wink-candidate.cjs --selection selection.json [--normalize-only|--preview-only] [--stage-only] [--no-gif]'); process.exitCode = 1; }
  else if (args.includes('--normalize-only') && args.includes('--preview-only')) { console.error('--normalize-only and --preview-only are mutually exclusive.'); process.exitCode = 1; }
  else build(selection, { normalizeOnly: args.includes('--normalize-only'), previewOnly: args.includes('--preview-only'), noGif: args.includes('--no-gif') }).then(r => console.log(JSON.stringify(r, null, 2))).catch(error => { console.error(error); process.exitCode = 1; });
}
