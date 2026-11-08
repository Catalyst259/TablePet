'use strict';
// Reproduce 0.3.4 from the complete 0.3.3 archive. Only the manifest is changed;
// no PNG is generated, encoded, overwritten or removed.
const fs=require('node:fs/promises'),path=require('node:path'),crypto=require('node:crypto');
const assert=require('node:assert/strict'),{isDeepStrictEqual}=require('node:util');
const repo=path.resolve(__dirname,'..');
const sourcePath='reference/motion-60hz/source-manifest-0.3.3.json';
const inventoryPath='reference/motion-60hz/source-png-inventory.json';
const runtimePath='assets/strawberry-cat/manifest.json';
const sourceSha256='82bf49467ef3ff47524418a79dbbf50c3c5526448c7b4e72a5698594dea6fc1a';
const inventorySha256='48bd33a33a1067844e8a2ecf03200bbf674609994c9c6834806abdcbb6013363';
const clipNames=['idle','blink','fall_asleep','sleep_loop','wake','rise','hold','settle','yawn','wink','meow','firefly'].sort();
const hash=bytes=>crypto.createHash('sha256').update(bytes).digest('hex');
const parse=bytes=>JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/,''));
const serialize=value=>JSON.stringify(value,null,2)+'\n';
const duration=clip=>clip.frames.reduce((sum,frame)=>sum+frame.durationMs,0);
const allTracks=manifest=>[...Object.values(manifest.clips),...Object.values(manifest.decorations||{})];
const referencedFiles=manifest=>[...new Set(allTracks(manifest).flatMap(clip=>clip.frames.map(frame=>frame.file)))].sort();

