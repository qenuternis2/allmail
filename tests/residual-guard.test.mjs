import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFileSync} from 'node:fs';
const guard=readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/residual-fingerprint-guard.v1.js',import.meta.url),'utf8');
const observer=readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/residual-fingerprint-observation.v1.js',import.meta.url),'utf8');
function context(document=true) {
  const c=vm.createContext({DOMException});
  vm.runInContext(`
    class Navigator {get deviceMemory(){return 32;}getBattery(){throw new Error('must not call');}getGamepads(){throw new Error('must not call');}}
    globalThis.navigator=new Navigator();navigator.mediaDevices={enumerateDevices(){throw new Error('must not call');}};
    navigator.mediaCapabilities={};navigator.getScreenDetails=()=>{};
    navigator.storage={estimate(){throw new Error('must not call');},persisted:()=>true};
    globalThis.performance={memory:{usedJSHeapSize:123},now:()=>42};
    globalThis.FontFace=class FontFace {constructor(family,source){this.family=family;this.source=source;}load(){return Promise.resolve(this);}};
    globalThis.Accelerometer=class {};globalThis.IdleDetector=class {};
    ${document?'globalThis.document={};':''}
  `,c);
  vm.runInContext(observer,c);
  return c;
}
test('strict document guard masks RAM and blocks APIs without calling devices',()=>{
  const c=context();vm.runInContext(guard,c);
  const o=c.collectResidualFingerprintObservation();assert.equal(c.residualFingerprintOutcome(o),'Verified');
  assert.equal(o.deviceMemory,8);assert.equal(o.scriptRestriction,true);assert.equal(o.documentContext,true);
  assert.equal(vm.runInContext('navigator.storage.persisted() && performance.now()===42',c),true);
});
test('worker receives the same restrictions without a document',()=>{
  const c=context(false);vm.runInContext(guard,c);
  assert.equal(c.residualFingerprintOutcome(c.collectResidualFingerprintObservation()),'Verified');
  assert.equal(c.collectResidualFingerprintObservation().documentContext,false);
});
test('locked navigator and prototype restrictions cannot be restored and installation is idempotent',()=>{
  const c=context();vm.runInContext(guard,c);vm.runInContext(guard,c);
  assert.equal(vm.runInContext('delete navigator.deviceMemory',c),false);
  assert.throws(()=>vm.runInContext('Object.defineProperty(Object.getPrototypeOf(navigator),"deviceMemory",{value:32})',c));
  assert.equal(c.collectResidualFingerprintObservation().deviceMemory,8);
});
test('local FontFace sources, CSS escapes, comments and source conversions are blocked',()=>{
  const c=context();vm.runInContext(guard,c);
  for(const source of ['local("Arial")','LOCAL (Arial)','lo\\63 al(Arial)','local/**/(Arial)','url("https://example.test/f.woff2"),local(Arial)']) {
    c.testSource=source;assert.throws(()=>vm.runInContext('new FontFace("probe",testSource)',c),e=>e.name==='SecurityError');
  }
  assert.throws(()=>vm.runInContext('new FontFace("probe",{toString(){return "local(Arial)"}})',c),e=>e.name==='SecurityError');
});
test('FontFace prototype constructor cannot bypass guard; URL and binary fonts remain usable',()=>{
  const c=context();vm.runInContext(guard,c);
  assert.equal(vm.runInContext('new FontFace("probe", "url(https://example.test/font.woff2)").source',c),'url(https://example.test/font.woff2)');
  assert.throws(()=>vm.runInContext('new FontFace.prototype.constructor("probe","local(Arial)")',c),e=>e.name==='SecurityError');
  assert.equal(vm.runInContext('new FontFace("probe",new ArrayBuffer(8)).source.byteLength',c),8);
});
test('conflicting immutable API fails installation instead of reporting protection',()=>{
  const c=context();vm.runInContext('Object.defineProperty(navigator,"deviceMemory",{value:32,configurable:false})',c);
  assert.throws(()=>vm.runInContext(guard,c),/conflicting deviceMemory/);
  assert.equal(c.residualFingerprintOutcome(c.collectResidualFingerprintObservation()),'Violation');
});
test('residual readback rejects missing fields and distinguishes script restriction from native RAM',()=>{
  const c=context();assert.equal(c.residualFingerprintOutcome(c.collectResidualFingerprintObservation()),'Violation');
  vm.runInContext(guard,c);const o=c.collectResidualFingerprintObservation();
  for(const key of Object.keys(o)){const partial={...o};delete partial[key];if(key!=='documentContext')assert.equal(c.residualFingerprintOutcome(partial),'Unavailable',key);}
  assert.equal(c.residualFingerprintOutcome({...o,deviceMemory:32}),'Violation');
  assert.equal(c.residualFingerprintOutcome({...o,localFontConstructionBlocked:false}),'Violation');
});
