import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFileSync} from 'node:fs';

// Descriptor semantics only. Actual WebView2 injection is tested on Windows.
const guard = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/audio-guard.v1.js', import.meta.url), 'utf8');
const observation = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/audio-observation.v1.js', import.meta.url), 'utf8');
const names = ['AudioContext', 'webkitAudioContext', 'OfflineAudioContext', 'webkitOfflineAudioContext'];
function realm(setup = '') {
  const context = vm.createContext({});
  vm.runInContext(observation + '\n' + setup, context);
  return context;
}
function observe(context) { return JSON.parse(JSON.stringify(context.collectAudioGuardObservation())); }

test('positive control renders synthetic offline audio, then all standard and legacy constructors are blocked', async () => {
  const context = realm(names.map(name => `globalThis.${name} = class {startRendering(){return Promise.resolve({length:128});}};`).join('\n'));
  assert.equal((await vm.runInContext('new OfflineAudioContext().startRendering()', context)).length, 128);
  assert.deepEqual(observe(context), {windowApisAvailable:true, guardVerified:false});
  for (let attempt = 0; attempt < 2; attempt++) {
    assert.equal(vm.runInContext(guard, context), true);
    assert.deepEqual(observe(context), {windowApisAvailable:false, guardVerified:true});
    for (const name of names) {
      assert.throws(() => vm.runInContext(`new globalThis.${name}()`, context), /not a constructor/);
      assert.throws(() => vm.runInContext(`Object.defineProperty(globalThis, '${name}', {value:function(){}})`, context), /redefine/);
    }
  }
});
test('natural absence is not verified blocking, and readback never changes APIs', () => {
  const context = realm();
  const before = vm.runInContext('Object.getOwnPropertyNames(globalThis).join()', context);
  assert.deepEqual(observe(context), {windowApisAvailable:false, guardVerified:false});
  assert.equal(vm.runInContext('Object.getOwnPropertyNames(globalThis).join()', context), before);
  vm.runInContext('globalThis.AudioContext = undefined;', context);
  assert.equal(observe(context).guardVerified, false);
});
test('prototype entry points are sealed without keeping a constructor or restoration object', () => {
  const context = realm('Object.setPrototypeOf(globalThis, {OfflineAudioContext:function Native(){}});');
  const before = Array.from(vm.runInContext('Object.getOwnPropertyNames(globalThis)', context));
  vm.runInContext(guard, context);
  assert.equal(vm.runInContext('Object.getPrototypeOf(globalThis).OfflineAudioContext', context), undefined);
  assert.deepEqual(Array.from(vm.runInContext('Object.getOwnPropertyNames(globalThis)', context)).filter(n => !before.includes(n)).sort(), names.slice().sort());
  vm.runInContext('Object.setPrototypeOf(globalThis, {OfflineAudioContext:function Bypass(){}});', context);
  assert.equal(observe(context).guardVerified, false);
});
test('conflicting immutable native descriptors fail visibly before changing earlier entry points', () => {
  for (const descriptor of ['{value:function Native(){}, writable:false}', '{get(){return function Native(){};}}']) {
    const context = realm(`globalThis.AudioContext=function Keep(){}; Object.defineProperty(globalThis,'OfflineAudioContext',${descriptor});`);
    assert.throws(() => vm.runInContext(guard, context), /conflicting descriptor/);
    assert.equal(vm.runInContext('typeof AudioContext', context), 'function');
    assert.equal(observe(context).guardVerified, false);
  }
});
test('unreadable observations are unavailable; HTML media APIs and unrelated JavaScript stay usable', () => {
  const context = realm('globalThis.Audio=class {play(){return "media";}}; globalThis.navigator={language:"ru-RU"};');
  vm.runInContext(guard, context);
  assert.equal(vm.runInContext('new Audio().play()', context), 'media');
  assert.equal(vm.runInContext('navigator.language', context), 'ru-RU');
  const broken = realm("Object.defineProperty(globalThis,'AudioContext',{get(){throw new Error('unavailable');}});");
  assert.deepEqual(observe(broken), {status:'NotPerformed'});
});
