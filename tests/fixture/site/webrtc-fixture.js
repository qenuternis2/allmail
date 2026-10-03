"use strict";
const results = [webrtcFirstResult];
const frames = new Set();
const popups = new Set();
const allowedOrigins = new Set(["https://localhost:8443", "https://127.0.0.1:8444"]);
function record(result) {
  results.push(result);
  document.getElementById("results").textContent = JSON.stringify({ fixtureVersion: 1, userAgent: navigator.userAgent, results }, null, 2);
}
function contextUrl(origin, label) {
  const u = new URL("/webrtc-context.html", origin);
  u.searchParams.set("expected", webrtcExpected);
  u.searchParams.set("label", label);
  u.searchParams.set("parentOrigin", location.origin);
  return u.href;
}
addEventListener("message", e => {
  if ((!allowedOrigins.has(e.origin) && e.origin !== "null") || (!frames.has(e.source) && !popups.has(e.source))) return;
  if (e.data?.fixture === "pp-webrtc-v1") record(e.data.result);
});
function checkImmediate(frame, label) {
  try { record(inspectWebRtcRealm(frame.contentWindow, label, webrtcExpected)); }
  catch (e) { record({ label, status: "Blocked", reason: String(e) }); }
}
function addFrame(label, configure, immediate = false) {
  const f = document.createElement("iframe");
  configure(f);
  document.getElementById("frames").append(f);
  frames.add(f.contentWindow);
  if (immediate) checkImmediate(f, label + " synchronous fresh realm");
  f.addEventListener("load", () => {
    if (!f.src || f.src === "about:blank") checkImmediate(f, label + " loaded realm");
  });
}
function inlineContext(label) {
  // Runs before any other script in srcdoc/blob documents; reports via the fixture's page-to-page channel only.
  return `<script>const result = (${inspectWebRtcRealm.toString()})(window, ${JSON.stringify(label)}, ${JSON.stringify(webrtcExpected)}); parent.postMessage({fixture:"pp-webrtc-v1",result}, ${JSON.stringify(location.origin)});<\/script>`;
}
document.getElementById("contexts").onclick = async () => {
  addFrame("about:blank", f => { f.src = "about:blank"; }, true);
  addFrame("srcdoc", f => { f.srcdoc = inlineContext("srcdoc first script"); }, true);
  const blob = URL.createObjectURL(new Blob([inlineContext("blob first script")], { type: "text/html" }));
  addFrame("blob", f => { f.src = blob; });
  // Keep the blob URL until windows/frames stop using it; it is fixture-only data.
  addFrame("same origin", f => { f.src = contextUrl(location.origin, "same origin first script"); });
  const other = location.origin === "https://localhost:8443" ? "https://127.0.0.1:8444" : "https://localhost:8443";
  addFrame("cross origin", f => { f.src = contextUrl(other, "cross origin first script"); });
  for (const kind of ["dedicated", "shared", "service"]) await inventoryWorker(kind);
};
document.getElementById("child").onclick = () => {
  const child = window.open(contextUrl(location.origin, "child first script"), "_blank");
  if (child) popups.add(child);
  else record({ label: "child", status: "Blocked", reason: "popup was not created" });
};
document.getElementById("blankChild").onclick = () => {
  const child = window.open("about:blank", "_blank");
  if (!child) { record({ label: "blank child", status: "Blocked" }); return; }
  popups.add(child);
  try { record(inspectWebRtcRealm(child, "blank child synchronous access", webrtcExpected)); }
  catch (e) { record({ label: "blank child synchronous access", status: "Blocked", reason: String(e) }); }
};
document.getElementById("permissions").onclick = async () => {
  for (const [name, constraints] of [["camera", {video:true}], ["microphone", {audio:true}]]) {
    try {
      const stream = await navigator.mediaDevices.getUserMedia(constraints);
      stream.getTracks().forEach(t => t.stop());
      record({ label: name, status: webrtcExpected === "Block" ? "Fail" : "Pass", outcome: "granted" });
    } catch (e) {
      record({ label: name, status: webrtcExpected === "Block" && e.name === "NotAllowedError" ? "Pass" : "Blocked", outcome: e.name,
        scope: "missing hardware or OS denial must be distinguished from application permission denial" });
    }
  }
};
async function inventoryWorker(kind) {
  let worker, port, registration;
  try {
    const ready = new Promise((resolve, reject) => {
      if (kind === "dedicated") { worker = new Worker("webrtc-worker.js"); port = worker; worker.onerror = reject; }
      if (kind === "shared") { worker = new SharedWorker("webrtc-worker.js"); port = worker.port; worker.onerror = reject; port.start(); }
      if (port) port.onmessage = e => resolve(e.data);
      if (kind === "service") {
        navigator.serviceWorker.register("webrtc-worker.js", {scope:"/webrtc-worker-scope/"}).then(async reg => {
          registration = reg;
          const w = reg.active || reg.installing || reg.waiting;
          if (w.state !== "activated") await new Promise(r => w.addEventListener("statechange", () => {if(w.state === "activated") r();}));
          const channel = new MessageChannel(); channel.port1.onmessage = e => resolve(e.data);
          w.postMessage("inventory", [channel.port2]);
        }).catch(reject);
      }
    });
    const data = await timeout(ready, 7000);
    // If a standalone transport/PC exists in a worker it is outside document injection coverage.
    record({ label: kind + " worker", ...data, status: data.constructible.length ? (webrtcExpected === "Block" ? "Fail" : "Pass") : "NotApplicable" });
  } catch (e) { record({ label: kind + " worker", status: "Blocked", reason: String(e) }); }
  finally { worker?.terminate?.(); port?.close?.(); await registration?.unregister(); }
}
function timeout(promise, ms) {
  let timer;
  return Promise.race([promise, new Promise((_, reject) => {timer = setTimeout(() => reject(new Error("timeout")), ms);})]).finally(() => clearTimeout(timer));
}
document.getElementById("pair").onclick = async () => {
  let a, b;
  try {
    const iceServers = JSON.parse(document.getElementById("ice").value);
    if (!Array.isArray(iceServers)) throw new Error("ICE servers must be an array");
    a = new RTCPeerConnection({iceServers}); b = new RTCPeerConnection({iceServers});
    const candidates = [];
    const tasks = [];
    for (const [peer, label] of [[a,"A"], [b,"B"]]) {
      tasks.push(new Promise(resolve => {
        peer.onicecandidate = e => {if(e.candidate) candidates.push({peer:label, candidate:e.candidate.candidate}); else resolve();};
      }));
    }
    const received = new Promise(resolve => {b.ondatachannel = e => { e.channel.onmessage = m => resolve(m.data); };});
    const sender = a.createDataChannel("synthetic");
    const opened = new Promise(resolve => {sender.onopen = resolve;});
    await a.setLocalDescription(await a.createOffer());
    await timeout(tasks[0], 10000);
    await b.setRemoteDescription(a.localDescription);
    await b.setLocalDescription(await b.createAnswer());
    await timeout(tasks[1], 10000);
    await a.setRemoteDescription(b.localDescription);
    await timeout(opened, 10000);
    sender.send("pp-synthetic-positive-control-v1");
    const payload = await timeout(received, 5000);
    record({ label: "DataChannel positive control", status: payload === "pp-synthetic-positive-control-v1" ? "Pass" : "Fail", candidates,
      scope: iceServers.length ? "controlled ICE; external traffic observation still required" : "two local peers, no external ICE servers" });
  } catch (e) {
    // A failed positive control NEVER proves protection. API blocking has its own independent tests above.
    record({ label: "DataChannel positive control", status: "Blocked", reason: e.name || "fixture setup error" });
  } finally { a?.close(); b?.close(); }
};
record({ label:"route enforcement", status:"Blocked", reason:"requires Windows Runtime and external network observation; no result inferred from API checks" });
