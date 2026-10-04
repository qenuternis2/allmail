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
    return {status:'Observed',documentContext:true,media,genericFonts,defaultFontSize,osTextScale,localFontLoad,localFontRendering,localFontMetrics,localFontConstructionBlocked};
  } catch { return {status:'NotPerformed',documentContext:true}; }
}
