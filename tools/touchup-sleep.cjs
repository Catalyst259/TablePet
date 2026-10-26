// Model-created sleep pose -> established RIFE interpolation -> restrained breathing loop.
// All other gestures and the shared asleep/wake boundary remain unchanged.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');
const { interpolate } = require('./motion-interpolate.cjs');
const { correctSleepEndpoint: stabilizeSleepEndpoint } = require('./sleep-endpoint.cjs');
const repo = path.resolve(__dirname, '..');
const root = path.join(repo, 'assets/strawberry-cat');
const work = path.join(repo, 'artifacts/sleep-v4');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const baselinePath = path.join(repo, 'reference/motion-v3/manifest.json');

function frameAt(clip, time) {
  const duration = clip.frames.reduce((sum, frame) => sum + frame.durationMs, 0);
  time = clip.loop ? time % duration : Math.min(time, duration - 1);
  let end = 0;
  for (const frame of clip.frames) { end += frame.durationMs; if (time < end) return frame.file; }
  return clip.frames.at(-1).file;
}

async function smoothSleep(manifest) {
  await fs.mkdir(work, { recursive: true });
  const baseline = JSON.parse(await fs.readFile(baselinePath, 'utf8'));
  const config = JSON.parse(await fs.readFile(path.join(root, 'keyframes-v4/sleep/recipe.json'), 'utf8'));
  const otherClips = Object.fromEntries(Object.entries(manifest.clips).filter(([name]) => name !== 'sleep_loop').map(([name, clip]) => [name, JSON.stringify(clip)]));
  const rest = baseline.clips.sleep_loop.frames[0].file;
  if (manifest.clips.fall_asleep.frames.at(-1).file !== rest || manifest.clips.wake.frames[0].file !== rest)
    throw Error('Shared sleep/wake endpoint changed; breathing must start on the exact resting sprite.');
  const keys = [rest, config.inhaleMaster];
  const sourceHashes = Object.fromEntries(await Promise.all(keys.map(async file => [file, hash(await fs.readFile(path.join(root, file)))])));
  const fingerprint = hash(Buffer.from(JSON.stringify({ sourceHashes, factor: config.factor, temporalTta: true, engine: 'RIFE4.6' })));
  const output = path.join(work, 'rife-' + fingerprint.slice(0, 12));
  let interpolation;
  try { interpolation = JSON.parse(await fs.readFile(path.join(output, 'interpolation-report.json'), 'utf8')); }
  catch { interpolation = await interpolate({ frames: keys.map(file => path.join(root, file)), outputDirectory: output, factor: config.factor, gpu: 0, temporalTta: true }); }
  if (!(config.peakPhase > 0 && config.peakPhase <= 1) || !(config.durationMs > 0)) throw Error('Invalid breathing recipe.');
  const count = Math.round(config.durationMs * config.sampleFps / 1000);
  const materialized = new Map([[0, rest]]);
  await fs.mkdir(path.join(root, 'frames/v4'), { recursive: true });
  const frames = [];
  for (let i = 0; i < count; i++) {
    // Zero velocity at inhale peak and at the repeat boundary; no fixed-pose pause.
    const phase = config.peakPhase * (1 - Math.cos(2 * Math.PI * i / (count - 1))) / 2;
    const index = Math.round(phase * config.factor);
    if (!materialized.has(index)) {
      const file = 'frames/v4/sleep-breath-' + String(index).padStart(4, '0') + '.png';
      await stabilizeSleepEndpoint({
        basePath: path.join(root, rest), framePath: path.join(output, String(index).padStart(5, '0') + '.png'),
        index, window: config.endpointWindow, outputPath: path.join(root, file)
      });
      materialized.set(index, file);
    }
    frames.push({ file: materialized.get(index), durationMs: Math.round((i + 1) * config.durationMs / count) - Math.round(i * config.durationMs / count) });
  }
  manifest.clips.sleep_loop = { loop: true, frames };
  manifest.version = Math.max(manifest.version || 1, 4);
  manifest.motion = { ...manifest.motion, revision: '0.2.2', sampleFps: config.sampleFps,
    sleepBreathing: { cycleMs: config.durationMs, peakPhase: config.peakPhase, keyframeSource: 'built-in image_gen', interpolation: 'RIFE 4.6 temporal TTA; RGBA dual matte', endpointCorrection: config.endpointCorrection } };
  const otherClipsUnchanged = Object.entries(otherClips).every(([name, before]) => JSON.stringify(manifest.clips[name]) === before);
  if (!otherClipsUnchanged || frames[0].file !== rest || frames.at(-1).file !== rest) throw Error('Breathing changed a shared boundary or another gesture.');
  const report = { checkedAt: new Date().toISOString(), sourceHashes, recipe: config, interpolationDirectory: output,
    originalUniquePngs: new Set(baseline.clips.sleep_loop.frames.map(frame => frame.file)).size,
    frameSlots: frames.length, uniquePngs: materialized.size, newPngs: materialized.size - 1,
    durationMs: frames.reduce((sum, frame) => sum + frame.durationMs, 0), otherClipsUnchanged,
    sharedBoundaryPreserved: true, loopBoundaryExact: frames[0].file === frames.at(-1).file };
  await previews(baseline.clips.sleep_loop, manifest);
  await fs.writeFile(path.join(work, 'report.json'), JSON.stringify(report, null, 2) + '\n');
  return report;
}

