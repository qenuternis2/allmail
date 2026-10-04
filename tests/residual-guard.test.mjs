import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFileSync} from 'node:fs';
const guard=readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/residual-fingerprint-guard.v1.js',import.meta.url),'utf8').replace('/*__PP_COARSE_CLOCK_GUARD__*/',readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/coarse-clock-guard.v1.js',import.meta.url),'utf8'));
const observer=readFileSync(new URL('../src/ProtonProfiles.Core/Privacy/residual-fingerprint-observation.v1.js',import.meta.url),'utf8');
const codecNames=['AudioDecoder','VideoDecoder','AudioEncoder','VideoEncoder','AudioData','VideoFrame','EncodedAudioChunk','EncodedVideoChunk'];
function context(document=true) {
  const c=vm.createContext({DOMException});
  vm.runInContext(`
    class Navigator {get deviceMemory(){return 32;}getBattery(){throw new Error('must not call');}getGamepads(){throw new Error('must not call');}}
    globalThis.navigator=new Navigator();navigator.mediaDevices={enumerateDevices(){throw new Error('must not call');}};
    navigator.mediaCapabilities={};globalThis.getScreenDetails=()=>{};
    navigator.storage={estimate(){throw new Error('must not call');},persisted:()=>true};
    globalThis.performance={timeOrigin:1000,memory:{usedJSHeapSize:123},now:()=>42};
    globalThis.FontFace=class FontFace {constructor(family,source){this.family=family;this.source=source;}load(){return Promise.resolve(this);}};
    globalThis.Accelerometer=class {};globalThis.IdleDetector=class {};
    ${document?'globalThis.document={};globalThis.screen={width:2560,height:1440,availWidth:2560,availHeight:1380,availLeft:0,availTop:0};globalThis.screenX=700;globalThis.screenY=90;globalThis.screenLeft=700;globalThis.screenTop=90;':''}
  `,c);
  vm.runInContext(observer,c);
  return c;
}
test('strict document guard masks RAM and blocks APIs without calling devices',()=>{
  const c=context();vm.runInContext(guard,c);
  const o=c.collectResidualFingerprintObservation();assert.equal(c.residualFingerprintOutcome(o),'Verified');
  assert.equal(o.deviceMemory,8);assert.equal(o.scriptRestriction,true);assert.equal(o.documentContext,true);
  assert.equal(vm.runInContext('navigator.storage.persisted() && performance.now()%100===0',c),true);
});
test('worker receives the same restrictions without a document',()=>{
  const c=context(false);vm.runInContext(guard,c);
  assert.equal(c.residualFingerprintOutcome(c.collectResidualFingerprintObservation()),'Verified');
  assert.equal(c.collectResidualFingerprintObservation().documentContext,false);
});
test('WebCodecs constructors, capability queries and prototype constructors are closed in documents and workers',()=>{
  for(const document of [true,false]) {
    const c=context(document);
    c.codecNames=codecNames;
    vm.runInContext(`globalThis.codecPrototypes=[];
      for(const name of codecNames){globalThis[name]=class {static isConfigSupported(){return true;}};codecPrototypes.push(globalThis[name].prototype);}
      globalThis.HTMLMediaElement=class {canPlayType(){return 'probably';}};
      globalThis.MediaSource=class {static isTypeSupported(){return true;}};`,c);
    assert.ok(Object.values(c.collectResidualFingerprintObservation().webCodecs).every(v=>v===true));
    vm.runInContext(guard,c);vm.runInContext(guard,c);
    assert.ok(Object.values(c.collectResidualFingerprintObservation().webCodecs).every(v=>v===false));
    assert.equal(vm.runInContext('codecPrototypes.every(p=>p.constructor===undefined)',c),true);
    for(const name of codecNames) {
      c.codecName=name;
      assert.throws(()=>vm.runInContext('Object.defineProperty(globalThis,codecName,{value:class {}})',c));
    }
    assert.throws(()=>vm.runInContext('Object.defineProperty(codecPrototypes[0],"constructor",{value:class {}})',c));
    assert.equal(vm.runInContext('new HTMLMediaElement().canPlayType()',c),'probably');
    assert.equal(vm.runInContext('MediaSource.isTypeSupported()',c),true);
  }
});
test('WebCodecs readback cannot pass with a missing, nonboolean or exposed entry point',()=>{
  const c=context();vm.runInContext(guard,c);const o=c.collectResidualFingerprintObservation();
  for(const name of codecNames) {
    const missing={...o,webCodecs:{...o.webCodecs}};delete missing.webCodecs[name];
    assert.equal(c.residualFingerprintOutcome(missing),'Unavailable');
    assert.equal(c.residualFingerprintOutcome({...o,webCodecs:{...o.webCodecs,[name]:null}}),'Unavailable');
    assert.equal(c.residualFingerprintOutcome({...o,webCodecs:{...o.webCodecs,[name]:true}}),'Violation');
  }
});
test('immutable WebCodecs conflict stops installation',()=>{
  const c=context();vm.runInContext('Object.defineProperty(globalThis,"VideoDecoder",{value:class {},configurable:false})',c);
  assert.throws(()=>vm.runInContext(guard,c),/conflicting VideoDecoder/);
  assert.equal(c.residualFingerprintOutcome(c.collectResidualFingerprintObservation()),'Violation');
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
  for(const key of Object.keys(o)){const partial={...o};delete partial[key];if(!['documentContext','mathPow'].includes(key))assert.equal(c.residualFingerprintOutcome(partial),'Unavailable',key);}
  assert.equal(c.residualFingerprintOutcome({...o,deviceMemory:32}),'Violation');
  assert.equal(c.residualFingerprintOutcome({...o,localFontConstructionBlocked:false}),'Violation');
});

