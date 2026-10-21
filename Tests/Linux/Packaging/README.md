# Package verification

Run after building and installing the same `.deb`:

```sh
python3 Tests/Linux/Packaging/verify.py
```

The default input is the newest `build/linux/packages/personal-tools_*.deb`; use
`--deb` to select another build. The report is `build/linux/packaging-qa.json`.
The command exits nonzero when a required check fails. It reads the installed
files, extracts the archive only into a private temporary directory, and never
installs, removes, starts, stops, enables or disables anything. The Personal Tools
daemon and audio services must be inactive before and after this acceptance run.

Checks cover archive paths/owners/modes, installed byte identity, bundled font
hashes/licenses, desktop identity, D-Bus/systemd references, GNOME metadata,
native ELF dependencies/RUNPATH, and known private filenames/credential formats.
Credential findings report a path and line number without the matching value.
This is a bounded check for known formats, not proof that every possible secret
is absent. Changes made to source after the package build are listed separately.

Actual upgrade, removal, retained user data, and login behavior require the
separate lifecycle acceptance procedure. This script does not claim to test them.

Validate graphical-session startup links and the migration from the old
`default.target` link without changing the live session:

```sh
python3 Tests/Linux/Packaging/session-startup.py
```

This runs the setup command through offline adapters, using the real systemctl
install-link implementation inside a temporary root. It checks removal of the
legacy link, repeated enablement, the unloaded-extension message, and a systemd
start transaction with normal default dependencies. No live target, service or
extension is started, stopped, enabled or disabled. The three live service states
and PIDs must stay unchanged. The report is `build/linux/session-startup-qa.json`;
it does not substitute for a real session logout/login acceptance test.

`lifecycle.py` provides the explicit upgrade/remove/reinstall procedure. The
default only prints a plan. In an interactive terminal, after reviewing that plan:

```sh
python3 Tests/Linux/Packaging/lifecycle.py --deb build/linux/packages/personal-tools_0.7.0~ubuntu4_amd64.deb --execute
```

It requires an older installed version, guards inactive daemon/audio units and
an unchanged independent vLLM PID/state, takes a fresh preservation inventory,
upgrades, removes, reinstalls, and compares preservation after each operation.
Removal/reinstall failures trigger a recovery reinstall in `finally`. Sudo
authentication uses the inherited terminal; no password is read or stored by the
script. The complete step/guard report is `build/linux/package-lifecycle.json`.

For a real package removal/reinstall, stop the services using the documented
shutdown procedure first. Then use the read-only preservation inventory:

```sh
python3 Tests/Linux/Packaging/preserve.py snapshot
# Perform the separately authorized package removal/reinstall here.
python3 Tests/Linux/Packaging/preserve.py compare
python3 Tests/Linux/Packaging/verify.py
```

Take the snapshot immediately before the package operation, with other tasks
finished changing settings. Config, clipboard, imported filters, HDR/audio
restoration records and the personal vLLM service file are compared. The script
also discovers a configured custom clipboard archive. NPU/vLLM runtime/model
trees are checked by path, size, mode, owner, inode and modification time, without
rereading their large payloads. This metadata check must not be described as a
model checksum validation. Volatile caches and logs are excluded. Inventory
reports contain no file contents and are mode 0600.

Verify the installed self-contained apphost after reinstall:

```sh
python3 Tests/Linux/Packaging/service-smoke.py
```

This starts the installed executable directly on a private D-Bus and private XDG
directories, reads its snapshot, saves/reads two theme values, restarts it to
verify persistence, and requests clean lifecycle exits. The real daemon/audio
units must already be inactive. Their states and the independent vLLM unit's
state/PID are compared before and after. No actual audio, vLLM, systemd or desktop
session is started or changed. The JSON report identifies the executable hash.
