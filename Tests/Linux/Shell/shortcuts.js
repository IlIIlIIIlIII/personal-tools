import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import Clutter from 'gi://Clutter';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {automation, prepareEnvironment} from './automation.js';

export const METRICS = {};
export const {init, run} = automation(shortcuts, METRICS);
function assert(condition, message) { if (!condition) throw new Error(message); }
const daemonCall = (method, args = null) => new Promise((resolve, reject) => Gio.DBus.session.call(
    'io.griplabs.PersonalTools', '/io/griplabs/PersonalTools', 'io.griplabs.PersonalTools1', method, args, null,
    Gio.DBusCallFlags.NONE, 5000, null, (connection, result) => {
        try { resolve(JSON.parse(connection.call_finish(result).deepUnpack()[0])); } catch (error) { reject(error); }
    }));
const update = settings => daemonCall('Execute', new GLib.Variant('(sss)', ['settings', 'update', JSON.stringify(settings)]));
async function until(predicate, message) {
    for (let i = 0; i < 100; i++) { if (await predicate()) return; await Scripting.sleep(50); }
    throw new Error(message);
}
async function shortcuts() {
    await prepareEnvironment(); Main.overview.hide(); await Scripting.sleep(300);
    const extension = Main.extensionManager.lookup('personal-tools@griplabs.io')?.stateObj;
    assert(extension?._panel, 'Extension was not initialized');
    const root = GLib.getenv('PERSONALTOOLS_TEST_ROOT'), privateRoot = GLib.getenv('PERSONALTOOLS_TEST_PRIVATE_ROOT');
    const launcher = new Gio.SubprocessLauncher({flags: Gio.SubprocessFlags.NONE});
    for (const [key, dir] of Object.entries({XDG_CONFIG_HOME: 'config', XDG_DATA_HOME: 'data', XDG_STATE_HOME: 'state', XDG_CACHE_HOME: 'cache'}))
        launcher.setenv(key, `${privateRoot}/daemon/${dir}`, true);
    const daemon = launcher.spawnv([`${root}/build/linux/toolchain/dotnet/dotnet`, `${root}/Sources/Linux/Service/bin/Debug/net10.0/personal-tools-service.dll`]);
    let client;
    let conflictingAction = Meta.KeyBindingAction.NONE;
    try {
        await until(() => extension._client.snapshot?.version === 1, 'Private daemon did not connect');
        assert(extension._settings.get_strv('clipboard-shortcut')[0] === '<Control><Shift>v', 'Clipboard default is not Ctrl+Shift+V');
        const temporary = await update({clipboardShortcut: '<Control><Alt>v'});
        assert(temporary.ok, `First shortcut update failed: ${JSON.stringify(temporary)}`);
        const changed = await update({clipboardShortcut: '<Shift><Control>V'});
        assert(changed.ok, `Ctrl+Shift+V change failed: ${JSON.stringify(changed)}`);
        assert(extension._settings.get_strv('clipboard-shortcut')[0] === '<Control><Shift>v', 'Shell did not register normalized Ctrl+Shift+V');
        assert((await daemonCall('GetSnapshot')).settings.clipboardShortcut === '<Control><Shift>v', 'Service snapshot did not persist Ctrl+Shift+V');
        print('PT_SHORTCUT_CONFIGURE_PASS private real daemon and GNOME Shell complete update without recursive command');
        const clientLauncher = new Gio.SubprocessLauncher({flags: Gio.SubprocessFlags.NONE});
        clientLauncher.setenv('GDK_BACKEND', 'wayland', true);
        client = clientLauncher.spawnv(['gjs', '-m', `${root}/Tests/Linux/Shell/wayland-client.js`, 'Target']);
        let window;
        await until(() => (window = global.get_window_actors().map(actor => actor.meta_window).find(win => win.get_pid() === Number(client.get_identifier()))), 'No shortcut target window');
        Main.activateWindow(window); await Scripting.sleep(500);
        assert(global.display.focus_window === window, 'Shortcut target did not receive focus');
        // The automation import can miss startup-complete (see automation.js),
        // leaving this private compositor in NONE. A real overview cycle gives
        // it the same NORMAL action mode as an interactive logged-in session.
        if (Main.actionMode === Shell.ActionMode.NONE) {
            Main.overview.show(); await Scripting.sleep(400);
            Main.overview.hide(); await Scripting.sleep(400);
        }
        await until(() => Main.actionMode === Shell.ActionMode.NORMAL, 'Private compositor did not enter the normal desktop action mode');
        Main.activateWindow(window); await Scripting.sleep(200);
        const keyboard = Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);
        // The compositor adds the virtual device asynchronously.
        await Scripting.sleep(200);
        // Linux input event codes preserve the physically held Shift modifier;
        // notify_keyval(lowercase v) is permitted to synthesize keymap modifiers.
        const chord = async keys => {
            for(const key of keys) { keyboard.notify_key(GLib.get_monotonic_time(), key, Clutter.KeyState.PRESSED); await Scripting.sleep(25); }
            for(const key of [...keys].reverse()) { keyboard.notify_key(GLib.get_monotonic_time(), key, Clutter.KeyState.RELEASED); await Scripting.sleep(25); }
        };
        const opens = async () => {
            await chord([29, 42, 47]); // KEY_LEFTCTRL, KEY_LEFTSHIFT, KEY_V
            await until(() => extension._panel.opened, `Ctrl+Shift+V did not open the panel; current=${extension._settings.get_strv('clipboard-shortcut')}; mode=${Main.actionMode}; focused=${global.display.focus_window?.title}; error=${extension._shortcutError}`);
            await chord([1]); await until(() => !extension._panel.opened, 'Escape did not close the panel');
        };
        await opens(); print('PT_SHORTCUT_ACTUAL_KEY_PASS Control Shift V opens; Escape closes');
        conflictingAction = global.display.grab_accelerator('<Control><Alt>9', Meta.KeyBindingFlags.NONE);
        assert(conflictingAction !== Meta.KeyBindingAction.NONE, 'Could not reserve the test conflict accelerator');
        const rejected = await update({clipboardShortcut: '<Control><Alt>9'});
        assert(!rejected.ok && rejected.error.message.includes('충돌'), `External conflict was not rejected: ${JSON.stringify(rejected)}`);
        assert(extension._settings.get_strv('clipboard-shortcut')[0] === '<Control><Shift>v', 'External conflict changed GSettings');
        assert((await daemonCall('GetSnapshot')).settings.clipboardShortcut === '<Control><Shift>v', 'External conflict changed service settings');
        await opens(); print('PT_SHORTCUT_CONFLICT_ROLLBACK_PASS conflicting global accelerator rejected; prior physical shortcut still opens');
        const duplicate = await update({clipboardShortcut: '<Alt><Super>M'});
        assert(!duplicate.ok && extension._settings.get_strv('clipboard-shortcut')[0] === '<Control><Shift>v', 'Equivalent microphone chord was accepted');
        await opens(); print('PT_SHORTCUT_MICROPHONE_DUPLICATE_PASS modifier-order/case alias rejected; prior physical shortcut still opens');
    } finally {
        if (conflictingAction !== Meta.KeyBindingAction.NONE) global.display.ungrab_accelerator(conflictingAction);
        extension._panel?.close(); client?.force_exit(); daemon.force_exit();
    }
}
