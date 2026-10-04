// Strict opt-in API restrictions. These are observable script changes, not native emulation.
((exceptions) => {
  'use strict';
  const g = globalThis, n = g.navigator, allowed=new Set(exceptions);
  const allow=name=>allowed.has(name);
  function restrict(target, name, value = undefined) {
    if (!target) return;
    const owners = [];
    for (let owner=target; owner; owner=Object.getPrototypeOf(owner)) {
      const d=Object.getOwnPropertyDescriptor(owner,name);
      if (!d) continue;
      if (!d.configurable && !(Object.hasOwn(d,'value') && d.value===value && !d.writable))
        throw new Error('Strict privacy: conflicting '+name);
      owners.push([owner,d.enumerable]);
    }
    // Lock a shadow too, so a caller cannot restore a prototype API on this object.
    if (!owners.some(([owner])=>owner===target)) owners.push([target,false]);
    for (const [owner,enumerable] of owners)
      Object.defineProperty(owner,name,{value,writable:false,configurable:false,enumerable});
  }
  restrict(n,'deviceMemory',8);
  for (const [name,feature] of [['getBattery','Battery'],['getGamepads','Gamepads'],['mediaDevices','MediaDevices'],['mediaCapabilities','MediaCapabilities'],['serviceWorker','ServiceWorkers']])
    if(!allow(feature))restrict(n,name);
  // Keyboard Layout Map exposes OS layout independent of navigator.language.
  // Chromium has no native RuntimeEnabled switch for this API. Ordinary input stays native.
  if(!allow('KeyboardLayout')) {
  const keyboard=n.keyboard;
  for (const name of ['getLayoutMap','lock','unlock']) {
    restrict(keyboard,name);
    restrict(g.Keyboard?.prototype,name);
  }
  restrict(n,'keyboard');
  for (const name of ['Keyboard','KeyboardLayoutMap']) {
    restrict(g[name]?.prototype,'constructor');
    restrict(g,name);
  }
  }
  restrict(g,'getScreenDetails');
  const constructorFeatures={BatteryManager:'Battery',Gamepad:'Gamepads',GamepadButton:'Gamepads',GamepadEvent:'Gamepads',GamepadHapticActuator:'Gamepads',
    MediaDevices:'MediaDevices',MediaDeviceInfo:'MediaDevices',InputDeviceInfo:'MediaDevices',MediaCapabilities:'MediaCapabilities',
    ServiceWorker:'ServiceWorkers',ServiceWorkerContainer:'ServiceWorkers',ServiceWorkerRegistration:'ServiceWorkers'};
  for (const name of ['BatteryManager','Gamepad','GamepadButton','GamepadEvent','GamepadHapticActuator','MediaDevices','MediaDeviceInfo','InputDeviceInfo','MediaCapabilities','Accelerometer','LinearAccelerationSensor','GravitySensor','Gyroscope','AbsoluteOrientationSensor','RelativeOrientationSensor','IdleDetector','ScreenDetails','ScreenDetailed','MemoryInfo','ServiceWorker','ServiceWorkerContainer','ServiceWorkerRegistration'])
    if(!allow(constructorFeatures[name]))restrict(g,name);
  // WebCodecs has no RuntimeEnabled switch in current Chromium. Keep HTML media/MSE intact.
  if(!allow('WebCodecs'))for (const name of ['AudioDecoder','VideoDecoder','AudioEncoder','VideoEncoder','AudioData','VideoFrame','EncodedAudioChunk','EncodedVideoChunk']) {
    const prototype = g[name]?.prototype;
    if (prototype) restrict(prototype,'constructor');
    restrict(g,name);
  }
  restrict(g.performance,'memory');
  if(!allow('StorageEstimate'))restrict(n.storage,'estimate');
  const NativeFontFace=g.FontFace;
  if (!allow('LocalFonts') && typeof NativeFontFace==='function' && Object.getOwnPropertyDescriptor(g,'FontFace')?.configurable!==false) {
    const guarded = new Proxy(NativeFontFace, {construct(target,args,newTarget) {
      const source=args[1];
      let binary=ArrayBuffer.isView(source);
      if (!binary) try {Object.getOwnPropertyDescriptor(ArrayBuffer.prototype,'byteLength').get.call(source);binary=true;} catch {}
      if (!binary) {
        const text=String(source);
        const decoded=text.replace(/\\([0-9a-f]{1,6}\s?|[^\r\n])/ig,(_,escape)=> /^[0-9a-f]/i.test(escape)?String.fromCodePoint(parseInt(escape.trim(),16)||0xfffd):escape)
          .replace(/\/\*[\s\S]*?\*\//g,'');
        if (/local\s*\(/i.test(decoded)) throw new DOMException('Local font sources are disabled by strict privacy.','SecurityError');
        args=[args[0],text,...args.slice(2)];
      }
      return Reflect.construct(target,args,newTarget);
    }});
    Object.defineProperty(NativeFontFace.prototype,'constructor',{value:guarded,writable:false,configurable:false});
    Object.defineProperty(g,'FontFace',{value:guarded,writable:false,configurable:false});
  }
  return true;
})(/*__PP_PRIVACY_EXCEPTIONS__*/[]);
