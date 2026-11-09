// Offline, candidate-only idle <-> focused gaze. No source artwork is redrawn.
// Writes only the selected gaze and transition artifact directories.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');
const { interpolate } = require('./motion-interpolate.cjs');
const { registerEyes, outsideMaskExact } = require('./build-work-preview-v3.cjs');
const { premultiply, subtractResidual, encodePremultiplied, residualWeight } = require('./wink-endpoint.cjs');
const artwork = require('./work-artwork.cjs').loadWorkArtwork();
const repo = path.resolve(__dirname, '..');
const assetRoot = path.join(repo, 'assets/strawberry-cat');
const output = artwork.gazeRoot;
const artifactRoot = artwork.transitionArtifacts;
const width = 512, height = 512, factor = 32, endpointWindow = 4;
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const relative = file => path.relative(repo, file).replaceAll('\\', '/');

async function readImage(file) {
  const bytes = await fs.readFile(file), metadata = await sharp(bytes).metadata();
  if (metadata.width !== width || metadata.height !== height || !metadata.hasAlpha) throw Error('Expected 512x512 RGBA PNG: ' + file);
  return { file, bytes, rgba:await sharp(bytes).ensureAlpha().raw().toBuffer(), sha256:hash(bytes) };
}

function eyeDifference(a, b, mask) {
  let sum = 0, count = 0, max = 0;
  for (let i = 0; i < mask.length; i++) if (mask[i]) for (let c = 0; c < 3; c++) {
    const delta = Math.abs(a[i * 4 + c] - b[i * 4 + c]);
    sum += delta; count++; max = Math.max(max, delta);
  }
  return { mean:sum / count,max };
}

// Match the selected artwork compositing: retain each source's existing feather and
// constrain only the inferred residual. Solid mask interiors are original RIFE.
function registerGaze(idle, focused, inferred, mask, phase) {
  if (phase === 0) return Buffer.from(idle);
  if (phase === 1) return Buffer.from(focused);
  const support = Buffer.from(mask.map(value => value ? Math.round(phase * 255) : 0));
  const baseline = registerEyes(idle, focused, support);
  return registerEyes(baseline, inferred, mask);
}

