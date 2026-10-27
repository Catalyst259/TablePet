// Complete upper-body idle actions using generated RGBA poses and upstream RIFE.
// node tools/build-idle-v7.cjs [selection.json] [--only=yawn,wink] [--stage-only]
// Use --normalize-only to inspect keys before running any inference.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { interpolate } = require('./motion-interpolate.cjs');
const { correctSleepEndpoint: correctEndpoint } = require('./sleep-endpoint.cjs');
const { normalize, loadSelection, verifyBaseline, root, work, idleFile, selectionPath } = require('./normalize-idle-v7.cjs');
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const sampleFps = 240, defaultFactor = 64, endpointWindow = 4;
const timing = {
  yawn: { durationMs: 4400, peakStartMs: 1800, peakEndMs: 2400 },
  wink: { durationMs: 1400, peakStartMs: 360, peakEndMs: 960 }
};

function phaseAt(time, knots) {
  if (time >= knots.at(-1)[0]) return knots.at(-1)[1];
  let segment = 0;
  while (segment < knots.length - 2 && time >= knots[segment + 1][0]) segment++;
  const [start, from] = knots[segment], [end, to] = knots[segment + 1];
  const t = (time - start) / (end - start), eased = t * t * (3 - 2 * t);
  return from + (to - from) * eased;
}

function makeRecipes(config, registered, selected) {
  const keyMap = new Map([['idle', idleFile], ...registered.results.map(key => [key.id, key.output])]);
  return Object.fromEntries(selected.map(name => {
    if (!timing[name]) throw Error('Unknown v7 action: ' + name);
    const spec = config.clips?.[name] || {};
    const ids = spec.keys || ['idle', ...registered.results.filter(key => key.action === name).map(key => key.id)];
    const keys = ids.map(id => { if (!keyMap.has(id)) throw Error('Missing normalized key id: ' + id); return keyMap.get(id); });
    if (keys.length < 2 || keys[0] !== idleFile) throw Error('Each action must begin at the canonical idle: ' + name);
    const durationMs = spec.durationMs ?? timing[name].durationMs;
    const peakIndex = spec.peakIndex ?? (keys.at(-1) === idleFile ? keys.length - 2 : keys.length - 1);
    const endPhase = keys.at(-1) === idleFile ? keys.length - 1 : 0;
    const knots = spec.knots || [[0, 0], [timing[name].peakStartMs, peakIndex], [timing[name].peakEndMs, peakIndex], [durationMs, endPhase]];
    const factor = spec.factor ?? config.factor ?? defaultFactor;
    if (!Number.isInteger(factor) || factor < 16 || factor > 1024 || !Number.isInteger(durationMs) || durationMs <= 0) throw Error('Invalid v7 duration/factor.');
    if (knots.length < 3 || knots[0][0] !== 0 || knots.at(-1)[0] !== durationMs) throw Error('Knots must cover the exact clip duration: ' + name);
    for (let i = 0; i < knots.length; i++) {
      if (knots[i].length !== 2 || !knots[i].every(Number.isFinite) || knots[i][1] < 0 || knots[i][1] > keys.length - 1 || (i && knots[i][0] <= knots[i - 1][0])) throw Error('Invalid phase knot: ' + name);
    }
    if (knots[0][1] !== 0 || keys[knots.at(-1)[1]] !== idleFile) throw Error('Both action boundaries must reference exact idle artwork: ' + name);
    return [name, { ids, keys, durationMs, knots, factor, peakIndex, peakAtMs: spec.peakAtMs ?? Math.round((timing[name].peakStartMs + timing[name].peakEndMs) / 2) }];
  }));
}

