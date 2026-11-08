'use strict';
// Independent v3 preview timing. Stage 640 x 640, cat sprite 512 x 512 at
// (64,100). Eye size is owned by the rig; this module supplies direction only.
// v2 timing was rejected by the user: entry was too quick and the final
// upward stare lasted too long. No v2 files or runtime files are modified.
const duration=10.0,fps=60;
const clamp=(x,a=0,b=1)=>Math.max(a,Math.min(b,x));
const smooth=x=>{x=clamp(x);return x<=0?0:x>=1?1:clamp(x*x*x*(10+x*(-15+6*x)));};
const mix=(a,b,u)=>a+(b-a)*u;
const timing={noticeStart:1.05,noticeEnd:2.35,rightHoldEnd:3.0,pawPeak:6.20,flyGone:8.50,returnStart:8.55,returnEnd:9.45};

// [time, stage x, stage y, x/y velocity, x/y acceleration]. Every junction
// shares both derivatives. The cross-body passage stays below eyes and nose.
// Provisional reach: sprite tip (120,386); flight near sprite (100,365).
// The body author can refine these close knots after freezing the pose.
const flight=[
 [0.00,486,512,   0,   0,   0,   0],
 [0.55,486,512,   0,   0,   0,   0],
 [1.10,529,496,  90, -50, -25, -50],
 [1.65,560,447,   0, -40, -32,  60],
 [2.20,558,426,  -6,   0,   0,  20],
 [3.00,547,438, -33,  28, -75,  70],
 [3.55,500,492,-128,  55, -35,-105],
 [4.20,407,514,-150,   0, -15, -35],
 [4.85,303,505,-163, -35,   0, -30],
 [5.30,227,499,-151, -25,  65, -45],
 [5.65,181,486, -83, -45, 160, -35],
 [5.90,166,471, -15, -35, 100,  80],
 [6.10,164,467,   0, -10,   0,-180],
 [6.25,165,451,  12,-162,  35,-110],
 [6.85,174,331,  40,-210,  45, -10],
 [7.55,218,182,  56,-215, -15, -10],
 [8.20,249, 40,  30,-220, -20,   0],
 [8.50,257,-26,  22,-220, -15,   0],
 [8.65,260,-59,  18,-220,   0,   0],
];
function axis(p0,p1,v0,v1,a0,a1,h,u){
 const c0=p0,c1=v0*h,c2=.5*a0*h*h;
 const d=p1-c0-c1-c2,e=v1*h-c1-2*c2,f=a1*h*h-2*c2;
 const c3=10*d-4*e+.5*f,c4=-15*d+7*e-f,c5=6*d-3*e+.5*f;
 return {p:c0+u*(c1+u*(c2+u*(c3+u*(c4+u*c5)))),v:(c1+u*(2*c2+u*(3*c3+u*(4*c4+u*5*c5))))/h};
}
function positionAt(time){
 const t=clamp(time,0,flight.at(-1)[0]);let i=0;
 while(i<flight.length-2&&t>flight[i+1][0])i++;
 const a=flight[i],b=flight[i+1],h=b[0]-a[0],u=clamp((t-a[0])/h);
 const x=axis(a[1],b[1],a[3],b[3],a[5],b[5],h,u),y=axis(a[2],b[2],a[4],b[4],a[6],b[6],h,u);
 return{x:x.p,y:y.p,vx:x.v,vy:y.v};
}
function targetAt(t){
 const p=positionAt(Math.min(timing.flyGone,t-.09)),dx=p.x-299;
 const depth=mix(64,58,smooth((t-3.05)/2.55));
 const rise=Math.max(0,467-p.y)*smooth((t-6.06)/.28);
 const u=.36*dx/Math.sqrt(dx*dx+depth*depth+.8*rise*rise);
 // The low crossing gets at most a small additional downward glance. At the
 // close left paw, v stays near .10 rather than looking far below the target.
 const crossPitch=.10+.025*Math.tanh((p.y-480)/50)*smooth((t-3.0)/.55);
 // A shallow upward range replaces the rejected v2 v=-.32 stare. The .10
 // close-paw starting pitch falls smoothly as the insect begins to ascend.
 const upPitch=mix(.10,-.15,smooth((467-p.y)/235));
 const v=mix(crossPitch,upPitch,smooth((t-6.06)/.28));
 return{u:clamp(u,-.35,.35),v:clamp(v,-.15,.13)};
}
function critical(state,target,omega,dt){
 const d=state.p-target,j=state.v+omega*d,e=Math.exp(-omega*dt);
 return{p:target+(d+j*dt)*e,v:(state.v-omega*j*dt)*e};
}
// Direction is stable before the gaze engages. The ONLY entry control is the
// 1.30 s engagement curve below; there is no short notice pulse or a second
// multiplied ramp that delays motion then accelerates it near the endpoint.
const hz=480,dt=1/hz,look=[];
const first=targetAt(0);let uState={p:first.u,v:0},vState={p:first.v,v:0};
for(let i=0;i<=Math.ceil(duration*hz);i++){
 const t=i/hz;if(i){const target=targetAt(t);uState=critical(uState,target.u,16,dt);vState=critical(vState,target.v,16,dt);}
 look.push([uState.p,vState.p,uState.v,vState.v]);
}
function gazeAt(t){
 if(t<=timing.noticeStart||t>=timing.returnEnd)return{u:0,v:0,engagement:0};
 const q=t*hz,i=Math.min(look.length-2,Math.floor(q)),f=q-i,a=look[i],b=look[i+1];
 const h00=2*f**3-3*f**2+1,h10=f**3-2*f**2+f,h01=-2*f**3+3*f**2,h11=f**3-f**2;
 const enter=smooth((t-timing.noticeStart)/(timing.noticeEnd-timing.noticeStart));
 const back=1-smooth((t-timing.returnStart)/(timing.returnEnd-timing.returnStart));
 const u=(h00*a[0]+h10*dt*a[2]+h01*b[0]+h11*dt*b[2])*back;
 const v=(h00*a[1]+h10*dt*a[3]+h01*b[1]+h11*dt*b[3])*back;
 return{u:clamp(u,-.35,.35),v:clamp(v,-.15,.13),engagement:enter*back};
}
function sample(time){
 const t=clamp(Number(time)||0,0,duration),p=positionAt(t);
 const prepare=smooth((t-5.10)/.75)*(1-smooth((t-6.38)/.92));
 const paw=smooth((t-5.55)/.65)*(1-smooth((t-6.32)/.90));
 const mouth=smooth((t-5.30)/.55)*(1-smooth((t-6.35)/.65));
 const opacity=smooth((t-.55)/.38)*(1-smooth((t-8.25)/.25));
 const scale=mix(.65,1,smooth((t-1.55)/3.90))*mix(1,.48,smooth((t-6.20)/2.45));
 const phase=t<timing.noticeStart?'emerge-behind':t<timing.noticeEnd?'notice-gradually':t<3.0?'hold-right-gaze':t<5.10?'cross-right-to-left':t<5.55?'prepare-left':t<6.20?'tap-left':t<timing.flyGone?'escape-up':t<timing.returnStart?'brief-follow-through':t<timing.returnEnd?'return-to-idle':'idle';
 return{fly:{x:p.x,y:p.y,opacity,scale,behind:t<1.63,angle:Math.atan2(p.vy,p.vx)},gaze:gazeAt(t),body:{prepare,paw,mouth},prepare,paw,mouth,phase};
}
module.exports={duration,fps,sample};
if(require.main===module){
 const times=[0,1.05,1.30,1.70,2.10,2.35,2.80,4.20,5.65,6.10,6.20,6.85,7.55,8.50,8.55,9.00,9.45,10];
 console.log(JSON.stringify({duration,fps,timing,samples:times.map(t=>({t,...sample(t)}))},null,2));
}
