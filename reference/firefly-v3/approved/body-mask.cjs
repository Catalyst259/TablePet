const fs=require('node:fs/promises'),path=require('node:path'),sharp=require('sharp');
const root=__dirname,repo=path.resolve(root,'../..');
const pawPath='M 130 420 C 137 408 149 401 163 403 C 179 403 192 410 199 423 C 205 434 208 449 201 462 C 195 472 178 478 160 478 C 144 478 129 476 121 469 C 111 461 111 449 116 437 C 119 429 124 423 130 420 Z';
async function buildMask(){
  await fs.mkdir(path.join(root,'body'),{recursive:true});
  const base=await sharp(path.join(repo,'assets/strawberry-cat/frames/fall_asleep-00.png')).ensureAlpha().raw().toBuffer();
  const svg=Buffer.from(`<svg width="512" height="512" viewBox="0 0 512 512"><path d="${pawPath}" fill="white"/></svg>`);
  const mask=await sharp(svg,{density:288}).resize(512,512).ensureAlpha().raw().toBuffer();
  const paw=Buffer.from(base);for(let p=0;p<paw.length;p+=4){paw[p+3]=Math.round(base[p+3]*mask[p+3]/255);if(!paw[p+3])paw.fill(0,p,p+4);}
  await sharp(paw,{raw:{width:512,height:512,channels:4}}).png().toFile(path.join(root,'body/paw-original.png'));
  await sharp(mask,{raw:{width:512,height:512,channels:4}}).png().toFile(path.join(root,'body/paw-mask.png'));
  const overlay=await sharp(base,{raw:{width:512,height:512,channels:4}}).composite([{input:Buffer.from(`<svg width="512" height="512"><path d="${pawPath}" fill="#23c9b3" fill-opacity=".28" stroke="#d13885" stroke-width=".8"/></svg>`)}]).png().toBuffer();
  const layers=[];
  for(let i=0;i<3;i++){
    const input=i===0?overlay:i===1?await sharp(paw,{raw:{width:512,height:512,channels:4}}).png().toBuffer():await sharp(base,{raw:{width:512,height:512,channels:4}}).png().toBuffer();
    layers.push({input:await sharp(input).extract({left:100,top:392,width:118,height:94}).resize(590,470).png().toBuffer(),left:i*590,top:38});
    layers.push({input:Buffer.from(`<svg width="590" height="38"><text x="14" y="27" font-family="Arial" font-size="20" fill="#465640">${['Original paw mask','Extracted original pixels / no resize','Original comparison'][i]}</text></svg>`),left:i*590,top:0});
  }
  await sharp({create:{width:1770,height:508,channels:4,background:'#edf0e9'}}).composite(layers).png().toFile(path.join(root,'body-mask-contact.png'));
  return {base,paw,mask,pawPath};
}
module.exports={buildMask,pawPath};
if(require.main===module)buildMask().catch(e=>{console.error(e);process.exitCode=1;});