function validateClip(clip){
  assert(clip&&Array.isArray(clip.frames)&&clip.frames.length>0,'A clip must contain frames.');
  assert(clip.frames.every(frame=>typeof frame.file==='string'&&frame.file.length>0&&Number.isSafeInteger(frame.durationMs)&&frame.durationMs>0),'Invalid frame or duration.');
  assert(Number.isSafeInteger(duration(clip)),'Clip duration exceeds the safe integer range.');
}
function resampleClip(clip){
  validateClip(clip);
  if(clip.frames.length===1)return structuredClone(clip);
  const total=duration(clip),frames=[];let index=0,sourceEnd=clip.frames[0].durationMs;
  for(let slot=0,start=0;start<total;slot++,start=Math.round(slot*1000/60)){
    const end=Math.min(total,Math.round((slot+1)*1000/60));
    while(start>=sourceEnd&&index<clip.frames.length-1)sourceEnd+=clip.frames[++index].durationMs;
    frames.push({file:end===total?clip.frames.at(-1).file:clip.frames[index].file,durationMs:end-start});
  }
  assert.equal(frames[0].file,clip.frames[0].file,'The clip is too short to preserve both endpoint poses at 60 fps.');
  const result={...structuredClone(clip),frames};
  assert.equal(duration(result),total);assert.equal(frames.at(-1).file,clip.frames.at(-1).file);
  return result;
}
function stats(clip){return{durationMs:duration(clip),frameSlots:clip.frames.length,uniquePngs:new Set(clip.frames.map(frame=>frame.file)).size,loop:clip.loop===true,static:clip.frames.length===1};}
function totals(manifest){
  const characterFrameSlots=Object.values(manifest.clips).reduce((sum,clip)=>sum+clip.frames.length,0);
  const decorationFrameSlots=Object.values(manifest.decorations||{}).reduce((sum,clip)=>sum+clip.frames.length,0);
  return{characterFrameSlots,decorationFrameSlots,totalFrameSlots:characterFrameSlots+decorationFrameSlots,uniquePngs:referencedFiles(manifest).length};
}
function validateFirefly(manifest){
  const cat=manifest.clips.firefly,overlay=manifest.decorations.firefly;
  validateClip(cat);validateClip(overlay);
  assert.equal(cat.frames.length,600);assert.equal(overlay.frames.length,600);
  assert.equal(duration(cat),10000);assert.equal(duration(overlay),10000);
  assert.deepEqual(cat.frames.map(frame=>frame.durationMs),overlay.frames.map(frame=>frame.durationMs));
  for(let i=0;i<600;i++)assert.equal(cat.frames[i].durationMs,Math.round((i+1)*1000/60)-Math.round(i*1000/60));
  assert.equal(cat.frames[0].file,manifest.clips.idle.frames[0].file);
  assert.equal(cat.frames.at(-1).file,manifest.clips.idle.frames[0].file);
  assert.equal(cat.loop,false);assert.notEqual(overlay.loop,true);
}
function buildManifest(source,checksum=sourceSha256){
  assert.equal(checksum,sourceSha256,'The archived 0.3.3 source manifest changed.');
  assert.equal(source.version,8);assert.equal(source.motion.revision,'0.3.3');assert.equal(source.motion.sampleFps,120);
  assert.deepEqual(Object.keys(source.clips).sort(),clipNames);
  assert.deepEqual(Object.keys(source.decorations),['firefly']);validateFirefly(source);
  const manifest=structuredClone(source);
  for(const [name,clip]of Object.entries(source.clips))manifest.clips[name]=name==='firefly'?structuredClone(clip):resampleClip(clip);
  manifest.version=9;
  manifest.idleActions=[{clip:'blink',weight:2},{clip:'yawn',weight:1},{clip:'rise',weight:1},{clip:'firefly',weight:1}];
  manifest.motion.revision='0.3.4';manifest.motion.sampleFps=60;
  // Keep generation provenance explicit instead of describing the new runtime
  // as a freshly interpolated or visually re-approved animation.
  manifest.motion.authoringInterpolation=source.motion.interpolation;
  manifest.motion.interpolation='No new interpolation: select existing PNGs at 60 fps; preserve the original 60 fps firefly tracks exactly.';
  const expressions=source.motion.idleExpressions||{},legacyAuthoring={...structuredClone(expressions)};delete legacyAuthoring.clips;
  const firefly={...structuredClone(source.motion.clickReactions.firefly),...stats(manifest.clips.firefly),sampleFps:60};
  manifest.motion.idleExpressions={legacyAuthoring,clips:{
    blink:{...stats(manifest.clips.blink),sampleFps:60,sourceManifest:sourcePath},
    yawn:{...structuredClone(expressions.clips?.yawn||{}),...stats(manifest.clips.yawn),sampleFps:60},
    rise:{sequence:['rise','hold','settle'],holdDuration:'Configured by the behavior controller; hold remains a static loop.',sourceManifest:sourcePath},
    firefly
  }};
  manifest.motion.clickReactions={choices:['wink','meow'],selection:'equal probability among available single-click reactions',
    wink:{...structuredClone(source.motion.clickReactions.wink),...stats(manifest.clips.wink),sampleFps:60},
    meow:{...stats(manifest.clips.meow),sampleFps:60,sourceManifest:sourcePath}};
  manifest.motion.doubleClickReaction={clip:'meow',selection:'always meow when available'};
  manifest.motion.idleSelection='Weighted idle actions: blink 2, yawn 1, rise/hold/settle 1, firefly 1. The behavior controller owns compound rise playback.';
  manifest.motion.clipStatistics=Object.fromEntries(Object.entries(manifest.clips).map(([name,clip])=>[name,stats(clip)]));
  for(const key of ['blinkPreserved','blinkKeyPosesPreserved','blinkTransitionFramesAdded'])delete manifest.motion[key];
  delete manifest.previewSampling;
  manifest.sampling={sourceManifest:sourcePath,sourceManifestSha256:sourceSha256,sourceRevision:'0.3.3',
    sourceFps:{legacyClips:120,firefly:60},targetFps:60,
    method:'Select the original frame at rounded 60 Hz start times; clamp the final slot to the original duration and retain the final pose. Static clips stay single-slot. Firefly character and decoration slots are copied unchanged.',
    originalPngBytesPreserved:true,sourceSampling:structuredClone(source.sampling),
    clipOverrides:{firefly:{targetFps:60,method:'No resampling; both approved 600-slot tracks are copied byte-for-byte as JSON values.'}},
    statistics:totals(manifest)};
  for(const [name,clip]of Object.entries(manifest.clips)){
    const original=source.clips[name];assert.equal(duration(clip),duration(original),name+' duration');
    assert.equal(clip.loop,original.loop,name+' loop');
    assert.equal(clip.frames[0].file,original.frames[0].file,name+' first pose');
    assert.equal(clip.frames.at(-1).file,original.frames.at(-1).file,name+' final pose');
    const allowed=new Set(original.frames.map(frame=>frame.file));assert(clip.frames.every(frame=>allowed.has(frame.file)),name+' must only select source PNGs');
    if(original.frames.length===1)assert.deepEqual(clip,original,name+' static clip');
  }
  assert.deepEqual(manifest.clips.firefly,source.clips.firefly);
  assert.deepEqual(manifest.decorations,source.decorations);validateFirefly(manifest);
  return manifest;
}
async function containedFile(root,relative){
  assert(typeof relative==='string'&&relative.length&&!path.isAbsolute(relative)&&!relative.includes('\\')&&!relative.split('/').includes('..'),'Expected a contained forward-slash path.');
  const realRoot=await fs.realpath(root),resolved=await fs.realpath(path.resolve(realRoot,relative)),tail=path.relative(realRoot,resolved);
  assert(tail&&tail!=='..'&&!tail.startsWith('..'+path.sep)&&!path.isAbsolute(tail),'Input path escapes its expected root.');
  assert((await fs.stat(resolved)).isFile(),'Expected a file: '+relative);return resolved;
}
async function diskStats(root){
  let count=0,bytes=0;for(const entry of await fs.readdir(root,{withFileTypes:true})){
    const file=path.join(root,entry.name);
    if(entry.isDirectory()){const nested=await diskStats(file);count+=nested.count;bytes+=nested.bytes;}
    else if(entry.isFile()&&entry.name.endsWith('.png')){count++;bytes+=(await fs.stat(file)).size;}
  }return{count,bytes};
}
async function collect(root=repo){
  const sourceBytes=await fs.readFile(await containedFile(root,sourcePath));assert.equal(hash(sourceBytes),sourceSha256);
  const inventoryBytes=await fs.readFile(await containedFile(root,inventoryPath));assert.equal(hash(inventoryBytes),inventorySha256,'The source PNG inventory changed.');
  const source=parse(sourceBytes),inventory=parse(inventoryBytes),manifest=buildManifest(source,hash(sourceBytes));
  assert.equal(inventory.sourceManifestSha256,sourceSha256);
  assert.deepEqual(inventory.files.map(file=>file.file),referencedFiles(source));
  const assetRoot=path.join(root,'assets/strawberry-cat'),sizes=new Map();
  // Validate all source-referenced PNGs, including those omitted at 60 fps.
  for(const record of inventory.files){const png=await fs.readFile(await containedFile(assetRoot,record.file));assert.equal(png.length,record.bytes,record.file+' size');assert.equal(hash(png),record.sha256,record.file+' bytes');sizes.set(record.file,record.bytes);}
  const pngBytes=m=>referencedFiles(m).reduce((sum,file)=>sum+sizes.get(file),0),disk=await diskStats(path.join(assetRoot,'frames'));
  const before={...totals(source),pngBytes:pngBytes(source),allFramePngsOnDisk:inventory.allSourceFramePngsOnDisk};
  const after={...totals(manifest),pngBytes:pngBytes(manifest),allFramePngsOnDisk:disk};
  const clips=Object.fromEntries(Object.keys(source.clips).map(name=>[name,{before:stats(source.clips[name]),after:stats(manifest.clips[name])}]));
  return{source,manifest,report:{revision:'0.3.4',sourceRevision:'0.3.3',sourceManifest:sourcePath,sourceManifestSha256:sourceSha256,
    sourceInventorySha256:inventorySha256,targetFps:60,before,after,
    removedRuntimeFrameSlots:before.totalFrameSlots-after.totalFrameSlots,omittedRuntimePngs:before.uniquePngs-after.uniquePngs,
    runtimePngBytesSaved:before.pngBytes-after.pngBytes,allSourcePngHashesMatch:true,pngsReencoded:0,pngsDeleted:0,
    originalDurationsEndpointsLoopsAndStaticClipsPreserved:true,fireflyCharacterAndDecorationExactlyPreserved:true,clips}};
}
async function selfTest(){
  let checks=0;const check=(condition,message)=>{assert(condition,message);checks++;};
  const staticClip={loop:true,frames:[{file:'rest.png',durationMs:1000}]};check(isDeepStrictEqual(resampleClip(staticClip),staticClip),'Static clip changed.');
  const source={loop:false,frames:Array.from({length:45},(_,i)=>({file:'f'+i+'.png',durationMs:Math.round((i+1)*1000/120)-Math.round(i*1000/120)}))};
  const sampled=resampleClip(source);check(sampled.frames.length===23,'375 ms must have 23 slots.');check(sampled.frames.at(-1).durationMs===8,'375 ms tail must be 8 ms.');
  check(sampled.frames.every((frame,index)=>frame.file==='f'+(index*2)+'.png'),'Rounded 60 Hz starts must select the matching even 120 Hz source slots.');
  check(duration(sampled)===375&&sampled.frames[0].file==='f0.png'&&sampled.frames.at(-1).file==='f44.png','Endpoints or total changed.');
  let end=0;for(let i=0;i<sampled.frames.length;i++){check(end===Math.round(i*1000/60),'Non-60 Hz boundary.');end+=sampled.frames[i].durationMs;}
  check(isDeepStrictEqual(resampleClip({...source,loop:true}).frames,sampled.frames),'Loop sampling changed.');
  const boundary=resampleClip({loop:false,frames:[{file:'a',durationMs:17},{file:'b',durationMs:16},{file:'c',durationMs:17}]});check(boundary.frames[1].file==='b','Exact boundary must select the following source frame.');
  assert.throws(()=>resampleClip({frames:[{file:'x',durationMs:0}]}));checks++;
  const bytes=await fs.readFile(await containedFile(repo,sourcePath)),approved=parse(bytes),runtime=buildManifest(approved,hash(bytes));
  check(isDeepStrictEqual(runtime.clips.firefly,approved.clips.firefly),'Firefly body changed.');
  check(isDeepStrictEqual(runtime.decorations,approved.decorations),'Firefly overlay changed.');
  check(isDeepStrictEqual(runtime.idleActions,[{clip:'blink',weight:2},{clip:'yawn',weight:1},{clip:'rise',weight:1},{clip:'firefly',weight:1}]),'Idle action weights changed.');
  check(isDeepStrictEqual(runtime.motion.clickReactions.choices,['wink','meow']),'Single-click pool changed.');
  check(runtime.motion.doubleClickReaction.clip==='meow','Double-click action changed.');
  check(runtime.motion.revision==='0.3.4'&&runtime.version===9&&runtime.motion.sampleFps===60,'Runtime version or FPS changed.');
  check(isDeepStrictEqual(approved,parse(bytes)),'Source was mutated.');
  assert.throws(()=>buildManifest(approved,'bad'));checks++;
  const bad=structuredClone(approved);bad.decorations.firefly.frames[1].durationMs++;assert.throws(()=>buildManifest(bad));checks++;
  for(const clip of Object.values(runtime.clips))if(clip.frames.length>1){let time=0;for(let i=0;i<clip.frames.length;i++){check(time===Math.round(i*1000/60),'Runtime boundary');time+=clip.frames[i].durationMs;}check(time===duration(clip),'Runtime total');}
  return{passed:true,mode:'self-test',checks,sourceMutation:false,pngWrites:0};
}
async function main(args){
  assert(args.length===1&&['--write','--verify','--self-test'].includes(args[0]),'Usage: node tools/resample-motion-60hz.cjs --write|--verify|--self-test');
  if(args[0]==='--self-test')return selfTest();
  const destination=await containedFile(repo,runtimePath),beforeBytes=await fs.readFile(destination),current=parse(beforeBytes);
  const {source,manifest,report}=await collect(),output=serialize(manifest);
  if(args[0]==='--write'){
    assert(isDeepStrictEqual(current,source)||isDeepStrictEqual(current,manifest),'Refused to overwrite an unrecognized or newer runtime manifest. Restore the archived 0.3.3 input or the reproducible 0.3.4 result deliberately.');
    assert((await fs.readFile(destination)).equals(beforeBytes),'Manifest changed concurrently; refused to overwrite it.');
    await fs.writeFile(destination,output);
    const diskAfter=await diskStats(path.join(repo,'assets/strawberry-cat/frames'));assert.deepEqual(diskAfter,report.after.allFramePngsOnDisk,'PNG files changed during manifest conversion.');
    await fs.writeFile(path.join(repo,'reference/motion-60hz/conversion-report.json'),serialize(report));
  }else{
    assert(isDeepStrictEqual(current,manifest),'Runtime differs from the reproducible 0.3.4 / 60 fps manifest.');
    assert.equal(beforeBytes.toString('utf8').replaceAll('\r\n','\n'),output,'Runtime manifest formatting differs.');
  }
  return{passed:true,mode:args[0].slice(2),runtimeManifestSha256:hash(output),...report};
}
module.exports={resampleClip,buildManifest,collect,selfTest};
if(require.main===module)main(process.argv.slice(2)).then(result=>console.log(JSON.stringify(result,null,2))).catch(error=>{console.error(error.message);process.exitCode=1;});
