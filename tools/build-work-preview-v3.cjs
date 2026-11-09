// Candidate-only work loop: downward gaze, local RIFE blink, and rigid breathing.
// Writes the selected preview/artifact directories. Source art stays unchanged.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { interpolate } = require('./motion-interpolate.cjs');
const { translateY, compositeProps, dyAt, checkRigidTranslation } = require('./build-work-preview-v2.cjs');
const artwork = require('./work-artwork.cjs').loadWorkArtwork();
const exec = promisify(execFile);
const repo = path.resolve(__dirname, '..');
const assetRoot = path.join(repo, 'assets/strawberry-cat');
const output = artwork.previewRoot;
const artifactRoot = artwork.artworkArtifacts;
const width = 512, height = 512, fps = 60, durationMs = 9600;
const bodyPeriodMs = 4800, bodyExcursionPx = 2, factor = 32;
const blinkStartsMs = [1250, 6700], blinkDurationMs = 375;
const closeAtMs = 140, openAtMs = 157;
const pawRegions = [{ x:102, y:436, width:108, height:43 }, { x:214, y:436, width:112, height:43 }];
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const relative = file => path.relative(repo, file).replaceAll('\\', '/');
const mod = (value, period) => { const result = value % period; return result < 0 ? result + period : result; };

function blinkPhase(time) {
  if (time <= 0 || time >= blinkDurationMs) return time <= 0 ? 0 : factor * 2;
  if (time < closeAtMs) return factor * time / closeAtMs;
  if (time < openAtMs) return factor;
  return factor + factor * (time - openAtMs) / (blinkDurationMs - openAtMs);
}

// This mask is the only region permitted to change between blink poses.
// Premultiplied interpolation keeps RGBA well-defined at soft mask boundaries.
function registerEyes(base, pose, mask) {
  const result = Buffer.from(base);
  for (let i = 0; i < mask.length; i++) {
    const w = mask[i] / 255, p = i * 4;
    if (!w) continue;
    if (w === 1) { pose.copy(result, p, p, p + 4); continue; }
    const aa = base[p + 3] * (1 - w), ab = pose[p + 3] * w, alpha = aa + ab;
    result[p + 3] = Math.round(alpha);
    if (!result[p + 3]) { result.fill(0, p, p + 4); continue; }
    for (let c = 0; c < 3; c++) result[p + c] = Math.round((base[p + c] * aa + pose[p + c] * ab) / alpha);
  }
  return result;
}

function outsideMaskExact(base, pose, mask) {
  for (let i = 0; i < mask.length; i++) if (!mask[i]) {
    const p = i * 4;
    if (!base.subarray(p, p + 4).equals(pose.subarray(p, p + 4))) return false;
  }
  return true;
}

async function readImage(file) {
  const bytes = await fs.readFile(file);
  const image = await sharp(bytes).ensureAlpha().raw().toBuffer({ resolveWithObject:true });
  if (image.info.width !== width || image.info.height !== height) throw Error('Expected 512x512: ' + file);
  return { file, bytes, rgba:image.data, sha256:hash(bytes), metadata:await sharp(bytes).metadata() };
}

