// Read-only diagnostics. No media is loaded, decoded, played or sent over the network.
function collectMediaFingerprintObservation(target = globalThis) {
  const types = ['video/mp4; codecs="avc1.42E01E, mp4a.40.2"',
    'video/webm; codecs="vp8, vorbis"', 'video/webm; codecs="vp9, opus"',
    'video/mp4; codecs="av01.0.05M.08"', 'audio/mp4; codecs="mp4a.40.2"',
    'audio/mpeg', 'audio/ogg; codecs="opus"', 'audio/flac', 'audio/wav; codecs="1"'];
  try {
    const documentContext = typeof target.document === 'object';
    const html = documentContext ? target.document.createElement('video') : null;
    const query = fn => { try { return fn(); } catch { return null; } };
    return {status:'Observed', documentContext,
      htmlCanPlayType: html ? Object.fromEntries(types.map(type => [type, query(() => {
        const value = html.canPlayType(type); return ['', 'maybe', 'probably'].includes(value) ? value : null;
      })])) : null,
      mediaSourceSupport: typeof target.MediaSource?.isTypeSupported === 'function'
        ? Object.fromEntries(types.map(type => [type, query(() => {
          const value = target.MediaSource.isTypeSupported(type); return typeof value === 'boolean' ? value : null;
        })])) : null,
      webCodecs: Object.fromEntries(['AudioDecoder','VideoDecoder','AudioEncoder','VideoEncoder']
        .map(name => [name, typeof target[name] === 'function']))};
  } catch { return {status:'NotPerformed'}; }
}

function collectTimerFingerprintObservation(target = globalThis) {
  try {
    if (typeof target.performance?.now !== 'function') return {status:'NotPerformed'};
    const start = target.performance.now();
    if (!Number.isFinite(start)) return {status:'NotPerformed'};
    let previous = start, minPositiveDeltaMs = null, positiveSamples = 0, regressions = 0, samples = 0;
    // Fixed, small upper bound; this is a timer observation, not a CPU benchmark.
    for (; samples < 2048; samples++) {
      const current = target.performance.now();
      if (!Number.isFinite(current)) return {status:'NotPerformed'};
      const delta = current - previous;
      if (delta < 0) regressions++;
      if (delta > 0) { positiveSamples++; minPositiveDeltaMs = Math.min(minPositiveDeltaMs ?? delta, delta); }
      previous = current;
      if (current - start >= 20) { samples++; break; }
    }
    return {status:'Observed', samples, positiveSamples, minPositiveDeltaMs, regressions,
      crossOriginIsolated: target.crossOriginIsolated === true,
      sharedArrayBufferAvailable: typeof target.SharedArrayBuffer === 'function'};
  } catch { return {status:'NotPerformed'}; }
}

async function collectFramePrivacyObservation() {
  try {
    const gl = document.createElement('canvas');
    let webGpuAdapterAvailable = false;
    if (navigator.gpu) {
      let timer;
      try {
        webGpuAdapterAvailable = !!(await Promise.race([navigator.gpu.requestAdapter(),
          new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('timeout')), 2000); })]));
      } catch { webGpuAdapterAvailable = null; }
      finally { clearTimeout(timer); }
    }
    return {status:'Observed', secureContext:isSecureContext,
      timeZone:Intl.DateTimeFormat().resolvedOptions().timeZone, utcOffsetMinutes:new Date().getTimezoneOffset(),
      hardwareConcurrency:navigator.hardwareConcurrency, deviceMemory:navigator.deviceMemory ?? null,
      webGlAvailable:!!(gl.getContext('webgl2') || gl.getContext('webgl')), webGpuAdapterAvailable,
      canvasReadback:await collectCanvasReadback(), webAudio:collectAudioGuardObservation(),
      screen:collectScreenObservation(), speech:collectSpeechObservation(), fontAccess:collectFontAccessObservation(),
      cpu:collectCpuObservation(), hardwareDevices:collectHardwareDevicesObservation(),
      computePressure:collectComputePressureObservation(), additionalPrivacy:await collectAdditionalFingerprintObservation(),
      standardPrivacy:await collectStandardFingerprintObservation(), residualPrivacy:collectResidualFingerprintObservation(),
      uaHints:await collectUaHintsObservation(), media:collectMediaFingerprintObservation(), timer:collectTimerFingerprintObservation()};
  } catch { return {status:'NotPerformed', reason:'Наблюдение iframe не завершено'}; }
}
