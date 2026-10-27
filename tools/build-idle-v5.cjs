// Build local idle expressions with model-created keys and the existing RIFE tool.
// The original eight clips are preserved; no new runtime rendering engine is used.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');
const { interpolate } = require('./motion-interpolate.cjs');
const { normalize: normalizeV5, faceMask, idleFile } = require('./normalize-idle-v5.cjs');
const { correctSleepEndpoint: correctEndpoint } = require('./sleep-endpoint.cjs');
const repo = path.resolve(__dirname, '..');
const root = path.join(repo, 'assets/strawberry-cat');
const work = path.join(repo, 'artifacts/idle-v6');
const previousWork = path.join(repo, 'artifacts/idle-v5');
const sha = value => crypto.createHash('sha256').update(value).digest('hex');
const factor = 64, sampleFps = 240, endpointWindow = 4;
const recipes = {
  yawn: {
    keys: [idleFile, 'keyframes-v5/shared/half-eyes.png', 'keyframes-v5/shared/low-eyes.png',
      'keyframes-v5/shared/closed-eyes.png', 'keyframes-v5/yawn/yawn-start-v1.png',
      'keyframes-v5/yawn/yawn-middle-v1.png', 'keyframes-v5/yawn/yawn-peak-v1.png'],
    mask: 'production', durationMs: 4400, peakAtMs: 2480,
    knots: [[0, 0], [560, 3], [2240, 6], [2800, 6], [3900, 3], [4400, 0]]
  },
  happy: {
    keys: [idleFile, 'keyframes-v6/happy/happy-quarter-v2.png', 'keyframes-v6/happy/happy-half-v2.png', 'keyframes-v6/happy/happy-peak-v1.png'],
    mask: 'eyes-v6', frameDirectory: 'frames/v6', durationMs: 1500, peakAtMs: 700,
    knots: [[0, 0], [380, 3], [1030, 3], [1500, 0]]
  },
  wink: {
    keys: [idleFile, 'keyframes-v6/wink/wink-half-v2.png', 'keyframes-v6/wink/wink-peak-v3.png'],
    mask: 'eyes-v6', frameDirectory: 'frames/v6', durationMs: 1060, peakAtMs: 450,
    knots: [[0, 0], [180, 2], [780, 2], [1060, 0]]
  }
};

function phaseAt(time, knots) {
  if (time >= knots.at(-1)[0]) return knots.at(-1)[1];
  let segment = 0;
  while (segment < knots.length - 2 && time >= knots[segment + 1][0]) segment++;
  const [start, a] = knots[segment], [end, b] = knots[segment + 1];
  const t = (time - start) / (end - start), eased = t * t * (3 - 2 * t);
  return a + (b - a) * eased;
}

async function registerInferred(input, target, base, matte) {
  const inferred = await sharp(input).ensureAlpha().raw().toBuffer();
  const layer = await sharp(inferred, { raw: { width: 512, height: 512, channels: 4 } })
    .composite([{ input: matte, raw: { width: 512, height: 512, channels: 4 }, blend: 'dest-in' }]).png().toBuffer();
  const output = await sharp(base, { raw: { width: 512, height: 512, channels: 4 } })
    .composite([{ input: layer }]).raw().toBuffer();
  // Preserve unchanged original pixels, including invisible RGB, outside the face.
  for (let p = 0; p < 512 * 512; p++) if (!matte[p * 4 + 3])
    base.copy(output, p * 4, p * 4, p * 4 + 4);
  await sharp(output, { raw: { width: 512, height: 512, channels: 4 } }).png().toFile(target);
}

async function restoreFixedPixels(file, base, matte) {
  // FFmpeg float conversion can round an unchanged channel by one level. Restore
  // protected pixels after that final operation, including transparent RGB.
  const output = await sharp(file).ensureAlpha().raw().toBuffer();
  for (let p = 0; p < 512 * 512; p++) if (!matte[p * 4 + 3])
    base.copy(output, p * 4, p * 4, p * 4 + 4);
  await sharp(output, { raw: { width: 512, height: 512, channels: 4 } }).png().toFile(file);
}

