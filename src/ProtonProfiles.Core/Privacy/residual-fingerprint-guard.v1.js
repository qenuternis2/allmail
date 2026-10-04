// Strict opt-in API restrictions. These are observable script changes, not native emulation.
(() => {
  'use strict';
  const g = globalThis, n = g.navigator;
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
  for (const name of ['getBattery','getGamepads','mediaDevices','mediaCapabilities']) restrict(n,name);
  restrict(g,'getScreenDetails');
  for (const name of ['BatteryManager','Gamepad','GamepadButton','GamepadEvent','GamepadHapticActuator','MediaDevices','MediaDeviceInfo','InputDeviceInfo','MediaCapabilities','Accelerometer','LinearAccelerationSensor','GravitySensor','Gyroscope','AbsoluteOrientationSensor','RelativeOrientationSensor','IdleDetector','ScreenDetails','ScreenDetailed','MemoryInfo']) restrict(g,name);
  restrict(g.performance,'memory');
  restrict(n.storage,'estimate');
  const NativeFontFace=g.FontFace;
  if (typeof NativeFontFace==='function' && Object.getOwnPropertyDescriptor(g,'FontFace')?.configurable!==false) {
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
})();
