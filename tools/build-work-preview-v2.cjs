// Candidate-only work loop: original loaf pose + independent blink + rigid breathing.
// Reads approved PNGs; never writes the application manifest, sources, or v1 output.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const exec = promisify(execFile);
const repo = path.resolve(__dirname, '..');
const assetRoot = path.join(repo, 'assets/strawberry-cat');
const output = path.join(assetRoot, 'work-preview-v2');
const artifactRoot = path.join(repo, 'artifacts/work-v2');
const width = 512, height = 512, fps = 60, durationMs = 9600;
const bodyPeriodMs = 4800, bodyExcursionPx = 2;
const blinkStartsMs = [1250, 6700];
const pawRegions = [{ x:102, y:436, width:108, height:43 }, { x:214, y:436, width:112, height:43 }];
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const relative = file => path.relative(repo, file).replaceAll('\\', '/');
const mod = (value, period) => ((value % period) + period) % period;
const dyAt = time => -bodyExcursionPx * (1 - Math.cos(2 * Math.PI * time / bodyPeriodMs)) / 2;

// One rigid y translation applied to every cat pixel. Interpolate in premultiplied
// RGBA so semi-transparent fur edges retain their color over light and dark grounds.
function translateY(source, w, h, dy) {
  const result = Buffer.alloc(source.length);
  for (let y = 0; y < h; y++) {
    const sy = y - dy, lo = Math.floor(sy), hi = lo + 1, fraction = sy - lo;
    const weight0 = 1 - fraction, weight1 = fraction;
    for (let x = 0; x < w; x++) {
      const out = (y*w+x)*4, p0 = (lo*w+x)*4, p1 = (hi*w+x)*4;
      const a0 = lo >= 0 && lo < h ? source[p0+3] : 0;
      const a1 = hi >= 0 && hi < h ? source[p1+3] : 0;
      const alpha = a0*weight0+a1*weight1, byteAlpha = Math.round(alpha);
      result[out+3] = byteAlpha;
      if (!byteAlpha) continue;
      for (let c = 0; c < 3; c++) {
        const v0 = a0 ? source[p0+c] : 0, v1 = a1 ? source[p1+c] : 0;
        result[out+c] = Math.round((v0*a0*weight0+v1*a1*weight1)/alpha);
      }
    }
  }
  return result;
}

function compositeProps(cat, props) {
  const result = Buffer.alloc(cat.length);
  for(let o=0;o<cat.length;o+=4) {
    const pa=props[o+3], ca=cat[o+3];
    if(pa===255) {props.copy(result,o,o,o+4);continue;}
    if(pa===0) {cat.copy(result,o,o,o+4);continue;}
    const remainder=1-pa/255, alpha=pa+ca*remainder;
    result[o+3]=Math.round(alpha);
    if(!result[o+3]) continue;
    for(let c=0;c<3;c++) result[o+c]=Math.round((props[o+c]*pa+cat[o+c]*ca*remainder)/alpha);
  }
  return result;
}

function checkRigidTranslation() {
  const w=8,h=24,source=Buffer.alloc(w*h*4),markers=[[1,4],[3,10],[5,17]],dy=-.73;
  for(const [x,y] of markers) source.set([93,173,219,255],(y*w+x)*4);
  const moved=translateY(source,w,h,dy), measured=[];
  for(const [x,y] of markers) {
    let sum=0,moment=0;
    for(let yy=0;yy<h;yy++) {const a=moved[(yy*w+x)*4+3];sum+=a;moment+=a*yy;}
    measured.push(moment/sum-y);
  }
  if(measured.some(value=>Math.abs(value-dy)>.01)) throw Error('Rigid translation fixture failed');
  return {requestedDy:dy,markerShifts:measured,maxErrorPx:Math.max(...measured.map(value=>Math.abs(value-dy))),headBodyTailUseSameTransform:true};
}

