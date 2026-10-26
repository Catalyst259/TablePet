// Split model-generated sprites, keep one shared scale and align the ground/nose anchor.
// This script does not draw or synthesize animation poses.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');
const root = path.resolve(__dirname, '..', 'assets', 'strawberry-cat');
const names = ['blink', 'fall_asleep', 'sleep_loop', 'wake', 'rise', 'settle'];
const size = 512, baseline = 478, bodyX = 232;
// Correct each independently generated sheet's overall scale once, not individual frames.
const sheetScale = { blink: 0.88, fall_asleep: 0.88, sleep_loop: 0.88, wake: 0.88, rise: 0.9152, settle: 0.88 };

function components(data, width, height, accept) {
  const labels = new Int32Array(width * height), queue = new Int32Array(width * height);
  const found = [];
  let id = 0;
  for (let p = 0; p < labels.length; p++) {
    if (labels[p] || !accept(p)) continue;
    id++;
    let head = 0, tail = 1, minX = width, minY = height, maxX = 0, maxY = 0;
    queue[0] = p; labels[p] = id;
    while (head < tail) {
      const q = queue[head++], x = q % width, y = Math.floor(q / width);
      minX = Math.min(minX, x); minY = Math.min(minY, y); maxX = Math.max(maxX, x); maxY = Math.max(maxY, y);
      for (const n of [x ? q - 1 : -1, x < width - 1 ? q + 1 : -1, y ? q - width : -1, y < height - 1 ? q + width : -1]) {
        if (n >= 0 && !labels[n] && accept(n)) { labels[n] = id; queue[tail++] = n; }
      }
    }
    found.push({id, count:tail, x:minX, y:minY, width:maxX-minX+1, height:maxY-minY+1});
  }
  return {labels, found};
}

async function split(name) {
  const sourceName = name === 'blink' ? 'blink-v2.png' : name+'.png';
  const scale = sheetScale[name];
  const {data, info} = await sharp(path.join(root,'sources',sourceName)).ensureAlpha().raw().toBuffer({resolveWithObject:true});
  const {width,height} = info;
  const valley=(a,b,count)=>{let best=a,min=Infinity;for(let v=a;v<=b;v++){const n=count(v);if(n<min){best=v;min=n;}}return best;};
  const rowCut=valley(440,590,y=>{let n=0;for(let x=0;x<width;x++)if(data[(y*width+x)*4+3]>=64)n++;return n;});
  const ordered=[];
  for(const [y0,y1] of [[0,rowCut],[rowCut,height]]) {
    const count=x=>{let n=0;for(let y=y0;y<y1;y++)if(data[(y*width+x)*4+3]>=64)n++;return n;};
    const x1=valley(490,535,count),x2=valley(995,1045,count);
    for(const [x0,xEnd] of [[0,x1],[x1,x2],[x2,width]]) {
      const rw=xEnd-x0,rh=y1-y0;
      const alphaFloor = name === 'blink' ? 192 : 16;
      const c=components(data,rw,rh,p=>data[((Math.floor(p/rw)+y0)*width+(p%rw)+x0)*4+3]>=alphaFloor);
      const box=c.found.sort((a,b)=>b.count-a.count)[0];
      if (name === 'blink') {
        // The regenerated blink sheet has a low-alpha ground shadow. Keep the opaque
        // silhouette plus a one-pixel antialias fringe, not the connected shadow.
        const originalRight=box.x+box.width, originalBottom=box.y+box.height;
        box.x=Math.max(0,box.x-1); box.y=Math.max(0,box.y-1);
        box.width=Math.min(rw,originalRight+1)-box.x;
        box.height=Math.min(rh,originalBottom+1)-box.y;
      }
      ordered.push({...box,x:box.x+x0,y:box.y+y0,labels:c.labels,region:{x:x0,y:y0,width:rw,height:rh}});
    }
  }
  const report=[];
  for(let i=0;i<ordered.length;i++) {
    const box=ordered[i], clean=Buffer.alloc(box.width*box.height*4);
    for(let y=0;y<box.height;y++) for(let x=0;x<box.width;x++) {
      const p=(box.y+y)*width+box.x+x, out=(y*box.width+x)*4;
      const local=(box.y+y-box.region.y)*box.region.width+box.x+x-box.region.x;
      if(box.labels[local]!==box.id) {
        if(name !== 'blink') continue;
        const w=box.region.width, h=box.region.height, lx=local%w, ly=Math.floor(local/w);
        if(![lx?local-1:-1,lx<w-1?local+1:-1,ly?local-w:-1,ly<h-1?local+w:-1].some(p=>p>=0&&box.labels[p]===box.id)) continue;
      }
      data.copy(clean,out,p*4,p*4+4);
      // Remove barely visible fringe and restore near-opaque interiors from model alpha.
      clean[out+3] = data[p*4+3] >= 245 ? 255 : data[p*4+3];
    }
    const pink = components(clean,box.width,box.height,p=>{
      const o=p*4,r=clean[o],g=clean[o+1],b=clean[o+2];
      return clean[o+3]>128 && r>210 && g<220 && r-g>25 && b-g<75 && b>140;
    }).found.filter(c=>c.count>20 && c.count<1800 && (c.x+c.width/2)>box.width*.30 && (c.x+c.width/2)<box.width*.60 && c.y>box.height*.3 && c.y<box.height*.85);
    const nose=pink.sort((a,b)=>Math.abs((a.x+a.width/2)/box.width-.43)-Math.abs((b.x+b.width/2)/box.width-.43))[0];
    if(!nose) throw new Error(`${name}:${i} could not locate nose anchor`);
    const anchor=nose.x+nose.width/2;
    const resized=await sharp(clean,{raw:{width:box.width,height:box.height,channels:4}}).resize({width:Math.round(box.width*scale),height:Math.round(box.height*scale)}).png().toBuffer();
    const left=Math.round(bodyX-anchor*scale), top=baseline-Math.round(box.height*scale);
    if(left<0 || top<0 || left+Math.round(box.width*scale)>size) throw new Error(`${name}:${i} exceeds canvas`);
    const file=`${name}-${String(i).padStart(2,'0')}.png`;
    await sharp({create:{width:size,height:size,channels:4,background:'#00000000'}}).composite([{input:resized,left,top}]).png().toFile(path.join(root,'frames',file));
    const {labels,...source}=box;
    report.push({frame:i,source,anchorX:anchor,placed:{left,top},nose});
  }
  return report;
}

