# Ubuntu GNOME Shell validation — 2026-10-03

## Scope and isolation

GNOME Shell 50.1 / Mutter 50.1, native St extension, GI `Meta.SelectionSource` helper, actual .NET 10 daemon, and two independent GTK 4 Wayland applications were tested. The original runs below use a private session D-Bus, runtime directory, extension installation and XDG archive. No existing clipboard data or default audio routes were changed by those isolated tests. The later current-session checks and their cleanup limitation are recorded separately below.

## Functional results

`build/linux/shell-daemon-pointer-final/shell.log` records all of these passes:

- `PT_REAL_DAEMON_MULTI_MIME_PASS`: text/plain, UTF-8 text, HTML and PNG cross the actual daemon Unix-FD boundary and are restored byte-for-byte into the second Wayland app.
- `PT_REAL_DAEMON_FOCUS_PASTE_PASS`: paste returns focus to the target that was active before the popup and delivers Ctrl+V.
- `PT_REAL_DAEMON_FILES_PASS`: two real files are archived, the originals are deleted, and restored URIs point to byte-identical preserved copies. A prior cut becomes copy.
- `PT_REAL_DAEMON_EXTENSION_SETTINGS_PASS`: actual `settings/update extensionEnabled` commands use public GNOME D-Bus calls to disable and re-enable the extension. Observed state, actor removal and archive reconnection match the request.
- `PT_REAL_DAEMON_EXTENSION_RECONNECT_SHORTCUT_PASS`: extension disable/enable reconnects to the archive; Super+Shift+V opens the popup; Down, Right and Escape navigate and dismiss it.
- `PT_REAL_DAEMON_RESTART_PASS`: daemon process restart preserves the archive and reconnects the extension.
- `PT_REAL_DAEMON_SOURCE_LIFETIME_PASS`: an already restored clipboard remains readable after both daemon and bridge shutdown.
- `PT_REAL_DAEMON_FILTER_POINTER_PASS`: real primary-button clicks open the nested filter popover, select an image filter, apply it, reset it, and Escape closes only the popover.
- `PT_REAL_DAEMON_OUTSIDE_CLICK_PASS`: a real primary-button event outside the panel closes it; repeated Super+Shift+V also closes it.
- Pause suppresses capture. `build/linux/shell-wayland-pointer-final/shell.log` also covers canceled restoration and missing-target copy-only fallback.

The mixed-monitor run uses actual Mutter virtual outputs: 1488×1056 at 100% and 1600×900 at 125%, arranged horizontally. Popup placement follows the pointer onto the second monitor. This verifies the compositor path; physical monitor hot-unplug was not performed.

`meson test -C build/linux/shell --print-errorlogs` passes the native FD-source test. `gjs -m Tests/Linux/Shell/model-test.js` passes bounded MIME selection, exclusions, terminal paste modifiers, geometry, collection scope and stale-response rejection.

## Defects found and repaired during integration

- Read-only clipboard queries triggered a Changed notification and another query, indefinitely invalidating pending results. The panel now compares clipboard content before requerying; the daemon separately suppresses read-only command broadcasts.
- GNOME startup could finish before attaching the automation listener. Shared `automation.js` uses a delayed recovery path and one run promise, preventing both missed and duplicate runs.
- Light CSS lost precedence when Shell replaced its theme. Theme replacement is tracked and generated light selectors have a `pt-light` root/descendant qualifier.
- GNOME 50 modal grabs deliver captured events to the grabbed actor, not the stage ancestor. The outside-click handler now listens on the panel actor and checks event coordinates. Separate popovers use nested modal grabs so their real pointer controls and Escape work. Final pointer tests use Clutter primary button 1; the prior raw Linux button code 272 was invalid for this API. The tests retain one virtual pointer across focus and hotkey operations so device destruction cannot restore an earlier seat position.
- The quick menu clipped its last controls on a 700px-high desktop. Its native menu content now scrolls within the monitor work area; the final action is verified visible after scrolling.
- Explicit pixel fonts ignored GNOME text enlargement. Base font size now follows `text-scaling-factor`, with relative child sizes.
- Shell's synthetic `window:<id>` fallback is no longer represented as a known application. Source attribution remains foreground-observed for resolved desktop IDs and unknown otherwise.
- A 30-second Shell status heartbeat prevents a healthy long-running extension from appearing disconnected after the daemon's 90-second observation window.
- Unchanged daemon health snapshots no longer rebuild the quick menu or collection tabs and discard keyboard focus.
- Search clear buttons and rounded selected collection tabs now follow the approved filled-search states. Shortcut badges follow the configured key binding.
- The extension loads the pinned Ubuntu Sans, Ubuntu Sans Mono and Noto Sans CJK files into its actual Clutter Pango font map. `build/linux/shell-scenes-pointer-final/shell.log` records all four bundled font files successfully loaded before native capture.