async function main() {
  const rigidTranslationCheck=checkRigidTranslation();
  await fs.mkdir(path.join(output,'frames'),{recursive:true});
  await fs.mkdir(artifactRoot,{recursive:true});
  const manifestPath=path.join(assetRoot,'manifest.json'),propsPath=path.join(output,'props.png');
  const manifestBytes=await fs.readFile(manifestPath);
  const manifest=JSON.parse(manifestBytes.toString('utf8').replace(/^\uFEFF/,''));
  const propsBytes=await fs.readFile(propsPath);
  const propsImage=await sharp(propsBytes).ensureAlpha().raw().toBuffer({resolveWithObject:true});
  const propsMetadata=await sharp(propsBytes).metadata();
  if(!propsMetadata.hasAlpha || propsImage.info.width!==width || propsImage.info.height!==height) throw Error('props.png must be a transparent 512x512 PNG');
  const props=propsImage.data,opaqueOffsets=[];
  for(let o=0;o<props.length;o+=4) if(props[o+3]===255) opaqueOffsets.push(o);
  if(!opaqueOffsets.length) throw Error('No opaque prop pixels found');
  const idleFile=manifest.clips.idle.frames[0].file,blink=manifest.clips.blink;
  const blinkDuration=blink.frames.reduce((sum,frame)=>sum+frame.durationMs,0);
  if(blinkDuration!==375) throw Error('Expected the approved 375ms blink');
  const images=new Map(),sourceInventory=[];
  for(const file of new Set([idleFile,...blink.frames.map(frame=>frame.file)])) {
    const absolute=path.resolve(assetRoot,file);
    if(!absolute.toLowerCase().startsWith((assetRoot+path.sep).toLowerCase())) throw Error('Source escapes asset root');
    const bytes=await fs.readFile(absolute),image=await sharp(bytes).ensureAlpha().raw().toBuffer({resolveWithObject:true});
    if(image.info.width!==width || image.info.height!==height) throw Error('Cat canvas mismatch: '+file);
    images.set(file,image.data);
    sourceInventory.push({file:relative(absolute),sha256:hash(bytes)});
  }
  function selectedCat(time) {
    const local=mod(time,durationMs);
    for(const start of blinkStartsMs) if(local>=start && local<start+blinkDuration) {
      let end=0;
      for(const frame of blink.frames) {end+=frame.durationMs;if(local-start<end)return frame.file;}
    }
    return idleFile;
  }
  function render(time) {
    const local=mod(time,durationMs),file=selectedCat(local),dy=dyAt(local);
    const cat=translateY(images.get(file),width,height,dy);
    return {rgba:compositeProps(cat,props),cat,file,dy};
  }
  const pawOcclusion=[];
  for(const dy of [0,-bodyExcursionPx]) for(let regionIndex=0;regionIndex<pawRegions.length;regionIndex++) {
    const region=pawRegions[regionIndex],idle=images.get(idleFile);
    let catPixels=0,uncoveredPixels=0,maxRemainingAlpha=0;
    for(let y=region.y;y<region.y+region.height;y++) for(let x=region.x;x<region.x+region.width;x++) {
      const sourceAlpha=idle[(y*width+x)*4+3];
      if(sourceAlpha<=16) continue;
      catPixels++;
      const targetY=y+dy,propAlpha=targetY>=0 && targetY<height?props[(targetY*width+x)*4+3]:0;
      const remaining=sourceAlpha*(1-propAlpha/255);
      maxRemainingAlpha=Math.max(maxRemainingAlpha,remaining);
      if(remaining>3) uncoveredPixels++;
    }
    pawOcclusion.push({regionIndex,region,dy,catPixels,uncoveredPixels,maxRemainingAlpha,pass:uncoveredPixels===0});
  }
  const pool=new Map(),clip={name:'work_loop',loop:true,durationMs,frames:[]},slots=[],slotCount=Math.round(durationMs*fps/1000);
  const sequence=path.join(artifactRoot,'video-frames');
  await fs.mkdir(sequence,{recursive:true});
  let opaquePropsExact=true,firstRgba=null,lastRgba=null;
  for(let i=0;i<slotCount;i++) {
    const time=i*1000/fps,start=Math.round(time),end=Math.round((i+1)*1000/fps);
    const result=render(time),digest=hash(result.rgba);
    if(i===0) firstRgba=result.rgba;
    if(i===slotCount-1) lastRgba=result.rgba;
    for(const o of opaqueOffsets) if(!result.rgba.subarray(o,o+4).equals(props.subarray(o,o+4))) {opaquePropsExact=false;break;}
    if(!pool.has(digest)) {
      const file='frames/frame-'+digest.slice(0,20)+'.png';
      const encoded=await sharp(result.rgba,{raw:{width,height,channels:4}}).png({compressionLevel:9,adaptiveFiltering:true}).toBuffer();
      await fs.writeFile(path.join(output,file),encoded);
      pool.set(digest,{file,rgbaSha256:digest,pngSha256:hash(encoded),bytes:encoded.length});
    }
    const record=pool.get(digest);
    clip.frames.push({file:record.file,durationMs:end-start});
    slots.push({index:i,timeMs:time,durationMs:end-start,catFile:result.file,dyPx:result.dy,file:record.file});
    await fs.copyFile(path.join(output,record.file),path.join(sequence,String(i).padStart(5,'0')+'.png'));
    if(i%120===0) console.log('Rendered '+i+' / '+slotCount);
  }
  const virtualEnd=render(durationMs).rgba;
  const virtualLoopBoundaryExact=firstRgba.equals(virtualEnd);
  if(!virtualLoopBoundaryExact || !opaquePropsExact || clip.frames.reduce((sum,frame)=>sum+frame.durationMs,0)!==durationMs) throw Error('Animation boundary, static props or duration check failed');
  const blinkMotionCheck=blinkStartsMs.map(start=>({startMs:start,endMs:start+blinkDuration,startDy:dyAt(start),endDy:dyAt(start+blinkDuration),displacementDuringBlink:dyAt(start+blinkDuration)-dyAt(start),bodyClockReset:false}));
  if(blinkMotionCheck.some(check=>Math.abs(check.displacementDuringBlink)<.01)) throw Error('Blink has no body motion');
  const sourceUnchanged=(await fs.readFile(manifestPath)).equals(manifestBytes) && (await fs.readFile(propsPath)).equals(propsBytes) &&
    (await Promise.all(sourceInventory.map(async record=>hash(await fs.readFile(path.join(repo,record.file)))===record.sha256))).every(Boolean);
  if(!sourceUnchanged) throw Error('Source changed during rendering');
  const metadata={version:2,status:'candidate-not-installed',canvas:{width,height},clip,
    bodyMotion:{type:'whole-cat-vertical-translation',periodMs:bodyPeriodMs,rangePx:[-bodyExcursionPx,0],formula:'-2*(1-cos(2*pi*t/4800))/2',rotation:0,scaling:1},
    blinking:{source:'approved 0.3.4 blink',durationMs:blinkDuration,startsMs:blinkStartsMs,independentOfBodyClock:true}};
  await fs.writeFile(path.join(output,'manifest.json'),JSON.stringify(metadata,null,2)+'\n');
  await fs.writeFile(path.join(output,'timeline.json'),JSON.stringify(slots,null,2)+'\n');
  const stillTimes=[0,1250,1400,1600,2400,4800,6700,6880,7050,7200,9000,durationMs-1000/fps];
  const contact=[];
  for(let i=0;i<stillTimes.length;i++) {
    const background=i>=8?'#252936':'#f3efe8';
    const tile=await sharp(render(stillTimes[i]).rgba,{raw:{width,height,channels:4}}).resize(256,256).flatten({background}).png().toBuffer();
    contact.push({input:tile,left:i%4*256,top:Math.floor(i/4)*256});
  }
  await sharp({create:{width:1024,height:768,channels:4,background:'#f3efe8'}}).composite(contact).png().toFile(path.join(output,'motion-contact.png'));
  await writePreview(clip);
  const ffargs=['-hide_banner','-loglevel','error','-y','-framerate',String(fps),'-i',path.join(sequence,'%05d.png')];
  await exec('ffmpeg',[...ffargs,'-filter_complex',`color=c=0xf3efe8:s=${width}x${height}:r=${fps}[bg];[bg][0:v]overlay=shortest=1:format=auto,format=yuv420p[out]`,'-map','[out]','-frames:v',String(slotCount),'-c:v','libx264','-crf','18','-movflags','+faststart',path.join(output,'work-loop.mp4')],{windowsHide:true,maxBuffer:4*1024*1024});
  await exec('ffmpeg',['-hide_banner','-loglevel','error','-y','-i',path.join(output,'work-loop.mp4'),'-filter_complex','fps=30,scale=384:384:flags=lanczos,split[a][b];[a]palettegen=max_colors=256[p];[b][p]paletteuse=dither=sierra2_4a','-loop','0',path.join(output,'work-loop.gif')],{windowsHide:true,maxBuffer:4*1024*1024});
  const probe=JSON.parse((await exec('ffprobe',['-v','error','-select_streams','v:0','-show_entries','stream=width,height,r_frame_rate,nb_frames,duration','-of','json',path.join(output,'work-loop.mp4')],{windowsHide:true,maxBuffer:1024*1024})).stdout).streams[0];
  const encodedVideoValid=Number(probe.nb_frames)===slotCount && Math.abs(Number(probe.duration)-durationMs/1000)<.001 && probe.r_frame_rate==='60/1';
  const qa={date:'2026-11-09',candidateOnly:true,canvas:{width,height},slots:slotCount,uniqueFrames:pool.size,durationMs,sampleFps:fps,
    sourceManifest:{file:relative(manifestPath),sha256:hash(manifestBytes)},props:{file:relative(propsPath),sha256:hash(propsBytes),opaquePixels:opaqueOffsets.length},sourceInventory,
    sourceUnchanged,opaquePropsExact,rigidTranslationCheck,blinkMotionCheck,
    loop:{virtualBoundaryExact:virtualLoopBoundaryExact,forcedLastFrameCopy:false,lastSampleTimeMs:(slotCount-1)*1000/fps,lastSampleDy:dyAt((slotCount-1)*1000/fps),lastSampleEqualsFirst:firstRgba.equals(lastRgba),dyAtStart:dyAt(0),dyAtVirtualEnd:dyAt(durationMs),velocityAtStart:0,velocityAtVirtualEnd:-bodyExcursionPx*Math.PI/bodyPeriodMs*Math.sin(2*Math.PI*durationMs/bodyPeriodMs)},
    pawOcclusion,pawOcclusionPass:pawOcclusion.every(check=>check.pass),encodedVideo:probe,encodedVideoValid,
    encodedPngBytes:[...pool.values()].reduce((sum,frame)=>sum+frame.bytes,0),frames:[...pool.values()]};
  qa.passed=sourceUnchanged && opaquePropsExact && virtualLoopBoundaryExact && qa.pawOcclusionPass && encodedVideoValid;
  await fs.writeFile(path.join(output,'verification.json'),JSON.stringify(qa,null,2)+'\n');
  console.log(JSON.stringify({output,passed:qa.passed,slots:slotCount,uniqueFrames:pool.size,durationMs,pawOcclusion,opaquePropsExact,virtualLoopBoundaryExact,forcedLastFrameCopy:false,pngMiB:Math.round(qa.encodedPngBytes/1048576*10)/10},null,2));
  if(!qa.passed) process.exitCode=2;
}

