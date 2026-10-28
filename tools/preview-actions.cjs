// Build a local, independent acceptance page from the exact delivered PNG timeline.
// node tools/preview-actions.cjs [version]
const fs = require('node:fs/promises');
const path = require('node:path');
const version = process.argv[2] || '0.3.1';
if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error('Supply a numeric release version.');
const assetRoot = path.resolve(__dirname, '../assets/strawberry-cat');

async function main() {
  const manifest = JSON.parse(await fs.readFile(path.join(assetRoot, 'manifest.json'), 'utf8'));
  const names = ['idle', 'blink', 'yawn', 'wink', 'rise', 'hold', 'settle'];
  const clips = Object.fromEntries(names.filter(name => manifest.clips[name]).map(name => [name, manifest.clips[name]]));
  if (names.some(name => !clips[name])) throw new Error('Missing release action.');
  clips.rise = { loop: false, frames: [...clips.rise.frames, ...clips.hold.frames, ...clips.settle.frames] };
  delete clips.hold; delete clips.settle;
  for (const clip of Object.values(clips)) for (const frame of clip.frames) {
    if (!frame.file || frame.file.includes('..') || path.isAbsolute(frame.file) || /[\\<>]/.test(frame.file)) throw new Error('Unsafe preview frame path.');
  }
  const idleActions = (manifest.idleActions || []).filter(action => ['blink', 'yawn'].includes(action.clip));
  if (idleActions.length !== 2 || new Set(idleActions.map(action => action.clip)).size !== 2) throw new Error('Expected blink and yawn idle pool.');
  const data = JSON.stringify({ version, clips, idleActions, clickActions: ['rise', 'wink'] }).replace(/</g, '\\u003c');
  const html = `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>草莓猫 ${version} · 动作预览</title>
<style>
*{box-sizing:border-box}body{margin:0;background:#f7f2ee;color:#44393d;font:15px/1.6 "Segoe UI","Microsoft YaHei",sans-serif}main{max-width:1060px;margin:auto;padding:38px 28px 50px}header{display:flex;align-items:flex-start;justify-content:space-between;gap:24px}h1{font-size:27px;line-height:1.25;margin:0 0 10px;font-weight:650}p{margin:0;color:#76666c}.badge{border:1px solid #d8c9ce;padding:5px 12px;border-radius:30px;font-size:12px;white-space:nowrap}.layout{display:grid;grid-template-columns:minmax(330px,1fr) 320px;gap:24px;margin-top:28px}.stage{position:relative;min-height:480px;background:#fffaf5;border:1px solid #e6dad5;border-radius:24px;display:flex;align-items:center;justify-content:center;overflow:hidden;transition:background .15s}.stage.dark{background:#26313f;border-color:#26313f}.stage.checker{background:repeating-conic-gradient(#eee4df 0% 25%,#fffaf5 0% 50%) 50%/24px 24px}canvas{display:block;width:240px;height:240px;max-width:100%;object-fit:contain}.size-note{position:absolute;bottom:22px;left:24px;font-size:12px;color:#9a868d}.dark .size-note{color:#abb7c7}.panel{background:#fffdfb;border:1px solid #e6dad5;border-radius:24px;padding:22px}.label{font-size:12px;letter-spacing:.05em;color:#95818a;margin:0 0 9px}.actions{display:grid;grid-template-columns:1fr 1fr;gap:9px}button,select{font:inherit;line-height:1.4;border:1px solid #ddcfd3;background:#fffaf7;color:#53444b;border-radius:11px;padding:10px 12px;cursor:pointer}button:hover{border-color:#c594a7;background:#fff4f5}button:focus-visible,select:focus-visible,input:focus-visible{outline:3px solid #e6b0c6;outline-offset:2px}button.active,button[aria-pressed="true"]{background:#f7dce6;border-color:#d6a6b7;color:#633d4b}button:disabled{opacity:.4;cursor:not-allowed}.wide{width:100%;margin-top:10px}.row{display:flex;gap:8px;align-items:center}.group{margin-top:23px}select{width:100%;background:#fff}.segmented{display:flex;gap:7px}.segmented button{flex:1;font-size:13px;padding:8px 5px}.play-row{display:grid;grid-template-columns:1fr 1fr;gap:9px;margin-top:16px}.transport{margin-top:20px}input[type=range]{width:100%;accent-color:#c5809b}.timeline{display:flex;justify-content:space-between;gap:10px;font-size:12px;color:#8d7880}.status{min-height:26px;margin-top:18px;font-size:14px;color:#715961}.notes{margin-top:22px;font-size:13px;color:#8c7980;line-height:1.8}.error{color:#9b344a}.missing{margin-top:10px;color:#9b344a;font-size:13px}@media(max-width:760px){main{padding:26px 18px}.layout{grid-template-columns:1fr}.stage{min-height:360px}.panel{padding:20px}header{display:block}.badge{display:inline-block;margin-top:12px}}
</style></head><body><main>
<header><div><h1>草莓猫的动作预览</h1><p>待机时眨眼、打哈欠；点击时起身或 Wink。</p></div><span class="badge">${version} · 冻结版</span></header>
<div class="layout"><section class="stage" id="stage" aria-label="动画预览"><canvas id="pet" width="512" height="512" aria-label="草莓猫动画"></canvas><div class="size-note" id="sizeNote">240 px · 默认桌宠大小</div></section>
<aside class="panel"><div class="label">待机动作</div><div class="actions" id="idleActions"></div><button id="random" class="wide">随机待机</button><div class="group"><div class="label">点击动作</div><div class="actions" id="clickActions"></div><button id="randomClick" class="wide">模拟点击 · 随机起身 / Wink</button></div>
<div class="group"><div class="label">播放速度</div><div class="segmented" id="speeds"><button data-speed="0.25">0.25×</button><button data-speed="0.5">0.5×</button><button data-speed="1" class="active" aria-pressed="true">正常</button></div></div>
<div class="group"><div class="label">背景</div><div class="segmented" id="backgrounds"><button data-bg="light" class="active" aria-pressed="true">浅色</button><button data-bg="dark">深色</button><button data-bg="checker">透明格</button></div></div>
<div class="group"><div class="label">随机播放间隔</div><select id="pace" aria-label="随机播放间隔"><option value="quick">快速浏览 · 1.5–2.5 秒</option><option value="normal">桌宠默认 · 3–8 秒</option></select></div>
<div class="play-row"><button id="pause">暂停</button><button id="replay">重播当前动作</button></div><div class="transport"><input id="scrub" type="range" min="0" max="1000" value="0" aria-label="逐帧查看进度"><div class="timeline"><span id="clipLabel">趴坐待机</span><span id="timeLabel">0.00 s</span></div></div><div class="status" id="status" role="status">正在准备预览…</div></aside></div>
<p class="notes">点选动作可检查完整表情；拖动进度条或慢放可查看衔接。随机待机只包含眨眼与打哈欠，并避免连续重复。模拟点击等概率选择起身或 Wink；起身包含停留和蹲下。页面播放本版本实际 PNG 帧，显示刷新率由当前屏幕决定。</p>
</main><script>
'use strict';
const data=${data};
const labels={idle:'趴坐待机',blink:'眨眼',yawn:'打哈欠',rise:'起身',wink:'Wink · 单眼眨'};
const canvas=document.getElementById('pet');
const renderSize=Math.max(1,Math.min(512,Math.round(canvas.getBoundingClientRect().width*(window.devicePixelRatio||1))));
canvas.width=renderSize;canvas.height=renderSize;
const context=canvas.getContext('2d',{alpha:true});
const status=document.getElementById('status'),scrub=document.getElementById('scrub'),pauseButton=document.getElementById('pause');
const restFile=data.clips.idle.frames[0].file;
const cache=new Map();
let idleImage=null,current=null,elapsed=0,speed=1,paused=false,loading=false,mode='single',lastAction=null,chosenAction='blink',nextAt=Infinity,previousTime=null,generation=0;
const duration=clip=>clip.frames.reduce((sum,frame)=>sum+frame.durationMs,0);
const available=['blink','yawn'].filter(name=>data.clips[name]);
const pool=available.map(clip=>({clip,weight:data.idleActions.find(a=>a.clip===clip)?.weight|| (clip==='blink'?4:1)}));
function fileAt(clip,time){let end=0;for(const frame of clip.frames){end+=frame.durationMs;if(time<end)return frame.file;}return clip.frames.at(-1).file;}
function draw(image){context.clearRect(0,0,canvas.width,canvas.height);if(image)context.drawImage(image,0,0,canvas.width,canvas.height);}
function setStatus(text,error=false){status.textContent=text;status.classList.toggle('error',error);}
function updateTransport(){const max=current?duration(current.clip):0;document.getElementById('clipLabel').textContent=current?labels[current.name]:'趴坐待机';document.getElementById('timeLabel').textContent=current?(Math.min(elapsed,max)/1000).toFixed(2)+' / '+(max/1000).toFixed(2)+' s':'0.00 s';scrub.value=max?Math.min(1000,Math.round(elapsed/max*1000)):0;}
function mark(){document.querySelectorAll('[data-action]').forEach(button=>{const active=current?.name===button.dataset.action;button.classList.toggle('active',active);button.setAttribute('aria-pressed',active);});document.getElementById('random').classList.toggle('active',mode==='random');document.getElementById('random').setAttribute('aria-pressed',mode==='random');pauseButton.textContent=paused?'继续':'暂停';}
function clearCache(){for(const [file,image] of cache){if(file!==restFile){image.close?.();cache.delete(file);}}}
async function loadImage(file){if(file===restFile&&idleImage)return idleImage;const image=new Image();image.decoding='async';image.src='../'+file;await image.decode();if(typeof createImageBitmap==='function'){const bitmap=await createImageBitmap(image,{resizeWidth:renderSize,resizeHeight:renderSize,resizeQuality:'high'});image.src='';return bitmap;}return image;}
function rest(){current=null;elapsed=0;draw(idleImage);updateTransport();mark();}
function schedule(){const quick=document.getElementById('pace').value==='quick';nextAt=performance.now()+(quick?1500+Math.random()*1000:3000+Math.random()*5000);}
async function play(name){if(!data.clips[name])return;const id=++generation;loading=true;nextAt=Infinity;current=null;draw(idleImage);updateTransport();mark();setStatus('正在准备'+labels[name]+'…');clearCache();const clip=data.clips[name],files=[...new Set(clip.frames.map(frame=>frame.file))];let cursor=0;const prepared=new Map([[restFile,idleImage]]);
try{await Promise.all(Array.from({length:Math.min(4,files.length)},async()=>{while(cursor<files.length&&id===generation){const file=files[cursor++];if(file===restFile)continue;const image=await loadImage(file);if(id!==generation){image.close?.();return;}prepared.set(file,image);}}));if(id!==generation){for(const [file,image]of prepared)if(file!==restFile)image.close?.();return;}for(const [file,image]of prepared)cache.set(file,image);current={name,clip};chosenAction=name;lastAction=name;elapsed=0;loading=false;previousTime=null;draw(cache.get(clip.frames[0].file));updateTransport();mark();setStatus(mode==='random'?'随机播放 · '+labels[name]:labels[name]+' · 可慢放或拖动进度查看');}
catch(error){for(const[file,image]of prepared)if(file!==restFile)image.close?.();if(id!==generation)return;generation++;loading=false;mode='single';rest();setStatus('动作图片载入失败，请确认预览页仍位于素材目录中。'+error.message,true);}}
function choose(){const choices=pool.filter(action=>pool.length===1||action.clip!==lastAction);const total=choices.reduce((sum,action)=>sum+action.weight,0);let ticket=Math.random()*total;for(const action of choices){ticket-=action.weight;if(ticket<0)return action.clip;}return choices.at(-1).clip;}
for(const [group,names]of Object.entries({idleActions:available,clickActions:data.clickActions})){for(const name of names){const button=document.createElement('button');button.type='button';button.dataset.action=name;button.textContent=labels[name];button.addEventListener('click',()=>{mode='single';paused=false;play(name);});document.getElementById(group).append(button);}}
function chooseClick(){return data.clickActions[Math.floor(Math.random()*data.clickActions.length)];}
document.getElementById('randomClick').addEventListener('click',()=>{mode='single';paused=false;play(chooseClick());});
document.getElementById('random').addEventListener('click',()=>{mode='random';paused=false;play(choose());});
document.getElementById('replay').addEventListener('click',()=>{mode='single';paused=false;play(chosenAction);});
pauseButton.addEventListener('click',()=>{paused=!paused;previousTime=null;mark();if(!paused&&!current&&mode==='random')schedule();setStatus(paused?'已暂停，可拖动进度查看':'继续播放');});
document.getElementById('speeds').addEventListener('click',event=>{const button=event.target.closest('[data-speed]');if(!button)return;speed=Number(button.dataset.speed);previousTime=null;document.querySelectorAll('[data-speed]').forEach(item=>{const active=item===button;item.classList.toggle('active',active);item.setAttribute('aria-pressed',active);});});
document.getElementById('backgrounds').addEventListener('click',event=>{const button=event.target.closest('[data-bg]');if(!button)return;document.getElementById('stage').className='stage '+(button.dataset.bg==='light'?'':button.dataset.bg);document.querySelectorAll('[data-bg]').forEach(item=>{const active=item===button;item.classList.toggle('active',active);item.setAttribute('aria-pressed',active);});});
document.getElementById('pace').addEventListener('change',()=>{if(mode==='random'&&!current)schedule();});
scrub.addEventListener('input',()=>{if(!current)return;paused=true;elapsed=Number(scrub.value)/1000*duration(current.clip);draw(cache.get(fileAt(current.clip,elapsed)));updateTransport();mark();setStatus('逐帧查看 · '+labels[current.name]);});
document.addEventListener('visibilitychange',()=>{previousTime=null;if(!document.hidden&&mode==='random'&&!current)schedule();});
function tick(now){const delta=previousTime===null?0:Math.min(now-previousTime,100);previousTime=now;if(!document.hidden&&!paused&&!loading){if(current){elapsed+=delta*speed;draw(cache.get(fileAt(current.clip,elapsed)));updateTransport();if(elapsed>=duration(current.clip)){if(mode==='random'){rest();schedule();setStatus('趴坐待机 · 稍后换一个小表情');}else{elapsed=duration(current.clip);paused=true;draw(idleImage);updateTransport();mark();setStatus(labels[current.name]+'播放完毕 · 已自然回到趴坐');}}}else if(mode==='random'&&now>=nextAt)play(choose());}requestAnimationFrame(tick);}
(async()=>{try{idleImage=await loadImage(restFile);cache.set(restFile,idleImage);draw(idleImage);updateTransport();mark();setStatus('选择一个动作，或开启随机待机');requestAnimationFrame(tick);}catch(error){setStatus('待机图片载入失败：'+error.message,true);}})();
window.addEventListener('pagehide',()=>{generation++;for(const image of cache.values())image.close?.();});
</script></body></html>`;
  const outputs = ['actions-preview-' + version + '.html', 'actions-preview.html', 'idle-actions-preview.html'].map(file => path.join(assetRoot, 'previews', file));
  await fs.mkdir(path.dirname(outputs[0]), { recursive: true });
  for (const output of outputs) await fs.writeFile(output, html);
  console.log(JSON.stringify({ outputs, idleActions, clickActions: ['rise', 'wink'] }, null, 2));
}
main().catch(error => { console.error(error); process.exitCode = 1; });
