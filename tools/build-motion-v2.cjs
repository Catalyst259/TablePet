// Generated keys -> upstream RIFE -> timed transparent PNG clips. No runtime AI needed.
const fs=require('node:fs/promises');
const path=require('node:path');
const crypto=require('node:crypto');
const sharp=require('sharp');
const {interpolate}=require('./motion-interpolate.cjs');
const {smoothBlink}=require('./touchup-blink.cjs');
const {smoothSleep}=require('./touchup-sleep.cjs');
const repo=path.resolve(__dirname,'..'), root=path.join(repo,'assets/strawberry-cat');
const work=path.join(repo,'artifacts/motion-v2'), fps=240;
const key=(name,i)=>`keyframes-v2/${name}/${String(i).padStart(2,'0')}.png`;
const idle='frames/fall_asleep-00.png', blink=i=>`frames/blink-${String(i).padStart(2,'0')}.png`;
const jobs={
  rise:{factor:128,frames:[idle,blink(1),blink(2),blink(3),key('rise',4),key('rise',5),key('rise',6),'keyframes-v2/half-rise.png'],times:[0,50,100,140,250,390,540,800]},
  settle:{factor:128,frames:['keyframes-v2/half-rise.png',key('settle',1),key('settle',2),blink(3),blink(4),idle],times:[0,160,320,620,800,1000]},
  fall_asleep:{factor:128,frames:[idle,key('sleep',1),key('sleep',2),key('sleep',3),key('sleep',4),key('sleep',5),key('sleep',8)],times:[0,250,550,850,1150,1450,1750]},
  breath:{factor:512,frames:[key('sleep',8),key('sleep',9)],times:[0,1800]}
};
function hash(data){return crypto.createHash('sha256').update(data).digest('hex');}
async function prepare(name,job){
  const inputs=await Promise.all(job.frames.map(f=>fs.readFile(path.join(root,f))));
  const fingerprint=hash(Buffer.concat([Buffer.from(JSON.stringify({factor:job.factor,engine:'RIFE4.6-TTA'})),...inputs]));
  const output=path.join(work,name+'-'+fingerprint.slice(0,12));
  let report;
  try{report=JSON.parse(await fs.readFile(path.join(output,'interpolation-report.json'),'utf8'));}
  catch{report=await interpolate({frames:job.frames.map(f=>path.join(root,f)),outputDirectory:output,factor:job.factor,gpu:0,temporalTta:true});}
  job.output=output; job.fingerprint=fingerprint;
  console.log(`${name}: ${report.outputFrames} dense RIFE phases ready`);
}
// Monotone cubic Hermite timing preserves motion through intermediate poses,
// while the first and last tangents ease into/out of the stationary pose.
function phaseAt(time,times){
  const n=times.length;
  if(time<=0)return 0;if(time>=times[n-1])return n-1;
  let i=0;while(i<n-2&&time>times[i+1])i++;
  const dt=times[i+1]-times[i],u=(time-times[i])/dt;
  const slope=k=>k===0||k===n-1?0:2/(times[k+1]-times[k-1]);
  const u2=u*u,u3=u2*u;
  return Math.max(i,Math.min(i+1,(2*u3-3*u2+1)*i+(u3-2*u2+u)*dt*slope(i)+(-2*u3+3*u2)*(i+1)+(u3-u2)*dt*slope(i+1)));
}
async function pack(){
  const original=JSON.parse(await fs.readFile(path.join(repo,'reference/motion-v1/manifest.json'),'utf8'));
  const copied=new Map(),canonical=new Map();
  await fs.mkdir(path.join(root,'frames/v2'),{recursive:true});
  async function materialize(name,index){
    const job=jobs[name];index=Math.max(0,Math.min((job.frames.length-1)*job.factor,Math.round(index)));
    const id=name+'-'+String(index).padStart(5,'0');if(copied.has(id))return copied.get(id);
    let target=`frames/v2/${id}.png`;
    if(index%job.factor===0){
      const source=job.frames[index/job.factor];
      if(source.startsWith('frames/')){copied.set(id,source);return source;}
      if(canonical.has(source)){target=canonical.get(source);copied.set(id,target);return target;}
      canonical.set(source,target);
    }
    await fs.copyFile(path.join(job.output,String(index).padStart(5,'0')+'.png'),path.join(root,target));copied.set(id,target);return target;
  }
  async function timeline(name,duration,map,loop=false){
    const count=Math.round(duration/1000*fps),frames=[];
    for(let i=0;i<count;i++)frames.push({file:await materialize(name,map(i/(count-1))*jobs[name].factor),durationMs:Math.round((i+1)*duration/count)-Math.round(i*duration/count)});
    return {loop,frames};
  }
  const rise=await timeline('rise',800,t=>phaseAt(t*800,jobs.rise.times));
  const settle=await timeline('settle',1000,t=>phaseAt(t*1000,jobs.settle.times));
  const falling=await timeline('fall_asleep',1750,t=>phaseAt(t*1750,jobs.fall_asleep.times));
  const sleep=await timeline('breath',3600,t=>(1-Math.cos(t*2*Math.PI))/2,true);
  const wake=await timeline('fall_asleep',1050,t=>phaseAt((1-t)*1750,jobs.fall_asleep.times));
  const manifest={...original,version:2,motion:{revision:'0.2.0',sampleFps:fps,display:'native WPF composition refresh',interpolation:'RIFE 4.6 temporal TTA; RGBA dual matte',blinkPreserved:true},clips:{
    idle:original.clips.idle,blink:original.clips.blink,fall_asleep:falling,sleep_loop:sleep,wake,rise,
    hold:{loop:true,frames:[{file:rise.frames.at(-1).file,durationMs:1000}]},settle
  }};
  await smoothBlink(manifest);
  await smoothSleep(manifest);
  await fs.writeFile(path.join(root,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');
  await fs.writeFile(path.join(work,'production.json'),JSON.stringify({fps,jobs,clips:Object.fromEntries(Object.entries(manifest.clips).map(([n,c])=>[n,{slots:c.frames.length,durationMs:c.frames.reduce((a,f)=>a+f.durationMs,0)}])),uniquePngs:new Set(Object.values(manifest.clips).flatMap(c=>c.frames.map(f=>f.file))).size},null,2)+'\n');
  await previews(manifest);
  console.log('Packaged 240 Hz motion assets with smooth blink transitions and the refined sleeping breath loop.');
}
async function previews(manifest){
  const preview=path.join(root,'previews');await fs.mkdir(preview,{recursive:true});
  const layers=[],poses=[['待机',manifest.clips.idle.frames[0].file],['睡眠',manifest.clips.sleep_loop.frames[0].file],['轻轻撑起',manifest.clips.hold.frames[0].file]];
  for(let i=0;i<poses.length;i++){
    layers.push({input:await sharp(path.join(root,poses[i][1])).resize(256,256).flatten({background:'#f9f2ee'}).png().toBuffer(),left:i*288+16,top:8});
    layers.push({input:Buffer.from(`<svg width="288" height="40"><text x="144" y="26" text-anchor="middle" font-family="Microsoft YaHei" font-size="20" fill="#4a3942">${poses[i][0]}</text></svg>`),left:i*288,top:270});
  }
  await sharp({create:{width:864,height:318,channels:4,background:'#f9f2ee'}}).composite(layers).png().toFile(path.join(preview,'key-poses-v2.png'));
  const frameList=[];
  function add(clip,duration){
    const total=clip.frames.reduce((s,f)=>s+f.durationMs,0),ms=duration||total;
    let index=0,end=clip.frames[0].durationMs;
    for(let t=0;t<ms;t+=20){const pos=t%total;while(index<clip.frames.length-1&&pos>=end){index++;end+=clip.frames[index].durationMs;}frameList.push(clip.frames[index].file);}
  }
  add(manifest.clips.idle,600);add(manifest.clips.blink);add(manifest.clips.idle,300);add(manifest.clips.rise);add(manifest.clips.hold);add(manifest.clips.settle);add(manifest.clips.idle,500);add(manifest.clips.fall_asleep);add(manifest.clips.sleep_loop);add(manifest.clips.wake);add(manifest.clips.idle,500);
  const cache=new Map(),raw=[];
  for(const file of frameList){if(!cache.has(file))cache.set(file,await sharp(path.join(root,file)).resize(256,256).flatten({background:'#f9f2ee'}).ensureAlpha().raw().toBuffer());raw.push(cache.get(file));}
  await sharp(Buffer.concat(raw),{raw:{width:256,height:256*raw.length,channels:4,pageHeight:256}}).gif({loop:0,delay:20,colours:256,dither:.1}).toFile(path.join(preview,'animation-preview-v2.gif'));
  // Browser preview uses the actual 240 Hz PNG timeline; GIF is limited to 50 fps.
  await fs.writeFile(path.join(preview,'motion-preview.html'),`<!doctype html><meta charset="utf-8"><title>草莓猫 · 动作预览</title><style>body{margin:0;background:#f9f2ee;color:#4a3942;font:16px 'Microsoft YaHei',sans-serif;text-align:center}main{max-width:980px;margin:28px auto}canvas{width:360px;height:360px}button{border:1px solid #cbb6b6;border-radius:20px;padding:10px 18px;background:white;color:inherit;margin:6px;cursor:pointer}.dark{background:#26313f;color:#f9f2ee}small{display:block;margin:12px}#status{min-height:28px}</style><main><h2>草莓猫 · 柔和动作</h2><div id="status">正在加载透明动画帧…</div><canvas id="pet" width="512" height="512"></canvas><div id="buttons"></div><button id="background">切换深浅背景</button><button id="speed">0.5× 慢放</button><small>240fps 素材时间轴 · 浏览器按屏幕刷新呈现。GIF 预览为50fps。</small></main><script>const manifest=${JSON.stringify(manifest)},images=new Map(),canvas=document.querySelector('canvas'),ctx=canvas.getContext('2d');let speed=1,queue=['rise','hold','settle','idle','fall_asleep','sleep_loop','wake','idle'],active='idle',started=0,automatic=true;const labels={idle:'待机',blink:'眨眼',rise:'起身',hold:'保持',settle:'蹲下',fall_asleep:'入睡',sleep_loop:'呼吸',wake:'醒来'};function play(n){active=n;started=performance.now();document.querySelector('#status').textContent=labels[n]}for(const n of Object.keys(labels)){const b=document.createElement('button');b.textContent=labels[n];b.onclick=()=>{automatic=false;play(n)};document.querySelector('#buttons').append(b)}document.querySelector('#background').onclick=()=>document.body.classList.toggle('dark');document.querySelector('#speed').onclick=e=>{speed=speed===1?.5:1;e.target.textContent=speed===1?'0.5× 慢放':'1× 正常';play(active)};const files=[...new Set(Object.values(manifest.clips).flatMap(c=>c.frames.map(f=>f.file)))];Promise.all(files.map(file=>new Promise((resolve,reject)=>{const im=new Image;im.onload=()=>{images.set(file,im);resolve()};im.onerror=reject;im.src='../'+file}))).then(()=>{play(queue.shift());requestAnimationFrame(draw)}).catch(()=>document.querySelector('#status').textContent='素材加载失败，请保留完整素材文件夹');function draw(now){let c=manifest.clips[active],duration=c.frames.reduce((s,f)=>s+f.durationMs,0),elapsed=(now-started)*speed;if(automatic&&elapsed>=duration){queue.push(active);play(queue.shift());c=manifest.clips[active];duration=c.frames.reduce((s,f)=>s+f.durationMs,0);elapsed=0}const t=c.loop?elapsed%duration:Math.min(elapsed,duration-1);let end=0,f=c.frames.at(-1);for(const item of c.frames){end+=item.durationMs;if(t<end){f=item;break}}ctx.clearRect(0,0,512,512);ctx.drawImage(images.get(f.file),0,0);requestAnimationFrame(draw)}</script>`);
}
(async()=>{await fs.mkdir(work,{recursive:true});for(const [name,job]of Object.entries(jobs))await prepare(name,job);await pack();})().catch(e=>{console.error(e);process.exit(1)});
