const fs=require('node:fs/promises');
const path=require('node:path');
const sharp=require('sharp');
const root=path.resolve('assets/strawberry-cat'),out=path.resolve('artifacts/motion-qa/v2');
(async()=>{
  const m=JSON.parse(await fs.readFile(path.join(root,'manifest.json'),'utf8'));
  const sets=[['rise',0],['rise_reopen',64],['settle',31],['fall_asleep',52]];
  for(const [name,start] of sets){
    const clip=m.clips[name.startsWith('rise')?'rise':name],layers=[];
    for(let i=0;i<16;i++){
      const index=start+i,file=clip.frames[index].file,background=i%4<2?'#f9f2ee':'#26313f',color=i%4<2?'#4a3942':'#ffffff';
      layers.push({input:await sharp(path.join(root,file)).extract({left:50,top:240,width:420,height:200}).resize(420,200).flatten({background}).png().toBuffer(),left:i%4*420,top:Math.floor(i/4)*228+28});
      layers.push({input:Buffer.from(`<svg width="420" height="28"><rect width="100%" height="100%" fill="${background}"/><text x="7" y="20" font-size="15" fill="${color}">${name} frame ${index}</text></svg>`),left:i%4*420,top:Math.floor(i/4)*228});
    }
    await sharp({create:{width:1680,height:912,channels:4,background:'#ffffff'}}).composite(layers).png().toFile(path.join(out,name+'-hotspot.png'));
  }
})().catch(e=>{console.error(e);process.exitCode=1});
