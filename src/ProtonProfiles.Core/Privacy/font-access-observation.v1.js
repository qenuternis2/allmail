// Observe entry points only: never enumerate local fonts, request permission or read font files.
function collectFontAccessObservation(target = globalThis) {
  try {
    return {status:'Observed', secureContext:target.isSecureContext === true,
      documentContext:typeof target.document === 'object',
      queryLocalFontsAvailable:typeof target.queryLocalFonts !== 'undefined',
      fontDataAvailable:typeof target.FontData !== 'undefined'};
  } catch { return {status:'NotPerformed'}; }
}