## Native visual evidence

All screenshots below come from `Shell.Screenshot` in a real isolated Wayland compositor. They are not HTML renderings or mockup images.

| Case | Evidence directory | Observed result |
| --- | --- | --- |
| 100%, dark, 1488×1056 logical | `build/linux/shell-scenes-pointer-final` | States 02, 03, 14, 17; panel 1440×488 at x=24,y=544; cards 288×236 |
| 125%, 1860×1320 physical | `build/linux/shell-qa-final-125` | DBus confirms monitor scale 1.25 and 1488×1056 logical stage |
| 150%, 2232×1584 physical | `build/linux/shell-qa-final-150` | DBus confirms monitor scale 1.5 and 1488×1056 logical stage |
| 200%, 2976×2112 physical | `build/linux/shell-qa-final-200` | DBus confirms monitor scale 2 and 1488×1056 logical stage |
| Light | `build/linux/shell-qa-final-light` | Native background resolves to #FAFAFA; olive controls use onAccent text |
| 1000×700, text scale 1.25 | `build/linux/shell-qa-final-small` | Search/footer stay visible; menu scroll exposes final action |

Scale, light and small-window variants were recaptured after the common typography, scrolling and separator fixes. The final 100% capture follows the modal pointer repair; that repair changes event routing and preserves the previous visual layout. All four app-owned crops are pixel-identical to `shell-scenes-fonts-final` (0 changed pixels), recorded in `shell-scenes-pointer-final/previous-capture-diff.json`. Actual filter open/selected states and outside-click dismissal are captured separately in `build/linux/shell-daemon-pointer-final`.

## Design comparison status

Run `python3 Tests/Linux/Shell/compare-design.py` to regenerate the combined reference/native images in `build/linux/shell-design-comparison`. Each source is normalized to 1488×1056. Only app-owned content is compared: the surrounding editor, wallpaper and desktop top-bar examples in the generated reference are not the extension UI. The popup crop excludes a 2px reference canvas alignment difference. Focused header and quick-menu control crops are included.

The comparison checks font family, size and weight, spacing, palette, imagery, icons and copy. The implementation follows the approved token values; actual Yaru symbolic icons replace image-generator approximations. Corrected shortcut hints are intentional. Fixture imagery is generated test content and never appears in the production archive without actual user entries.

The final Shell comparison found no unresolved functional P0/P1/P2 defect. The missing fourth preview line, quick-menu row separators, search clear control and rounded selected tabs were repaired and recaptured. Remaining expected differences are generated-reference thumbnail composition, actual Yaru icon shapes, and reference rendering noise. The quick-menu groups are approximately 5–10px more compact than their generated counterparts, with all controls visible. This is recorded as a minor visual difference; a uniform ≤2 logical-pixel result has not been established. Overall approval of all 17 screens remains in the root `design-qa.md`.

## Reproduce

