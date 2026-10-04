import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

const html = readFileSync(new URL('../src/ProtonProfiles.App/Diagnostics/fingerprint.html', import.meta.url), 'utf8');
const canvasHelper = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/canvas-readback.v1.js', import.meta.url), 'utf8');
const audioHelper = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/audio-observation.v1.js', import.meta.url), 'utf8');
const screenHelper = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/screen-observation.v1.js', import.meta.url), 'utf8');
const speechHelper = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/speech-observation.v1.js', import.meta.url), 'utf8');
const hintsHelper = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/ua-hints-observation.v1.js', import.meta.url), 'utf8');
const fontHelper = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/font-access-observation.v1.js', import.meta.url), 'utf8');
const cpuHelper = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/cpu-observation.v1.js', import.meta.url), 'utf8');
const deviceHelper = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/hardware-devices-observation.v1.js', import.meta.url), 'utf8');
const pressureHelper = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/compute-pressure-observation.v1.js', import.meta.url), 'utf8');
const standardHelper = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/standard-fingerprint-observation.v1.js',import.meta.url),'utf8');
const additionalHelper = readFileSync(new URL("../src/ProtonProfiles.Core/Privacy/additional-fingerprint-observation.v1.js", import.meta.url), "utf8");
const realm = vm.createContext({URL});
vm.runInContext(pressureHelper, realm);
vm.runInContext(additionalHelper, realm);
vm.runInContext(deviceHelper, realm);
vm.runInContext(hintsHelper, realm);
const logic = html.match(/\/\/ BEGIN PURE DIAGNOSTIC LOGIC[^\n]*\n([\s\S]*?)\/\/ END PURE DIAGNOSTIC LOGIC/)[1];
vm.runInContext(logic, realm);
const ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/154.0.0.0 Safari/537.36';
const brands = [{brand: 'Microsoft Edge', version: '154'}, {brand: 'Not A(Brand', version: '99'},
  {brand: 'Microsoft Edge WebView2', version: '154'}, {brand: 'Chromium', version: '154'}];
const sections = () => ({
  'Браузер': {'User-Agent': ua, 'Соединение': '3g, rtt 250 мс, 0.45 Мбит/с', 'Ядра CPU (hardwareConcurrency)': 12},
  'Экран и отображение': {'Экран': '2048×1152', 'devicePixelRatio': 1.25, 'Окно (inner)': '1180×563', 'Окно (outer)': '1180×563', 'Доступно': '2048×1104'},
  'Графика и аппаратные отпечатки': {'Хэш Canvas': 'synthetic-a'},
});
const input = (s) => realm.stableFingerprintInput(s, 'Europe/Berlin', 'en-US');

test('Compute Pressure status requires both entry points absent in the right secure context', () => {
  const policy='BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuDevicesAndPressureExperimental';
  const absent={status:'Observed',secureContext:true,documentContext:true,observerAvailable:false,recordAvailable:false};
  const status=observation=>realm.computePressureObservationStatus(policy,observation);
  assert.equal(status(absent),'Pass');
  for (const key of Object.keys(absent)) {const partial={...absent};delete partial[key];assert.equal(status(partial),'NotPerformed');}
  for (const key of ['observerAvailable','recordAvailable']) {
    assert.equal(status({...absent,[key]:true}),'Fail');
    assert.equal(status({...absent,[key]:'false'}),'NotPerformed');
  }
  assert.equal(status({...absent,recordAvailable:undefined,observerAvailable:true}),'Fail');
  assert.equal(status({...absent,secureContext:false}),'NotPerformed');
  assert.equal(status({...absent,documentContext:false}),'NotPerformed');
  assert.equal(realm.computePressureObservationStatus(policy,{...absent,documentContext:false},true),'Pass');
  assert.equal(realm.computePressureObservationStatus('BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental',absent),'NotApplicable');
});

test('Compute Pressure observer never reads API getters, starts measurements or modifies globals', () => {
  let calls=0;
  const target=Object.create({get PressureObserver(){calls++;throw new Error('must not read');}});
  Object.assign(target,{isSecureContext:true,document:{}});
  const before=Object.getOwnPropertyDescriptors(target);
  const observation=realm.collectComputePressureObservation(target);
  assert.equal(observation.observerAvailable,true);assert.equal(observation.recordAvailable,false);
  assert.equal(calls,0);assert.deepEqual(Object.getOwnPropertyDescriptors(target),before);
  assert.equal(realm.collectComputePressureObservation(new Proxy({}, {has(){throw new Error('unavailable');}})).status,'NotPerformed');
});

test('Compute Pressure observations do not change environment ID input', () => {
  const first=sections(),second=sections();second['Compute Pressure']={PressureObserver:false,PressureRecord:false};
  assert.equal(input(first),input(second));
});

test('hardware device status requires complete absence in the correct secure document or worker', () => {
  const policy = 'BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental';
  const absent = JSON.parse(JSON.stringify(realm.collectHardwareDevicesObservation({navigator:{},document:{},isSecureContext:true})));
  const status = observation => realm.hardwareDevicesObservationStatus(policy,observation);
  assert.equal(status(absent),'Pass');
  for (const group of ['navigatorApis','constructors']) for (const name of Object.keys(absent[group])) {
    const exposed = structuredClone(absent); exposed[group][name] = true; assert.equal(status(exposed),'Fail');
    const partial = structuredClone(absent); delete partial[group][name]; assert.equal(status(partial),'NotPerformed');
    const malformed = structuredClone(absent); malformed[group][name] = 'false'; assert.equal(status(malformed),'NotPerformed');
  }
  for (const key of Object.keys(absent)) {const partial = {...absent}; delete partial[key]; assert.equal(status(partial),'NotPerformed');}
  assert.equal(status({...absent,secureContext:false}),'NotPerformed');
  assert.equal(status({...absent,navigatorApis:[]}),'NotPerformed');
  assert.equal(status({...absent,constructors:null}),'NotPerformed');
  assert.equal(status({...absent,documentContext:false}),'NotPerformed');
  assert.equal(realm.hardwareDevicesObservationStatus(policy,{...absent,documentContext:false},true),'Pass');
  assert.equal(realm.hardwareDevicesObservationStatus('BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental',absent),'NotApplicable');
});

