// Passive native readback: no permission request, hardware emulation or persistent DOM changes.
function collectDisplayPostureObservation(target=globalThis) {
  if(!target.document)return {status:'NotApplicable'};
  let style,probe;
  try {
    const d=target.document, id='display-check-'+Math.random().toString(36).slice(2),mq=q=>target.matchMedia(q).matches;
    const nativeGetter=(object,key)=>{
      for(let owner=object;owner;owner=Object.getPrototypeOf(owner)) {
        const descriptor=Object.getOwnPropertyDescriptor(owner,key);
        if(descriptor)return typeof descriptor.get==='function'&&Function.prototype.toString.call(descriptor.get).includes('[native code]');
      }return false;
    };
    const posture=target.navigator?.devicePosture,viewport=target.viewport;
    style=d.createElement('style');probe=d.createElement('div');probe.id=id;
    style.textContent=`#${id}{position:fixed!important;visibility:hidden!important;width:env(viewport-segment-left 1 0,777px)!important;--display-posture:unsupported;--display-horizontal:0;--display-vertical:0;}
      @media (device-posture:continuous){#${id}{--display-posture:continuous!important;}}
      @media (device-posture:folded){#${id}{--display-posture:folded!important;}}
      @media (horizontal-viewport-segments:1){#${id}{--display-horizontal:1!important;}}
      @media (horizontal-viewport-segments:2){#${id}{--display-horizontal:2!important;}}
      @media (vertical-viewport-segments:1){#${id}{--display-vertical:1!important;}}
      @media (vertical-viewport-segments:2){#${id}{--display-vertical:2!important;}}`;
    d.documentElement.appendChild(style);d.documentElement.appendChild(probe);
    if(typeof target.CSS?.supports!=='function')return {status:'NotPerformed'};
    const envSupported=target.CSS.supports('width','env(viewport-segment-left 1 0,777px)');
    const css=target.getComputedStyle(probe),segments=viewport?.segments;
    return {status:'Observed',
      media:{continuous:mq('(device-posture:continuous)'),folded:mq('(device-posture:folded)'),horizontalSingle:mq('(horizontal-viewport-segments:1)'),horizontalDouble:mq('(horizontal-viewport-segments:2)'),verticalSingle:mq('(vertical-viewport-segments:1)'),verticalDouble:mq('(vertical-viewport-segments:2)')},
      css:{posture:css.getPropertyValue('--display-posture').trim(),horizontal:Number(css.getPropertyValue('--display-horizontal')),vertical:Number(css.getPropertyValue('--display-vertical')),envSupported,secondSegmentLeft:envSupported?parseFloat(css.width):null},
      postureApiAvailable:posture!==undefined,postureType:posture?.type??null,nativePostureGetter:posture!==undefined?nativeGetter(posture,'type'):null,
      viewportApiAvailable:viewport!==undefined,segmentCount:segments?.length??null,nativeSegmentsGetter:viewport!==undefined?nativeGetter(viewport,'segments'):null};
  }catch{return {status:'NotPerformed'};}
  finally{probe?.remove();style?.remove();}
}
function displayPostureOutcome(o,documentContext=false) {
  if(o?.status==='NotApplicable')return documentContext===false?'Verified':'Unavailable';
  if(o?.status!=='Observed')return 'Unavailable';
  const mediaKeys=['continuous','folded','horizontalSingle','horizontalDouble','verticalSingle','verticalDouble'];
  if(mediaKeys.some(k=>typeof o.media?.[k]!=='boolean')||typeof o.postureApiAvailable!=='boolean'||typeof o.viewportApiAvailable!=='boolean'
    ||typeof o.css?.posture!=='string'||!Number.isInteger(o.css.horizontal)||!Number.isInteger(o.css.vertical)||typeof o.css.envSupported!=='boolean'||(o.css.envSupported?!Number.isFinite(o.css.secondSegmentLeft):o.css.secondSegmentLeft!==null))return 'Unavailable';
  if(o.postureApiAvailable?(typeof o.postureType!=='string'||typeof o.nativePostureGetter!=='boolean'):(o.postureType!==null||o.nativePostureGetter!==null))return 'Unavailable';
  if(o.viewportApiAvailable?(![true,false].includes(o.nativeSegmentsGetter)||(o.segmentCount!==null&&(!Number.isInteger(o.segmentCount)||o.segmentCount<1))):(o.nativeSegmentsGetter!==null||o.segmentCount!==null))return 'Unavailable';
  return o.media.continuous&&!o.media.folded&&!o.media.horizontalSingle&&!o.media.horizontalDouble&&!o.media.verticalSingle&&!o.media.verticalDouble
    &&o.css.posture==='continuous'&&o.css.horizontal===0&&o.css.vertical===0&&(!o.css.envSupported||o.css.secondSegmentLeft===777)
    &&(!o.postureApiAvailable||o.postureType==='continuous'&&o.nativePostureGetter)
    &&!o.viewportApiAvailable?'Verified':'Violation';
}