```sh
python3 Tests/Linux/Shell/run-isolated.py --script daemon-wayland.js --real-service --second-monitor 1600x900 --monitor-scales 1,1.25 --output build/linux/shell-daemon-pointer-final
python3 Tests/Linux/Shell/run-isolated.py --script wayland.js --output build/linux/shell-wayland-pointer-final
python3 Tests/Linux/Shell/run-isolated.py --script scenes.js --output build/linux/shell-scenes-pointer-final
python3 Tests/Linux/Shell/run-isolated.py --script scenes.js --theme light --output build/linux/shell-qa-final-light
python3 Tests/Linux/Shell/run-isolated.py --script scenes.js --size 1000x700 --text-scale 1.25 --output build/linux/shell-qa-final-small
python3 Tests/Linux/Shell/compare-design.py
```

The harness compiles the native helper and stages the generated styles. Rebuild the Debug daemon first if service source changed. `environment.json` records the observed logical geometry and monitor scales for each run. Some private-session shutdowns produce GNOME IBus/portal warnings after the test markers; they do not originate in extension code. App/extension errors before the pass markers remain failures.

## Current user session after login

On 2026-10-03 the user logged out and back in. The installed extension became
available, was enabled through the public API, and reported `clipboardReady=true`
with no error. The daemon and extension report the same connected state.

`Tests/Linux/Shell/live-session.js` uses a dedicated GTK target and the public
Mutter RemoteDesktop input API. It does not use Shell Eval or unsafe mode.
`build/linux/relogin-session/clipboard-current-session-old-shell.json` records
28 successful checks for text/HTML/PNG capture and byte-identical FD restoration,
Ctrl+V, Super+Shift+V open/toggle, Escape, and focus return. The final run restored
all eight original MIME payloads byte-for-byte, removed only two identified test
entries, and restored recording. A memory-only clipboard owner exits on the next
copy; clipboard bytes are not written to the evidence files.

The current loaded ubuntu3 module fails card Enter. A direct paste request using
the same record succeeds, including original-window focus. The failure remains
with the synthetic query pasted into the search field, avoiding keyboard IME
conversion. `0.7.0~ubuntu4` explicitly connects card key handling before the
St.Button default Return action. The new actual-key regression in
`build/linux/shell-card-enter-final/shell.log` passes
`PT_REAL_DAEMON_CARD_ENTER_PASTE_PASS` in a fresh mixed-scale Wayland compositor.
This patch changes no style or layout. The installed package passes 23 checks,
with no source drift, in `build/linux/packaging-qa-ubuntu4.json`.

GNOME 50's installed `extensionSystem.js` imports each module URI once. Its
ReloadExtension reuses that module cache. The current user session therefore
still needs a login restart to execute and verify the corrected card code.
This is an open functional acceptance item, not a completed current-session pass.

The first live harness attempt failed to confirm original clipboard restoration
after its focus cleanup timed out. It is preserved in
`build/linux/relogin-session/clipboard-attempt1.json`; original data presence is
unknown, so restoration is not claimed for that attempt. The harness now retains
its in-memory backup after cleanup errors instead of exiting. Later successful
restorations do not retroactively establish the first attempt's restoration.

The login also exposed a service activation defect when the user manager survives
logout. The ubuntu4 unit now belongs to `graphical-session.target`; setup uses
reenable to migrate the old default-target link. Both the temporary-root tests
(8/8, `session-startup-qa.json`) and real-user-manager temporary target/worker
behavior checks (9/9, `session-relogin-live.json`) pass. The latter reproduces the
old missed restart and verifies a fresh worker PID with the corrected relationship,
without stopping the real GNOME session or production services. The actual link
migration is recorded in `build/linux/relogin-session/installation.json`.

## Window focus slots — 2026-10-06

Added eight **window** slots on `1–4`, `Q`, `W`, `E`, `R`. `Super+Shift+key`
registers the focused window; `Super+key` activates that exact window. Registrations
belong to the current Shell session, survive an extension disable/enable cycle
while the window lives, and are cleared when that window closes.

`build/linux/window-shortcuts-qa/shell.log` passes all 11 acceptance groups in a
private GNOME 50.1 compositor, using real GTK Wayland/XWayland windows and virtual
keyboard **keycode** events through Mutter:

