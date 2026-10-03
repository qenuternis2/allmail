// Controlled fixture only. No mail data, external servers or host message bridge.
function inspectWebRtcRealm(realm, label, expected) {
  const names = ["RTCPeerConnection", "webkitRTCPeerConnection", "RTCIceTransport", "RTCDtlsTransport", "RTCSctpTransport"];
  const entryPoints = {};
  for (const name of names) {
    const d = realm.Object.getOwnPropertyDescriptor(realm, name);
    let usable = false;
    let error = null;
    if (typeof realm[name] === "function") {
      try {
        const instance = new realm[name]();
        usable = true;
        if (typeof instance.createDataChannel === "function") instance.createDataChannel("synthetic");
        if (typeof instance.close === "function") instance.close();
        if (typeof instance.stop === "function") instance.stop();
      } catch (e) { error = String(e); }
    }
    entryPoints[name] = { type: typeof realm[name], usable, error, sealedUndefined: !!d && "value" in d && d.value === undefined && !d.writable && !d.configurable };
  }
  return {
    label, expected, entryPoints,
    rtcInventory: realm.Object.getOwnPropertyNames(realm).filter(n => /^(RTC|webkitRTC)/.test(n)),
    status: expected === "Block"
      ? (names.every(n => entryPoints[n].sealedUndefined && !entryPoints[n].usable) ? "Pass" : "Fail")
      : (entryPoints.RTCPeerConnection.usable ? "Pass" : "Blocked"),
    scope: "document API only; not network verification"
  };
}
const webrtcQuery = new URLSearchParams(location.search);
const webrtcExpected = webrtcQuery.get("expected") === "Allow" ? "Allow" : "Block";
const webrtcFirstResult = inspectWebRtcRealm(window, webrtcQuery.get("label") || "first page script", webrtcExpected);
document.addEventListener("DOMContentLoaded", () => {
  const sink = document.getElementById("first");
  if (sink) sink.textContent = JSON.stringify(webrtcFirstResult, null, 2);
  const owner = opener || (parent !== window ? parent : null);
  if (owner) {
    const origin = webrtcQuery.get("parentOrigin");
    // The fixture runner only allows its two controlled HTTPS origins.
    if (origin === "https://localhost:8443" || origin === "https://127.0.0.1:8444")
      owner.postMessage({ fixture: "pp-webrtc-v1", result: webrtcFirstResult }, origin);
  }
});
