// Shared by the owned startup document, bundled probe and native Windows fixture.
// Observations only: no API replacements or saved native functions.
async function collectCanvasReadback() {
  const result = {htmlSupported: typeof document === 'object', offscreenSupported: typeof OffscreenCanvas === 'function',
    htmlDrawing: null, htmlGetImageData: 'NotApplicable', htmlToDataURL: 'NotApplicable', htmlToBlob: 'NotApplicable',
    offscreenDrawing: null, offscreenGetImageData: 'NotApplicable', offscreenConvertToBlob: 'NotApplicable'};
  const bounded = promise => new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('readback timeout')), 2000);
    Promise.resolve(promise).then(value => { clearTimeout(timer); resolve(value); },
      error => { clearTimeout(timer); reject(error); });
  });
  const check = async (action, valid) => {
    try { return valid(await bounded(action())) ? 'Readable' : 'Unavailable'; }
    catch (error) { return error?.name === 'SecurityError' ? 'Blocked' : 'Unavailable'; }
  };
  const draw = canvas => {
    canvas.width = 4; canvas.height = 4;
    const context = canvas.getContext('2d');
    if (!context) throw new Error('2D context unavailable');
    context.fillStyle = '#112233'; context.fillRect(0, 0, 1, 1);
    return context;
  };
  if (result.htmlSupported) {
    try {
      const canvas = document.createElement('canvas'), context = draw(canvas);
      result.htmlDrawing = true;
      result.htmlGetImageData = await check(() => context.getImageData(0, 0, 1, 1), value => value?.data?.length === 4);
      result.htmlToDataURL = await check(() => canvas.toDataURL(), value => typeof value === 'string' && value.startsWith('data:image/'));
      result.htmlToBlob = await check(() => new Promise((resolve, reject) => {
        try { canvas.toBlob(resolve); } catch (error) { reject(error); }
      }), value => value instanceof Blob && value.size > 0);
    } catch { result.htmlDrawing = false; }
  }
  if (result.offscreenSupported) {
    try {
      const canvas = new OffscreenCanvas(4, 4), context = draw(canvas);
      result.offscreenDrawing = true;
      result.offscreenGetImageData = await check(() => context.getImageData(0, 0, 1, 1), value => value?.data?.length === 4);
      result.offscreenConvertToBlob = await check(() => canvas.convertToBlob(), value => value instanceof Blob && value.size > 0);
    } catch { result.offscreenDrawing = false; }
  }
  return result;
}
