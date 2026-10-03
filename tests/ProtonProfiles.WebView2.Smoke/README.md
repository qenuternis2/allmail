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

This checks native runtime graphics on the CI machine, not all machines or the
complete app UI, network routes, OOPIFs, shared/service workers or driver changes.
The normal Windows build and release workflows require it to pass.

The Web Audio mode additionally requires blocked standard/legacy constructors and
immutable descriptors in the document, main/second controller, and loaded same-origin,
srcdoc and cross-origin frames. Previous modes render a real offline oscillator as
positive controls. Workers report natural absence of Window APIs, not verified blocking.
An initial empty iframe must also reject immediate OfflineAudioContext construction;
baseline modes must accept it. This is checked separately from loaded frame coverage.
The bundled report must emit v7 and the blocked Audio hash marker. HTML Audio API
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
