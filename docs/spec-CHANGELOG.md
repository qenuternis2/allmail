# Review Changes

## Version 1.1

Reviewed October 3, 2026. The English specification and Russian application UI requirement are retained. This is a document and API review; no application was built or tested on Windows during this revision.

| Finding in version 1.0 | Correction in version 1.1 | Relevant acceptance criteria |
| --- | --- | --- |
| Browser language was described without a separate JavaScript locale setting | Added the documented `ScriptLocale` controller option and its WPF initialization route; separated locale from timezone | A08, A22, A28 |
| “System” theme could be mistaken for an SDK enum value | Explicit mapping from the UI label to `CoreWebView2PreferredColorScheme.Auto` | A28 |
| Shutdown was described as waiting for a process without a complete lifecycle contract | Added `BrowserProcessExited`, generation tracking, asynchronous timeout handling, and RecoveryRequired | A11, A15, A23 |
| Fast switching could race with restart, deletion, or delayed authentication callbacks | Added a per-profile operation gate, interprocess ownership, immutable event context, and disposal of late initialization results | A03, A21, A24 |
| WPF initialization could accidentally create a default environment | Required explicit environment/controller initialization before navigation and validation of the effective UDF | A22, A32 |
| STA threading, reentrancy, and asynchronous event completion were unspecified | Added UI-thread rules and exception-safe event deferrals | A21, A22 |
| Pop-ups were only described as sharing the environment | Required the same environment and profile, an unnavigated child, and explicit handling/cancellation | A05, A22 |
| A saved browser profile might perform background activity before the visible page was opened | Required route configuration before controller creation and network observation from startup with persisted workers | A09, A10, A31 |
| “Transactional” proxy changes implied an atomicity guarantee across unrelated systems | Replaced that claim with pending/applied revisions and crash recovery; disabled silent route rollback | A11, A24 |
| Changing proxy credentials could retain cached authentication | Required full environment restart and bounded authentication retries | A12, A24 |
| Proxy credentials were not tied to an exact challenge endpoint and generation | Added endpoint validation, rejection of unrelated 401 challenges, and no global authorization-header injection | A12 |
| Prototype success and production readiness were insufficiently separated | Added explicit Core, Experimental Proxy, and Production Candidate completion rules | Network criteria and release report |
| Local reset, deletion, and service-side sign-out were conflated | Defined each action, added resumable cleanup, and prohibited claiming remote account deletion/session revocation | A04, A25 |
| Filesystem deletion and imported paths lacked precise ownership checks | Added canonical-path checks, reparse-point handling, fresh identities, and preservation of external attachments | A25, A26 |
| Permission persistence and duplicate frame events were unspecified | Added one policy store and `SavesInProfile = false`, explicit remembered choices, and deduplication | A14, A27 |
| Browser autofill/password storage was left implicit | Disabled both additional browser stores for the MVP while preserving Proton session data | A16, A27 |
| The three-profile limit could imply silent eviction of an unsaved draft | Require an explicit choice when capacity is reached; do not claim reliable draft-state detection | A18, A30 |
| Export/import omitted a concrete data contract and failure rules | Added a versioned JSON interchange format, validation limits, unresolved-network handling, and a sample | A16, A26 |
| Schema migration and interrupted operations could lose metadata | Added database recovery requirements and persistent operation state | A24, A25, A32 |
| Calendar-month reminders were ambiguous across month ends and timezone changes | Added the original local date/timezone basis, month-end clamping, and independent snoozes | A17 |
| The reminder default did not explain the current official inactivity threshold | Added Proton's annual sign-in/use guidance, the expired legacy grace period, and a clear distinction between policy and the six-month reminder default | A17 |
| Attachment and external-link handling could bypass the selected browser network | Required browser-based downloads, explicit external launch, and handling of blob attachments and cancellation | A13, A29 |
| Runtime prerequisites, performance checks, and test evidence were vague | Added .NET 10/Windows 11 x64 baseline, separate .NET/WebView2 prerequisites, bounded fixture checks, and per-test evidence statuses | A18, A19, A32 |

Version 1.1 contains 32 acceptance criteria and 24 source references. The original A01–A20 identifiers are retained; A21–A32 cover the newly specified failure modes. The review does not establish a production-safe per-profile proxy API in WebView2, nor any guarantee of unlinkable fingerprints.

## Version 1.0

Initial specification and English translation, with the WebView2 profile architecture, experimental proxy caveat, supported UA settings, and 20 acceptance criteria.
