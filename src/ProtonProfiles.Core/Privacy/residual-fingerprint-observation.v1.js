function collectResidualFingerprintObservation(target=globalThis) {
  try {
    const n=target.navigator;
    const navigatorApis=Object.fromEntries(['getBattery','getGamepads','mediaDevices','mediaCapabilities','serviceWorker'].map(k=>[k,n[k]!==undefined]));
    const constructors=Object.fromEntries(['BatteryManager','Gamepad','GamepadButton','GamepadEvent','GamepadHapticActuator','MediaDevices','MediaDeviceInfo','InputDeviceInfo','MediaCapabilities','Accelerometer','LinearAccelerationSensor','GravitySensor','Gyroscope','AbsoluteOrientationSensor','RelativeOrientationSensor','IdleDetector','ScreenDetails','ScreenDetailed','MemoryInfo','ServiceWorker','ServiceWorkerContainer','ServiceWorkerRegistration'].map(k=>[k,target[k]!==undefined]));
    let localFontConstructionBlocked=null;
    if (typeof target.FontFace==='function') {
      try {new target.FontFace('ProtonProfilesBlockedLocalProbe','local("Arial")');localFontConstructionBlocked=false;}
      catch(e) {localFontConstructionBlocked=e?.name==='SecurityError';}
    }
    const keyboard=n.keyboard; // Presence only; never request a layout map or keyboard lock.
    const descriptor=Object.getOwnPropertyDescriptor(n,'deviceMemory');
    return {status:'Observed',documentContext:!!target.document,deviceMemory:n.deviceMemory??null,
      scriptRestriction:descriptor?.value===8 && descriptor.writable===false && descriptor.configurable===false,
      navigatorApis,constructors,
      canvasTextMetrics:{html:typeof target.CanvasRenderingContext2D?.prototype.measureText==='function',
        offscreen:typeof target.OffscreenCanvasRenderingContext2D?.prototype.measureText==='function'},
      fontSetCheckAvailable:typeof target.FontFaceSet?.prototype.check==='function',
      coarseClocks:collectCoarseClockObservation(target),mathPow:collectMathPowObservation(target),
      workArea:collectWorkAreaObservation(target),
      keyboardLayout:{keyboard:keyboard!==undefined,Keyboard:target.Keyboard!==undefined,KeyboardLayoutMap:target.KeyboardLayoutMap!==undefined,
        getLayoutMap:typeof keyboard?.getLayoutMap==='function',lock:typeof keyboard?.lock==='function',unlock:typeof keyboard?.unlock==='function'},
      webCodecs:Object.fromEntries(['AudioDecoder','VideoDecoder','AudioEncoder','VideoEncoder','AudioData','VideoFrame','EncodedAudioChunk','EncodedVideoChunk'].map(k=>[k,target[k]!==undefined])),
      performanceMemoryAvailable:target.performance?.memory!==undefined,
      storageEstimateAvailable:typeof n.storage?.estimate==='function',getScreenDetailsAvailable:typeof target.getScreenDetails==='function',localFontConstructionBlocked};
  } catch {return {status:'NotPerformed'};}
}

function collectWorkAreaObservation(target=globalThis) {
  try {
    if(!target.screen)return {status:'NotApplicable'};
    const s=target.screen;
    return {status:'Observed',normalized:s.availWidth===s.width&&s.availHeight===s.height&&s.availLeft===0&&s.availTop===0
      &&['screenX','screenY','screenLeft','screenTop'].every(k=>target[k]===0)};
  }catch{return {status:'NotPerformed'};}
}
function collectCoarseClockObservation(target=globalThis) {
  try {
    const p=target.performance,q=100,aligned=v=>Number.isFinite(v)&&v%q===0;
    const now=p.now(),epoch=target.Date.now(),date=new target.Date().getTime();
    const marker=Symbol.for('CoarseClockQuantum');
    let entry=null,serialized=null;
    if(typeof p.mark==='function'&&typeof p.clearMarks==='function') {
      const name='clock-check-'+Math.random().toString(36).slice(2);
      try {const mark=p.mark(name,{startTime:133.375});entry=aligned(mark.startTime);serialized=aligned(mark.toJSON().startTime);}
      finally {p.clearMarks(name);}
    }
    const event=typeof target.Event==='function'?aligned(new target.Event('clock-check').timeStamp):null;
    const temporal=typeof target.Temporal?.Now?.instant==='function'?target.Temporal.Now.instant().epochNanoseconds%100000000n===0n:null;
    return {status:'Observed',quantumMs:p.now[marker]===q?q:null,nowAligned:aligned(now),originAligned:aligned(p.timeOrigin),dateNowAligned:aligned(epoch),dateConstructorAligned:aligned(date),
      eventAligned:event,entryAligned:entry,serializedEntryAligned:serialized,temporalAligned:temporal,
      animationFrameWrapped:typeof target.requestAnimationFrame==='function'?target.requestAnimationFrame[marker]===q:null};
  }catch{return {status:'NotPerformed'};}
}