test('device observer detects inherited APIs without reading getters, enumerating devices or modifying objects', () => {
  let reads = 0;
  const navigator = Object.create({get usb(){reads++;throw new Error('must not invoke device getters');}});
  const target = Object.create({USBDevice:function Native(){}});
  Object.assign(target,{navigator,document:{},isSecureContext:true});
  const before = Object.getOwnPropertyDescriptors(navigator);
  const observed = realm.collectHardwareDevicesObservation(target);
  assert.equal(observed.navigatorApis.usb,true); assert.equal(observed.constructors.USBDevice,true);
  assert.equal(reads,0); assert.deepEqual(Object.getOwnPropertyDescriptors(navigator),before);
  assert.equal(realm.collectHardwareDevicesObservation({}).status,'NotPerformed');
  assert.equal(realm.collectHardwareDevicesObservation({get navigator(){throw new Error('missing');}}).status,'NotPerformed');
});

test('hardware API observations and new explanatory section do not change environment ID input', () => {
  const first=sections(), second=sections();
  second['Аппаратные API']={usb:'недоступен',serial:'недоступен'};
  assert.equal(input(first),input(second));
});

test('environment ID ignores network speed, window size, available screen area and storage', () => {
  const a = sections(), b = sections();
  b['Браузер']['Соединение'] = '4g, rtt 50 мс, 10 Мбит/с';
  b['Экран и отображение']['Окно (inner)'] = '800×600';
  b['Экран и отображение']['Окно (outer)'] = '800×650';
  b['Экран и отображение']['Доступно'] = '2048×1000';
  b['Сеть'] = {IP: 'synthetic-proxy'};
  b['Хранилище, устройства и разрешения'] = {usage: '100'};
  assert.equal(input(a), input(b));
});
test('environment ID retains changes in hardware, timezone, locale and browser version', () => {
  const a = sections(), b = sections();
  b['Графика и аппаратные отпечатки']['Хэш Canvas'] = 'synthetic-b';
  assert.notEqual(input(a), input(b));
  assert.notEqual(input(a), realm.stableFingerprintInput(a, 'Africa/Nairobi', 'en-US'));
  assert.notEqual(input(a), realm.stableFingerprintInput(a, 'Europe/Berlin', 'de-DE'));
  b['Браузер']['User-Agent'] = ua.replace('154', '155');
  assert.notEqual(input(a), input(b));
});
test('matching Chromium version does not hide Edge WebView2 brand disagreement', () => {
  const checks = realm.userAgentChecks(ua, brands, 'Windows');
  assert.ok(checks.some(c => c.level === 'ok' && c.text.includes('Основная версия')));
  assert.ok(checks.some(c => c.level === 'warn' && c.text.includes('без Edg')));
  assert.ok(checks.some(c => c.level === 'info' && c.text.includes('WebView2')));
});
test('compares Chromium version rather than first matching Edge brand', () => {
  const checks = realm.userAgentChecks(ua, [{brand: 'Microsoft Edge', version: '154'}, {brand: 'Chromium', version: '153'}], 'Windows');
  assert.ok(checks.some(c => c.level === 'warn' && c.text.includes('153')));
  assert.ok(!checks.some(c => c.level === 'ok'));
});
test('native Edge UA and matching platform do not receive mismatch warnings', () => {
  const checks = realm.userAgentChecks(ua + ' Edg/154.0.4258.53', brands, 'Windows');
  assert.ok(!checks.some(c => c.level === 'warn'));
});
test('platform mismatch and Chrome versus Edge disagreement are reported', () => {
  assert.ok(realm.userAgentChecks(ua, brands, 'macOS').some(c => c.level === 'warn' && c.text.includes('Платформа')));
  assert.ok(realm.userAgentChecks(ua + ' Edg/154.0.0.0', [{brand: 'Google Chrome', version: '154'}], 'Windows').some(c => c.level === 'warn'));
});
test('server UA and Client Hints disagreements are detected without depending on header case or order', () => {
  const hints = [...brands].reverse().map(b => `"${b.brand}";v="${b.version}"`).join(', ');
  assert.equal(realm.serverUserAgentChecks(ua, brands, {'user-agent': ua, 'SEC-CH-UA': hints}).length, 0);
  assert.ok(realm.serverUserAgentChecks(ua, brands, {'User-Agent': 'changed-by-proxy'}).some(c => c.level === 'warn'));
  assert.ok(realm.serverUserAgentChecks(ua, brands, {'Sec-Ch-Ua': hints.replace('154', '153')}).some(c => c.level === 'warn'));
  assert.equal(realm.serverUserAgentChecks(ua, brands, {'Результат': 'ошибка сети'}).length, 0);
});
test('graphics readback never reports missing or failed observations as confirmed blocking', () => {
  const policy = 'BlockWebGlAndWebGpuExperimental';
  assert.equal(realm.graphicsObservationStatus(policy, {webGlAvailable: false, webGpuAdapterAvailable: false}), 'Pass');
  assert.equal(realm.graphicsObservationStatus(policy, {webGlAvailable: true, webGpuAdapterAvailable: false}), 'Fail');
  assert.equal(realm.graphicsObservationStatus(policy, {webGlAvailable: false, webGpuAdapterAvailable: true}), 'Fail');
  assert.equal(realm.graphicsObservationStatus(policy, {webGlAvailable: false, webGpuAdapterAvailable: null}), 'NotPerformed');
  assert.equal(realm.graphicsObservationStatus(policy, {status: 'NotPerformed'}), 'NotPerformed');
  assert.equal(realm.graphicsObservationStatus('RuntimeDefault', {}), 'NotApplicable');
  assert.equal(realm.graphicsObservationStatus('BlockWebGlWebGpuAndCanvasReadbackExperimental', {webGlAvailable:false, webGpuAdapterAvailable:false}), 'Pass');
});
test('Canvas status requires explicit security denials and successful drawing in each observed context', () => {
  const policy = 'BlockWebGlWebGpuAndCanvasReadbackExperimental';
  const blocked = {htmlSupported:true, offscreenSupported:true, htmlDrawing:true, offscreenDrawing:true,
    htmlGetImageData:'Blocked', htmlToDataURL:'Blocked', htmlToBlob:'Blocked', offscreenGetImageData:'Blocked', offscreenConvertToBlob:'Blocked'};
  assert.equal(realm.canvasObservationStatus(policy, blocked), 'Pass');
  assert.equal(realm.canvasObservationStatus(policy, {...blocked, htmlSupported:false, htmlDrawing:null,
    htmlGetImageData:'NotApplicable', htmlToDataURL:'NotApplicable', htmlToBlob:'NotApplicable'}, true), 'Pass');
  for (const key of Object.keys(blocked)) {
    const missing = {...blocked}; delete missing[key];
    assert.equal(realm.canvasObservationStatus(policy, missing), 'NotPerformed');
  }
  assert.equal(realm.canvasObservationStatus(policy, {offscreenConvertToBlob:'Readable'}, true), 'Fail');
  assert.equal(realm.canvasObservationStatus(policy, {...blocked, htmlGetImageData:'Unavailable'}), 'NotPerformed');
  assert.equal(realm.canvasObservationStatus(policy, {...blocked, offscreenDrawing:false}), 'NotPerformed');
  assert.equal(realm.canvasObservationStatus(policy, null), 'NotPerformed');
  assert.equal(realm.canvasObservationStatus('BlockWebGlAndWebGpuExperimental', blocked), 'NotApplicable');
});
test('shared Canvas collector distinguishes native security denials, working exports, errors and callback timeouts', async () => {
  for (const mode of ['readable', 'blocked', 'error', 'timeout', 'drawing-error']) {
    const failure = () => {
      if (mode === 'blocked') throw new DOMException('synthetic denial', 'SecurityError');
      if (mode === 'error') throw new TypeError('synthetic failure');
    };
    class Canvas {
      getContext() { return {
        fillRect() { if (mode === 'drawing-error') throw new Error('drawing failed'); },
        getImageData() { failure(); return {data:new Uint8ClampedArray([17,34,51,255])}; },
      }; }
      toDataURL() { failure(); return 'data:image/png;base64,synthetic'; }
      toBlob(callback) { failure(); if (mode !== 'timeout') callback(new Blob(['synthetic'])); }
      convertToBlob() { failure(); return mode === 'timeout' ? new Promise(() => {}) : Promise.resolve(new Blob(['synthetic'])); }
    }
    const nativeGetContext = Canvas.prototype.getContext;
    for (const worker of [false, true]) {
      const sandbox = vm.createContext({Blob, OffscreenCanvas:Canvas, clearTimeout,
        setTimeout: callback => setTimeout(callback, 5), ...(worker ? {} : {document:{createElement:()=>new Canvas()}})});
      vm.runInContext(canvasHelper, sandbox);
      const observation = await sandbox.collectCanvasReadback();
      assert.equal(Canvas.prototype.getContext, nativeGetContext);
      assert.equal(observation.htmlSupported, !worker);
      if (mode === 'drawing-error') { assert.equal(observation.offscreenDrawing, false); continue; }
      assert.equal(observation.offscreenDrawing, true);
      const expected = mode === 'readable' ? 'Readable' : mode === 'blocked' ? 'Blocked' : 'Unavailable';
      assert.equal(observation.offscreenConvertToBlob, expected);
      assert.equal(observation.offscreenGetImageData, mode === 'timeout' ? 'Readable' : expected);
      if (!worker) assert.equal(observation.htmlToBlob, expected);
      else assert.equal(observation.htmlToBlob, 'NotApplicable');
    }
  }
});
test('blocked Canvas hash does not abort graphics, audio or Math diagnostics', async () => {
  const collect = html.match(/async function collectGraphics\(\) \{[\s\S]*?\n\}/)[0];
  const sandbox = vm.createContext({navigator:{}, window:{}, report:{sections:{}},
    document:{createElement:()=>({getContext: type => type === '2d' ? {
      fillRect(){}, fillText(){}, beginPath(){}, arc(){}, fill(){} } : null,
      toDataURL(){throw new DOMException('blocked','SecurityError');}})},
    collectCanvasReadback: async () => ({htmlToDataURL:'Blocked'}),
    safeAsync: async action => { try { return await action(); } catch { return 'unavailable'; } },
    short: value => value, sha256: async () => 'synthetic-hash'});
  vm.runInContext(audioHelper, sandbox);
    vm.runInContext(screenHelper, sandbox);
    vm.runInContext(speechHelper, sandbox);
    vm.runInContext(hintsHelper, sandbox);
    vm.runInContext(fontHelper, sandbox);
    vm.runInContext(cpuHelper, sandbox);
    vm.runInContext(deviceHelper, sandbox);
    vm.runInContext(pressureHelper, sandbox);
    vm.runInContext(additionalHelper, sandbox);
    vm.runInContext(standardHelper, sandbox);
  vm.runInContext(collect, sandbox);
  const observation = await sandbox.collectGraphics();
  assert.equal(observation.canvasReadback.htmlToDataURL, 'Blocked');
  const graphics = sandbox.report.sections['Графика и аппаратные отпечатки'];
  assert.equal(graphics['Хэш Canvas'], 'чтение заблокировано');
  assert.equal(graphics['Хэш Audio'], 'недоступно');
  assert.equal(graphics.Math, 'synthetic-hash');
});
test('network observations validate address families and treat empty/error replies as missing evidence', () => {
  assert.equal(realm.ipObservation('198.51.100.1', 4).status, 'AddressObserved');
  assert.equal(realm.ipObservation('2001:db8::1', 6).status, 'AddressObserved');
  for (const value of ['', 'ошибка: таймаут', 'XXX.XXX.XXX.XXX', '300.1.1.1', '2001:db8::1'])
    assert.equal(realm.ipObservation(value, 4).status, 'NotObserved');
  for (const value of ['', '198.51.100.1', ':::', '2001:db8::1]'])
    assert.equal(realm.ipObservation(value, 6).status, 'NotObserved');
});
test('native WebGL readback rejects live contexts and uses supported OffscreenCanvas context names', () => {
  const source = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/GraphicsRestriction.cs', import.meta.url), 'utf8').replace(/\r\n/g, '\n');
  const script = source.match(/WebGlVerificationScript = """\n([\s\S]*?)\n\s*""";/)[1];
  const context = (canvasActive = '', offscreenActive = '') => vm.createContext({
    document: {createElement: () => ({getContext: type => type === canvasActive ? {} : null})},
    OffscreenCanvas: class { getContext(type) {
      if (!['webgl', 'webgl2'].includes(type)) throw new TypeError('unsupported enum');
      return type === offscreenActive ? {} : null;
    } },
  });
  const blocked = JSON.parse(JSON.stringify(vm.runInContext(script, context())));
  assert.deepEqual(blocked, {canvasWebGl: false, canvasExperimentalWebGl: false, canvasWebGl2: false,
    offscreenSupported: true, offscreenWebGl: false, offscreenWebGl2: false});
  for (const [type, key] of [['webgl', 'canvasWebGl'], ['experimental-webgl', 'canvasExperimentalWebGl'], ['webgl2', 'canvasWebGl2']])
    assert.equal(vm.runInContext(script, context(type))[key], true);
  // Regression: the native document flag can block HTML canvas while leaving OffscreenCanvas live.
  const partial = vm.runInContext(script, context('', 'webgl2'));
  assert.equal(partial.canvasWebGl2, false);
  assert.equal(partial.offscreenWebGl2, true);
  const throwing = context();
  throwing.OffscreenCanvas = class { getContext() { throw new Error('synthetic failure'); } };
  assert.equal(vm.runInContext(script, throwing).offscreenWebGl, null);
});
test('local worker observes native capabilities and releases its worker and Blob URL', async () => {
  const workerFunction = html.match(/async function collectWorkerContext\(\) \{[\s\S]*?\n\}/)[0];
  for (const result of ['adapter', 'none', 'error', 'timeout']) {
    let terminated = false, revoked = false, code;
    const sandbox = vm.createContext({
      Blob: class { constructor(parts) { code = parts.join(''); } },
      URL: {createObjectURL: () => 'blob:synthetic-local', revokeObjectURL: () => { revoked = true; }},
      withTimeout: promise => result === 'timeout' ? Promise.reject(new Error('synthetic timeout')) : promise,
      Worker: class {
        constructor() {
          queueMicrotask(() => {
            const workerRealm = vm.createContext({
              OffscreenCanvas: class { getContext() { return null; } },
              navigator: {userAgent:'synthetic-native',hardwareConcurrency: 8, deviceMemory: 8, gpu: {requestAdapter: async () => {
                if (result === 'error') throw new Error('synthetic adapter error');
                return result === 'adapter' ? {} : null;
              }}},
              postMessage: data => { if (!terminated) this.onmessage?.({data}); },
            });
            vm.runInContext(code, workerRealm);
          });
        }
        terminate() { terminated = true; }
      },
    });
    vm.runInContext(canvasHelper, sandbox);
    vm.runInContext(audioHelper, sandbox);
    vm.runInContext(screenHelper, sandbox);
    vm.runInContext(speechHelper, sandbox);
    vm.runInContext(hintsHelper, sandbox);
    vm.runInContext(fontHelper, sandbox);
    vm.runInContext(cpuHelper, sandbox);
    vm.runInContext(deviceHelper, sandbox);
    vm.runInContext(pressureHelper, sandbox);
    vm.runInContext(additionalHelper, sandbox);
    vm.runInContext(standardHelper, sandbox);
    vm.runInContext(workerFunction, sandbox);
    const observation = await sandbox.collectWorkerContext();
    assert.equal(terminated, true); assert.equal(revoked, true);
    if (result === 'timeout') assert.equal(observation.status, 'NotPerformed');
    else {
      assert.equal(observation.status, 'Observed');
      assert.equal(observation.hardwareConcurrency, 8);
      assert.equal(observation.webGlAvailable, false);
      assert.equal(observation.webGpuAdapterAvailable, result === 'error' ? null : result === 'adapter');
    }
  }
});
test('page script remains syntactically valid and emits versioned IDs and explicit missing route evidence', () => {
  new vm.Script(html.match(/<script>([\s\S]*?)<\/script>/)[1]);
  assert.match(html, /fingerprintVersion = 2/);
  assert.match(html, /stateFingerprintId/);
  assert.match(html, /proxyRoutes: "NotPerformed"/);
  assert.match(html, /allContextCoverage: "NotPerformed"/);
});

