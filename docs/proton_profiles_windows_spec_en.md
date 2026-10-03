# Technical Specification for a Proton Mail Profile Manager for Windows

Version 1.1 · October 3, 2026 · Specification language: English · Application UI language: Russian

This revision supersedes version 1.0. `CHANGELOG.md` records the review findings. MUST denotes an acceptance requirement; SHOULD denotes a recommended implementation choice that may be changed with a documented reason.

This document is intended to be handed to a development bot in full. Build a Windows application for manually working with existing Proton Mail accounts through the official web interface, including accounts on the free plan. The main benefits are quick switching, separate sessions, individual profile settings, and reminders to visit each account.

This document is a specification informed by a documentation review. No Windows prototype was run during its preparation. The developer must verify Proton compatibility, proxy operation, and session persistence against an actual WebView2 Runtime.

## 1 Objectives and Scope

Implement an application with a profile directory and an embedded browser. Each application profile represents one logical email account. Users complete sign-in, two-factor authentication, and any Proton verification challenges themselves on the service's actual pages.

Verifiable objectives: site data must not mix between profiles; settings must affect only the selected profile; the network mode must have a clearly defined scope; failures in one profile must not clear another profile's data.

“100 percent different and unlinkable fingerprints” is not an acceptance criterion. Different browser settings do not prove that accounts cannot be linked. The operating system, graphics stack, fonts, and other attributes may be shared [S12]. Separate processes and directories are not a security boundary against software running with the same Windows user's privileges.

Proton supports using multiple accounts through its web interface [S1]. Its terms restrict creating or operating large numbers of free accounts for one person or organization; the relevant clause does not specify a numeric threshold [S2]. Account registration, bypassing verification, automated bulk messaging, and simulated activity are outside the scope of this specification.

The first version also excludes a unified inbox, cross-account message search, extraction of Proton keys, a custom encryption implementation, and connecting free accounts through Bridge. The official Bridge requires a paid plan [S3].

## 2 Technology Selection and Proxy Clarification

Primary stack: C#, WPF, .NET 10 LTS, the stable Microsoft.Web.WebView2 package, and SQLite [S22]. Baseline platform: Windows 11 x64 on a build supported at the time of testing. Windows 10 and ARM64 require separate qualification and are not implicit targets. Pin SDK and NuGet versions after compatibility checks; record the tested Runtime separately. Distribute with WebView2 Runtime detection and an installation path [S11]. Do not freeze an old Runtime indefinitely to preserve a flag. An Evergreen Runtime is the default deployment choice.

WebView2 architecture: one `CoreWebView2Environment` and a separate `UserDataFolder` for each profile. Use one persistent browser profile within each environment. Do not substitute multiple `ProfileName` values within a shared folder for this architecture. Separate folders create separate WebView2 process groups, at the cost of additional memory and processes [S4, S5].

Clarification of the preliminary approach: `--proxy-server` can be passed as a browser flag through `AdditionalBrowserArguments`, but Microsoft explicitly advises against using these flags in production because they may change or disappear [S6]. The existence of the `AdditionalBrowserArguments` property does not make every supplied flag a stable API contract.

The developer must follow this sequence:

1. Build a minimal WebView2 prototype with two isolated profiles and verify sign-in, restart behavior, and settings.
2. Check the current stable SDK for proxy configuration. If a documented API at the required scope is available, cite it and use it.
3. If no such API is available, implement `--proxy-server` only in an experimental build. Label the network feature as experimental and record the tested versions. Do not present this build as a production solution with guaranteed network isolation.
4. For a release requiring individual proxies, propose an Electron alternative: persistent sessions and `session.setProxy()` are documented there. This changes the network API; it does not guarantee independent fingerprints [S13].

The default implementation scope under this specification is a WebView2 prototype and a reviewable application that clearly states the proxy feature's status. Do not silently rewrite the entire product in Electron or omit the proxy requirement from the report. Prepare a separate architecture decision with verification results and migration scope. Complete the remaining functionality independently of that decision.

Use the following delivery labels consistently:

| Delivery | Required scope | Completion rule |
| --- | --- | --- |
| Core build | Persistent profiles, supported settings, lifecycle, reminders, and System networking | May be reviewed independently; it does not satisfy the full per-profile proxy objective |
| Experimental proxy build | Core plus the investigated WebView2 proxy adapter | Network tests must pass before using real accounts with this mode; passing tests does not remove the documented browser-flag limitation |
| Production candidate with proxies | All functional requirements and a supportable network implementation | Requires a documented API decision and passing proxy acceptance tests; merely hiding the experimental label is not sufficient |

An unsupported Proxy configuration MUST stay blocked. It must never open as System. The development bot should produce the first two deliverables where feasible, and clearly report any unresolved production requirement rather than stopping all work or declaring the whole product complete.

## 3 User Scenarios

| ID | Requirement |
| --- | --- |
| F01 | Create an empty local profile with a name, color, and optional email address used as a label |
| F02 | Open the official Proton Mail page for manual sign-in; do not build a custom Proton password form |
| F03 | Provide a profile list searchable by name and label; persist order and favorites |
| F04 | Switch profiles without mixing tabs, downloads, or settings |
| F05 | Preserve browser state after a normal shutdown; allow sign-in again if the server revokes the session |
| F06 | Provide separate network, UA, browser language, script locale, theme, zoom, and permission settings for each profile |
| F07 | Provide commands to restart a profile, reset its local session, and delete it; resetting and deletion require a clear confirmation naming the profile |
| F08 | Remind users to visit accounts manually and retain local history of user confirmations and snoozes |
| F09 | Export settings without passwords, cookies, tokens, or browser folders; generate new profile identifiers on import |
| F10 | Provide diagnostics containing application and Runtime versions, effective settings, and test results, without email content |

