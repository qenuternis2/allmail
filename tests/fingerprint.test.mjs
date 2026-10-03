import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

const html = readFileSync(new URL('../src/ProtonProfiles.App/Diagnostics/fingerprint.html', import.meta.url), 'utf8');
const realm = vm.createContext({URL});
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
              navigator: {hardwareConcurrency: 8, deviceMemory: 8, gpu: {requestAdapter: async () => {
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
