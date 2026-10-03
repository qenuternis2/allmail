import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

// Tests JavaScript descriptor semantics in a VM, not WebView2 injection or network enforcement.
const script = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/webrtc-guard.v1.js', import.meta.url), 'utf8');
const hostSource = readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/WebRtcPageGuard.cs', import.meta.url), 'utf8');
const verifyScript = hostSource.match(/VerifyScript = """\n([\s\S]*?)\n\s*""";/)[1];
const names = ['RTCPeerConnection', 'webkitRTCPeerConnection', 'RTCIceTransport', 'RTCDtlsTransport', 'RTCSctpTransport'];
function context(setup = '') {
  const realm = vm.createContext({});
  vm.runInContext(setup, realm);
  return realm;
}
function verify(realm) {
  for (const name of names) {
    assert.equal(vm.runInContext(`typeof globalThis.${name}`, realm), 'undefined');
    assert.throws(() => vm.runInContext(`new globalThis.${name}()`, realm), /not a constructor/);
    const d = vm.runInContext(`Object.getOwnPropertyDescriptor(globalThis, '${name}')`, realm);
    assert.equal(d.writable, false);
    assert.equal(d.configurable, false);
    assert.throws(() => vm.runInContext(`Object.defineProperty(globalThis, '${name}', {value: function(){}})`, realm), /redefine/);
  }
}
test('positive control constructs a usable synthetic peer before protection', () => {
  const realm = context('globalThis.RTCPeerConnection = class { createDataChannel() { return {label: "test"}; } };');
  assert.equal(vm.runInContext('new RTCPeerConnection().createDataChannel().label', realm), 'test');
  assert.equal(vm.runInContext(script, realm), true);
  verify(realm);
});
test('standard, legacy and present transport entry points are immutable and repeatable', () => {
  const realm = context(names.map(n => `globalThis.${n} = function Native(){};`).join('\n'));
  vm.runInContext(script, realm);
  verify(realm);
  assert.equal(vm.runInContext(script, realm), true);
  verify(realm);
});
test('no prototype constructor or restoration/debug object is exposed', () => {
  const realm = context('Object.setPrototypeOf(globalThis, { RTCPeerConnection: function Native(){} });');
  const before = vm.runInContext('Object.getOwnPropertyNames(globalThis)', realm);
  vm.runInContext(script, realm);
  assert.equal(vm.runInContext('Object.getPrototypeOf(globalThis).RTCPeerConnection', realm), undefined);
  const after = vm.runInContext('Object.getOwnPropertyNames(globalThis)', realm);
  assert.deepEqual(Array.from(after).filter(n => !before.includes(n)).sort(), names.slice().sort());
  verify(realm);
});
test('preserves enumerability and seals a nonconfigurable writable native property', () => {
  const realm = context('Object.defineProperty(globalThis, "RTCPeerConnection", {value: function Native(){}, writable: true, configurable: false, enumerable: true});');
  vm.runInContext(script, realm);
  assert.equal(vm.runInContext('Object.getOwnPropertyDescriptor(globalThis, "RTCPeerConnection").enumerable', realm), true);
  verify(realm);
});
for (const setup of [
  'Object.defineProperty(globalThis, "RTCPeerConnection", {value: function Native(){}, configurable: false, writable: false});',
  'Object.defineProperty(globalThis, "RTCPeerConnection", {get(){return function Native(){};}, configurable: false});',
  'Object.preventExtensions(globalThis);',
]) {
  test(`installation conflict is observable: ${setup}`, () => {
    // A vm global itself cannot be made non-extensible; use a regular sandbox object for that case.
    if (setup.includes('preventExtensions')) {
      const target = Object.preventExtensions({});
      const realm = vm.createContext({ globalThis: target });
      assert.throws(() => vm.runInContext(script, realm), /extensible/);
    } else {
      const realm = context(setup);
      assert.throws(() => vm.runInContext(script, realm), /conflicting descriptor/);
    }
  });
}
test('normal JavaScript and unrelated browser properties remain usable', () => {
  const realm = context('globalThis.navigator = {language:"ru-RU"}; globalThis.Canvas = function Canvas(){};');
  vm.runInContext(script, realm);
  assert.equal(vm.runInContext('navigator.language', realm), 'ru-RU');
  assert.equal(vm.runInContext('typeof Canvas', realm), 'function');
  assert.equal(vm.runInContext('[1,2,3].map(n=>n*2).join(",")', realm), '2,4,6');
});
test('host readback detects an unguarded or writable undefined entry point', () => {
  const realm = context();
  assert.equal(vm.runInContext(verifyScript, realm), false);
  vm.runInContext('globalThis.RTCPeerConnection = undefined;', realm);
  assert.equal(vm.runInContext(verifyScript, realm), false);
  vm.runInContext(script, realm);
  assert.equal(vm.runInContext(verifyScript, realm), true);
});
test('host readback rejects an inherited constructor even when own names are sealed', () => {
  const realm = context();
  vm.runInContext(script, realm);
  vm.runInContext('Object.setPrototypeOf(globalThis, {RTCPeerConnection: function Native(){}});', realm);
  assert.equal(vm.runInContext(verifyScript, realm), false);
});
