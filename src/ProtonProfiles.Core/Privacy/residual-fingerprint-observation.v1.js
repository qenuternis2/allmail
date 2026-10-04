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
      keyboardLayout:{keyboard:keyboard!==undefined,Keyboard:target.Keyboard!==undefined,KeyboardLayoutMap:target.KeyboardLayoutMap!==undefined,
        getLayoutMap:typeof keyboard?.getLayoutMap==='function',lock:typeof keyboard?.lock==='function',unlock:typeof keyboard?.unlock==='function'},
      webCodecs:Object.fromEntries(['AudioDecoder','VideoDecoder','AudioEncoder','VideoEncoder','AudioData','VideoFrame','EncodedAudioChunk','EncodedVideoChunk'].map(k=>[k,target[k]!==undefined])),
      performanceMemoryAvailable:target.performance?.memory!==undefined,
      storageEstimateAvailable:typeof n.storage?.estimate==='function',getScreenDetailsAvailable:typeof target.getScreenDetails==='function',localFontConstructionBlocked};
  } catch {return {status:'NotPerformed'};}
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
    [o.canvasTextMetrics?.html,'CanvasTextMetrics'],[o.canvasTextMetrics?.offscreen,'CanvasTextMetrics'],
    [o.performanceMemoryAvailable,null],[o.storageEstimateAvailable,'StorageEstimate'],[o.getScreenDetailsAvailable,null]];
  if(entries.some(([v])=>typeof v!=='boolean') || ![true,false,null].includes(o.localFontConstructionBlocked))return 'Unavailable';
  return o.deviceMemory===8 && o.scriptRestriction && entries.every(([v,feature])=>v===false || exceptions.includes(feature))
    && (o.localFontConstructionBlocked!==false || exceptions.includes('LocalFonts')) ? 'Verified':'Violation';
}
