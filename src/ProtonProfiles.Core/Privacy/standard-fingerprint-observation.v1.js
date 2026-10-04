// Observe native document media, generic font metrics and a disposable local FontFace.
// No installed-font enumeration or global replacements; disposable font-set entry is always removed.
async function collectStandardFingerprintObservation(target = globalThis) {
  try {
    if (!target.document) return {status:'NotApplicable',documentContext:false};
    const expected = {'prefers-color-scheme':'light','prefers-contrast':'no-preference',
      'prefers-reduced-motion':'no-preference','prefers-reduced-data':'no-preference',
      'prefers-reduced-transparency':'no-preference','forced-colors':'none','color-gamut':'srgb'};
    const media = Object.fromEntries(Object.entries(expected).map(([k,v]) => {
      const supported = !['prefers-reduced-data','prefers-reduced-transparency'].includes(k)
        || target.matchMedia(`(${k}: reduce), (${k}: no-preference)`).matches;
      return [k,supported ? target.matchMedia(`(${k}: ${v})`).matches : null];
    }));
    const canvas = target.document.createElement('canvas'), ctx = canvas.getContext('2d');
    if (!ctx) return {status:'NotPerformed',documentContext:true};
    const width = family => { ctx.font = `32px ${family}`; return ctx.measureText('Wim0123@# Съешь').width; };
    const genericFonts = {serif:width('serif')===width('"Times New Roman"'),
      sansSerif:width('sans-serif')===width('"Arial"'),fixed:width('monospace')===width('"Courier New"'),
      cursive:width('cursive')===width('"Comic Sans MS"'),fantasy:width('fantasy')===width('"Impact"')};
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
    const face = new target.FontFace(family, 'local("Arial")');
    try {
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
    } finally { clearTimeout(timer); target.document.fonts.delete(face); }
    return {status:'Observed',documentContext:true,media,genericFonts,defaultFontSize,osTextScale,localFontLoad,localFontRendering,localFontMetrics};
  } catch { return {status:'NotPerformed',documentContext:true}; }
}
