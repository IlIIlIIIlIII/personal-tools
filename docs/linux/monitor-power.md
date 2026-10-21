# MO32U24 input-based power control

The main Display page (both tabs) and GNOME popup expose the same
`display.monitorPower` snapshot and `display / monitor-power` command.
The toggle controls the separately installed `~/.local/bin/mo32-power` helper:

- Mouse/keyboard input powers on the two MO32U24 monitors.
- 60 seconds without physical input powers them off with DDC.
- GNOME idle inhibitors are ignored and the desktop layout stays unchanged.
- Turning the toggle off disables the persistent user unit and powers both on.
- Personal Tools restarts do not stop or recreate the guard.

This integration requires the DDC guard and input timestamp service configured
on the desktop. The ordinary app package does not grant input-device privileges
or install a root service automatically. A missing helper disables the toggle;
a stopped enabled service or status older than 15 seconds displays an error.
State is polled with the normal daemon refresh, so changes from the CLI are
reflected in both UIs.

Validation: GTK model checks, isolated GNOME popup command/state checks, captures
for the main Display pages and popup. The service suite passes 55 checks.
The installed live D-Bus disable/enable round trip passed; the policy was restored
to enabled and running with no error.
GNOME caches imported extension modules for the current login; copying updated
popup source requires a fresh login to load that module.