test('Audio status distinguishes guarded documents, natural worker absence and missing evidence', () => {
  const policy = 'BlockGraphicsCanvasAndWebAudioExperimental';
  assert.equal(realm.audioObservationStatus(policy, {windowApisAvailable:false, guardVerified:true}), 'Pass');
  assert.equal(realm.audioObservationStatus(policy, {windowApisAvailable:false, guardVerified:false}), 'Fail');
  assert.equal(realm.audioObservationStatus(policy, {windowApisAvailable:true, guardVerified:true}), 'Fail');
  assert.equal(realm.audioObservationStatus(policy, {windowApisAvailable:false, guardVerified:false}, true), 'NotApplicable');
  assert.equal(realm.audioObservationStatus(policy, {windowApisAvailable:true, guardVerified:false}, true), 'Fail');
  for (const value of [null, {}, {status:'NotPerformed'}, {windowApisAvailable:false}])
    assert.equal(realm.audioObservationStatus(policy, value), 'NotPerformed');
  assert.equal(realm.audioObservationStatus('RuntimeDefault', {}), 'NotApplicable');
  assert.equal(realm.graphicsObservationStatus(policy, {webGlAvailable:false, webGpuAdapterAvailable:false}), 'Pass');
});

test('DPR normalization needs complete numeric metrics and matching native media queries', () => {
  const policy = 'BlockGraphicsCanvasAudioAndNormalizeDprExperimental';
  const matching = {screenApisAvailable:true,width:1920,height:1080,availWidth:1920,availHeight:1080,availLeft:0,availTop:0,
    devicePixelRatio:1,orientationType:'landscape-primary',orientationAngle:0,deviceWidthMatches:true,deviceHeightMatches:true,resolutionMatches:true};
  assert.equal(realm.dprObservationStatus(policy, matching), 'Pass');
  assert.equal(realm.dprObservationStatus(policy, {...matching,devicePixelRatio:1.25},1.25), 'Pass');
  for (const key of ["screenApisAvailable","width","height","availWidth","availHeight","devicePixelRatio","deviceWidthMatches","deviceHeightMatches","resolutionMatches"]) {
    const incomplete = {...matching}; delete incomplete[key];
    assert.equal(realm.dprObservationStatus(policy, incomplete), 'NotPerformed');
  }
  for (const extra of [{devicePixelRatio:1.25},{resolutionMatches:false},{deviceWidthMatches:false}])
    assert.equal(realm.dprObservationStatus(policy, {...matching,...extra}), 'Fail');
  assert.equal(realm.dprObservationStatus(policy, {...matching,width:2048,height:1152}), 'Pass');
  assert.equal(realm.dprObservationStatus(policy, {screenApisAvailable:false},1,true), 'NotApplicable');
  assert.equal(realm.dprObservationStatus(policy, matching,1,true), 'Fail');
  assert.equal(realm.dprObservationStatus('RuntimeDefault', matching), 'NotApplicable');
  assert.equal(realm.dprObservationStatus(policy, null), 'NotPerformed');
});
test('Screen collector observes getters without modifying them, and workers expose no Window Screen API', () => {
  const target = {screen:{width:1920,height:1080,availWidth:1920,availHeight:1080,availLeft:0,availTop:0,orientation:{type:'landscape-primary',angle:0}},
    devicePixelRatio:1.25,matchMedia:q=>({matches:q.includes('1920px') || q.includes('1080px') || q.includes('1.25dppx')})};
  const before = target.matchMedia;
  const sandbox = vm.createContext({}); vm.runInContext(screenHelper,sandbox);
  const result = sandbox.collectScreenObservation(target);
  assert.equal(result.resolutionMatches,true); assert.equal(result.deviceWidthMatches,true);
  assert.equal(result.devicePixelRatio,1.25); assert.equal(target.matchMedia,before);
  assert.equal(sandbox.collectScreenObservation().screenApisAvailable,false);
  assert.equal(sandbox.collectScreenObservation({get screen(){throw new Error('unavailable');}}).status,'NotPerformed');
});

