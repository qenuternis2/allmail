// Document guard only. Web Audio is a Window API; other audio/media APIs are unaffected.
(() => {
  'use strict';
  const names = ['AudioContext', 'webkitAudioContext', 'OfflineAudioContext', 'webkitOfflineAudioContext'];
  const targets = [];
  for (const name of names) {
    for (let owner = globalThis; owner !== null; owner = Object.getPrototypeOf(owner)) {
      const descriptor = Object.getOwnPropertyDescriptor(owner, name);
      if (owner !== globalThis && !descriptor) continue;
      if (descriptor && !descriptor.configurable &&
        (!Object.hasOwn(descriptor, 'value') || (descriptor.value !== undefined && !descriptor.writable)))
        throw new Error('Web Audio guard v1: conflicting descriptor for ' + name);
      targets.push({owner, name, enumerable: descriptor ? descriptor.enumerable : false});
    }
  }
  for (const {owner, name, enumerable} of targets) {
    Object.defineProperty(owner, name, {value:undefined, writable:false, configurable:false, enumerable});
    const descriptor = Object.getOwnPropertyDescriptor(owner, name);
    if (!descriptor || descriptor.value !== undefined || descriptor.writable || descriptor.configurable)
      throw new Error('Web Audio guard v1: installation failed for ' + name);
  }
  return true;
})();
