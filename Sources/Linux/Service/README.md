# Ubuntu user service

The .NET 10 service owns archive/configuration writes and exposes the versioned
session D-Bus contract in `../Protocol`. GTK and GNOME Shell clients subscribe to
its snapshots and signals. Files use XDG config/data/state/cache/runtime locations;
new private data has a `0077` umask. `--snapshot` probes without taking hardware
restoration ownership or starting the background loop.

Clipboard capture and restore use real Unix descriptors through
`Tmds.DBus.Protocol`; the service validates bounded MIME metadata and payload sizes.
Restore descriptors are sealed and read-only. Copied files are preserved in the
archive and reoffered as copy operations. Query supports `offset` (default 0),
`limit` (default 60, maximum 500), and an unpaginated filtered `total` so old items
remain accessible. Source attribution distinguishes verified test sources,
foreground observation, and unknown sources.

The service starts each new app session with the microphone unmuted, including
when older settings saved a muted state. Later mute changes are persisted and
replayed with audio configuration on engine reconnection within that session.
The C++ engine owns realtime processing and a private runtime socket. HDR ownership
uses a durable restoration journal and honors explicit manual changes. GNOME
display-power and logind sleep inhibitors are separate observations. The local
Antigravity HTTP bridge requires its generated bearer key; exported diagnostic
reports omit keys, clipboard content, image tags, and audio data. vLLM remains a
separate systemd user unit when this daemon exits.

Autostart reflects the actual systemd unit state, including changes made by the
packaged setup command. Toggling it only enables/disables future startup; it never
starts or stops an active service. Shell extension availability and actual enabled
state are queried through the public GNOME Extensions interface separately from
the extension's live connection heartbeat. Explicit quit replies are flushed
through a D-Bus name-release round trip before the process disconnects.

Build and run from the repository root with .NET 10:

```sh
dotnet build Sources/Linux/Service/PersonalTools.Service.csproj
dotnet run --project Sources/Linux/Service/PersonalTools.Service.csproj
dotnet run --project Tests/Linux/Service/PersonalTools.Service.Tests.csproj
python3 Tests/Linux/Service/test_vllm_runtime.py
```

The service suite covers archive, FD, protocol, lifecycle, persistence,
pagination, and diagnostic-redaction checks. It uses an isolated XDG tree, actual
D-Bus Unix-FD messages, a deterministic child CLI, and an audio protocol peer.
These checks complement actual GNOME/GTK clipboard round trips and hardware tests;
they do not claim NPU, HDR hardware, or visual-design acceptance.

`Tests/Linux/Service/LiveAntigravity.cs` additionally exercises the installed,
logged-in CLI using synthetic public text and an image: model discovery, real
authenticated HTTP/SSE generation, collection-scoped retrieval, image tagging,
and bridge shutdown. It does not send the user's clipboard history. Run this
explicitly using the test executable's `--live-antigravity` argument when an
authenticated account is available; it can consume account quota.
