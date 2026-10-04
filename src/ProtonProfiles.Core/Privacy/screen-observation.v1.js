// Observes native screen and media queries; no API replacements or retained window references.
function collectScreenObservation(target = globalThis) {
  try {
    if (typeof target.screen !== 'object' || !target.screen) return {screenApisAvailable:false};
    const s = target.screen, dpr = target.devicePixelRatio;
    const names = ['width','height','availWidth','availHeight','availLeft','availTop'];
    const nativeGetters = names.every(name => {
      let prototype = Object.getPrototypeOf(s), descriptor;
      while (prototype && !descriptor) {
        descriptor = Object.getOwnPropertyDescriptor(prototype, name);
        prototype = Object.getPrototypeOf(prototype);
      }
      return typeof descriptor?.get === 'function'
        && Function.prototype.toString.call(descriptor.get).includes('[native code]');
    });
    return {screenApisAvailable:true, width:s.width, height:s.height, availWidth:s.availWidth, availHeight:s.availHeight,
      availLeft:s.availLeft, availTop:s.availTop, devicePixelRatio:dpr,
      orientationType:s.orientation?.type ?? null, orientationAngle:s.orientation?.angle ?? null,
      nativeGetters, ownProperties:names.some(name => Object.hasOwn(s,name)),
      deviceWidthMatches:target.matchMedia(`(device-width: ${s.width}px)`).matches,
      deviceHeightMatches:target.matchMedia(`(device-height: ${s.height}px)`).matches,
      resolutionMatches:target.matchMedia(`(resolution: ${dpr}dppx)`).matches};
  } catch { return {status:'NotPerformed'}; }
}