async function main() {
  const [idle, focused, maskImage] = await Promise.all([
    path.join(assetRoot, 'frames/fall_asleep-00.png'),
    path.join(artwork.previewRoot, 'keys/open.png'),
    path.join(artwork.previewRoot, 'keys/eye-mask.png')
  ].map(readImage));
  const sourceInventory = [idle, focused, maskImage].map(source => ({ file:relative(source.file),sha256:source.sha256 }));
  const manifestPath = path.join(assetRoot, 'manifest.json'), applicationManifest = await fs.readFile(manifestPath);
  const mask = Buffer.alloc(width * height);
  for (let i = 0; i < mask.length; i++) mask[i] = maskImage.rgba[i * 4 + 3];
  const activeMaskPixels = mask.reduce((sum, value) => sum + Number(value > 0), 0);
  if (!activeMaskPixels || activeMaskPixels > width * height / 5) throw Error('Unexpected eye mask coverage');
  if (!outsideMaskExact(idle.rgba, focused.rgba, mask)) throw Error('Source focused gaze changes pixels outside eye mask');
  for (let i = 0; i < mask.length; i++) {
    if (idle.rgba[i * 4 + 3] !== focused.rgba[i * 4 + 3]) throw Error('Source gaze changes silhouette alpha');
    if (mask[i] && idle.rgba[i * 4 + 3] !== 255) throw Error('Expected eye mask wholly inside opaque face');
  }
  await fs.mkdir(path.join(output, 'frames'), { recursive:true });
  await fs.mkdir(artifactRoot, { recursive:true });
  const fingerprint = hash(Buffer.from(JSON.stringify({ sourceInventory,factor,engine:'RIFE 4.6',temporalTta:true,scope:'idle-to-focused' })));
  const inferenceRoot = path.join(artifactRoot, 'rife-gaze-' + fingerprint.slice(0, 12));
  let inference;
  try { inference = JSON.parse(await fs.readFile(path.join(inferenceRoot, 'interpolation-report.json'), 'utf8')); }
  catch (error) {
    if (error.code !== 'ENOENT') throw error;
    console.log('Interpolating idle to focused gaze with RIFE 4.6');
    inference = await interpolate({ frames:[idle.file,focused.file],outputDirectory:inferenceRoot,factor,gpu:0,temporalTta:true });
  }
  if (inference.outputFrames !== factor + 1 || inference.model !== 'rife-v4.6' || inference.factor !== factor ||
      inference.keyframeChecks?.length !== 2 || !inference.keyframeChecks.every(check => check.exactRgbMatch)) throw Error('Unexpected or unverified RIFE output');
  const blackRoot = path.join(inferenceRoot, 'work/black-output');
  const blackFiles = (await fs.readdir(blackRoot)).filter(file => file.endsWith('.png')).sort();
  if (blackFiles.length !== factor * 2) throw Error('Unexpected black-matte output count');
  // Upstream numbering starts at 1; phase indices must address the sorted list.
  for (const [index, source] of [[0,idle.rgba],[factor,focused.rgba]]) {
    const black = await sharp(path.join(blackRoot, blackFiles[index])).ensureAlpha().raw().toBuffer();
    for (let i = 0; i < mask.length; i++) if (mask[i]) for (let c = 0; c < 3; c++)
      if (black[i * 4 + c] !== source[i * 4 + c]) throw Error('Black-matte key mapping mismatch at phase ' + index);
  }
  const inferredFrames = [];
  let avoidedFalseAlphaPixels = 0;
  for (let index = 0; index <= factor; index++) {
    if (index === 0 || index === factor) { inferredFrames.push(index === 0 ? idle.rgba : focused.rgba); continue; }
    // This entire eye region is opaque in both source keys. Dual-matte alpha
    // recovery can disagree between motion estimates and invent holes here.
    // Keep the model's black-matte RGB directly and the exact source alpha.
    const dual = (await readImage(path.join(inferenceRoot, String(index).padStart(5, '0') + '.png'))).rgba;
    const black = await sharp(path.join(blackRoot, blackFiles[index])).ensureAlpha().raw().toBuffer();
    const rgba = Buffer.from(idle.rgba);
    for (let i = 0; i < mask.length; i++) if (mask[i]) {
      const p = i * 4;
      if (dual[p + 3] !== 255) avoidedFalseAlphaPixels++;
      black.copy(rgba, p, p, p + 3);
    }
    inferredFrames.push(rgba);
  }
  const registered = inferredFrames.map((frame, index) => registerGaze(idle.rgba, focused.rgba, frame, mask, index / factor));
  const endpoints = [{ index:0,direction:1 },{ index:factor,direction:-1 }].map(endpoint => {
    const { index,direction } = endpoint;
    const firstStep = eyeDifference(registered[index], registered[index + direction], mask);
    const secondStep = eyeDifference(registered[index + direction], registered[index + direction * 2], mask);
    const base = premultiply(registered[index]), near1 = premultiply(registered[index + direction]), near2 = premultiply(registered[index + direction * 2]);
    let residualSum = 0, channels = 0;
    for (let i = 0; i < mask.length; i++) if (mask[i]) for (let c = 0; c < 3; c++) {
      const p = i * 4 + c; residualSum += Math.abs(2 * near1[p] - near2[p] - base[p]); channels++;
    }
    const residualMean = residualSum / channels;
    // A small, ordinary first step needs no correction. Only suppress a clear
    // endpoint offset; the existing helper preserves linear inferred motion.
    const correctionApplied = residualMean > .75 && firstStep.mean > Math.max(.5, secondStep.mean * 2);
    return { ...endpoint,firstStep,secondStep,residualMean,correctionApplied,base,near1,near2 };
  });
  const frames = [], outputRgba = [];
  let nonEyePixelsExact = true, alphaExact = true;
  for (let index = 0; index <= factor; index++) {
    let rgba = registered[index];
    const endpoint = endpoints.find(point => point.correctionApplied && Math.abs(index - point.index) > 0 && Math.abs(index - point.index) < endpointWindow);
    if (endpoint) {
      rgba = encodePremultiplied(subtractResidual(endpoint.base, endpoint.near1, endpoint.near2,
        premultiply(rgba), residualWeight(Math.abs(index - endpoint.index), endpointWindow)));
      // Restore protected bytes, including invisible RGB, after float encoding.
      for (let i = 0; i < mask.length; i++) if (!mask[i]) idle.rgba.copy(rgba, i * 4, i * 4, i * 4 + 4);
    }
    if (!outsideMaskExact(idle.rgba, rgba, mask)) nonEyePixelsExact = false;
    for (let p = 3; p < rgba.length; p += 4) if (rgba[p] !== idle.rgba[p]) { alphaExact = false; break; }
    const file = 'frames/gaze-' + String(index).padStart(2, '0') + '.png', destination = path.join(output, file);
    if (index === 0 || index === factor) await fs.copyFile(index === 0 ? idle.file : focused.file, destination);
    else await sharp(rgba, { raw:{ width,height,channels:4 } }).png().toFile(destination);
    const bytes = await fs.readFile(destination);
    frames.push({ index,phase:index / factor,file,pngSha256:hash(bytes),rgbaSha256:hash(rgba) });
    outputRgba.push(rgba);
  }
  const idleEndpointExact = outputRgba[0].equals(idle.rgba), focusedEndpointExact = outputRgba.at(-1).equals(focused.rgba);
  const adjacentDifferences = outputRgba.slice(1).map((frame, index) => ({ from:index,to:index + 1,...eyeDifference(outputRgba[index], frame, mask) }));
  const endpointReport = endpoints.map(({ base,near1,near2,...report }) => report);
  const manifest = { version:artwork.transitionVersion,status:'candidate-not-installed',canvas:{ width,height },direction:'idle-to-focused',reversible:true,
    fileBase:'directory containing this manifest.json',phaseCount:factor + 1,phaseRange:[0,1],factor,
    timing:'Untimed phases including both endpoints; caller maps its 400ms timeline to phase and may reverse it.',
    recommendedDurationMs:400,reverseIndices:frames.map(frame => frame.index).reverse(),
    sourceInventory,mask:{ file:relative(maskImage.file),channel:'alpha',activePixels:activeMaskPixels },
    interpolation:{ engine:'RIFE 4.6',factor,registration:'Already-feathered source baseline plus masked RIFE residual; exact source keys and fixed non-eye pixels',alpha:'Opaque face uses RIFE black-matte RGB and exact original alpha',endpointWindow,endpointCorrections:endpointReport },frames };
  await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
  const sourceUnchanged = (await fs.readFile(manifestPath)).equals(applicationManifest) &&
    (await Promise.all(sourceInventory.map(async source => hash(await fs.readFile(path.join(repo, source.file))) === source.sha256))).every(Boolean);
  const qa = { passed:sourceUnchanged && nonEyePixelsExact && alphaExact && idleEndpointExact && focusedEndpointExact,
    candidateOnly:true,canvas:{ width,height },phaseCount:frames.length,sourceInventory,sourceUnchanged,
    originalApplicationManifestSha256:hash(applicationManifest),nonEyePixelsExact,alphaExact,idleEndpointExact,focusedEndpointExact,
    inference:{ directory:relative(inferenceRoot),fingerprint,model:inference.model,factor,outputFrames:inference.outputFrames,keyframeChecks:inference.keyframeChecks,
      opaqueEyeRgbSource:'work/black-output',blackMatteKeyMappingExact:true,alphaSource:'exact original idle',avoidedFalseAlphaPixels },
    endpointCorrections:endpointReport,adjacentDifferences,frames };
  await fs.writeFile(path.join(output, 'verification.json'), JSON.stringify(qa, null, 2) + '\n');
  await writeContact(outputRgba);
  console.log(JSON.stringify({ output,passed:qa.passed,phaseCount:frames.length,sourceUnchanged,nonEyePixelsExact,alphaExact,idleEndpointExact,focusedEndpointExact,endpointCorrections:endpointReport }, null, 2));
  if (!qa.passed) process.exitCode = 2;
}

async function writeContact(frames) {
  const indices = [0,1,4,8,16,24,28,31,32], tiles = [];
  for (let i = 0; i < indices.length; i++) {
    const image = await sharp(frames[indices[i]], { raw:{ width,height,channels:4 } }).resize(256,256).flatten({ background:i >= 6 ? '#252936' : '#f3efe8' }).png().toBuffer();
    tiles.push({ input:image,left:i % 3 * 256,top:Math.floor(i / 3) * 280 });
    const label = Buffer.from(`<svg width="256" height="24"><text x="128" y="17" text-anchor="middle" font-family="Arial" font-size="14" fill="#534b45">${indices[i]} / 32</text></svg>`);
    tiles.push({ input:label,left:i % 3 * 256,top:Math.floor(i / 3) * 280 + 256 });
  }
  await sharp({ create:{ width:768,height:840,channels:4,background:'#f3efe8' } }).composite(tiles).png().toFile(path.join(output, 'contact.png'));
}

if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1; });
module.exports = { registerGaze,eyeDifference };
