// Offline motion interpolation using the upstream RIFE model, not frame blending.
// Usage: node tools/motion-interpolate.cjs input.json
// input.json: { frames:[absolute PNG paths], outputDirectory, factor:16, gpu:0 }
// Frames must share one canvas/ground anchor and be equally spaced key poses.
// RIFE handles RGB. White/black matte passes recover a transparent RGBA output.
const fs = require('node:fs/promises');
const path = require('node:path');
const { promisify } = require('node:util');
const { execFile } = require('node:child_process');
const sharp = require('sharp');
const exec = promisify(execFile);
const repo = path.resolve(__dirname,'..');
const tool = path.join(repo,'.tools/motion-rife/rife-ncnn-vulkan-20221029-windows');
const median=(a,b,c)=>a+b+c-Math.min(a,b,c)-Math.max(a,b,c);

async function interpolate(config) {
  const frames=config.frames.map(f=>path.resolve(f));
  if(frames.length<2)throw Error('At least two keyframes required');
  const factor=config.factor??16;
  if(!Number.isInteger(factor)||factor<2||factor>1024)throw Error('factor must be an integer from 2 through 1024');
  const output=path.resolve(config.outputDirectory), work=path.join(output,'work');
  await fs.mkdir(output,{recursive:true});
  const first=await sharp(frames[0]).metadata(), width=first.width, height=first.height;
  const count=(frames.length-1)*factor+1;
  // Upstream main.cpp (20221029), lines 713–718: fx=i*inputCount/numframe.
  // Ask for N*factor, then discard the repeated final-pose tail. Requesting
  // (N-1)*factor+1 directly would place source keyframes at the wrong times.
  const rifeCount=frames.length*factor;
  const inputs={},outputs={};
  const engine=config.engine??'rife';
  if(!['rife','ffmpeg'].includes(engine))throw Error('engine must be rife or ffmpeg');
  const stagedFrames=engine==='ffmpeg'?[frames[0],frames[0],...frames,frames.at(-1),frames.at(-1)]:frames;
  for(const matte of ['black','white']){
    inputs[matte]=path.join(work,matte+'-input');outputs[matte]=path.join(work,matte+'-output');
    await fs.mkdir(inputs[matte],{recursive:true});await fs.mkdir(outputs[matte],{recursive:true});
    for(let i=0;i<stagedFrames.length;i++){
      const meta=await sharp(stagedFrames[i]).metadata();
      if(meta.width!==width||meta.height!==height)throw Error(`Canvas mismatch: ${stagedFrames[i]}`);
      await sharp(stagedFrames[i]).flatten({background:matte==='black'?'#000000':'#ffffff'}).png().toFile(path.join(inputs[matte],`${String(i).padStart(8,'0')}.png`));
    }
  }
  const started=Date.now(), logs={};
  for(const matte of ['black','white']){
    const args=['-i',inputs[matte],'-o',outputs[matte],'-m',path.join(tool,'rife-v4.6'),'-n',String(rifeCount),'-g',String(config.gpu??0),'-j','2:2:2','-f','%08d.png'];
    if(config.temporalTta)args.push('-z');
    let result;
    if(engine==='rife')result=await exec(path.join(tool,'rife-ncnn-vulkan.exe'),args,{cwd:tool,windowsHide:true,maxBuffer:16*1024*1024});
    else result=await exec('ffmpeg',['-hide_banner','-loglevel','warning','-y','-framerate','1','-i',path.join(inputs[matte],'%08d.png'),'-vf',`format=yuv444p,minterpolate=fps=${factor}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1:scd=none,trim=start_frame=${2*factor}:end_frame=${2*factor+count},setpts=PTS-STARTPTS`,'-start_number','0',path.join(outputs[matte],'%08d.png')],{windowsHide:true,maxBuffer:16*1024*1024});
    logs[matte]=result.stdout+result.stderr;
    await fs.writeFile(path.join(work,matte+'.log'),logs[matte]);
    console.log(`${engine} ${matte} pass: ${count} frames`);
  }
  const blackFiles=(await fs.readdir(outputs.black)).filter(f=>f.endsWith('.png')).sort();
  const whiteFiles=(await fs.readdir(outputs.white)).filter(f=>f.endsWith('.png')).sort();
  const expected=engine==='rife'?rifeCount:count;
  if(blackFiles.length!==expected||whiteFiles.length!==expected)throw Error(`Unexpected interpolation output count ${blackFiles.length}/${whiteFiles.length}/${expected}`);
  const keyframeChecks=[];
  if(engine==='rife'){
    for(let k=0;k<frames.length;k++){
      const source=await sharp(path.join(inputs.black,`${String(k).padStart(8,'0')}.png`)).removeAlpha().raw().toBuffer();
      const result=await sharp(path.join(outputs.black,blackFiles[k*factor])).removeAlpha().raw().toBuffer();
      const equal=source.equals(result);
      keyframeChecks.push({inputIndex:k,outputIndex:k*factor,exactRgbMatch:equal});
      if(!equal)throw Error(`RIFE time mapping failed at keyframe ${k}; refusing forced-frame discontinuity`);
    }
  }
  let partialAlpha=0,transparent=0;const outputFiles=[];
  for(let i=0;i<count;i++){
    const destination=path.join(output,`${String(i).padStart(5,'0')}.png`);outputFiles.push(destination);
    if(i%factor===0){await fs.copyFile(frames[i/factor],destination);continue;}
    const b=await sharp(path.join(outputs.black,blackFiles[i])).removeAlpha().raw().toBuffer();
    const w=await sharp(path.join(outputs.white,whiteFiles[i])).removeAlpha().raw().toBuffer();
    const rgba=Buffer.alloc(width*height*4);
    for(let p=0;p<width*height;p++){
      const k=p*3,o=p*4;
      let a=Math.max(0,Math.min(255,255-median(w[k]-b[k],w[k+1]-b[k+1],w[k+2]-b[k+2])));
      if(a<=3){transparent++;continue;}if(a>=252)a=255;else partialAlpha++;
      rgba[o+3]=a;
      for(let c=0;c<3;c++)rgba[o+c]=Math.min(255,Math.round(b[k+c]*255/a));
    }
    await sharp(rgba,{raw:{width,height,channels:4}}).png().toFile(destination);
  }
  const report={engine:engine==='rife'?'nihui/rife-ncnn-vulkan':'FFmpeg minterpolate',release:engine==='rife'?'20221029':'installed',model:engine==='rife'?'rife-v4.6':'mci/aobmc/bidir/vsbmc',method:'black/white matte motion interpolation; source keyframes preserved exactly',width,height,keyframes:frames.length,keyframeChecks,factor,outputFrames:count,elapsedSeconds:(Date.now()-started)/1000,partialAlpha,transparent,outputFiles};
  await fs.writeFile(path.join(output,'interpolation-report.json'),JSON.stringify(report,null,2)+'\n');
  return report;
}
module.exports={interpolate};
if(require.main===module){
  if(!process.argv[2])throw Error('Provide the path to a JSON job');
  fs.readFile(process.argv[2],'utf8').then(JSON.parse).then(interpolate).then(r=>console.log(JSON.stringify({outputFrames:r.outputFrames,elapsedSeconds:r.elapsedSeconds}))).catch(e=>{console.error(e);process.exitCode=1;});
}
