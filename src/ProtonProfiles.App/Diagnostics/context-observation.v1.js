// Check ordinary language intrinsics separately from the selected privacy policy.
function collectJavaScriptIntrinsicsObservation(target=globalThis) {
  try {return {status:'Observed',objectConstructor:target.Object.prototype.constructor===target.Object,
    arrayConstructor:target.Array.prototype.constructor===target.Array,errorConstructor:target.Error.prototype.constructor===target.Error,
    eventTargetConstructor:target.EventTarget.prototype.constructor===target.EventTarget,
    nativeConstructors:[target.Object,target.Array,target.Error,target.EventTarget].every(fn=>typeof fn==='function'&&Function.prototype.toString.call(fn).includes('[native code]'))};}
  catch {return {status:'NotPerformed'};}
}

// Local ephemeral Web Crypto self-test. No application data, keys, random values or ciphertext are exported.
async function collectWebCryptoObservation(target=globalThis) {
  const result={status:'Observed',secureContext:target.isSecureContext===true,
    cryptoAvailable:typeof target.crypto?.getRandomValues==='function',subtleAvailable:!!target.crypto?.subtle,
    nativeMethods:false,randomGeneration:false,sha256:false,aesGcmRoundTrip:false,aesGcmTamperRejected:false};
  try {
    const c=target.crypto,s=c?.subtle;
    if(!result.secureContext || !result.cryptoAvailable || !result.subtleAvailable)return result;
    result.nativeMethods=[c.getRandomValues,s.digest,s.generateKey,s.encrypt,s.decrypt].every(fn=>typeof fn==='function'&&Function.prototype.toString.call(fn).includes('[native code]'));
    const random=new Uint8Array(32);c.getRandomValues(random);result.randomGeneration=true;random.fill(0);
    const digest=new Uint8Array(await s.digest('SHA-256',new Uint8Array([97,98,99])));
    result.sha256=Array.from(digest,v=>v.toString(16).padStart(2,'0')).join('')==='ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad';
    const key=await s.generateKey({name:'AES-GCM',length:256},false,['encrypt','decrypt']);
    const iv=c.getRandomValues(new Uint8Array(12)),plain=new Uint8Array([65,108,108,32,77,97,105,108,115]);
    const ciphertext=new Uint8Array(await s.encrypt({name:'AES-GCM',iv},key,plain));
    const decoded=new Uint8Array(await s.decrypt({name:'AES-GCM',iv},key,ciphertext));
    result.aesGcmRoundTrip=decoded.length===plain.length&&decoded.every((v,i)=>v===plain[i]);
    ciphertext[0]^=1;
    try {await s.decrypt({name:'AES-GCM',iv},key,ciphertext);}
    catch(e) {result.aesGcmTamperRejected=e?.name==='OperationError';}
    iv.fill(0);plain.fill(0);decoded.fill(0);ciphertext.fill(0);
    return result;
  } catch(e) {return {...result,status:'NotPerformed',errorName:typeof e?.name==='string'?e.name:'Error'};}
}

// Read-only diagnostics. No media is loaded, decoded, played or sent over the network.
function collectTimeZoneFingerprintObservation(target = globalThis) {
  try {
    const dates = [1768478400000, 1784116800000]; // 2026-01-15 / 2026-07-15, 12:00 UTC.
    return {status:'Observed', timeZone:target.Intl.DateTimeFormat().resolvedOptions().timeZone,
      offsets:dates.map(epoch => ({epoch,offset:new target.Date(epoch).getTimezoneOffset()}))};
  } catch { return {status:'NotPerformed'}; }
}

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
      uaHints:await collectUaHintsObservation(), media:collectMediaFingerprintObservation(), timer:collectTimerFingerprintObservation(),
      timeZoneObservation:collectTimeZoneFingerprintObservation(),webCrypto:await collectWebCryptoObservation(),javascriptIntrinsics:collectJavaScriptIntrinsicsObservation()};
  } catch { return {status:'NotPerformed', reason:'Наблюдение iframe не завершено'}; }
}