async function previews(oldClip, manifest) {
  const preview = path.join(root, 'previews');
  await fs.mkdir(preview, { recursive: true });
  const clip = manifest.clips.sleep_loop, duration = clip.frames.reduce((sum, frame) => sum + frame.durationMs, 0);
  const backgrounds = ['#f9f2ee', '#26313f'];
  const contact = [];
  for (let row = 0; row < 2; row++) for (let col = 0; col < 8; col++) {
    const file = frameAt(clip, duration * col / 8);
    contact.push({ input: await sharp(path.join(root, file)).resize(240, 240).flatten({ background: backgrounds[row] }).png().toBuffer(), left: col * 240, top: row * 264 + 24 });
    contact.push({ input: Buffer.from(`<svg width="240" height="24"><rect width="240" height="24" fill="${backgrounds[row]}"/><text x="12" y="18" font-size="13" fill="${row ? '#fff' : '#4a3942'}">${Math.round(duration * col / 8)} ms</text></svg>`), left: col * 240, top: row * 264 });
  }
  await sharp({ create: { width: 1920, height: 528, channels: 4, background: backgrounds[0] } }).composite(contact).png().toFile(path.join(preview, 'sleep-breathing-contact.png'));
  const cache = new Map(), comparison = [], solo = [];
  async function resized(file, size) {
    const id = file + ':' + size;
    if (!cache.has(id)) cache.set(id, await sharp(path.join(root, file)).resize(size, size).png().toBuffer());
    return cache.get(id);
  }
  for (let t = 0; t < duration; t += 20) {
    const before = frameAt(oldClip, t * 3600 / duration), after = frameAt(clip, t);
    comparison.push(await sharp({ create: { width: 480, height: 264, channels: 4, background: backgrounds[0] } }).composite([
      { input: await resized(before, 240), left: 0, top: 24 }, { input: await resized(after, 240), left: 240, top: 24 },
      { input: Buffer.from('<svg width="480" height="24"><text x="92" y="18" font-size="14" fill="#4a3942">Before</text><text x="338" y="18" font-size="14" fill="#4a3942">After</text></svg>'), left: 0, top: 0 }
    ]).raw().toBuffer());
    solo.push(await sharp(await resized(after, 320)).flatten({ background: backgrounds[0] }).ensureAlpha().raw().toBuffer());
  }
  await sharp(Buffer.concat(comparison), { raw: { width: 480, height: comparison.length * 264, channels: 4, pageHeight: 264 } }).gif({ loop: 0, delay: 20, colours: 256, dither: 0 }).toFile(path.join(preview, 'sleep-breathing-comparison.gif'));
  await sharp(Buffer.concat(solo), { raw: { width: 320, height: solo.length * 320, channels: 4, pageHeight: 320 } }).gif({ loop: 0, delay: 20, colours: 256, dither: 0 }).toFile(path.join(preview, 'sleep-breathing.gif'));
  // Existing browser preview reads these exact PNGs and still follows screen refresh.
  const htmlPath = path.join(preview, 'motion-preview.html');
  const html = await fs.readFile(htmlPath, 'utf8').catch(() => null);
  if (html) {
    const start = html.indexOf('const manifest=') + 'const manifest='.length;
    const end = html.indexOf(',images=new Map()', start);
    if (start >= 'const manifest='.length && end > start) await fs.writeFile(htmlPath, html.slice(0, start) + JSON.stringify(manifest) + html.slice(end));
  }
}

module.exports = { smoothSleep };
if (require.main === module) (async () => {
  const file = path.join(root, 'manifest.json');
  const manifest = JSON.parse(await fs.readFile(file, 'utf8'));
  const report = await smoothSleep(manifest);
  await fs.writeFile(file, JSON.stringify(manifest, null, 2) + '\n');
  console.log(JSON.stringify(report, null, 2));
})().catch(error => { console.error(error); process.exitCode = 1; });
