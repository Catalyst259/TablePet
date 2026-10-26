// Crop and align model-created key poses. Motion interpolation is delegated to RIFE.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');
const root=path.resolve(__dirname,'../assets/strawberry-cat');
function components(data,w,h,accept) {
  const labels=new Int32Array(w*h),queue=new Int32Array(w*h),found=[];let id=0;
  for(let p=0;p<w*h;p++){
    if(labels[p]||!accept(p))continue;
    let head=0,tail=1,x0=w,y0=h,x1=0,y1=0;queue[0]=p;labels[p]=++id;
    while(head<tail){const q=queue[head++],x=q%w,y=Math.floor(q/w);x0=Math.min(x0,x);x1=Math.max(x1,x);y0=Math.min(y0,y);y1=Math.max(y1,y);
      for(const n of [x?q-1:-1,x<w-1?q+1:-1,y?q-w:-1,y<h-1?q+w:-1])if(n>=0&&!labels[n]&&accept(n)){labels[n]=id;queue[tail++]=n;}
    }found.push({id,count:tail,x:x0,y:y0,width:x1-x0+1,height:y1-y0+1});
  }return {labels,found};
}
async function split(name,columns,rows,scaleOverride){
  const {data,info}=await sharp(path.join(root,'sources/motion-v2',name+'-sheet.png')).ensureAlpha().raw().toBuffer({resolveWithObject:true});
  const w=info.width,h=info.height;
  function valley(center,radius,count){let best=Math.round(center),min=Infinity;for(let k=Math.round(center-radius);k<=center+radius;k++){const n=count(k);if(n<min){best=k;min=n;}}return best;}
  const ycuts=[0];for(let r=1;r<rows;r++)ycuts.push(valley(h*r/rows,h/rows*.15,y=>{let n=0;for(let x=0;x<w;x++)if(data[(y*w+x)*4+3]>128)n++;return n;}));ycuts.push(h);
  const poses=[];
  for(let row=0;row<rows;row++){
    const y0=ycuts[row],y1=ycuts[row+1],xcuts=[0];
    for(let c=1;c<columns;c++)xcuts.push(valley(w*c/columns,w/columns*.12,x=>{let n=0;for(let y=y0;y<y1;y++)if(data[(y*w+x)*4+3]>128)n++;return n;}));xcuts.push(w);
    for(let col=0;col<columns;col++){
      const x0=xcuts[col],x1=xcuts[col+1],rw=x1-x0,rh=y1-y0;
      const {labels,found}=components(data,rw,rh,p=>data[((Math.floor(p/rw)+y0)*w+(p%rw)+x0)*4+3]>=64);
      const box=found.sort((a,b)=>b.count-a.count)[0];if(!box||box.count<1000)throw new Error('Missing pose');
      const rgba=Buffer.alloc(box.width*box.height*4);
      for(let y=0;y<box.height;y++)for(let x=0;x<box.width;x++){
        const loc=(box.y+y)*rw+box.x+x,src=((box.y+y+y0)*w+box.x+x+x0)*4,out=(y*box.width+x)*4;
        if(labels[loc]!==box.id)continue;data.copy(rgba,out,src,src+4);if(rgba[out+3]>=245)rgba[out+3]=255;
      }
      const pink=components(rgba,box.width,box.height,p=>{const k=p*4,r=rgba[k],g=rgba[k+1],b=rgba[k+2];return rgba[k+3]>128&&r>210&&g<220&&r-g>25&&b-g<75&&b>140;}).found
        .filter(c=>c.count>8&&c.count<1000&&(c.x+c.width/2)>box.width*.3&&(c.x+c.width/2)<box.width*.6&&c.y>box.height*.35&&c.y<box.height*.85);
      const nose=pink.sort((a,b)=>Math.abs((a.x+a.width/2)/box.width-.43)-Math.abs((b.x+b.width/2)/box.width-.43))[0];if(!nose)throw new Error('Missing nose '+poses.length);
      poses.push({box,rgba,nose,source:{x:x0+box.x,y:y0+box.y,width:box.width,height:box.height}});
    }
  }
  const scale=scaleOverride||414/poses[0].box.width, report=[];
  await fs.mkdir(path.join(root,'keyframes-v2',name),{recursive:true});
  for(let i=0;i<poses.length;i++){
    const {box,rgba,nose,source}=poses[i],width=Math.round(box.width*scale),height=Math.round(box.height*scale);
    const left=Math.round(232-(nose.x+nose.width/2)*scale),top=478-height;
    if(left<2||top<2||left+width>510)throw new Error('Out of canvas: '+JSON.stringify({name,i,left,top,width,height}));
    const png=await sharp(rgba,{raw:{width:box.width,height:box.height,channels:4}}).resize(width,height).png().toBuffer();
    const output=path.join(root,'keyframes-v2',name,String(i).padStart(2,'0')+'.png');
    await sharp({create:{width:512,height:512,channels:4,background:'#00000000'}}).composite([{input:png,left,top}]).png().toFile(output);
    report.push({index:i,source,scale,left,top,width,height,nose:{x:232,y:top+(nose.y+nose.height/2)*scale}});
  }
  await fs.writeFile(path.join(root,'keyframes-v2',name,'alignment.json'),JSON.stringify(report,null,2));
  const layers=[];
  for(let i=0;i<poses.length;i++)layers.push({input:await sharp(path.join(root,'keyframes-v2',name,String(i).padStart(2,'0')+'.png')).resize(256,256).flatten({background:i%2?'#263345':'#f8f0eb'}).png().toBuffer(),left:(i%columns)*256,top:Math.floor(i/columns)*256});
  await sharp({create:{width:columns*256,height:rows*256,channels:4,background:'#f8f0eb'}}).composite(layers).png().toFile(path.join(root,'keyframes-v2',name,'contact.png'));
  console.log(JSON.stringify({name,scale,report},null,2));
}
split(process.argv[2]||'rise',Number(process.argv[3]||4),Number(process.argv[4]||3),Number(process.argv[5])||undefined).catch(e=>{console.error(e);process.exit(1);});