UI states, expressed here in English and to be localized into Russian: “Closed,” “Opening,” “Open,” “Closing,” and “Recovery required.” Network errors, missing credentials, and pending settings are separate status details, not proof that the browser process has closed. Do not show “Account active in Proton” merely because a page loaded. Show an expired-authentication status only when the scenario can be recognized reliably; otherwise let the user interact with the actual sign-in page.

The top bar must always show the selected profile's name. This is a local label, not proof of the account currently signed in: users can switch accounts within the website. Do not implement automatic address verification using brittle DOM parsing in the MVP.

## 4 Profile Isolation

### 4.1 Identifiers and Storage

- Generate an immutable UUID `profileId`; do not use the name or email address as a filesystem path.
- Derive a path such as `%LOCALAPPDATA%\ProtonProfiles\Profiles\<UUID>\WebViewData` exclusively from the internal UUID.
- Create a new empty folder for each new profile. Copying settings must not copy browser data.
- The UUID-to-directory mapping must be unique. A collision or attempted reassignment is an error, not a reason to open a shared directory.
- Keep cookies, DOM storage, IndexedDB, CacheStorage, and service worker data within the corresponding profile's storage. Do not create a shared application cache of decrypted email [S5].
- Track the download directory separately. Do not delete attachments saved outside the application when deleting a profile unless the user separately chooses to do so.

### 4.2 Profile Ownership

Add an interprocess lock keyed by Windows user and UUID so two application instances cannot open the same profile simultaneously. Keep the lock outside the deletable UDF, under the application's Locks directory. An exclusively opened file handle is one suitable implementation; avoid holding a thread-affine mutex across asynchronous continuations on different threads. Acquire the lock before creating the environment and release it only after the shutdown checks in Section 4.4. A stale lock file must not itself count as a live lock. After a host crash, surviving Runtime processes may still hold the UDF; do not bypass that conflict by silently creating another folder.

Set `ExclusiveUserDataFolderAccess = true` and select a stable SDK/Runtime combination that supports it [S7]. It limits other processes joining the browser process associated with the same UDF; it does not encrypt the folder or replace Windows permissions or the application's lock. Treat unavailable required capabilities as an explicit startup error, not a reason to ignore the setting.

Do not open the system Edge profile directory or import its cookies. Explicitly keep `AllowSingleSignOnUsingOSPrimaryAccount = false`, and disable browser extensions in the MVP [S7].

### 4.3 Windows and Event Handlers

Create all child WebViews needed for allowed Proton workflows using the initiating profile's environment and browser profile. For `NewWindowRequested`, obtain a deferral for asynchronous creation, initialize an unnavigated child, apply settings and handlers, assign `NewWindow`, and complete the deferral even on failure. Microsoft requires the same environment/profile and a child that has not already navigated [S18]. Explicitly handle or cancel every request; do not let a default window bypass the application's policy.

Every download, permission, proxy-authentication, and error handler must carry an immutable `(profileId, generationId)` context. Ignore or cancel stale callbacks after a restart. Do not use a mutable global `CurrentProfile` as the source of credentials for asynchronous events. Child windows count toward the owning profile's resource use and must close with it.

### 4.4 Memory and Shutdown

The directory must support at least 100 local records without launching 100 browsers at once. This is a load test using synthetic profiles, not a requirement to create 100 Proton accounts.

Keep no more than three live profile environments by default, including those starting or closing. Favorites and pinning do not bypass this limit. When opening a fourth profile, ask the user which existing profile to close or allow them to cancel the new opening. Do not silently evict a profile: the host cannot reliably know whether a Proton draft is saved without inspecting application internals. Pinning prevents suggested eviction, not explicit user closure. Do not extract draft text into the local database.

“Hidden,” “Suspended,” and “Closed” are different states. A hidden WebView may continue making network requests. Suspension is optional and is not a network-disconnection guarantee. Before closing, warn about possible unsaved edits and active downloads, regardless of whether a DOM-based draft detector exists.

Subscribe to `BrowserProcessExited` while creating the environment, before shutdown can race with the subscription. To restart, reset, or delete a profile, close all its children and controllers, dispose the WPF controls, and await the event for that environment/generation. It signals release of the Runtime's resources, including the UDF [S16]. A navigation completion, `Stop()`, `ProcessFailed`, or disposing one control is not a substitute. If completion is not observed within 15 seconds, enter RecoveryRequired and offer retry; do not reopen or delete the UDF, or kill all processes named `msedgewebview2.exe`. The wait must not block the UI thread.

Retain a completion signal for each generation so an exit already observed is not awaited a second time. Initialization that failed before any environment/process was created has no exit event to await; release only resources actually acquired. If process ownership after partial initialization is uncertain, use RecoveryRequired rather than assuming that no process exists.

### 4.5 Deterministic Initialization

Do not set `Source` in XAML or navigate before initialization; this can initialize an unintended default environment. Use this sequence for a normal profile window:

1. Acquire the profile operation gate and interprocess lock. Validate configuration, path ownership, required capabilities, and credential availability.
2. Create the specified environment with its UDF, language, SSO, extension, and network settings. Network options must exist before creating any controller.
3. Check the effective `environment.UserDataFolder` against the intended canonical path and active profile mappings. Environment variables or deployment configuration may override supplied values; an unexpected path or engine channel blocks opening [S23]. Do not rewrite machine policies to make the check pass.
4. Create controller options, explicitly use a persistent profile, and apply `ScriptLocale` if configured. On WPF, use the documented `EnsureCoreWebView2Async(environment, controllerOptions)` path [S21].
5. Attach origin, permission, authentication, child-window, download, and failure handlers; apply UA, color scheme, zoom, and autofill policy before the first explicit navigation.
6. Record the effective revision and generation, then navigate. A startup check is diagnostic, not a network security boundary: persisted background workers may have independent activity. Test restored profiles from process creation, not just their first visible navigation.

