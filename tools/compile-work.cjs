'use strict';
// Read-only import of the frozen release. Output is confined to this new repo.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const zlib = require('node:zlib');
const sharp = require('sharp');

const repo = path.resolve(__dirname, '..');
const sha = data => crypto.createHash('sha256').update(data).digest('hex');
const workNames = ['work_enter', 'work_loop', 'work_exit', 'work_props'];

async function compile(sourceRepo) {
  sourceRepo = path.resolve(sourceRepo);
  if (sourceRepo.toLowerCase() === repo.toLowerCase()) throw Error('Expected a separate frozen source repository');
  const sourceRoot = path.join(sourceRepo, 'assets/strawberry-cat');
  const output = path.join(repo, 'assets/strawberry-cat/skeletal/work');
  const manifestBytes = await fs.readFile(path.join(sourceRoot, 'manifest.json'));
  const manifest = JSON.parse(manifestBytes.toString('utf8').replace(/^\uFEFF/, ''));
  const files = [...new Set(workNames.flatMap(name => manifest.clips[name].frames.map(frame => frame.file)))];
  const baseFile = manifest.clips.idle.frames[0].file;
  const propsFile = manifest.clips.work_props.frames[0].file;
  const inventory = [];
  async function read(file) {
    const absolute = path.resolve(sourceRoot, file);
    if (!absolute.startsWith(sourceRoot + path.sep)) throw Error('Source escapes frozen asset root');
    const bytes = await fs.readFile(absolute);
    const { data, info } = await sharp(bytes).ensureAlpha().raw().toBuffer({ resolveWithObject:true });
    if (info.width !== 512 || info.height !== 512 || info.channels !== 4) throw Error('Wrong source dimensions: ' + file);
    inventory.push({ file, pngSha256:sha(bytes), rgbaSha256:sha(data), pngBytes:bytes.length });
    return data;
  }
  const base = await read(baseFile), chunks = [], records = [], attachments = new Map(), encodedBlocks = new Map();
  let offset = 0;
  function add(data) {
    const key = sha(data);
    if (attachments.has(key)) return attachments.get(key);
    const encoded = zlib.deflateSync(data, { level:9 });
    const record = { offset, length:encoded.length, rawLength:data.length, sha256:key };
    chunks.push(encoded); encodedBlocks.set(offset, encoded); offset += encoded.length; attachments.set(key, record);
    return record;
  }
  const baseTexture = add(base);
  let changedPixelCount = 0, exact = 0;
  for (const file of files) {
    const rgba = file === baseFile ? base : await read(file);
    if (file === propsFile) {
      records.push({ file, kind:'texture', block:add(rgba), rgbaSha256:sha(rgba) });
      exact++; continue;
    }
    let x0=512,y0=512,x1=-1,y1=-1,changed=0;
    for(let p=0;p<512*512;p++) {
      const o=p*4;
      if (rgba[o]===base[o] && rgba[o+1]===base[o+1] && rgba[o+2]===base[o+2] && rgba[o+3]===base[o+3]) continue;
      const x=p%512,y=Math.floor(p/512);
      x0=Math.min(x0,x); y0=Math.min(y0,y); x1=Math.max(x1,x); y1=Math.max(y1,y); changed++;
    }
    changedPixelCount += changed;
    if (!changed) { records.push({file,kind:'base',rgbaSha256:sha(rgba)}); exact++; continue; }
    const width=x1-x0+1,height=y1-y0+1,patch=Buffer.alloc(width*height*4),rebuilt=Buffer.from(base);
    for(let y=0;y<height;y++) for(let x=0;x<width;x++) for(let c=0;c<4;c++) {
      const source=((y0+y)*512+x0+x)*4+c,dest=(y*width+x)*4+c;
      patch[dest]=(rgba[source]-base[source]+256)&255;
    }
    const block=add(patch), decoded=zlib.inflateSync(encodedBlocks.get(block.offset));
    for(let y=0;y<height;y++) for(let x=0;x<width;x++) for(let c=0;c<4;c++) {
      const target=((y0+y)*512+x0+x)*4+c,source=(y*width+x)*4+c;
      rebuilt[target]=(base[target]+decoded[source])&255;
    }
    if (!rebuilt.equals(rgba)) throw Error('Lossless reconstruction failed: ' + file);
    records.push({ file,kind:'expression',rect:{x:x0,y:y0,width,height},block,rgbaSha256:sha(rgba),changedPixels:changed });
    exact++;
  }
  const binary=Buffer.concat(chunks), schema={
    version:1, width:512,height:512,format:'RGBA8',compression:'zlib',archive:'work.bin',archiveSha256:sha(binary),baseTexture,
    bones:[{name:'cat-root',parent:null,animation:'Core WorkBodyOffsetY (cos(2*pi*t/4800)-1)',attachment:'baseTexture'},
      {name:'face',parent:'cat-root',attachment:'local expression residual'},
      {name:'foreground-props',parent:null,attachment:propsFile,animation:'Core WorkPropsOpacity'}],
    frames:records,
    clips:Object.fromEntries(workNames.map(name=>[name,manifest.clips[name]]))
  };
  await fs.mkdir(output,{recursive:true});
  await fs.writeFile(path.join(output,'work.bin'),binary);
  const metadata=Buffer.from(JSON.stringify(schema));
  await fs.writeFile(path.join(output,'work.json'),metadata);
  const sourceBytes=inventory.reduce((sum,item)=>sum+item.pngBytes,0);
  const report={passed:true,sourceManifestSha256:sha(manifestBytes),uniqueFrames:records.length,allFramesRgbaExact:exact===records.length,
    exactFrames:exact,changedPixelCount,originalReferencedPngBytes:sourceBytes,archiveBytes:binary.length,metadataBytes:metadata.length,
    runtimeBytes:binary.length+metadata.length,reductionPercent:100*(1-(binary.length+metadata.length)/sourceBytes),
    transformPreserved:'WorkScenePresentation continues applying the original cat root breathing and stationary prop opacity; the local face attachment is reconstructed losslessly.',
    sourceInventory:inventory};
  await fs.mkdir(path.join(repo,'reference/skeletal'),{recursive:true});
  await fs.writeFile(path.join(repo,'reference/skeletal/work-verification.json'),JSON.stringify(report,null,2)+'\n');
  console.log(JSON.stringify({...report,sourceInventory:undefined},null,2));
  return report;
}
if(require.main===module) compile(process.argv[2] || path.join(repo,'../TablePet.Legacy')).catch(error=>{console.error(error);process.exitCode=1;});
module.exports={compile};
