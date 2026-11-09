'use strict';
// Pure, narrowly scoped manifest update shared by installation and verification.
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { isDeepStrictEqual } = require('node:util');
const recordPath = 'reference/yawn-repair/runtime.json';
const interpolationException = 'Yawn exception: complete original preparation poses and fresh RIFE interpolation; see ' + recordPath + '.';
const previousInterpolationException = 'Yawn exception: corrected preparation poses and fresh RIFE interpolation; see ' + recordPath + '.';
const hashClip = clip => crypto.createHash('sha256').update(JSON.stringify(clip)).digest('hex');
const uniqueFiles = clip => [...new Set(clip.frames.map(frame => frame.file))].sort();

function applyYawnRepair(manifest, runtime, options = {}) {
  assert(manifest?.clips?.yawn && manifest?.clips?.idle?.frames?.length, 'The manifest needs yawn and idle clips.');
  assert(runtime?.clip && Array.isArray(runtime.frames), 'The yawn repair record needs clip and frames.');
  assert(/^[a-f0-9]{64}$/.test(runtime.sourceClipSha256 || ''), 'Invalid original yawn clip checksum.');
  assert(typeof runtime.revision === 'string' && runtime.revision.length, 'The repair revision is required.');
  const original = manifest.clips.yawn, repaired = runtime.clip;
  if (runtime.previousClipSha256 !== undefined)
    assert(/^[a-f0-9]{64}$/.test(runtime.previousClipSha256), 'Invalid previous yawn repair checksum.');
  if (options.replaceClipSha256 !== undefined)
    assert(/^[a-f0-9]{64}$/.test(options.replaceClipSha256), 'Invalid explicitly selected current clip checksum.');
  assert([runtime.sourceClipSha256, runtime.previousClipSha256, options.replaceClipSha256, hashClip(repaired)].includes(hashClip(original)),
    'Yawn changed concurrently; review before installing.');
  assert.equal(repaired.loop, false, 'Yawn must not loop.');
  assert(Array.isArray(repaired.frames) && repaired.frames.length === 264, 'Yawn needs exactly 264 frame slots.');
  const durations = repaired.frames.map(frame => frame.durationMs);
  assert.deepEqual(durations, original.frames.map(frame => frame.durationMs), 'The original yawn duration sequence changed.');
  assert(durations.every((value, index) => Number.isSafeInteger(value) &&
    value === Math.round((index + 1) * 1000 / 60) - Math.round(index * 1000 / 60)), 'Yawn must retain rounded 60 Hz timing.');
  assert.equal(durations.reduce((sum, value) => sum + value, 0), 4400, 'Yawn must last 4400 ms.');
  const idle = manifest.clips.idle.frames[0].file;
  assert.equal(repaired.frames[0].file, idle, 'Yawn must start at the shared idle.');
  assert.equal(repaired.frames.at(-1).file, idle, 'Yawn must end at the shared idle.');
  assert(repaired.frames.every(frame => typeof frame.file === 'string' && frame.file.startsWith('frames/') &&
    !frame.file.includes('\\') && !frame.file.split('/').some(part => !part || part === '..')), 'Invalid yawn frame path.');
  assert.deepEqual(runtime.frames.map(frame => frame.file).sort(), uniqueFiles(repaired),
    'The repair inventory must list each referenced PNG exactly once.');
  assert(runtime.frames.every(frame => /^[a-f0-9]{64}$/.test(frame.sha256 || '')), 'Invalid yawn PNG checksum.');
  assert(manifest.motion?.idleExpressions?.clips && manifest.sampling, 'Yawn metadata containers are missing.');

  const result = structuredClone(manifest);
  result.clips.yawn = structuredClone(repaired);
  result.motion.idleExpressions.clips.yawn = {
    ...result.motion.idleExpressions.clips.yawn,
    revision: runtime.revision, sourceRecord: recordPath,
    sourceHashes: structuredClone(runtime.sourceHashes || {}),
    frameSlots: repaired.frames.length, uniquePngs: runtime.frames.length, sampleFps: 60
  };
  result.sampling.clipOverrides = { ...result.sampling.clipOverrides,
    yawn: { targetFps: 60, sourceRecord: recordPath,
      method: runtime.revision === 'yawn-v2'
        ? 'Rebuild only yawn from corrected preparation composites; retain every original 60 fps phase and duration.'
        : 'Rebuild only yawn from complete source poses; retain every original 60 fps phase and duration.' }
  };
  // Preserve unrelated authoring notes while qualifying the old blanket claim.
  const description = (result.motion.interpolation || '').replace(/^No new interpolation:\s*/, 'Default clips: ')
    .replace(previousInterpolationException, '').replace(interpolationException, '').trim();
  const selectedDescription = runtime.revision === 'yawn-v2' ? previousInterpolationException : interpolationException;
  result.motion.interpolation = [description, selectedDescription].filter(Boolean).join(' ');
  return result;
}