async function main() {
  const args = process.argv.slice(2), maskArg = args.indexOf('--mask');
  if (maskArg >= 0 && !args[maskArg + 1]) throw Error('--mask requires a PNG path');
  const maskPath = maskArg >= 0 ? path.resolve(args[maskArg + 1]) : path.join(output, 'keys/eye-mask.png');
  const paths = [path.join(output, 'keys/open.png'), path.join(output, 'keys/closed.png'), path.join(output, 'props.png'), maskPath];
  const [open, closed, propsImage, maskImage] = await Promise.all(paths.map(readImage));
  if (!open.metadata.hasAlpha || !closed.metadata.hasAlpha || !propsImage.metadata.hasAlpha) throw Error('Keys and props must have alpha');
  const manifestPath = path.join(assetRoot, 'manifest.json'), manifestBytes = await fs.readFile(manifestPath);
  const mask = Buffer.alloc(width * height);
  for (let i = 0; i < mask.length; i++) mask[i] = maskImage.rgba[i * 4 + (maskImage.metadata.hasAlpha ? 3 : 0)];
  const activeMaskPixels = mask.reduce((sum, value) => sum + Number(value > 0), 0);
  if (!activeMaskPixels || activeMaskPixels > width * height / 5) throw Error('Eye mask is empty or unexpectedly broad');
  for (let i = 0; i < mask.length; i++) {
    const alpha = i * 4 + 3;
    if (open.rgba[alpha] !== closed.rgba[alpha]) throw Error('Blink keys change silhouette alpha');
    if (mask[i] && open.rgba[alpha] !== 255) throw Error('Expected eye mask wholly inside opaque face');
  }
  const sourceInventory = [open, closed, propsImage, maskImage].map(item => ({ file:relative(item.file), sha256:item.sha256 }));
  const props = propsImage.rgba;
  const opaqueOffsets = [];
  for (let p = 0; p < props.length; p += 4) if (props[p + 3] === 255) opaqueOffsets.push(p);
  if (!opaqueOffsets.length) throw Error('No opaque prop pixels');
  await fs.mkdir(path.join(output, 'frames'), { recursive:true });
  await fs.mkdir(artifactRoot, { recursive:true });
  const registeredKeys = path.join(artifactRoot, 'registered-keys');
  await fs.mkdir(registeredKeys, { recursive:true });
  // Parent-provided keys are already feathered. Never apply the mask twice.
  const localClosed = closed.rgba;
  if (!outsideMaskExact(open.rgba, localClosed, mask)) throw Error('Closed key changes pixels outside the supplied eye mask');
  const openPath = path.join(registeredKeys, 'open.png'), closedPath = path.join(registeredKeys, 'closed.png');
  await fs.copyFile(open.file, openPath);
  await fs.copyFile(closed.file, closedPath);
  const fingerprint = hash(Buffer.from(JSON.stringify({ sourceInventory, factor, temporalTta:true, localEyesOnly:true, featheredKeys:true, version:3 })));
  const inferenceRoot = path.join(artifactRoot, 'rife-' + fingerprint.slice(0, 12));
  let inference;
  try { inference = JSON.parse(await fs.readFile(path.join(inferenceRoot, 'interpolation-report.json'), 'utf8')); }
  catch (error) {
    if (error.code !== 'ENOENT') throw error;
    console.log('Interpolating new downward-gaze blink with RIFE 4.6');
    inference = await interpolate({ frames:[openPath,closedPath,openPath], outputDirectory:inferenceRoot, factor, gpu:0, temporalTta:true });
  }
  if (inference.outputFrames !== 65 || inference.factor !== factor || inference.model !== 'rife-v4.6' ||
      inference.keyframeChecks?.length !== 3 || !inference.keyframeChecks.every(check => check.exactRgbMatch)) throw Error('Unexpected or unverified RIFE output');
  const blackRoot = path.join(inferenceRoot, 'work/black-output');
  const blackFiles = (await fs.readdir(blackRoot)).filter(file => file.endsWith('.png')).sort();
  if (blackFiles.length !== factor * 3) throw Error('Unexpected black-matte output count');
  // Upstream numbering begins at 1. Address sorted files by phase, including
  // the three exact keys at 0, 32 and 64, then ignore the repeated-pose tail.
  for (const [index, key] of [[0,open.rgba],[factor,localClosed],[factor * 2,open.rgba]]) {
    const black = (await readImage(path.join(blackRoot, blackFiles[index]))).rgba;
    for (let i = 0; i < mask.length; i++) if (mask[i]) for (let c = 0; c < 3; c++)
      if (black[i * 4 + c] !== key[i * 4 + c]) throw Error('Black-matte key mapping mismatch at phase ' + index);
  }
  const blinkDirectory = path.join(output, 'blink');
  await fs.mkdir(blinkDirectory, { recursive:true });
  const blinkFrames = [], blinkInventory = [];
  let nonEyePixelsExact = true, alphaExact = true;
  for (let index = 0; index <= factor * 2; index++) {
    let source;
    if (index === 0 || index === factor * 2) source = open.rgba;
    else if (index === factor) source = localClosed;
    else {
      // Both keys are fully opaque inside this eye mask. Dual-matte motion
      // estimates can disagree and invent holes; use black-matte RGB directly
      // while retaining every alpha byte from the original open pose.
      const black = (await readImage(path.join(blackRoot, blackFiles[index]))).rgba;
      source = Buffer.from(open.rgba);
      for (let i = 0; i < mask.length; i++) if (mask[i]) {
        const p = i * 4;
        black.copy(source, p, p, p + 3);
      }
    }
    let rgba;
    if (index % factor === 0) rgba = Buffer.from(source);
    else {
      // The supplied keys already include their soft edge. Preserve that edge's
      // baseline and mask only the RIFE residual, so it converges to every exact
      // key instead of shrinking the same feather twice. Solid mask interiors
      // use the unmodified RIFE image; protected pixels still equal open RGBA.
      const closedWeight = index < factor ? index / factor : (factor * 2 - index) / factor;
      const support = Buffer.from(mask.map(value => value ? Math.round(closedWeight * 255) : 0));
      const baseline = registerEyes(open.rgba, localClosed, support);
      rgba = registerEyes(baseline, source, mask);
    }
    if (!outsideMaskExact(open.rgba, rgba, mask)) nonEyePixelsExact = false;
    for (let p = 3; p < rgba.length; p += 4) if (rgba[p] !== open.rgba[p]) { alphaExact = false; break; }
    const file = 'blink/blink-' + String(index).padStart(2, '0') + '.png';
    const encoded = await sharp(rgba, { raw:{ width,height,channels:4 } }).png().toBuffer();
    await fs.writeFile(path.join(output, file), encoded);
    blinkFrames.push(rgba);
    blinkInventory.push({ index,file,pngSha256:hash(encoded),rgbaSha256:hash(rgba) });
  }
  const blinkEndpointsExact = blinkFrames[0].equals(open.rgba) && blinkFrames.at(-1).equals(open.rgba);
  const blinkClosedKeyExact = blinkFrames[factor].equals(closed.rgba);
  if (!nonEyePixelsExact || !alphaExact || !blinkEndpointsExact || !blinkClosedKeyExact) throw Error('Blink changed protected pixels, source alpha or a source key');

  function selectedCat(time) {
    const local = mod(time, durationMs);
    for (const start of blinkStartsMs) if (local >= start && local < start + blinkDurationMs) {
      const phase = blinkPhase(local - start), index = Math.round(phase);
      return { rgba:blinkFrames[index], file:blinkInventory[index].file, blinkIndex:index, phase, blinkTimeMs:local - start };
    }
    return { rgba:open.rgba, file:'keys/open.png', blinkIndex:null, phase:null, blinkTimeMs:null };
  }
  function render(time) {
    const local = mod(time, durationMs), selected = selectedCat(local), dy = dyAt(local);
    const cat = translateY(selected.rgba, width, height, dy);
    return { ...selected, rgba:compositeProps(cat, props), cat, dy };
  }
  const pawOcclusion = [];
  for (const dy of [0, -bodyExcursionPx]) for (let regionIndex = 0; regionIndex < pawRegions.length; regionIndex++) {
    const region = pawRegions[regionIndex];
    let catPixels = 0, uncoveredPixels = 0, maxRemainingAlpha = 0;
    for (let y = region.y; y < region.y + region.height; y++) for (let x = region.x; x < region.x + region.width; x++) {
      const sourceAlpha = open.rgba[(y * width + x) * 4 + 3];
      if (sourceAlpha <= 16) continue;
      catPixels++;
      const targetY = y + dy, propAlpha = targetY >= 0 && targetY < height ? props[(targetY * width + x) * 4 + 3] : 0;
      const remaining = sourceAlpha * (1 - propAlpha / 255);
      maxRemainingAlpha = Math.max(maxRemainingAlpha, remaining);
      if (remaining > 3) uncoveredPixels++;
    }
    pawOcclusion.push({ regionIndex,region,dy,catPixels,uncoveredPixels,maxRemainingAlpha,pass:uncoveredPixels === 0 });
  }
  const pool = new Map(), clip = { name:'work_loop',loop:true,durationMs,frames:[] }, slots = [];
  const slotCount = Math.round(durationMs * fps / 1000), sequence = path.join(artifactRoot, 'video-frames');
  await fs.mkdir(sequence, { recursive:true });
  let opaquePropsExact = true, firstRgba = null, lastRgba = null;
  for (let index = 0; index < slotCount; index++) {
    const time = index * 1000 / fps, start = Math.round(time), end = Math.round((index + 1) * 1000 / fps);
    const result = render(time), digest = hash(result.rgba);
    if (index === 0) firstRgba = result.rgba;
    if (index === slotCount - 1) lastRgba = result.rgba;
    for (const p of opaqueOffsets) if (!result.rgba.subarray(p, p + 4).equals(props.subarray(p, p + 4))) { opaquePropsExact = false; break; }
    if (!pool.has(digest)) {
      const file = 'frames/frame-' + digest.slice(0, 20) + '.png';
      const encoded = await sharp(result.rgba, { raw:{ width,height,channels:4 } }).png({ compressionLevel:9,adaptiveFiltering:true }).toBuffer();
      await fs.writeFile(path.join(output, file), encoded);
      pool.set(digest, { file,rgbaSha256:digest,pngSha256:hash(encoded),bytes:encoded.length });
    }
    const record = pool.get(digest);
    clip.frames.push({ file:record.file,durationMs:end - start });
    slots.push({ index,timeMs:time,durationMs:end - start,catFile:result.file,blinkIndex:result.blinkIndex,blinkPhase:result.phase,blinkTimeMs:result.blinkTimeMs,dyPx:result.dy,file:record.file });
    await fs.copyFile(path.join(output, record.file), path.join(sequence, String(index).padStart(5, '0') + '.png'));
    if (index % 120 === 0) console.log('Rendered ' + index + ' / ' + slotCount);
  }
  const virtualLoopBoundaryExact = firstRgba.equals(render(durationMs).rgba);
  const blinkMotionCheck = blinkStartsMs.map(start => {
    const blinkSlots = slots.filter(slot => slot.timeMs >= start && slot.timeMs < start + blinkDurationMs);
    return { startMs:start,endMs:start + blinkDurationMs,startDy:dyAt(start),endDy:dyAt(start + blinkDurationMs),
      displacementDuringBlink:dyAt(start + blinkDurationMs) - dyAt(start),bodyClockReset:false,
      usesAbsoluteBodyTime:blinkSlots.every(slot => slot.dyPx === dyAt(slot.timeMs)),
      displayedClosedMs:blinkSlots.filter(slot => slot.blinkIndex === factor).reduce((sum, slot) => sum + slot.durationMs, 0),
      returnedOpenExact:selectedCat(start + blinkDurationMs).rgba.equals(open.rgba),
      selectedFrames:blinkSlots.map(slot => ({ timeMs:slot.blinkTimeMs,index:slot.blinkIndex })) };
  });
  const bodyClockContinuous = blinkMotionCheck.every(check => check.usesAbsoluteBodyTime && Math.abs(check.displacementDuringBlink) > .01 && check.returnedOpenExact);
  if (!virtualLoopBoundaryExact || !opaquePropsExact || !bodyClockContinuous || clip.frames.reduce((sum, frame) => sum + frame.durationMs, 0) !== durationMs)
    throw Error('Loop/props/body/duration check failed: ' + JSON.stringify({ virtualLoopBoundaryExact,opaquePropsExact,bodyClockContinuous,blinkMotionCheck,duration:clip.frames.reduce((sum, frame) => sum + frame.durationMs, 0) }));
  const metadata = { version:artwork.artworkVersion,status:'candidate-not-installed',canvas:{ width,height },clip,
    bodyMotion:{ type:'whole-cat-vertical-translation',periodMs:bodyPeriodMs,rangePx:[-bodyExcursionPx,0],formula:'-2*(1-cos(2*pi*t/4800))/2',rotation:0,scaling:1 },
    blinking:{ source:'new downward-gaze open and closed keys',engine:'RIFE 4.6',factor,durationMs:blinkDurationMs,startsMs:blinkStartsMs,closeAtMs,closedHoldMs:openAtMs - closeAtMs,independentOfBodyClock:true,eyeMask:relative(maskPath),newOpenEndpointsExact:blinkEndpointsExact } };
  await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(metadata, null, 2) + '\n');
  await fs.writeFile(path.join(output, 'timeline.json'), JSON.stringify(slots, null, 2) + '\n');
  await fs.writeFile(path.join(output, 'blink/manifest.json'), JSON.stringify({ fps,phaseCount:65,factor,closeAtMs,openAtMs,durationMs:blinkDurationMs,frames:blinkInventory }, null, 2) + '\n');
  await writeContact(render, [0,1250,1333.3333333,1400,1450,1516.6666667,1616.6666667,2400,4800,6700,6850,9000], path.join(output, 'motion-contact.png'));
  await writeContact(time => ({ rgba:compositeProps(blinkFrames[Math.round(blinkPhase(time))], props) }), [0,50,100,140,157,200,275,350,375], path.join(output, 'blink-contact.png'));
  await writePreview(clip);
  const ffargs = ['-hide_banner','-loglevel','error','-y','-framerate',String(fps),'-i',path.join(sequence, '%05d.png')];
  await exec('ffmpeg', [...ffargs,'-filter_complex',`color=c=0xf3efe8:s=${width}x${height}:r=${fps}[bg];[bg][0:v]overlay=shortest=1:format=auto,format=yuv420p[out]`,'-map','[out]','-frames:v',String(slotCount),'-c:v','libx264','-crf','18','-movflags','+faststart',path.join(output, 'work-loop.mp4')], { windowsHide:true,maxBuffer:4 * 1024 * 1024 });
  await exec('ffmpeg', ['-hide_banner','-loglevel','error','-y','-i',path.join(output, 'work-loop.mp4'),'-filter_complex','fps=30,scale=384:384:flags=lanczos,split[a][b];[a]palettegen=max_colors=256[p];[b][p]paletteuse=dither=sierra2_4a','-loop','0',path.join(output, 'work-loop.gif')], { windowsHide:true,maxBuffer:4 * 1024 * 1024 });
  const probe = JSON.parse((await exec('ffprobe', ['-v','error','-select_streams','v:0','-show_entries','stream=width,height,r_frame_rate,nb_frames,duration','-of','json',path.join(output, 'work-loop.mp4')], { windowsHide:true,maxBuffer:1024 * 1024 })).stdout).streams[0];
  const encodedVideoValid = probe.width === width && probe.height === height && Number(probe.nb_frames) === slotCount && Math.abs(Number(probe.duration) - durationMs / 1000) < .001 && probe.r_frame_rate === '60/1';
  const sourceUnchanged = (await fs.readFile(manifestPath)).equals(manifestBytes) &&
    (await Promise.all(sourceInventory.map(async record => hash(await fs.readFile(path.join(repo, record.file))) === record.sha256))).every(Boolean);
  const qa = { date:'2026-11-09',candidateOnly:true,canvas:{ width,height },slots:slotCount,uniqueFrames:pool.size,durationMs,sampleFps:fps,
    sourceManifest:{ file:relative(manifestPath),sha256:hash(manifestBytes) },sourceInventory,sourceUnchanged,
    eyeMask:{ file:relative(maskPath),channel:maskImage.metadata.hasAlpha ? 'alpha' : 'red',activePixels:activeMaskPixels },
    inference:{ fingerprint,directory:relative(inferenceRoot),model:inference.model,factor,frames:inference.outputFrames,keyframeChecks:inference.keyframeChecks,
      opaqueEyeRgbSource:'work/black-output',blackMatteKeyMappingExact:true,alphaSource:'exact original open',
      registration:'Feathered source-key baseline plus masked RIFE residual; exact source keys and alpha; original open RGBA outside eye mask' },
    nonEyePixelsExact,alphaExact,blinkEndpointsExact,blinkClosedKeyExact,blinkFrameCount:blinkFrames.length,opaquePropsExact,opaquePropPixels:opaqueOffsets.length,
    rigidTranslationCheck:checkRigidTranslation(),blinkMotionCheck,bodyClockContinuous,
    loop:{ virtualBoundaryExact:virtualLoopBoundaryExact,forcedLastFrameCopy:false,lastSampleTimeMs:(slotCount - 1) * 1000 / fps,lastSampleDy:dyAt((slotCount - 1) * 1000 / fps),lastSampleEqualsFirst:firstRgba.equals(lastRgba),dyAtStart:dyAt(0),dyAtVirtualEnd:dyAt(durationMs),velocityAtStart:0,velocityAtVirtualEnd:-bodyExcursionPx * Math.PI / bodyPeriodMs * Math.sin(2 * Math.PI * durationMs / bodyPeriodMs) },
    pawOcclusion,pawOcclusionPass:pawOcclusion.every(check => check.pass),encodedVideo:probe,encodedVideoValid,
    encodedPngBytes:[...pool.values()].reduce((sum, frame) => sum + frame.bytes, 0),frames:[...pool.values()] };
  qa.passed = sourceUnchanged && nonEyePixelsExact && alphaExact && blinkEndpointsExact && blinkClosedKeyExact && opaquePropsExact && bodyClockContinuous && virtualLoopBoundaryExact && qa.pawOcclusionPass && encodedVideoValid && blinkMotionCheck.every(check => check.displayedClosedMs <= 17);
  await fs.writeFile(path.join(output, 'verification.json'), JSON.stringify(qa, null, 2) + '\n');
  console.log(JSON.stringify({ output,passed:qa.passed,slots:slotCount,uniqueFrames:pool.size,durationMs,nonEyePixelsExact,alphaExact,blinkEndpointsExact,bodyClockContinuous,pawOcclusionPass:qa.pawOcclusionPass,closedMs:blinkMotionCheck.map(check => check.displayedClosedMs),opaquePropsExact,virtualLoopBoundaryExact,encodedVideoValid,pngMiB:Math.round(qa.encodedPngBytes / 1048576 * 10) / 10 }, null, 2));
  if (!qa.passed) process.exitCode = 2;
}

