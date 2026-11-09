'use strict';
const fs = require('node:fs/promises'), path = require('node:path');
const { resampleClip } = require('./resample-motion-60hz.cjs');
const repo = path.resolve(__dirname, '..');
async function main() {
  const source = JSON.parse(await fs.readFile(path.join(repo, 'reference/motion-60hz/source-manifest-0.3.3.json'), 'utf8'));
  const candidate = process.argv.includes('--candidate');
  const repaired = JSON.parse(await fs.readFile(path.join(repo, candidate ? 'reference/yawn-repair/yawn-v3.json' : 'reference/yawn-repair/runtime.json'), 'utf8'));
  const previous = repaired.previousRecord ? JSON.parse(await fs.readFile(path.join(repo, repaired.previousRecord), 'utf8')).clip : resampleClip(source.clips.yawn);
  const clips = [previous, repaired.clip];
  const currentLabel = candidate ? '候选版本 · 尚未选用' : '当前选用 · ' + repaired.revision;
  const html = `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>打哈欠修复对比</title>
<style>*{box-sizing:border-box}body{margin:0;background:#f3eee8;color:#302b2d;font:16px/1.6 system-ui,"Microsoft YaHei"}main{max-width:1080px;margin:24px auto;padding:0 24px}h1{font-size:24px;margin:0 0 4px}p{margin:0 0 20px;color:#756a69}.pair{display:grid;grid-template-columns:1fr 1fr;gap:20px}.card{border-radius:16px;overflow:hidden;background:#fffaf5;border:1px solid #ddd1c8}.card h2{font-size:16px;font-weight:500;margin:0;padding:12px 18px;border-bottom:1px solid #ddd1c8}.stage{height:430px;display:flex;align-items:center;justify-content:center}canvas{width:400px;height:400px;max-width:100%;object-fit:contain}.dark .stage{background:#28394c}.controls{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin-top:18px}button,select{font:inherit;padding:8px 13px;border:1px solid #cdbfb4;border-radius:8px;background:#fffaf5;color:#302b2d;cursor:pointer}input{width:100%;accent-color:#ae6879}output{font-variant-numeric:tabular-nums}#status{margin-top:8px;font-size:14px}</style>
<main><h1>打哈欠 · 左脸修复对比</h1><p>${currentLabel}。两边同步播放，保留原来 4.4 秒、60fps 的节奏。</p>
<section class="pair"><article class="card"><h2>历史版本</h2><div class="stage"><canvas width="512" height="512"></canvas></div></article><article class="card"><h2>${currentLabel}</h2><div class="stage"><canvas width="512" height="512"></canvas></div></article></section>
<div class="controls"><button id="play">暂停</button><button id="restart">重播</button><label>速度 <select id="speed"><option value="1">正常</option><option value="0.25">0.25× 慢放</option></select></label><button id="background">深色背景</button><button id="crop">放大左脸</button><label>大小 <select id="size"><option value="400">400 px</option><option value="240">240 px · 桌宠大小</option></select></label><output id="time"></output></div>
<input id="seek" type="range" min="0" max="263" value="0" aria-label="动画帧"><div id="status">正在加载…</div></main>
<script>const clips=${JSON.stringify(clips)},cache=new Map(),canvases=[...document.querySelectorAll('canvas')],seek=document.getElementById('seek'),play=document.getElementById('play');let paused=false,crop=false,t=0,last=null;
function indexAt(time){let end=0;return clips[0].frames.findIndex(f=>(end+=f.durationMs)>time)}
function draw(){let index=Math.max(0,indexAt(Math.min(t,4399)));canvases.forEach((c,n)=>{let ctx=c.getContext('2d'),im=cache.get(clips[n].frames[index].file);ctx.clearRect(0,0,512,512);if(im){if(crop)ctx.drawImage(im,52,264,160,190,40,0,432,512);else ctx.drawImage(im,0,0)}});seek.value=index;document.getElementById('time').textContent=(t/1000).toFixed(2)+' / 4.40 秒 · 帧 '+index;play.textContent=paused?'播放':'暂停'}
play.onclick=()=>{paused=!paused;last=null;draw()};document.getElementById('restart').onclick=()=>{t=0;paused=false;last=null;draw()};seek.oninput=()=>{paused=true;t=clips[0].frames.slice(0,+seek.value).reduce((s,f)=>s+f.durationMs,0);draw()};document.getElementById('background').onclick=e=>{document.body.classList.toggle('dark');e.target.textContent=document.body.classList.contains('dark')?'浅色背景':'深色背景'};document.getElementById('crop').onclick=e=>{crop=!crop;e.target.textContent=crop?'查看全身':'放大左脸';draw()};document.getElementById('size').onchange=e=>canvases.forEach(c=>{c.style.width=e.target.value+'px';c.style.height=e.target.value+'px'});
async function init(){for(const file of new Set(clips.flatMap(c=>c.frames.map(f=>f.file)))){const im=new Image();im.src='../../assets/strawberry-cat/'+file;await im.decode();cache.set(file,im)}document.getElementById('status').textContent='已加载 '+cache.size+' 张原始 PNG · 同步循环播放';draw();requestAnimationFrame(tick)}function tick(now){if(last!==null&&!paused)t=(t+(now-last)*Number(document.getElementById('speed').value))%4400;last=now;draw();requestAnimationFrame(tick)}init().catch(e=>{document.getElementById('status').textContent='加载失败：'+e.message});</script></html>`;
  const output = path.join(repo, 'artifacts/yawn-repair/preview.html');
  await fs.mkdir(path.dirname(output), { recursive: true }); await fs.writeFile(output, html);
  console.log(output);
}
main().catch(error => { console.error(error); process.exitCode = 1; });
