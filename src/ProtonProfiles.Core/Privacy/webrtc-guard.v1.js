// Proton Profiles WebRTC page guard v1. Document realms only; this is not a native engine switch.
// No constructor, restoration function, or status bridge is retained on a page-visible object.
(() => {
  "use strict";
  const names = ["RTCPeerConnection", "webkitRTCPeerConnection", "RTCIceTransport", "RTCDtlsTransport", "RTCSctpTransport"];
  const targets = [];
  for (const name of names) {
    // Also cover inherited entry points: hiding an own name must not leave a constructor on a prototype.
    let owner = globalThis;
    while (owner !== null) {
      const descriptor = Object.getOwnPropertyDescriptor(owner, name);
      if (owner === globalThis || descriptor) {
        if (descriptor && !descriptor.configurable &&
            (!Object.hasOwn(descriptor, "value") || (descriptor.value !== undefined && !descriptor.writable))) {
          throw new Error("WebRTC guard v1: conflicting descriptor for " + name);
        }
        targets.push({ owner, name, enumerable: descriptor ? descriptor.enumerable : false });
      }
      owner = Object.getPrototypeOf(owner);
    }
  }
  for (const { owner, name, enumerable } of targets) {
    Object.defineProperty(owner, name, { value: undefined, writable: false, configurable: false, enumerable });
    const installed = Object.getOwnPropertyDescriptor(owner, name);
    if (!installed || installed.value !== undefined || installed.writable || installed.configurable) {
      throw new Error("WebRTC guard v1: installation failed for " + name);
    }
  }
  return true;
})();