test('keyboard layout and lock paths are closed, prototype aliases locked, ordinary key events untouched',()=>{
  const c=context();vm.runInContext(`
    class Keyboard {getLayoutMap(){throw Error('must not collect layout');}lock(){throw Error('must not lock keyboard');}unlock(){}}
    globalThis.Keyboard=Keyboard;globalThis.KeyboardLayoutMap=class KeyboardLayoutMap {};
    Object.defineProperty(Object.getPrototypeOf(navigator),'keyboard',{get(){return savedKeyboard;},configurable:true});
    globalThis.savedKeyboard=new Keyboard();globalThis.savedKeyboardPrototype=Keyboard.prototype;
    globalThis.savedMapPrototype=KeyboardLayoutMap.prototype;
    globalThis.KeyboardEvent=class KeyboardEvent {constructor(type,options){this.type=type;this.key=options.key;}};
    globalThis.savedKeyboardEvent=KeyboardEvent;
  `,c);
  assert.ok(Object.values(c.collectResidualFingerprintObservation().keyboardLayout).every(v=>v===true));
  vm.runInContext(guard,c);vm.runInContext(guard,c);
  assert.ok(Object.values(c.collectResidualFingerprintObservation().keyboardLayout).every(v=>v===false));
  assert.equal(vm.runInContext("['getLayoutMap','lock','unlock'].every(k=>savedKeyboard[k]===undefined && savedKeyboardPrototype[k]===undefined)",c),true);
  assert.equal(vm.runInContext('savedKeyboardPrototype.constructor===undefined && savedMapPrototype.constructor===undefined',c),true);
  for(const path of ['navigator','Object.getPrototypeOf(navigator)'])
    assert.throws(()=>vm.runInContext(`Object.defineProperty(${path},'keyboard',{value:savedKeyboard})`,c));
  for(const name of ['getLayoutMap','lock','unlock'])
    assert.throws(()=>vm.runInContext(`Object.defineProperty(savedKeyboardPrototype,'${name}',{value:()=>{}})`,c));
  assert.equal(vm.runInContext("KeyboardEvent===savedKeyboardEvent && new KeyboardEvent('keydown',{key:'Enter'}).key==='Enter'",c),true);
});
test('keyboard readback requires every field and never confuses incomplete observations with protection',()=>{
  const c=context();vm.runInContext(guard,c);const o=c.collectResidualFingerprintObservation();
  for(const name of Object.keys(o.keyboardLayout)) {
    const partial={...o,keyboardLayout:{...o.keyboardLayout}};delete partial.keyboardLayout[name];
    assert.equal(c.residualFingerprintOutcome(partial),'Unavailable');
    assert.equal(c.residualFingerprintOutcome({...o,keyboardLayout:{...o.keyboardLayout,[name]:null}}),'Unavailable');
    assert.equal(c.residualFingerprintOutcome({...o,keyboardLayout:{...o.keyboardLayout,[name]:true}}),'Violation');
  }
});
test('immutable keyboard layout API stops strict installation',()=>{
  const c=context();vm.runInContext("Object.defineProperty(navigator,'keyboard',{value:{getLayoutMap(){}},configurable:false})",c);
  assert.throws(()=>vm.runInContext(guard,c),/conflicting keyboard/);
  assert.equal(c.residualFingerprintOutcome(c.collectResidualFingerprintObservation()),'Violation');
});

