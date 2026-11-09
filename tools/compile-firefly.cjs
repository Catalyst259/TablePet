'use strict';
// Compile the frozen v3 rig; the original checkout is only read. Preparation
// scripts run inside a disposable, independent authoring tree, never in source.
const fs=require('node:fs/promises'),path=require('node:path'),crypto=require('node:crypto'),zlib=require('node:zlib');
const repo=path.resolve(__dirname,'..');
const sharp=require('sharp');
const sha=b=>crypto.createHash('sha256').update(b).digest('hex');
const smooth=x=>{x=Math.max(0,Math.min(1,x));return x*x*(3-2*x);};
async function main(){
 const original=path.resolve(process.argv[2]||path.join(repo,'../TablePet.Legacy'));
 if(original===repo)throw Error('Source must be a separate frozen checkout');
 const source=path.join(original,'assets/strawberry-cat/sources/firefly-v3');
 const staging=path.join(repo,'artifacts/firefly-authoring'),root=path.join(staging,'assets/strawberry-cat/sources/firefly-v3');
 const output=path.join(repo,'assets/strawberry-cat/skeletal/firefly');
 await fs.mkdir(root,{recursive:true});await fs.mkdir(output,{recursive:true});
 for(const file of ['body.cjs','body-mask.cjs','eyes-rig.cjs','eyes-geometry.cjs','choreography.cjs','eyes/geometry.json',...['firefly.png','iris.png','pupils.png','right-corrected.png','swat-anatomy-reference.png','underbody.png','up-larger.png','prepare-mouth.png'].map(f=>'sources/'+f)]){
  await fs.mkdir(path.dirname(path.join(root,file)),{recursive:true});await fs.copyFile(path.join(source,file),path.join(root,file));
 }
 const basePath=path.join(staging,'assets/strawberry-cat/frames/fall_asleep-00.png');await fs.mkdir(path.dirname(basePath),{recursive:true});await fs.copyFile(path.join(original,'assets/strawberry-cat/frames/fall_asleep-00.png'),basePath);
 async function amend(name,changes){let s=await fs.readFile(path.join(root,name),'utf8');for(const[a,b]of changes){if(!s.includes(a))throw Error('Frozen source anchor changed: '+name+' '+a);s=s.replace(a,b);}await fs.writeFile(path.join(root,name),s);}
 await amend('body.cjs',[
  ['return amount=>{','const morph = amount=>{'],
  ['}else mixAt(result,p,source,target,amount);','}else mixAt(result,p,source,target,amount);'],
  ['}return result;\n  };\n}', '}return result;\n  };\n  morph.data={contours,sourceDark,targetDark}; return morph;\n}'],
  ['return {render,metadata,compositeForeground,finalize:', 'return {data:{base,paw,cleared,exposedUnderbody,arm,supportMask,mouthTexture,mouthMask,morph:morphUnderbody.data},render,metadata,compositeForeground,finalize:']
 ]);
 await amend('eyes-rig.cjs', [['return {render,measureEyes,base,metadata,poseData,frames:', 'return {data:eyes,render,measureEyes,base,metadata,poseData,frames:']]);
 const body=await require(path.join(root,'body.cjs')).createBodyRig(),eye=await require(path.join(root,'eyes-rig.cjs')).createRig(),choreo=require(path.join(root,'choreography.cjs'));
 const chunks=[],buffers={};let offset=0;
 function store(name,value){const b=Buffer.isBuffer(value)?value:Buffer.from(value.buffer,value.byteOffset,value.byteLength);buffers[name]={offset,length:b.length,type:Buffer.isBuffer(value)?'u8':'f32'};chunks.push(b);offset+=b.length;return name;}
 const bd=body.data,bodyData={};
 for(const name of ['base','paw','cleared','exposedUnderbody','arm','supportMask','mouthTexture','mouthMask'])bodyData[name]=store('body/'+name,bd[name]);
 bodyData.contours=bd.morph.contours;bodyData.sourceDark=store('body/sourceDark',bd.morph.sourceDark);bodyData.targetDark=store('body/targetDark',bd.morph.targetDark);
 const eyes=eye.data.map((e,i)=>({baseFrame:e.baseFrame,layer:store('eye/'+i+'/layer',e.layer),mask:store('eye/'+i+'/mask',e.mask),background:store('eye/'+i+'/background',e.background),highlights:e.highlights.map((h,j)=>({width:h.width,height:h.height,sourceCenter:h.sourceCenter,upWidth:h.upWidth,upHeight:h.upHeight,upSourceCenter:h.upSourceCenter,patch:store('eye/'+i+'/'+j+'/patch',h.patch),upPatch:store('eye/'+i+'/'+j+'/upPatch',h.upPatch)}))}));
 const frames=[],sprites=[],spriteMap=new Map(),expected=[];
 const manifest=JSON.parse(await fs.readFile(path.join(original,'assets/strawberry-cat/manifest.json'),'utf8'));
 for(let i=0;i<600;i++){
  const t=i/60,m=choreo.sample(t),bs=body.render(m.body),cat=body.finalize(eye.render(bs.background,{...m.gaze,eyes:bs.eyes}),bs);
  const poses=[0,1].map(e=>{const p=eye.poseData(e,m.gaze);return {center:p.center,Pinv:p.Pinv,highlights:p.highlights.map(h=>({center:h.center,Hinv:h.Hinv,upBlend:h.upBlend}))};});
  let fly=null;
  if(m.fly.opacity>.0001){
   const departure=smooth((t-6.15)/.95),rotation=-24*(1-departure)+43*departure,pulse=.92+.08*Math.sin(t*Math.PI*2*.85),width=Math.max(24,Math.round(92*m.fly.scale));
   const key=width+'/'+rotation;let sprite=spriteMap.get(key);
   if(sprite===undefined){const r=await sharp(path.join(root,'sources/firefly.png')).resize({width}).rotate(rotation,{background:'#00000000'}).ensureAlpha().raw().toBuffer({resolveWithObject:true});sprite=sprites.length;spriteMap.set(key,sprite);sprites.push({width:r.info.width,height:r.info.height,data:store('fly/'+sprite,r.data)});}
   const s=sprites[sprite];fly={sprite,left:Math.round(m.fly.x-s.width*.5),top:Math.round(m.fly.y-s.height*.5),opacity:m.fly.opacity,pulse,behind:m.fly.behind};
  }
  frames.push({body:m.body,eyeFrames:bs.eyes,engagement:m.gaze.engagement,poses,fly});
  const expectedCat=await sharp(path.join(original,'assets/strawberry-cat',manifest.clips.firefly.frames[i].file)).ensureAlpha().raw().toBuffer();
  if(!cat.equals(expectedCat))throw Error('Frozen JavaScript rig does not match source at '+i);
  const overlay=await sharp(path.join(original,'assets/strawberry-cat',manifest.decorations.firefly.frames[i].file)).ensureAlpha().raw().toBuffer();
  expected.push({index:i,cat:sha(expectedCat),overlay:sha(overlay)});
  if(i%100===0)console.log('Firefly compile '+i+'/600');
 }
 const files={};for(let i=0;i<600;i++){files[manifest.clips.firefly.frames[i].file]={index:i,overlay:false};files[manifest.decorations.firefly.frames[i].file]={index:i,overlay:true};}
 const textureData=Buffer.concat(chunks),compressed=zlib.deflateSync(textureData,{level:9});
 const pack={version:1,buffers,body:bodyData,eyes,sprites,frames,files,textureBytes:textureData.length,textureSha256:sha(textureData)};
 await fs.writeFile(path.join(output,'firefly.json'),JSON.stringify(pack));await fs.writeFile(path.join(output,'textures.bin.z'),compressed);
 await fs.mkdir(path.join(repo,'reference'),{recursive:true});await fs.writeFile(path.join(repo,'reference/firefly-expected.json'),JSON.stringify(expected));
 let sourceBytes=0;for(const file of Object.keys(files))sourceBytes+=(await fs.stat(path.join(original,'assets/strawberry-cat',file))).size;
 const report={frames:frames.length,spriteAttachments:sprites.length,rawTextureBytes:offset,textureBytes:compressed.length,jsonBytes:(await fs.stat(path.join(output,'firefly.json'))).size,sourceFiles:Object.keys(files).length,sourceBytes,sourceRigExact:true};
 await fs.writeFile(path.join(repo,'reference/firefly-compilation.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report));
}
main().catch(e=>{console.error(e);process.exitCode=1;});