function residualFingerprintOutcome(o,exceptions=[]) {
  if(o?.status!=='Observed' || typeof o.scriptRestriction!=='boolean' || !Number.isFinite(o.deviceMemory))return 'Unavailable';
  const navigatorKeys=['getBattery','getGamepads','mediaDevices','mediaCapabilities','serviceWorker'];
  const constructorKeys=['BatteryManager','Gamepad','GamepadButton','GamepadEvent','GamepadHapticActuator','MediaDevices','MediaDeviceInfo','InputDeviceInfo','MediaCapabilities','Accelerometer','LinearAccelerationSensor','GravitySensor','Gyroscope','AbsoluteOrientationSensor','RelativeOrientationSensor','IdleDetector','ScreenDetails','ScreenDetailed','MemoryInfo','ServiceWorker','ServiceWorkerContainer','ServiceWorkerRegistration'];
  const codecKeys=['AudioDecoder','VideoDecoder','AudioEncoder','VideoEncoder','AudioData','VideoFrame','EncodedAudioChunk','EncodedVideoChunk'];
  const features={getBattery:'Battery',BatteryManager:'Battery',getGamepads:'Gamepads',Gamepad:'Gamepads',GamepadButton:'Gamepads',GamepadEvent:'Gamepads',GamepadHapticActuator:'Gamepads',
    mediaDevices:'MediaDevices',MediaDevices:'MediaDevices',MediaDeviceInfo:'MediaDevices',InputDeviceInfo:'MediaDevices',mediaCapabilities:'MediaCapabilities',MediaCapabilities:'MediaCapabilities',
    serviceWorker:'ServiceWorkers',ServiceWorker:'ServiceWorkers',ServiceWorkerContainer:'ServiceWorkers',ServiceWorkerRegistration:'ServiceWorkers'};
  const entries=[...navigatorKeys.map(k=>[o.navigatorApis?.[k],features[k]]),...constructorKeys.map(k=>[o.constructors?.[k],features[k]]),
    ...codecKeys.map(k=>[o.webCodecs?.[k],'WebCodecs']),...['keyboard','Keyboard','KeyboardLayoutMap','getLayoutMap','lock','unlock'].map(k=>[o.keyboardLayout?.[k],'KeyboardLayout']),
    [o.canvasTextMetrics?.html,'CanvasTextMetrics'],[o.canvasTextMetrics?.offscreen,'CanvasTextMetrics'],[o.fontSetCheckAvailable,'LocalFonts'],
    [o.performanceMemoryAvailable,null],[o.storageEstimateAvailable,'StorageEstimate'],[o.getScreenDetailsAvailable,null]];
  if(entries.some(([v])=>typeof v!=='boolean') || ![true,false,null].includes(o.localFontConstructionBlocked))return 'Unavailable';
  const clock=coarseClockOutcome(o.coarseClocks),area=workAreaOutcome(o.workArea,o.documentContext);
  if(!exceptions.includes('HighResolutionTimers')&&clock==='Unavailable'||!exceptions.includes('ScreenWorkArea')&&area==='Unavailable')return 'Unavailable';
  return o.deviceMemory===8 && o.scriptRestriction && entries.every(([v,feature])=>v===false || exceptions.includes(feature))
    && (o.localFontConstructionBlocked!==false || exceptions.includes('LocalFonts'))
    && (exceptions.includes('HighResolutionTimers')||clock==='Verified')&&(exceptions.includes('ScreenWorkArea')||area==='Verified') ? 'Verified':'Violation';
}
function coarseClockOutcome(o) {
  if(o?.status!=='Observed'||![100,null].includes(o.quantumMs))return 'Unavailable';
  const required=['nowAligned','originAligned','dateNowAligned','dateConstructorAligned'],optional=['eventAligned','entryAligned','serializedEntryAligned','temporalAligned','animationFrameWrapped'];
  if(required.some(k=>typeof o[k]!=='boolean')||optional.some(k=>![true,false,null].includes(o[k])))return 'Unavailable';
  return o.quantumMs===100&&required.every(k=>o[k])&&optional.every(k=>o[k]!==false)?'Verified':'Violation';
}
function workAreaOutcome(o,documentContext=false) {
  if(o?.status==='NotApplicable')return documentContext===false?'Verified':'Unavailable';
  if(o?.status!=='Observed'||typeof o.normalized!=='boolean')return 'Unavailable';
  return o.normalized?'Verified':'Violation';
}
function collectMathPowObservation(target=globalThis) {
  try {
    // V8 15.3 LLVM pow vectors: compiled pinned LLVM source, FMA/non-FMA and 150/250-digit oracle.
    // Inputs never depend on user data. Native Math is not wrapped or rounded.
    const vectors=[[0.9955197777794697,2.887188198044896,"3fef967b5aa8b974"],[404.8842551004796,1.6121421288698912,"40cf3286f4ca2958"],[0.13741862589758608,-2.433998903259635,"405f5407e07e0932"],[3221.1334551026525,1.9946165662258863,"4162f2ad4c9e14d6"],[8.460012025768735e-05,1.4791039284318686,"3eafc314ad19faa7"],[0.0017892136866214281,-0.3275273907929659,"401fc2b51a4228db"],[0.029084165798707786,0.8993725348263979,"3fa5421457a49441"],[133.15099494324576,-1.916966063901782,"3f1631c56724ff44"],[10373.998292561495,3.289665902033448,"42ad93a9ee2439f0"],[0.1250828916451598,1.5663068536669016,"3fa3bbc8df1da607"],[314.949933812676,2.6227630097419024,"414b36ac8da32e9c"],[3.4940818036176144e-07,0.1264579650014639,"3fc387cdcec1b63a"],[2436.1811908173095,1.275183891877532,"40d4575be716f225"],[1.0287625332060378e-08,-0.6829390320926905,"411167c2916e2fba"],[0.0016891972674491597,0.22265701554715633,"3fcee5f22ebb0553"],[0.0004098696563518409,1.9867252353578806,"3e8901e1f9db84cd"]];
    const data=new target.DataView(new target.ArrayBuffer(8));
    const bits=v=>{data.setFloat64(0,v,false);return data.getBigUint64(0,false).toString(16).padStart(16,'0');};
    const values=vectors.map(([x,y])=>bits(target.Math.pow(x,y)));
    return {status:'Observed',native:target.Function.prototype.toString.call(target.Math.pow).includes('[native code]'),
      referenceMatches:values.every((value,i)=>value===vectors[i][2]),vectors:values.length,values};
  }catch{return {status:'NotPerformed'};}
}

function mathPowOutcome(o) {
  const reference=["3fef967b5aa8b974","40cf3286f4ca2958","405f5407e07e0932","4162f2ad4c9e14d6","3eafc314ad19faa7","401fc2b51a4228db","3fa5421457a49441","3f1631c56724ff44","42ad93a9ee2439f0","3fa3bbc8df1da607","414b36ac8da32e9c","3fc387cdcec1b63a","40d4575be716f225","411167c2916e2fba","3fcee5f22ebb0553","3e8901e1f9db84cd"];
  if(o?.status!=='Observed'||o.vectors!==reference.length||!Array.isArray(o.values)||o.values.length!==reference.length||!o.values.every(v=>typeof v==='string'&&/^[a-f0-9]{16}$/.test(v))||typeof o.native!=='boolean'||typeof o.referenceMatches!=='boolean')return 'Unavailable';
  return o.native&&o.referenceMatches&&o.values.every((v,i)=>v===reference[i])?'Verified':'Violation';
}
