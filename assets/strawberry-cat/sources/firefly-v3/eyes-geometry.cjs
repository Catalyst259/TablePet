'use strict';
const fs=require('node:fs/promises'),path=require('node:path'),sharp=require('sharp');
const out=path.join(__dirname,'eyes');
const root=path.resolve(__dirname,'../../../..');
const files=[['idle',path.join(root,'assets/strawberry-cat/frames/fall_asleep-00.png')],['corrected-right',path.join(__dirname,'sources/right-corrected.png')],['up-reference',path.join(__dirname,'sources/up-reference.png')],['up-larger',path.join(__dirname,'sources/up-larger.png')]];
const rois=[[115,280,211,378],[252,294,355,392]];
const edgeDistance=(x,y,p)=>{const dx=x-p[0],dy=y-p[1],c=Math.cos(p[4]),s=Math.sin(p[4]);return (Math.hypot((c*dx+s*dy)/p[2],(-s*dx+c*dy)/p[3])-1)*(p[2]+p[3])/2;};
const cyan=(raw,p)=>raw[p+1]>110&&raw[p+2]>145&&raw[p+1]-raw[p]>65&&raw[p+2]-raw[p]>75;
const dark=(raw,p)=>Math.max(raw[p],raw[p+1],raw[p+2])<75;
function fit(points,guess,{prior=.001,span=20}={}){
  const lower=[guess[0]-span,guess[1]-span,20,25,-1.4],upper=[guess[0]+span,guess[1]+span,55,56,1.4];
  const score=p=>points.reduce((sum,[x,y])=>{const d=Math.abs(edgeDistance(x,y,p));return sum+(d<2?d*d:4*d-4);},0)/points.length+prior*((p[2]-guess[2])**2+(p[3]-guess[3])**2);
  let best=guess.slice(),loss=score(best);
  for(const startAngle of [-.8,-.3,.3,.8]){
    let p=guess.slice();p[4]=startAngle;let cost=score(p);
    for(let step=4;step>.002;step*=.6){
      for(let round=0;round<90;round++){
        let changed=false;
        for(let j=0;j<5;j++)for(const sign of [-1,1]){
          const q=p.slice();q[j]+=sign*step*(j===4?.035:1);
          if(q[j]<lower[j]||q[j]>upper[j])continue;
          const next=score(q);if(next<cost){cost=next;p=q;changed=true;}
        }
        if(!changed)break;
      }
    }
    if(cost<loss){best=p;loss=cost;}
  }
  const residuals=points.map(p=>Math.abs(edgeDistance(...p,best))).sort((a,b)=>a-b);
  return {cx:best[0],cy:best[1],rx:best[2],ry:best[3],rotation:best[4],meanResidualPx:residuals.reduce((a,b)=>a+b,0)/residuals.length,p95ResidualPx:residuals[Math.floor(residuals.length*.95)],pointCount:points.length};
}
function hull(points){
  points=points.slice().sort((a,b)=>a[0]-b[0]||a[1]-b[1]);const cross=(o,a,b)=>(a[0]-o[0])*(b[1]-o[1])-(a[1]-o[1])*(b[0]-o[0]);
  const lo=[],hi=[];for(const p of points){while(lo.length>=2&&cross(lo.at(-2),lo.at(-1),p)<=0)lo.pop();lo.push(p);}for(const p of points.toReversed()){while(hi.length>=2&&cross(hi.at(-2),hi.at(-1),p)<=0)hi.pop();hi.push(p);}return lo.slice(0,-1).concat(hi.slice(0,-1));
}
function highlights(raw,roi,outer){
  const [x0,y0,x1,y1]=roi,seen=new Set(),components=[];
  const isBright=i=>{const p=i*4;return Math.min(raw[p],raw[p+1],raw[p+2])>180&&edgeDistance(i%512,Math.floor(i/512),[outer.cx,outer.cy,outer.rx,outer.ry,outer.rotation])<-2;};
  for(let y=y0;y<=y1;y++)for(let x=x0;x<=x1;x++){
    const i=y*512+x;if(seen.has(i)||!isBright(i))continue;const stack=[i],pts=[];seen.add(i);
    while(stack.length){const p=stack.pop();pts.push([p%512,Math.floor(p/512)]);for(const q of [p-1,p+1,p-512,p+512])if(!seen.has(q)&&isBright(q)){seen.add(q);stack.push(q);}}
    if(pts.length>=3)components.push({area:pts.length,cx:pts.reduce((a,p)=>a+p[0],0)/pts.length,cy:pts.reduce((a,p)=>a+p[1],0)/pts.length,bounds:[Math.min(...pts.map(p=>p[0])),Math.min(...pts.map(p=>p[1])),Math.max(...pts.map(p=>p[0])),Math.max(...pts.map(p=>p[1]))]});
  }
  return components.sort((a,b)=>a.cx-b.cx);
}
async function main(){
  await fs.mkdir(out,{recursive:true});const result={coordinateSystem:'512x512 cat sprite, x right/y down. Outer frames must stay fixed during animation.',poses:[]},layers=[];
  for(let f=0;f<files.length;f++){
    const [name,file]=files[f],raw=await sharp(file).resize(512,512).ensureAlpha().raw().toBuffer(),eyes=[],svg=[];
    for(let e=0;e<2;e++){
      const roi=rois[e],[x0,y0,x1,y1]=roi,all=[],pupilPoints=[];
      const guess=f===0?(e===0?[178,330,34,39,.65]:[289,340,37,36,-.35]):name.startsWith('up')?(e===0?[169,318,32,35,0]:[298,332,32,35,0]):(e===0?[178,333,33,39,.6]:[316,348,34,39,.6]);
      for(let y=y0;y<=y1;y++)for(let x=x0;x<=x1;x++){
        const p=(y*512+x)*4;if(dark(raw,p)||cyan(raw,p))all.push([x,y]);
        if(!dark(raw,p))continue;
        for(const [dx,dy] of [[-1,0],[1,0],[0,-1],[0,1]])if(cyan(raw,((y+dy)*512+x+dx)*4)&&Math.hypot(x-guess[0],y-guess[1])<Math.hypot(x+dx-guess[0],y+dy-guess[1])){pupilPoints.push([x+.5*dx,y+.5*dy]);break;}
      }
      const outerPoints=hull(all),outer=fit(outerPoints,e===0?[164,329,42,42,0]:[305,344,44,41,0],{prior:.0002,span:6});
      const pupil=fit(pupilPoints,guess),norm={x:(pupil.cx-outer.cx)/outer.rx,y:(pupil.cy-outer.cy)/outer.ry,pupilRx:pupil.rx/outer.rx,pupilRy:pupil.ry/outer.ry};
      eyes.push({name:e===0?'screen-left':'screen-right',outer,pupil,normalized:norm,highlights:highlights(raw,roi,outer),pupilBoundary:pupilPoints});
      for(const [s,color] of [[outer,'#ff9400'],[pupil,'#1aff50']])svg.push(`<ellipse cx="${s.cx}" cy="${s.cy}" rx="${s.rx}" ry="${s.ry}" transform="rotate(${s.rotation*180/Math.PI} ${s.cx} ${s.cy})" fill="none" stroke="${color}" stroke-width=".6"/>`);
      svg.push(`<line x1="${outer.cx-3}" y1="${outer.cy}" x2="${outer.cx+3}" y2="${outer.cy}" stroke="#f70" stroke-width=".7"/><line x1="${outer.cx}" y1="${outer.cy-3}" x2="${outer.cx}" y2="${outer.cy+3}" stroke="#f70" stroke-width=".7"/>`);
    }
    const marked=await sharp(raw,{raw:{width:512,height:512,channels:4}}).composite([{input:Buffer.from(`<svg width="512" height="512">${svg.join('')}</svg>`)}]).png().toBuffer();
    layers.push({input:await sharp(marked).extract({left:110,top:274,width:252,height:126}).resize(1008,504).png().toBuffer(),left:0,top:f*544+40});
    layers.push({input:Buffer.from(`<svg width="1008" height="40"><text x="12" y="28" font-size="24" font-family="Arial" fill="#66515a">${name}: orange=eye frame, green=pupil</text></svg>`),left:0,top:f*544});
    result.poses.push({name,file,eyes});
  }
  await fs.writeFile(path.join(out,'geometry.json'),JSON.stringify(result,null,2));
  await sharp({create:{width:1008,height:files.length*544,channels:4,background:'#fff8f2'}}).composite(layers).png().toFile(path.join(out,'geometry-contact.png'));
  console.log(JSON.stringify(result.poses.map(p=>({name:p.name,eyes:p.eyes.map(({name,outer,pupil,normalized,highlights})=>({name,outer,pupil,normalized,highlights}))})),null,2));
}
module.exports={fit,hull,edgeDistance,highlights};
if(require.main===module)main().catch(e=>{console.error(e);process.exitCode=1;});
