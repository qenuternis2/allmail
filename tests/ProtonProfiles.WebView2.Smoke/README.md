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
pixel. The production WebRTC bootstrap must also pass.

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
An initial empty iframe is observed separately; no coverage is inferred for it.
The bundled report must emit v5 and the blocked Audio hash marker. HTML Audio API
availability is checked; physical playback is not. This is document script injection,
not native removal of all audio fingerprint surfaces.
