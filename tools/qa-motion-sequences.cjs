// Contact sheets at fixed real-time intervals for inspecting interpolation artifacts.
const fs=require('node:fs/promises');
const path=require('node:path');
const sharp=require('sharp');
const root=path.resolve('assets/strawberry-cat'),out=path.resolve('artifacts/motion-qa/v2');
const sets=[['rise',0,50],['settle',0,65],['fall_asleep',0,110],['sleep_loop',0,240],['sleep_loop_peak',1200,80],['sleep_loop_seam',3320,40]];
(async()=>{
  await fs.mkdir(out,{recursive:true});
  const m=JSON.parse(await fs.readFile(path.join(root,'manifest.json'),'utf8'));
  const selection={};
  for(const [name,start,step] of sets){
    const clip=m.clips[name.startsWith('sleep_loop')?'sleep_loop':name];
    const duration=clip.frames.reduce((s,f)=>s+f.durationMs,0);
    const selected=[],layers=[];
    for(let i=0;i<16;i++){
      const ms=start+i*step,pos=clip.loop?ms%duration:Math.min(ms,duration-1);
      let index=0,end=clip.frames[0].durationMs;
      while(index<clip.frames.length-1&&pos>=end){index++;end+=clip.frames[index].durationMs;}
      const file=clip.frames[index].file,background=(i%4<2)?'#f9f2ee':'#26313f',color=(i%4<2)?'#4a3942':'#ffffff';
      layers.push({input:await sharp(path.join(root,file)).resize(256,256).flatten({background}).png().toBuffer(),left:(i%4)*256,top:Math.floor(i/4)*284+28});
      layers.push({input:Buffer.from(`<svg width="256" height="28"><rect width="100%" height="100%" fill="${background}"/><text x="7" y="20" font-family="sans-serif" font-size="15" fill="${color}">${ms} ms · frame ${index}</text></svg>`),left:(i%4)*256,top:Math.floor(i/4)*284});
      selected.push({ms,index,file});
    }
    selection[name]=selected;
    await sharp({create:{width:1024,height:1136,channels:4,background:'#ffffff'}}).composite(layers).png().toFile(path.join(out,name+'-16.png'));
  }
  await fs.writeFile(path.join(out,'sequence-selection.json'),JSON.stringify(selection,null,2)+'\n');
  console.log('Wrote six 16-frame inspection sheets.');
})().catch(e=>{console.error(e);process.exitCode=1;});
