import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {automation, prepareEnvironment} from './automation.js';

export const METRICS = {};
export const {init, run} = automation(test, METRICS);
const assert = (value, message) => { if (!value) throw new Error(message); };
async function until(predicate, message) {
    for (let n = 0; n < 100; n++) { if (predicate()) return; await Scripting.sleep(50); }
    throw new Error(message);
}
async function test() {
    await prepareEnvironment();
    if (Main.actionMode === Shell.ActionMode.NONE) { Main.overview.show(); await Scripting.sleep(200); }
    Main.overview.hide(); await Scripting.sleep(300);
    const root = GLib.getenv('PERSONALTOOLS_TEST_ROOT'), output = GLib.getenv('PERSONALTOOLS_TEST_OUTPUT');
    const record = `${output}/window-view.json`;
    const launcher = new Gio.SubprocessLauncher({flags: Gio.SubprocessFlags.NONE});
    launcher.setenv('GDK_BACKEND', 'wayland', true);
    const clients = [];
    const spawn = args => { const child = launcher.spawnv(['gjs', '-m', ...args]); clients.push(child); return child; };
    const win = child => global.get_window_actors().map(actor => actor.meta_window).find(w => w.get_pid() === Number(child.get_identifier()));
    const read = () => { try { return JSON.parse(new TextDecoder().decode(GLib.file_get_contents(record)[1])); } catch { return {}; } };
    try {
        spawn([`${root}/Tests/Linux/UI/window-shortcuts-client.js`, record]);
        await until(() => read().state?.ready, 'GTK page did not connect to the real Shell');
        assert(read().labels.filter(value => value === '미등록').length === 8, 'Empty list did not show eight empty slots');
        const first = spawn([`${root}/Tests/Linux/Shell/window-shortcuts-client.js`, '문서 검토 — 첫 번째 창']);
        const second = spawn([`${root}/Tests/Linux/Shell/window-shortcuts-client.js`, '문서 검토 — 두 번째 창']);
        await until(() => win(first) && win(second), 'Two test windows did not open');
        const keyboard = Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);
        await Scripting.sleep(200);
        const chord = async codes => {
            for (const code of codes) { keyboard.notify_key(GLib.get_monotonic_time(), code, Clutter.KeyState.PRESSED); await Scripting.sleep(25); }
            for (const code of [...codes].reverse()) { keyboard.notify_key(GLib.get_monotonic_time(), code, Clutter.KeyState.RELEASED); await Scripting.sleep(25); }
        };
        const focus = async child => { Main.activateWindow(win(child), global.display.get_current_time_roundtrip()); await until(() => global.display.focus_window === win(child), 'Target did not focus'); };
        await focus(first); await chord([125, 42, 2]);
        await until(() => read().labels?.includes('문서 검토 — 첫 번째 창') && read().labels.includes('1 / 8 등록'), 'Registration did not update GTK labels');
        await chord([60]);
        await until(() => read().labels?.includes('문서 검토 — 첫 번째 창 · 수정됨'), 'Title signal did not update GTK labels');
        await focus(second); await chord([125, 42, 16]);
        await until(() => read().labels?.includes('문서 검토 — 두 번째 창') && read().labels.includes('2 / 8 등록'), 'Q slot did not update GTK labels');
        print('PT_WINDOW_VIEW_REGISTER_RENAME_PASS physical registration and title change reached GTK labels over real D-Bus');

        const main = spawn([`${root}/Sources/Linux/App/app.js`, '--page', 'windows', '--width', '1488', '--height', '1056']);
        await until(() => win(main), 'Main app window did not open');
        win(main).make_fullscreen(); await focus(main); await Scripting.sleep(1200);
        const stream = Gio.File.new_for_path(`${output}/main-window-slots.png`).replace(null, false, Gio.FileCreateFlags.REPLACE_DESTINATION, null);
        await new Shell.Screenshot().screenshot(false, stream); stream.close(null);
        print('PT_WINDOW_VIEW_MAIN_APP_CAPTURE_PASS main application opened on windows route with real Shell registrations');

        await focus(second); await chord([125, 42, 2]);
        await until(() => read().state?.slots?.find(slot => slot.key === '1')?.title === '문서 검토 — 두 번째 창', 'Replacement not reflected');
        assert(!read().labels.includes('문서 검토 — 첫 번째 창 · 수정됨'), 'Replaced title remained in GTK');
        await chord([56, 62]);
        await until(() => read().labels?.includes('0 / 8 등록') && read().labels.filter(value => value === '미등록').length === 8, 'Closing window did not clear both displayed slots');
        print('PT_WINDOW_VIEW_REPLACE_CLOSE_PASS replacing and closing windows update all affected GTK rows');
        await Main.extensionManager._callExtensionDisable('personal-tools@griplabs.io');
        await until(() => read().state?.state === 'unavailable' && read().labels?.includes('확인할 수 없음'), 'Disconnect retained stale registrations');
        assert(!read().labels.includes('미등록'), 'Disconnected state masqueraded as empty slots');
        await Main.extensionManager._callExtensionEnable('personal-tools@griplabs.io');
        await until(() => read().state?.ready && read().labels?.includes('0 / 8 등록'), 'GTK page did not reconnect');
        print('PT_WINDOW_VIEW_RECONNECT_PASS disconnect clears stale titles; re-enable restores the live view');
    } finally { for (const child of clients) { try { child.force_exit(); } catch { /* Closed. */ } } }
}
