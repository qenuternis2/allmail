// Shared observation helper; does not replace APIs or retain constructors.
function collectAudioGuardObservation() {
  try {
    const names = ['AudioContext', 'webkitAudioContext', 'OfflineAudioContext', 'webkitOfflineAudioContext'];
    const windowApisAvailable = names.some(name => typeof globalThis[name] === 'function');
    const guardVerified = names.every(name => {
      const own = Object.getOwnPropertyDescriptor(globalThis, name);
      if (!own || !Object.hasOwn(own, 'value') || own.value !== undefined || own.writable || own.configurable) return false;
      for (let owner = Object.getPrototypeOf(globalThis); owner; owner = Object.getPrototypeOf(owner)) {
        const descriptor = Object.getOwnPropertyDescriptor(owner, name);
        if (descriptor && (!Object.hasOwn(descriptor, 'value') || descriptor.value !== undefined || descriptor.writable || descriptor.configurable)) return false;
      }
      return true;
    });
    return {windowApisAvailable, guardVerified};
  } catch { return {status:'NotPerformed'}; }
}