### 4.6 Threading and Concurrent Operations

Create and access WebView2 objects on the WPF STA UI thread with a running message pump. Use asynchronous waits; never block it using `.Result`, `.Wait()`, or synchronous process waits. Use event deferrals when asynchronous work must finish before the event returns, and complete them on success, cancellation, and exceptions [S17].

Serialize open, close, restart, reset, and delete operations per profile through an asynchronous operation gate. Coalesce duplicate opens. A cancelled initialization that later completes must dispose its result, not resurrect a closed profile. Marshal UI updates through the dispatcher and reject callbacks from older generations. File/database work may run elsewhere; WebView2 objects must not be accessed from `Task.Run`.

### 4.7 Reset and Deletion Semantics

Separate these actions clearly: signing out on Proton is a website action; resetting local session data clears this application's browser state; deleting a local profile removes its metadata and managed data. Neither local operation deletes the Proton account or guarantees server-side revocation of its sessions.

For reset, hold the profile gate and lock, close the environment, wait for resource release, then remove only its UDF and application-owned permission decisions. Preserve its UUID, display/network preferences, and separately stored attachments. Create a fresh UDF only when the profile is next opened. Explain that local website data and unsynced work may be lost.

For deletion, first persist a deletion intent, close and release the environment, then remove the owned UDF and profile-specific secrets before finalizing the metadata deletion. Interrupted cleanup must be resumable without opening the profile. Validate the canonical managed path and UUID ownership; reject traversal and reparse points/junctions escaping the managed tree. Never recursively delete a user-selected downloads directory. If files are locked or deletion is denied, report pending cleanup instead of claiming success. Filesystem and SQLite changes are coordinated operations, not one atomic transaction.

## 5 Profile Settings

Settings are explicitly changed by the user and persisted. Random configuration generation and automatic rotation are not included. The UI must indicate which changes require a restart.

| Setting | Implementation | Application and Limitations |
| --- | --- | --- |
| User-Agent | `CoreWebView2.Settings.UserAgent` | `Default` and `Custom` modes; apply after initialization and before the first navigation; restart an already open profile |
| Browser language | `CoreWebView2EnvironmentOptions.Language` | System default or a BCP 47 tag such as `ru-RU`; set before creating the environment; verify `Accept-Language` and observed navigator language values |
| Script locale | `CoreWebView2ControllerOptions.ScriptLocale` | Default, MatchBrowserLanguage, or an explicit BCP 47 tag; set at controller creation; controls default locale for locale-sensitive JavaScript APIs such as `Intl`; does not change timezone [S15] |
| Color scheme | `CoreWebView2Profile.PreferredColorScheme` | UI “System” maps to API enum `Auto`; other enum values are `Light` and `Dark` [S19]; the website may have its own theme setting |
| Zoom | The controller's standard `ZoomFactor` | A user display preference; do not treat it as a screen-resolution override |
| Window size | Normal application window geometry | Save position and size; do not represent this as hardware-screen emulation |
| Tracking prevention | `EnableTrackingPrevention` and `PreferredTrackingPreventionLevel` | Enabled by default at Balanced; Strict is available subject to compatibility testing |
| Permissions | Requests handled per profile and origin | Notifications are user-controlled; do not automatically grant camera, microphone, or geolocation access |
| Proxy | Network adapter described in Section 6 | Support status depends on the selected engine and verified API |

Settings references: [S7–S10]. Display and language settings are useful for personalization and compatibility testing; they do not themselves strengthen storage separation.

Browser language, JavaScript locale, and timezone are different settings. The default script locale can depend on both browser language and the OS region. This revision adds the supported `ScriptLocale` setting rather than assuming that `Language` alone controls every locale-sensitive API [S15]. Use `Default` unless explicitly configured; in MatchBrowserLanguage mode derive a resolved tag and apply it to every controller in that profile. Do not change the machine's timezone or global .NET culture for one profile. Restart the profile when language, script locale, or UA changes.

Validate custom UA as a single-line string of 1–512 characters without CR/LF or control characters. Validate locale tags and reject unsupported/invalid values before replacing an active revision. Store color scheme as Auto/Light/Dark and zoom as a finite number between 0.5 and 2.0. Theme and zoom may apply live. Defaults must not silently overwrite a saved explicit setting after a restart.

Required UA behavior:

- In `Default` mode, use the current Runtime's native value. Do not retain a hard-coded old UA after an engine update.
- To return from `Custom` to `Default`, restart the full profile environment and recreate all its WebViews without an override: assigning an empty string to the setter does not reset it [S8]. Retain the UDF; do not clear the user's session to reset a UA preference.
- Apply the same setting to every WebView within one profile. Test workers and child windows: the documentation describes special behavior when workers share UA settings [S8].
- Check the actual HTTP UA, `navigator.userAgent`, and UA Client Hints. An override may clear Client Hints; record missing values in diagnostics rather than reporting an unqualified success [S8].

Do not include timezone, Canvas, WebGL, AudioContext, installed fonts, core count, memory, TLS/HTTP2 behavior, or WebRTC in the list of guaranteed independently overridable settings. The reviewed WebView2 documentation did not establish a stable, comprehensive API for independently replacing all these attributes. This states the limits of the review, not the absence of every possible experimental mechanism.

Do not inject general-purpose JavaScript overrides of `navigator`, Canvas, or WebGL into Proton pages. Do not use CDP emulation as evidence of a new hardware identity. If individual web features need testing, run such experiments in a separate laboratory build against an owned test origin; they are not part of the application's production contract.

## 6 Network Modes and Proxies

### 6.1 Settings Model

Modes: `System` uses the normal Windows/Runtime network route; `Proxy` uses a specific configured proxy. Do not call `System` a direct connection: the system configuration may already contain a proxy or VPN.

