// Observe API entry points only. Never instantiate an observer or start a CPU measurement.
function collectComputePressureObservation(target = globalThis) {
  try {
    return {status:'Observed',secureContext:target.isSecureContext === true,
      documentContext:typeof target.document === 'object',
      observerAvailable:'PressureObserver' in target, recordAvailable:'PressureRecord' in target};
  } catch { return {status:'NotPerformed'}; }
}

function computePressureObservationOutcome(observation, worker = false) {
  if (!observation || observation.status !== 'Observed' || observation.secureContext !== true
      || observation.documentContext !== !worker) return 'Unavailable';
  if (observation.observerAvailable === true || observation.recordAvailable === true) return 'Violation';
  return observation.observerAvailable === false && observation.recordAvailable === false ? 'Verified' : 'Unavailable';
}
