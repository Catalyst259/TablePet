'use strict';
// Snapshot or verify the original checkout without writing anything inside it.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
const repo = path.resolve(__dirname, '..');
const digest = b => crypto.createHash('sha256').update(b).digest('hex');
async function main() {
  const [mode, sourceArg] = process.argv.slice(2);
  if (!['snapshot','verify'].includes(mode) || !sourceArg) throw Error('Usage: node tools/frozen-source.cjs snapshot|verify <original-repo>');
  const source = path.resolve(sourceArg);
  if (source.toLowerCase() === repo.toLowerCase()) throw Error('Original and rewrite must be separate directories');
  const names = execFileSync('git', ['--no-optional-locks','-C',source,'ls-files','--cached','--others','--exclude-standard','-z'], {windowsHide:true,maxBuffer:16*1024*1024}).toString().split('\0').filter(Boolean).sort();
  const files = [];
  for (const name of [...new Set(names)]) {
    const bytes = await fs.readFile(path.join(source,name));
    files.push({file:name,bytes:bytes.length,sha256:digest(bytes)});
  }
  const file = path.join(repo,'artifacts','frozen-source','snapshot.json');
  if (mode === 'snapshot') {
    try { await fs.access(file); throw Error('Frozen source snapshot already exists'); }
    catch (e) { if (e.code !== 'ENOENT') throw e; }
    await fs.mkdir(path.dirname(file),{recursive:true});
    await fs.writeFile(file,JSON.stringify({version:1,source,files},null,2)+'\n');
  } else {
    const baseline = JSON.parse(await fs.readFile(file,'utf8'));
    if (JSON.stringify(baseline.files) !== JSON.stringify(files)) throw Error('Original checkout differs from frozen snapshot');
    const report = {files:files.length,bytes:files.reduce((sum,f)=>sum+f.bytes,0),exact:true,source};
    await fs.writeFile(path.join(path.dirname(file),'verification.json'),JSON.stringify(report,null,2)+'\n');
  }
  console.log(JSON.stringify({mode,files:files.length,bytes:files.reduce((sum,f)=>sum+f.bytes,0)}));
}
main().catch(e=>{console.error(e);process.exitCode=1;});