Store the host, port, proxy type, and secret reference separately. An HTTP proxy with CONNECT for HTTPS and an explicitly supported authentication scheme is sufficient for the first verifiable implementation. Add SOCKS, PAC, and other options only after separate tests; do not claim they are supported automatically.

Store proxy usernames and passwords using Windows Credential Manager or DPAPI CurrentUser, not in SQLite, URIs, process arguments, or logs. OS protection of a secret does not imply encryption of the entire browser profile.

Define a proxy endpoint by scheme, normalized host, and port; reject embedded credentials, query strings, fragments, and arbitrary arguments. Distinguish the proxy transport from the destination's HTTPS: CONNECT through a plaintext HTTP proxy does not encrypt the connection to the proxy itself. Use that transport only on a trusted path; test HTTPS-to-proxy separately before listing it as supported. Authentication schemes beyond the explicitly tested set are unsupported, not an invitation to retry with default Windows credentials.

Do not create a Proxy environment while its required secret is unavailable. Mark the profile “Credentials required” and allow the user to supply the secret. A username/password change requires a full profile restart because an existing process may retain authentication state [S16]. Credential cancellation and authentication errors must not trigger automatic retry storms; permit one bounded retry, then require user action.

### 6.2 WebView2 Configuration

In the experimental adapter, pass the verified `--proxy-server` flag when creating the environment. Construct the argument from parsed host and port fields. Do not allow user input to append arbitrary browser flags. Keep a separate UDF for each profile with its own network configuration.

Handle proxy authentication through the verified API of the chosen SDK version. `BasicAuthenticationRequested` covers more than proxy authentication. Its `Uri` identifies the proxy for proxy challenges [S14]. Bind the event to the immutable profile generation; compare the parsed challenge URI to that profile's exact proxy endpoint and verify the supported challenge context before releasing credentials. Cancel ambiguous or mismatched challenges. Never synthesize a global `Proxy-Authorization` header or supply proxy credentials to a website's 401 response. Verify HTTP 407 handling, redirects, wrong credentials, and an unrelated site's 401 separately.

### 6.3 Route Changes

Use a recoverable configuration workflow; network state cannot participate in a SQLite transaction:

1. Validate the new settings and secret without changing the active profile. Persist a pending revision while retaining the last successfully applied revision.
2. Explain that the current page and unfinished operations must be closed.
3. Stop new navigations, close all WebViews and downloads belonging to the environment, and wait for its `BrowserProcessExited` event. A timeout blocks the change.
4. Create the candidate environment using the same UDF and the pending revision. Apply the route before any controller is created, including controllers that may restore persisted background activity.
5. Run the agreed diagnostic check through this profile's browser network stack, never a generic host `HttpClient`. Commit the candidate as the last successfully applied revision after the required checks succeed, then perform the normal explicit Proton navigation. A successful check does not prove continuing proxy availability or suppress autonomous worker activity.
6. On failure, close the candidate environment where possible; retain the old configuration, the pending revision, and a sanitized error. Remain closed or in RecoveryRequired. Offer Retry or an explicit Revert and restart. Do not silently reconnect using the old route, switch to `System`, delete cookies, or claim that the browser has closed while processes remain alive.

After a host crash during reconfiguration, inspect the persisted operation state and recover without automatically launching accounts. Keep a single winning revision and discard stale async completions. Profile configuration commits, credential writes, and filesystem operations require compensating recovery; do not promise all-or-nothing behavior across those stores.

If the configured proxy is unavailable, email-related HTTP/HTTPS requests must fail rather than silently falling back to the normal route. Verify this through external observation on the test setup; checking the displayed IP address alone is insufficient.

### 6.4 Scope of the Guarantee

The documentation describes `--proxy-server` as an HTTP/HTTPS setting [S6]. Do not promise that it routes every application network operation, DNS, WebRTC, Runtime updates, external-browser navigation, or other protocols.

Test HTML navigations, fetch/XHR, iframes, worker requests, attachment downloads, HTTPS CONNECT, and WSS. Investigate DNS and attempted direct UDP connections separately, and document the observed results. Denying camera/microphone access does not prove that WebRTC data channels are unavailable.

Test initial startup with a stopped proxy, startup with an existing service worker, authentication failure, proxy loss mid-session, and runtime restart. Observe the route from process startup through shutdown. Define expected loopback/test-fixture exceptions in the report. Do not treat request interception or a successful IP-check page as a firewall. Use the application's owned test endpoint for automated diagnostics; a production diagnostic endpoint must be explicitly configured and must receive no account credentials or exported identifiers.

If all connections outside the selected route must be prohibited, treat that as a separate requirement for network isolation at the OS or virtual-environment level. Do not replace it with an assurance based solely on WebView2 settings. Do not use UI wording such as “all traffic is protected” before verifying that mode.

## 7 Web Content and Local Data Security

Run the application without administrator privileges. Do not grant remote web content access to the filesystem, profile database, secrets, or arbitrary command execution. Disable unnecessary host objects and web messaging in WebView2; validate the origin before any interaction between the host and page [S10].

Apply `AreHostObjectsAllowed = false` and `IsWebMessageEnabled = false` to Proton WebViews. A separate diagnostic fixture may use a narrowly scoped channel, but that channel must not carry over into a Proton view. Set profile `IsPasswordAutosaveEnabled = false` and `IsGeneralAutofillEnabled = false` for the MVP; these settings are shared by WebViews within the same profile [S9]. This disables the browser's extra credential/form store, not Proton's session persistence.

Use one authoritative permission policy keyed by `(profileId, requestingOrigin, permissionKind)`. Set `SavesInProfile = false` when deciding requests and store an “Always allow/deny” choice in the application's policy store only when the user explicitly selects it. Deduplicate frame and top-level permission events; Microsoft notes that both can fire [S20]. Provide a reset-permissions action. Unknown permissions default to deny. Recheck policy for every generation; an allowed notification in A must not grant it in B. Notifications, if enabled, are best effort while the relevant environment is alive; do not promise delivery from closed profiles.

