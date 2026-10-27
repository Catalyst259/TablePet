// Register revised model-drawn eyes on the approved idle, using the existing
// Sharp alignment/compositing workflow. No eye shape is drawn by this script.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');
const { readCanvas, noseCenter, registerExpression, idleFile } = require('./normalize-idle-v5.cjs');
const root = path.resolve(__dirname, '../assets/strawberry-cat');
const outputRoot = path.join(root, 'keyframes-v6');

async function normalize() {
  const base = await readCanvas(idleFile), targetNose = noseCenter(base.data);
  const names = (await fs.readdir(path.join(root, 'sources/idle-v6')))
    .filter(name => /^(happy|wink)-.+\.png$/.test(name)).sort();
  await fs.mkdir(outputRoot, { recursive: true });
  const results = [];
  for (const name of names) {
    const action = name.split('-')[0];
    results.push(await registerExpression({ source: 'sources/idle-v6/' + name,
      output: `keyframes-v6/${action}/${name}`, mask: 'eyes-v6', label: name.replace('.png', '') }, base.data, targetNose));
  }
  const entries = [{ output: idleFile, label: 'Approved idle' }, ...results];
  const layers = [], cell = 256, rowHeight = 282, columns = 4;
  for (let i = 0; i < entries.length; i++) {
    const background = Math.floor(i / columns) % 2 ? '#26313f' : '#fff8f3';
    layers.push({ input: await sharp(path.join(root, entries[i].output)).resize(cell, cell).flatten({ background }).png().toBuffer(), left: i % columns * cell, top: Math.floor(i / columns) * rowHeight + 26 });
    layers.push({ input: Buffer.from(`<svg width="256" height="26"><rect width="100%" height="100%" fill="#fff8f3"/><text x="8" y="19" font-size="14" fill="#443943">${entries[i].label}</text></svg>`), left: i % columns * cell, top: Math.floor(i / columns) * rowHeight });
  }
  await sharp({ create: { width: columns * cell, height: Math.ceil(entries.length / columns) * rowHeight, channels: 4, background: '#fff8f3' } })
    .composite(layers).png().toFile(path.join(outputRoot, 'candidate-contact.png'));
  const faceMaskBounds = { left: Math.min(...results.map(r => r.mask.bounds.left)), top: Math.min(...results.map(r => r.mask.bounds.top)), right: Math.max(...results.map(r => r.mask.bounds.right)), bottom: Math.max(...results.map(r => r.mask.bounds.bottom)) };
  const report = { createdAt: new Date().toISOString(), base: idleFile, baseSHA256: base.sourceSHA256, faceMaskBounds,
    method: 'Built-in imagegen eyes registered by nose using existing Sharp pipeline, original mouth/nose/body preserved.', results };
  await fs.writeFile(path.join(outputRoot, 'normalization.json'), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify({ candidates: results.length, contact: path.join(outputRoot, 'candidate-contact.png') }));
  return report;
}
module.exports = { normalize };
if (require.main === module) normalize().catch(error => { console.error(error); process.exitCode = 1; });
