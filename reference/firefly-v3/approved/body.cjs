'use strict';
// Original-paw rig for the isolated preview. The paw is never regenerated,
// dissolved, or scaled down. The generated underbody fills only its old hole.
const fs=require('node:fs/promises'),path=require('node:path'),sharp=require('sharp');
const {buildMask}=require('./body-mask.cjs');
const root=__dirname,repo=path.resolve(root,'../..'),spec={width:512,height:512,channels:4};
const eyeDefaults=[{cx:164.5437392283,cy:328.7822235076,rx:42.746102033,ry:40.6430409317,rotation:-.7577536},{cx:304.0543211135,cy:343.8565418625,rx:43.88056064,ry:40.9359499266,rotation:-.248492869}];
const clamp=x=>Math.max(0,Math.min(1,x)),smooth=x=>{x=clamp(x);return x*x*(3-2*x);};
function pixel(raw,x,y,out,p){
  if(x<0||y<0||x>=511||y>=511)return;
  const ix=Math.floor(x),iy=Math.floor(y),u=x-ix,v=y-iy,idx=[(iy*512+ix)*4,(iy*512+ix+1)*4,((iy+1)*512+ix)*4,((iy+1)*512+ix+1)*4],w=[(1-u)*(1-v),u*(1-v),(1-u)*v,u*v];
  let a=0,r=0,g=0,b=0;for(let k=0;k<4;k++){const q=idx[k],wa=w[k]*raw[q+3];a+=wa;r+=wa*raw[q];g+=wa*raw[q+1];b+=wa*raw[q+2];}
  out[p+3]=Math.round(a);if(a>.001){out[p]=Math.round(r/a);out[p+1]=Math.round(g/a);out[p+2]=Math.round(b/a);}
}
function mixAt(out,p,a,b,w){
  if(w===0){a.copy(out,p,p,p+4);return;}if(w===1){b.copy(out,p,p,p+4);return;}
  const aa=a[p+3]*(1-w),ba=b[p+3]*w,alpha=aa+ba;out[p+3]=Math.round(alpha);if(alpha>.001)for(let c=0;c<3;c++)out[p+c]=Math.round((a[p+c]*aa+b[p+c]*ba)/alpha);
}
function compositeForeground(background,foreground){
  if(!foreground)return Buffer.from(background);
  const out=Buffer.from(background);
  for(let p=0;p<out.length;p+=4){const a=foreground[p+3]/255;if(a===0)continue;const b=out[p+3]/255,alpha=a+b*(1-a);for(let c=0;c<3;c++)out[p+c]=Math.round((foreground[p+c]*a+out[p+c]*b*(1-a))/alpha);out[p+3]=Math.round(alpha*255);}
  return out;
}
function headTransform(prepare){return {theta:-.028*prepare,dx:-2.5*prepare,dy:-4*prepare,pivot:{x:232,y:400}};}
function pointTransform(x,y,t){const c=Math.cos(t.theta),s=Math.sin(t.theta),px=x-t.pivot.x,py=y-t.pivot.y;return {x:t.pivot.x+c*px-s*py+t.dx,y:t.pivot.y+s*px+c*py+t.dy};}
function bodyWarp(raw,prepare){
  if(prepare===0)return Buffer.from(raw);
  const out=Buffer.alloc(raw.length),t=headTransform(prepare);
  for(let y=0;y<512;y++)for(let x=0;x<512;x++){
    const p=(y*512+x)*4,w=(1-smooth((y-385)/54))*(1-smooth((x-401)/28));
    if(w===0){raw.copy(out,p,p,p+4);continue;}
    const angle=t.theta*w,c=Math.cos(angle),s=Math.sin(angle),dx=x-t.pivot.x-t.dx*w,dy=y-t.pivot.y-t.dy*w;
    pixel(raw,t.pivot.x+c*dx+s*dy,t.pivot.y-s*dx+c*dy,out,p);
  }return out;
}
function pawTransform(value,variant='b'){
  const p=clamp(value);if(variant==='b')return {center:{x:160-55*p-3*Math.sin(Math.PI*p),y:440-50*p-3*Math.sin(Math.PI*p)},theta:Math.PI/3*p,scale:1};
  if(variant==='c')return {center:{x:160-55*p-3*Math.sin(Math.PI*p),y:440-50*p-3*Math.sin(Math.PI*p)},theta:42*Math.PI/180*p,scale:1};
  return {center:{x:160-40*p-5*Math.sin(Math.PI*p),y:440-54*p-3*Math.sin(Math.PI*p)},theta:-Math.PI/10*p,scale:1};
}
function transformPaw(raw,value,variant){
  const t=pawTransform(value,variant),out=Buffer.alloc(raw.length),c=Math.cos(t.theta),s=Math.sin(t.theta);let x0=512,y0=512,x1=-1,y1=-1;
  for(let y=315;y<490;y++)for(let x=55;x<225;x++){
    const p=(y*512+x)*4,dx=x-t.center.x,dy=y-t.center.y;
    pixel(raw,160+c*dx+s*dy,440-s*dx+c*dy,out,p);
    if(out[p+3]>127){x0=Math.min(x0,x);y0=Math.min(y0,y);x1=Math.max(x1,x);y1=Math.max(y1,y);}
  }
  return {raw:out,transform:t,bounds:{x0,y0,x1,y1},leadingUpperLeft:{x:t.center.x+c*(-30)-s*(-20),y:t.center.y+s*(-30)+c*(-20)}};
}
async function svgMask(d){return sharp(Buffer.from(`<svg width="512" height="512"><path d="${d}" fill="white"/></svg>`),{density:288}).resize(512,512).ensureAlpha().raw().toBuffer();}
function armWarp(raw,paw,amount){
  const shoulder={x:170,y:425},c=Math.cos(paw.theta),s=Math.sin(paw.theta),wrist={x:paw.center.x-s*(-35),y:paw.center.y+c*(-35)};
  const sourceShoulder={x:171,y:426},sourceWrist={x:128,y:384},sdx=sourceWrist.x-sourceShoulder.x,sdy=sourceWrist.y-sourceShoulder.y,sl=Math.hypot(sdx,sdy),tx=wrist.x-shoulder.x,ty=wrist.y-shoulder.y,tl=Math.max(8,Math.hypot(tx,ty)),sc=sdx/sl,ss=sdy/sl,tc=tx/tl,ts=ty/tl;
  const out=Buffer.alloc(raw.length),widthScale=.94+.06*amount;
  const exposed=smooth((tl-22)/12);if(exposed===0)return out;
  for(let y=345;y<479;y++)for(let x=65;x<218;x++){
    const dx=x-shoulder.x,dy=y-shoulder.y,along=(dx*tc+dy*ts)*sl/tl,across=(-dx*ts+dy*tc)/widthScale;
    const p=(y*512+x)*4;pixel(raw,sourceShoulder.x+sc*along-ss*across,sourceShoulder.y+ss*along+sc*across,out,p);out[p+3]=Math.round(out[p+3]*exposed);
  }return out;
}
function nose(raw){let sx=0,sy=0,w=0;for(let y=310;y<365;y++)for(let x=219;x<244;x++){const p=(y*512+x)*4;if(raw[p]>200&&raw[p]-raw[p+1]>30&&raw[p]-raw[p+2]>5&&raw[p+1]>90){const a=raw[p]-raw[p+1];sx+=x*a;sy+=y*a;w+=a;}}return {x:sx/w,y:sy/w};}
function leftContour(raw,y){
  for(let x=40;x<228;x++){
    const a=raw[(y*512+x)*4+3],b=raw[(y*512+x+1)*4+3];
    if(a<128&&b>=128)return x+(128-a)/Math.max(1,b-a);
  }return null;
}
function darkDistance(raw){
  const size=512*512,inside=new Uint8Array(size),toDark=new Float32Array(size),toLight=new Float32Array(size);
  for(let i=0;i<size;i++){
    const p=i*4;inside[i]=raw[p+3]>40&&raw[p]<105&&raw[p+1]<100&&raw[p+2]<100?1:0;
    toDark[i]=inside[i]?0:1000;toLight[i]=inside[i]?1000:0;
  }
  const pass=d=>{
    for(let y=1;y<511;y++)for(let x=1;x<511;x++){const i=y*512+x;d[i]=Math.min(d[i],d[i-1]+1,d[i-512]+1,d[i-513]+Math.SQRT2,d[i-511]+Math.SQRT2);}
    for(let y=510;y>0;y--)for(let x=510;x>0;x--){const i=y*512+x;d[i]=Math.min(d[i],d[i+1]+1,d[i+512]+1,d[i+513]+Math.SQRT2,d[i+511]+Math.SQRT2);}
  };pass(toDark);pass(toLight);
  const sdf=new Float32Array(size);for(let i=0;i<size;i++)sdf[i]=inside[i]?toLight[i]-.5:.5-toDark[i];return sdf;
}
function fieldAt(field,x,y){
  if(x<0||x>=511||y<0||y>=511)return -20;
  const ix=Math.floor(x),iy=Math.floor(y),u=x-ix,v=y-iy,i=iy*512+ix;
  return field[i]*(1-u)*(1-v)+field[i+1]*u*(1-v)+field[i+512]*(1-u)*v+field[i+513]*u*v;
}
function makeUnderbodyMorph(source,target,sourceControl,targetControl){
  // Control curves come from the two complete silhouettes. The masked layers
  // contain small antialias islands that must never drive a geometric warp.
  const readCurve=raw=>{
    const values=[];for(let y=389;y<=467;y++)values[y]=leftContour(raw,y);
    const median=[];for(let y=391;y<=465;y++){
      const nearby=values.slice(y-1,y+2).filter(v=>v!==null).sort((a,b)=>a-b);
      median[y]=nearby[Math.floor(nearby.length/2)];
    }
    const out=[];for(let y=393;y<=463;y++)out[y]=(median[y-1]+2*median[y]+median[y+1])/4;
    return out;
  };
  const from=readCurve(sourceControl),to=readCurve(targetControl),contours=[];
  const sourceDark=darkDistance(source),targetDark=darkDistance(target);
  for(let y=393;y<489;y++){
    // The lower contour is fully occluded by the original foreground paws;
    // stop following the generated support paw at the bottom of the patch.
    const yy=Math.min(y,463),top=smooth((y-395)/10);
    contours[y]={source:from[yy],target:from[yy]+(to[yy]-from[yy])*top};
  }
  return amount=>{
    if(amount<=0)return Buffer.from(source);if(amount>=1)return Buffer.from(target);
    const result=Buffer.from(source),a=Buffer.alloc(4),b=Buffer.alloc(4),anchor=237;
    for(let y=393;y<489;y++){
      const row=contours[y],hasContours=row.source!==null&&row.target!==null;
      const edge=hasContours?row.source+(row.target-row.source)*amount:null;
      for(let x=50;x<anchor;x++){
        const p=(y*512+x)*4;
        if(hasContours){
          const u=(x-edge)/(anchor-edge),sx=row.source+u*(anchor-row.source),tx=row.target+u*(anchor-row.target);
          a.fill(0);b.fill(0);pixel(source,sx,y,a,0);pixel(target,tx,y,b,0);
          const aa=a[3]*(1-amount),ba=b[3]*amount,alpha=aa+ba;result[p+3]=Math.round(alpha);
          if(alpha>.001)for(let channel=0;channel<3;channel++)result[p+channel]=Math.round((a[channel]*aa+b[channel]*ba)/alpha);else result.fill(0,p,p+4);
          // Interpolate the small fur-notch outline as a shape. Crossfading
          // its black pixels into white creates a conspicuous gray duplicate.
          const lineRegion=smooth((y-402)/7)*(1-smooth((y-458)/8))*(1-smooth((x-123)/13));
          if(lineRegion>0&&alpha>1){
            const distance=fieldAt(sourceDark,sx,y)*(1-amount)+fieldAt(targetDark,tx,y)*amount;
            const ink=smooth(distance+.5),black=[23,10,7];
            for(let c=0;c<3;c++){
              const paperA=a[c]>190?a[c]:[255,254,251][c],paperB=b[c]>190?b[c]:[255,254,251][c];
              const paper=(paperA*aa+paperB*ba)/alpha,paint=paper*(1-ink)+black[c]*ink;
              result[p+c]=Math.round(result[p+c]*(1-lineRegion)+paint*lineRegion);
            }
          }
        }else mixAt(result,p,source,target,amount);
      }
    }return result;
  };
}
async function createBodyRig(){
  const {base,paw,mask}=await buildMask();
  let under=await sharp(path.join(root,'sources/underbody.png')).resize(512,512).ensureAlpha().raw().toBuffer();
  const noseBase=nose(base),noseUnder=nose(under),shift={dx:noseBase.x-noseUnder.x,dy:noseBase.y-noseUnder.y};
  // Register the patch by the stable face; preserve original pixels outside
  // the original paw mask regardless of generated cutout differences.
  const registeredUnder=Buffer.alloc(under.length);for(let y=0;y<512;y++)for(let x=0;x<512;x++)pixel(under,x-shift.dx,y-shift.dy,registeredUnder,(y*512+x)*4);
  const cleared=Buffer.alloc(base.length);for(let p=0;p<base.length;p+=4)mixAt(cleared,p,base,registeredUnder,mask[p+3]/255);
  const exposedUnderbody=Buffer.from(cleared);
  for(let y=393;y<489;y++)for(let x=50;x<237;x++){
    const p=(y*512+x)*4,expanded=smooth((x-50)/12)*(1-smooth((x-227)/9))*smooth((y-395)/10),w=Math.max(mask[p+3]/255,expanded);
    mixAt(exposedUnderbody,p,base,registeredUnder,w);
  }
  const morphUnderbody=makeUnderbodyMorph(cleared,exposedUnderbody,base,registeredUnder);
  await sharp(cleared,{raw:spec}).png().toFile(path.join(root,'body/underbody-local.png'));
  const mouthSource=await sharp(path.join(repo,'artifacts/firefly-preview-v2/sources/prepare.png')).resize(512,512).ensureAlpha().raw().toBuffer(),noseSource=nose(mouthSource);
  const reference=await sharp(path.join(root,'sources/swat-anatomy-reference.png')).resize(512,512).ensureAlpha().raw().toBuffer();
  const armMask=await svgMask('M 108 365 C 130 370 150 380 165 390 L 195 410 C 187 433 171 447 151 447 C 128 443 110 427 96 408 Z');
  const arm=Buffer.from(reference);for(let p=0;p<arm.length;p+=4){
    const x=(p/4)%512,y=Math.floor(p/4/512),rootBlend=1-smooth((x-137)/48);
    const upperEdge=x<165?365+(x-108)*25/57:390+(x-165)*20/30,upperFeather=smooth((y-upperEdge)/12);
    const underside=443-.01*(x-150)**2,furBlend=.25+.75*smooth((y-(underside-15))/8);
    arm[p+3]=Math.round(arm[p+3]*armMask[p+3]/255*rootBlend*upperFeather*furBlend);if(!arm[p+3])arm.fill(0,p,p+4);
  }await sharp(arm,{raw:spec}).png().toFile(path.join(root,'body/forearm-reference.png'));
  const supportMask=await svgMask('M 232 424 C 242 414 258 408 272 409 C 291 409 308 416 315 431 C 323 447 321 461 308 470 C 293 479 264 479 242 474 C 225 471 213 462 214 447 C 215 437 221 429 232 424 Z');
  const supportPaw=Buffer.from(base);for(let p=0;p<base.length;p+=4){supportPaw[p+3]=Math.round(base[p+3]*supportMask[p+3]/255);if(!supportPaw[p+3])supportPaw.fill(0,p,p+4);}
  const mouthTexture=Buffer.from(base),mouthMask=new Float32Array(512*512),offset={dx:noseBase.x-noseSource.x,dy:noseBase.y-noseSource.y};
  for(let y=347;y<380;y++)for(let x=211;x<249;x++){
    const p=(y*512+x)*4,r=Math.hypot((x-230)/17,(y-363)/13),w=(1-smooth((r-.72)/.28))*smooth((y-(noseBase.y+3))/3);
    if(w){pixel(mouthSource,x-offset.dx,y-offset.dy,mouthTexture,p);mouthMask[y*512+x]=w;}
  }
  const metadata={selectedVariant:'b',pawSource:'Original complete screen-left front paw, no redraw',originalPawCenter:{x:160,y:440},pawScale:1,peak:pawTransform(1,'b'),peakLeadingUpperEdge:{x:107.32050807568876,y:354.01923788646684},peakBounds:{x0:63,y0:350,x1:145,y1:435},headTransform:headTransform(1),underbodyRegistration:shift,mouthRegistration:offset,eyeGeometry:'Original idle ellipses transformed analytically; renderer should overlay foregroundPaw once after eyes',notes:'Original paw moves continuously with no replacement/fade/scaling. Generated underbody is used only around the exposed old paw and adjacent forearm. Reference forearm is a separate, shoulder-feathered layer behind the original paw. Both eyes have rigid affine head motion. The planted right paw is restored from original pixels. Neutral render and finalize are exactly the source.'};
  await fs.writeFile(path.join(root,'body-rig.json'),JSON.stringify(metadata,null,2));
  function render({prepare=0,paw:amount=0,mouth=0,pawVariant='b'}={}){
    prepare=clamp(prepare);amount=clamp(amount);mouth=clamp(mouth);
    const t=headTransform(prepare),eyes=eyeDefaults.map(e=>{const p=pointTransform(e.cx,e.cy,t);return {...e,cx:p.x,cy:p.y,rotation:e.rotation+t.theta};});
    if(prepare===0&&amount===0&&mouth===0)return {rgba:Buffer.from(base),background:Buffer.from(base),foregroundPaw:null,eyes,pawCenter:{x:160,y:440},pawTip:{x:130,y:420},pawBounds:{x0:113,y0:403,x1:205,y1:477}};
    // Align the old/new opaque outer contours before mixing interior texture.
    // The dark edge migrates geometrically rather than fading to transparent.
    const withMouth=pawVariant==='a'?Buffer.from(cleared):morphUnderbody(smooth(prepare));
    if(mouth>0)for(let i=0;i<mouthMask.length;i++)if(mouthMask[i])mixAt(withMouth,i*4,withMouth,mouthTexture,mouthMask[i]*mouth);
    let torso=bodyWarp(withMouth,prepare);const moving=transformPaw(paw,amount,pawVariant);
    if(pawVariant!=='a')for(let p=0;p<torso.length;p+=4)if(supportMask[p+3])mixAt(torso,p,torso,base,supportMask[p+3]/255);
    if(pawVariant!=='a'&&amount>.001){
      const forearm=armWarp(arm,moving.transform,amount);
      // The folded upper arm stays behind the chest silhouette; only the
      // original foreground paw can move outside it. This is occlusion,
      // not a dissolve of an old paw into a new one.
      for(let p=0;p<forearm.length;p+=4)forearm[p+3]=Math.round(forearm[p+3]*torso[p+3]/255);
      torso=compositeForeground(torso,forearm);
    }
    const rgba=compositeForeground(torso,moving.raw);
    return {rgba,background:torso,foregroundPaw:moving.raw,eyes,pawCenter:moving.transform.center,pawTip:moving.leadingUpperLeft,pawBounds:moving.bounds,pawTransform:moving.transform};
  }
  return {render,metadata,compositeForeground,finalize:(rgba,state)=>state.foregroundPaw?compositeForeground(rgba,state.foregroundPaw):Buffer.from(rgba)};
}
async function prototype(){
  const rig=await createBodyRig(),phases=[0,.25,.5,1],layers=[],details=[],variant=process.argv.includes('--a')?'a':process.argv.includes('--c')?'c':'b',suffix=variant==='a'?'':'-'+variant;
  for(let i=0;i<phases.length;i++){
    const p=phases[i],frame=rig.render({prepare:p?1:0,paw:p,mouth:p?.75:0,pawVariant:variant}),file=path.join(root,'body',`prototype${suffix}-${i}.png`);await sharp(frame.rgba,{raw:spec}).png().toFile(file);
    layers.push({input:await sharp(frame.rgba,{raw:spec}).resize(384,384).png().toBuffer(),left:i*384,top:34});
    layers.push({input:Buffer.from(`<svg width="384" height="34"><text x="12" y="24" font-family="Arial" font-size="18" fill="#526049">Original paw / ${(p*100).toFixed(0)}% / scale 1.00</text></svg>`),left:i*384,top:0});
    details.push({input:await sharp(frame.rgba,{raw:spec}).extract({left:60,top:330,width:170,height:155}).resize(510,465).png().toBuffer(),left:i*510,top:34});
    details.push({input:Buffer.from(`<svg width="510" height="34"><text x="12" y="24" font-family="Arial" font-size="18" fill="#526049">${(p*100).toFixed(0)}% / center (${frame.pawCenter.x.toFixed(1)},${frame.pawCenter.y.toFixed(1)})</text></svg>`),left:i*510,top:0});
    console.log(JSON.stringify({phase:p,pawCenter:frame.pawCenter,pawTip:frame.pawTip,pawBounds:frame.pawBounds}));
  }
  await sharp({create:{width:1536,height:418,channels:4,background:'#fff8f2'}}).composite(layers).png().toFile(path.join(root,`body-prototype${suffix}-contact.png`));
  await sharp({create:{width:2040,height:499,channels:4,background:'#fff8f2'}}).composite(details).png().toFile(path.join(root,`body-prototype${suffix}-detail.png`));
}
module.exports={createBodyRig,compositeForeground,pawTransform};
if(require.main===module)prototype().catch(e=>{console.error(e);process.exitCode=1;});
