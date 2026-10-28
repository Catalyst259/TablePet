// Preserve inferred motion while removing RIFE's nonzero endpoint residual.
// near1/near2 are the first two inferred frames on the same side of the key.
// Unlike a dissolve toward the key, this subtracts a decaying constant offset
// in premultiplied RGBA, so a linear trajectory with no residual is unchanged.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');

function premultiply(bytes) {
  const result = new Float32Array(bytes.length);
  for (let i = 0; i < bytes.length; i += 4) {
    const alpha = bytes[i + 3];
    result[i + 3] = alpha;
    for (let c = 0; c < 3; c++) result[i + c] = bytes[i + c] * alpha / 255;
  }
  return result;
}

function subtractResidual(base, near1, near2, frame, weight) {
  if ([near1, near2, frame].some(data => data.length !== base.length)) throw Error('RGBA lengths must match.');
  const corrected = new Float32Array(frame.length);
  for (let i = 0; i < frame.length; i++) corrected[i] = frame[i] - weight * (2 * near1[i] - near2[i] - base[i]);
  return corrected;
}

function encodePremultiplied(values) {
  const result = Buffer.alloc(values.length);
  for (let i = 0; i < values.length; i += 4) {
    const alpha = Math.max(0, Math.min(255, values[i + 3]));
    const byteAlpha = Math.round(alpha);
    result[i + 3] = byteAlpha;
    if (!byteAlpha) continue;
    for (let c = 0; c < 3; c++) {
      // The float alpha is the divisor; byte alpha is quantized only at output.
      result[i + c] = Math.round(Math.max(0, Math.min(alpha, values[i + c])) * 255 / alpha);
    }
  }
  return result;
}

function residualWeight(index, window) {
  const t = Math.max(0, Math.min(1, index / window));
  return 1 - t * t * (3 - 2 * t);
}

async function correctWinkEndpoint({ basePath, near1Path, near2Path, framePath, index, window = 12, outputPath }) {
  if (!Number.isInteger(index) || index < 0 || !Number.isInteger(window) || window < 2) throw Error('Endpoint correction needs a nonnegative integer index and a window of at least 2.');
  if (![basePath, near1Path, near2Path, framePath, outputPath].every(value => typeof value === 'string' && value.length)) throw Error('All five PNG paths are required.');
  const resolved = [basePath, near1Path, near2Path, framePath].map(value => path.resolve(value));
  const destination = path.resolve(outputPath);
  const comparable = value => process.platform === 'win32' ? value.toLowerCase() : value;
  if (resolved.some(value => comparable(value) === comparable(destination))) throw Error('Correction must not overwrite an input image.');
  if (path.extname(destination).toLowerCase() !== '.png') throw Error('Output must be a PNG.');
  await fs.mkdir(path.dirname(destination), { recursive: true });
  if (index === 0 || index >= window) {
    await fs.copyFile(index === 0 ? resolved[0] : resolved[3], destination);
    return { corrected: false, index, window, residualWeight: index === 0 ? 1 : 0 };
  }
  const images = await Promise.all(resolved.map(async file => {
    const { data, info } = await sharp(file).toColourspace('srgb').ensureAlpha().raw().toBuffer({ resolveWithObject: true });
    return { data, width: info.width, height: info.height, channels: info.channels };
  }));
  const { width, height } = images[0];
  if (images.some(image => image.width !== width || image.height !== height || image.channels !== 4)) throw Error('All endpoint PNGs must share one RGBA canvas.');
  const weight = residualWeight(index, window);
  const corrected = subtractResidual(...images.map(image => premultiply(image.data)), weight);
  await sharp(encodePremultiplied(corrected), { raw: { width, height, channels: 4 } }).png().toFile(destination);
  return { corrected: true, index, window, residualWeight: weight, method: 'two-sample endpoint residual subtraction in premultiplied RGBA' };
}

function selfTest() {
  const assert = require('node:assert/strict');
  const close = (actual, expected) => actual.forEach((value, index) => assert.ok(Math.abs(value - expected[index]) < 1e-5, `Channel ${index}: ${value} versus ${expected[index]}`));
  const base = Float32Array.from([40, 60, 80, 200, 20, 10, 30, 100]);
  const velocity = Float32Array.from([1, -2, 3, 1, -1, 2, 1, 2]);
  const offset = Float32Array.from([7, -4, 5, 3, 2, 4, -1, 2]);
  const at = (index, residual = false) => Float32Array.from(base, (value, i) => value + index * velocity[i] + (residual ? offset[i] : 0));
  for (const index of [1, 2, 5, 11]) close(subtractResidual(base, at(1), at(2), at(index), residualWeight(index, 12)), at(index));
  close(subtractResidual(base, at(1, true), at(2, true), at(0, true), 1), base);
  for (const index of [12, 13, 24]) close(subtractResidual(base, at(1, true), at(2, true), at(index, true), residualWeight(index, 12)), at(index, true));
  const transparent = encodePremultiplied(Float32Array.from([20, -10, 30, 0, 10, 20, 30, -4]));
  assert.deepEqual([...transparent], Array(8).fill(0));
  const opaque = Buffer.from([33, 77, 199, 255, 201, 99, 17, 128, 100, 200, 255, 1]);
  assert.deepEqual(encodePremultiplied(premultiply(opaque)), opaque);
  return { passed: true, checks: ['linear premultiplied trajectory preserved', 'constant endpoint residual removed', 'outside correction window unchanged', 'transparent RGB cleared', 'RGBA roundtrip'] };
}

module.exports = { correctWinkEndpoint, premultiply, subtractResidual, encodePremultiplied, residualWeight, selfTest };
if (require.main === module) {
  if (process.argv.includes('--self-test')) console.log(JSON.stringify(selfTest(), null, 2));
  else console.error('Use as a module, or run node tools/wink-endpoint.cjs --self-test.');
}