async function buildIdleExpressions(manifest, options = {}) {
  const preserved = await verifyBaseline(manifest);
  const config = await loadSelection(options.selection || selectionPath);
  const selected = options.only || ['yawn', 'wink'];
  if (!selected.length || selected.some(name => !Object.hasOwn(timing, name)))
    throw Error('The frozen 0.3.0 build supports only yawn and wink; blink is preserved from the original assets.');
  const normalization = await normalize(config, { only: selected });
  const recipes = makeRecipes(config, normalization, selected);
  if (options.normalizeOnly) return { normalization, recipes, inferenceRun: false };
  const next = structuredClone(manifest), reports = {};
  await fs.mkdir(path.join(root, 'frames/v7'), { recursive: true });
  for (const [name, recipe] of Object.entries(recipes)) {
    const sourceHashes = Object.fromEntries(await Promise.all(recipe.keys.map(async file => [file, sha(await fs.readFile(path.join(root, file)))])));
    const fingerprint = sha(Buffer.from(JSON.stringify({ sourceHashes, keys: recipe.keys, factor: recipe.factor, temporalTta: true,
      engine: 'RIFE4.6', registration: 'full-RGBA-paw-anchor-v7', endpointWindow })));
    const output = path.join(work, 'rife-' + name + '-' + fingerprint.slice(0, 12));
    let interpolation;
    try { interpolation = JSON.parse(await fs.readFile(path.join(output, 'interpolation-report.json'), 'utf8')); }
    catch (error) {
      if (error.code !== 'ENOENT') throw error;
      console.log('Interpolating complete ' + name + ' poses: ' + recipe.keys.length + ' keys, factor ' + recipe.factor);
      interpolation = await interpolate({ frames: recipe.keys.map(file => path.join(root, file)), outputDirectory: output,
        factor: recipe.factor, gpu: 0, temporalTta: true });
    }
    if (interpolation.outputFrames !== (recipe.keys.length - 1) * recipe.factor + 1 ||
      interpolation.keyframeChecks?.length !== recipe.keys.length || !interpolation.keyframeChecks.every(check => check.exactRgbMatch))
      throw Error('RIFE keyframe count or source-time mapping failed: ' + name);
    const files = new Map(recipe.keys.flatMap((file, index) => file === idleFile ? [[index * recipe.factor, idleFile]] : []));
    const correctedPhases = [];
    async function materialize(index) {
      if (files.has(index)) return files.get(index);
      const file = 'frames/v7/' + name + '-' + fingerprint.slice(0, 12) + '-' + String(index).padStart(5, '0') + '.png';
      const destination = path.join(root, file), inferred = path.join(output, String(index).padStart(5, '0') + '.png');
      if (index % recipe.factor === 0) await fs.copyFile(path.join(root, recipe.keys[index / recipe.factor]), destination);
      else {
        const closestKey = Math.round(index / recipe.factor), distance = Math.abs(index - closestKey * recipe.factor);
        // Correction affects only three inferred phases next to each exact key.
        // All channels and the entire moving contour remain part of the frame.
        if (distance < endpointWindow) {
          await correctEndpoint({ basePath: path.join(root, recipe.keys[closestKey]), framePath: inferred,
            index: distance, window: endpointWindow, outputPath: destination });
          correctedPhases.push(index);
        } else await fs.copyFile(inferred, destination);
      }
      files.set(index, file);
      return file;
    }
    const count = Math.round(recipe.durationMs * sampleFps / 1000), frames = [];
    for (let i = 0; i < count; i++) {
      const phase = Math.round(phaseAt(i * recipe.durationMs / (count - 1), recipe.knots) * recipe.factor);
      frames.push({ file: await materialize(phase), durationMs: Math.round((i + 1) * recipe.durationMs / count) - Math.round(i * recipe.durationMs / count) });
    }
    if (frames[0].file !== idleFile || frames.at(-1).file !== idleFile || frames.reduce((sum, frame) => sum + frame.durationMs, 0) !== recipe.durationMs)
      throw Error('Action timing or shared idle boundary changed: ' + name);
    next.clips[name] = { loop: false, frames };
    reports[name] = { ...recipe, sampleFps, sourceHashes, fingerprint, interpolationDirectory: output,
      frameSlots: frames.length, uniquePngs: new Set(frames.map(frame => frame.file)).size,
      correctedPhases, fullRgbaInterpolation: true, exactIdleEndpoints: true, keyframeMappingVerified: true };
    console.log('Prepared v7 ' + name + ': ' + frames.length + ' frame slots, ' + recipe.durationMs + ' ms.');
  }
  next.version = Math.max(7, next.version || 1);
  // Historical happy artwork stays archived, but no rebuild may reactivate it.
  delete next.clips.happy;
  next.idleActions = [{ clip: 'blink', weight: 4 }, { clip: 'yawn', weight: 1 }, { clip: 'wink', weight: 1 }];
  next.motion = { ...next.motion, revision: '0.3.0', sampleFps,
    idleExpressions: { generator: 'built-in image_gen', interpolation: 'RIFE 4.6 temporal TTA; complete RGBA dual-matte poses',
      registration: 'Grounded front-paw registration; complete head, ears, decoration and shoulders remain free to move. No nose registration or facial cutout.',
      endpointCorrection: 'FFmpeg floating-point premultiplied-alpha smoothstep only at three inferred phases beside each exact key',
      clips: { ...next.motion?.idleExpressions?.clips, ...Object.fromEntries(Object.entries(reports).map(([name, r]) => [name,
        { durationMs: r.durationMs, peakAtMs: r.peakAtMs, uniquePngs: r.uniquePngs, frameSlots: r.frameSlots, revision: 'v7' }])) } } };
  delete next.motion.idleExpressions.clips.happy;
  await verifyBaseline(next);
  const report = { createdAt: new Date().toISOString(), revision: next.motion.revision, existingClipsUnchanged: true,
    oldClipNames: preserved.protectedClips, protectedPngs: preserved.protectedPngs, selectedClips: selected,
    sourceSelection: config, normalization: path.join(root, 'keyframes-v7/normalization.json'), clips: reports };
  await fs.writeFile(path.join(root, 'keyframes-v7/recipe.json'), JSON.stringify({ sampleFps, endpointWindow, recipes }, null, 2) + '\n');
  await fs.writeFile(path.join(work, 'build-report.json'), JSON.stringify(report, null, 2) + '\n');
  await fs.writeFile(path.join(work, 'proposed-manifest.json'), JSON.stringify(next, null, 2) + '\n');
  Object.assign(manifest, next);
  return report;
}

module.exports = { buildIdleExpressions, makeRecipes, phaseAt, timing };
if (require.main === module) (async () => {
  const args = process.argv.slice(2), selection = args.find(arg => !arg.startsWith('--')) || selectionPath;
  const onlyArg = args.find(arg => arg.startsWith('--only='));
  const manifestPath = path.join(root, 'manifest.json'), manifest = JSON.parse(await fs.readFile(manifestPath, 'utf8'));
  const normalizeOnly = args.includes('--normalize-only');
  const report = await buildIdleExpressions(manifest, { selection, only: onlyArg ? onlyArg.slice(7).split(',') : undefined, normalizeOnly });
  if (!normalizeOnly && !args.includes('--stage-only')) {
    const pending = path.join(root, 'manifest-v7.pending.json');
    await fs.writeFile(pending, JSON.stringify(manifest, null, 2) + '\n');
    await fs.rename(pending, manifestPath);
  }
  console.log(JSON.stringify({ normalizedOnly: normalizeOnly, activeManifestUpdated: !normalizeOnly && !args.includes('--stage-only'),
    report: path.join(work, normalizeOnly ? 'registration-report.json' : 'build-report.json'), revision: report.revision || '0.3.0' }, null, 2));
})().catch(error => { console.error(error); process.exitCode = 1; });