The initial page is `https://mail.proton.me/`. Allow the necessary verified Proton addresses for top-level navigation, including `https://account.proton.me/`. Compare parsed schemes and hostnames rather than substrings. Verify actual authentication redirects in the prototype. Subresource policy must account for CDNs and sign-in verification; do not blindly apply the same short allowlist to subresources.

Open external links from messages through a separate “Open in external browser” action initiated by the user. In Proxy mode, explain next to that action that the external browser uses its own session and network configuration. Permit only parsed HTTP/HTTPS URLs for that action. Do not automatically open a non-Proton redirect externally; authentication URLs can contain tokens. Do not transfer cookies or launch arbitrary URI schemes through the shell. Handle downloads, including blob-based attachments, through the download workflow rather than a blanket external-URL rule.

Save attachments through the browser download API to a validated, user-chosen destination; sanitize server-supplied filenames and handle collisions and cancellation. Do not refetch attachments using host networking, execute them automatically, or strip Windows-origin metadata supplied by the download path. Handle interrupted downloads without confusing completion with success. Verify keyboard focus, file dialogs, and Russian labels at 100–200 percent Windows display scaling; a normal WPF WebView may require deliberate layout for native child-window layering.

Keep JavaScript, the same-origin policy, sandboxing, and TLS validation in their normal operating state. Do not disable certificate validation to pass tests or make a proxy work. Remote debugging is available only in development builds.

Exclude passwords, tokens, cookies, keys, authorization headers, message contents, and URLs with sensitive query parameters from logs. Log metadata using UUIDs. Network traces and crash dumps may contain secrets; exclude them from diagnostic exports by default.

Store only application metadata in SQLite. Profile directories are accessible to the current Windows user; do not claim that all WebView2 files are automatically encrypted with DPAPI. Locking the application's UI does not encrypt data at rest.

## 8 Activity Reminders

Maintain two independent fields: `lastOpenedAt`, indicating that the application opened the profile; and `lastUserConfirmedVisitAt`, indicating that the user confirmed checking the mailbox. Do not present either field as Proton's server-side activity timestamp.

Proton's current support guidance says a Free account should be signed into and used at least once a year; a simple action such as opening an email counts as use. Accounts inactive for more than 12 months may be deleted. The older 24-month grace period ended on April 9, 2026 [S24]. Display a dated policy-help link; do not estimate a server-side deletion deadline from local records.

The suggested reminder interval is six calendar months after the user's confirmation, with a configurable interval of 1–12 months. Six months is an application default chosen to provide margin, not Proton's official inactivity threshold. If no confirmation exists on first launch, show a localized equivalent of “No check date recorded.” Store timestamps in UTC and display them in the local timezone.

For reproducible calendar arithmetic, retain the confirmation's local date and timezone ID alongside its UTC timestamp. Add calendar months to that local date and clamp invalid month-end dates to the last valid day; notify when the due date is reached. A later OS timezone change must not silently recompute the original anniversary. Store snooze separately, so postponing a reminder does not create a false visit. Keep confirmation events as a bounded local history; the UI must allow correcting an accidental mark. Session timestamps remain independent of both fields.

Reminders after the main application closes require an explicitly enabled tray mode or a configured Windows mechanism. By default, a reminder at the next launch is sufficient. Do not promise notifications while the application is not running.

MVP delivery requires only reminders during application use and on next launch. Tray operation and Windows scheduled notifications are optional later features. Start with profiles closed; do not implement automatic account launch or background checking solely to serve reminders.

Do not perform automated sign-ins, message reads, or background navigations to “keep accounts active.” Proton's rules concern account usage, not the existence of a local profile; its terms identify sign-in or product usage while already signed in [S2].

## 9 Data Model and Modules

Minimum `ProfileConfig` model:

| Field | Type and Meaning |
| --- | --- |
| `id` | Immutable UUID |
| `displayName`, `emailLabel`, `color`, `sortOrder`, `isFavorite`, `isPinned` | Local metadata; pinning does not override resource limits |
| `configRevision`, `lastAppliedRevision`, `pendingRevision` | Versioned configuration and recoverable application state; immutable revision snapshots |
| `networkMode`, `networkReadiness` | System/Proxy, or unset after an incomplete import; readiness records unresolved endpoint/credential requirements and blocks opening |
| `proxyEndpoint`, `proxyType`, `proxyAuthMode`, `proxyCredentialRef` | Parsed endpoint, supported type, None/Basic in the initial tested scope, and a reference to the secret |
| `userAgentMode`, `customUserAgent` | Default/Custom and an optional value |
| `languageMode`, `languageTag` | System/Custom and an optional validated BCP 47 tag |
| `scriptLocaleMode`, `scriptLocaleTag` | Default/MatchBrowserLanguage/Custom and an optional validated BCP 47 tag |
| `colorScheme`, `zoomFactor`, `windowBounds` | Auto/Light/Dark, finite zoom, and optional last window placement |
| `trackingPreventionLevel` | Balanced or Strict |
| `downloadDirectory` | User-selected attachment location |
| `lastOpenedAt`, `lastUserConfirmedVisitAt`, `confirmationLocalDate`, `confirmationTimeZoneId`, `reminderMonths`, `snoozedUntil` | Local visit bookkeeping, calendar basis, and independent snooze |

Related tables: `VisitConfirmation` for event history, `PermissionDecision` for per-origin choices, and `PendingOperation` for restart/reset/delete recovery. Use a versioned SQLite schema, transactions for metadata updates, and a pre-migration database backup. That backup is local recovery data and must not be included in diagnostic exports. A migration failure must keep the original database recoverable; never rebuild it silently as empty.

