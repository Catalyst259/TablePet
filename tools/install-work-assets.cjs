'use strict';
// Install the approved work cat/prop layers without touching other clip content.
// node tools/install-work-assets.cjs [--verify]
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');
const { isDeepStrictEqual } = require('node:util');
const { translateY, compositeProps, dyAt } = require('./build-work-preview-v2.cjs');
const { ease } = require('./work-transition-motion.cjs');
const artwork = require('./work-artwork.cjs').loadWorkArtwork();
const repo = path.resolve(__dirname, '..');
const root = path.join(repo, 'assets/strawberry-cat');
const previewRoot = artwork.previewRoot;
const gazeRoot = artwork.gazeRoot;
const output = path.join(root, 'frames/work-v1');
const reference = path.join(repo, 'reference/work-runtime');
const manifestPath = path.join(root, 'manifest.json');
const workNames = ['work_enter', 'work_loop', 'work_exit', 'work_props'];
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const relative = file => path.relative(repo, file).replaceAll('\\', '/');
const parse = bytes => JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
const json = value => JSON.stringify(value, null, 2) + '\n';

function resolveWithin(directory, file) {
  const absolute = path.resolve(directory, file), child = path.relative(directory, absolute);
  if (child.startsWith('..') || path.isAbsolute(child)) throw Error('Path escapes asset directory: ' + file);
  return absolute;
}

// Small JSON span reader, used only to preserve untouched manifest text exactly.
function whitespace(text, at) { while (/\s/.test(text[at] || '') && at < text.length) at++; return at; }
function stringEnd(text, at) {
  if (text[at] !== '"') throw Error('Expected JSON string');
  for (let i = at + 1; i < text.length; i++) {
    if (text[i] === '\\') { i++; continue; }
    if (text[i] === '"') return i + 1;
  }
  throw Error('Unclosed JSON string');
}
function valueEnd(text, at) {
  at = whitespace(text, at);
  if (text[at] === '"') return stringEnd(text, at);
  if (text[at] === '{' || text[at] === '[') {
    const close = text[at] === '{' ? '}' : ']';
    let i = at + 1;
    while (i < text.length) {
      i = whitespace(text, i);
      if (text[i] === close) return i + 1;
      if (text[i] === '"') i = stringEnd(text, i);
      else if (text[i] === '{' || text[i] === '[') i = valueEnd(text, i);
      else i++;
    }
    throw Error('Unclosed JSON container');
  }
  let i = at;
  while (i < text.length && !/[\s,}\]]/.test(text[i])) i++;
  return i;
}
function objectEntries(text, at) {
  if (text[at] !== '{') throw Error('Expected JSON object');
  const entries = [];
  let i = whitespace(text, at + 1);
  while (text[i] !== '}') {
    const keyStart = i, keyEnd = stringEnd(text, i), key = JSON.parse(text.slice(i, keyEnd));
    i = whitespace(text, keyEnd);
    if (text[i++] !== ':') throw Error('Expected JSON colon');
    const valueStart = whitespace(text, i), end = valueEnd(text, valueStart);
    entries.push({ key,keyStart,keyEnd,valueStart,valueEnd:end });
    i = whitespace(text, end);
    if (text[i] === ',') i = whitespace(text, i + 1);
    else if (text[i] !== '}') throw Error('Expected JSON comma');
  }
  return { start:at,end:i + 1,entries };
}
function clipSpans(text) {
  const top = objectEntries(text, whitespace(text.replace(/^\uFEFF/, ' '), 0));
  const clips = top.entries.find(entry => entry.key === 'clips');
  if (!clips) throw Error('Manifest has no clips object');
  return objectEntries(text, clips.valueStart);
}
function nonWorkTextDigest(text) {
  const clips = clipSpans(text);
  return hash(Buffer.from(JSON.stringify({ before:text.slice(0, clips.start),after:text.slice(clips.end),
    otherClips:clips.entries.filter(entry => !workNames.includes(entry.key)).map(entry => ({ name:entry.key,text:text.slice(entry.keyStart, entry.valueEnd) })) })));
}
function nonWorkObject(manifest) {
  return { ...manifest,clips:Object.fromEntries(Object.entries(manifest.clips).filter(([name]) => !workNames.includes(name))) };
}
function patchWorkClips(text, expected) {
  const eol = text.includes('\r\n') ? '\r\n' : '\n';
  const format = value => JSON.stringify(value, null, 2).split('\n').join(eol + '    ');
  let result = text;
  const original = clipSpans(text);
  for (const entry of original.entries.filter(entry => workNames.includes(entry.key)).sort((a, b) => b.valueStart - a.valueStart))
    result = result.slice(0, entry.valueStart) + format(expected[entry.key]) + result.slice(entry.valueEnd);
  const current = clipSpans(result), present = new Set(current.entries.map(entry => entry.key));
  const missing = workNames.filter(name => !present.has(name));
  if (missing.length) {
    const at = current.entries.length ? current.entries.at(-1).valueEnd : current.start + 1;
    const inserted = (current.entries.length ? ',' : '') + eol + missing.map(name => '    ' + JSON.stringify(name) + ': ' + format(expected[name])).join(',' + eol);
    result = result.slice(0, at) + inserted + result.slice(at);
  }
  const before = parse(Buffer.from(text)), after = parse(Buffer.from(result));
  if (!isDeepStrictEqual(nonWorkObject(before), nonWorkObject(after)) || nonWorkTextDigest(text) !== nonWorkTextDigest(result))
    throw Error('Manifest patch changed non-work content');
  return result;
}

