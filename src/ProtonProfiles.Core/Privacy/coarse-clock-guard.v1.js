// Observable 100 ms clocks, not protection against every timing side channel.
// Scheduling, native cryptography, explicit Date parsing and arithmetic stay intact.
const quantum=100;
// Idempotence uses an ordinary locked descriptor, never a public proprietary marker.
// Independent readback requires an explicit non-rounded PerformanceMark to round.
const nowDescriptor=g.performance&&Object.getOwnPropertyDescriptor(g.performance,'now');
if(!nowDescriptor || nowDescriptor.writable!==false || nowDescriptor.configurable!==false) {
  const down=value=>Number.isFinite(value)?Math.floor(value/quantum)*quantum:value;
  const lock=(target,key,value)=>Object.defineProperty(target,key,{value,writable:false,configurable:false});
  function wrap(target,key,apply,ownOnly=false) {
    const native=target?.[key];if(typeof native!=='function')return;
    const proxy=new Proxy(native,{apply:(fn,self,args)=>apply(fn,self,args)});
    restrict(target,key,proxy,ownOnly);
  }
  function wrapGetter(prototype,key,convert=down) {
    if(!prototype)return;
    const d=Object.getOwnPropertyDescriptor(prototype,key);if(!d?.get)return;
    if(!d.configurable)throw Error('Strict privacy: conflicting clock '+key);
    Object.defineProperty(prototype,key,{get:new Proxy(d.get,{apply:(fn,self,args)=>convert(Reflect.apply(fn,self,args))}),set:d.set,enumerable:d.enumerable,configurable:false});
  }
  const origin=g.performance?.timeOrigin;
  const relative=down;
  if(g.performance) {
    wrap(g.performance,'now',(fn,self,args)=>relative(Reflect.apply(fn,self,args)));
    // A getter on the native Performance prototype validates the receiver first.
    wrapGetter(g.Performance?.prototype,'timeOrigin');
    if(!g.Performance)lock(g.performance,'timeOrigin',down(origin));
  }
  const NativeDate=g.Date;
  const nativeNow=NativeDate?.now;
  if(typeof NativeDate==='function') {
    wrap(NativeDate,'now',(fn,self,args)=>down(Reflect.apply(fn,self,args)));
    const guarded=new Proxy(NativeDate,{
      construct(fn,args,newTarget){return Reflect.construct(fn,args.length?args:[down(Reflect.apply(nativeNow,fn,[]))],newTarget);},
      apply(fn,self,args){return new fn(down(Reflect.apply(nativeNow,fn,[]))).toString();}
    });
    lock(NativeDate.prototype,'constructor',guarded);lock(g,'Date',guarded);
  }
  const formatter=g.Intl?.DateTimeFormat?.prototype;
  const formatDescriptor=formatter&&Object.getOwnPropertyDescriptor(formatter,'format');
  if(formatDescriptor?.get) {
    const cached=new WeakMap();
    Object.defineProperty(formatter,'format',{get:new Proxy(formatDescriptor.get,{apply(fn,self,args){
      const bound=Reflect.apply(fn,self,args);
      if(!cached.has(bound))cached.set(bound,new Proxy(bound,{apply(method,receiver,values){
        return Reflect.apply(method,receiver,values.length&&values[0]!==undefined?values:[down(Reflect.apply(nativeNow,NativeDate,[]))]);
      }}));
      return cached.get(bound);
    }}),enumerable:formatDescriptor.enumerable,configurable:false});
  }
  wrap(formatter,'formatToParts',(fn,self,args)=>Reflect.apply(fn,self,args.length&&args[0]!==undefined?args:[down(Reflect.apply(nativeNow,NativeDate,[]))]),true);
  wrap(g.Temporal?.Now,'instant',(fn,self,args)=>{
    const value=Reflect.apply(fn,self,args),n=value.epochNanoseconds,q=100000000n;
    return g.Temporal.Instant.fromEpochNanoseconds((n/q-(n<0n&&n%q!==0n?1n:0n))*q);
  },true);
  for(const name of ['plainDateTimeISO','plainTimeISO','zonedDateTimeISO'])
    wrap(g.Temporal?.Now,name,(fn,self,args)=>{
      const value=Reflect.apply(fn,self,args);
      return value.with({millisecond:Math.floor(value.millisecond/100)*100,microsecond:0,nanosecond:0});
    },true);
  wrapGetter(g.AnimationTimeline?.prototype,'currentTime');
  wrapGetter(g.Animation?.prototype,'currentTime');wrapGetter(g.Animation?.prototype,'startTime');
  wrapGetter(g.HTMLMediaElement?.prototype,'currentTime',v=>Number.isFinite(v)?down(v*1000)/1000:v);
  wrap(g.IdleDeadline?.prototype,'timeRemaining',(fn,self,args)=>down(Reflect.apply(fn,self,args)),true);
  wrapGetter(g.Event?.prototype,'timeStamp',relative);
  wrapGetter(g.PerformanceEntry?.prototype,'startTime',relative);
  wrapGetter(g.PerformanceEntry?.prototype,'duration');
  for(const [name,keys] of [
    ['PerformanceResourceTiming',['workerStart','redirectStart','redirectEnd','fetchStart','domainLookupStart','domainLookupEnd','connectStart','connectEnd','secureConnectionStart','requestStart','responseStart','responseEnd','firstInterimResponseStart','finalResponseHeadersStart']],
    ['PerformanceNavigationTiming',['unloadEventStart','unloadEventEnd','domInteractive','domContentLoadedEventStart','domContentLoadedEventEnd','domComplete','loadEventStart','loadEventEnd','activationStart','criticalCHRestart']],
    ['PerformanceEventTiming',['processingStart','processingEnd']],
    ['PerformanceLongAnimationFrameTiming',['renderStart','styleAndLayoutStart','firstUIEventTimestamp','blockingDuration','styleDuration','layoutDuration','paintTime','presentationTime']],
    ['PerformanceScriptTiming',['executionStart','forcedStyleAndLayoutDuration','forcedStyleDuration','forcedLayoutDuration','pauseDuration']],
    ['PerformancePaintTiming',['paintTime','presentationTime']],
    ['PerformanceElementTiming',['renderTime','loadTime','paintTime','presentationTime']],
    ['LargestContentfulPaint',['renderTime','loadTime','paintTime','presentationTime']],
    ['LayoutShift',['lastInputTime']]])
    for(const key of keys)wrapGetter(g[name]?.prototype,key,relative);
  // Epoch-based legacy navigation timestamps; serialization is handled below.
  for(const key of ['navigationStart','unloadEventStart','unloadEventEnd','redirectStart','redirectEnd','fetchStart','domainLookupStart','domainLookupEnd','connectStart','connectEnd','secureConnectionStart','requestStart','responseStart','responseEnd','domLoading','domInteractive','domContentLoadedEventStart','domContentLoadedEventEnd','domComplete','loadEventStart','loadEventEnd'])
    wrapGetter(g.PerformanceTiming?.prototype,key);
  const times=new Set(['startTime','workerStart','redirectStart','redirectEnd','fetchStart','domainLookupStart','domainLookupEnd','connectStart','connectEnd','secureConnectionStart','requestStart','responseStart','responseEnd','firstInterimResponseStart','finalResponseHeadersStart','unloadEventStart','unloadEventEnd','domInteractive','domContentLoadedEventStart','domContentLoadedEventEnd','domComplete','loadEventStart','loadEventEnd','activationStart','criticalCHRestart','processingStart','processingEnd','renderTime','loadTime','lastInputTime','renderStart','styleAndLayoutStart','firstUIEventTimestamp','blockingDuration','styleDuration','layoutDuration','paintTime','presentationTime','executionStart','forcedStyleAndLayoutDuration','forcedStyleDuration','forcedLayoutDuration','pauseDuration']);
  // Native serialization bypasses overridden getters, including LoAF's nested scripts.
  // Traverse only native entry arrays; explicit mark.detail/user data stay unchanged.
  const entryCopy=result=>Object.fromEntries(Object.entries(result).map(([key,value])=>[
    key,key==='duration'||times.has(key)?down(value):
      ['scripts','userTimingEntries'].includes(key)&&Array.isArray(value)?value.map(entryCopy):value
  ]));
  for(const name of ['PerformanceEntry','PerformanceMark','PerformanceMeasure','PerformancePaintTiming','PerformanceResourceTiming','PerformanceNavigationTiming','PerformanceEventTiming','PerformanceLongTaskTiming','PerformanceLongAnimationFrameTiming','PerformanceScriptTiming','PerformanceElementTiming','TaskAttributionTiming','LargestContentfulPaint','LayoutShift']) {
    const proto=g[name]?.prototype;
    if(proto&&Object.hasOwn(proto,'toJSON'))wrap(proto,'toJSON',(fn,self,args)=>{
      const result=Reflect.apply(fn,self,args);
      return entryCopy(result);
    },true);
  }
  if(g.PerformanceTiming?.prototype&&Object.hasOwn(g.PerformanceTiming.prototype,'toJSON'))
    wrap(g.PerformanceTiming.prototype,'toJSON',(fn,self,args)=>Object.fromEntries(Object.entries(Reflect.apply(fn,self,args)).map(([key,value])=>[key,down(value)])),true);
  wrap(g,'requestAnimationFrame',(fn,self,args)=>{
    if(typeof args[0]!=='function')return Reflect.apply(fn,self,args);
    const callback=args[0];return Reflect.apply(fn,self,[timestamp=>Reflect.apply(callback,g,[relative(timestamp)]),...args.slice(1)]);
  });
}
