// Prepare eye-only animation keys; original pose and production assets stay untouched.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');
const { compositeProps } = require('./build-work-preview-v2.cjs');
const artwork = require('./work-artwork.cjs').loadWorkArtwork();
const root = path.resolve(__dirname, '..');
const asset = path.join(root, 'assets/strawberry-cat');
const output = artwork.previewRoot;
const W=512,H=512;
const eyeRegions=[{cx:164.5,cy:329,rx:53,ry:50},{cx:304,cy:344,rx:54,ry:50}];
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const smooth=t=>t*t*(3-2*t);
async function main(){
  await fs.mkdir(path.join(output,'keys'),{recursive:true});
  const files={base:'frames/fall_asleep-00.png',open:artwork.sourceFile,closed:'frames/blink-03.png',props:'work-preview-v2/props.png'};
  const raws={},inventory=[];
  for(const [name,file] of Object.entries(files)){
    const bytes=await fs.readFile(path.join(asset,file));
    inventory.push({name,file:'assets/strawberry-cat/'+file,sha256:hash(bytes)});
    raws[name]=await sharp(bytes).resize(W,H,{fit:'fill',kernel:'lanczos3'}).ensureAlpha().raw().toBuffer();
  }
  const mask=Buffer.alloc(W*H*4);
  for(let y=0;y<H;y++)for(let x=0;x<W;x++){
    let weight=0;
    for(const e of eyeRegions){
      const r=Math.hypot((x-e.cx)/e.rx,(y-e.cy)/e.ry);
      weight=Math.max(weight,r<=.9?1:r>=1?0:smooth((1-r)/.1));
    }
    const p=(y*W+x)*4;mask[p]=mask[p+1]=mask[p+2]=255;mask[p+3]=Math.round(weight*255);
  }
  await sharp(mask,{raw:{width:W,height:H,channels:4}}).png().toFile(path.join(output,'keys/eye-mask.png'));
  const keys={};
  for(const name of ['open','closed']){
    const raw=Buffer.from(raws.base);
    for(let p=0;p<raw.length;p+=4){
      const w=mask[p+3]/255;if(!w)continue;
      // All changed pixels are inside the opaque face; retain the original alpha.
      for(let c=0;c<3;c++)raw[p+c]=Math.round(raws.base[p+c]*(1-w)+raws[name][p+c]*w);
    }
    keys[name]=raw;
    await sharp(raw,{raw:{width:W,height:H,channels:4}}).png().toFile(path.join(output,'keys',name+'.png'));
  }
  await fs.copyFile(path.join(asset,files.props),path.join(output,'props.png'));
  const master=compositeProps(keys.open,raws.props);
  await sharp(master,{raw:{width:W,height:H,channels:4}}).png().toFile(path.join(output,'master.png'));
  const panels=[];
  for(const [i,raw] of [raws.base,keys.open,keys.closed].entries()){
    const tile=await sharp(compositeProps(raw,raws.props),{raw:{width:W,height:H,channels:4}}).flatten({background:'#f3efe8'}).png().toBuffer();
    panels.push({input:tile,left:i*W,top:0});
  }
  await sharp({create:{width:W*3,height:H,channels:4,background:'#f3efe8'}}).composite(panels).png().toFile(path.join(output,'eyes-comparison.png'));
  let exactOutsideMask=true;
  for(let p=0;p<mask.length;p+=4)if(!mask[p+3])for(const name of ['open','closed'])if(!keys[name].subarray(p,p+4).equals(raws.base.subarray(p,p+4)))exactOutsideMask=false;
  const report={date:'2026-11-09',sourceTool:'built-in image_gen',canvas:{width:W,height:H},eyeRegions,featherNormalizedRange:[.9,1],sourceInventory:inventory,exactOutsideMask,alphaPreserved:true,keys:Object.fromEntries(Object.entries(keys).map(([name,raw])=>[name,{rgbaSha256:hash(raw)}]))};
  await fs.mkdir(artwork.artworkReference,{recursive:true});
  await fs.writeFile(path.join(artwork.artworkReference,'keys.json'),JSON.stringify(report,null,2)+'\n');
  console.log(JSON.stringify({exactOutsideMask,output},null,2));
}
if(require.main===module)main().catch(e=>{console.error(e);process.exitCode=1});
