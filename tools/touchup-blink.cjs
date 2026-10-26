// Small blink-only refinement: reuse existing poses and the established RIFE tool.
// Other clips and their PNGs are never rewritten by this tool.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');
const { interpolate } = require('./motion-interpolate.cjs');
const repo = path.resolve(__dirname, '..');
const root = path.join(repo, 'assets/strawberry-cat');
const baselinePath = path.join(repo, 'reference/motion-v2/manifest.json');
const work = path.join(repo, 'artifacts/blink-touchup');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');

function applyBlinkMetadata(manifest) {
  manifest.version = Math.max(3, Number(manifest.version) || 0);
  const revision = manifest.motion?.revision;
  const parts = /^(\d+)\.(\d+)\.(\d+)/.exec(revision || '');
  const older = parts && Number(parts[1]) === 0 && (Number(parts[2]) < 2 || (Number(parts[2]) === 2 && Number(parts[3]) < 1));
  manifest.motion = { ...manifest.motion, revision: !revision || older ? '0.2.1' : revision,
    blinkPreserved: false, blinkKeyPosesPreserved: true, blinkTransitionFramesAdded: 20 };
}

async function smoothBlink(manifest) {
  const nonBlinkSnapshot = () => JSON.stringify(Object.fromEntries(Object.entries(manifest.clips).filter(([name]) => name !== 'blink')));
  const otherClipsBefore = nonBlinkSnapshot();
  await fs.mkdir(path.dirname(baselinePath), { recursive: true });
  try { await fs.access(baselinePath); }
  catch { await fs.writeFile(baselinePath, JSON.stringify(manifest, null, 2) + '\n'); }
  const baseline = JSON.parse(await fs.readFile(baselinePath, 'utf8'));
  const originals = baseline.clips.blink.frames.map(f => f.file);
  const sourceHashes = Object.fromEntries(await Promise.all([...new Set(originals)].map(async file => [file, hash(await fs.readFile(path.join(root, file)))])));
  const factor = 5;
  const fingerprint = hash(Buffer.from(JSON.stringify({ sourceHashes, factor, temporalTta: true })));
  const output = path.join(work, 'rife-' + fingerprint.slice(0, 12));
  await fs.mkdir(work, { recursive: true });
  let interpolation;
  try { interpolation = JSON.parse(await fs.readFile(path.join(output, 'interpolation-report.json'), 'utf8')); }
  catch { interpolation = await interpolate({ frames: originals.map(f => path.join(root, f)), outputDirectory: output, factor, gpu: 0, temporalTta: true }); }
  const files = [];
  await fs.mkdir(path.join(root, 'frames/v3'), { recursive: true });
  for (let i = 0; i < interpolation.outputFrames; i++) {
    if (i % factor === 0) { files.push(originals[i / factor]); continue; }
    const file = 'frames/v3/blink-' + String(i).padStart(2, '0') + '.png';
    await fs.copyFile(path.join(output, String(i).padStart(5, '0') + '.png'), path.join(root, file));
    files.push(file);
  }
  // Retain the 375 ms gesture and 90 ms closed pause. Each source-pose interval
  // receives four in-betweens; the application still samples the timeline at 240 Hz.
  const times = [0, 50, 110, 155, 245, 305, 375];
  const phases = [0, 1, 2, 3, 3, 4, 5];
  function phaseAt(time) {
    if (time >= 375) return 5;
    let segment = 0;
    while (segment < times.length - 2 && time >= times[segment + 1]) segment++;
    const t = (time - times[segment]) / (times[segment + 1] - times[segment]);
    return phases[segment] + t * (phases[segment + 1] - phases[segment]);
  }
  const frames = [];
  // Insert exact closed-pause boundaries into the nominal 240 Hz samples.
  // Nearest-pose sampling alone would accidentally extend the closed eyes to 100 ms.
  const edges = [...new Set([...Array.from({ length: 91 }, (_, i) => Math.round(i * 375 / 90)), 155, 245])].sort((a, b) => a - b);
  for (let i = 0; i < edges.length - 1; i++) {
    const time = edges[i];
    let index = Math.round(phaseAt(time) * factor);
    if (time < 155) index = Math.min(14, index);
    else if (time < 245) index = 15;
    else index = Math.max(16, index);
    frames.push({ file: files[index], durationMs: edges[i + 1] - time });
  }
  manifest.clips.blink = { loop: false, frames };
  applyBlinkMetadata(manifest);
  const otherClipsUnchanged = nonBlinkSnapshot() === otherClipsBefore;
  if (!otherClipsUnchanged) throw Error('Non-blink clip changed; refusing blink-only update.');
  const report = { checkedAt: new Date().toISOString(), sourceHashes, originalFrameSlots: baseline.clips.blink.frames.length,
    frameSlots: frames.length, uniquePngs: new Set(frames.map(f => f.file)).size, addedPngs: files.filter(f => f.startsWith('frames/v3/')).length,
    durationMs: frames.reduce((s, f) => s + f.durationMs, 0), intendedClosedPauseMs: 90,
    displayedFullyClosedMs: frames.filter(f => f.file === originals[3]).reduce((s, f) => s + f.durationMs, 0), otherClipsUnchanged,
    sourceKeyframesPreserved: originals.every(file => frames.some(frame => frame.file === file)), interpolationDirectory: output };
  await fs.writeFile(path.join(work, 'report.json'), JSON.stringify(report, null, 2) + '\n');
  await previews(baseline.clips.blink, manifest.clips.blink, files);
  return report;
}