test('Speech status requires all entry points absent and distinguishes natural worker absence from blocking', () => {
  const policy = 'BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental';
  const absent = {synthesisAvailable:false,synthesisConstructorAvailable:false,utteranceConstructorAvailable:false,voiceConstructorAvailable:false};
  assert.equal(realm.speechObservationStatus(policy, absent), 'Pass');
  assert.equal(realm.speechObservationStatus(policy, absent, true), 'NotApplicable');
  for (const key of Object.keys(absent)) {
    const partial = {...absent}; delete partial[key];
    assert.equal(realm.speechObservationStatus(policy, partial), 'NotPerformed');
    assert.equal(realm.speechObservationStatus(policy, {[key]:true}), 'Fail');
    assert.equal(realm.speechObservationStatus(policy, {...absent,[key]:'false'}), 'NotPerformed');
    assert.equal(realm.speechObservationStatus(policy, {...absent,[key]:true}, true), 'Fail');
  }
  for (const invalid of [null, {}, [], {status:'NotPerformed'}])
    assert.equal(realm.speechObservationStatus(policy, invalid), 'NotPerformed');
  assert.equal(realm.speechObservationStatus('BlockGraphicsCanvasAudioAndNormalizeDprExperimental', absent), 'NotApplicable');
  assert.equal(realm.graphicsObservationStatus(policy, {webGlAvailable:false,webGpuAdapterAvailable:false}), 'Pass');
  assert.equal(realm.audioObservationStatus(policy, {windowApisAvailable:false,guardVerified:true}), 'Pass');
  assert.equal(realm.dprObservationStatus(policy, {screenApisAvailable:false}, 1, true), 'NotApplicable');
});

