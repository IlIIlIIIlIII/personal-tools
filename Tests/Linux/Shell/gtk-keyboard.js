import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Clutter from 'gi://Clutter';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {automation, prepareEnvironment} from './automation.js';

export const METRICS = {};
export const {init, run} = automation(keyboard, METRICS);
function assert(condition, message) { if (!condition) throw new Error(message); }
async function until(predicate, message) {
    for (let n = 0; n < 80; n++) { if (predicate()) return;await Scripting.sleep(50); }
    throw new Error(message);
}
async function keyboard() {
    await prepareEnvironment();Main.overview.hide();await Scripting.sleep(400);
    const root = GLib.getenv('PERSONALTOOLS_TEST_ROOT');
    const output = `${GLib.getenv('PERSONALTOOLS_TEST_OUTPUT')}/gtk-keyboard-state.json`;
    const launcher = new Gio.SubprocessLauncher({flags: Gio.SubprocessFlags.NONE});
    launcher.setenv('GDK_BACKEND', 'wayland', true);
    const client = launcher.spawnv(['gjs', '-m', `${root}/Tests/Linux/UI/keyboard-client.js`, output]);
    const read = () => { try { return JSON.parse(new TextDecoder().decode(GLib.file_get_contents(output)[1])); } catch { return {}; } };
    try {
        let window;
        await until(() => (window = global.get_window_actors().map(actor => actor.meta_window).find(win => win.get_pid() === Number(client.get_identifier()))), 'GTK keyboard client did not open');
        if (Main.actionMode === Shell.ActionMode.NONE) { Main.overview.show();await Scripting.sleep(300);Main.overview.hide();await Scripting.sleep(300); }
        Main.activateWindow(window);await Scripting.sleep(200);
        const device = Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);
        await Scripting.sleep(200);
        const chord = async codes => {
            for (const code of codes) { device.notify_key(GLib.get_monotonic_time(), code, Clutter.KeyState.PRESSED);await Scripting.sleep(25); }
            for (const code of [...codes].reverse()) { device.notify_key(GLib.get_monotonic_time(), code, Clutter.KeyState.RELEASED);await Scripting.sleep(25); }
            await Scripting.sleep(120);
        };
        let activations = read().activations ?? 0;
        for (const [code, focused] of [[59, 'search'], [60, 'editor'], [61, 'spin'], [62, 'card'], [63, 'text']]) {
            await chord([code]);await until(() => read().focused === focused, `Could not focus ${focused}`);
            await chord([29, 33]); // physical Ctrl+F, no direct signal emission
            await until(() => read().activations === activations + 1 && read().focused === 'search', `Ctrl+F was consumed by ${focused}: ${JSON.stringify(read())}`);
            assert(read().selection[0] && read().selection[1] === 0 && read().selection[2] === 3, 'Ctrl+F did not select the existing Korean query');
            activations++;print(`PT_GTK_SEARCH_FOCUS_PASS ${focused}`);
        }
        await chord([58]); // Caps Lock: physical F now produces uppercase F.
        await chord([60]);await chord([29, 33]);
        await until(() => read().activations === activations + 1 && read().focused === 'search', 'Caps Lock prevented Ctrl+F');activations++;
        await chord([58]);print('PT_GTK_SEARCH_CAPS_LOCK_PASS');
        await chord([60]);await chord([33]);
        assert(read().activations === activations && read().focused === 'editor', 'Plain F incorrectly intercepted as search');
        print('PT_GTK_SEARCH_PLAIN_TEXT_PASS');
    } finally { client.force_exit(); }
}
