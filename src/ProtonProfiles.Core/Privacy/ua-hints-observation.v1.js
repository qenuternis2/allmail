async function collectUaHintsObservation(target = globalThis) {
  try {
    const n = target.navigator;
    const userAgent = n.userAgent;
    const data = n.userAgentData;
    const secureContext = target.isSecureContext;
    const sharedWorkerAvailable = typeof target.SharedWorker !== 'undefined';
    if (typeof data === 'undefined') return {status:'Observed',secureContext,userAgent,sharedWorkerAvailable,uaDataAvailable:false};
    if (!data || typeof data.getHighEntropyValues !== 'function') return {status:'NotPerformed'};
    const lowEntropy = {brands:data.brands,platform:data.platform,mobile:data.mobile};
    const highEntropy = await data.getHighEntropyValues(['architecture','bitness','model','platformVersion','uaFullVersion','fullVersionList','wow64','formFactors']);
    return {status:'Observed',secureContext,userAgent,sharedWorkerAvailable,uaDataAvailable:true,lowEntropy,highEntropy};
  } catch (_) { return {status:'NotPerformed'}; }
}

function uaHintsObservationOutcome(observation, expectedUserAgent = null) {
  if (!observation || observation.status !== 'Observed' || observation.secureContext !== true || typeof observation.userAgent !== 'string' || !observation.userAgent.trim()) return 'Unavailable';
  if (expectedUserAgent !== null && observation.userAgent !== expectedUserAgent) return 'Violation';
  if (typeof observation.sharedWorkerAvailable !== 'boolean') return 'Unavailable';
  if (observation.sharedWorkerAvailable) return 'Violation';
  if (observation.uaDataAvailable === false) return 'Verified';
  if (observation.uaDataAvailable !== true) return 'Unavailable';
  const low = observation.lowEntropy, high = observation.highEntropy;
  if (!low || !Array.isArray(low.brands) || typeof low.platform !== 'string' || typeof low.mobile !== 'boolean'
      || !high || typeof high !== 'object' || Array.isArray(high)) return 'Unavailable';
  if (low.brands.length || low.platform || low.mobile) return 'Violation';
  const strings = ['architecture','bitness','model','platformVersion','uaFullVersion','platform'];
  const arrays = ['brands','fullVersionList','formFactors'];
  const booleans = ['wow64','mobile'];
  for (const key of strings) if (key in high) {
    if (typeof high[key] !== 'string') return 'Unavailable';
    if (high[key]) return 'Violation';
  }
  for (const key of arrays) if (key in high) {
    if (!Array.isArray(high[key])) return 'Unavailable';
    if (high[key].length) return 'Violation';
  }
  for (const key of booleans) if (key in high) {
    if (typeof high[key] !== 'boolean') return 'Unavailable';
    if (high[key]) return 'Violation';
  }
  return 'Verified';
}