async function buildIdleExpressions(manifest) {
  const existing = Object.fromEntries(Object.entries(manifest.clips).filter(([name]) => !recipes[name])
    .map(([name, clip]) => [name, JSON.stringify(clip)]));
  if (manifest.clips.idle.frames[0].file !== idleFile) throw Error('The shared idle pose changed. Review registration first.');
  await normalizeV5();
  const { normalize: normalizeV6 } = require('./normalize-idle-v6.cjs');
  await normalizeV6();
  await fs.mkdir(work, { recursive: true });
  await fs.mkdir(path.join(root, 'frames/v5'), { recursive: true });
  const base = await sharp(path.join(root, idleFile)).ensureAlpha().raw().toBuffer();
  const reports = {};
  for (const [name, recipe] of Object.entries(recipes)) {
    const frameDirectory = recipe.frameDirectory || 'frames/v5';
    await fs.mkdir(path.join(root, frameDirectory), { recursive: true });
    const sourceHashes = Object.fromEntries(await Promise.all(recipe.keys.map(async file => [file, sha(await fs.readFile(path.join(root, file)))])));
    const fingerprint = sha(Buffer.from(JSON.stringify({ sourceHashes, factor, temporalTta: true, engine: 'RIFE4.6' })));
    const cacheName = 'rife-' + name + '-' + fingerprint.slice(0, 12);
    let output = path.join(work, cacheName);
    let interpolation;
    try { interpolation = JSON.parse(await fs.readFile(path.join(output, 'interpolation-report.json'), 'utf8')); }
    catch (error) {
      if (error.code !== 'ENOENT') throw error;
      // Reuse the exact earlier inference when only timing changed, as for yawn.
      const previousOutput = path.join(previousWork, cacheName);
      try {
        interpolation = JSON.parse(await fs.readFile(path.join(previousOutput, 'interpolation-report.json'), 'utf8'));
        output = previousOutput;
      } catch (previousError) {
        if (previousError.code !== 'ENOENT') throw previousError;
        console.log('Interpolating ' + name + ': ' + recipe.keys.length + ' registered keys, factor ' + factor);
        interpolation = await interpolate({ frames: recipe.keys.map(file => path.join(root, file)), outputDirectory: output, factor, gpu: 0, temporalTta: true });
      }
    }
    const expectedCount = (recipe.keys.length - 1) * factor + 1;
    if (interpolation.outputFrames !== expectedCount) throw Error('Unexpected interpolation count: ' + name);
    if (interpolation.keyframeChecks?.length !== recipe.keys.length || !interpolation.keyframeChecks.every(check => check.exactRgbMatch))
      throw Error('RIFE keyframe mapping was not verified: ' + name);
    const stage = path.join(output, 'registered');
    await fs.mkdir(stage, { recursive: true });
    const matte = (await faceMask(recipe.mask, base, base)).data;
    // A separately authored return path can end at another copy of the idle
    // key. Keep all exact idle keys on the canonical file for runtime joins.
    const files = new Map(recipe.keys.flatMap((file, index) => file === idleFile ? [[index * factor, idleFile]] : []));
    const correctedPhases = [];
    async function materialize(index) {
      if (files.has(index)) return files.get(index);
      const relative = frameDirectory + '/' + name + '-' + String(index).padStart(5, '0') + '.png';
      const destination = path.join(root, relative);
      if (index % factor === 0) {
        await fs.copyFile(path.join(root, recipe.keys[index / factor]), destination);
      } else {
        const registered = path.join(stage, String(index).padStart(5, '0') + '.png');
        await registerInferred(path.join(output, String(index).padStart(5, '0') + '.png'), registered, base, matte);
        const closestKey = Math.round(index / factor), distance = Math.abs(index - closestKey * factor);
        if (distance < endpointWindow) {
          await correctEndpoint({ basePath: path.join(root, recipe.keys[closestKey]), framePath: registered,
            index: distance, window: endpointWindow, outputPath: destination });
          await restoreFixedPixels(destination, base, matte);
          correctedPhases.push(index);
        } else await fs.copyFile(registered, destination);
      }
      files.set(index, relative);
      return relative;
    }
    const count = Math.round(recipe.durationMs * sampleFps / 1000), frames = [];
    for (let i = 0; i < count; i++) {
      const phase = Math.round(phaseAt(i * recipe.durationMs / (count - 1), recipe.knots) * factor);
      frames.push({ file: await materialize(phase),
        durationMs: Math.round((i + 1) * recipe.durationMs / count) - Math.round(i * recipe.durationMs / count) });
    }
    if (frames[0].file !== idleFile || frames.at(-1).file !== idleFile) throw Error('Idle endpoint changed: ' + name);
    manifest.clips[name] = { loop: false, frames };
    reports[name] = { ...recipe, factor, sampleFps, sourceHashes, interpolationDirectory: output,
      frameSlots: frames.length, uniquePngs: files.size, correctedPhases,
      exactIdleEndpoints: true, keyframeMappingVerified: true };
    console.log('Prepared ' + name + ': ' + frames.length + ' slots, ' + files.size + ' PNGs, ' + recipe.durationMs + ' ms.');
  }
  if (!Object.entries(existing).every(([name, before]) => JSON.stringify(manifest.clips[name]) === before)) throw Error('An existing clip changed.');
  manifest.version = Math.max(6, manifest.version || 1);
  manifest.idleActions = [{ clip: 'blink', weight: 4 }, { clip: 'yawn', weight: 1 }, { clip: 'happy', weight: 1 }, { clip: 'wink', weight: 1 }];
  manifest.motion = { ...manifest.motion, revision: '0.3.0-preview.2', sampleFps,
    idleExpressions: { generator: 'built-in image_gen', interpolation: 'RIFE 4.6 temporal TTA; dual-matte RGBA',
      registration: 'Model-drawn face on unchanged approved idle body; original paws, tail and decoration retained',
      endpointCorrection: 'FFmpeg float premultiplied-alpha smoothstep, at most 3 inferred phases beside each exact key',
      clips: Object.fromEntries(Object.entries(reports).map(([name, r]) => [name, { durationMs: r.durationMs, peakAtMs: r.peakAtMs, uniquePngs: r.uniquePngs, frameSlots: r.frameSlots }])) } };
  const report = { createdAt: new Date().toISOString(), revision: manifest.motion.revision,
    existingClipsUnchanged: true, oldClipNames: Object.keys(existing), idleActions: manifest.idleActions, clips: reports };
  await fs.writeFile(path.join(work, 'build-report.json'), JSON.stringify(report, null, 2) + '\n');
  await fs.writeFile(path.join(root, 'keyframes-v5/recipe.json'), JSON.stringify({ sampleFps, factor, endpointWindow, recipes }, null, 2) + '\n');
  await fs.writeFile(path.join(root, 'keyframes-v6/recipe.json'), JSON.stringify({ sampleFps, factor, endpointWindow, recipes }, null, 2) + '\n');
  return report;
}

module.exports = { buildIdleExpressions, recipes, phaseAt };
if (require.main === module) (async () => {
  const file = path.join(root, 'manifest.json'), manifest = JSON.parse(await fs.readFile(file, 'utf8'));
  await buildIdleExpressions(manifest);
  await fs.writeFile(file, JSON.stringify(manifest, null, 2) + '\n');
})().catch(error => { console.error(error); process.exitCode = 1; });
