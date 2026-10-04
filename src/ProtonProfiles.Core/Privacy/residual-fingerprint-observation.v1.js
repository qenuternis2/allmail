function collectResidualFingerprintObservation(target=globalThis) {
  try {
    const n=target.navigator;
    const navigatorApis=Object.fromEntries(['getBattery','getGamepads','mediaDevices','mediaCapabilities','getScreenDetails'].map(k=>[k,n[k]!==undefined]));
    const constructors=Object.fromEntries(['BatteryManager','Gamepad','GamepadButton','GamepadEvent','GamepadHapticActuator','MediaDevices','MediaDeviceInfo','InputDeviceInfo','MediaCapabilities','Accelerometer','LinearAccelerationSensor','GravitySensor','Gyroscope','AbsoluteOrientationSensor','RelativeOrientationSensor','IdleDetector','ScreenDetails','ScreenDetailed','MemoryInfo'].map(k=>[k,target[k]!==undefined]));
    let localFontConstructionBlocked=null;
    if (typeof target.FontFace==='function') {
      try {new target.FontFace('ProtonProfilesBlockedLocalProbe','local("Arial")');localFontConstructionBlocked=false;}
      catch(e) {localFontConstructionBlocked=e?.name==='SecurityError';}
    }
    const descriptor=Object.getOwnPropertyDescriptor(n,'deviceMemory');
    return {status:'Observed',documentContext:!!target.document,deviceMemory:n.deviceMemory??null,
      scriptRestriction:descriptor?.value===8 && descriptor.writable===false && descriptor.configurable===false,
      navigatorApis,constructors,performanceMemoryAvailable:target.performance?.memory!==undefined,
      storageEstimateAvailable:typeof n.storage?.estimate==='function',localFontConstructionBlocked};
  } catch {return {status:'NotPerformed'};}
}

function residualFingerprintOutcome(o) {
  if(o?.status!=='Observed' || typeof o.scriptRestriction!=='boolean' || !Number.isFinite(o.deviceMemory))return 'Unavailable';
  const navigatorKeys=['getBattery','getGamepads','mediaDevices','mediaCapabilities','getScreenDetails'];
  const constructorKeys=['BatteryManager','Gamepad','GamepadButton','GamepadEvent','GamepadHapticActuator','MediaDevices','MediaDeviceInfo','InputDeviceInfo','MediaCapabilities','Accelerometer','LinearAccelerationSensor','GravitySensor','Gyroscope','AbsoluteOrientationSensor','RelativeOrientationSensor','IdleDetector','ScreenDetails','ScreenDetailed','MemoryInfo'];
  const values=[...navigatorKeys.map(k=>o.navigatorApis?.[k]),...constructorKeys.map(k=>o.constructors?.[k]),o.performanceMemoryAvailable,o.storageEstimateAvailable];
  if(values.some(v=>typeof v!=='boolean') || ![true,false,null].includes(o.localFontConstructionBlocked))return 'Unavailable';
  return o.deviceMemory===8 && o.scriptRestriction && values.every(v=>v===false) && o.localFontConstructionBlocked!==false ? 'Verified':'Violation';
}
