// Offline correction for the tiny non-zero residual in RIFE's first inferred poses.
// This is standard floating-point premultiplied-alpha compositing via FFmpeg,
// not pose generation, optical flow, or a dissolve across the entire breath.
// Only phases 1..window-1 blend with the exact resting sprite. At window and
// beyond, the original RIFE PNG is copied unchanged; phase 0 stays byte-exact.
const fs = require('node:fs/promises');
const path = require('node:path');
const { promisify } = require('node:util');
const { execFile } = require('node:child_process');
const exec = promisify(execFile);

async function correctSleepEndpoint({ basePath, framePath, index, window = 16, outputPath }) {
  if (!Number.isInteger(index) || index < 0 || !Number.isInteger(window) || window < 2)
    throw new Error('Endpoint correction needs a nonnegative integer phase and a window of at least 2.');
  if (!basePath || !framePath || !outputPath) throw new Error('basePath, framePath and outputPath are required.');
  const base = path.resolve(basePath), frame = path.resolve(framePath), output = path.resolve(outputPath);
  const comparable = value => process.platform === 'win32' ? value.toLowerCase() : value;
  if ([base, frame].some(input => comparable(input) === comparable(output)))
    throw new Error('Endpoint correction output must not overwrite an input image.');
  if (path.extname(output).toLowerCase() !== '.png') throw new Error('Endpoint correction output must be a PNG.');
  await fs.mkdir(path.dirname(output), { recursive: true });
  if (index === 0 || index >= window) {
    await fs.copyFile(index === 0 ? base : frame, output);
    return { corrected: false, index, window, inferenceWeight: index === 0 ? 0 : 1 };
  }

  const u = index / window;
  const inferenceWeight = u * u * (3 - 2 * u);
  // Float planar RGBA avoids quantising the two weighted alpha values before
  // adding them. Ordinary "over" would change edge opacity; straight-RGBA
  // mixing would introduce colour fringes at transparent edges.
  const filter = '[0:v]format=gbrapf32le,premultiply=inplace=1[rest];' +
    '[1:v]format=gbrapf32le,premultiply=inplace=1[inferred];' +
    `[rest][inferred]blend=all_expr='A*${1 - inferenceWeight}+B*${inferenceWeight}',` +
    'unpremultiply=inplace=1,format=rgba[out]';
  await exec('ffmpeg', [
    '-hide_banner', '-loglevel', 'error', '-y',
    '-i', base, '-i', frame,
    '-filter_complex', filter, '-map', '[out]',
    '-frames:v', '1', '-update', '1', '-c:v', 'png', '-pix_fmt', 'rgba', output
  ], { windowsHide: true, maxBuffer: 1024 * 1024, timeout: 60000 });
  return { corrected: true, index, window, inferenceWeight };
}

module.exports = { correctSleepEndpoint };
