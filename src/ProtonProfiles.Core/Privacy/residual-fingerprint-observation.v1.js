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
    // Fixed fdlibm reference vectors, checked with V8 --no-use-std-math-pow.
    // Inputs never depend on user data. Native Math is not wrapped or rounded.
    const vectors=[[0.08817491032130563,0.6182495858520269,"3fcc8576b9821290"],[217738916.7713856,3.3721094951033592,"45c51f96ddfe1294"],[0.17271508647487982,-2.9896778780966997,"4067d365369167d6"],[5.567815684210494,3.9025653079152107,"408967e63d6967c0"],[0.07725243589924193,2.6057992167770863,"3f54ba2f365f4928"],[837743.1123589359,-2.41935889236629,"3cf50efe4c0f072a"],[0.564511022355228,1.4834998026490211,"3fdb66f934e8c7a4"],[500.3251252485142,0.7164688613265753,"405578cdac450aa4"],[0.6928457835058637,-0.5896763671189547,"3ff3dd7d668fcde4"],[0.733498245421778,0.18536948412656784,"3fee369efcbec66e"],[4147295.727081294,0.7062664981931448,"40e70b81d193cd0e"],[1.5601331201139639,3.4310877099633217,"4012665d63588a80"],[0.15579948562316873,-0.6106275226920843,"4008e5671ac17260"],[299782.9593383668,-0.8033249229192734,"3f04e38b44ae1118"],[839946.6386989702,-0.4113036133348942,"3f6df8f995c43c2c"],[0.8211717955998948,-2.561578817665577,"3ffa80e859564e22"]];
    const data=new target.DataView(new target.ArrayBuffer(8));
    const bits=v=>{data.setFloat64(0,v,false);return data.getBigUint64(0,false).toString(16).padStart(16,'0');};
    const values=vectors.map(([x,y])=>bits(target.Math.pow(x,y)));
    return {status:'Observed',native:target.Function.prototype.toString.call(target.Math.pow).includes('[native code]'),
      referenceMatches:values.every((value,i)=>value===vectors[i][2]),vectors:values.length,values};
  }catch{return {status:'NotPerformed'};}
}

function mathPowOutcome(o) {
  const reference=["3fcc8576b9821290","45c51f96ddfe1294","4067d365369167d6","408967e63d6967c0","3f54ba2f365f4928","3cf50efe4c0f072a","3fdb66f934e8c7a4","405578cdac450aa4","3ff3dd7d668fcde4","3fee369efcbec66e","40e70b81d193cd0e","4012665d63588a80","4008e5671ac17260","3f04e38b44ae1118","3f6df8f995c43c2c","3ffa80e859564e22"];
  if(o?.status!=='Observed'||o.vectors!==reference.length||!Array.isArray(o.values)||o.values.length!==reference.length||!o.values.every(v=>typeof v==='string'&&/^[a-f0-9]{16}$/.test(v))||typeof o.native!=='boolean'||typeof o.referenceMatches!=='boolean')return 'Unavailable';
  return o.native&&o.referenceMatches&&o.values.every((v,i)=>v===reference[i])?'Verified':'Violation';
}