test('Speech observer does not enumerate voices or modify APIs and treats unreadable getters as missing evidence', () => {
  const sandbox = vm.createContext({}); vm.runInContext(speechHelper, sandbox);
    vm.runInContext(hintsHelper, sandbox);
    vm.runInContext(fontHelper, sandbox);
    vm.runInContext(cpuHelper, sandbox);
    vm.runInContext(deviceHelper, sandbox);
    vm.runInContext(pressureHelper, sandbox);
    vm.runInContext(additionalHelper, sandbox);
    vm.runInContext(standardHelper, sandbox);
  const synthesis = {getVoices(){throw new Error('observer must not enumerate voices');}};
  const ctor = function Native(){};
  const target = {speechSynthesis:synthesis,SpeechSynthesis:ctor,SpeechSynthesisUtterance:ctor,SpeechSynthesisVoice:ctor};
  const before = Object.getOwnPropertyDescriptors(target);
  const present = sandbox.collectSpeechObservation(target);
  assert.deepEqual(Object.values(present), [true,true,true,true]);
  assert.deepEqual(Object.getOwnPropertyDescriptors(target), before);
  assert.deepEqual(Object.values(sandbox.collectSpeechObservation({})), [false,false,false,false]);
  assert.equal(sandbox.collectSpeechObservation({get speechSynthesis(){throw new Error('unreadable');}}).status, 'NotPerformed');
});

