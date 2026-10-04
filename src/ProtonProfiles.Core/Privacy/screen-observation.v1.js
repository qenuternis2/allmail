// Observes native screen and media queries; no API replacements or retained window references.
function collectScreenObservation(target = globalThis) {
  try {
    if (typeof target.screen !== 'object' || !target.screen) return {screenApisAvailable:false};
    const s = target.screen, dpr = target.devicePixelRatio;
    return {screenApisAvailable:true, width:s.width, height:s.height, availWidth:s.availWidth, availHeight:s.availHeight,
      availLeft:s.availLeft, availTop:s.availTop, devicePixelRatio:dpr,
      orientationType:s.orientation?.type ?? null, orientationAngle:s.orientation?.angle ?? null,
      deviceWidthMatches:target.matchMedia(`(device-width: ${s.width}px)`).matches,
      deviceHeightMatches:target.matchMedia(`(device-height: ${s.height}px)`).matches,
      resolutionMatches:target.matchMedia(`(resolution: ${dpr}dppx)`).matches};
  } catch { return {status:'NotPerformed'}; }
}
