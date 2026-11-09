// Shared deterministic timing for the exported demo and interactive preview.
// This candidate controller is independent of the desktop application's state.
(function(root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.WorkTransition = api;
})(typeof globalThis === 'object' ? globalThis : this, function() {
  const transitionMs = 400, workLoopMs = 9600, breathPeriodMs = 4800;
  const blinkStarts = [1250, 6700], blinkMs = 375, gazeSteps = 32;
  const clamp = x => Math.max(0, Math.min(1, x));
  const ease = x => { x = clamp(x); return x*x*x*(10+x*(-15+6*x)); };
  const mod = (x, d) => { const r = x % d; return r < 0 ? r + d : r; };
  function breathing(t) {
    const w = 2*Math.PI/breathPeriodMs;
    return { y:Math.cos(w*t)-1, velocity:-w*Math.sin(w*t), acceleration:-w*w*Math.cos(w*t) };
  }
  function blinkAt(t) {
    const local = mod(t, workLoopMs);
    for (const start of blinkStarts) {
      const elapsed = local-start;
      if (elapsed >= 0 && elapsed < blinkMs) {
        const phase = elapsed < 140 ? 32*elapsed/140 : elapsed < 157 ? 32 : 32+32*(elapsed-157)/218;
        return { index:Math.round(phase), elapsed, remaining:blinkMs-elapsed };
      }
    }
    return null;
  }
  // Continue the original breathing clock while smoothly reducing its range.
  // The quintic envelope matches position/velocity/acceleration at both ends.
  function exitOffset(workTime, elapsed) {
    return breathing(workTime+Math.max(0,elapsed)).y*(1-ease(elapsed/transitionMs));
  }
  function createController() {
    let state='idle', since=0, workStart=0, pendingExitAt=null, exitWorkTime=0;
    function advance(now) {
      if (state==='entering' && now >= since+transitionMs) {
        workStart=since+transitionMs; since=workStart; state='working';
      }
      if (state==='working' && pendingExitAt!==null && now>=pendingExitAt) {
        since=pendingExitAt; exitWorkTime=since-workStart; pendingExitAt=null; state='exiting';
      }
      if (state==='exiting' && now>=since+transitionMs) {
        since+=transitionMs; state='idle';
      }
    }
    function begin(now) {
      advance(now);
      if (state!=='idle') return false;
      state='entering';since=now;pendingExitAt=null;return true;
    }
    function finish(now) {
      advance(now);
      if (state!=='working' || pendingExitAt!==null) return false;
      const blink=blinkAt(now-workStart);
      pendingExitAt=now+(blink?blink.remaining:0);
      advance(now);return true;
    }
    function sample(now) {
      advance(now);
      if(state==='idle') return {state,gazeIndex:0,blinkIndex:null,propsOpacity:0,dy:0,waitingForBlink:false};
      if(state==='entering') {
        const q=ease((now-since)/transitionMs);
        return {state,gazeIndex:Math.round(gazeSteps*q),blinkIndex:null,propsOpacity:q,dy:0,waitingForBlink:false};
      }
      if(state==='working') {
        const workTime=now-workStart, blink=blinkAt(workTime);
        return {state,gazeIndex:gazeSteps,blinkIndex:blink?.index??null,propsOpacity:1,dy:breathing(workTime).y,workTime,waitingForBlink:pendingExitAt!==null};
      }
      const elapsed=now-since,q=ease(elapsed/transitionMs);
      return {state,gazeIndex:Math.round(gazeSteps*(1-q)),blinkIndex:null,propsOpacity:1-q,dy:exitOffset(exitWorkTime,elapsed),exitWorkTime,waitingForBlink:false};
    }
    return {begin,finish,sample};
  }
  const demo = {idleMs:1000,enterMs:transitionMs,exitRequestWorkMs:6800,exitStartWorkMs:7075,durationMs:9900};
  function demoAt(time) {
    const t=Math.max(0,Math.min(demo.durationMs,time)),c=createController();
    if(t>=demo.idleMs)c.begin(demo.idleMs);
    const requestAt=demo.idleMs+transitionMs+demo.exitRequestWorkMs;
    if(t>=requestAt)c.finish(requestAt);
    return c.sample(t);
  }
  return {transitionMs,workLoopMs,breathPeriodMs,blinkStarts,blinkMs,gazeSteps,ease,breathing,blinkAt,exitOffset,createController,demo,demoAt};
});
