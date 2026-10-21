# Personal Tools Linux protocol v1

All JSON uses camelCase and UTC ISO8601 timestamps. UI localization is ko-KR.
The daemon owns persistence; GTK and Shell clients only hold view state.

## Session D-Bus

- Bus name: `io.griplabs.PersonalTools`
- Object: `/io/griplabs/PersonalTools`
- Interface: `io.griplabs.PersonalTools1`
- `GetSnapshot() -> s`: complete snapshot JSON, as below.
- `Execute(s domain, s action, s payload) -> s`: result JSON `{ok,data?,operationId?,error?:{code,message,retryable}}`. Invalid input is an error, never a successful empty state.
- `CaptureClipboard(s metadata, ah payloadFds) -> s`: import clipboard payloads; metadata `{generation,sourceApp,sourceAttribution,mimes:[{mime,fdIndex,length,name?}]}`. FDs are supplied with the message UnixFDList. Enforce size, MIME and file limits before persistence. Text/image/files only; do not follow arbitrary symlinks or URI schemes.
- `OpenClipboard(s entryId) -> (s metadata, ah payloadFds)`: immutable, read-only payload FDs; metadata `{entryId,mimes:[{mime,fdIndex,length}]}`. Do not send base64 payloads over D-Bus. Returned source survives daemon restart.
- Signals `Changed(s snapshot)`, `ClipboardChanged()`, `Event(s event)`.
- JSON/metadata messages <= 1 MiB. Bulk clipboard limits come from settings.

Snapshot shape (arrays may be empty; missing hardware is status=unavailable, never fake data):

```json
{
  "version":1,"revision":0,"observedAt":"2026-10-03T00:00:00Z",
  "service":{"status":"running","version":"0.7.0"},
  "clipboard":{"paused":false,"entries":[],"collections":[],"settings":{"archivePath":"","itemLimitBytes":104857600,"totalLimitBytes":10737418240,"excludedApps":[],"aiEnabled":false,"taggingEnabled":false}},
  "audio":{"status":"unavailable","error":null,"outputs":[],"inputs":[],"outputId":"","inputId":"","eqEnabled":false,"aecEnabled":false,"muted":false,"leftFilters":[],"rightFilters":[],"leftFile":"","rightFile":"","npu":{"status":"unavailable","device":"","inferences":0},"latencyMs":null,"inputLevelDb":null},
  "display":{"status":"unknown","error":null,"monitors":[],"autoHdr":false,"rules":[],"inhibitors":[],"sleepInhibitors":[],"observedAt":null},
  "ai":{"antigravity":{"status":"stopped","cliPath":"","model":"antigravity","models":[],"port":8877,"timeoutSeconds":120,"baseUrl":"http://127.0.0.1:8877/v1","keyConfigured":false,"error":null},"vllm":{"status":"stopped","model":"qwen3.8-27b","checkpoint":"RedHatAI/Qwen3.8-27B-INT4","baseUrl":"http://127.0.0.1:8000/v1","context":8192,"quantization":"INT4","kvCache":"FP8","steps":[],"elapsedSeconds":0,"gpu":"","error":null}},
  "settings":{"theme":"system","autostart":false,"background":true,"clipboardShortcut":"<Control><Shift>v","microphoneShortcut":"<Super><Alt>m","extensionConnected":false,"dataPath":"","logPath":""},
  "diagnostics":{"events":[],"components":[]}
}
```

Entry: `{id,kind,title,preview,sourceApp,sourceAttribution,createdAt,pinned,categoryIds,thumbnailUri?,tags:[],tagState?,attachments:[{name,mime,length}]}`. `kind` is text/url/image/files. Source attribution is verified/foreground-observed/unknown. Thumbnail URI is a daemon-owned file URI. Original copied data is never fixture content.
Collection: `{id,name}`. EQ filter: `{frequency,gain,q,enabled:true}`. Device: `{id,name,description?,channels?}`.
Monitor: `{id,name,connector,width,height,refreshRate,primary,hdrSupported,hdrEnabled}`.
Inhibitor: `{id,appId,appName,reason,source,scope}`. Event: `{id,time,component,level,message}`.
vLLM step: `{id,label,state,detail?,progress?,current?,total?}`; state=pending/running/done/error. `progress` is only factual progress inside that step.

## Execute domains and actions

