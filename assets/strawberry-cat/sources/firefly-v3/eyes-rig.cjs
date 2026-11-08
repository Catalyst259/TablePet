'use strict';
// Preview-only binocular rig. u/v are absolute offsets in each eye frame,
// not identical pixel translations added to the original convergent idle.
const fs=require('node:fs/promises'),path=require('node:path'),sharp=require('sharp');
const {fit,hull,edgeDistance}=require('./eyes-geometry.cjs');
const root=path.resolve(__dirname,'../../../..'),out=path.join(__dirname,'eyes');
const clamp=(v,a=0,b=1)=>Math.max(a,Math.min(b,v));
const smooth=x=>{x=clamp(x);return x*x*(3-2*x);};
const mix=(a,b,t)=>a+(b-a)*t;
const mul=(a,b)=>[a[0]*b[0]+a[1]*b[2],a[0]*b[1]+a[1]*b[3],a[2]*b[0]+a[3]*b[2],a[2]*b[1]+a[3]*b[3]];
const inv=a=>{const d=a[0]*a[3]-a[1]*a[2];return [a[3]/d,-a[1]/d,-a[2]/d,a[0]/d];};
const vec=(a,x,y)=>[a[0]*x+a[1]*y,a[2]*x+a[3]*y];
const rot=t=>[Math.cos(t),-Math.sin(t),Math.sin(t),Math.cos(t)];
const ellipseMatrix=e=>mul(mul(rot(e.rotation),[e.rx,0,0,e.ry]),rot(-e.rotation));
const eyeDefaults=[{cx:164.5437392283,cy:328.7822235076,rx:42.746102033,ry:40.6430409317,rotation:-.7577536},{cx:304.0543211135,cy:343.8565418625,rx:43.88056064,ry:40.9359499266,rotation:-.248492869}];
function canonicalFrame(f,b){
  const list=[{...f},{...f,rx:f.ry,ry:f.rx,rotation:f.rotation+Math.PI/2},{...f,rx:f.ry,ry:f.rx,rotation:f.rotation-Math.PI/2}];
  for(const q of list){while(q.rotation-b.rotation>Math.PI/2)q.rotation-=Math.PI;while(q.rotation-b.rotation<-Math.PI/2)q.rotation+=Math.PI;}
  return list.sort((x,y)=>Math.abs(x.rotation-b.rotation)-Math.abs(y.rotation-b.rotation))[0];
}
function sample(src,w,h,x,y,c,channels=4){
  if(x<0||x>=w-1||y<0||y>=h-1)return 0;
  const ix=Math.floor(x),iy=Math.floor(y),u=x-ix,v=y-iy;
  return src[(iy*w+ix)*channels+c]*(1-u)*(1-v)+src[(iy*w+ix+1)*channels+c]*u*(1-v)+src[((iy+1)*w+ix)*channels+c]*(1-u)*v+src[((iy+1)*w+ix+1)*channels+c]*u*v;
}
function fitPupilAtFixedCenter(points,cx,cy,initial){
  const score=p=>points.reduce((a,[x,y])=>{const d=Math.abs(edgeDistance(x,y,[cx,cy,p[0],p[1],p[2]]));return a+(d<1.5?d*d:3*d-2.25);},0)/points.length;
  let best=[initial.rx,initial.ry,initial.rotation],cost=score(best);
  for(const angle of [-1,-.4,.3,.9,1.3]){
    let q=[initial.rx,initial.ry,angle],s=score(q);
    for(let step=3;step>.002;step*=.58)for(let k=0;k<70;k++){
      let changed=false;for(let j=0;j<3;j++)for(const sign of [-1,1]){const p=q.slice();p[j]+=sign*step*(j===2?.035:1);if(p[0]<26||p[0]>52||p[1]<28||p[1]>52)continue;const n=score(p);if(n<s){q=p;s=n;changed=true;}}if(!changed)break;
    }
    if(s<cost){best=q;cost=s;}
  }
  return {cx,cy,rx:best[0],ry:best[1],rotation:best[2],fitLoss:cost};
}
function measureEyes(raw,{hints=eyeDefaults}={}){
  if(!Buffer.isBuffer(raw)||raw.length!==512*512*4)throw Error('measureEyes expects 512 x 512 RGBA');
  return hints.map((h,e)=>{
    let points=[];const binary=new Uint8Array(512*512),x0=Math.max(0,Math.floor(h.cx-55)),x1=Math.min(511,Math.ceil(h.cx+55)),y0=Math.max(0,Math.floor(h.cy-54)),y1=Math.min(511,Math.ceil(h.cy+54));
    for(let y=y0;y<=y1;y++)for(let x=x0;x<=x1;x++){const p=(y*512+x)*4,r=raw[p],g=raw[p+1],b=raw[p+2];if(raw[p+3]>220&&((r<70&&g<70&&b<70)||(g>110&&b>145&&g-r>65&&b-r>75)))binary[y*512+x]=1;}
    const seen=new Uint8Array(512*512);for(let y=y0;y<=y1;y++)for(let x=x0;x<=x1;x++){const start=y*512+x;if(!binary[start]||seen[start])continue;const stack=[start],part=[];seen[start]=1;while(stack.length){const q=stack.pop();part.push([q%512,Math.floor(q/512)]);for(const z of [q-1,q+1,q-512,q+512])if(binary[z]&&!seen[z]){seen[z]=1;stack.push(z);}}if(part.length>points.length)points=part;}
    if(points.length<300)throw Error('Eye frame not found: '+e);
    const f=fit(hull(points),[h.cx,h.cy,h.rx,h.ry,h.rotation],{span:24,prior:.004});
    const candidates=[f,{...f,rx:f.ry,ry:f.rx,rotation:f.rotation+Math.PI/2},{...f,rx:f.ry,ry:f.rx,rotation:f.rotation-Math.PI/2}];
    for(const c of candidates){while(c.rotation-h.rotation>Math.PI/2)c.rotation-=Math.PI;while(c.rotation-h.rotation<-Math.PI/2)c.rotation+=Math.PI;}
    return candidates.sort((a,b)=>Math.abs(a.rotation-h.rotation)-Math.abs(b.rotation-h.rotation))[0];
  });
}
async function createEyeRig(){
  const base=await sharp(path.join(root,'assets/strawberry-cat/frames/fall_asleep-00.png')).ensureAlpha().raw().toBuffer();
  const reference=await sharp(path.join(__dirname,'sources/right-corrected.png')).resize(512,512).ensureAlpha().raw().toBuffer();
  const upArtwork=await sharp(path.join(__dirname,'sources/up-larger.png')).resize(512,512).ensureAlpha().raw().toBuffer();
  const iris=await sharp(path.join(__dirname,'sources/iris.png')).resize(512,512).ensureAlpha().raw().toBuffer();
  const genPupil=await sharp(path.join(__dirname,'sources/pupils.png')).resize(512,256).ensureAlpha().raw().toBuffer();
  const geometry=JSON.parse(await fs.readFile(path.join(out,'geometry.json'),'utf8'));
  const idle=geometry.poses.find(p=>p.name==='idle').eyes,approved=geometry.poses.find(p=>p.name==='corrected-right').eyes,upReference=geometry.poses.find(p=>p.name==='up-larger').eyes;
  const N=192,span=2.5,eyes=[],anchor={u:.35,v:.10};
  for(let e=0;e<2;e++){
    const b=idle[e],r=approved[e],bf=b.outer,rf=r.outer;
    // The hidden half of a pupil does not uniquely determine center/radius.
    // Fix the approved common gaze, then fit radii to its visible boundary.
    const sourceFit=fitPupilAtFixedCenter(r.pupilBoundary,rf.cx+anchor.u*rf.rx,rf.cy+anchor.v*rf.ry,r.pupil);
    const sourceP=ellipseMatrix(sourceFit),sourcePinv=inv(sourceP),sourceToBase=[bf.rx/rf.rx,0,0,bf.ry/rf.ry];
    const anchorP=mul(sourceToBase,sourceP),idleP=ellipseMatrix(b.pupil),idlePinv=inv(idleP);
    const up=upReference[e],upNorm={u:(up.pupil.cx-up.outer.cx)/up.outer.rx,v:(up.pupil.cy-up.outer.cy)/up.outer.ry};
    // A partially occluded right pupil overestimates the hidden black disc.
    // The complete up-reference boundary supplies size for EVERY direction;
    // the approved right reference still supplies its direction/orientation.
    const upSize=mul([bf.rx/up.outer.rx,0,0,bf.ry/up.outer.ry],ellipseMatrix(up.pupil));
    const ab=(anchorP[1]+anchorP[2])/2,upAb=(upSize[1]+upSize[2])/2,upMean=(upSize[0]+upSize[3])/2,upSpread=Math.hypot((upSize[0]-upSize[3])/2,upAb);
    const eigen={major:upMean+upSpread,minor:upMean-upSpread,angle:.5*Math.atan2(2*ab,anchorP[0]-anchorP[3])};
    let upAngle=up.pupil.rotation+(up.pupil.rx<up.pupil.ry?Math.PI/2:0);while(upAngle-eigen.angle>Math.PI/2)upAngle-=Math.PI;while(upAngle-eigen.angle<-Math.PI/2)upAngle+=Math.PI;
    const layer=new Float32Array(N*N*4),mask=new Float32Array(512*512),background=new Float32Array(512*512*3),highlights=[];
    for(let y=Math.floor(bf.cy-48);y<=Math.ceil(bf.cy+48);y++)for(let x=Math.floor(bf.cx-49);x<=Math.ceil(bf.cx+49);x++){
      const i=y*512+x,p=i*4,cov=clamp(Math.max((iris[p+1]-iris[p])/118,(iris[p+2]-iris[p])/152));mask[i]=smooth((cov-.06)/.94);
      let rgb=[iris[p],iris[p+1],iris[p+2]];
      if(rgb[1]<175||rgb[2]<205||rgb[1]-rgb[0]<75){
        let nearest=1e9,best=-1;for(let dy=-13;dy<=13;dy++)for(let dx=-13;dx<=13;dx++){const q=((y+dy)*512+x+dx)*4,d=dx*dx+dy*dy;if(d<nearest&&iris[q+1]>175&&iris[q+2]>205&&iris[q+1]-iris[q]>75){nearest=d;best=q;}}
        if(best>=0)rgb=[iris[best],iris[best+1],iris[best+2]];
      }
      for(let c=0;c<3;c++)background[i*3+c]=rgb[c];
    }
    for(let y=0;y<N;y++)for(let x=0;x<N;x++){
      const u=(x/(N-1)-.5)*span,v=(y/(N-1)-.5)*span,[dx,dy]=vec(sourceP,u,v),sx=sourceFit.cx+dx,sy=sourceFit.cy+dy;
      const radial=Math.hypot(u,v),a=clamp((1-radial)*Math.min(sourceFit.rx,sourceFit.ry)+.5);
      const orig=[0,1,2].map(c=>sample(reference,512,512,sx,sy,c));
      const gpX=(e===0?80:333)+clamp(u+.5)*35,gpY=160+clamp(v+.5)*35;
      let rgb=[0,1,2].map(c=>sample(genPupil,512,256,gpX,gpY,c));
      // Copy black surface texture only. Each highlight is extracted below
      // exactly once, so no second catchlight can remain in the pupil texture.
      // Use one continuous generated black surface. Copying isolated dark
      // pixels around the source highlights retained their antialiased
      // outlines and made faint empty highlight rings when turning left.
      const p=(y*N+x)*4;for(let c=0;c<3;c++)layer[p+c]=rgb[c]*a;layer[p+3]=a;
    }
    for(let h=0;h<2;h++){
      const rh=r.highlights[h],ih=b.highlights[h],bounds=[rh.bounds[0]-2,rh.bounds[1]-2,rh.bounds[2]+2,rh.bounds[3]+2],width=bounds[2]-bounds[0]+1,height=bounds[3]-bounds[1]+1,patch=new Float32Array(width*height*4);
      for(let y=0;y<height;y++)for(let x=0;x<width;x++){
        const p=((bounds[1]+y)*512+bounds[0]+x)*4,q=(y*width+x)*4;
        const lum=Math.min(reference[p],reference[p+1],reference[p+2]),chroma=Math.max(reference[p],reference[p+1],reference[p+2])-lum,a=chroma<45?clamp((lum-18)/237):0;
        for(let c=0;c<3;c++)patch[q+c]=255*a;patch[q+3]=a;
      }
      const rCenter=vec(sourcePinv,rh.cx-sourceFit.cx,rh.cy-sourceFit.cy),iCenter=vec(idlePinv,ih.cx-b.pupil.cx,ih.cy-b.pupil.cy);
      const anchorH=mul(sourcePinv,[width,0,0,height]),idleH=mul(idlePinv,[ih.bounds[2]-ih.bounds[0]+5,0,0,ih.bounds[3]-ih.bounds[1]+5]);
      const uh=up.highlights[h],upBounds=[uh.bounds[0]-2,uh.bounds[1]-2,uh.bounds[2]+2,uh.bounds[3]+2],upWidth=upBounds[2]-upBounds[0]+1,upHeight=upBounds[3]-upBounds[1]+1,upPatch=new Float32Array(upWidth*upHeight*4);
      for(let y=0;y<upHeight;y++)for(let x=0;x<upWidth;x++){
        const p=((upBounds[1]+y)*512+upBounds[0]+x)*4,q=(y*upWidth+x)*4;
        const lum=Math.min(upArtwork[p],upArtwork[p+1],upArtwork[p+2]),chroma=Math.max(upArtwork[p],upArtwork[p+1],upArtwork[p+2])-lum,a=chroma<45?clamp((lum-18)/237):0;
        for(let c=0;c<3;c++)upPatch[q+c]=255*a;upPatch[q+3]=a;
      }
      const upWorld=[bf.cx+(uh.cx-up.outer.cx)*bf.rx/up.outer.rx,bf.cy+(uh.cy-up.outer.cy)*bf.ry/up.outer.ry],upWorldH=[upWidth*bf.rx/up.outer.rx,0,0,upHeight*bf.ry/up.outer.ry];
      highlights.push({patch,width,height,rCenter,iCenter,anchorH,idleH,sourceCenter:[rh.cx-bounds[0],rh.cy-bounds[1]],upPatch,upWidth,upHeight,upSourceCenter:[uh.cx-upBounds[0],uh.cy-upBounds[1]],upWorld,upWorldH});
    }
    const maskBytes=Buffer.from(mask.map(x=>Math.round(x*255))),softMask=await sharp(maskBytes,{raw:{width:512,height:512,channels:1}}).blur(.45).toColourspace('b-w').raw().toBuffer();
    for(let i=0;i<mask.length;i++)mask[i]=Math.min(mask[i],softMask[i]/255);
    eyes.push({baseFrame:bf,referenceFrame:rf,idle:b,sourceFit,anchorP,idleP,eigen,upNorm,upAngle,up,layer,mask,background,highlights});
  }
  function poseData(e,{u,v,engagement=1}){
    const eye=eyes[e],a=clamp(engagement),b=eye.baseFrame;
    u=clamp(Number(u)||0,-.42,.42);v=clamp(Number(v)||0,-.40,.40);
    const upWeight=smooth(-v/.15),trackU=u+upWeight*eye.upNorm.u,trackV=v+upWeight*(eye.upNorm.v+.15);
    const center=[mix(eye.idle.pupil.cx,b.cx+trackU*b.rx,a),mix(eye.idle.pupil.cy,b.cy+trackV*b.ry,a)];
    const theta=mix(eye.eigen.angle,eye.upAngle,upWeight),trackingP=mul(mul(rot(theta),[eye.eigen.major,0,0,eye.eigen.minor]),rot(-theta));
    const iMean=(eye.idleP[0]+eye.idleP[3])/2,iSpread=Math.hypot((eye.idleP[0]-eye.idleP[3])/2,eye.idleP[1]),iAngle=.5*Math.atan2(2*eye.idleP[1],eye.idleP[0]-eye.idleP[3]);let targetAngle=theta;while(targetAngle-iAngle>Math.PI/2)targetAngle-=Math.PI;while(targetAngle-iAngle<-Math.PI/2)targetAngle+=Math.PI;
    const thetaEntry=mix(iAngle,targetAngle,a),majorEntry=mix(iMean+iSpread,eye.eigen.major,a),minorEntry=mix(iMean-iSpread,eye.eigen.minor,a),P=mul(mul(rot(thetaEntry),[majorEntry,0,0,minorEntry]),rot(-thetaEntry));
    const Pinv=inv(P),trackingCenter=[b.cx+trackU*b.rx,b.cy+trackV*b.ry],anchorCenter=[b.cx+anchor.u*b.rx,b.cy+anchor.v*b.ry];
    const away=smooth(Math.hypot(u-anchor.u,v-anchor.v)/.10);
    const highlights=eye.highlights.map(h=>{
      const hi=vec(eye.idleP,...h.iCenter),ht=vec(trackingP,...h.rCenter);
      const idleWorld=[eye.idle.pupil.cx+hi[0],eye.idle.pupil.cy+hi[1]];
      // A corneal reflection moves less than the rotating pupil. The anchor
      // is exact; left/up glances retain full oval reflections instead of
      // driving them into the fixed eyelid and clipping them into slivers.
      const normalWorld=[trackingCenter[0]+ht[0]+.45*(anchorCenter[0]-trackingCenter[0]),trackingCenter[1]+ht[1]+.45*(anchorCenter[1]-trackingCenter[1])];
      const upWorld=[h.upWorld[0]+u*b.rx*.55,h.upWorld[1]+(v+.15)*b.ry*.55];
      const trackWorld=normalWorld.map((n,i)=>mix(n,upWorld[i],upWeight));
      const trackWorldH=mul(trackingP,h.anchorH).map((n,i)=>mix(n,h.upWorldH[i],upWeight)),trackingH=mul(inv(trackingP),trackWorldH);
      const world=idleWorld.map((n,i)=>mix(n,trackWorld[i],a)),centerUV=vec(Pinv,world[0]-center[0],world[1]-center[1]),H=h.idleH.map((n,i)=>mix(n,trackingH[i],a));
      const extent=Math.max(Math.hypot(H[0]+H[1],H[2]+H[3]),Math.hypot(H[0]-H[1],H[2]-H[3]))*.5;
      const radius=Math.hypot(...centerUV),safe=1-extent-.025;
      if(radius>safe){const correction=1-a*away*(1-upWeight)*(1-safe/radius);centerUV[0]*=correction;centerUV[1]*=correction;}
      return {...h,center:centerUV,H,Hinv:inv(H),upBlend:a*upWeight};
    });
    return {center,P,Pinv:inv(P),highlights,a,u,v};
  }
  function render(baseRGBA,{u=0,v=0,engagement=1,eyes:providedFrames}={}){
    if(!Buffer.isBuffer(baseRGBA)||baseRGBA.length!==512*512*4)throw Error('render expects 512 x 512 RGBA Buffer');
    const dest=Buffer.from(baseRGBA),a=clamp(engagement);if(a<1e-9)return dest;
    const frames=providedFrames??(baseRGBA.equals(base)?eyes.map(e=>e.baseFrame):measureEyes(baseRGBA));
    // Near-circular eye fits have unstable major-axis angles. Actual head
    // roll comes from the line joining the two eye centers, shared by both
    // eyes; per-eye fitted angles only disambiguate swapped ellipse radii.
    const headRoll=clamp(Math.atan2(frames[1].cy-frames[0].cy,frames[1].cx-frames[0].cx)-Math.atan2(eyes[1].baseFrame.cy-eyes[0].baseFrame.cy,eyes[1].baseFrame.cx-eyes[0].baseFrame.cx),-.22,.22);
    for(let e=0;e<2;e++){
      const eye=eyes[e],b=eye.baseFrame,f=canonicalFrame(frames[e],b),pose=poseData(e,{u,v,engagement:a});
      const A=mul(rot(headRoll),[f.rx/b.rx,0,0,f.ry/b.ry]),Ainv=inv(A),startup=smooth(a/.10);
      const radius=Math.ceil(Math.max(f.rx,f.ry)+9);
      for(let y=Math.max(0,Math.floor(f.cy-radius));y<=Math.min(511,Math.ceil(f.cy+radius));y++)for(let x=Math.max(0,Math.floor(f.cx-radius));x<=Math.min(511,Math.ceil(f.cx+radius));x++){
        const [dx,dy]=vec(Ainv,x-f.cx,y-f.cy),cx=b.cx+dx,cy=b.cy+dy;
        const weight=sample(eye.mask,512,512,cx,cy,0,1)*startup;if(weight<1e-6)continue;
        const uv=vec(pose.Pinv,cx-pose.center[0],cy-pose.center[1]),tx=(uv[0]/span+.5)*(N-1),ty=(uv[1]/span+.5)*(N-1);
        const alpha=sample(eye.layer,N,N,tx,ty,3),rgb=[0,1,2].map(c=>sample(eye.layer,N,N,tx,ty,c)+sample(eye.background,512,512,cx,cy,c,3)*(1-alpha));
        for(const h of pose.highlights){
          const q=vec(h.Hinv,uv[0]-h.center[0],uv[1]-h.center[1]),hx=h.sourceCenter[0]+q[0]*h.width,hy=h.sourceCenter[1]+q[1]*h.height;
          const upHx=h.upSourceCenter[0]+q[0]*h.upWidth,upHy=h.upSourceCenter[1]+q[1]*h.upHeight;
          const ha=mix(sample(h.patch,h.width,h.height,hx,hy,3),sample(h.upPatch,h.upWidth,h.upHeight,upHx,upHy,3),h.upBlend)*alpha;
          for(let c=0;c<3;c++)rgb[c]=rgb[c]*(1-ha)+255*ha;
        }
        const p=(y*512+x)*4;for(let c=0;c<3;c++)dest[p+c]=Math.round(clamp(baseRGBA[p+c]*(1-weight)+rgb[c]*weight,0,255));
      }
    }
    return dest;
  }
  const metadata={version:3,anchor,normalizedConvention:'u/v describe common gaze; up v=-.15 adds measured binocular convergence and vertical offsets from up-larger. Engagement transitions from the original convergent idle.',sources:['sources/right-corrected.png','sources/up-larger.png'],projection:'Fixed principal radii measured from the complete up-larger pupils; orientation alone changes with direction. Right reference supplies direction and highlight layout, not its uncertain hidden pupil size. Entry/exit interpolate principal radii and angle, never matrix elements.',startupWindowEngagement:.1,eyes:eyes.map(e=>({baseFrame:e.baseFrame,referenceFrame:e.referenceFrame,sourcePupil:e.sourceFit,idlePupil:e.idle.pupil,trackingRadii:e.eigen,upNorm:e.upNorm,upAngle:e.upAngle})),limitations:['Original idle pupil contours differ slightly from generated references, so pupil size settles smoothly during engagement.','Eyelid clipping changes visible black area near the edge even though the underlying pupil disc stays constant.','Catchlights remain two single texture instances per eye; each morphs position, size and alpha texture continuously toward the up reference.']};
  return {render,measureEyes,base,metadata,poseData,frames:eyes.map(e=>e.baseFrame)};
}
module.exports={createEyeRig,createRig:createEyeRig,measureEyes};