test('each residual exception leaves only its feature exposed and remaining APIs locked',()=>{
  const entries=[['Battery','getBattery'],['Gamepads','getGamepads'],['MediaDevices','mediaDevices'],['MediaCapabilities','mediaCapabilities'],['ServiceWorkers','serviceWorker']];
  for(const [feature,key] of entries){
    const c=context();vm.runInContext('navigator.serviceWorker={register(){throw new Error("must not call")}}',c);
    const before=vm.runInContext(`navigator.${key}`,c);
    vm.runInContext(guard.replace('/*__PP_PRIVACY_EXCEPTIONS__*/[]',JSON.stringify([feature])),c);
    assert.equal(vm.runInContext(`navigator.${key}`,c),before,feature);
    for(const [other,otherKey] of entries)if(other!==feature)assert.equal(vm.runInContext(`navigator.${otherKey}`,c),undefined,other);
    const o=c.collectResidualFingerprintObservation();assert.equal(c.residualFingerprintOutcome(o,[feature]),'Verified');assert.equal(c.residualFingerprintOutcome(o),'Violation');
  }
});
test('storage, local fonts, WebCodecs and keyboard exceptions retain original APIs independently',()=>{
  for(const feature of ['StorageEstimate','LocalFonts','WebCodecs','KeyboardLayout']){
    const c=context();c.codecNames=codecNames;
    vm.runInContext(`for(const name of codecNames)globalThis[name]=class {};globalThis.Keyboard=class {getLayoutMap(){}lock(){}unlock(){}};globalThis.KeyboardLayoutMap=class {};navigator.keyboard=new Keyboard();`,c);
    vm.runInContext(guard.replace('/*__PP_PRIVACY_EXCEPTIONS__*/[]',JSON.stringify([feature])),c);
    assert.equal(vm.runInContext('typeof navigator.storage.estimate==="function"',c),feature==='StorageEstimate');
    assert.equal(vm.runInContext('typeof VideoDecoder==="function"',c),feature==='WebCodecs');
    assert.equal(vm.runInContext('typeof navigator.keyboard?.getLayoutMap==="function"',c),feature==='KeyboardLayout');
    assert.equal(vm.runInContext('(()=>{try{new FontFace("test","local(Arial)");return true}catch{return false}})()',c),feature==='LocalFonts');
    assert.equal(c.residualFingerprintOutcome(c.collectResidualFingerprintObservation(),[feature]),'Verified');
    assert.equal(vm.runInContext('navigator.deviceMemory',c),8);assert.equal(vm.runInContext('performance.memory',c),undefined);
  }
});