// Observe native document media, generic font metrics and a disposable local FontFace.
// No installed-font enumeration or global replacements; disposable font-set entry is always removed.
function measureDiagnosticTextWidth(target,ctx,text,font) {
  if(typeof ctx.measureText==='function') {ctx.font=font;return ctx.measureText(text).width;}
  // DOM font metrics remain observable when Canvas text metrics are restricted.
  const span=target.document.createElement('span');
  span.style.cssText='all:initial!important;position:fixed!important;visibility:hidden!important;white-space:pre!important;';
  span.style.setProperty('font',font,'important');span.textContent=text;
  try {target.document.documentElement.appendChild(span);return span.getBoundingClientRect().width;}
  finally {span.remove();}
}
async function collectStandardFingerprintObservation(target = globalThis) {
  try {
    if (!target.document) return {status:'NotApplicable',documentContext:false};
    const expected = {'prefers-color-scheme':'light','prefers-contrast':'no-preference',
      'prefers-reduced-motion':'no-preference','prefers-reduced-data':'no-preference',
      'prefers-reduced-transparency':'no-preference','forced-colors':'none','color-gamut':'srgb'};
    const media = Object.fromEntries(Object.entries(expected).map(([k,v]) => {
      const supported = !['prefers-reduced-data','prefers-reduced-transparency'].includes(k)
        || target.matchMedia(`(${k}: reduce), (${k}: no-preference)`).matches;
      let matched = supported ? target.matchMedia(`(${k}: ${v})`).matches : null;
      if (k === 'color-gamut') matched = matched && !target.matchMedia('(color-gamut: p3)').matches
        && !target.matchMedia('(color-gamut: rec2020)').matches;
      return [k,matched];
    }));
    const canvas = target.document.createElement('canvas'), ctx = canvas.getContext('2d');
    if (!ctx) return {status:'NotPerformed',documentContext:true};
    const width = family => measureDiagnosticTextWidth(target,ctx,'Wim0123@# Съешь',`32px ${family}`);
    const genericFonts = {serif:width('serif')===width('"Times New Roman"'),
      sansSerif:width('sans-serif')===width('"Arial"'),fixed:width('monospace')===width('"Courier New"'),
      cursive:width('cursive')===width('"Comic Sans MS"'),fantasy:width('fantasy')===width('"Impact"'),
      math:width('math')===width('"Cambria Math"')};
    let defaultFontSize = null, osTextScale = null;
    const element = target.document.createElement('span');
    element.style.cssText = 'all:initial!important;position:fixed!important;visibility:hidden!important;';
    try {
      target.document.documentElement.appendChild(element);
      defaultFontSize = parseFloat(target.getComputedStyle(element).fontSize);
      // Without meta text-scale the Windows engine deliberately exposes 1,
      // even for a different OS scale. Do not call that a verified override.
      if (target.document.querySelector('meta[name="text-scale"][content="scale"]')) {
        element.style.setProperty('font-size','calc(10px * env(preferred-text-scale, 999))','important');
        const measured = parseFloat(target.getComputedStyle(element).fontSize) / 10;
        osTextScale = Number.isFinite(measured) && measured !== 999 ? measured : null;
      }
    } finally { element.remove(); }
    let timer, localFontLoad = null, localFontRendering = null, localFontMetrics = null;
    const family = 'ProtonProfilesLocalFontProbe';
    let face, localFontConstructionBlocked=false;
    try {face=new target.FontFace(family, 'local("Arial")');}
    catch(e) {if(e?.name==='SecurityError')localFontConstructionBlocked=true;else throw e;}
    try {
      if (face) {
      localFontLoad = await Promise.race([
        face.load().then(() => true, e => e?.name === 'NetworkError' ? false : null),
        new Promise(resolve => { timer = setTimeout(() => resolve(null), 1500); })
      ]);
      if (localFontLoad === true) {
        target.document.fonts.add(face);
        const local = width(`"${family}", "Courier New"`), fallback = width('"Courier New"'), explicit = width('"Arial"');
        localFontMetrics = {local,fallback,explicit};
        if (fallback !== explicit) localFontRendering = local === explicit ? true : local === fallback ? false : null;
      }
      }
    } finally { clearTimeout(timer); if(face)target.document.fonts.delete(face); }
    return {status:'Observed',documentContext:true,displayPosture:collectDisplayPostureObservation(target),media,genericFonts,defaultFontSize,osTextScale,localFontLoad,localFontRendering,localFontMetrics,localFontConstructionBlocked};
  } catch { return {status:'NotPerformed',documentContext:true}; }
}
