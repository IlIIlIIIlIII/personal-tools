// Real shortcut -> arrow -> Enter paths, with no preliminary Down key or click.
// Every subprocess and virtual input device runs on run-isolated.py's private
// session bus, data directory, and compositor, never the user's desktop.
import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {automation, prepareEnvironment} from './automation.js';

export const METRICS = {};
export const {init, run} = automation(browse, METRICS);
const assert = (condition, message) => { if (!condition) throw new Error(message); };
const encode = text => GLib.base64_encode(new TextEncoder().encode(text));
const call = (role, method, args = null) => new Promise((resolve, reject) => Gio.DBus.session.call(
    `io.griplabs.PersonalTools.Preview.${role}`, '/io/griplabs/PersonalTools/Test',
    'io.griplabs.PersonalTools.Test1', method, args, null, Gio.DBusCallFlags.NONE, 5000, null,
    (connection, result) => { try { resolve(connection.call_finish(result).deepUnpack()); } catch (error) { reject(error); } }));
async function waitFor(predicate, message) {
    for (let n = 0; n < 160; n++) {
        if (await predicate()) return;
        await Scripting.sleep(50);
    }
    throw new Error(message);
}

async function browse() {
    await prepareEnvironment();
    if (Main.actionMode === Shell.ActionMode.NONE) {
        Main.overview.show();
        await Scripting.sleep(150);
    }
    Main.overview.hide();
    await Scripting.sleep(300);
    const extension = Main.extensionManager.lookup('personal-tools@griplabs.io')?.stateObj;
    assert(extension?._bridge, 'Native clipboard bridge is unavailable');
    const root = GLib.getenv('PERSONALTOOLS_TEST_ROOT');
    const privateRoot = GLib.getenv('PERSONALTOOLS_TEST_PRIVATE_ROOT');
    const panel = extension._panel;
    const children = [];
    const windows = {};
    const keyboard = Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);
    const pointer = Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.POINTER_DEVICE);
    const chord = keys => {
        const now = GLib.get_monotonic_time();
        keys.forEach(key => keyboard.notify_keyval(now, key, Clutter.KeyState.PRESSED));
        [...keys].reverse().forEach(key => keyboard.notify_keyval(now + 1, key, Clutter.KeyState.RELEASED));
    };
    const focus = async win => {
        win.raise();
        win.activate(global.display.get_current_time_roundtrip());
        await Scripting.sleep(200);
        const rect = win.get_frame_rect();
        pointer.notify_absolute_motion(GLib.get_monotonic_time(), rect.x + 100, rect.y + 100);
        await Scripting.sleep(60);
        pointer.notify_button(GLib.get_monotonic_time(), Clutter.BUTTON_PRIMARY, Clutter.ButtonState.PRESSED);
        await Scripting.sleep(60);
        pointer.notify_button(GLib.get_monotonic_time(), Clutter.BUTTON_PRIMARY, Clutter.ButtonState.RELEASED);
        await waitFor(() => global.display.focus_window === win, 'Real Wayland target did not gain focus');
    };
    const click = async actor => {
        const [x, y] = actor.get_transformed_position();
        const [width, height] = actor.get_transformed_size();
        pointer.notify_absolute_motion(GLib.get_monotonic_time(), x + width / 2, y + height / 2);
        await Scripting.sleep(60);
        pointer.notify_button(GLib.get_monotonic_time(), Clutter.BUTTON_PRIMARY, Clutter.ButtonState.PRESSED);
        await Scripting.sleep(60);
        pointer.notify_button(GLib.get_monotonic_time(), Clutter.BUTTON_PRIMARY, Clutter.ButtonState.RELEASED);
        await Scripting.sleep(100);
    };
    const firstText = 'alpha keyboard archive first';
    const newestText = 'bravo keyboard archive newest';
    const inSearch = () => panel.search.contains(global.stage.key_focus);
    const selectedId = () => panel.entries[panel._selected]?.id;
    const cardFocus = () => panel._cards.some(card => card.contains(global.stage.key_focus));
    const open = async () => {
        chord([Clutter.KEY_Control_L, Clutter.KEY_Shift_L, Clutter.KEY_v]);
        await waitFor(() => panel.opened && panel._cards.length === 2, 'Shortcut did not open two archived cards');
        // Let the asynchronous real-service query arrive before the first key.
        await Scripting.sleep(250);
    };
    const verifyPaste = async expected => {
        await waitFor(() => !panel.opened && !panel.actor.visible && !panel._grab, 'Enter left the clipboard panel open or modal');
        await waitFor(async () => global.display.focus_window === windows.Target && (await call('Target', 'Text'))[0] === expected,
            `Enter did not paste the selected text into the original Wayland target (${expected})`);
        await waitFor(() => !extension._pasteBindingHolds, 'Paste did not restore the global shortcut bindings');
    };
    let restoreExecute;
    try {
        const launcher = new Gio.SubprocessLauncher({flags: Gio.SubprocessFlags.NONE});
        for (const [name, directory] of Object.entries({XDG_CONFIG_HOME:'config', XDG_DATA_HOME:'data', XDG_STATE_HOME:'state', XDG_CACHE_HOME:'cache'}))
            launcher.setenv(name, `${privateRoot}/daemon/${directory}`, true);
        children.push(launcher.spawnv([`${root}/build/linux/toolchain/dotnet/dotnet`, `${root}/Sources/Linux/Service/bin/Debug/net10.0/personal-tools-service.dll`]));
        await waitFor(() => extension._client.snapshot?.version === 1, 'Private real service did not connect');
        assert(extension._client.snapshot.clipboard.entries.length === 0, 'Private archive was not empty');
        for (const role of ['Source', 'Target']) {
            const appLauncher = new Gio.SubprocessLauncher({flags: Gio.SubprocessFlags.NONE});
            appLauncher.setenv('GDK_BACKEND', 'wayland', true);
            const child = appLauncher.spawnv(['gjs', '-m', `${root}/Tests/Linux/Shell/wayland-client.js`, role,
                role === 'Target' && GLib.getenv('PERSONALTOOLS_TEST_TERMINAL') === '1' ? 'terminal' : 'editor']);
            children.push(child);
            await waitFor(() => {
                windows[role] = global.get_window_actors().map(actor => actor.meta_window).find(win => win.get_pid() === Number(child.get_identifier()));
                return windows[role];
            }, `No private ${role} window`);
            windows[role].move_resize_frame(true, role === 'Source' ? 40 : 760, 80, 600, 320);
        }
        await focus(windows.Source);
        for (const [index, text] of [firstText, newestText].entries()) {
            await call('Source', 'Publish', new GLib.Variant('(s)', [JSON.stringify({'text/plain':encode(text)})]));
            await waitFor(() => extension._client.snapshot.clipboard.entries.length === index + 1, 'Real clipboard publication was not archived');
        }
        await focus(windows.Target);
        await call('Target', 'Clear');
        await open();
        print(`PT_BROWSE_INITIAL ${JSON.stringify({selected:panel._selected, searchFocus:inSearch(), cardFocus:cardFocus()})}`);
        assert(panel._selected === 0, 'Opening clipboard did not select the first card');
        chord([Clutter.KEY_Right]);
        await waitFor(() => panel._selected === 1, 'Open -> Right did not move to the next card (no Down or click allowed)');
        chord([Clutter.KEY_Left]);
        await waitFor(() => panel._selected === 0, 'Open -> Right -> Left did not return to the first card');
        chord([Clutter.KEY_Return]);
        await verifyPaste(newestText);
        print('PT_BROWSE_EXACT_SHORTCUT_RIGHT_LEFT_ENTER_PASS real input, native service, original Wayland target');

        await call('Target', 'Clear');
        await open();
        chord([Clutter.KEY_Return]);
        await verifyPaste(newestText);
        print('PT_BROWSE_OPEN_ENTER_FIRST_CARD_PASS no Down or click');

        await call('Target', 'Clear');
        await open();
        chord([Clutter.KEY_Right]);
        await waitFor(() => panel._selected === 1, 'Right did not select the older card');
        const retainedId = selectedId();
        // Delay an actual service query to reproduce a background refresh arriving
        // after keyboard navigation. No response contents are fabricated.
        const originalExecute = extension._client.execute.bind(extension._client);
        let release, reached;
        const pendingResponse = new Promise(resolve => { release = resolve; });
        const queryReached = new Promise(resolve => { reached = resolve; });
        restoreExecute = () => { extension._client.execute = originalExecute; };
        extension._client.execute = async (...args) => {
            const response = await originalExecute(...args);
            if (args[0] === 'clipboard' && args[1] === 'query') { reached(); await pendingResponse; }
            return response;
        };
        const refresh = panel.query();
        await queryReached;
        release();
        await refresh;
        restoreExecute(); restoreExecute = null;
        assert(selectedId() === retainedId && cardFocus(), 'Async query discarded the selected card or its keyboard focus');
        chord([Clutter.KEY_Return]);
        await verifyPaste(firstText);
        print('PT_BROWSE_REFRESH_PRESERVES_CARD_ENTER_PASS selected identity and actual paste');

        await call('Target', 'Clear');
        await open();
        let releaseSearch, reachedSearch;
        const pendingSearch = new Promise(resolve => { releaseSearch = resolve; });
        const searchReached = new Promise(resolve => { reachedSearch = resolve; });
        restoreExecute = () => { extension._client.execute = originalExecute; };
        extension._client.execute = async (...args) => {
            const response = await originalExecute(...args);
            if (args[0] === 'clipboard' && args[1] === 'query') { reachedSearch(); await pendingSearch; }
            return response;
        };
        const delayedRefresh = panel.query();
        await searchReached;
        chord([Clutter.KEY_Control_L, Clutter.KEY_f]);
        await waitFor(inSearch, 'Ctrl+F did not enter search from a newly opened panel');
        releaseSearch();
        await delayedRefresh;
        restoreExecute(); restoreExecute = null;
        assert(inSearch(), 'Late browse query stole focus after Ctrl+F');
        for (const letter of 'bravo') { chord([letter.codePointAt(0)]); await Scripting.sleep(30); }
        await waitFor(() => panel.search.get_text() === 'bravo', 'Real typing did not reach the search field');
        chord([Clutter.KEY_Return]);
        await waitFor(() => panel.entries.length === 1 && panel.entries[0].preview === newestText, 'Search Enter did not filter the real archive');
        await waitFor(() => panel.opened && cardFocus() && panel._selected === 0, 'Search Enter did not focus the first result');
        assert((await call('Target', 'Text'))[0] === '', 'Search Enter pasted unexpectedly');
        print('PT_BROWSE_CTRL_F_SEARCH_ENTER_PASS real typing, late-query focus, search without paste');
        chord([Clutter.KEY_Right]);
        await waitFor(cardFocus, 'Arrow navigation lost filtered card focus');
        chord([Clutter.KEY_Return]);
        await verifyPaste(newestText);
        print('PT_BROWSE_FILTERED_CARD_ENTER_PASS original Wayland target retained after search');

        await call('Target', 'Clear');
        panel.search.set_text('');
        await open();
        chord([Clutter.KEY_Control_L, Clutter.KEY_f]);
        panel.search.set_text('keyboard');
        chord([Clutter.KEY_Return]);
        await waitFor(() => panel.entries.length === 2 && cardFocus() && panel._selected === 0,
            'Search Enter did not focus the first of multiple results');
        chord([Clutter.KEY_Right]);
        await waitFor(() => panel._selected === 1 && cardFocus(), 'Search Enter -> Right did not select the next result');
        chord([Clutter.KEY_Left]);
        await waitFor(() => panel._selected === 0 && cardFocus(), 'Search Enter -> Left did not select the previous result');
        assert((await call('Target', 'Text'))[0] === '', 'Search submission pasted instead of focusing results');
        chord([Clutter.KEY_Control_L, Clutter.KEY_f]);
        panel.search.set_text('no-matching-clipboard-fixture');
        chord([Clutter.KEY_Return]);
        await waitFor(() => panel.entries.length === 0 && panel.empty.visible, 'Empty search did not finish');
        assert(inSearch(), 'Empty search removed focus from the search field');
        chord([Clutter.KEY_Escape]);
        await waitFor(() => !panel.opened, 'Empty search panel did not close');
        print('PT_BROWSE_SEARCH_ENTER_ARROWS_PASS first-result focus, Left/Right, no implicit paste, empty-search focus');

        await call('Target', 'Clear');
        panel.search.set_text('');
        await open();
        chord([Clutter.KEY_Control_L, Clutter.KEY_f]);
        await waitFor(inSearch, 'Ctrl+F did not focus search before the single-click test');
        const clickedIndex = panel.entries.findIndex(entry => entry.preview === firstText);
        assert(clickedIndex >= 0, 'Older clipboard entry was missing before the single-click test');
        await click(panel._cards[clickedIndex]);
        await verifyPaste(firstText);
        print('PT_BROWSE_SEARCH_SINGLE_CLICK_PASTE_PASS actual pointer click pastes the card without Enter');

        await call('Target', 'Clear');
        panel.search.set_text('');
        await open();
        // Inject a backend failure while cards exist: those hidden cached cards
        // must not remain actionable through a retained selection index.
        panel.unavailable('회귀 검사용 서비스 연결 오류');
        panel.actor.grab_key_focus();
        assert(panel.empty.visible && !panel.scroll.visible && panel.entries.length > 0,
            'Error-state guard test did not retain hidden cached cards');
        chord([Clutter.KEY_Return]);
        await Scripting.sleep(350);
        assert(panel.opened && panel.actor.visible && panel._grab, 'Enter activated a cached card hidden by an error');
        assert((await call('Target', 'Text'))[0] === '', 'Enter pasted stale clipboard data while the error screen was showing');
        chord([Clutter.KEY_Escape]);
        await waitFor(() => !panel.opened && !panel.actor.visible && !panel._grab, 'Escape did not dismiss the error screen');
        print('PT_BROWSE_ERROR_ENTER_GUARD_PASS hidden cached cards cannot paste; Escape releases the grab');
        if (GLib.getenv('PERSONALTOOLS_TEST_TERMINAL') === '1') {
            // A real Ptyxis PTY consumes bytes without running a shell command.
            const resultPath = `${privateRoot}/terminal-input.txt`;
            const readyPath = `${privateRoot}/terminal-ready`;
            const terminalLauncher = new Gio.SubprocessLauncher({flags:Gio.SubprocessFlags.NONE});
            terminalLauncher.setenv('GDK_BACKEND', 'wayland', true);
            // The private bus has no systemd user manager. Only bypass scope
            // creation; the real Ptyxis/VTE/PTY/input path remains unchanged.
            const bin = `${privateRoot}/terminal-bin`;
            GLib.mkdir_with_parents(bin, 0o700);
            const scopeShim = `${bin}/systemd-run`;
            GLib.file_set_contents(scopeShim, '#!/bin/sh\nif [ "$1" = "--version" ]; then exec /usr/bin/systemd-run --version; fi\nwhile [ "$#" -gt 0 ]; do\n case "$1" in --user|--scope|--collect|--quiet|--same-dir|--unit=ptyxis-spawn-*.scope) shift ;; *) break ;; esac\ndone\nexec "$@"\n');
            Gio.File.new_for_path(scopeShim).set_attribute_uint32('unix::mode', 0o700, Gio.FileQueryInfoFlags.NONE, null);
            terminalLauncher.setenv('PATH', `${bin}:${GLib.getenv('PATH')}`, true);
            const terminal = terminalLauncher.spawnv(['ptyxis', '--standalone', '--title', 'Personal Tools paste regression',
                '--', 'python3', `${root}/Tests/Linux/Shell/terminal-input.py`, resultPath, readyPath]);
            children.push(terminal);
            let terminalWindow;
            await waitFor(() => {
                terminalWindow = global.get_window_actors().map(actor => actor.meta_window)
                    .find(win => win.get_pid() === Number(terminal.get_identifier()));
                return terminalWindow && Gio.File.new_for_path(readyPath).query_exists(null);
            }, 'Ptyxis input collector did not start').catch(error => {
                print(`PT_PTYXIS_DIAGNOSTIC ${JSON.stringify({pid:terminal.get_identifier(), ready:Gio.File.new_for_path(readyPath).query_exists(null), windows:global.get_window_actors().map(actor => ({pid:actor.meta_window.get_pid(),wmClass:actor.meta_window.get_wm_class(),title:actor.meta_window.title}))})}`);
                throw error;
            });
            await focus(terminalWindow);
            panel.search.set_text('');
            await open();
            const expectedTerminalText = panel.entries[panel._selected].preview;
            assert([firstText, newestText].includes(expectedTerminalText), 'Unexpected terminal test selection');
            chord([Clutter.KEY_Return]);
            await waitFor(() => {
                const file = Gio.File.new_for_path(resultPath);
                if (!file.query_exists(null)) return false;
                const [, bytes] = file.load_contents(null);
                return new TextDecoder().decode(bytes) === expectedTerminalText;
            }, 'Card Enter did not paste exact bytes into the real Ptyxis PTY').catch(error => {
                const file = Gio.File.new_for_path(resultPath);
                print(`PT_PTYXIS_PASTE_DIAGNOSTIC ${JSON.stringify({open:panel.opened,focus:global.display.focus_window?.title,wmClass:terminalWindow.get_wm_class(),holds:extension._pasteBindingHolds,input:file.query_exists(null)?new TextDecoder().decode(file.load_contents(null)[1]):null})}`);
                throw error;
            });
            await waitFor(() => !extension._pasteBindingHolds, 'Ptyxis paste did not restore shortcuts');
            assert(!panel.opened && global.display.focus_window === terminalWindow, 'Ptyxis paste reopened the panel or lost focus');
            await open();
            chord([Clutter.KEY_Escape]);
            await waitFor(() => !panel.opened, 'Ptyxis shortcut was not restored after paste');
            print('PT_REAL_PTYXIS_ENTER_PASTE_PASS exact PTY bytes, panel closed, shortcut restored');
        }
        print('PT_BROWSE_REGRESSION_PASS');
    } finally {
        restoreExecute?.();
        panel.close();
        for (const child of children) try { child.force_exit(); } catch {}
    }
}