function frameAt(clip, time) {
  let end = 0;
  for (const frame of clip.frames) { end += frame.durationMs; if (time < end) return frame.file; }
  return clip.frames.at(-1).file;
}
async function previews(oldClip, newClip, allFiles) {
  const preview = path.join(root, 'previews');
  const layers = [];
  const cell = 220, columns = 6, rowHeight = 246;
  const rows = [oldClip.frames.map(f => f.file), ...Array.from({ length: Math.ceil(allFiles.length / columns) }, (_, i) => allFiles.slice(i * columns, (i + 1) * columns))];
  for (let row = 0; row < rows.length; row++) for (let col = 0; col < rows[row].length; col++) {
    const file = rows[row][col], background = row % 2 ? '#26313f' : '#f9f2ee', text = row % 2 ? '#fff' : '#4a3942';
    layers.push({ input: await sharp(path.join(root, file)).resize(cell, cell).flatten({ background }).png().toBuffer(), left: col * cell, top: row * rowHeight + 26 });
    const label = row === 0 ? `Original ${col}` : `Smooth ${(row - 1) * columns + col}`;
    layers.push({ input: Buffer.from(`<svg width="${cell}" height="26"><rect width="100%" height="100%" fill="${background}"/><text x="8" y="18" font-size="14" fill="${text}">${label}</text></svg>`), left: col * cell, top: row * rowHeight });
  }
  await sharp({ create: { width: cell * columns, height: rows.length * rowHeight, channels: 4, background: '#929292' } }).composite(layers).png().toFile(path.join(preview, 'blink-touchup-contact.png'));
  const raw = [];
  for (let t = 0; t < 1400; t += 20) {
    const time = t < 500 ? 0 : Math.min(t - 500, 374);
    const pair = [];
    for (const [col, clip] of [oldClip, newClip].entries()) pair.push({ input: await sharp(path.join(root, frameAt(clip, time))).resize(256, 256).png().toBuffer(), left: col * 256, top: 22 });
    pair.push({ input: Buffer.from('<svg width="512" height="22"><text x="90" y="17" font-size="14" fill="#4a3942">Before</text><text x="350" y="17" font-size="14" fill="#4a3942">After</text></svg>'), left: 0, top: 0 });
    raw.push(await sharp({ create: { width: 512, height: 278, channels: 4, background: '#f9f2ee' } }).composite(pair).raw().toBuffer());
  }
  await sharp(Buffer.concat(raw), { raw: { width: 512, height: raw.length * 278, channels: 4, pageHeight: 278 } }).gif({ loop: 0, delay: 20, colours: 256, dither: .1 }).toFile(path.join(preview, 'blink-touchup.gif'));
  // Keep the existing interactive preview's embedded manifest current as well.
  const htmlPath = path.join(preview, 'motion-preview.html');
  const html = await fs.readFile(htmlPath, 'utf8').catch(() => null);
  if (html) {
    const start = html.indexOf('const manifest=') + 'const manifest='.length;
    const end = html.indexOf(',images=new Map()', start);
    if (start >= 'const manifest='.length && end > start) {
      const embedded = JSON.parse(html.slice(start, end)); embedded.clips.blink = newClip;
      applyBlinkMetadata(embedded);
      await fs.writeFile(htmlPath, html.slice(0, start) + JSON.stringify(embedded) + html.slice(end));
    }
  }
}
module.exports = { smoothBlink };
if (require.main === module) (async () => {
  const file = path.join(root, 'manifest.json');
  const manifest = JSON.parse(await fs.readFile(file, 'utf8'));
  const report = await smoothBlink(manifest);
  await fs.writeFile(file, JSON.stringify(manifest, null, 2) + '\n');
  console.log(JSON.stringify(report, null, 2));
})().catch(error => { console.error(error); process.exitCode = 1; });
