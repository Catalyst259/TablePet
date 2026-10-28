// Promote the reviewed V9 refinement into runtime assets. Other clip bytes/timings stay unchanged.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const repo = path.resolve(__dirname, '..');
const root = path.join(repo, 'assets/strawberry-cat');
const hash = b => crypto.createHash('sha256').update(b).digest('hex');

async function snapshotOtherClips(manifest) {
  const clips = Object.fromEntries(Object.entries(manifest.clips).filter(([name]) => name !== 'wink'));
  const files = [...new Set(Object.values(clips).flatMap(c => c.frames.map(f => f.file)))].sort();
  const digest = crypto.createHash('sha256');
  for (const file of files) digest.update(file + '\0' + hash(await fs.readFile(path.join(root, file))) + '\n');
  return { timelinesSHA256: hash(JSON.stringify(clips)), pngAggregateSHA256: digest.digest('hex'), pngCount: files.length };
}

async function main() {
  const args = process.argv.slice(2), selectionIndex = args.indexOf('--selection');
  const selection = selectionIndex < 0 ? 'reference/idle-v9-fluid-selection.json' : args[selectionIndex + 1];
  if (!selection) throw Error('--selection requires a JSON file.');
  const config = JSON.parse(await fs.readFile(path.resolve(repo, selection), 'utf8'));
  if (config.outputGroup !== 'wink-v9' || !['smooth-click', 'fluid-click'].includes(config.name)) throw Error('Expected a V9 refinement.');
  const fluid = config.name === 'fluid-click';
  const candidateRoot = path.join(repo, 'artifacts/wink-v9', config.name);
  const frameFolder = fluid ? 'wink-v9-fluid' : 'wink-v9-smooth';
  const expectedDuration = config.knots.at(-1)[0];
  const manifestPath = path.join(root, 'manifest.json');
  const original = await fs.readFile(manifestPath);
  const manifest = JSON.parse(original);
  const candidate = JSON.parse(await fs.readFile(path.join(candidateRoot, 'candidate-manifest.json'), 'utf8'));
  const qa = JSON.parse(await fs.readFile(path.join(candidateRoot, 'qa.json'), 'utf8'));
  if (!candidate.candidateOnly || candidate.name !== config.name || !qa.passed || !qa.firstExact || !qa.lastExact)
    throw Error('Expected reviewed candidate with exact V9 art and original idle endpoints.');
  const baseline = JSON.parse(await fs.readFile(path.join(repo, 'reference/idle-v9-approved-keys.json'), 'utf8'));
  const refined = JSON.parse(await fs.readFile(path.join(candidateRoot, 'normalization.json'), 'utf8'));
  for (const key of baseline.keys) {
    const match = refined.keys.find(k => k.id === key.id);
    if (!match || match.sha256 !== key.sha256 || hash(await fs.readFile(path.join(candidateRoot, match.file))) !== key.sha256) throw Error('V9 key changed: ' + key.id);
  }
  for (const [time, phase] of config.knots.slice(0, -1)) {
    let end = 0;
    const frame = candidate.clips.wink.frames.find(f => (end += f.durationMs) > time);
    if (!frame || frame.file !== refined.keys.find(k => k.id === refined.order[phase]).file) throw Error('Key timestamp mismatch: ' + time);
  }
  const before = await snapshotOtherClips(manifest);
  const idleFile = manifest.clips.idle.frames[0].file;
  const idleHash = hash(await fs.readFile(path.join(root, idleFile)));
  const mapping = new Map();
  const target = path.join(root, 'frames', frameFolder);
  await fs.mkdir(target, { recursive: true });
  for (const file of new Set(candidate.clips.wink.frames.map(f => f.file))) {
    const source = path.resolve(candidateRoot, file);
    if (!source.startsWith(candidateRoot + path.sep)) throw Error('Candidate frame path escapes folder.');
    const bytes = await fs.readFile(source), sha = hash(bytes);
    const dest = sha === idleHash ? idleFile : 'frames/' + frameFolder + '/' + sha + '.png';
    if (dest !== idleFile) await fs.writeFile(path.join(root, dest), bytes);
    mapping.set(file, dest);
  }
  const frames = candidate.clips.wink.frames.map(f => ({ file: mapping.get(f.file), durationMs: f.durationMs }));
  if (frames[0].file !== idleFile || frames.at(-1).file !== idleFile) throw Error('Invalid reaction endpoints.');
  if (frames.reduce((s, f) => s + f.durationMs, 0) !== expectedDuration) throw Error('Unexpected wink duration.');
  manifest.clips.wink = { loop: false, frames };
  manifest.idleActions = [{ clip: 'blink', weight: 4 }, { clip: 'yawn', weight: 1 }];
  if (manifest.motion?.idleExpressions?.clips) delete manifest.motion.idleExpressions.clips.wink;
  manifest.motion.clickReactions = {
    choices: ['rise', 'wink'], selection: 'equal probability; rise includes hold/settle',
    wink: { revision: 'v9-' + config.name, durationMs: expectedDuration, peakHoldMs: 350,
      closingMs: config.knots[4][0], openingMs: config.knots[7][0] - config.knots[5][0],
      frameSlots: frames.length, uniquePngs: new Set(frames.map(f => f.file)).size,
      interpolationFactor: config.factor, endpointWindow: config.endpointWindow,
      endpointMode: config.endpointMode || 'blend', refinementDivisions: config.refinementDivisions || 1,
      motionSpacing: config.motionSpacing || 'phase', gaze: 'V9 unchanged', recipe: selection }
  };
  const after = await snapshotOtherClips(manifest);
  if (JSON.stringify(before) !== JSON.stringify(after)) throw Error('Other clips changed.');
  const backup = path.join(candidateRoot, 'runtime-manifest-before.json');
  try { await fs.writeFile(backup, original, { flag: 'wx' }); } catch (e) { if (e.code !== 'EEXIST') throw e; }
  await fs.writeFile(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
  const report = { candidate: candidateRoot, updated: manifestPath, otherClipsUnchanged: true,
    before, after, idleActions: manifest.idleActions, wink: manifest.motion.clickReactions.wink,
    sourceKeysExactV9: true, sourceGenerationUsed: false };
  await fs.writeFile(path.join(repo, fluid ? 'reference/wink-v9-fluid-runtime.json' : 'reference/wink-v9-smooth-runtime.json'), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report, null, 2));
}
main().catch(error => { console.error(error); process.exitCode = 1; });
