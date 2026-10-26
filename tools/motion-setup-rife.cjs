// Fetch only RIFE 4.6, the executable, runtime and license from the official release.
// Archive ranges are fixed to this release and entries are checked against ZIP CRC32.
const fs = require('node:fs/promises');
const path = require('node:path');
const { promisify } = require('node:util');
const { execFile } = require('node:child_process');
const { inflateRawSync, crc32 } = require('node:zlib');
const { createHash } = require('node:crypto');
const exec = promisify(execFile);
const root = path.resolve(__dirname, '..', '.tools', 'motion-rife');
const url = 'https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-windows.zip';
const length = 431540241, tailStart = length - 65536;
let redirectPromise;

function resolveDownloadUrl(){
  redirectPromise??=(async()=>{
    const headerPath=path.join(root,'redirect-headers.txt');
    await exec('curl.exe',['-I','--fail','--silent','--show-error','--retry','2','--max-time','30',url,'-o',headerPath],{windowsHide:true});
    const headers=await fs.readFile(headerPath,'utf8');
    const location=headers.match(/^Location:\s*(.+)$/mi)?.[1]?.trim();
    if(!location||!location.startsWith('https://release-assets.githubusercontent.com/'))throw Error('Unexpected official release redirect');
    return location;
  })();
  return redirectPromise;
}

async function fetchRange(start, end, target) {
  const cached=await fs.readFile(target).catch(()=>null);
  if(cached&&cached.length===end-start+1)return cached;
  if(cached&&cached.length>0&&cached.length<end-start+1){
    const rest=await fetchRange(start+cached.length,end,target+'.remainder');
    const complete=Buffer.concat([cached,rest]);await fs.writeFile(target,complete);return complete;
  }
  if(end-start>131071){
    const parts=[];
    for(let p=start;p<=end;p+=131072)parts.push({start:p,end:Math.min(end,p+131071)});
    let next=0;
    const data=new Array(parts.length);
    await Promise.all(Array.from({length:Math.min(8,parts.length)},async()=>{
      while(next<parts.length){const i=next++;const part=parts[i];data[i]=await fetchRange(part.start,part.end,target+'.'+i);}
    }));
    return Buffer.concat(data);
  }
  if(end-start+1===131072){
    const [a,b]=await Promise.all([fetchRange(start,start+65535,target+'.a'),fetchRange(start+65536,end,target+'.b')]);
    const complete=Buffer.concat([a,b]);await fs.writeFile(target,complete);return complete;
  }
  const downloadUrl=await resolveDownloadUrl();
  const networkOptions=process.env.RIFE_DOWNLOAD_IPV4?['--resolve',`release-assets.githubusercontent.com:443:${process.env.RIFE_DOWNLOAD_IPV4}`]:[];
  await exec('curl.exe', ['-L', '--fail', '--retry', '2', '--retry-all-errors', '--connect-timeout', '10', '--max-time', '60',...networkOptions,'--silent', '--show-error', '--range', `${start}-${end}`, downloadUrl, '-o', target], { windowsHide:true });
  const data = await fs.readFile(target);
  if(data.length !== end-start+1) throw Error(`Server did not honor range ${start}-${end}: ${data.length} bytes`);
  return data;
}

(async()=>{
  await fs.mkdir(root,{recursive:true});
  const tailPath=path.join(root,'archive-tail.bin');
  const tail=await fs.readFile(tailPath).catch(()=>fetchRange(tailStart,length-1,tailPath));
  const wanted=[];
  for(let p=0;p<tail.length-46;p++) {
    if(tail.readUInt32LE(p)!==0x02014b50)continue;
    const n=tail.readUInt16LE(p+28),e=tail.readUInt16LE(p+30),c=tail.readUInt16LE(p+32);
    const name=tail.subarray(p+46,p+46+n).toString().split('/').slice(1).join('/');
    if(name==='LICENSE'||name==='README.md'||name==='rife-ncnn-vulkan.exe'||name==='vcomp140.dll'||/^rife-v4\.6\/.+/.test(name))
      wanted.push({name, size:tail.readUInt32LE(p+24), compressed:tail.readUInt32LE(p+20), offset:tail.readUInt32LE(p+42), crc:tail.readUInt32LE(p+16), method:tail.readUInt16LE(p+10)});
    p+=45+n+e+c;
  }
  const verified=[];
  for(const entry of wanted){
    const destination=path.join(root,'rife-ncnn-vulkan-20221029-windows',entry.name);
    const existing=await fs.readFile(destination).catch(()=>null);
    if(existing&&existing.length===entry.size&&crc32(existing)===entry.crc){verified.push({...entry,sha256:createHash('sha256').update(existing).digest('hex')});console.log(`Verified existing ${entry.name}`);continue;}
    const chunkPath=path.join(root,entry.name.replace(/[\/]/g,'_')+'.part');
    // Read the local header and compressed member in a single bounded range.
    const chunk=await fetchRange(entry.offset,entry.offset+entry.compressed+255,chunkPath);
    const header=chunk.subarray(0,30);
    if(header.readUInt32LE(0)!==0x04034b50)throw Error('Invalid ZIP local header');
    const dataStart=30+header.readUInt16LE(26)+header.readUInt16LE(28);
    if(dataStart>256)throw Error('Unexpectedly long ZIP local header');
    const compressed=chunk.subarray(dataStart,dataStart+entry.compressed);
    const data=entry.method===8?inflateRawSync(compressed):compressed;
    if(data.length!==entry.size||crc32(data)!==entry.crc)throw Error(`ZIP validation failed: ${entry.name}`);
    await fs.mkdir(path.dirname(destination),{recursive:true});await fs.writeFile(destination,data);
    verified.push({...entry,sha256:createHash('sha256').update(data).digest('hex')});
    await fs.unlink(chunkPath).catch(()=>{});console.log(`Verified ${entry.name} (${data.length} bytes)`);
  }
  await fs.writeFile(path.join(root,'provenance.json'),JSON.stringify({url,release:'20221029',downloadedAt:new Date().toISOString(),archiveBytes:length,files:verified},null,2)+'\n');
})().catch(e=>{console.error(e.message);process.exitCode=1;});