Derive the actual UDF path from the UUID and application root; do not accept an arbitrary path from an imported configuration. Keep runtime state separately: phase, browserProcessId, activeRevision, effectiveRuntimeVersion, and lastError. Do not mix it with persistent configuration.

Modules: `ProfileRepository`, `ProfileLifecycleService`, `BrowserHost`, `NetworkConfigurationAdapter`, `CredentialStore`, `NavigationPolicy`, `DownloadCoordinator`, `ReminderService`, and `DiagnosticsService`.

The browser adapter interface must explicitly describe its capabilities: persistent storage, proxy configuration type, restart requirements, and supported settings. Do not build a universal framework for dozens of engines; this boundary exists to allow replacement of the uncertain network implementation.

Main lifecycle states: Closed → Starting → Open → Closing → Closed. RecoveryRequired means resource ownership or shutdown is unresolved and blocks reuse or deletion. Keep errors, authentication status, and pending settings as orthogonal fields; a page-level network error does not imply a closed process. Changes requiring restart create a pending revision; apply network changes through Section 6.3. Other restart-required changes use the same close/create/recovery lifecycle with unchanged networking and their own setting checks. Theme and zoom may apply live with success/error handling; name, favorites, and reminder edits need no browser restart. Never mix generations or revisions.

### 9.1 Settings Import and Export

Use UTF-8 JSON with `schemaVersion: 1` and a `profiles` array. The example file in this package is a non-secret interchange example, not a copy of the internal database schema. Export only display metadata and user preferences. Omit UUIDs, UDF paths, credential references, permission grants, visit history, pending operations, machine-specific download paths, and window coordinates. A proxy endpoint can reveal infrastructure; include it only when the user selects that export option, and never include its credentials.

Validate the whole import before modifying storage. Reject unknown schema versions, duplicate keys, unknown fields, invalid enum values, invalid locale tags, non-finite/out-of-range numbers, and unsupported proxy types. Set a 1 MiB file limit and 1,000-record import limit. Display a preview, then import metadata transactionally with fresh UUIDs and empty lazy-created UDFs. Do not auto-open imported profiles or transfer trust decisions. If proxy data is omitted, record it as omitted, not as a silently selected System route; require the user to choose a network mode before that profile can open. Imported proxies requiring secrets remain in CredentialsRequired until configured.

Interchange profile keys are `displayName`, `emailLabel`, `color`, `isFavorite`, `network`, `userAgent`, `language`, `scriptLocale`, `colorScheme`, `zoomFactor`, `trackingPreventionLevel`, and `reminderMonths`. The companion example defines their nesting. A Proxy `network` object uses `mode`, `endpoint` (scheme/host/port), and `authMode`; a deliberately omitted endpoint is `null` and requires configuration before opening. `userAgent.value` is null in Default mode; `language.tag` is null in System mode; `scriptLocale.tag` is null unless mode is Custom. Export order defines initial imported order. Implement and ship a JSON Schema for this contract; empty optional labels may be null. A `network` mode of Unconfigured is valid in the interchange format and maps to an unset internal network mode.

Apply the same validation in the UI and importer: trimmed display name of 1–100 characters; optional label of at most 254 characters; color in `#RRGGBB` format; integer port from 1 to 65535; integer reminder interval from 1 to 12. Labels are display text, not verified identities. Enforce Section 5 limits and the conditional field rules above in the schema and semantic validator; locale support and network capability still require runtime checks.

## 10 Test Setup and Acceptance Criteria

Run automated checks on an owned HTTPS test setup using synthetic data, two origins, a test proxy endpoint, and controllable failures. The local test page must not read real Proton data. For test TLS, use a separately trusted test configuration rather than disabling certificate validation in the application.

