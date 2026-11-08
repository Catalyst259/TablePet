'use strict';
// Lossless runtime export of the approved v3 preview. No generated artwork,
// geometry, choreography or eye parameters are changed by this builder.
const fs=require('node:fs/promises');
const path=require('node:path');
const crypto=require('node:crypto');
const sharp=require('sharp');
const repo=path.resolve(__dirname,'..');
const assetRoot=path.join(repo,'assets/strawberry-cat');
const sourceRoot=path.join(assetRoot,'sources/firefly-v3');
const referenceRoot=path.join(repo,'reference/firefly-v3');
const frameRoot=path.join(assetRoot,'frames/firefly-v3');
const runtimeFile=path.join(repo,'reference/firefly-v3-runtime.json');
const catSpec={width:512,height:512,channels:4};
const overlaySpec={width:640,height:640,channels:4};
const sourceNames=['body.cjs','body-mask.cjs','eyes-rig.cjs','eyes-geometry.cjs','choreography.cjs','eyes/geometry.json',
  ...['firefly.png','iris.png','pupils.png','right-corrected.png','swat-anatomy-reference.png','underbody.png','up-larger.png','up-reference.png','prepare-mouth.png'].map(f=>'sources/'+f)];
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const relative=f=>path.relative(repo,f).split(path.sep).join('/');
const writeJson=(file,data)=>fs.writeFile(file,JSON.stringify(data,null,2)+'\n');
const clamp=x=>Math.max(0,Math.min(1,x));
const smooth=x=>{x=clamp(x);return x*x*(3-2*x);};

async function validateRigArchive(){
  // These are the ONLY edits from the approved scripts: portable input paths.
  const edits={
    'body.cjs':[["repo=path.resolve(root,'../..')","repo=path.resolve(root,'../../../..')"],["path.join(repo,'artifacts/firefly-preview-v2/sources/prepare.png')","path.join(root,'sources/prepare-mouth.png')"]],
    'body-mask.cjs':[["repo=path.resolve(root,'../..')","repo=path.resolve(root,'../../../..')"]],
    'eyes-rig.cjs':[["path.resolve(__dirname,'../..')","path.resolve(__dirname,'../../../..')"]],
    'eyes-geometry.cjs':[["path.resolve(__dirname,'../..')","path.resolve(__dirname,'../../../..')"]],
    'choreography.cjs':[]
  };
  for(const [file,replacements]of Object.entries(edits)){
    let original=await fs.readFile(path.join(referenceRoot,'approved',file),'utf8');
    for(const [a,b]of replacements){if(!original.includes(a))throw Error('Missing approved path substitution: '+file);original=original.replace(a,b);}
    const portable=await fs.readFile(path.join(sourceRoot,file),'utf8');
    if(original.replaceAll('\r\n','\n')!==portable.replaceAll('\r\n','\n'))throw Error('Approved rig changed beyond portable paths: '+file);
  }
}

async function sourceInventory(){
  const items=[];
  for(const file of sourceNames){const bytes=await fs.readFile(path.join(sourceRoot,file));items.push({file:relative(path.join(sourceRoot,file)),sha256:hash(bytes),bytes:bytes.length});}
  const idle=await fs.readFile(path.join(assetRoot,'frames/fall_asleep-00.png'));
  items.push({file:'assets/strawberry-cat/frames/fall_asleep-00.png',sha256:hash(idle),bytes:idle.length});
  return items;
}

