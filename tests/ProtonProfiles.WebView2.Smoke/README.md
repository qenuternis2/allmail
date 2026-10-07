# Native WebView2 graphics regression

Run on Windows with the Evergreen WebView2 Runtime installed:

```powershell
dotnet run --project tests/ProtonProfiles.WebView2.Smoke -c Release
```

The WPF STA harness creates fresh temporary browser data folders and offscreen
windows. It first records the 0.1.5 flags, then verifies the production restricted
flags and the actual production WebGL readback script. A local HTTPS virtual host
allows WebGPU observations without Internet requests. The restricted main view,
second controller sharing its environment, and dedicated workers must expose no
WebGL contexts or WebGPU adapters; CPU Canvas 2D must still produce the expected
pixel. The production WebRTC bootstrap must also pass in the earlier modes. The Audio
mode runs with WebRTC allowed and requires the peer constructor to stay available.

The Canvas restriction mode additionally requires native SecurityError denials
from HTML canvas getImageData/toDataURL/toBlob and OffscreenCanvas
getImageData/convertToBlob in both controllers and their dedicated workers.
Drawing commands must remain accepted; readable exports are positive controls
in the other modes. Startup uses the production CDP awaited readback expression.
The actual bundled fingerprint page must finish with Canvas and graphics Pass,
a blocked Canvas hash, and retained Math observations and mode-appropriate Audio observations. External HTTP for
this report is intercepted and replaced with empty local JSON responses.

The legacy run is observational: hardware or software graphics may be unavailable
on a particular runner. Null/error/timeout results fail the restricted check.
Browser controllers are disposed and process exit is awaited before cleanup.

The graphics modes cover the CI machine's runtime and driver. Later modes below also
exercise production UI components, proxy routes, frames and worker fixtures; they do
not constitute complete manual UI acceptance or coverage of every machine and driver.
The normal Windows build and release workflows require it to pass.

The Web Audio mode additionally requires blocked standard/legacy constructors and
immutable descriptors in the document, main/second controller, and loaded same-origin,
srcdoc and cross-origin frames. Previous modes render a real offline oscillator as
positive controls. Workers report natural absence of Window APIs, not verified blocking.
An initial empty iframe must also reject immediate OfflineAudioContext construction;
baseline modes must accept it. This is checked separately from loaded frame coverage.
The bundled report must emit the current report version and the blocked Audio hash marker. HTML Audio API
availability is checked; physical playback is not. This is document script injection,
not native removal of all audio fingerprint surfaces.

The DPR mode uses the production browser-wide --force-device-scale-factor=1 flag.
A separate --force-device-scale-factor=2 control must expose DPR 2. Main and second
controllers use different browser zoom values and require matching DPR and native
CSS device/resolution media queries in the document, loaded same-origin/srcdoc/
cross-origin frames and initial empty iframe. Screen dimensions remain native.
The viewport must stay responsive to the actual control size; native Screen getters
must remain native. Workers report no Window Screen API. The real bundled report
must report DPR Pass. This does not cover all display APIs, renderer replacement,
real window.open, every OOPIF configuration or host rendering side channels.

The Speech mode adds the production native Blink flag
`--disable-blink-features=ScriptedSpeechSynthesis`. All four entry points
speechSynthesis, SpeechSynthesis, SpeechSynthesisUtterance and SpeechSynthesisVoice
must be absent in the main/second controller, loaded same-origin/srcdoc/cross-origin
frames and initial empty iframe. Baseline controls require getVoices() to return an
array and an utterance to construct successfully; no text is spoken. Workers
naturally expose none of these Window APIs. The bundled report must emit Speech
Main Pass, Worker NotApplicable and the unavailable voice enumeration marker,
while retaining previous graphics/Canvas/Audio/DPR checks and HTML Audio API.
This does not test physical playback, Speech Recognition, OS screen readers or
every renderer/context configuration.

The UA Client Hints mode reuses the actual production UserAgentHintsBootstrap
and UserAgentHintsProtocol sources. It applies Emulation.setUserAgentOverride
with the native UA and omitted userAgentMetadata in documents. Recursive
page-level Target.setAutoAttach pauses related targets until emulation is prepared,
without disabling site isolation. It then verifies the result in a host-intercepted
HTTPS document before any target URL. A secure-context check prevents natural
absence on about:blank from falsely confirming suppression. Main/second controller,
loaded frames and dedicated workers must expose no UA Client Hints identity data,
while preserving the native UA string in JavaScript and HTTP. The bundled report emits the current version with Main/Worker Pass
and HTTP echo NotPerformed because external echo is mocked with empty JSON.
Custom UA is rejected by settings/import validation and production bootstrap
before navigation or UA mutation: WebView2 preserves the native JS UA in service
workers with a custom HTTP UA. The harness verifies this rejection using an actual
controller. Other modes retain their custom-UA behavior.
SharedWorker is natively disabled in this opt-in mode, because it is not related
to page-level auto-attachment. Its constructor must be absent in the secure
bootstrap and loaded document scopes. Previous modes preserve SharedWorker.

A separate actual loopback HttpListener responds with Accept-CH and echoes received
HTTP headers. Baseline controls require full-version HTTP hints in the main document
and live JS UAData in main/dedicated/shared/service workers. Worker HTTP hints may
already be naturally absent, which is logged as NotApplicable. Native restricted controls require
consistent native HTTP/JS UA and absent Sec-CH-UA* headers in
main/dedicated/service scopes; SharedWorker absence is checked separately.
Worker UAData is captured at the beginning of its script, before activation or
message handlers, to expose startup races. Worker targets receive an explicitly
empty native metadata object with every optional field included: omission falls
back to the worker's creation metadata in Chromium. Service-worker commands are queued
before releasing the browser's main-script throttle, with responses awaited
together to avoid a deadlock before the renderer exists. This tests a
local receiver and fresh workers; existing workers, arbitrary origins, target
replacement, external proxy routes and all Runtime versions are not covered.

