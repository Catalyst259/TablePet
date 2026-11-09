// Compose the selected art with reversible gaze and 400ms prop transitions.
const fs=require('node:fs/promises'),path=require('node:path'),crypto=require('node:crypto'),sharp=require('sharp');
const {execFile}=require('node:child_process'),{promisify}=require('node:util');
const exec=promisify(execFile),motion=require('./work-transition-motion.cjs');
const {translateY,compositeProps}=require('./build-work-preview-v2.cjs');
const artwork=require('./work-artwork.cjs').loadWorkArtwork();
const root=path.resolve(__dirname,'..'),asset=path.join(root,'assets/strawberry-cat');
const output=artwork.transitionRoot,work=path.join(artwork.transitionArtifacts,'demo');
const W=512,H=512,fps=60,hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const relative=f=>path.relative(root,f).replaceAll('\\','/');
async function main(){
  await fs.mkdir(path.join(output,'frames'),{recursive:true});
  await fs.mkdir(path.join(work,'video-frames'),{recursive:true});
  const inventory=[],rawCache=new Map();
  async function read(file){
    if(rawCache.has(file))return rawCache.get(file);
    const bytes=await fs.readFile(file),decoded=await sharp(bytes).ensureAlpha().raw().toBuffer({resolveWithObject:true});
    if(decoded.info.width!==W||decoded.info.height!==H)throw Error('Canvas mismatch: '+file);
    inventory.push({file:relative(file),sha256:hash(bytes)});rawCache.set(file,decoded.data);return decoded.data;
  }
  const gazeManifest=JSON.parse(await fs.readFile(path.join(output,'gaze/manifest.json'),'utf8'));
  const gazeFiles=gazeManifest.frames.map(f=>typeof f==='string'?f:f.file);
  if(gazeFiles.length!==33)throw Error('Expected 33 gaze phases');
  const gaze=await Promise.all(gazeFiles.map(f=>read(path.join(output,'gaze',f))));
  const blinkFiles=Array.from({length:65},(_,i)=>'../'+artwork.previewName+'/blink/blink-'+String(i).padStart(2,'0')+'.png');
  const blink=await Promise.all(blinkFiles.map(f=>read(path.resolve(output,f))));
  const props=await read(path.join(artwork.previewRoot,'props.png'));
  const idle=await read(path.join(asset,'frames/fall_asleep-00.png'));
  const focused=await read(path.join(artwork.previewRoot,'keys/open.png'));
  const runtimeManifest=await fs.readFile(path.join(asset,'manifest.json'));
  if(!gaze[0].equals(idle)||!gaze[32].equals(focused))throw Error('Gaze endpoints drifted');
  function render(state){
    const source=state.blinkIndex===null?gaze[state.gazeIndex]:blink[state.blinkIndex];
    const cat=state.dy===0?source:translateY(source,W,H,state.dy);
    if(state.propsOpacity<=0)return Buffer.from(cat);
    if(state.propsOpacity>=1)return compositeProps(cat,props);
    const faded=Buffer.from(props);
    for(let p=3;p<faded.length;p+=4)faded[p]=Math.round(faded[p]*state.propsOpacity);
    return compositeProps(cat,faded);
  }
  const framePool=new Map();
  async function storeFrame(rgba){
    const digest=hash(rgba);
    if(framePool.has(digest))return framePool.get(digest).file;
    const file='frames/frame-'+digest.slice(0,20)+'.png';
    const png=await sharp(rgba,{raw:{width:W,height:H,channels:4}}).png({compressionLevel:9,adaptiveFiltering:true}).toBuffer();
    await fs.writeFile(path.join(output,file),png);
    framePool.set(digest,{file,rgbaSha256:digest,pngSha256:hash(png),bytes:png.length});return file;
  }
  const durationMs=motion.demo.durationMs,slotCount=Math.round(durationMs*fps/1000),frames=[],timeline=[];
  for(let i=0;i<slotCount;i++){
    const time=i*1000/fps,state=motion.demoAt(time),rgba=render(state),file=await storeFrame(rgba);
    const durationMs=Math.round((i+1)*1000/fps)-Math.round(time);
    frames.push({file,durationMs});timeline.push({timeMs:time,...state,file,durationMs});
    await fs.copyFile(path.join(output,file),path.join(work,'video-frames',String(i).padStart(5,'0')+'.png'));
    if(i%120===0)console.log('Composed transition demo '+i+'/'+slotCount);
  }
  const enterController=motion.createController();enterController.begin(0);
  const exitController=motion.createController();exitController.begin(-400);
  exitController.finish(motion.demo.exitStartWorkMs);
  async function clip(sample){
    const frames=[];
    for(let i=0;i<24;i++){
      const t=i*1000/fps,file=await storeFrame(render(sample(t)));
      frames.push({file,durationMs:Math.round((i+1)*1000/fps)-Math.round(t)});
    }
    return {loop:false,durationMs:400,frames};
  }
  const enter=await clip(t=>enterController.sample(t));
  const exit=await clip(t=>exitController.sample(motion.demo.exitStartWorkMs+t));
  const focusedWithProps=compositeProps(focused,props);
  const checks={gazeIdleExact:gaze[0].equals(idle),gazeFocusedExact:gaze[32].equals(focused),
    entryStartExact:render(motion.demoAt(1000)).equals(idle),
    entryEndExact:render(motion.demoAt(1400)).equals(focusedWithProps),
    demoEndsInOriginalIdle:render(motion.demoAt(8875)).equals(idle),
    demoLoopSeamExact:render(motion.demoAt(0)).equals(render(motion.demoAt(durationMs))),
    exitRequestAtBlinkWaits:motion.demoAt(8200).waitingForBlink===true&&motion.demoAt(8474.99).state==='working',
    exitStartsAfterBlink:motion.demoAt(8475).state==='exiting',
    exitStartsAtCurrentBodyPosition:Math.abs(motion.demoAt(8475).dy-motion.breathing(7075).y)<1e-12,
    exitSettlesAtZero:motion.demoAt(8875).dy===0,
    durationsCorrect:frames.reduce((n,f)=>n+f.durationMs,0)===durationMs&&enter.frames.reduce((n,f)=>n+f.durationMs,0)===400&&exit.frames.reduce((n,f)=>n+f.durationMs,0)===400};
  const selected=JSON.parse(await fs.readFile(path.join(artwork.previewRoot,'manifest.json'),'utf8'));
  checks.approvedLoopSamplesExact=true;
  for(const index of [0,75,84,96,144,288,402,414,432,575]){
    const t=index*1000/fps,b=motion.blinkAt(t),state={gazeIndex:32,blinkIndex:b?.index??null,propsOpacity:1,dy:motion.breathing(t).y};
    const approved=await read(path.join(artwork.previewRoot,selected.clip.frames[index].file));
    if(!render(state).equals(approved))checks.approvedLoopSamplesExact=false;
  }
  if(Object.values(checks).some(v=>v!==true))throw Error('Transition checks failed: '+JSON.stringify(checks));
  const contactTimes=[1000,1100,1200,1300,1400,8475,8575,8675,8775,8875];
  const tiles=[];
  for(const [i,t]of contactTimes.entries()){
    const png=await sharp(render(motion.demoAt(t)),{raw:{width:W,height:H,channels:4}}).resize(256,256).flatten({background:i<5?'#f3efe8':'#252936'}).png().toBuffer();
    tiles.push({input:png,left:(i%5)*256,top:Math.floor(i/5)*256});
  }
  await sharp({create:{width:1280,height:512,channels:4,background:'#f3efe8'}}).composite(tiles).png().toFile(path.join(output,'transition-contact.png'));
  await fs.writeFile(path.join(output,'manifest.json'),JSON.stringify({version:artwork.transitionVersion,status:'candidate-not-installed',canvas:{width:W,height:H},sampleFps:fps,
    clips:{enter,exit:{...exit,exampleWorkPhaseMs:7075,arbitraryPhase:'Use work-transition-motion.cjs for current breath phase'},demo:{loop:true,durationMs,frames}},
    approvedWorkLoop:'../'+artwork.previewName+'/manifest.json',transitionMs:400,gaze:'gaze/manifest.json',exitWait:'Finish current blink only; no wait for the work loop',controller:relative(path.join(root,'tools/work-transition-motion.cjs'))},null,2)+'\n');
  await fs.writeFile(path.join(output,'timeline.json'),JSON.stringify(timeline,null,2)+'\n');
  await writePreview({gazeFiles:gazeFiles.map(f=>'gaze/'+f),blinkFiles,props:'../'+artwork.previewName+'/props.png'});
  await exec('ffmpeg',['-hide_banner','-loglevel','error','-y','-framerate','60','-i',path.join(work,'video-frames/%05d.png'),'-filter_complex','color=c=0xf3efe8:s=512x512:r=60[bg];[bg][0:v]overlay=shortest=1:format=auto,format=yuv420p[out]','-map','[out]','-frames:v',String(slotCount),'-c:v','libx264','-crf','18','-movflags','+faststart',path.join(output,'work-transition.mp4')],{windowsHide:true,maxBuffer:4e6});
  await exec('ffmpeg',['-hide_banner','-loglevel','error','-y','-i',path.join(output,'work-transition.mp4'),'-filter_complex','fps=30,scale=384:384:flags=lanczos,split[a][b];[a]palettegen=max_colors=256[p];[b][p]paletteuse=dither=sierra2_4a','-loop','0',path.join(output,'work-transition.gif')],{windowsHide:true,maxBuffer:4e6});
  const probe=JSON.parse((await exec('ffprobe',['-v','error','-select_streams','v:0','-show_entries','stream=width,height,r_frame_rate,nb_frames,duration','-of','json',path.join(output,'work-transition.mp4')],{windowsHide:true})).stdout).streams[0];
  checks.encodedVideoValid=probe.width===512&&probe.height===512&&probe.r_frame_rate==='60/1'&&Number(probe.nb_frames)===slotCount&&Math.abs(Number(probe.duration)-durationMs/1000)<.001;
  checks.sourceUnchanged=(await fs.readFile(path.join(asset,'manifest.json'))).equals(runtimeManifest)&&(await Promise.all(inventory.map(async x=>hash(await fs.readFile(path.join(root,x.file)))===x.sha256))).every(Boolean);
  const qa={date:'2026-11-09',candidateOnly:true,checks,passed:Object.values(checks).every(Boolean),durationMs,slotCount,uniqueFrames:framePool.size,video:probe,sourceInventory:inventory,frames:[...framePool.values()]};
  await fs.writeFile(path.join(output,'verification.json'),JSON.stringify(qa,null,2)+'\n');
  console.log(JSON.stringify({passed:qa.passed,checks,durationMs,slotCount,uniqueFrames:framePool.size,output},null,2));
  if(!qa.passed)process.exitCode=1;
}
async function writePreview(assets){
  const controller=await fs.readFile(path.join(root,'tools/work-transition-motion.cjs'),'utf8');
  const html=`<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>草莓猫 · 工作进出过渡</title>
<style>*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:#f3efe8;color:#493d35;font:15px system-ui}.card{text-align:center;padding:24px}h1{font-size:24px;margin:0 0 8px}.hint{color:#7d6d61;margin:0 0 8px}canvas{display:block;width:min(512px,85vw);height:auto;margin:auto}button,select{font:inherit;padding:9px 14px;margin:5px;border:1px solid #cdbbaa;border-radius:10px;background:#fffaf4;color:#493d35}button:disabled{opacity:.5}.primary{background:#674f42;color:white;border-color:#674f42}input{width:min(440px,80vw);margin:14px auto 8px;display:block}.status{min-height:24px;font-variant-numeric:tabular-nums}.dark{background:#252936;color:#eee}.dark .hint{color:#c3b6ac}</style>
<main class="card"><h1>待机 → 工作 → 待机</h1><p class="hint">电脑轻轻出现，目光落向屏幕</p><canvas width="512" height="512"></canvas><div class="status" id="status">载入中…</div><div><button class="primary" id="toggle" disabled>开始工作</button><button id="demo" disabled>完整演示</button><button id="pause" disabled>暂停</button></div><div><select id="speed"><option value="1">正常速度</option><option value=".5">半速查看</option></select><button id="bg">切换背景</button></div><input id="scrub" type="range" min="0" max="9900" value="0" disabled></main>
<script>${controller}</script><script>
const assets=${JSON.stringify(assets)},M=WorkTransition,cache=new Map(),canvas=document.querySelector('canvas'),ctx=canvas.getContext('2d');
const toggle=document.getElementById('toggle'),demoButton=document.getElementById('demo'),pauseButton=document.getElementById('pause'),scrub=document.getElementById('scrub'),status=document.getElementById('status');
let ready=false,mode='demo',time=0,last=null,paused=false,controller=M.createController(),lastState=null;
function sample(){return mode==='demo'?M.demoAt(time):controller.sample(time)}
function draw(){if(!ready)return;const s=sample();lastState=s;ctx.clearRect(0,0,512,512);ctx.globalAlpha=1;ctx.drawImage(cache.get(s.blinkIndex===null?assets.gazeFiles[s.gazeIndex]:assets.blinkFiles[s.blinkIndex]),0,s.dy);if(s.propsOpacity>0){ctx.globalAlpha=s.propsOpacity;ctx.drawImage(cache.get(assets.props),0,0);ctx.globalAlpha=1}status.textContent=s.waitingForBlink?'等这次眨眼结束，即刻返回待机':({idle:'待机',entering:'进入工作',working:'陪你工作中',exiting:'返回待机'})[s.state];toggle.textContent=s.state==='idle'?'开始工作':s.waitingForBlink?'睁眼后返回':'返回待机';toggle.disabled=!['idle','working'].includes(s.state)||s.waitingForBlink;pauseButton.textContent=paused?'继续':'暂停';scrub.disabled=mode!=='demo';if(mode==='demo')scrub.value=time;}
toggle.onclick=()=>{if(!ready)return;const s=sample();if(mode==='demo'){controller=M.createController();if(s.state==='working')controller.begin(time-s.workTime-M.transitionMs);mode='live'}if(s.state==='idle')controller.begin(time);else if(s.state==='working')controller.finish(time);paused=false;last=null;draw()};
demoButton.onclick=()=>{mode='demo';time=0;paused=false;last=null;draw()};pauseButton.onclick=()=>{paused=!paused;last=null;draw()};scrub.oninput=()=>{mode='demo';time=+scrub.value;paused=true;last=null;draw()};document.getElementById('bg').onclick=()=>document.body.classList.toggle('dark');document.addEventListener('visibilitychange',()=>last=null);
function tick(now){const delta=last===null?0:Math.min(now-last,100);last=now;if(ready&&!paused&&!document.hidden){time+=delta*+document.getElementById('speed').value;if(mode==='demo'&&time>=M.demo.durationMs){time=M.demo.durationMs;paused=true}draw()}requestAnimationFrame(tick)}
(async()=>{try{const files=[...new Set([...assets.gazeFiles,...assets.blinkFiles,assets.props])];let n=0;await Promise.all(Array.from({length:4},async()=>{while(n<files.length){const f=files[n++],img=new Image();img.src=f;await img.decode();cache.set(f,img)}}));ready=true;demoButton.disabled=false;pauseButton.disabled=false;draw();requestAnimationFrame(tick)}catch(e){status.textContent='预览载入失败：'+e.message}})();
</script></html>`;
  await fs.writeFile(path.join(output,'index.html'),html);
}
if(require.main===module)main().catch(e=>{console.error(e);process.exitCode=1});