- All 16 combinations, with eight different windows owned by one application.
- Slot replacement, minimized-window restoration, workspace changes and overview exit.
- Caps Lock, reserved clipboard keys and clipboard paste binding holds.
- Closing a window, empty-slot focus preservation and a new window with the same title.
- An XWayland window registered and activated alongside Wayland windows.
- Shell/WM/Dock conflicts yielding only matching accelerators; unrelated alternate
  bindings, Ctrl combinations and Dock slots 5–0 retained. Dock handlers are
  reproduced using real GSettings keybindings, rather than loading the whole Dock.
- Disable-time restoration, external-grab failure with complete rollback, preservation
  of newer user settings, interrupted-journal recovery, and a full extension cycle
  that keeps living targets and drops windows closed while disabled.

The registration OSD is captured and visually checked in
`build/linux/window-shortcuts-qa/registration.png`. Existing clipboard keyboard
regressions also pass in `build/linux/window-shortcuts-keyboard-regression/shell.log`;
the Shell model test and strict GSettings schema compilation pass.

Mutter's preference notification runs at `DEFAULT_IDLE + 10`; shortcut acquisition
therefore runs at `PRIORITY_LOW`, after the binding table is rebuilt. This was
confirmed against the installed headers and [Mutter 50.1 preference handling](https://github.com/GNOME/mutter/blob/50.1/src/core/prefs.c).
The private compositor's X11 allowlist includes the GTK test client's observed
lowercase `gjs` WM class. Neither adjustment changes the user's input source.

Installed only `extension.js`, `windowShortcuts.js`, the schema XML and its compiled
schema in the existing user extension. File hashes, backup path and current-session
status are recorded in `build/linux/window-shortcuts-install.json`. Live keyboard
and input-source settings are unchanged. The running Shell still has the previous
module without `windowShortcuts` in `GetStatus`: **activation requires the next
logout/login**. The current physical desktop and a real lock/unlock flow remain
unverified; the isolated extension lifecycle checks do not claim those results.

### Main application registration list — 2026-10-06

The main application's **창 빠른 전환** sidebar page displays all eight slots,
their shortcut, exact window title, app name and registered count. Shell provides
an authorized `GetWindowShortcuts` method and coalesced `WindowShortcutsChanged`
signals for registration, replacement, title changes and closure. The GTK client
clears stale titles when the Shell owner disconnects and reconnects automatically;
older extensions are shown as requiring a login restart, never as eight empty slots.
This session state bypasses the persistent service snapshot and does not wait for
an unrelated daemon connection before updating the page.

Validation:

- `build/linux/window-shortcuts-ui/shell.log`: physical shortcuts → real Shell
  D-Bus → rendered GTK labels; registration, title change, replacement, closing
  a window registered in two slots, disconnect and reconnect all passed.
- `main-window-slots.png` in that directory: actual main application, dark theme,
  1488×1056, displaying two real test-window registrations.
- `build/linux/window-shortcuts-ui-light/`: 1120×900, 1.25 text scale; registered,
  empty and outdated-extension states, plus mouse-wheel captures showing the
  last R row and complete footer. Fixture images identify their example content.
- `build/linux/window-shortcuts-with-ui-regression/shell.log`: all 11 original
  window-shortcut acceptance groups still pass. Existing GTK model tests: 22 pass.
- An independent native UI review returned `ship`; supplemental scroll captures
  resolved its evidence gap. Existing tokens/styles were preserved.

System UI files are root-owned, so this update uses a reversible user overlay in
`~/.local/share/personal-tools/ui/`, with a launcher at `~/.local/bin/personal-tools`
and a user desktop entry. Unchanged UI files and Design resources link to the
installed system package. The main UI alone was reopened at `--page windows`;
the daemon and audio process IDs stayed unchanged. Deployment hashes and Shell
file backups are recorded in `build/linux/window-slots-ui-install.json`.
`build/linux/window-slots-ui-installed-accessibility.json` verifies the actual
installed page, eight unavailable rows and the accurate next-login notice through
AT-SPI. The running Shell still needs logout/login to expose the new API. After a
system package incorporates these UI changes, remove the user launcher, desktop
entry and UI overlay so subsequent system updates own those files again.
