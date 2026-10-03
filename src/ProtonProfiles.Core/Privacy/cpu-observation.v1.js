// Read the native count and descriptor only; do not replace navigator properties or run benchmarks.
function collectCpuObservation(target = globalThis) {
  try {
    const nav = target.navigator;
    let prototype = Object.getPrototypeOf(nav), descriptor;
    while (prototype && !descriptor) {
      descriptor = Object.getOwnPropertyDescriptor(prototype, 'hardwareConcurrency');
      prototype = Object.getPrototypeOf(prototype);
    }
    return {status:'Observed', hardwareConcurrency:nav.hardwareConcurrency,
      ownProperty:Object.hasOwn(nav, 'hardwareConcurrency'),
      nativeGetter:typeof descriptor?.get === 'function' && Function.prototype.toString.call(descriptor.get).includes('[native code]')};
  } catch { return {status:'NotPerformed'}; }
}