- clipboard/query `{query,filter:{formats:[],apps:[],from?,through?,categoryId?,pinnedOnly?}}` -> `{entries}`.
- clipboard/pause `{paused}`; pin `{id,pinned}`; delete `{id}`.
- clipboard/save-collection `{id?,name}`; delete-collection `{id}`; set-collections `{id,categoryIds}`.
- clipboard/settings `{archivePath?,itemLimitBytes?,totalLimitBytes?,excludedApps?,aiEnabled?,taggingEnabled?}`.
- clipboard/ai-search `{query,categoryId?,requestId}` -> `{entries,summary}`; cancel-search `{requestId}`; tag `{id,retry?}`.
- clipboard/detail `{id}` -> `{entry,text?,files?,imageUri?}`. Copy/paste themselves are Shell calls.
- audio/refresh `{}`; configure `{outputId?,inputId?,eqEnabled?,aecEnabled?,leftFilters?,rightFilters?}`; import-eq `{channel:"left"|"right",path}`; mute `{muted?}` (omitted toggles all active inputs excluding monitors); shutdown `{}`.
- display/refresh `{}`; hdr `{id,enabled}`; auto-hdr `{enabled}`; save-rule `{id?,name,executable,desktopId?,enabled:true}`; delete-rule `{id}`.
- antigravity/configure `{cliPath?,model?,port?,timeoutSeconds?}`; models `{}` -> `{models}`; start `{}`; stop `{}`; login `{}`; test `{prompt}` -> `{response}`; api-key `{}` -> `{apiKey}`. API key is returned only on explicit reveal/copy, never in snapshots/logs.
- vllm/refresh `{}`; start `{}`; stop `{}`; configure `{context?,maxSequences?}`; logs `{}` -> `{text}`. Start/stop are asynchronous operations and snapshots provide progress.
- settings/update `{theme?,autostart?,background?,clipboardShortcut?,microphoneShortcut?}`.
- diagnostics/refresh `{}`; export `{}` -> `{path}`; audio `{}` -> `{...actual engine diagnostics}`.
- lifecycle/quit `{}` (restores owned HDR/audio changes, cancels own jobs; does not stop vLLM).

## Shell service

- Bus `io.griplabs.PersonalTools.Shell`; object `/io/griplabs/PersonalTools/Shell`; interface `io.griplabs.PersonalTools.Shell1`.
- `ShowClipboard()`; `PutClipboard(s entryId,b paste) -> s result`; `GetStatus() -> s`; `ConfigureShortcuts(s json) -> s result`.
- Shell registers/refreshes connectivity through `Execute("shell","status",{"connected":true,"version":"..."})`; on disable false.
- Daemon may obtain real display state/HDR via Mutter directly. Shell owns clipboard, shortcut/paste and its own views.
- Source creation uses a native Meta.SelectionSource subclass with per-read independent cursor; source+FD lifetime is independent of JS UI and daemon.
- Capture pins source+generation and discards superseded data. Sensitive/excluded foreground apps do not capture. Clipboard persistence is not cleared when a panel closes.

## Audio runtime socket

- `$XDG_RUNTIME_DIR/personal-tools/audio.sock`, mode0600, same UID.
- JSON lines <=1 MiB: `{id,command,args}` -> `{id,ok,data?,error?:{code,message}}`.
- commands `status`, `devices`, `configure`, `import-eq`, `mute`, `shutdown` use the same keys as audio domain above. `status` data is the snapshot audio object; `devices` data `{outputs,inputs}`.
- The control thread validates and prepares processing state; realtime callbacks allocate no memory, block on no locks and perform no file/IPC work.
- Audio settings under `$XDG_CONFIG_HOME/personal-tools/audio.json`; imported EQ under `$XDG_DATA_HOME/personal-tools/filters`.

## Test isolation

Fixtures are selected explicitly by GTK `--fixture` and `--screen <manifest-id>` or isolated Shell test harness. Fixtures must never trigger hardware, capture the user's clipboard, start models, modify user settings, or claim live success. Production defaults contain no sample entries or invented hardware.

### MO32U24 input-based power control

`display.monitorPower` contains `available`, `enabled` (persistent policy), `running`, `status`, `error`, `timeoutSeconds` and `target`. `Execute("display","monitor-power",{"enabled":true|false})` controls the separately installed `~/.local/bin/mo32-power` helper. Disabling stops the policy and powers on both displays. Missing helpers are unavailable; stale or stopped services report an error. Both GTK and Shell use this same state and command.