const frame = (name, index, durationMs) => ({ file: `frames/${name}-${String(index).padStart(2,'0')}.png`, durationMs });

async function packageAssets() {
  const idle = frame('fall_asleep',0,1000);
  const sleep = frame('sleep_loop',0,450);
  const rise = [ {...idle,durationMs:80}, frame('rise',1,100), frame('rise',2,110), frame('rise',3,120), frame('rise',4,120), frame('rise',5,150) ];
  const falling = [ {...idle,durationMs:120}, frame('fall_asleep',1,170), frame('fall_asleep',2,180), frame('fall_asleep',3,190), frame('fall_asleep',4,240), {...sleep,durationMs:240} ];
  const manifest = {
    id:'strawberry-cat', version:1, displayName:'草莓猫',
    canvas:{width:size,height:size}, anchor:{x:bodyX,y:baseline},
    hitRegion:{x:64,y:240,width:416,height:238},
    clips:{
      idle:{loop:true,frames:[idle]},
      blink:{loop:false,frames:[{...idle,durationMs:50},frame('blink',1,60),frame('blink',2,45),frame('blink',3,90),frame('blink',4,60),{...idle,durationMs:70}]},
      fall_asleep:{loop:false,frames:falling},
      sleep_loop:{loop:true,frames:[sleep,frame('sleep_loop',1,500),frame('sleep_loop',2,500),frame('sleep_loop',3,550),frame('sleep_loop',2,450),frame('sleep_loop',1,500),sleep]},
      wake:{loop:false,frames:[...falling].reverse().map((f,i)=>({...f,durationMs:[160,160,140,120,100,100][i]}))},
      rise:{loop:false,frames:rise},
      hold:{loop:true,frames:[{...rise.at(-1),durationMs:1000}]},
      settle:{loop:false,frames:[...rise].reverse().map((f,i)=>({...f,durationMs:[120,120,130,130,130,120][i]}))}
    }
  };
  await fs.writeFile(path.join(root,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');
  await fs.mkdir(path.join(root,'previews'),{recursive:true});
  // Reuse decoded source art for previews, never synthesize intermediate poses.
  const thumb=async(file,background='#F9F2EE')=>sharp(path.join(root,file)).resize(256,256).flatten({background}).png().toBuffer();
  const labels=['待机','睡眠','半起身'];
  const poseFiles=[idle.file,sleep.file,rise.at(-1).file];
  const layers=[];
  for(let i=0;i<poseFiles.length;i++) {
    layers.push({input:await thumb(poseFiles[i]),left:i*288+16,top:16});
    layers.push({input:Buffer.from(`<svg width="288" height="40"><text x="144" y="26" text-anchor="middle" font-family="Microsoft YaHei, sans-serif" font-size="20" fill="#4A3942">${labels[i]}</text></svg>`),left:i*288,top:278});
  }
  await sharp({create:{width:864,height:326,channels:4,background:'#F9F2EE'}}).composite(layers).png().toFile(path.join(root,'previews','key-poses.png'));
  const clips=Object.entries(manifest.clips); const contact=[];
  for(let row=0;row<clips.length;row++) {
    const [name,clip]=clips[row];
    for(let col=0;col<clip.frames.length;col++) {
      const background=row%2?'#293340':'#F9F2EE';
      contact.push({input:await sharp(path.join(root,clip.frames[col].file)).resize(192,192).flatten({background}).png().toBuffer(),left:col*208+8,top:row*224+24});
      const color=row%2?'#FFFFFF':'#4A3942';
      contact.push({input:Buffer.from(`<svg width="208" height="24"><text x="10" y="18" font-size="14" fill="${color}">${name} ${col} · ${clip.frames[col].durationMs}ms</text></svg>`),left:col*208,top:row*224});
    }
  }
  await sharp({create:{width:1456,height:1792,channels:4,background:'#858585'}}).composite(contact).png().toFile(path.join(root,'previews','animation-contact-sheet.png'));
  const timeline=[{...idle,durationMs:1000},...manifest.clips.blink.frames,{...idle,durationMs:600},...falling,...manifest.clips.sleep_loop.frames,...manifest.clips.wake.frames,...rise,{...rise.at(-1),durationMs:1000},...manifest.clips.settle.frames,{...idle,durationMs:800}];
  const gifFrames=[];
  for(const f of timeline) gifFrames.push(await sharp(await thumb(f.file)).ensureAlpha().raw().toBuffer());
  await sharp(Buffer.concat(gifFrames),{raw:{width:256,height:256*gifFrames.length,channels:4,pageHeight:256}}).gif({loop:0,delay:timeline.map(f=>f.durationMs),colours:256,dither:0.3}).toFile(path.join(root,'previews','animation-preview.gif'));
  // ICO directory entries containing PNG payloads are supported by modern Windows.
  const iconSizes=[16,24,32,48,64,128,256], payloads=[];
  for(const iconSize of iconSizes)payloads.push(await sharp(path.join(root,idle.file)).trim().resize(iconSize,iconSize,{fit:'contain',background:'#00000000'}).png().toBuffer());
  const header=Buffer.alloc(6+16*payloads.length);header.writeUInt16LE(1,2);header.writeUInt16LE(payloads.length,4);
  let offset=header.length;
  payloads.forEach((payload,i)=>{const at=6+i*16,s=iconSizes[i];header[at]=s===256?0:s;header[at+1]=s===256?0:s;header.writeUInt16LE(1,at+4);header.writeUInt16LE(32,at+6);header.writeUInt32LE(payload.length,at+8);header.writeUInt32LE(offset,at+12);offset+=payload.length;});
  await fs.writeFile(path.join(root,'tray.ico'),Buffer.concat([header,...payloads]));
  const qa=[];
  for(const file of [...new Set(Object.values(manifest.clips).flatMap(c=>c.frames.map(f=>f.file)))]) {
    const {data,info}=await sharp(path.join(root,file)).ensureAlpha().raw().toBuffer({resolveWithObject:true});
    let x0=size,y0=size,x1=-1,y1=-1,opaque=0,transparent=0;
    for(let y=0;y<size;y++)for(let x=0;x<size;x++){const a=data[(y*size+x)*4+3];if(a===0)transparent++;if(a>=128){opaque++;x0=Math.min(x0,x);y0=Math.min(y0,y);x1=Math.max(x1,x);y1=Math.max(y1,y);}}
    if(info.width!==size||info.height!==size||x0<=0||y0<=0||x1>=size-1||y1>=size-1)throw new Error(`Frame touches canvas edge: ${file}`);
    qa.push({file,width:info.width,height:info.height,opaquePixels:opaque,transparentPixels:transparent,bounds:{left:x0,top:y0,right:x1,bottom:y1}});
  }
  await fs.writeFile(path.join(root,'previews','asset-qa.json'),JSON.stringify({canvas:{width:size,height:size},anchor:{x:bodyX,y:baseline},selectedFrameCount:qa.length,frames:qa},null,2)+'\n');
  console.log(`Packaged 8 clips and ${qa.length} selected PNG frames, transparent canvas, ICO, contact sheet and GIF.`);
}

(async()=>{
  await fs.mkdir(path.join(root,'frames'),{recursive:true});
  const report={};
  for(const name of names)report[name]=await split(name);
  await fs.writeFile(path.join(root,'sources','alignment-report.json'),JSON.stringify(report,null,2));
  console.log('Split and aligned 36 generated frames with constant scale per sheet.');
  await packageAssets();
})().catch(e=>{console.error(e);process.exit(1);});
