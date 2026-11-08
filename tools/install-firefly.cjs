'use strict';
// Install the approved, lossless two-track export without resampling old clips.
const fs=require('node:fs/promises'),path=require('node:path');
const repo=path.resolve(__dirname,'..'),assetRoot=path.join(repo,'assets/strawberry-cat');
async function main(){
 const runtime=JSON.parse(await fs.readFile(path.join(repo,'reference/firefly-v3-runtime.json')));
 const manifestPath=path.join(assetRoot,'manifest.json'),manifest=JSON.parse(await fs.readFile(manifestPath));
 if(manifest.version>=9)throw Error('Refused legacy firefly installation: the current 0.3.4-or-newer manifest uses the complete 60 fps action policy. Reproduce it with node tools/resample-motion-60hz.cjs --write; this installer would downgrade it to 0.3.3.');
 const idle=manifest.clips.idle.frames[0].file;
 if(runtime.durationMs!==10000||runtime.cat.width!==manifest.canvas.width||runtime.cat.height!==manifest.canvas.height||
    runtime.cat.frames[0].file!==idle||runtime.cat.frames.at(-1).file!==idle)throw Error('Firefly must join the existing idle canvas and pose.');
 if(runtime.cat.frames.length!==runtime.overlay.frames.length)throw Error('Track lengths differ.');
 for(const [i,frame] of runtime.cat.frames.entries())if(frame.durationMs!==runtime.overlay.frames[i].durationMs)throw Error('Track timing differs at '+i);
 for(const track of [runtime.cat,runtime.overlay]){
   if(track.frames.reduce((n,f)=>n+f.durationMs,0)!==runtime.durationMs)throw Error('Incorrect duration.');
   for(const file of new Set(track.frames.map(f=>f.file))){
     const resolved=path.resolve(assetRoot,file),relative=path.relative(assetRoot,resolved);
     if(relative.startsWith('..')||path.isAbsolute(relative))throw Error('Asset outside pack.');
     await fs.access(resolved);
   }
 }
 manifest.version=8;
 manifest.clips.firefly={loop:false,frames:runtime.cat.frames};
 manifest.decorations={...manifest.decorations,firefly:{
   canvas:{width:runtime.overlay.width,height:runtime.overlay.height},
   offset:{x:runtime.overlay.offsetX,y:runtime.overlay.offsetY},frames:runtime.overlay.frames
 }};
 manifest.motion.revision='0.3.3';
 manifest.motion.clickReactions.choices=['rise','wink','firefly'];
 manifest.motion.clickReactions.selection='equal probability among available single-click reactions; rise includes hold/settle';
 manifest.motion.clickReactions.firefly={revision:'approved-v3',durationMs:runtime.durationMs,sampleFps:runtime.fps,
   frameSlots:runtime.cat.frames.length,recipe:'tools/build-firefly.cjs',runtime:'reference/firefly-v3-runtime.json',
   overlay:'decorations.firefly',visualApproval:{status:'approved',date:'2026-11-08',reviewer:'user'}};
 manifest.sampling.clipOverrides={...manifest.sampling.clipOverrides,firefly:{targetFps:60,method:'Approved v3 analytic animation exported losslessly at its original 60 fps; shared cat/overlay time slots.'}};
 await fs.writeFile(manifestPath,JSON.stringify(manifest,null,2)+'\n');
 const files=new Set([...Object.values(manifest.clips),...Object.values(manifest.decorations)].flatMap(c=>c.frames.map(f=>f.file)));
 console.log(JSON.stringify({clips:Object.keys(manifest.clips).length,uniquePngs:files.size,durationMs:runtime.durationMs,sourceArtUnchanged:true},null,2));
}
main().catch(e=>{console.error(e);process.exitCode=1;});
