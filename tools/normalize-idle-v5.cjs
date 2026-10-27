// Register model-drawn facial expressions on the approved, unchanged idle body.
// Sharp only resizes, translates and composites existing artwork; no expression
// is drawn here. Original generated PNGs and every selection remain preserved.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');

const repo = path.resolve(__dirname, '..');
const root = path.join(repo, 'assets/strawberry-cat');
const outputRoot = path.join(root, 'keyframes-v5');
const idleFile = 'frames/fall_asleep-00.png';
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const xml = text => String(text).replace(/[<>&"]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', '"': '&quot;' }[c]));

async function readCanvas(file) {
  const png = await fs.readFile(path.join(root, file));
  const metadata = await sharp(png).metadata();
  if (metadata.width !== metadata.height) throw Error(`Expected square source canvas: ${file}`);
  const data = await sharp(png).resize(512, 512, { kernel: 'lanczos3' }).ensureAlpha().raw().toBuffer();
  return { data, sourceSHA256: hash(png), sourceSize: metadata.width };
}

function noseCenter(data) {
  let xTotal = 0, yTotal = 0, weight = 0, firstPinkRow = -1;
  // Use the first compact pink band. A small open mouth can put its tongue
  // inside this search box as well, but that lower band is not the nose.
  for (let y = 300; y < 371; y++) for (let x = 211; x < 251; x++) {
    const i = (y * 512 + x) * 4, r = data[i], g = data[i + 1], b = data[i + 2];
    if (data[i + 3] < 250 || r < 220 || g < 110 || g > 228 || r - g < 24 || b - g > 55) continue;
    if (firstPinkRow < 0) firstPinkRow = y;
    if (y > firstPinkRow + 11) continue;
    const w = Math.min(80, r - g);
    xTotal += x * w; yTotal += y * w; weight += w;
  }
  if (weight < 1200) throw Error('Could not find the central pink nose in its registered search region.');
  return { x: xTotal / weight, y: yTotal / weight, weight };
}

async function noseBridgeProtection(base) {
  const selection = Buffer.alloc(512 * 512), eyePixels = Buffer.alloc(512 * 512);
  const queue = new Uint32Array(512 * 512);
  let head = 0, tail = 1;
  queue[0] = 310 * 512 + 232;
  selection[queue[0]] = 255;
  // Select only the connected white forehead marking, not the isolated white
  // catchlights inside either eye. The boundary comes from the original art.
  while (head < tail) {
    const p = queue[head++];
    for (const next of [p - 1, p + 1, p - 512, p + 512]) {
      const x = next % 512, y = Math.floor(next / 512), i = next * 4;
      if (x < 190 || x >= 280 || y < 288 || y >= 353 || selection[next]) continue;
      if (base[i + 3] < 250 || Math.min(base[i], base[i + 1], base[i + 2]) < 232) continue;
      selection[next] = 255; queue[tail++] = next;
    }
  }
  for (let y = 285; y < 391; y++) for (let x = 110; x < 355; x++) {
    if (x >= 211 && x <= 260) continue;
    const p = y * 512 + x, i = p * 4;
    if ((base[i] < 160 && base[i + 1] < 160 && base[i + 2] < 160) ||
      (base[i + 1] - base[i] > 20 && base[i + 2] - base[i] > 20)) eyePixels[p] = 255;
  }
  // Expand along the real white-cloud outline into a small amount of gray fur,
  // then feather that outline. Explicitly exclude the old eyes and their AA rim.
  const shaped = await sharp(selection, { raw: { width: 512, height: 512, channels: 1 } })
    .dilate(4).blur(2).toColourspace('b-w').raw().toBuffer();
  const excludedEyes = await sharp(eyePixels, { raw: { width: 512, height: 512, channels: 1 } })
    .dilate(2).toColourspace('b-w').raw().toBuffer();
  for (let p = 0; p < 512 * 512; p++) {
    const y = Math.floor(p / 512), u = Math.max(0, Math.min(1, (y - 349) / 5));
    shaped[p] = excludedEyes[p] ? 0 : Math.round(shaped[p] * (1 - u * u * (3 - 2 * u)));
  }
  return shaped;
}

async function faceMask(kind, base, translated) {
  if (kind === 'eyes-v6') {
    // Keep the original mouth/nose and every part of the body intact. These
    // mattes only register model-drawn eyelids, never synthesize an expression.
    const full = await faceMask('production', base, translated);
    for (let y = 0; y < 512; y++) for (let x = 0; x < 512; x++) {
      const distance = x < 232 ? Math.min(211 - x, 402 - y) : Math.min(x - 253, 409 - y);
      const u = Math.max(0, Math.min(1, distance / 4));
      const i = (y * 512 + x) * 4 + 3;
      full.data[i] = Math.round(full.data[i] * u * u * (3 - 2 * u));
    }
    return full;
  }
  if (kind === 'production') {
    // Shared preparation poses use the wider face matte, while the generated
    // yawn uses its tighter eyes and complete cheek. Their union prevents the
    // matte itself from changing when playback crosses an exact keyframe.
    const face = await faceMask('face', base, translated);
    const yawn = await faceMask('yawn', base, translated);
    for (let p = 0; p < 512 * 512; p++) face.data[p * 4 + 3] = Math.max(face.data[p * 4 + 3], yawn.data[p * 4 + 3]);
    return face;
  }
  // Cover both the old and new complete eye shapes, including their lower rims.
  // No protective cheek holes: they can retain pieces of the old blue irises.
  const left = '<rect x="90" y="278" width="132" height="124" rx="21" fill="white"/>';
  const face = '<rect x="90" y="278" width="132" height="132" rx="21" fill="white"/>' +
    '<rect x="211" y="294" width="56" height="121" rx="12" fill="white"/>' +
    '<rect x="250" y="297" width="126" height="113" rx="21" fill="white"/>';
  const mouth = '<rect x="207" y="349" width="52" height="72" rx="15" fill="white"/>';
  const yawnEyes = '<ellipse cx="164" cy="334" rx="55" ry="53" fill="white"/>' + '<ellipse cx="305" cy="347" rx="55" ry="55" fill="white"/>' +
    '<rect x="88" y="340" width="76" height="54" rx="18" fill="white"/>';
  const shapes = kind === 'left-eye' ? left : kind === 'yawn' ? yawnEyes + mouth : face + mouth;
  const svg = Buffer.from(`<svg width="512" height="512">${shapes}</svg>`);
  const data = await sharp(svg).blur(3).ensureAlpha().raw().toBuffer();
  const interior = await sharp(base, { raw: { width: 512, height: 512, channels: 4 } })
    .extractChannel(3).threshold(254).erode(9).raw().toBuffer();
  for (let p = 0; p < 512 * 512; p++) data[p * 4 + 3] = Math.min(data[p * 4 + 3], interior[p]);
  if (kind === 'yawn') {
    for (let p = 0; p < 512 * 512; p++) {
      const i = p * 4, x = p % 512, y = Math.floor(p / 512);
      if (x >= 250 && y < 298) data[i + 3] = 0;
    }
  }
  const bridge = await noseBridgeProtection(base);
  for (let p = 0; p < 512 * 512; p++) data[p * 4 + 3] = Math.round(data[p * 4 + 3] * (1 - bridge[p] / 255));
  // Keep the central forehead/nose bridge registered for every expression.
  // Feather outside its original pixels, stopping before either original eye.
  for (let y = 293; y < 341; y++) for (let x = 206; x < 258; x++) {
    const distance = Math.max(212 - x, x - 251, 299 - y, y - 334, 0);
    const u = Math.min(1, distance / 6), weight = u * u * (3 - 2 * u);
    const i = (y * 512 + x) * 4 + 3;
    data[i] = Math.round(data[i] * weight);
  }
  // The left inner ear must stay on the approved body. Existing blink artwork
  // has a slightly displaced ear line that must not enter prepared face keys.
  // Its entire ROI is excluded; the two-pixel exterior feather stops before
  // the original eye, so it cannot retain any old pupil during a closed pose.
  for (let y = 240; y < 302; y++) for (let x = 96; x < 135; x++) {
    const distance = Math.max(98 - x, x - 132, 242 - y, y - 299, 0);
    const u = Math.min(1, distance / 2), weight = u * u * (3 - 2 * u);
    const i = (y * 512 + x) * 4 + 3;
    data[i] = Math.round(data[i] * weight);
  }
  return { data };
}

async function registerExpression(spec, base, targetNose) {
  const source = await readCanvas(spec.source);
  const nose = noseCenter(source.data);
  const left = Math.round(targetNose.x - nose.x), top = Math.round(targetNose.y - nose.y);
  if (Math.abs(left) > 24 || Math.abs(top) > 32) throw Error(`Registration displacement is too large: ${spec.source}`);
  const extended = await sharp(source.data, { raw: { width: 512, height: 512, channels: 4 } })
    .extend({ left: 40, right: 40, top: 40, bottom: 40, background: '#00000000' }).png().toBuffer();
  const translated = await sharp(extended)
    .extract({ left: 40 - left, top: 40 - top, width: 512, height: 512 }).raw().toBuffer();
  const facialMatte = await faceMask(spec.mask, base, translated), mask = facialMatte.data;
  const cutout = await sharp(translated, { raw: { width: 512, height: 512, channels: 4 } })
    .composite([{ input: mask, raw: { width: 512, height: 512, channels: 4 }, blend: 'dest-in' }]).png().toBuffer();
  const composited = await sharp(base, { raw: { width: 512, height: 512, channels: 4 } })
    .composite([{ input: cutout }]).raw().toBuffer();
  // Keep every pixel outside the facial matte byte-exact, including invisible RGB.
  let editedPixels = 0, preservedPixels = 0, outsideDifferences = 0, leftInnerEarDifferences = 0, x0 = 512, y0 = 512, x1 = -1, y1 = -1;
  for (let i = 0; i < composited.length; i += 4) {
    if (!mask[i + 3]) {
      for (let c = 0; c < 4; c++) composited[i + c] = base[i + c];
      preservedPixels++;
    } else { editedPixels++; const p = i / 4, x = p % 512, y = Math.floor(p / 512); x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
    if (!mask[i + 3] && !base.subarray(i, i + 4).equals(composited.subarray(i, i + 4))) outsideDifferences++;
  }
  for (let y = 242; y < 300; y++) for (let x = 98; x < 133; x++) {
    const i = (y * 512 + x) * 4;
    if (!base.subarray(i, i + 4).equals(composited.subarray(i, i + 4))) leftInnerEarDifferences++;
  }
  if (leftInnerEarDifferences) throw Error(`Left inner ear changed: ${spec.source}`);
  const destination = path.join(root, spec.output);
  await fs.mkdir(path.dirname(destination), { recursive: true });
  await sharp(composited, { raw: { width: 512, height: 512, channels: 4 } }).png().toFile(destination);
  return { ...spec, sourceSHA256: source.sourceSHA256, sourceSize: source.sourceSize, nose, targetNose,
    normalization: { resizedCanvas: 512, left, top, interpolation: 'Sharp Lanczos3; integer translation' },
    mask: { name: spec.mask, featherSigma: 3, editedPixels, preservedPixels, outsideDifferences, leftInnerEarDifferences, bounds: { left: x0, top: y0, right: x1, bottom: y1 }, contourInsetPixels: 9 },
    outputSHA256: hash(await fs.readFile(destination)) };
}

async function makeContacts(results, base) {
  const columns = 4, cell = 256, rowHeight = 284;
  const entries = [{ output: idleFile, label: 'Approved idle' }, ...results];
  const rows = Math.ceil(entries.length / columns), layers = [];
  for (let i = 0; i < entries.length; i++) {
    const entry = entries[i], col = i % columns, row = Math.floor(i / columns);
    const background = row % 2 ? '#26313f' : '#f9f2ee', text = row % 2 ? '#fff' : '#443943';
    layers.push({ input: await sharp(path.join(root, entry.output)).resize(cell, cell).flatten({ background }).png().toBuffer(), left: col * cell, top: row * rowHeight + 28 });
    layers.push({ input: Buffer.from(`<svg width="${cell}" height="28"><rect width="100%" height="100%" fill="${background}"/><text x="8" y="19" font-size="15" fill="${text}">${xml(entry.label)}</text></svg>`), left: col * cell, top: row * rowHeight });
  }
  await sharp({ create: { width: columns * cell, height: rows * rowHeight, channels: 4, background: '#e7d9d7' } }).composite(layers).png().toFile(path.join(outputRoot, 'candidate-contact.png'));
  const detail = [];
  const detailSize = { left: 100, top: 274, width: 270, height: 155 };
  for (let i = 0; i < entries.length; i++) {
    detail.push({ input: await sharp(path.join(root, entries[i].output)).extract(detailSize).resize(540, 310).flatten({ background: '#f9f2ee' }).png().toBuffer(), left: (i % 2) * 540, top: Math.floor(i / 2) * 340 + 30 });
    detail.push({ input: Buffer.from(`<svg width="540" height="30"><text x="10" y="21" font-size="18" fill="#443943">${xml(entries[i].label)}</text></svg>`), left: (i % 2) * 540, top: Math.floor(i / 2) * 340 });
  }
  await sharp({ create: { width: 1080, height: Math.ceil(entries.length / 2) * 340, channels: 4, background: '#f9f2ee' } }).composite(detail).png().toFile(path.join(outputRoot, 'candidate-face-details.png'));
}

async function normalize() {
  const baseline = await readCanvas(idleFile), targetNose = noseCenter(baseline.data);
  const specs = [
    { source: 'frames/blink-01.png', output: 'keyframes-v5/shared/half-eyes.png', mask: 'eyes', label: 'Prepared half eyes' },
    { source: 'frames/blink-02.png', output: 'keyframes-v5/shared/low-eyes.png', mask: 'eyes', label: 'Prepared low eyes' },
    { source: 'frames/blink-03.png', output: 'keyframes-v5/shared/closed-eyes.png', mask: 'eyes', label: 'Prepared closed eyes' },
    { source: 'frames/blink-01.png', output: 'keyframes-v5/shared/half-wink.png', mask: 'left-eye', label: 'Prepared half wink' },
    { source: 'frames/blink-02.png', output: 'keyframes-v5/shared/low-wink.png', mask: 'left-eye', label: 'Prepared low wink' }
  ];
  const names = (await fs.readdir(path.join(root, 'sources/idle-v5'))).filter(name => /^(yawn|happy|wink)-.+\.png$/.test(name)).sort();
  for (const name of names) {
    const action = name.split('-')[0];
    specs.push({ source: 'sources/idle-v5/' + name, output: `keyframes-v5/${action}/${name}`, mask: action === 'yawn' ? 'yawn' : action === 'wink' ? 'wink' : 'face', label: name.replace('.png', '') });
  }
  await fs.mkdir(outputRoot, { recursive: true });
  const results = [];
  for (const spec of specs) results.push(await registerExpression(spec, baseline.data, targetNose));
  await makeContacts(results, baseline.data);
  const faceMaskBounds = { left: Math.min(...results.map(r => r.mask.bounds.left)), top: Math.min(...results.map(r => r.mask.bounds.top)), right: Math.max(...results.map(r => r.mask.bounds.right)), bottom: Math.max(...results.map(r => r.mask.bounds.bottom)) };
  const report = { createdAt: new Date().toISOString(), base: idleFile, baseSHA256: baseline.sourceSHA256, faceMaskBounds,
    method: 'Existing model-drawn face artwork registered by nose; Sharp resize/translate and feathered face-only composite on exact approved idle body.',
    requiresVisualApprovalBeforeInterpolation: true, results };
  await fs.writeFile(path.join(outputRoot, 'normalization.json'), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify({ candidates: results.length, allOutsideFacesExact: results.every(result => result.mask.outsideDifferences === 0), contact: path.join(outputRoot, 'candidate-contact.png'), details: path.join(outputRoot, 'candidate-face-details.png') }, null, 2));
  return report;
}

module.exports = { normalize, faceMask, idleFile, readCanvas, noseCenter, registerExpression };
if (require.main === module) normalize().catch(error => { console.error(error); process.exitCode = 1; });
