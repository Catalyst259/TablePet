'use strict';
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const {execFileSync} = require('node:child_process');
const repo = path.resolve(__dirname,'..');
async function main() {
  const source = path.resolve(process.argv[2] || path.join(repo,'../TablePet'));
  if (source.toLowerCase() === repo.toLowerCase()) throw Error('Use a separate frozen source checkout');
  const sourceRoot = path.join(source,'assets/strawberry-cat');
  const manifest = await fs.readFile(path.join(sourceRoot,'manifest.json'));
  const run = (command,args) => execFileSync(command,args,{cwd:repo,stdio:'inherit',windowsHide:true});
  run(process.execPath,[path.join(__dirname,'compile-firefly.cjs'),source]);
  run(process.execPath,[path.join(__dirname,'compile-work.cjs'),source]);
  const python = process.env.TABLEPET_RIG_PYTHON || path.join(repo,'.tools/rig-python/Scripts/python.exe');
  run(python,[path.join(__dirname,'compile-legacy.py'),'--source',source]);
  const legacy = path.join(repo,'assets/strawberry-cat/skeletal/legacy');
  const packed = path.join(repo,'.tools/legacy-recompressed');
  run(python,[path.join(__dirname,'compile-legacy-recompress.py'),'--source',legacy,'--output',packed,'--workers','2']);
  for (const file of ['legacy.bin','legacy.json','verification.json']) await fs.copyFile(path.join(packed,file),path.join(legacy,file));
  const seekOptimized = path.join(repo,'.tools/legacy-seek-optimized');
  run(python,[path.join(__dirname,'compile-legacy-checkpoints.py'),'--input',legacy,'--source',source,'--output',seekOptimized]);
  for (const file of ['legacy.bin','legacy.json','verification.json']) await fs.copyFile(path.join(seekOptimized,'skeletal/legacy',file),path.join(legacy,file));
  if (!(await fs.readFile(path.join(sourceRoot,'manifest.json'))).equals(manifest)) throw Error('Source manifest changed during import');
  const target = path.join(repo,'assets/strawberry-cat');
  for (const file of ['manifest.json','fallback.png','tray.ico']) await fs.copyFile(path.join(sourceRoot,file),path.join(target,file));
  const components = {};
  for (const name of ['legacy','work','firefly']) {
    const metadata = JSON.parse(await fs.readFile(path.join(target,'skeletal',name,name+'.json'),'utf8'));
    components[name] = name === 'firefly' ? {textureSha256:metadata.textureSha256} : {archiveSha256:metadata.archiveSha256};
  }
  await fs.writeFile(path.join(target,'skeletal/pack.json'),JSON.stringify({version:1,renderers:['legacy','work','firefly'],pixelFormat:'RGBA8',fidelity:'lossless',timeline:'manifest.json',sourceRelease:'0.4.0',sourceManifestSha256:crypto.createHash('sha256').update(manifest).digest('hex'),components})+'\n');
}
main().catch(error=>{console.error(error);process.exitCode=1;});