async function main(args = process.argv.slice(2), { quiet = false } = {}) {
  if (args.length > 1 || args.length === 1 && args[0] !== '--verify') throw Error('Usage: node tools/install-work-assets.cjs [--verify]');
  const verifyOnly = args.includes('--verify');
  const sourceInventory = new Map(), images = new Map(), copies = new Map();
  async function sourceBytes(file) {
    const bytes = await fs.readFile(file);
    sourceInventory.set(file, { file:relative(file),sha256:hash(bytes) });
    return bytes;
  }
  async function sourceJson(file) { return parse(await sourceBytes(file)); }
  async function imageAt(file, source = true) {
    if (images.has(file)) return images.get(file);
    const bytes = source ? await sourceBytes(file) : await fs.readFile(file);
    const metadata = await sharp(bytes).metadata();
    if (metadata.width !== 512 || metadata.height !== 512 || !metadata.hasAlpha) throw Error('Expected 512x512 RGBA PNG: ' + file);
    const value = { bytes,rgba:await sharp(bytes).ensureAlpha().raw().toBuffer(),sha256:hash(bytes) };
    images.set(file, value); return value;
  }
  const [selected, timeline, blink, gaze, selectedQa, gazeQa] = await Promise.all([
    path.join(previewRoot, 'manifest.json'),path.join(previewRoot, 'timeline.json'),path.join(previewRoot, 'blink/manifest.json'),
    path.join(gazeRoot, 'manifest.json'),path.join(previewRoot, 'verification.json'),path.join(gazeRoot, 'verification.json')
  ].map(sourceJson));
  if (!selectedQa.passed || !gazeQa.passed || timeline.length !== 576 || selected.clip.durationMs !== 9600 || blink.frames.length !== 65 || gaze.frames.length !== 33)
    throw Error('Approved candidate duration, phases or verification do not match');
  const idleFile = 'frames/fall_asleep-00.png', focusedFile = 'frames/work-v1/focused.png', propsFile = 'frames/work-v1/props.png';
  const idle = await imageAt(path.join(root, idleFile)), focused = await imageAt(path.join(previewRoot, 'keys/open.png'));
  async function importImage(source, destination) {
    const item = await imageAt(source), target = resolveWithin(root, destination);
    if (destination === idleFile) {
      if (!item.rgba.equals(idle.rgba)) throw Error('Idle endpoint mismatch');
      return destination;
    }
    if (destination === focusedFile && !item.rgba.equals(focused.rgba)) throw Error('Focused endpoint mismatch');
    const previous = copies.get(destination);
    if (previous) {
      if (!previous.image.rgba.equals(item.rgba)) throw Error('Conflicting work output: ' + destination);
      previous.aliasSources.push({ file:relative(source),sha256:item.sha256,pixelsExact:true });
    } else copies.set(destination, { source,target,image:item,aliasSources:[] });
    return destination;
  }
  await importImage(path.join(previewRoot, 'keys/open.png'), focusedFile);
  await importImage(path.join(previewRoot, 'props.png'), propsFile);
  const gazeFiles = new Map(), catFiles = new Map();
  async function gazeFile(index) {
    if (gazeFiles.has(index)) return gazeFiles.get(index);
    const frame = gaze.frames[index], source = resolveWithin(gazeRoot, frame.file);
    const file = index === 0 ? idleFile : index === 32 ? focusedFile : 'frames/work-v1/gaze-' + String(index).padStart(2, '0') + '.png';
    await importImage(source, file); gazeFiles.set(index, file); return file;
  }
  async function catFile(file) {
    if (catFiles.has(file)) return catFiles.get(file);
    const source = resolveWithin(previewRoot, file), image = await imageAt(source);
    let target;
    if (image.rgba.equals(focused.rgba)) target = focusedFile;
    else {
      const match = /^blink\/blink-(\d{2})\.png$/.exec(file);
      if (!match) throw Error('Unrecognized approved cat layer: ' + file);
      target = 'frames/work-v1/blink-' + match[1] + '.png';
    }
    await importImage(source, target); catFiles.set(file, target); return target;
  }
  const work = { work_enter:{ loop:false,frames:[] },work_loop:{ loop:true,frames:[] },work_exit:{ loop:false,frames:[] },work_props:{ loop:true,frames:[{ file:propsFile,durationMs:17 }] } };
  const transitionPhases = [];
  for (let i = 0; i < 24; i++) {
    const progress = i === 23 ? 1 : ease(i / 24), phase = Math.round(32 * progress);
    const durationMs = Math.round((i + 1) * 400 / 24) - Math.round(i * 400 / 24);
    transitionPhases.push({ index:i,startMs:Math.round(i * 400 / 24),durationMs,enterPhase:phase,exitPhase:32 - phase });
    work.work_enter.frames.push({ file:await gazeFile(phase),durationMs });
    work.work_exit.frames.push({ file:await gazeFile(32 - phase),durationMs });
  }
  for (let i = 0; i < timeline.length; i++) {
    const slot = timeline[i];
    if (slot.index !== i || slot.durationMs !== selected.clip.frames[i].durationMs || slot.file !== selected.clip.frames[i].file || ![16,17].includes(slot.durationMs))
      throw Error('Candidate timeline mismatch at slot ' + i);
    work.work_loop.frames.push({ file:await catFile(slot.catFile),durationMs:slot.durationMs });
  }
  const clipSummary = Object.fromEntries(workNames.map(name => { const clip = work[name]; return [name,{
    slots:clip.frames.length,durationMs:clip.frames.reduce((sum, frame) => sum + frame.durationMs, 0),loop:clip.loop,
    first:clip.frames[0].file,last:clip.frames.at(-1).file,uniqueFiles:new Set(clip.frames.map(frame => frame.file)).size
  }]; }));
  if (clipSummary.work_enter.durationMs !== 400 || clipSummary.work_exit.durationMs !== 400 || clipSummary.work_loop.durationMs !== 9600 ||
      Object.values(work).some(clip => clip.frames.some(frame => ![16,17].includes(frame.durationMs)))) throw Error('Invalid production clip timing');
  if (work.work_enter.frames[0].file !== idleFile || work.work_enter.frames.at(-1).file !== focusedFile ||
      work.work_exit.frames[0].file !== focusedFile || work.work_exit.frames.at(-1).file !== idleFile ||
      work.work_loop.frames[0].file !== focusedFile || work.work_loop.frames.at(-1).file !== focusedFile) throw Error('Work clip seam mismatch');

  if (!verifyOnly) await fs.mkdir(output, { recursive:true });
  const installedImages = new Map(), copiedAssets = [];
  for (const [file, item] of copies) {
    if (!verifyOnly) await fs.copyFile(item.source, item.target);
    const installed = await imageAt(item.target, false);
    const exactSha = installed.sha256 === item.image.sha256, exactPixels = installed.rgba.equals(item.image.rgba);
    if (!exactSha || !exactPixels) throw Error('Production copy changed source bytes or pixels: ' + file);
    installedImages.set(file, installed);
    copiedAssets.push({ file,source:relative(item.source),sha256:installed.sha256,rgbaSha256:hash(installed.rgba),bytes:installed.bytes.length,exactSha,exactPixels,aliasSources:item.aliasSources });
  }
  installedImages.set(idleFile, idle);
  // Cover every distinct blink/focus source, both blink events, breath extrema,
  // ordinary work poses and the final sample against the selected artwork.
  const indices = new Set([0,1,72,144,288,432,575,402,411,423]);
  const seenCats = new Set();
  timeline.forEach(slot => { if (!seenCats.has(slot.catFile)) { indices.add(slot.index); seenCats.add(slot.catFile); } });
  const representativeFrames = [], props = installedImages.get(propsFile).rgba;
  for (const index of [...indices].sort((a,b) => a-b)) {
    const slot = timeline[index], productionFile = work.work_loop.frames[index].file;
    const cat = installedImages.get(productionFile).rgba, dy = dyAt(slot.timeMs);
    const combined = compositeProps(translateY(cat, 512, 512, dy), props);
    const expected = await imageAt(resolveWithin(previewRoot, slot.file));
    const pixelsExact = combined.equals(expected.rgba);
    representativeFrames.push({ index,timeMs:slot.timeMs,catFile:productionFile,dyPx:dy,previewFile:relative(resolveWithin(previewRoot, slot.file)),
      actualRgbaSha256:hash(combined),expectedRgbaSha256:hash(expected.rgba),pixelsExact });
    if (!pixelsExact || dy !== slot.dyPx) throw Error('Production composition differs from selected artwork at slot ' + index);
  }
  const sourceUnchanged = (await Promise.all([...sourceInventory].map(async ([file, entry]) => hash(await fs.readFile(file)) === entry.sha256))).every(Boolean);
  if (!sourceUnchanged) throw Error('A candidate source changed during export');
  // Read the latest manifest only after all work images and checks are ready,
  // so independent yawn or other action updates are preserved.
  const beforeBytes = await fs.readFile(manifestPath), beforeText = beforeBytes.toString('utf8'), before = parse(beforeBytes);
  if (before.clips.idle.frames[0].file !== idleFile || before.canvas.width !== 512 || before.canvas.height !== 512) throw Error('Production idle or canvas changed');
  const nextText = verifyOnly ? beforeText : patchWorkClips(beforeText, work);
  const after = parse(Buffer.from(nextText));
  const clipsExact = workNames.every(name => isDeepStrictEqual(after.clips[name], work[name]));
  const nonWorkExact = isDeepStrictEqual(nonWorkObject(before), nonWorkObject(after));
  const nonWorkTextExact = nonWorkTextDigest(beforeText) === nonWorkTextDigest(nextText);
  if (!clipsExact || !nonWorkExact || !nonWorkTextExact) throw Error('Installed work clips or untouched manifest content do not match');
  if (!verifyOnly) {
    if (!(await fs.readFile(manifestPath)).equals(beforeBytes)) throw Error('Manifest changed concurrently; rerun to preserve latest edits');
    await fs.writeFile(manifestPath, nextText);
    if (!(await fs.readFile(manifestPath)).equals(Buffer.from(nextText))) throw Error('Manifest write verification failed');
  }
  const report = { passed:true,mode:verifyOnly ? 'verify' : 'install',canvas:{ width:512,height:512 },sampleFps:60,
    artwork:{ version:artwork.artworkVersion,preview:artwork.previewName,transition:artwork.transitionName },
    clips:clipSummary,importedPngs:copies.size,productionReferencedPngs:new Set(Object.values(work).flatMap(clip => clip.frames.map(frame => frame.file))).size,
    importedPngBytes:copiedAssets.reduce((sum, item) => sum + item.bytes, 0),sourceUnchanged,sourceInventory:[...sourceInventory.values()],copiedAssets,
    clipContentsExact:clipsExact,nonWorkManifestExact:nonWorkExact,nonWorkManifestTextExact:nonWorkTextExact,
    manifest:{ beforeSha256:hash(beforeBytes),afterSha256:hash(Buffer.from(nextText)),
      beforeNonWorkSha256:hash(Buffer.from(JSON.stringify(nonWorkObject(before)))),afterNonWorkSha256:hash(Buffer.from(JSON.stringify(nonWorkObject(after)))),
      beforeNonWorkTextSha256:nonWorkTextDigest(beforeText),afterNonWorkTextSha256:nonWorkTextDigest(nextText),
      preservedClipNames:Object.keys(before.clips).filter(name => !workNames.includes(name)),
      yawnSha256:hash(Buffer.from(JSON.stringify(before.clips.yawn))),version:before.version },
    transitionPhases,representativeFrames,
    runtime:{ bodyTranslation:'dy(t) = cos(2*pi*t/4800)-1, in source 512px coordinates',props:'Static independent image; opacity belongs to Core/WPF',
      transitionMs:400,gazeTiming:'quintic ease; final slot is exact endpoint',workClock:'Continuous while working; independent of blink selection',
      exit:'Finish an active blink first; reverse gaze and fade props over 400ms; damp continuing breath to zero using 1-quinticEase' } };
  if (!verifyOnly) {
    await fs.mkdir(reference, { recursive:true });
    await fs.writeFile(path.join(reference, 'verification.json'), json(report));
    await fs.writeFile(path.join(reference, 'README.md'), readme(report));
  }
  if (!quiet) console.log(json({ passed:report.passed,mode:report.mode,clips:clipSummary,importedPngs:report.importedPngs,productionReferencedPngs:report.productionReferencedPngs,
    nonWorkManifestExact:nonWorkExact,nonWorkManifestTextExact:nonWorkTextExact,sourceUnchanged,representativeFrames:representativeFrames.length,
    verification:relative(path.join(reference, 'verification.json')) }));
  return report;
}