async function createRenderer(){
  const choreography=require(path.join(sourceRoot,'choreography.cjs'));
  if(choreography.duration!==10||choreography.fps!==60)throw Error('Approved timing must remain 10 seconds at 60 fps.');
  const bodyRig=await require(path.join(sourceRoot,'body.cjs')).createBodyRig();
  const eyes=await require(path.join(sourceRoot,'eyes-rig.cjs')).createRig();
  const flySource=path.join(sourceRoot,'sources/firefly.png');
  return async index=>{
    const t=index/60,m=choreography.sample(t),body=bodyRig.render(m.body);
    const cat=bodyRig.finalize(eyes.render(body.background??body.rgba,{...m.gaze,eyes:body.eyes}),body);
    const overlay=Buffer.alloc(640*640*4);let occludedPixels=0;
    if(m.fly.opacity>.0001){
      // Exact approved preview sprite resizing, rotation, pulse and rounding.
      const departure=smooth((t-6.15)/.95),rotation=-24*(1-departure)+43*departure;
      const pulse=.92+.08*Math.sin(t*Math.PI*2*.85),width=Math.max(24,Math.round(92*m.fly.scale));
      const sprite=await sharp(flySource).resize({width}).rotate(rotation,{background:'#00000000'}).ensureAlpha().raw().toBuffer({resolveWithObject:true});
      const fw=sprite.info.width,fh=sprite.info.height,flyLeft=Math.round(m.fly.x-fw*.5),flyTop=Math.round(m.fly.y-fh*.5);
      for(let y=Math.max(0,flyTop);y<Math.min(640,flyTop+fh);y++)for(let x=Math.max(0,flyLeft);x<Math.min(640,flyLeft+fw);x++){
        const sp=((y-flyTop)*fw+x-flyLeft)*4,dp=(y*640+x)*4;
        let alpha=Math.round(sprite.data[sp+3]*m.fly.opacity*pulse);
        if(m.fly.behind&&x>=64&&x<576&&y>=100&&y<612){
          const catAlpha=cat[((y-100)*512+x-64)*4+3];
          if(alpha&&catAlpha)occludedPixels++;
          // The decoration HWND is always in front. Remove the part hidden
          // by the cat so the opening still appears to emerge from behind.
          alpha=Math.round(alpha*(255-catAlpha)/255);
          if(catAlpha===255&&alpha!==0)throw Error('Opaque cat did not occlude rear firefly.');
        }
        if(alpha){sprite.data.copy(overlay,dp,sp,sp+3);overlay[dp+3]=alpha;}
      }
    }
    return {cat,overlay,m,occludedPixels};
  };
}

async function exportFrames(){
  await validateRigArchive();
  await fs.mkdir(frameRoot,{recursive:true});
  await fs.mkdir(referenceRoot,{recursive:true});
  const inventory=await sourceInventory(),render=await createRenderer();
  const idleFile='frames/fall_asleep-00.png',idleBytes=await fs.readFile(path.join(assetRoot,idleFile));
  const idleRaw=await sharp(idleBytes).ensureAlpha().raw().toBuffer();
  const idleHash=hash(idleRaw),catMap=new Map([[idleHash,idleFile]]),overlayMap=new Map(),pool=new Map();
  pool.set(idleFile,{file:idleFile,width:512,height:512,sha256:hash(idleBytes),pixelSha256:idleHash,bytes:idleBytes.length,sharedExisting:true});
  let bytesWritten=0,occludedFrames=0,occludedPixels=0;
  async function store(raw,kind){
    const spec=kind==='cat'?catSpec:overlaySpec,map=kind==='cat'?catMap:overlayMap,pixelSha256=hash(raw);
    if(map.has(pixelSha256))return map.get(pixelSha256);
    let transparent=true;for(let p=3;p<raw.length;p+=4)if(raw[p]){transparent=false;break;}
    const file='frames/firefly-v3/'+(transparent?'transparent':kind+'-'+pixelSha256.slice(0,20))+'.png';
    const existing=pool.get(file);if(existing&&existing.pixelSha256!==pixelSha256)throw Error('Frame hash name collision.');
    const png=await sharp(raw,{raw:spec}).png({compressionLevel:9,adaptiveFiltering:true,palette:false}).toBuffer();
    const decoded=await sharp(png).ensureAlpha().raw().toBuffer();
    if(!decoded.equals(raw))throw Error('Lossless frame roundtrip failed: '+file);
    await fs.writeFile(path.join(assetRoot,file),png);bytesWritten+=png.length;
    pool.set(file,{file,width:spec.width,height:spec.height,sha256:hash(png),pixelSha256,bytes:png.length,transparent});
    map.set(pixelSha256,file);return file;
  }
  const catFrames=[],overlayFrames=[],slots=[];
  for(let i=0;i<600;i++){
    const {cat,overlay,m,occludedPixels:occluded}=await render(i);
    if((i===0||i===599)&&!cat.equals(idleRaw))throw Error('Cat endpoints differ from the approved original idle.');
    const durationMs=Math.round((i+1)*1000/60)-Math.round(i*1000/60),startMs=Math.round(i*1000/60);
    const catFile=await store(cat,'cat'),overlayFile=await store(overlay,'overlay');
    catFrames.push({file:catFile,durationMs});overlayFrames.push({file:overlayFile,durationMs});
    slots.push({index:i,startMs,durationMs,catFile,overlayFile,behind:m.fly.behind});
    if(occluded){occludedFrames++;occludedPixels+=occluded;}
    if(i%120===0)console.log('Exported approved firefly frame '+i+'/600');
  }
  const runtime={version:1,id:'firefly-v3',sourcePreviewVersion:3,durationMs:10000,fps:60,frameCount:600,
    pathsRelativeTo:'assets/strawberry-cat',loop:false,
    cat:{width:512,height:512,frames:catFrames},
    overlay:{width:640,height:640,offsetX:-64,offsetY:-100,clickThrough:true,behindOcclusion:'alpha multiplied by (1 - cat alpha) before front decoration rendering',frames:overlayFrames},
    stats:{catUniqueFrames:catMap.size,overlayUniqueFrames:overlayMap.size,sharedIdleSlots:catFrames.filter(f=>f.file===idleFile).length,sharedTransparentSlots:overlayFrames.filter(f=>f.file.endsWith('/transparent.png')).length,exportedPngBytes:bytesWritten,occludedFrames,occludedPixels},slots};
  await writeJson(runtimeFile,runtime);
  await writeJson(path.join(referenceRoot,'source-inventory.json'),{approvedVersion:3,entry:'node tools/build-firefly.cjs',inputs:inventory,sourceFingerprint:hash(Buffer.from(JSON.stringify(inventory)))});
  await writeJson(path.join(referenceRoot,'frame-pool.json'),{lossless:true,uniqueFiles:[...pool.values()]});
  return verifyExport();
}