function selfTest() {
  let checks = 0;
  const check = (condition, message) => { assert(condition, message); checks++; };
  const idle = 'frames/idle.png';
  const frames = Array.from({ length: 264 }, (_, index) => ({
    file: index === 0 || index === 263 ? idle : `frames/old-${index}.png`,
    durationMs: Math.round((index + 1) * 1000 / 60) - Math.round(index * 1000 / 60)
  }));
  const base = { clips: { idle: { loop: true, frames: [{ file: idle, durationMs: 1000 }] }, yawn: { loop: false, frames },
    work: { frames: [{ file: 'frames/work.png', durationMs: 1000 }] } },
    motion: { interpolation: 'No new interpolation: select existing PNGs.',
      idleExpressions: { clips: { yawn: { durationMs: 4400 }, work: { author: 'another process' } } } },
    sampling: { clipOverrides: { work: { sourceRecord: 'unrelated.json' } } } };
  const clip = { loop: false, frames: frames.map(frame => ({ ...frame, file: frame.file.replace('/old-', '/repaired-') })) };
  const runtime = { revision: 'yawn-v2', sourceClipSha256: hashClip(base.clips.yawn), clip,
    frames: uniqueFiles(clip).map(file => ({ file, sha256: 'a'.repeat(64) })), sourceHashes: { 'key.png': 'b'.repeat(64) } };
  const originalBase = structuredClone(base), originalRecord = structuredClone(runtime);
  const repaired = applyYawnRepair(base, runtime);
  check(isDeepStrictEqual(base, originalBase) && isDeepStrictEqual(runtime, originalRecord), 'Inputs were mutated.');
  check(isDeepStrictEqual(repaired.clips.yawn, clip), 'Repaired yawn is not installed.');
  check(isDeepStrictEqual(repaired.clips.work, base.clips.work) &&
    isDeepStrictEqual(repaired.motion.idleExpressions.clips.work, base.motion.idleExpressions.clips.work) &&
    isDeepStrictEqual(repaired.sampling.clipOverrides.work, base.sampling.clipOverrides.work), 'Unrelated work was changed.');
  check(isDeepStrictEqual(applyYawnRepair(repaired, runtime), repaired), 'Applying the repair twice is not idempotent.');
  check(repaired.motion.interpolation.includes(previousInterpolationException) && !repaired.motion.interpolation.startsWith('No new interpolation'),
    'Interpolation provenance still claims no new interpolation.');
  const concurrent = structuredClone(base); concurrent.clips.yawn.frames[1].file = 'frames/concurrent.png';
  assert.throws(() => applyYawnRepair(concurrent, runtime), /changed concurrently/); checks++;
  const badDuration = structuredClone(runtime); badDuration.clip.frames[1].durationMs++; badDuration.clip.frames[2].durationMs--;
  assert.throws(() => applyYawnRepair(base, badDuration), /duration sequence/); checks++;
  const badEndpoint = structuredClone(runtime); badEndpoint.clip.frames[263].file = 'frames/wrong.png';
  assert.throws(() => applyYawnRepair(base, badEndpoint), /shared idle/); checks++;
  const badInventory = structuredClone(runtime); badInventory.frames.push(badInventory.frames[0]);
  assert.throws(() => applyYawnRepair(base, badInventory), /exactly once/); checks++;
  const badLoop = structuredClone(runtime); badLoop.clip.loop = true;
  assert.throws(() => applyYawnRepair(base, badLoop), /must not loop/); checks++;
  const nextRecord = structuredClone(runtime);
  nextRecord.revision = 'yawn-v3'; nextRecord.previousClipSha256 = hashClip(runtime.clip);
  nextRecord.clip.frames = nextRecord.clip.frames.map(frame => ({ ...frame, file: frame.file.replace('/repaired-', '/complete-') }));
  nextRecord.frames = uniqueFiles(nextRecord.clip).map(file => ({ file, sha256: 'c'.repeat(64) }));
  const next = applyYawnRepair(repaired, nextRecord);
  check(isDeepStrictEqual(next.clips.yawn, nextRecord.clip), 'A recorded previous repair cannot be upgraded.');
  check(isDeepStrictEqual(next.clips.work, base.clips.work), 'An upgrade modified another action.');
  const restored = applyYawnRepair(next, runtime, { replaceClipSha256: hashClip(nextRecord.clip) });
  check(isDeepStrictEqual(restored, repaired), 'Explicit rollback did not restore the selected previous repair.');
  return { passed: true, checks };
}

module.exports = { applyYawnRepair, selfTest, recordPath };
if (require.main === module) {
  assert.deepEqual(process.argv.slice(2), ['--self-test'], 'Usage: node tools/yawn-runtime.cjs --self-test');
  console.log(JSON.stringify(selfTest(), null, 2));
}