async function writePreview(clip) {
  const html=`<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>草莓猫 · 工作循环第二版</title><style>*{box-sizing:border-box}body{margin:0;background:#f5f0e8;color:#473c35;font:15px system-ui;min-height:100vh;display:grid;place-items:center}.card{text-align:center;padding:24px}h1{font-size:23px;margin:0 0 6px}p{margin:0 0 12px;color:#786b61}canvas{width:min(512px,82vw);height:auto;display:block;margin:auto}button,select{font:inherit;background:#fffaf3;color:inherit;border:1px solid #d5c5b5;border-radius:10px;padding:8px 12px;margin:5px}input{width:min(440px,80vw)}.dark{background:#242934;color:#eee}.dark p{color:#bbb}.status{font-variant-numeric:tabular-nums;font-size:13px;margin-top:9px}</style><main class="card"><h1>趴着陪你工作</h1><p>原来的姿势 · 整只猫轻轻起伏 · 眨眼自然衔接</p><canvas width="512" height="512"></canvas><div><button id="play">载入中</button><button id="restart">重新播放</button><select id="speed"><option value="1">正常速度</option><option value=".5">半速检查</option></select><button id="bg">切换背景</button></div><input id="scrub" type="range" min="0" max="9599" value="0"><div class="status" id="status"></div></main><script>const clip=${JSON.stringify(clip)},cache=new Map(),canvas=document.querySelector('canvas'),ctx=canvas.getContext('2d'),play=document.getElementById('play'),scrub=document.getElementById('scrub'),status=document.getElementById('status');let time=0,last=null,paused=true,ready=false;function frame(t){let end=0;for(const f of clip.frames){end+=f.durationMs;if(t<end)return f.file}return clip.frames.at(-1).file}function draw(){ctx.clearRect(0,0,512,512);const img=cache.get(frame(time));if(img)ctx.drawImage(img,0,0);scrub.value=time;status.textContent=(time/1000).toFixed(2)+' / 9.60 秒';play.textContent=paused?'播放工作循环':'暂停预览'}play.onclick=()=>{if(ready){paused=!paused;last=null;draw()}};document.getElementById('restart').onclick=()=>{if(ready){time=0;paused=false;last=null;draw()}};document.getElementById('bg').onclick=()=>document.body.classList.toggle('dark');scrub.oninput=()=>{time=+scrub.value;paused=true;last=null;draw()};function tick(now){const delta=last===null?0:Math.min(100,now-last);last=now;if(ready&&!paused&&!document.hidden){time=(time+delta*+document.getElementById('speed').value)%clip.durationMs;draw()}requestAnimationFrame(tick)}document.addEventListener('visibilitychange',()=>last=null);(async()=>{try{let n=0;const files=[...new Set(clip.frames.map(f=>f.file))];await Promise.all(Array.from({length:4},async()=>{while(n<files.length){const f=files[n++],img=new Image();img.src=f;await img.decode();cache.set(f,img)}}));ready=true;paused=false;draw();requestAnimationFrame(tick)}catch(e){status.textContent='载入失败：'+e.message}})();</script></html>`;
  await fs.writeFile(path.join(output,'index.html'),html);
}

if(require.main===module) main().catch(error=>{console.error(error);process.exitCode=1});
module.exports={translateY,compositeProps,dyAt,checkRigidTranslation};