async function verifyExport(){
  await validateRigArchive();
  const runtime=JSON.parse(await fs.readFile(runtimeFile,'utf8')),pool=JSON.parse(await fs.readFile(path.join(referenceRoot,'frame-pool.json'),'utf8'));
  const recorded=JSON.parse(await fs.readFile(path.join(referenceRoot,'source-inventory.json'),'utf8'));
  const inputs=await sourceInventory();
  if(hash(Buffer.from(JSON.stringify(inputs)))!==recorded.sourceFingerprint)throw Error('Archived firefly inputs changed after export.');
  if(runtime.frameCount!==600||runtime.durationMs!==10000||runtime.fps!==60)throw Error('Wrong runtime timing.');
  for(const track of [runtime.cat,runtime.overlay]){
    if(track.frames.length!==600||track.frames.reduce((sum,f)=>sum+f.durationMs,0)!==10000)throw Error('Tracks are not 600 synchronized slots / 10000 ms.');
    for(let i=0;i<600;i++)if(track.frames[i].durationMs!==Math.round((i+1)*1000/60)-Math.round(i*1000/60))throw Error('A frame duration was changed.');
  }
  if(runtime.cat.frames[0].file!=='frames/fall_asleep-00.png'||runtime.cat.frames[599].file!=='frames/fall_asleep-00.png')throw Error('Endpoints do not share the original idle file.');
  const records=new Map(pool.uniqueFiles.map(f=>[f.file,f]));
  for(const track of [runtime.cat,runtime.overlay])for(const f of track.frames){const p=records.get(f.file);if(!p||p.width!==track.width||p.height!==track.height)throw Error('Missing frame or incorrect dimensions: '+f.file);}
  for(const record of pool.uniqueFiles){
    const png=await fs.readFile(path.join(assetRoot,record.file));if(hash(png)!==record.sha256)throw Error('Encoded frame changed: '+record.file);
    const decoded=await sharp(png).ensureAlpha().raw().toBuffer({resolveWithObject:true});
    if(decoded.info.width!==record.width||decoded.info.height!==record.height||hash(decoded.data)!==record.pixelSha256)throw Error('Lossless pixel validation failed: '+record.file);
  }
  const report={approvedRigPathOnlyChanges:true,allSourceHashesMatch:true,allPngHashesAndPixelsMatch:true,lossless:true,catDurationMs:10000,overlayDurationMs:10000,frameCount:600,firstAndLastShareOriginalIdle:true,overlayOffset:{x:-64,y:-100},...runtime.stats};
  await writeJson(path.join(referenceRoot,'verification.json'),report);console.log(JSON.stringify(report,null,2));return report;
}

module.exports={createRenderer,validateRigArchive,verifyExport,exportFrames};
if(require.main===module)(process.argv.includes('--verify')?verifyExport():exportFrames()).catch(error=>{console.error(error);process.exitCode=1;});
