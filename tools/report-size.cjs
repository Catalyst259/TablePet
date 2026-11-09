'use strict';
const fs=require('node:fs/promises'),path=require('node:path');
const repo=path.resolve(__dirname,'..');
async function sumTree(dir){let bytes=0,files=0;for(const e of await fs.readdir(dir,{withFileTypes:true})){const p=path.join(dir,e.name);if(e.isDirectory()){const child=await sumTree(p);bytes+=child.bytes;files+=child.files;}else{bytes+=(await fs.stat(p)).size;files++;}}return{bytes,files};}
async function main(){
  const source=path.resolve(process.argv[2]||path.join(repo,'../TablePet.Legacy')),oldRoot=path.join(source,'assets/strawberry-cat'),newRoot=path.join(repo,'assets/strawberry-cat');
  const manifest=JSON.parse(await fs.readFile(path.join(oldRoot,'manifest.json'),'utf8'));
  const files=[...new Set([...Object.values(manifest.clips),...Object.values(manifest.decorations||{})].flatMap(c=>c.frames.map(f=>f.file)))];
  let originalPngBytes=0;for(const f of files)originalPngBytes+=(await fs.stat(path.join(oldRoot,f))).size;
  // Legacy authoring drafts may remain untracked in this checkout. Count only
  // the runtime content shipped by the skeletal project, never that history.
  const skeletal=await sumTree(path.join(newRoot,'skeletal'));
  const commonFiles=['manifest.json','fallback.png','tray.ico'];
  const newCommonBytes=(await Promise.all(commonFiles.map(async f=>(await fs.stat(path.join(newRoot,f))).size))).reduce((s,n)=>s+n,0);
  const allAssets={bytes:skeletal.bytes+newCommonBytes,files:skeletal.files+commonFiles.length};
  const originalCommonBytes=(await Promise.all(['manifest.json','fallback.png','tray.ico'].map(async f=>(await fs.stat(path.join(oldRoot,f))).size))).reduce((s,n)=>s+n,0);
  const report={original:{frameFiles:files.length,pngBytes:originalPngBytes,totalRuntimeAssetBytes:originalPngBytes+originalCommonBytes},rewritten:{skeletal,totalRuntimeAssets:allAssets},animationReductionPercent:100*(1-skeletal.bytes/originalPngBytes),totalAssetReductionPercent:100*(1-allAssets.bytes/(originalPngBytes+originalCommonBytes)),components:{}};
  for(const name of ['legacy','work','firefly'])report.components[name]=await sumTree(path.join(newRoot,'skeletal',name));
  try{
    const previous=(await fs.stat(path.join(source,'dist/TablePet-0.4.0-win-x64.zip'))).size;
    const rewritten=(await fs.stat(path.join(repo,'dist/TablePet.Skeletal-0.5.0-win-x64.zip'))).size;
    report.selfContainedZip={originalBytes:previous,rewrittenBytes:rewritten,reductionPercent:100*(1-rewritten/previous),note:'Both include the Windows x64 .NET runtime. New runtime bundle additionally uses standard single-file compression.'};
  }catch(e){if(e.code!=='ENOENT')throw e;}
  await fs.mkdir(path.join(repo,'reference/skeletal'),{recursive:true});
  await fs.writeFile(path.join(repo,'reference/skeletal/size-report.json'),JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify(report,null,2));
}
main().catch(e=>{console.error(e);process.exitCode=1;});