test('UA hints readback rejects exposed identity, malformed observations and changed UA, including in workers', async () => {
  const policy = 'BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental';
  const blank = {status:'Observed',secureContext:true,userAgent:ua,sharedWorkerAvailable:false,uaDataAvailable:true,lowEntropy:{brands:[],platform:'',mobile:false},highEntropy:{brands:[],platform:'',mobile:false,architecture:'',bitness:'',model:'',platformVersion:'',uaFullVersion:'',fullVersionList:[],wow64:false,formFactors:[]}};
  assert.equal(realm.uaHintsObservationStatus(policy, blank, ua), 'Pass');
  assert.equal(realm.uaHintsObservationStatus(policy, blank, 'other'), 'Fail');
  assert.equal(realm.uaHintsObservationStatus(policy, {status:'Observed',secureContext:true,userAgent:ua,sharedWorkerAvailable:false,uaDataAvailable:false}, ua), 'Pass');
  assert.equal(realm.uaHintsObservationStatus(policy, {...blank,sharedWorkerAvailable:true},ua),'Fail');
  assert.equal(realm.uaHintsObservationStatus(policy, {...blank,secureContext:false}, ua), 'NotPerformed');
  for (const key of ['secureContext','status','userAgent','uaDataAvailable','lowEntropy','highEntropy','sharedWorkerAvailable']) {
    const partial={...blank};delete partial[key];
    assert.equal(realm.uaHintsObservationStatus(policy, partial, ua), 'NotPerformed');
  }
  for (const key of ['architecture','bitness','model','platformVersion','uaFullVersion']) {
    assert.equal(realm.uaHintsObservationStatus(policy, {...blank,highEntropy:{...blank.highEntropy,[key]:'native-value'}},ua), 'Fail');
    assert.equal(realm.uaHintsObservationStatus(policy, {...blank,highEntropy:{...blank.highEntropy,[key]:null}},ua), 'NotPerformed');
  }
  const data={brands:[],platform:'',mobile:false,async getHighEntropyValues(keys){assert.ok(keys.includes('platformVersion'));return blank.highEntropy;}};
  const target={isSecureContext:true,navigator:{userAgent:ua,userAgentData:data}};
  const before=Object.getOwnPropertyDescriptors(data);
  assert.equal(realm.uaHintsObservationOutcome(await realm.collectUaHintsObservation(target),ua),'Verified');
  assert.deepEqual(Object.getOwnPropertyDescriptors(data),before);
  assert.equal((await realm.collectUaHintsObservation({navigator:{userAgent:ua,userAgentData:{...data,getHighEntropyValues(){throw new Error('unavailable');}}}})).status,'NotPerformed');
  assert.equal(realm.uaHintsObservationStatus('RuntimeDefault',blank), 'NotApplicable');
});

test('UA hints HTTP echo needs a real UA header and treats any UA Client Hints as remaining exposure', () => {
  const policy='BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental';
  assert.equal(realm.uaHintsHttpStatus(policy,{'User-Agent':ua},ua),'Pass');
  assert.equal(realm.uaHintsHttpStatus(policy,{'User-Agent':'other'},ua),'Fail');
  assert.equal(realm.uaHintsHttpStatus(policy,{'user-agent':ua,'SEC-CH-UA-FULL-VERSION-LIST':'native-version'},ua),'Fail');
  assert.equal(realm.uaHintsHttpStatus(policy,{'User-Agent':ua,'Sec-Ch-Ua':''},ua),'Fail');
  for (const missing of [null,{},[],{'Error':'unavailable'}]) assert.equal(realm.uaHintsHttpStatus(policy,missing,ua),'NotPerformed');
  assert.equal(realm.uaHintsHttpStatus('RuntimeDefault',{'User-Agent':ua},ua),'NotApplicable');
});