test('constructor alias blocking preserves shared ancestors and ordinary JavaScript libraries',()=>{
  for(const document of [true,false]) {
    const c=context(document);c.codecNames=codecNames;
    vm.runInContext(`globalThis.EventTarget=class EventTarget {};globalThis.Keyboard=class Keyboard extends EventTarget {getLayoutMap(){}lock(){}unlock(){}};globalThis.KeyboardLayoutMap=class KeyboardLayoutMap extends Map {};navigator.keyboard=new Keyboard();
      globalThis.savedKeyboard=Keyboard.prototype;globalThis.savedMap=KeyboardLayoutMap.prototype;
      for(const name of codecNames)globalThis[name]=class extends EventTarget {};
      globalThis.intrinsicsBefore=[Object,Array,Error,EventTarget,Map].map(t=>[t,t.prototype,Object.getOwnPropertyDescriptor(t.prototype,'constructor')]);`,c);
    vm.runInContext(guard,c);
    assert.equal(vm.runInContext(`intrinsicsBefore.every(([type,p,before])=>{const after=Object.getOwnPropertyDescriptor(p,'constructor');return p.constructor===type && after.value===before.value && after.configurable===before.configurable && after.writable===before.writable && after.enumerable===before.enumerable})`,c),true);
    assert.equal(vm.runInContext('Object.prototype.constructor.toString()===Object.toString() && ({}).constructor===Object && [].constructor===Array && new Error().constructor===Error',c),true);
    assert.equal(vm.runInContext('savedKeyboard.constructor===undefined && savedMap.constructor===undefined',c),true);
    assert.equal(c.residualFingerprintOutcome(c.collectResidualFingerprintObservation()),'Verified');
  }
});

test('Canvas text metrics are closed independently; text drawing and shared ancestors stay intact',()=>{
  for(const document of [true,false])for(const exceptions of [[],['CanvasTextMetrics'],['CanvasReadback'],['LocalFonts']]) {
    const c=context(document);
    vm.runInContext(`globalThis.CanvasRenderingContext2D=class {measureText(){return {width:42}}fillText(){return 'draw'}strokeText(){return 'stroke'}};
      globalThis.OffscreenCanvasRenderingContext2D=class extends CanvasRenderingContext2D {};
      globalThis.savedCanvas=new CanvasRenderingContext2D();globalThis.savedOffscreen=new OffscreenCanvasRenderingContext2D();
      globalThis.originalMeasureText=CanvasRenderingContext2D.prototype.measureText;`,c);
    const script=guard.replace('/*__PP_PRIVACY_EXCEPTIONS__*/[]',JSON.stringify(exceptions));
    vm.runInContext(script,c);vm.runInContext(script,c);
    const allowed=exceptions.includes('CanvasTextMetrics');
    for(const path of ['savedCanvas','savedOffscreen']) {
      assert.equal(vm.runInContext(`typeof ${path}.measureText==='function'`,c),allowed);
      assert.equal(vm.runInContext(`${path}.fillText()`,c),'draw');
      assert.equal(vm.runInContext(`${path}.strokeText()`,c),'stroke');
      if(allowed)assert.equal(vm.runInContext(`${path}.measureText===originalMeasureText && ${path}.measureText().width===42`,c),true);
    }
    if(!allowed)assert.throws(()=>vm.runInContext("Object.defineProperty(CanvasRenderingContext2D.prototype,'measureText',{value:originalMeasureText})",c));
    assert.equal(vm.runInContext('Object.prototype.constructor===Object',c),true);
    const o=c.collectResidualFingerprintObservation();assert.equal(c.residualFingerprintOutcome(o,exceptions),'Verified');
    if(allowed)assert.equal(c.residualFingerprintOutcome(o),'Violation');
    for(const key of ['html','offscreen'])for(const value of [undefined,null,'false']) {
      const bad={...o,canvasTextMetrics:{...o.canvasTextMetrics,[key]:value}};
      assert.equal(c.residualFingerprintOutcome(bad,exceptions),'Unavailable');
    }
  }
});

