// Read-only animation inspection. Sharp decodes and lays out existing PNGs;
// this checker does not make or alter animation poses.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');

const assetRoot = path.resolve(process.argv[2] || 'assets/strawberry-cat');
const reportRoot = path.resolve(process.argv[3] || 'artifacts/motion-qa/current');
const baselineFile = process.argv[4];
const checkSize = 128;
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const xml = value => String(value).replace(/[<>&"]/g, c => ({'<':'&lt;', '>':'&gt;', '&':'&amp;', '"':'&quot;'}[c]));

async function main() {
  const manifest = JSON.parse(await fs.readFile(path.join(assetRoot, 'manifest.json'), 'utf8'));
  const files = [...new Set(Object.values(manifest.clips).flatMap(clip => clip.frames.map(frame => frame.file)))];
  const checks = [], images = new Map();
  for (const file of files) {
    const encoded = await fs.readFile(path.join(assetRoot, file));
    const {data, info} = await sharp(encoded).ensureAlpha().raw().toBuffer({resolveWithObject:true});
    let x0=info.width, y0=info.height, x1=-1, y1=-1, opaque=0, transparent=0;
    for(let y=0;y<info.height;y++) for(let x=0;x<info.width;x++) {
      const a=data[(y*info.width+x)*4+3];
      if(a===0) transparent++;
      if(a>=128) {opaque++; x0=Math.min(x0,x);y0=Math.min(y0,y);x1=Math.max(x1,x);y1=Math.max(y1,y);}
    }
    const small = await sharp(encoded).ensureAlpha().resize(checkSize,checkSize).raw().toBuffer();
    // Work in premultiplied RGBA so invisible RGB cannot inflate movement scores.
    for(let i=0;i<small.length;i+=4) for(let c=0;c<3;c++) small[i+c]=Math.round(small[i+c]*small[i+3]/255);
    const result={file,encodedSha256:hash(encoded),pixelSha256:hash(data),width:info.width,height:info.height,
      opaquePixels:opaque,transparentPixels:transparent,bounds:{left:x0,top:y0,right:x1,bottom:y1}};
    checks.push(result); images.set(file,{...result,small});
    if(info.width!==manifest.canvas.width || info.height!==manifest.canvas.height || opaque===0 || transparent===0 || x0<=0 || y0<=0 || x1>=info.width-1 || y1>=info.height-1)
      throw new Error(`Invalid canvas, missing alpha/subject or clipped silhouette: ${file}`);
  }
  function difference(a,b) {
    const first=images.get(a),second=images.get(b);
    let total=0,visible=0,alpha=0;
    for(let i=0;i<first.small.length;i+=4) {
      if(first.small[i+3]<8 && second.small[i+3]<8) continue;
      visible++;
      for(let c=0;c<4;c++) total+=Math.abs(first.small[i+c]-second.small[i+c]);
      alpha+=Math.abs(first.small[i+3]-second.small[i+3]);
    }
    return {equalPixels:first.pixelSha256===second.pixelSha256,
      visibleMeanDifference:Number((total/Math.max(1,visible)/4).toFixed(3)),
      alphaMeanDifference:Number((alpha/Math.max(1,visible)).toFixed(3))};
  }
  const clips={};
  for(const [name,clip] of Object.entries(manifest.clips)) {
    const pairs=[];
    for(let i=1;i<clip.frames.length;i++) pairs.push({from:i-1,to:i,...difference(clip.frames[i-1].file,clip.frames[i].file)});
    if(clip.loop) pairs.push({from:clip.frames.length-1,to:0,loop:true,...difference(clip.frames.at(-1).file,clip.frames[0].file)});
    clips[name]={frameSlots:clip.frames.length,uniqueImages:new Set(clip.frames.map(f=>images.get(f.file).pixelSha256)).size,
      durationMs:clip.frames.reduce((sum,f)=>sum+f.durationMs,0),
      minFrameMs:Math.min(...clip.frames.map(f=>f.durationMs)),maxFrameMs:Math.max(...clip.frames.map(f=>f.durationMs)),
      bottomRange:[Math.min(...clip.frames.map(f=>images.get(f.file).bounds.bottom)),Math.max(...clip.frames.map(f=>images.get(f.file).bounds.bottom))],
      identicalAdjacentFrames:pairs.filter(pair=>pair.equalPixels).length,
      largestChanges:[...pairs].sort((a,b)=>b.visibleMeanDifference-a.visibleMeanDifference).slice(0,10),pairs};
  }
  const first=name=>manifest.clips[name].frames[0].file;
  const last=name=>manifest.clips[name].frames.at(-1).file;
  const boundaries=[
    ['idle → blink',first('idle'),first('blink')],['blink → idle',last('blink'),first('idle')],
    ['idle → fall_asleep',first('idle'),first('fall_asleep')],['fall_asleep → sleep_loop',last('fall_asleep'),first('sleep_loop')],
    ['sleep_loop → wake',first('sleep_loop'),first('wake')],['wake → rise',last('wake'),first('rise')],
    ['idle → rise',first('idle'),first('rise')],['rise → hold',last('rise'),first('hold')],
    ['hold → settle',first('hold'),first('settle')],['settle → idle',last('settle'),first('idle')]
  ].map(([name,a,b])=>({name,a,b,...difference(a,b)}));
  let blinkPreserved;
  if(baselineFile) {
    const baseline=JSON.parse(await fs.readFile(baselineFile,'utf8'));
    const old=baseline.manifest.clips.blink.frames;
    blinkPreserved=JSON.stringify(old)===JSON.stringify(manifest.clips.blink.frames) && old.every(frame=>
      baseline.frames.find(f=>f.file===frame.file)?.encodedSha256===images.get(frame.file).encodedSha256);
    if(!blinkPreserved) throw new Error('Approved blink file bytes, order or duration changed.');
  }
  await fs.mkdir(reportRoot,{recursive:true});
  const sheet=[];const columns=8,cell=184,rowHeight=214;
  const clipEntries=Object.entries(manifest.clips);
  for(let row=0;row<clipEntries.length;row++) {
    const [name,clip]=clipEntries[row];
    for(let col=0;col<Math.min(columns,clip.frames.length);col++) {
      const index=clip.frames.length<=columns?col:Math.round(col*(clip.frames.length-1)/(columns-1));
      const background=row%2?'#26313f':'#f8f2ed';
      const color=row%2?'#ffffff':'#413437';
      const png=await sharp(path.join(assetRoot,clip.frames[index].file)).resize(cell,cell).flatten({background}).png().toBuffer();
      sheet.push({input:png,left:col*cell,top:row*rowHeight+26});
      sheet.push({input:Buffer.from(`<svg width="${cell}" height="26"><rect width="100%" height="100%" fill="${background}"/><text x="6" y="18" font-size="13" fill="${color}">${xml(name)} · ${index}</text></svg>`),left:col*cell,top:row*rowHeight});
    }
  }
  await sharp({create:{width:columns*cell,height:rowHeight*clipEntries.length,channels:4,background:'#929292'}}).composite(sheet).png().toFile(path.join(reportRoot,'contact-sheet.png'));
  const report={checkedAt:new Date().toISOString(),assetRoot,manifest,uniqueFiles:files.length,frames:checks,clips,boundaries,blinkPreserved,
    note:'Pixel metrics flag discontinuities for visual review; low differences do not establish natural movement. Contact sheet samples the supplied frame order.'};
  await fs.writeFile(path.join(reportRoot,'report.json'),JSON.stringify(report,null,2)+'\n');
  console.log(JSON.stringify({report:path.join(reportRoot,'report.json'),uniqueFiles:files.length,boundariesPass:boundaries.every(b=>b.equalPixels),blinkPreserved,
    clips:Object.fromEntries(Object.entries(clips).map(([name,c])=>[name,{frames:c.frameSlots,durationMs:c.durationMs,largestChange:c.largestChanges[0],bottomRange:c.bottomRange}]))},null,2));
  if(boundaries.some(b=>!b.equalPixels)) process.exitCode=2;
}
main().catch(error=>{console.error(error);process.exitCode=1;});