test('Local Font Access requires secure document evidence and preserves natural worker absence', () => {
  const policy='BlockGraphicsCanvasAudioDprSpeechUaHintsAndFontAccessExperimental';
  const absent={status:'Observed',secureContext:true,documentContext:true,queryLocalFontsAvailable:false,fontDataAvailable:false};
  assert.equal(realm.fontAccessObservationStatus(policy,absent),'Pass');
  assert.equal(realm.fontAccessObservationStatus(policy,{...absent,documentContext:false},true),'NotApplicable');
  for (const key of Object.keys(absent)) {
    const partial={...absent};delete partial[key];
    assert.equal(realm.fontAccessObservationStatus(policy,partial),'NotPerformed');
  }
  assert.equal(realm.fontAccessObservationStatus(policy,{...absent,secureContext:false}),'NotPerformed');
  assert.equal(realm.fontAccessObservationStatus(policy,{...absent,queryLocalFontsAvailable:true}),'Fail');
  assert.equal(realm.fontAccessObservationStatus('RuntimeDefault',absent),'NotApplicable');
});
test('font observer neither requests permission nor reads or replaces font APIs', () => {
  const sandbox=vm.createContext({});vm.runInContext(fontHelper,sandbox);
  let calls=0;const query=()=>{calls++;};const font=function(){};
  const target={isSecureContext:true,document:{},queryLocalFonts:query,FontData:font};
  const descriptors=Object.getOwnPropertyDescriptors(target);
  assert.equal(sandbox.collectFontAccessObservation(target).queryLocalFontsAvailable,true);
  assert.equal(calls,0);assert.deepEqual(Object.getOwnPropertyDescriptors(target),descriptors);
  assert.equal(sandbox.collectFontAccessObservation({get queryLocalFonts(){throw Error();}}).status,'NotPerformed');
});

test('font metrics and environment ID are independent of collector version and Local Font Access availability', () => {
  const collect=html.match(/function collectFonts\(\) \{[\s\S]*?\n\}/)[0];
  const observe=(version,api)=>{
    const sandbox=vm.createContext({report:{applicationVersion:version,collectorHash:version,sections:{}},
      queryLocalFonts:api,document:{createElement:()=>({getContext:()=>({font:'',measureText(){return {width:this.font.includes('Arial')?2:1};}})})}});
    vm.runInContext(collect,sandbox);sandbox.collectFonts();return sandbox.report.sections;
  };
  const a=observe('0.1.11',()=>{throw Error('must never enumerate fonts');}),b=observe('0.1.13',undefined);
  assert.equal(input(a),input(b));
});

test('CPU readback requires a host bucket, matching native count and unmodified getter in both scopes', () => {
  const policy='BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental';
  const native={status:'Observed',hardwareConcurrency:8,nativeGetter:true,ownProperty:false};
  assert.equal(realm.cpuObservationStatus(policy,native,8),'Pass');
  assert.equal(realm.cpuObservationStatus(policy,{...native,hardwareConcurrency:12},8),'Fail');
  assert.equal(realm.cpuObservationStatus(policy,{...native,hardwareConcurrency:4},8),'Fail');
  assert.equal(realm.cpuObservationStatus(policy,{...native,nativeGetter:false},8),'Fail');
  assert.equal(realm.cpuObservationStatus(policy,{...native,ownProperty:true},8),'Fail');
  for (const key of Object.keys(native)) {const partial={...native};delete partial[key];assert.equal(realm.cpuObservationStatus(policy,partial,8),'NotPerformed');}
  for (const value of [null,12,0,'8',undefined]) assert.equal(realm.cpuObservationStatus(policy,native,value),'NotPerformed');
  assert.equal(realm.cpuObservationStatus('RuntimeDefault',native,8),'NotApplicable');
});
test('CPU observer leaves navigator and its prototype untouched and detects JavaScript replacements', () => {
  const sandbox=vm.createContext({});vm.runInContext(cpuHelper,sandbox);
  const prototype={get hardwareConcurrency(){return 12;}};const navigator=Object.create(prototype);
  const descriptors=Object.getOwnPropertyDescriptors(prototype);
  const result=sandbox.collectCpuObservation({navigator});
  assert.equal(result.hardwareConcurrency,12);assert.equal(result.ownProperty,false);assert.equal(result.nativeGetter,false);
  assert.deepEqual(Object.getOwnPropertyDescriptors(prototype),descriptors);
  assert.equal(Object.getOwnPropertyNames(navigator).length,0);
  assert.equal(sandbox.collectCpuObservation({navigator:{get hardwareConcurrency(){throw Error();}}}).status,'NotPerformed');
});

test('strict additional statuses require complete API, permission and native network observations', () => {
  const policy='StrictFingerprintExperimental';
  const apis=Object.fromEntries(['xr','cpuPerformance','measureMemory','getDisplayMedia','selectAudioOutput','AmbientLightSensor','Magnetometer','NDEFReader','NDEFRecord','NDEFMessage'].map(k=>[k,false]));
  const permissions=Object.fromEntries(['camera','microphone','geolocation','accelerometer','gyroscope','magnetometer','midi','camera-ptz','midi-sysex','idle-detection','window-management'].map(k=>[k,'denied']));
  const connection={status:'Observed',nativeGetters:true,effectiveType:'4g',rtt:150,downlink:1.5};
  const o={status:'Observed',secureContext:true,documentContext:true,apis,permissions,connection};
  const status=(observation,kind,worker=false)=>realm.additionalPrivacyStatus(policy,observation,kind,worker);
  assert.equal(status(o,'apis'),'Pass');assert.equal(status(o,'permissions'),'Pass');assert.equal(status(o,'network'),'Pass');
  for(const key of Object.keys(apis)) {
    const partial={...apis};delete partial[key];
    assert.equal(status({...o,apis:partial},'apis'),'NotPerformed');
    assert.equal(status({...o,apis:{...apis,[key]:true}},'apis'),'Fail');
  }
  for(const key of Object.keys(permissions)) {
    assert.equal(status({...o,permissions:{...permissions,[key]:'prompt'}},'permissions'),'Fail');
    assert.equal(status({...o,permissions:{...permissions,[key]:'NotPerformed'}},'permissions'),'NotPerformed');
  }
  assert.equal(status({...o,documentContext:false},'apis',true),'NotApplicable');
  assert.equal(status({...o,apis:{...apis,xr:true},documentContext:false},'apis',true),'Fail');
  assert.equal(status({...o,connection:{...connection,rtt:500}},'network'),'Fail');
  assert.equal(status({...o,connection:{...connection,effectiveType:'3g'}},'network'),'Fail');
  assert.equal(status({...o,connection:{...connection,nativeGetters:false}},'network'),'NotPerformed');
  assert.equal(status({...o,secureContext:false},'apis'),'NotPerformed');
  assert.equal(realm.additionalPrivacyStatus('RuntimeDefault',o,'apis'),'NotApplicable');
});

