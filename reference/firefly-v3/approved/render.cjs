'use strict';
// Standalone preview compositor. This file never writes to runtime directories.
const fs=require('node:fs/promises'),path=require('node:path'),crypto=require('node:crypto');
const sharp=require('sharp');
const {spawn,execFile}=require('node:child_process'),{once}=require('node:events'),{promisify}=require('node:util');
const {duration,fps,sample}=require('./choreography.cjs');
const {makePhaseSampler}=require('../../tools/build-wink-candidate.cjs');
const root=__dirname,repo=path.resolve(root,'../..');
const spec={width:512,height:512,channels:4};
const clamp=(x,a=0,b=1)=>Math.max(a,Math.min(b,x));
const smooth=x=>{x=clamp(x);return x*x*(3-2*x);};
const bodyPhase=m=>m.body.prepare+m.body.paw;
function blend(a,b,w){if(w<1e-8)return Buffer.from(a);const o=Buffer.alloc(a.length);for(let p=0;p<a.length;p+=4){const aa=a[p+3]*(1-w),ba=b[p+3]*w,alpha=aa+ba;o[p+3]=Math.round(alpha);for(let c=0;c<3;c++)o[p+c]=alpha?Math.round((a[p+c]*aa+b[p+c]*ba)/alpha):0;}return o;}
function translateHead(raw,dx,dy){
  if(Math.abs(dx)+Math.abs(dy)<.00001)return raw;
  const out=Buffer.from(raw);
  for(let y=80;y<435;y++)for(let x=45;x<440;x++){
    const w=(1-smooth((y-384)/50))*(1-smooth((x-408)/28)*smooth((y-320)/20));if(!w)continue;
    const sx=x-dx*w,sy=y-dy*w,x0=Math.floor(sx),y0=Math.floor(sy),u=sx-x0,v=sy-y0;
    const q=[(1-u)*(1-v),u*(1-v),(1-u)*v,u*v],p0=(y0*512+x0)*4,idx=[p0,p0+4,p0+2048,p0+2052];
    let alpha=0,r=0,g=0,b=0;
    for(let k=0;k<4;k++){const a=raw[idx[k]+3]*q[k];alpha+=a;r+=raw[idx[k]]*a;g+=raw[idx[k]+1]*a;b+=raw[idx[k]+2]*a;}
    const p=(y*512+x)*4;out[p+3]=Math.round(alpha);out[p]=alpha?Math.round(r/alpha):0;out[p+1]=alpha?Math.round(g/alpha):0;out[p+2]=alpha?Math.round(b/alpha):0;
  }return out;
}
function encoder(name,width,height){
 const proc=spawn('ffmpeg',['-hide_banner','-loglevel','error','-y','-f','rawvideo','-pixel_format','rgb24','-video_size',`${width}x${height}`,'-framerate',String(fps),'-i','pipe:0','-an','-c:v','libx264','-preset','medium','-crf','16','-pix_fmt','yuv420p','-movflags','+faststart',path.join(root,name)],{windowsHide:true});
 let error='';proc.stderr.on('data',b=>error+=b);const done=once(proc,'close');
 return {async write(b){if(!proc.stdin.write(b))await once(proc.stdin,'drain');},async finish(){proc.stdin.end();const [code]=await done;if(code)throw Error(error);}};
}
async function main(){
 const {createRig}=require('./eyes-rig.cjs');const rig=await createRig();
 const before=JSON.parse(await fs.readFile(path.join(root,'production-before.json')));
 const idle=await sharp(path.join(repo,'assets/strawberry-cat/frames/fall_asleep-00.png')).ensureAlpha().raw().toBuffer();
 const bodyRig=await require('./body.cjs').createBodyRig();
 const flySource=path.join(root,'sources/firefly.png');
 await fs.mkdir(path.join(root,'stills'),{recursive:true});
 const onlyStills=process.argv.includes('--stills');
 const full=onlyStills?null:encoder('firefly-preview.mp4',640,640),eye=onlyStills?null:encoder('eye-detail.mp4',960,512),dark=onlyStills?null:encoder('firefly-dark.mp4',640,640),pawDetail=onlyStills?null:encoder('paw-detail.mp4',920,568);
 const n=Math.round(duration*fps),timeline=[],stills=[];
 const times=[0,1.5,2.5,3.5,5.1,5.5,5.9,6.2,6.5,7.4,8.5,duration-.02];
 const stillIndices=times.map(t=>Math.min(n-1,Math.round(t*fps)));
 let maxGazeStep=0,maxHeadStep=0,prev=null,first=null,last=null;
 for(let i=0;i<n;i++){
   const t=i/fps,m=sample(t);
   if(onlyStills&&!stillIndices.includes(i))continue;
   const body=await bodyRig.render(m.body);
   let cat=body.background??body.rgba;
   const currentEyes=body.eyes;
   cat=rig.render(cat,{...m.gaze,eyes:currentEyes});
   if(bodyRig.finalize)cat=await bodyRig.finalize(cat,body);
   if(i===0)first=cat;if(i===n-1)last=cat;
   const catPng=await sharp(cat,{raw:spec}).png().toBuffer();
   const layers=[];
   if(m.fly.opacity>.0001){
     // The same generated insect sprite follows the path; its orientation
     // changes gently from approaching leftward to departing upward.
     const departure=smooth((t-6.15)/.95),rotation=-24*(1-departure)+43*departure;
     const pulse=.92+.08*Math.sin(t*Math.PI*2*.85);
     const width=Math.max(24,Math.round(92*m.fly.scale));
     let sprite=await sharp(flySource).resize({width}).rotate(rotation,{background:'#00000000'}).ensureAlpha().raw().toBuffer({resolveWithObject:true});
     const fw=sprite.info.width,fh=sprite.info.height;
     for(let q=3;q<sprite.data.length;q+=4)sprite.data[q]=Math.round(sprite.data[q]*m.fly.opacity*pulse);
     const fly={input:await sharp(sprite.data,{raw:{width:fw,height:fh,channels:4}}).png().toBuffer(),left:Math.round(m.fly.x-fw*.5),top:Math.round(m.fly.y-fh*.5)};
     // Keep partial sprites at the canvas edge without a compositor bounds error.
     const left=Math.max(0,fly.left),top=Math.max(0,fly.top),right=Math.min(640,fly.left+fw),bottom=Math.min(640,fly.top+fh);
     if(right>left&&bottom>top){fly.input=await sharp(fly.input).extract({left:left-fly.left,top:top-fly.top,width:right-left,height:bottom-top}).png().toBuffer();fly.left=left;fly.top=top;if(m.fly.behind)layers.push(fly);else layers.front=fly;}
   }
   layers.push({input:catPng,left:64,top:100});if(layers.front)layers.push(layers.front);
   const scene=await sharp({create:{width:640,height:640,channels:4,background:'#00000000'}}).composite(layers).png().toBuffer();
   if(!onlyStills){
     const light=await sharp(scene).flatten({background:'#f5f0e9'}).removeAlpha().raw().toBuffer();
     const night=await sharp(scene).flatten({background:'#34313e'}).removeAlpha().raw().toBuffer();
     const detail=await sharp(cat,{raw:spec}).extract({left:112,top:264,width:240,height:128}).resize(960,512,{kernel:'lanczos3'}).flatten({background:'#f5f0e9'}).removeAlpha().raw().toBuffer();
     const pawCrop=await sharp(cat,{raw:spec}).extract({left:72,top:350,width:230,height:142}).resize(920,568,{kernel:'lanczos3'}).flatten({background:'#f5f0e9'}).removeAlpha().raw().toBuffer();
     await Promise.all([full.write(light),dark.write(night),eye.write(detail),pawDetail.write(pawCrop)]);
   }
   if(stillIndices.includes(i)){
     const file=path.join(root,'stills',String(i).padStart(5,'0')+'.png');
     await sharp(scene).flatten({background:'#f5f0e9'}).png().toFile(file);
     await sharp(cat,{raw:spec}).extract({left:112,top:264,width:240,height:128}).resize(720,384).png().toFile(path.join(root,'stills','eyes-'+String(i).padStart(5,'0')+'.png'));
     stills.push({t,file,scene});
   }
   if(prev){maxGazeStep=Math.max(maxGazeStep,Math.hypot(m.gaze.u-prev.gaze.u,m.gaze.v-prev.gaze.v));maxHeadStep=Math.max(maxHeadStep,Math.abs(bodyPhase(m)-bodyPhase(prev)));}prev=m;
   timeline.push({frame:i,t,...m});
   if(i%120===0)console.log('Rendered '+i+'/'+n);
 }
 if(!onlyStills)await Promise.all([full.finish(),eye.finish(),dark.finish(),pawDetail.finish()]);
 const tiles=[];for(let k=0;k<stills.length;k++){
   const x=k%4*320,y=Math.floor(k/4)*348;
   tiles.push({input:await sharp(stills[k].scene).resize(320,320).png().toBuffer(),left:x,top:y});
   tiles.push({input:Buffer.from(`<svg width="320" height="28"><text x="160" y="19" text-anchor="middle" font-family="Arial" font-size="14" fill="#705c54">${stills[k].t.toFixed(2)} s</text></svg>`),left:x,top:y+320});
 }
 await sharp({create:{width:1280,height:Math.ceil(stills.length/4)*348,channels:4,background:'#f5f0e9'}}).composite(tiles).png().toFile(path.join(root,'contact-sheet.png'));
 if(!onlyStills){
   await promisify(execFile)('ffmpeg',['-hide_banner','-loglevel','error','-y','-i',path.join(root,'firefly-preview.mp4'),'-filter_complex','fps=30,scale=480:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a','-loop','0',path.join(root,'firefly-preview.gif')],{windowsHide:true});
   await fs.writeFile(path.join(root,'timeline.json'),JSON.stringify({duration,fps,frames:timeline},null,2));
 }
 const changed=[];for(const [f,h]of Object.entries(before)){const now=crypto.createHash('sha256').update(await fs.readFile(path.join(repo,f))).digest('hex');if(now!==h)changed.push(f);}
 const qa={candidateOnly:true,version:3,durationSeconds:duration,fps,frames:onlyStills?stills.length:n,eyeMethod:'Common normalized binocular direction; fixed pupil dimensions, up reference calibration, clipped inside eye contours.',maxRawDirectionStepIncludingDisengagedChanges:maxGazeStep,maxBodyPhaseStep:maxHeadStep,paw:'screen-left',bodyMethod:'Original full-size paw continuously transformed from idle position',firstFrameExactIdle:first?first.equals(idle):null,lastFrameExactIdle:last?last.equals(idle):null,protectedFileCount:Object.keys(before).length,changedProductionFiles:changed,stillsOnly:onlyStills};
 await fs.writeFile(path.join(root,onlyStills?'stills-qa.json':'verification.json'),JSON.stringify(qa,null,2));
 if(changed.length)throw Error('Protected runtime changed: '+changed.join(','));console.log(JSON.stringify(qa,null,2));
}
main().catch(e=>{console.error(e);process.exitCode=1;});