async function writeContact(render, times, destination) {
  const tiles = [];
  for (let i = 0; i < times.length; i++) {
    const input = await sharp(render(times[i]).rgba, { raw:{ width,height,channels:4 } }).resize(256,256).flatten({ background:i >= 8 ? '#252936' : '#f3efe8' }).png().toBuffer();
    tiles.push({ input,left:i % 4 * 256,top:Math.floor(i / 4) * 256 });
  }
  await sharp({ create:{ width:1024,height:Math.ceil(times.length / 4) * 256,channels:4,background:'#f3efe8' } }).composite(tiles).png().toFile(destination);
}

async function writePreview(clip) {
  const html = `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>草莓猫 · 工作循环 v${artwork.artworkVersion}</title><style>*{box-sizing:border-box}body{margin:0;background:#f5f0e8;color:#473c35;font:15px system-ui;min-height:100vh;display:grid;place-items:center}.card{text-align:center;padding:24px}h1{font-size:23px;margin:0 0 6px}p{margin:0 0 12px;color:#786b61}canvas{width:min(512px,82vw);height:auto;display:block;margin:auto}button,select{font:inherit;background:#fffaf3;color:inherit;border:1px solid #d5c5b5;border-radius:10px;padding:8px 12px;margin:5px}input{width:min(440px,80vw)}.dark{background:#242934;color:#eee}.dark p{color:#bbb}.status{font-variant-numeric:tabular-nums;font-size:13px;margin-top:9px}</style><main class="card"><h1>趴着陪你工作</h1><p>看着电脑 · 轻轻呼吸 · 自然眨眼</p><canvas width="512" height="512"></canvas><div><button id="play">载入中</button><button id="restart">重新播放</button><select id="speed"><option value="1">正常速度</option><option value=".5">半速检查</option></select><button id="bg">切换背景</button></div><input id="scrub" type="range" min="0" max="9599" value="0"><div class="status" id="status"></div></main><script>const clip=${JSON.stringify(clip)},cache=new Map(),canvas=document.querySelector('canvas'),ctx=canvas.getContext('2d'),play=document.getElementById('play'),scrub=document.getElementById('scrub'),status=document.getElementById('status');let time=0,last=null,paused=true,ready=false;function frame(t){let end=0;for(const f of clip.frames){end+=f.durationMs;if(t<end)return f.file}return clip.frames.at(-1).file}function draw(){ctx.clearRect(0,0,512,512);const img=cache.get(frame(time));if(img)ctx.drawImage(img,0,0);scrub.value=time;status.textContent=(time/1000).toFixed(2)+' / 9.60 秒';play.textContent=paused?'播放工作循环':'暂停预览'}play.onclick=()=>{if(ready){paused=!paused;last=null;draw()}};document.getElementById('restart').onclick=()=>{if(ready){time=0;paused=false;last=null;draw()}};document.getElementById('bg').onclick=()=>document.body.classList.toggle('dark');scrub.oninput=()=>{time=+scrub.value;paused=true;last=null;draw()};function tick(now){const delta=last===null?0:Math.min(100,now-last);last=now;if(ready&&!paused&&!document.hidden){time=(time+delta*+document.getElementById('speed').value)%clip.durationMs;draw()}requestAnimationFrame(tick)}document.addEventListener('visibilitychange',()=>last=null);(async()=>{try{let n=0;const files=[...new Set(clip.frames.map(f=>f.file))];await Promise.all(Array.from({length:4},async()=>{while(n<files.length){const f=files[n++],img=new Image();img.src=f;await img.decode();cache.set(f,img)}}));ready=true;paused=false;draw();requestAnimationFrame(tick)}catch(e){status.textContent='载入失败：'+e.message}})();</script></html>`;
  await fs.writeFile(path.join(output, 'index.html'), html);
}

if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1; });
module.exports = { blinkPhase,registerEyes,outsideMaskExact };