| ID | Test | Acceptance Condition |
| --- | --- | --- |
| A01 | Open the same origin in profiles A and B and write different cookies, LocalStorage, IndexedDB, and CacheStorage values | Each profile sees only its own values after switching and restarting |
| A02 | Register a service worker and BroadcastChannel on the same origin in A and B | Worker state and channel messages do not cross profile boundaries |
| A03 | Open one profile from two application instances | The second opening is rejected; a crash does not leave a permanent lock |
| A04 | Sign out, reset, or delete A | B's authentication, settings, and data remain unchanged |
| A05 | Create a child window from A | The window uses A's environment, not B's or the default environment |
| A06 | Inspect UA over the network and in the document, iframe, and worker | Values match the configured policy; Client Hints limitations are documented |
| A07 | Switch Custom UA to Default | The current Runtime's native UA is restored, not an empty or outdated string |
| A08 | Change A's language, theme, and zoom | B is unaffected; any required restart is handled explicitly |
| A09 | Configure different test proxies for A and B | Each profile's HTTP/HTTPS requests are observed only on its assigned route |
| A10 | Disable A's proxy while pages and background worker requests are active | No undeclared direct HTTP/HTTPS access occurs; A reports an error and B continues working |
| A11 | Change A's proxy | The old environment terminates; new requests do not reuse old connections; the session persists unless revoked by the server |
| A12 | Receive 407 from the proxy and 401 from an unrelated website | The proxy secret is used only in the authorized context |
| A13 | Test fetch, iframes, workers, WSS, attachments, DNS, and UDP | Record the result, supported scope, and remaining limitations for each path |
| A14 | Test permissions and downloads in A and B | A decision for one profile does not apply to the other; downloaded files are not automatically executed |
| A15 | Simulate a crash of A's browser process | B retains its state; recovering A does not automatically clear its folder |
| A16 | Export settings and diagnostics | No secrets, tokens, cookies, or message content are included; import does not reuse UUIDs or UDFs |
| A17 | Test reminder calendar-month calculations and timezones | Dates are correct; the UI does not claim verified server-side activity |
| A18 | Create 100 synthetic records and switch among three open profiles | The directory works, the active-environment limit is respected, and closed profiles have no live browser processes |
| A19 | Test a missing Runtime and a Runtime update | Recovery/installation is understandable, the version is recorded, and profile data is preserved |
| A20 | Run a manual Proton smoke test using existing authorized accounts | Sign-in, 2FA, reading, drafts, attachments, and relaunch work; limitations are listed |
| A21 | Rapidly alternate open, close, restart, and delete, including cancelled initialization and delayed callbacks | At most one live generation per profile; no resurrected window or cross-profile callback/credential use |
| A22 | Exercise initialization, child windows, and asynchronous permission dialogs on WPF | No implicit default environment or early navigation; STA/message-pump rules respected; no hung deferrals |
| A23 | Delay process exit and leave a Runtime process alive after a simulated host crash | Timeout enters RecoveryRequired; UDF is not reused/deleted and unrelated processes are not killed |
| A24 | Interrupt each network-change step and change proxy credentials | One recoverable revision remains; no silent old-route/System reconnection; new credentials are used only by the new generation |
| A25 | Interrupt reset/delete and supply a junction, invalid ownership path, locked file, and external download directory | Cleanup resumes safely, blocks unsafe paths, preserves unrelated data, and reports incomplete deletion honestly |
| A26 | Import malformed/oversized JSON, unknown fields/versions, invalid values, and missing proxy data | No partial import; valid records receive fresh identities; incomplete networking stays blocked; no grants or sessions transfer |
| A27 | Grant/revoke permissions in A, restart it, and request the same permissions in B and child frames | One authoritative policy, no duplicate prompts, no cross-profile grants, and reset revokes stored application choices |
| A28 | Use different browser languages/script locales in A and B, then restore defaults | Header/navigator/Intl observations match documented scopes; timezone remains native; Auto/Light/Dark mapping is correct |
| A29 | Download a blob attachment, cancel another download, follow an external link, and reject an unsafe URI | Downloads stay on the owning browser path; cancellation is not completion; external launch requires a user action and never transfers session data |
| A30 | Open a fourth profile while three are live and one has unsaved test input or an active download | No silent eviction or draft-loss claim; user selects closure or cancels; limit includes starting/closing environments |
| A31 | Start a persisted worker-enabled profile with a stopped proxy, then restore connectivity | Observation from process startup shows no undeclared direct HTTP/HTTPS route; failed startup is not reported as protected or ready |
| A32 | Test schema migration failure, effective UDF override, Runtime update, and repeated open/close cycles | Original metadata stays recoverable; unexpected UDF blocks opening; no surviving closed-profile process groups or steadily accumulating controllers/handlers |

For A20, the user supplies accounts during the manual test; do not put passwords in source code, tests, or reports. Send a test email only within an explicitly agreed test scenario. If account access is unavailable, complete the remaining work and mark A20 Blocked with the reason “not performed: account access unavailable,” not as passed.

Record the Windows build, SDK and Runtime versions, proxy type, test setup configuration, results, and network observation method. Each criterion needs a status (Pass, Fail, Blocked, or NotApplicable), a short evidence reference, and a reason for anything other than Pass. A09–A13, A24, and A31 cannot pass in a System-only build; mark them NotApplicable to that build and Blocked for the full proxy objective. Other mixed criteria must still exercise their applicable parts. Tests on one version combination are not an indefinite guarantee. After Runtime changes, repeat the checks related to networking, sign-in, storage, and UA.

For criteria containing several subchecks, record each subcheck separately. A parent criterion cannot be Pass if any required subcheck is failed or unperformed.

Matching Canvas, WebGL, fonts, or other hardware-related attributes across profiles is acceptable and does not constitute a session-isolation failure. Do not substitute a third-party site's “100 percent uniqueness” score for A01–A32. Test reports must not include fingerprints gathered from real mail accounts; use synthetic fixtures.

Reference performance setup: Windows 11 x64, four CPU cores, 16 GB RAM, and SSD; record the actual machine. With 100 synthetic records, profile-list search and selection should respond within 200 ms at the 95th percentile over 30 actions, excluding website load time. After 20 open/close cycles and settled shutdowns, report total application/Runtime memory and process counts and investigate sustained growth. Do not set a fixed mail-page loading SLA that depends on Proton or a proxy outside the application's control.

## 11 Development Stages and Deliverables

Stage 1 — technical prototype. Two profiles, two folders, baseline A01–A08 checks, A21–A23/A28 initialization and lifecycle checks, and investigation of A09–A13/A31. Manual sign-in follows when authorized test accounts are available. Deliver a working minimal project and `ADR-001-browser-engine.md` with a conclusion on the network API. Resolve data mixing before investing in a large UI.

Stage 2 — application. Implement the directory, lifecycle, settings, secrets, downloads, navigation, and reminders. Clearly label experimental networking. Handle API errors rather than silently replacing failed settings with defaults.

Stage 3 — verification and packaging. Run applicable acceptance checks and prepare a Windows x64 build, instructions, Runtime installation/detection, and a report. If no signing certificate is available, mark installer signing as outstanding; do not claim that the installer is signed.

Deliver source code, pinned dependencies, a build script, a runnable build with a stated status, a README, an import/export JSON Schema and secret-free example, the test setup, A01–A32 results, and a limitations list. Include a capability manifest listing minimum SDK/Runtime requirements and actual tested versions for every API used. Distinguish “implemented,” “verified,” “experimental,” and “not performed” in the report.

Provide a repeatable Windows CI build and fixture test command. Use self-contained x64 publishing or document installation of the matching .NET Desktop Runtime; this is separate from WebView2 Runtime installation. Installation and updates preserve app metadata and UDFs. Uninstall offers an explicit choice to keep or remove managed profiles; it does not delete independently saved attachments or the shared WebView2 Runtime. Do not ship an automatic UDF backup/export feature in the MVP: sessions are sensitive and copying live browser folders is not a validated recovery mechanism.