The v22 collector additionally requires native Web Crypto SHA-256, AES-GCM roundtrip and altered-ciphertext rejection in every document/worker/frame context. PrivacyExceptionsSmoke checks WebAudio-only and all individual exceptions in isolated environments with main/child controllers, original API availability positive controls, remaining residual restrictions, and successful SharedWorker/service worker startup. Allowed SharedWorker identity/script coverage and service-worker script coverage are explicitly unverified. Native observations proved the page CDP does not attach to the SharedWorker on this Runtime; its original UA Client Hints remain visible. SharedWorker WebCodecs absence is natural and is not treated as a failed exception. No account data or media device access is used.

CI also sets ALLMAIL_PROTON_LIVE_CHECK=1 for a bounded, read-only observation of the public Proton landing page in fresh temporary profiles (WebAudio-only / all exceptions). No account, form submission or media permission is used. External outages are explicitly NotPerformed and do not replace the mandatory controlled tests; this observation cannot prove authenticated account compatibility.

0.1.41 adds a production-startup control which resets native permissions before
each of three readbacks: persistent camera=prompt must still load the site with
the native request guard ready. Fingerprint reporting continues to expose the
nonuniform query. A separate fake-device control first proves camera/microphone
capture works with stored Allow, then uses the production request handler to erase
conflicting grants and deny real camera/microphone and geolocation requests,
including a cross-origin iframe. Fake UI is disabled; no physical device is used.
An explicit Camera exception preserves its stored grant. This fixture does not
prove authenticated Proton account compatibility or all permission types.

0.1.43 exercises the production WPF middle-button preview event on an inactive
tab and verifies it closes without changing selection. It then shuts down the
profile, waits for browser process exit, and starts the same profile in a new
generation. Three ordered tabs, including a blank and a URL fragment, and the
active selection must restore. Real page first-script guards and permission
denials are checked again. Explicitly closed tabs are excluded and closing the
last tab persists an empty session. Core storage tests cover profile isolation,
invalid/internal URL rejection, malformed files, and reset/delete behavior.

0.1.50 adds production UI reset-permission decisions without deleting browser
state/tab files; global/per-profile window placement and visible-desktop restore;
105-profile directory with 30-action search/selection p95; native IndexedDB,
CacheStorage, cookie/LocalStorage isolation and restart of both profiles;
BroadcastChannel same-profile positive and cross-profile negative controls;
Service Worker cache isolation/persistence with RuntimeDefault; real browser PID
crash, controller disposal and recovery while B remains open; 20 production
lifecycle cycles with exit/PID checks and `lifecycle-memory.csv` (aggregate host/owned Runtime working set while open and after close, Runtime
process family count, managed bytes, live environments and weakly observed closed sessions; all captured child PIDs must exit and closed sessions must not accumulate).
Production blob attachments are verified byte-for-byte. Download identity/save-choice
bookkeeping must be released after terminal operations and save-dialog cancellation
while the originating tab is still open; paused/interrupted Range retries and parallel
downloads of the same URL retain their operation identity. ProxyRouting uses a
short-lived certificate explicitly trusted in the isolated runner’s LocalMachine Root store
and removed on dispose, with no certificate-error bypass; HTTPS/WSS CONNECT,
stopped-proxy and startup-check HTTP403 acceptance are required. Other older
GeoIP fixtures still have narrowly scoped fixture-only TLS exception handlers;
those tests do not certify production certificate validation.

The native suite step budget is eight minutes because these additional real
browser/process tests extend coverage. Existing individual assertions remain;
the production tab/lifecycle fixture allows 150 seconds for its additional
storage restarts and 20 lifecycle cycles.

The full TLS fixture requires an elevated, isolated Windows test session. It imports
only its newly generated public certificate with `certutil` and removes that exact
certificate in `finally`; CurrentUser Root otherwise displays a blocking Windows
trust-consent dialog. The application and per-user installer do not require this
test-only elevation. Native memory sampling drains pending WPF callbacks and runs
GC finalizers off the STA, while exact process handles use only query/synchronize
rights suitable for sandboxed children. BootstrapNavigationSmoke holds image loading in three committed prior documents
and requires an overlapping previous NavigationCompleted while verifying the owned
secure bootstrap document. All fixture resources are local and uncached.

The stopped-proxy boundary drains both directions of existing CONNECT tunnels and
accepted TLS receiver handlers before taking its request-count baseline. Late
completion of an earlier request is not counted as a new direct connection.

0.1.51 runs the 105-record directory benchmark while three real production
profile environments are open. Every measured selection switches among them,
a fourth is rejected, and all three are closed with authoritative exit signals
before the window is disposed. The complete UI fixture budget is 60 seconds for
this additional browser setup; the 200 ms p95 assertion is unchanged.

The 0.1.51 benchmark waits for the local document to finish and reports each
first native-frame switch separately. Its 30 measured loaded-page operations
end at WPF Render priority; directory-performance.csv records synchronous
search, synchronous selection and rendering delay. A surviving selected row
must retain identity across filtering; hidden/excluded rows may lose selection.