function clockContext() {
  const c=context();vm.runInContext(`
    globalThis.tick=150.375;
    globalThis.Performance=class {now(){return tick;}get timeOrigin(){return 1000.25;}mark(name,opts){return new PerformanceMark(opts.startTime);}clearMarks(){}};
    globalThis.performance=new Performance();
    globalThis.PerformanceEntry=class {constructor(start=133.375){this.start=start;}get startTime(){return this.start;}get duration(){return 25.125;}toJSON(){return {startTime:this.start,duration:25.125,name:'probe'};}};
    globalThis.PerformanceMark=class extends PerformanceEntry {toJSON(){return {startTime:this.start,duration:25.125,name:'mark',detail:'retained'};}};
    globalThis.Event=class {get timeStamp(){return 133.375;}};
    globalThis.HTMLMediaElement=class {get currentTime(){return this.time??0.155;}set currentTime(v){this.time=v;}};
    globalThis.requestAnimationFrame=fn=>{fn(167.5);return 7;};
    globalThis.Temporal={Instant:{fromEpochNanoseconds:epochNanoseconds=>({epochNanoseconds})},Now:{instant(){return {epochNanoseconds:1770123456789123456n};}}};
  `,c);return c;
}
test('coarse clocks cover prototype paths, native serialization, events, animation callbacks and Temporal',()=>{
  const c=clockContext();vm.runInContext(guard,c);vm.runInContext(guard,c);
  assert.equal(vm.runInContext('performance.now()',c),100);
  assert.equal(vm.runInContext('Performance.prototype.now.call(performance)',c),100);
  assert.equal(vm.runInContext('performance.timeOrigin',c),1000);
  assert.equal(vm.runInContext('new Event().timeStamp',c),100);
  assert.equal(vm.runInContext('new PerformanceMark().toJSON().startTime',c),100);
  assert.equal(vm.runInContext('new PerformanceMark().toJSON().detail',c),'retained');
  assert.equal(vm.runInContext('new PerformanceMark().duration',c),0);
  assert.equal(vm.runInContext('Temporal.Now.instant().epochNanoseconds%100000000n===0n',c),true);
  assert.equal(vm.runInContext('(()=>{let value;const id=requestAnimationFrame(t=>value=t);return id===7&&value===100})()',c),true);
  assert.equal(vm.runInContext('(()=>{const media=new HTMLMediaElement();media.currentTime=0.225;return media.currentTime})()',c),0.2);
  assert.equal(c.coarseClockOutcome(c.collectCoarseClockObservation()),'Verified');
  vm.runInContext('tick=250.625',c);assert.equal(vm.runInContext('performance.now()',c),200);
  assert.throws(()=>vm.runInContext("Object.defineProperty(Performance.prototype,'now',{value:()=>1.25})",c));
});
test('Date and Intl default clocks are coarse; parsing, explicit dates, subclasses and invalid dates remain native',()=>{
  const c=clockContext();vm.runInContext(guard,c);
  assert.equal(vm.runInContext('Date.now()%100===0&&new Date().getTime()%100===0',c),true);
  assert.equal(vm.runInContext('Date.prototype.constructor===Date&&new Date() instanceof Date',c),true);
  assert.equal(vm.runInContext('new Date(133).getTime()',c),133);
  assert.equal(vm.runInContext('new Date(undefined).toString()',c),'Invalid Date');
  assert.equal(vm.runInContext('Date.parse("2026-01-01T00:00:00.123Z")',c),1767225600123);
  assert.equal(vm.runInContext('(()=>{class D extends Date{};return new D(133).getTime()===133&&new D() instanceof D})()',c),true);
  assert.equal(vm.runInContext('(()=>{const f=new Intl.DateTimeFormat("en-US",{second:"numeric",fractionalSecondDigits:3,timeZone:"UTC"});return f.format===f.format&&Number(f.formatToParts().find(p=>p.type==="fractionalSecond").value)%100===0})()',c),true);
  assert.equal(vm.runInContext('new Intl.DateTimeFormat("en-US",{fractionalSecondDigits:3,timeZone:"UTC"}).formatToParts(133).find(p=>p.type==="fractionalSecond").value',c),'133');
});
test('precise timer exception retains original Date, clocks, events and serialization independently',()=>{
  const c=clockContext();const date=vm.runInContext('Date',c),now=vm.runInContext('Performance.prototype.now',c);
  vm.runInContext(guard.replace('/*__PP_PRIVACY_EXCEPTIONS__*/[]',JSON.stringify(['HighResolutionTimers'])),c);
  assert.equal(vm.runInContext('Date',c),date);assert.equal(vm.runInContext('Performance.prototype.now',c),now);
  assert.equal(vm.runInContext('performance.now()',c),150.375);
  assert.equal(vm.runInContext('new Event().timeStamp',c),133.375);
  assert.equal(vm.runInContext('new PerformanceMark().toJSON().startTime',c),133.375);
  const o=c.collectResidualFingerprintObservation();assert.equal(c.residualFingerprintOutcome(o,['HighResolutionTimers']),'Verified');assert.equal(c.residualFingerprintOutcome(o),'Violation');
});
test('work area and coordinates are normalized without replacing screen dimensions; exception retains native getters',()=>{
  for(const allowed of [false,true]) {
    const c=clockContext();vm.runInContext(`globalThis.Screen=class {get width(){return 2560;}get height(){return 1440;}get availWidth(){return 2520;}get availHeight(){return 1380;}get availLeft(){return 40;}get availTop(){return 20;}};globalThis.screen=new Screen();globalThis.screenX=700;globalThis.screenY=90;globalThis.screenLeft=700;globalThis.screenTop=90;`,c);
    const exceptions=allowed?['ScreenWorkArea']:[];vm.runInContext(guard.replace('/*__PP_PRIVACY_EXCEPTIONS__*/[]',JSON.stringify(exceptions)),c);vm.runInContext(guard.replace('/*__PP_PRIVACY_EXCEPTIONS__*/[]',JSON.stringify(exceptions)),c);
    assert.equal(vm.runInContext('screen.width',c),2560);assert.equal(vm.runInContext('screen.height',c),1440);
    assert.equal(vm.runInContext('screen.availHeight',c),allowed?1380:1440);assert.equal(vm.runInContext('screen.availLeft',c),allowed?40:0);
    assert.equal(vm.runInContext('screenX',c),allowed?700:0);
    assert.equal(c.collectWorkAreaObservation().normalized,!allowed);
  }
});
test('FontFaceSet.check is unavailable without probing fonts; loading and ready remain native',()=>{
  for(const allowed of [false,true]) {
    const c=clockContext();vm.runInContext(`globalThis.FontFaceSet=class {check(){throw Error('must not probe');}load(){return 7;}get ready(){return 9;}};document.fonts=new FontFaceSet();`,c);
    const exceptions=allowed?['LocalFonts']:[];vm.runInContext(guard.replace('/*__PP_PRIVACY_EXCEPTIONS__*/[]',JSON.stringify(exceptions)),c);
    assert.equal(vm.runInContext('typeof document.fonts.check==="function"',c),allowed);assert.equal(vm.runInContext('document.fonts.load()',c),7);assert.equal(vm.runInContext('document.fonts.ready',c),9);
  }
});

test('a document cannot verify a missing screen as worker-only NotApplicable',()=>{
  const c=context();vm.runInContext(guard,c);const o=c.collectResidualFingerprintObservation();
  assert.equal(c.residualFingerprintOutcome({...o,workArea:{status:'NotApplicable'}}),'Unavailable');
  assert.equal(c.residualFingerprintOutcome({...o,documentContext:false,workArea:{status:'NotApplicable'}}),'Verified');
});