function readme(report) {
  return `# Work mode production assets\n\nInstall: \`node tools/install-work-assets.cjs\`\n\nRead-only verification: \`node tools/install-work-assets.cjs --verify\`\n\nThe selected source artwork is ${artwork.previewName} and ${artwork.transitionName}/gaze (reference/work-artwork-selection.json). Production PNGs are copied without re-encoding to \`assets/strawberry-cat/frames/work-v1/\`. Only the four work clips are inserted into the current application manifest; existing clips, idleActions, decorations, version and other metadata retain their exact content and text.\n\n| Clip | Slots | Duration | Layer |\n| --- | ---: | ---: | --- |\n| work_enter | 24 | 400 ms | Idle-to-focused cat, quintic gaze timing |\n| work_loop | 576 | 9600 ms | Focused cat and approved blink; no body offset or props |\n| work_exit | 24 | 400 ms | Focused-to-idle cat, quintic gaze timing |\n| work_props | 1 | 17 ms, loop | Transparent computer and Working sign |\n\nAll slots are 16 or 17 ms. \`work_enter\` begins with the original idle and ends at \`frames/work-v1/focused.png\`; \`work_exit\` has the opposite endpoints. The loop starts and ends with that same focused PNG.\n\nCore/WPF owns whole-cat vertical translation, \`dy(t)=cos(2*pi*t/4800)-1\` in 512px source coordinates. Props stay fixed and fade with the 400 ms quintic transition. Exit can start at any breathing phase after an active blink completes; continue that clock while damping its offset to zero. These behaviors are intentionally absent from the exported cat PNGs.\n\nVerification confirms source and copy SHA-256, exact copied RGBA, exact clip timing and joins, unchanged non-work manifest content/text, and ${report.representativeFrames.length} representative cat + translation + props reconstructions matching the selected artwork pixel-for-pixel. ${report.importedPngs} PNGs are imported; ${report.productionReferencedPngs} PNGs are referenced including the existing idle. Details are in \`verification.json\`.\n`;
}

if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1; });
module.exports = { patchWorkClips,nonWorkTextDigest,verify:() => main(['--verify'], { quiet:true }) };