test('additional observer queries without requesting permissions or opening devices', async () => {
  const queries=[];let deviceReads=0;
  const n={permissions:{async query({name}){queries.push(name);return {state:'denied'};}},
    mediaDevices:Object.create({get getDisplayMedia(){deviceReads++;throw new Error('must not read');}})};
  const target={navigator:n,isSecureContext:true,document:{},performance:{}};
  const before=Object.getOwnPropertyDescriptors(target);
  const o=await realm.collectAdditionalFingerprintObservation(target);
  assert.equal(o.apis.getDisplayMedia,true);assert.equal(o.apis.cpuPerformance,false);assert.equal(o.apis.measureMemory,false);
  assert.equal(deviceReads,0);assert.equal(queries.length,11);
  assert.deepEqual(Object.getOwnPropertyDescriptors(target),before);
  assert.equal(o.connection.status,'NotPerformed');
  assert.equal((await realm.collectAdditionalFingerprintObservation({})).status,'NotPerformed');
});

test('additional observations and residual audit do not change environment ID input', () => {
  const first=sections(),second=sections();
  second['Дополнительная защита']={xr:false};second['Оставшиеся источники отпечатка']={deviceMemory:'Visible'};
  assert.equal(input(first),input(second));
  assert.equal(realm.computePressureObservationStatus('StrictFingerprintExperimental',{status:'Observed',secureContext:true,documentContext:true,observerAvailable:false,recordAvailable:false}),'Pass');
});

test('Native document defaults require complete readbacks; local errors and worker absence do not prove protection', () => {
  const policy='StrictFingerprintExperimental';
  const make=()=>({status:'Observed',documentContext:true,media:Object.fromEntries(['prefers-color-scheme','prefers-contrast','prefers-reduced-motion','prefers-reduced-data','prefers-reduced-transparency','forced-colors','color-gamut'].map(k=>[k,true])),genericFonts:{serif:true,sansSerif:true,fixed:true,cursive:true,fantasy:true},defaultFontSize:16,osTextScale:1,localFontLoad:true,localFontRendering:false});
  assert.equal(realm.standardPrivacyStatus(policy,make()),'Pass');
  for(const group of ['media','genericFonts']) for(const key of Object.keys(make()[group])) {
    const missing=make();delete missing[group][key];assert.equal(realm.standardPrivacyStatus(policy,missing),'NotPerformed');
    const wrong=make();wrong[group][key]=false;assert.equal(realm.standardPrivacyStatus(policy,wrong),'Fail');
  }
  for(const v of [null,undefined,'false']) assert.equal(realm.standardPrivacyStatus(policy,{...make(),localFontRendering:v}),'NotPerformed');
  assert.equal(realm.standardPrivacyStatus(policy,{...make(),localFontRendering:true}),'Fail');
  const unsupported=make();unsupported.media['prefers-reduced-data']=null;unsupported.media['prefers-reduced-transparency']=null;
  assert.equal(realm.standardPrivacyStatus(policy,unsupported),'Pass');
  assert.equal(realm.standardPrivacyStatus(policy,null,true),'NotApplicable');
  assert.equal(realm.standardPrivacyStatus('RuntimeDefault',make()),'NotApplicable');
});

test('Native defaults observer distinguishes local denial from unexpected errors and retains globals', async () => {
  const context=vm.createContext({setTimeout,clearTimeout});
  vm.runInContext(readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/standard-fingerprint-observation.v1.js',import.meta.url),'utf8'),context);
  let errorName=null, added=0, addedFonts=0;
  const widths={'32px serif':10,'32px "Times New Roman"':10,'32px sans-serif':20,'32px "Arial"':20,'32px monospace':30,'32px "Courier New"':30,'32px cursive':40,'32px "Comic Sans MS"':40,'32px fantasy':50,'32px "Impact"':50,'32px math':60,'32px "Cambria Math"':60,'32px "ProtonProfilesLocalFontProbe", "Courier New"':30};
  const canvasContext={font:'',measureText(){return {width:widths[this.font]};}};
  const target={document:{fonts:{add(){addedFonts++;},delete(){addedFonts--;}},documentElement:{appendChild(){}},querySelector(){return null;},createElement(){added++;return {style:{cssText:''},remove(){},getContext(){return canvasContext;}};}},getComputedStyle(){return {fontSize:'16px'};},matchMedia(query){return {matches:!query.includes('color-gamut: p3') && !query.includes('color-gamut: rec2020')};},FontFace:class {load(){return errorName ? Promise.reject({name:errorName}) : Promise.resolve();}}};
  const before=Object.getOwnPropertyDescriptors(target);
  const o=await context.collectStandardFingerprintObservation(target);
  assert.equal(o.localFontLoad,true);assert.equal(o.localFontRendering,false);assert.equal(addedFonts,0);assert.equal(added,2);assert.deepEqual(Object.getOwnPropertyDescriptors(target),before);
  assert.equal(realm.standardPrivacyStatus('StrictFingerprintExperimental',o),'Pass');
  errorName='SecurityError';assert.equal((await context.collectStandardFingerprintObservation(target)).localFontLoad,null);
  assert.equal((await context.collectStandardFingerprintObservation({})).status,'NotApplicable');
});
