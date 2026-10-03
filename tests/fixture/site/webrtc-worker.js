function inventory() {
  const names = Object.getOwnPropertyNames(globalThis).filter(n => /^(RTC|webkitRTC)/.test(n));
  const constructible = [];
  for (const name of ["RTCPeerConnection", "webkitRTCPeerConnection", "RTCIceTransport", "RTCDtlsTransport", "RTCSctpTransport"]) {
    try {
      if (typeof globalThis[name] !== "function") continue;
      const value = new globalThis[name](); constructible.push(name);
      value.close?.(); value.stop?.();
    } catch { /* Present type is recorded above; failure to construct is not treated as engine disablement. */ }
  }
  return { names, constructible, scope:"worker API inventory; document guard does not run here" };
}
if (typeof ServiceWorkerGlobalScope !== "undefined" && self instanceof ServiceWorkerGlobalScope) {
  self.addEventListener("message", e => e.ports[0]?.postMessage(inventory()));
} else if (typeof SharedWorkerGlobalScope !== "undefined" && self instanceof SharedWorkerGlobalScope) {
  self.onconnect = e => {e.ports[0].postMessage(inventory()); e.ports[0].start();};
} else {
  postMessage(inventory());
}
