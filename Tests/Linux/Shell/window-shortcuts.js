import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {automation, prepareEnvironment} from './automation.js';

export const METRICS = {};
export const {init, run} = automation(test, METRICS);
const assert = (value, message) => { if (!value) throw new Error(message); };
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const keys = ['1', '2', '3', '4', 'q', 'w', 'e', 'r'];
const codes = [2, 3, 4, 5, 16, 17, 18, 19];
async function until(predicate, message) {
    for (let i = 0; i < 100; i++) {
        if (predicate()) return;
        await Scripting.sleep(50);
    }
    throw new Error(message);
}

async function test() {
    await prepareEnvironment();
    if (Main.actionMode === Shell.ActionMode.NONE) { Main.overview.show(); await Scripting.sleep(200); }
    Main.overview.hide(); await Scripting.sleep(300);
    const extension = Main.extensionManager.lookup('personal-tools@griplabs.io')?.stateObj;
    assert(extension?._windowShortcuts, 'Window shortcuts were not loaded');
    await until(() => extension._windowShortcuts.ready || extension._windowShortcuts.error, 'No startup result');
    assert(extension._windowShortcuts.ready, extension._windowShortcuts.error);
    const Controller = extension._windowShortcuts.constructor;
    const settings = extension._settings;
    const start = async () => {
        extension._windowShortcuts = new Controller(settings, extension._windowSlots, () => extension._panel.close());
        await until(() => extension._windowShortcuts.ready || extension._windowShortcuts.error, 'No binding result');
        assert(extension._windowShortcuts.ready, extension._windowShortcuts.error);
    };
    extension._windowShortcuts.destroy();
    const shell = new Gio.Settings({schema_id: 'org.gnome.shell.keybindings'});
    const dock = new Gio.Settings({schema_id: 'org.gnome.shell.extensions.dash-to-dock'});
    const wm = new Gio.Settings({schema_id: 'org.gnome.desktop.wm.keybindings'});
    const backups = [];
    const set = (object, key, value) => {
        backups.push({object, key, user: object.get_user_value(key)});
        object.set_strv(key, value);
    };
    const dockBindings = [];
    const clients = [];
    let dockInvocations = 0;
    let blocker = Meta.KeyBindingAction.NONE;
    try {
        // Reproduce Ubuntu Dock's real GSettings-backed bindings, alongside
        // Shell's built-in Super+number bindings, in this private compositor.
        for (let index = 1; index <= 4; index++) {
            set(shell, `switch-to-application-${index}`, [`<Super>${index}`, `<Alt>${index}`]);
            for (const prefix of ['app-hotkey', 'app-shift-hotkey']) {
                const name = `${prefix}-${index}`;
                set(dock, name, [`${prefix === 'app-hotkey' ? '<Super>' : '<Shift><Super>'}${index}`]);
                Main.wm.addKeybinding(name, dock, Meta.KeyBindingFlags.IGNORE_AUTOREPEAT,
                    Shell.ActionMode.NORMAL | Shell.ActionMode.OVERVIEW, () => dockInvocations++);
                dockBindings.push(name);
            }
        }
        set(dock, 'shortcut', ['<Super>q']);
        Main.wm.addKeybinding('shortcut', dock, Meta.KeyBindingFlags.IGNORE_AUTOREPEAT,
            Shell.ActionMode.NORMAL | Shell.ActionMode.OVERVIEW, () => dockInvocations++);
        dockBindings.push('shortcut');
        set(wm, 'raise', ['<Super>w', '<Alt>F12']);
        const dockFive = dock.get_strv('app-hotkey-5');
        const dockControl = dock.get_strv('app-ctrl-hotkey-1');
        await Scripting.sleep(150);
        await start();
        assert(same(shell.get_strv('switch-to-application-1'), ['<Alt>1']), 'Unrelated alternate chord was removed');
        assert(same(wm.get_strv('raise'), ['<Alt>F12']), 'WM alternate chord was removed');
        assert(same(dock.get_strv('app-hotkey-5'), dockFive) && same(dock.get_strv('app-ctrl-hotkey-1'), dockControl), 'Other Dock slots changed');
        assert(dock.get_strv('shortcut').length === 0, 'Super+Q Dock conflict was not yielded');
        print('PT_WINDOW_CONFLICT_ACQUIRE_PASS Shell + Dock + WM conflicting chords yielded; alternate, Ctrl and 5-0 chords preserved');

        const launcher = new Gio.SubprocessLauncher({flags: Gio.SubprocessFlags.NONE});
        launcher.setenv('GDK_BACKEND', 'wayland', true);
        const script = `${GLib.getenv('PERSONALTOOLS_TEST_ROOT')}/Tests/Linux/Shell/window-shortcuts-client.js`;
        const client = launcher.spawnv(['gjs', '-m', script]); clients.push(client);
        const clientWindows = () => global.get_window_actors().map(actor => actor.meta_window)
            .filter(window => window.get_pid() === Number(client.get_identifier()));
        await until(() => clientWindows().length === 9, 'Nine real Wayland windows were not created');
        const windows = clientWindows().sort((a, b) => a.title.localeCompare(b.title));
        assert(new Set(windows.map(window => window.get_gtk_application_id())).size === 1, 'Targets are not the same application');
        const control = windows[8];
        const focus = async window => {
            Main.activateWindow(window, global.display.get_current_time_roundtrip());
            await until(() => global.display.focus_window === window, `Could not focus ${window.title}`);
        };
        const keyboard = Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);
        await Scripting.sleep(200);
        const chord = async keys => {
            for (const key of keys) { keyboard.notify_key(GLib.get_monotonic_time(), key, Clutter.KeyState.PRESSED); await Scripting.sleep(25); }
            for (const key of [...keys].reverse()) { keyboard.notify_key(GLib.get_monotonic_time(), key, Clutter.KeyState.RELEASED); await Scripting.sleep(25); }
            await Scripting.sleep(100);
        };
        for (let index = 0; index < keys.length; index++) {
            await focus(windows[index]);
            await chord([125, 42, codes[index]]);
            assert(extension._windowSlots[index] === windows[index], `Super+Shift+${keys[index]} did not register the exact window`);
            assert(global.display.focus_window === windows[index], 'Registration stole focus');
            if (index === 0) {
                const stream = Gio.File.new_for_path(`${GLib.getenv('PERSONALTOOLS_TEST_OUTPUT')}/registration.png`)
                    .replace(null, false, Gio.FileCreateFlags.REPLACE_DESTINATION, null);
                await new Shell.Screenshot().screenshot(false, stream);
                stream.close(null);
            }
        }
        for (let index = 0; index < keys.length; index++) {
            await focus(control);
            await chord([125, codes[index]]);
            assert(global.display.focus_window === windows[index], `Super+${keys[index]} selected the wrong window`);
        }
        assert(dockInvocations === 0, 'Dock handled a window shortcut');
        assert(extension._windowShortcuts.status.registered === 8, 'Not all eight slots registered');
        print('PT_WINDOW_ALL_16_KEYS_PASS eight distinct windows of one app registered and focused with physical key events');

        await focus(control); await chord([125, 42, 2]);
        await focus(windows[0]); await chord([125, 2]);
        assert(global.display.focus_window === control, 'Re-registration did not replace slot 1');
        assert(extension._windowSlots[1] === windows[1], 'Replacing slot 1 changed another slot');
        print('PT_WINDOW_REPLACE_PASS same slot replaced without changing other slots');

        windows[1].minimize(); await focus(control); await chord([125, 3]);
        assert(!windows[1].minimized && global.display.focus_window === windows[1], 'Minimized window was not restored');
        const workspace = global.workspace_manager.get_workspace_by_index(1) ?? global.workspace_manager.append_new_workspace(false, global.get_current_time());
        windows[2].change_workspace(workspace);
        await focus(control); await chord([125, 4]);
        await until(() => global.display.focus_window === windows[2] && global.workspace_manager.get_active_workspace() === workspace, 'Other workspace was not activated');
        Main.overview.show(); await Scripting.sleep(300); await chord([125, 3]);
        await until(() => !Main.overview.visible && global.display.focus_window === windows[1], 'Overview shortcut did not return to the exact window');
        print('PT_WINDOW_MINIMIZED_WORKSPACE_OVERVIEW_PASS restores minimized window; changes workspace; exits overview');

        // Caps Lock must not change slot identity; use hardware keycodes for
        // Shift+digits so shifted symbols cannot be hidden by keyval synthesis.
        await chord([58]); await focus(windows[4]); await chord([125, 42, 16]);
        await focus(control); await chord([125, 16]);
        assert(global.display.focus_window === windows[4], 'Caps Lock changed Q slot');
        await chord([58]);
        print('PT_WINDOW_CAPS_LOCK_PASS Q slot works with Caps Lock');

        let rejected = false;
        try { extension._configureShortcuts({clipboardShortcut: '<Super>q', microphoneShortcut: '<Super><Alt>m'}); }
        catch { rejected = true; }
        assert(rejected && settings.get_strv('clipboard-shortcut')[0] === '<Control><Shift>v', 'Reserved key changed clipboard settings');
        const release = extension._holdPasteBindings();
        await focus(control); await chord([125, 16]);
        assert(global.display.focus_window === windows[4], 'Clipboard paste temporarily disabled window shortcuts');
        release();
        print('PT_WINDOW_CLIPBOARD_COEXISTENCE_PASS reserved keys rejected; window bindings survive paste binding hold');

        await focus(windows[7]); await chord([56, 62]); // Alt+F4 closes the registered window.
        await until(() => extension._windowSlots[7] === null, 'Closed window slot was not cleared');
        await focus(control); await chord([125, 19]);
        assert(global.display.focus_window === control, 'Empty slot stole focus');
        const replacement = launcher.spawnv(['gjs', '-m', script, 'Window slot test 7']); clients.push(replacement);
        await until(() => global.get_window_actors().some(actor => actor.meta_window.get_pid() === Number(replacement.get_identifier())), 'Replacement window did not appear');
        await focus(control); await chord([125, 19]);
        assert(global.display.focus_window === control && extension._windowSlots[7] === null, 'Same-title new window inherited a closed slot');
        print('PT_WINDOW_CLOSE_EMPTY_IDENTITY_PASS close clears slot; empty slot preserves focus; same-title new window is not registered');

        launcher.setenv('GDK_BACKEND', 'x11', true);
        const xclient = launcher.spawnv(['gjs', '-m', script, 'XWayland window']); clients.push(xclient);
        let xwindow;
        await until(() => (xwindow = global.get_window_actors().map(actor => actor.meta_window)
            .find(window => window.get_pid() === Number(xclient.get_identifier()))), 'XWayland window did not appear');
        assert(xwindow.get_client_type() === Meta.WindowClientType.X11, 'XWayland target is not X11');
        await focus(xwindow); await chord([125, 42, 19]);
        await focus(control); await chord([125, 19]);
        assert(global.display.focus_window === xwindow, 'XWayland window did not receive focus');
        print(`PT_WINDOW_XWAYLAND_PASS registered and focused a real X11 window alongside Wayland windows; wmClass=${xwindow.get_wm_class()}`);

        // An unrecognized extension's grab must fail atomically and roll back
        // our known overrides. No partial set of window shortcuts may remain.
        extension._windowShortcuts.destroy();
        assert(same(shell.get_strv('switch-to-application-1'), ['<Super>1', '<Alt>1']), 'Shell shortcut restoration failed');
        assert(same(dock.get_strv('shortcut'), ['<Super>q']), 'Dock Super+Q restoration failed');
        assert(settings.get_string('window-shortcut-backup') === '[]', 'Restoration journal was not cleared');
        blocker = global.display.grab_accelerator('<Super>e', Meta.KeyBindingFlags.NONE);
        assert(blocker !== Meta.KeyBindingAction.NONE, 'Could not reserve an external conflict');
        extension._windowShortcuts = new Controller(settings, extension._windowSlots, () => {});
        await until(() => extension._windowShortcuts.error, 'External conflict was not reported');
        assert(!extension._windowShortcuts.ready && extension._windowShortcuts._bindings.length === 0, 'Conflict left partial bindings');
        assert(same(dock.get_strv('shortcut'), ['<Super>q']), 'Conflict did not restore Dock shortcut');
        extension._windowShortcuts.destroy();
        global.display.ungrab_accelerator(blocker); blocker = Meta.KeyBindingAction.NONE;
        await start();
        await focus(control); await chord([125, 16]);
        assert(global.display.focus_window === windows[4], 'Disable/re-enable lost a live window slot');
        wm.set_strv('raise', ['<Alt>F11']);
        extension._windowShortcuts.destroy();
        assert(same(wm.get_strv('raise'), ['<Alt>F11']), 'Restoration overwrote a newer user setting');
        print('PT_WINDOW_LIFECYCLE_PASS overrides restored; external conflict rolls back; re-enable retains live slots; later user edits preserved');

        await start();
        const journal = settings.get_string('window-shortcut-backup');
        extension._windowShortcuts.destroy();
        // Simulate only the persisted state left by an interrupted compositor.
        settings.set_string('window-shortcut-backup', journal);
        for (const entry of JSON.parse(journal))
            new Gio.Settings({schema_id: entry.schema}).set_strv(entry.key, entry.applied);
        await start();
        extension._windowShortcuts.destroy();
        assert(same(shell.get_strv('switch-to-application-1'), ['<Super>1', '<Alt>1']), 'Crash recovery lost original chords');
        assert(same(dock.get_strv('shortcut'), ['<Super>q']), 'Crash recovery lost Dock shortcut');
        print('PT_WINDOW_JOURNAL_RECOVERY_PASS next activation recovers interrupted overrides without losing originals');

        await start();
        await Main.extensionManager._callExtensionDisable('personal-tools@griplabs.io');
        assert(!extension._windowShortcuts, 'Whole extension did not disable');
        windows[0].delete(global.display.get_current_time_roundtrip());
        windows[5].delete(global.display.get_current_time_roundtrip());
        await until(() => !clientWindows().includes(windows[5]), 'Window did not close while disabled');
        await Main.extensionManager._callExtensionEnable('personal-tools@griplabs.io');
        await until(() => extension._windowShortcuts?.ready || extension._windowShortcuts?.error, 'Whole extension did not enable');
        assert(extension._windowShortcuts.ready, extension._windowShortcuts.error);
        assert(extension._windowSlots[5] === null && extension._windowSlots[0] === control, 'Re-enable retained a closed window or cleared a replacement slot');
        await focus(control); await chord([125, 16]);
        assert(global.display.focus_window === windows[4], 'Whole extension cycle lost a live window');
        print('PT_WINDOW_EXTENSION_CYCLE_PASS full disable/enable keeps living slots and drops windows closed while disabled');
    } finally {
        extension._windowShortcuts?.destroy(); extension._windowShortcuts = null;
        if (blocker !== Meta.KeyBindingAction.NONE) global.display.ungrab_accelerator(blocker);
        for (const name of dockBindings) Main.wm.removeKeybinding(name);
        for (const {object, key, user} of backups.reverse()) {
            if (user === null) object.reset(key); else object.set_value(key, user);
        }
        for (const client of clients) { try { client.force_exit(); } catch { /* Already closed. */ } }
    }
}