If one feature is blocked, complete independent parts. Do not imply that successful compilation on another OS verifies WebView2 behavior when Windows testing has not been performed.

## 12 Further Isolation Options

A separate host process per profile can reduce the impact of application crashes or defects. It complicates window management and IPC; introduce it after profiling and identifying a specific threat. It does not prevent the trusted main process or other software running as the same Windows user from reading accessible files.

Separate Windows users or virtual machines provide stronger separation of permissions and system environments. This is a separate deployment mode with higher resource costs, not a `ProfileName` setting. Even this does not prove that accounts are unlinkable using all server-side signals.

Alternative for individual proxies: Electron with a persistent session per profile and `session.setProxy()`. Account for in-flight requests and old pooled connections when changing the network; the documentation identifies `closeAllConnections()` for closing connections [S13]. Browser storage and session configuration remain separate from network requests issued by the host process itself. Do not assume WebView2 browser folders can be migrated into Electron: migrate metadata and require sign-in again. Repeat the acceptance matrix for the new engine.

## 13 Sources and Verification Status

Reviewed on October 3, 2026, using official Proton, Microsoft, Electron, and MDN documentation. The references below substantiate platform behavior. Architecture choices, defaults, the active-profile limit, and acceptance criteria are design decisions made in this specification.

| Code | Source | Supported Point |
| --- | --- | --- |
| S1 | [Proton Multiple accounts in browser tabs](https://proton.me/support/multiple-accounts-browser-tabs) | Multiple accounts through the web interface |
| S2 | [Proton Terms of Service](https://proton.me/legal/terms) | Free-account usage restrictions and activity rules |
| S3 | [Proton IMAP SMTP and POP3 setup](https://proton.me/support/imap-smtp-and-pop3-setup) | Bridge eligibility |
| S4 | [Microsoft WebView2 process model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model) | Relationship between UDFs, environments, and process groups |
| S5 | [Microsoft Manage user data folders](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder) | Storage, profiles, and the resource cost of multiple UDFs |
| S6 | [Microsoft WebView2 browser flags](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/webview-features-flags) | Restrictions on production use of flags and the scope of proxy-server |
| S7 | [Microsoft CoreWebView2EnvironmentOptions](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2environmentoptions?view=webview2-winrt-1.0.3856.49) | Language, SSO, extensions, tracking prevention, and exclusive access |
| S8 | [Microsoft UserAgent property](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2settings.useragent) | UA behavior, empty values, Client Hints, and workers |
| S9 | [Microsoft CoreWebView2Profile](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2profile?view=webview2-winrt-1.0.3650.58) | Separate data, color scheme, and tracking prevention level |
| S10 | [Microsoft Develop secure WebView2 apps](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security) | Origin checks, host objects, web messaging, and least privilege |
| S11 | [Microsoft Distribute WebView2 Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) | Runtime distribution and detection |
| S12 | [MDN Fingerprinting](https://developer.mozilla.org/en-US/docs/Glossary/Fingerprinting) | Composite attributes of a browser fingerprint |
| S13 | [Electron Session API](https://www.electronjs.org/docs/latest/api/session) | Persistent sessions, setProxy, and closing connections |
| S14 | [Microsoft BasicAuthenticationRequestedEventArgs](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2basicauthenticationrequestedeventargs?view=webview2-1.0.4078.44) | Authentication-request context, including the proxy address |
| S15 | [Microsoft ControllerOptions and ScriptLocale](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2controlleroptions?view=webview2-winrt-1.0.4191.47) | Script locale and its distinction from browser language |
| S16 | [Microsoft Process-related events](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-related-events) | BrowserProcessExited, UDF release, and cached authentication lifecycle |
| S17 | [Microsoft WebView2 threading model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model) | STA UI access, message pump, asynchronous event deferrals |
| S18 | [Microsoft NewWindowRequested](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2newwindowrequestedeventargs?view=webview2-winrt-1.0.3912.50) | Same environment/profile and unnavigated child requirement |
| S19 | [Microsoft PreferredColorScheme enum](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2preferredcolorscheme?view=webview2-winrt-1.0.3856.49) | Auto, Light, and Dark API values |
| S20 | [Microsoft PermissionRequested](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2permissionrequestedeventargs?view=webview2-winrt-1.0.4078.44) | Persistence, SavesInProfile, and frame/top-level events |
| S21 | [Microsoft WPF EnsureCoreWebView2Async](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.wpf.webview2.ensurecorewebview2async?view=webview2-dotnet-1.0.4129.50) | Explicit environment and controller-options initialization |
| S22 | [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) | Supported .NET 10 LTS baseline |
| S23 | [Microsoft CoreWebView2Environment.CreateAsync](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.createasync?view=webview2-dotnet-1.0.4129.50) | Environment/registry overrides of startup configuration |
| S24 | [Proton Inactive accounts](https://proton.me/support/inactive-accounts) | Annual sign-in/use guidance, possible deletion after 12 months, and expired legacy grace period |

## 14 Instructions to the Development Bot

Implement the application according to this specification, starting with the technical prototype and isolation matrix. Use current stable APIs and record the actual versions selected. Do not create Proton accounts for load testing. Do not present different UA strings or IP addresses as independent hardware fingerprints.

First validate the owned HTTPS test setup and two profiles. Then implement the remaining features and prepare a reviewable build and a report against A01–A32. Use `CHANGELOG.md` for the rationale behind this revision; the specification takes precedence over examples. If the engine cannot meet a requirement, state the exact limitation, cite the supporting source, and present a workable alternative. Do not hide the problem behind experimental flags or falsely successful tests.
