// Presence and permissions only: never opens a device, requests permission, or starts a sensor.
async function collectAdditionalFingerprintObservation(target = globalThis) {
  try {
    const n=target.navigator, media=n.mediaDevices;
    const apis={xr:'xr' in n,cpuPerformance:'cpuPerformance' in n,measureMemory:!!target.performance && 'measureUserAgentSpecificMemory' in target.performance,getDisplayMedia:!!media && 'getDisplayMedia' in media,
      selectAudioOutput:!!media && 'selectAudioOutput' in media};
    for(const name of ['AmbientLightSensor','Magnetometer','NDEFReader','NDEFRecord','NDEFMessage']) apis[name]=name in target;
    apis.presentation='presentation' in n;
    apis.mediaRemote=typeof target.HTMLMediaElement==='function' && 'remote' in target.HTMLMediaElement.prototype;
    for(const name of ['RemotePlayback','Presentation','PresentationRequest','PresentationAvailability','PresentationConnection','PresentationConnectionAvailableEvent','PresentationConnectionCloseEvent','PresentationConnectionList','PresentationReceiver']) apis[name]=name in target;
    const permissions={};
    for(const name of ['camera','microphone','geolocation','accelerometer','gyroscope','magnetometer','midi','camera-ptz','midi-sysex','idle-detection','window-management']) {
      try {const descriptor=name==='camera-ptz'?{name:'camera',panTiltZoom:true}:name==='midi-sysex'?{name:'midi',sysex:true}:{name};permissions[name]=(await n.permissions.query(descriptor)).state;} catch {permissions[name]='NotPerformed';}
    }
    let connection={status:'NotPerformed'};
    try {
      const c=n.connection;
      if(c) {
        const nativeGetters=['effectiveType','rtt','downlink'].every(key=>{
          let proto=Object.getPrototypeOf(c), descriptor;
          while(proto && !descriptor) {descriptor=Object.getOwnPropertyDescriptor(proto,key);proto=Object.getPrototypeOf(proto);}
          return !Object.hasOwn(c,key) && typeof descriptor?.get==='function' && Function.prototype.toString.call(descriptor.get).includes('[native code]');
        });
        connection={status:'Observed',effectiveType:c.effectiveType,rtt:c.rtt,downlink:c.downlink,nativeGetters};
      }
    } catch {}
    return {status:'Observed',secureContext:target.isSecureContext===true,documentContext:typeof target.document==='object',apis,permissions,connection,remainingApis:{
      getBattery:n.getBattery!==undefined,getGamepads:n.getGamepads!==undefined,mediaDevices:n.mediaDevices!==undefined,
      mediaCapabilities:n.mediaCapabilities!==undefined,Accelerometer:target.Accelerometer!==undefined,
      Gyroscope:target.Gyroscope!==undefined,performanceMemory:target.performance?.memory!==undefined,
      getScreenDetails:typeof target.getScreenDetails==='function',IdleDetector:target.IdleDetector!==undefined}};
  } catch {return {status:'NotPerformed'};}
}

function additionalApiObservationOutcome(o,worker=false) {
  if(!o || o.status!=='Observed' || o.secureContext!==true || o.documentContext!==!worker || !o.apis) return 'Unavailable';
  const keys=['xr','cpuPerformance','measureMemory','getDisplayMedia','selectAudioOutput','AmbientLightSensor','Magnetometer','NDEFReader','NDEFRecord','NDEFMessage','presentation','mediaRemote','RemotePlayback','Presentation','PresentationRequest','PresentationAvailability','PresentationConnection','PresentationConnectionAvailableEvent','PresentationConnectionCloseEvent','PresentationConnectionList','PresentationReceiver'];
  if(keys.some(k=>o.apis[k]===true)) return 'Violation';
  if(!keys.every(k=>o.apis[k]===false)) return 'Unavailable';
  return worker ? 'NotApplicable' : 'Verified'; // None of these APIs is exposed to DedicatedWorker.
}
function hardwarePermissionObservationOutcome(o) {
  if(!o || o.status!=='Observed' || o.secureContext!==true || o.documentContext!==true || !o.permissions) return 'Unavailable';
  const values=['camera','microphone','geolocation','accelerometer','gyroscope','magnetometer','midi','camera-ptz','midi-sysex','idle-detection','window-management'].map(k=>o.permissions[k]);
  if(values.some(v=>v==='prompt'||v==='granted')) return 'Violation';
  return values.every(v=>v==='denied') ? 'Verified' : 'Unavailable';
}
function networkEstimateObservationOutcome(o) {
  if(!o || o.status!=='Observed' || o.nativeGetters!==true || typeof o.effectiveType!=='string'
      || !Number.isFinite(o.rtt) || !Number.isFinite(o.downlink)) return 'Unavailable';
  return o.effectiveType==='4g' && o.rtt>=100 && o.rtt<=250 && o.downlink>=1 && o.downlink<=2 ? 'Verified' : 'Violation';
}
