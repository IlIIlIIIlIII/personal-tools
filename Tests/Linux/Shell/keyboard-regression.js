import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Shell from 'gi://Shell';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {FixtureClient} from './fixtures.js';
import {automation, prepareEnvironment} from './automation.js';

export const METRICS = {};
export const {init, run} = automation(regressions, METRICS);
const assert = (condition, message) => { if (!condition) throw new Error(message); };
async function waitFor(predicate, message) {
    for (let i = 0; i < 100; i++) {
        if (predicate()) return;
        await Scripting.sleep(50);
    }
    throw new Error(message);
}
function descendants(actor, predicate) {
    return [...(predicate(actor) ? [actor] : []), ...actor.get_children().flatMap(child => descendants(child, predicate))];
}
async function capture(name) {
    const path = `${GLib.getenv('PERSONALTOOLS_TEST_OUTPUT')}/${name}.png`;
    const stream = Gio.File.new_for_path(path).replace(null, false, Gio.FileCreateFlags.REPLACE_DESTINATION, null);
    await new Shell.Screenshot().screenshot(false, stream);
    stream.close(null);
}

// Virtual devices belong to the private compositor created by run-isolated.py.
// Keyboard events pass through Mutter accelerators and focused Clutter actors;
// Calling panel._key(), panel.open(), or overview.show() for a tested action
// would miss these bugs.
async function regressions() {
    await prepareEnvironment();
    // The automation module can load after startup-complete and miss Shell's
    // initial ActionMode.NONE -> NORMAL transition. Establish a normal desktop
    // through Shell's own modal lifecycle before exercising any input.
    if (Main.actionMode === Shell.ActionMode.NONE) {
        Main.overview.show();
        await Scripting.sleep(150);
    }
    Main.overview.hide();
    await Scripting.sleep(300);
    assert(Main.actionMode === Shell.ActionMode.NORMAL, 'Private Shell did not finish initializing normal desktop input');
    const extension = Main.extensionManager.lookup('personal-tools@griplabs.io')?.stateObj;
    assert(extension?._panel, 'Personal Tools extension is unavailable');
    extension._bridge?.destroy();
    extension._bridge = null;
    extension._client.destroy();
    const client = new FixtureClient();
    extension._client = client;
    extension._panel.client = client;
    extension._panel.bridge = null;
    extension._menu.client = client;
    extension._onSnapshot(client.snapshot);
    const panel = extension._panel;
    const keyboard = Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);
    const pointer = Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.POINTER_DEVICE);
    const chord = keys => {
        const now = GLib.get_monotonic_time();
        for (const key of keys) keyboard.notify_keyval(now, key, Clutter.KeyState.PRESSED);
        for (const key of [...keys].reverse()) keyboard.notify_keyval(now + 1, key, Clutter.KeyState.RELEASED);
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
    const inSearch = () => panel.search.contains(global.stage.key_focus);
    const fullyClosed = () => !panel.opened && !panel.actor.visible && !panel._grab && !panel._popover && !panel._popoverGrab;
    const open = async () => {
        chord([Clutter.KEY_Control_L, Clutter.KEY_Shift_L, Clutter.KEY_v]);
        await waitFor(() => panel.opened && panel.actor.visible && panel._cards.length > 0, 'Ctrl+Shift+V did not open a populated clipboard panel');
        await Scripting.sleep(120);
    };
    const focusSearch = async origin => {
        chord([Clutter.KEY_Control_L, Clutter.KEY_f]);
        await waitFor(() => inSearch() && !panel._popover, `Ctrl+F did not focus the main search from ${origin}`);
        assert(panel.opened && panel.actor.visible && panel._grab, `Ctrl+F broke the panel grab from ${origin}`);
        print(`PT_KEYBOARD_CTRL_F_PASS ${origin}`);
    };
    let unexpectedVisibility = 0;
    let visibleTransitions = 0;
    let hiddenTransitions = 0;
    let overlayEvents = 0;
    let targetProcess;
    let targetWindow;
    const originalShortcuts = {...client.snapshot.settings};
    const overlaySignal = global.display.connect('overlay-key', () => overlayEvents++);
    const visibilitySignal = panel.actor.connect('notify::visible', () => {
        if (panel.actor.visible) visibleTransitions++;
        else hiddenTransitions++;
        if (panel.actor.visible && !panel.opened) unexpectedVisibility++;
    });
    try {
        const launcher = new Gio.SubprocessLauncher({flags: Gio.SubprocessFlags.NONE});
        launcher.setenv('GDK_BACKEND', 'wayland', true);
        targetProcess = launcher.spawnv(['gjs', '-m', `${GLib.getenv('PERSONALTOOLS_TEST_ROOT')}/Tests/Linux/Shell/wayland-client.js`, 'Target']);
        await waitFor(() => {
            targetWindow = global.get_window_actors().map(actor => actor.meta_window)
                .find(win => win.get_pid() === Number(targetProcess.get_identifier()));
            return Boolean(targetWindow);
        }, 'Private Wayland focus target did not appear');
        targetWindow.move_resize_frame(true, 100, 100, 600, 320);
        targetWindow.activate(global.display.get_current_time_roundtrip());
        await waitFor(() => global.display.focus_window === targetWindow, 'Private Wayland target did not gain focus');
        await Scripting.sleep(200);
        print(`PT_KEYBOARD_OVERLAY_SETUP ${JSON.stringify({key: new Gio.Settings({schema_id: 'org.gnome.mutter'}).get_string('overlay-key'), actionMode: Main.actionMode})}`);
        assert(fullyClosed(), 'Clipboard panel was already visible or grabbed before any shortcut');
        // Rapid successive overlay-key releases are the real GNOME apps-grid path.
        chord([Clutter.KEY_Super_L]);
        await Scripting.sleep(80);
        print(`PT_KEYBOARD_FIRST_SUPER ${JSON.stringify({overlayEvents, visible: Main.overview.visible, grid: Main.overview.dash.showAppsButton.checked, actionMode: Main.actionMode})}`);
        chord([Clutter.KEY_Super_L]);
        await Scripting.sleep(200);
        print(`PT_KEYBOARD_SECOND_SUPER ${JSON.stringify({overlayEvents, visible: Main.overview.visible, grid: Main.overview.dash.showAppsButton.checked, actionMode: Main.actionMode})}`);
        await waitFor(() => Main.overview.visible && Main.overview.dash.showAppsButton.checked,
            'Two real Super taps did not reach the applications grid');
        await Scripting.sleep(200);
        assert(fullyClosed() && unexpectedVisibility === 0,
            `Super twice revealed a closed clipboard actor (${unexpectedVisibility} unexpected shows)`);
        await capture('super-twice-applications-no-clipboard');
        print('PT_KEYBOARD_SUPER_TWICE_PASS actual overlay keys; apps grid reached; no clipboard actor or modal grab');
        await open();
        await Scripting.sleep(350);
        assert(!Main.overview.visible && panel.opened && panel.actor.visible && panel._grab,
            'Ctrl+Shift+V from applications grid did not remain open after overview dismissal');
        assert(panel.target === targetWindow, 'Apps-grid invocation lost the previous Wayland paste target');
        print('PT_KEYBOARD_OPEN_FROM_APPS_GRID_PASS panel remains open and previous Wayland target retained');
        chord([Clutter.KEY_Escape]);
        await waitFor(fullyClosed, 'Escape did not close the clipboard after applications-grid invocation');
        await waitFor(() => global.display.focus_window === targetWindow, 'Closing clipboard did not restore Wayland target focus');
        chord([Clutter.KEY_Super_L]);
        await waitFor(() => Main.overview.visible && !Main.overview.dash.showAppsButton.checked, 'Super did not reach the window overview');
        await open();
        await Scripting.sleep(350);
        assert(!Main.overview.visible && panel.opened && panel.actor.visible && panel._grab,
            'Ctrl+Shift+V from window overview did not remain open after overview dismissal');
        assert(panel.target === targetWindow, 'Overview invocation lost the previous Wayland paste target');
        print('PT_KEYBOARD_OPEN_FROM_OVERVIEW_PASS panel remains open and previous Wayland target retained');
        chord([Clutter.KEY_Escape]);
        await waitFor(fullyClosed, 'Escape did not close clipboard after window-overview invocation');
        await waitFor(() => global.display.focus_window === targetWindow, 'Wayland target focus did not return after overview clipboard closed');

        await open();
        await waitFor(() => panel._cards.some(card => card.contains(global.stage.key_focus)),
            'Opening clipboard did not focus its selected card without an extra Down key');
        await focusSearch('clipboard card');
        panel.normalButton.grab_key_focus();
        assert(panel.normalButton.contains(global.stage.key_focus), 'Mode button did not receive keyboard focus');
        await focusSearch('mode button');

        await click(panel.filterButton);
        await waitFor(() => Boolean(panel._popover), 'Filter button did not open its modal popover');
        let fields = descendants(panel._popover, actor => actor instanceof St.Entry);
        assert(fields.length === 2, 'Filter date fields are missing');
        await click(fields[0]);
        assert(fields[0].contains(global.stage.key_focus), 'Filter date field did not receive text focus');
        await focusSearch('filter date text entry');

        const addCollection = descendants(panel.categories, actor => actor.accessible_name === '모음 추가')[0];
        assert(addCollection, 'Add collection control is unavailable');
        await click(addCollection);
        await waitFor(() => Boolean(panel._popover), 'Collection button did not open its modal popover');
        fields = descendants(panel._popover, actor => actor instanceof St.Entry);
        assert(fields.length === 1 && fields[0].contains(global.stage.key_focus), 'Collection name field did not receive text focus');
        await focusSearch('collection name text entry');
        panel.search.set_text('검색 단축키 확인');
        panel.search.clutter_text.set_selection(0, 0);
        chord([Clutter.KEY_Control_L, Clutter.KEY_f]);
        await waitFor(() => panel.search.clutter_text.get_selection() === '검색 단축키 확인',
            'Ctrl+F in the search text field did not select the existing query');
        print('PT_KEYBOARD_CTRL_F_PASS search text entry selects the query');
        panel.search.set_text('');
        await capture('ctrl-f-main-search-restored');
        await click(panel.filterButton);
        await waitFor(() => Boolean(panel._popover), 'Escape test filter popover did not open');
        chord([Clutter.KEY_Escape]);
        await waitFor(() => !panel._popover && !panel._popoverGrab, 'Escape left a nested popover or modal grab behind');
        assert(panel.opened && panel.actor.visible && panel._grab, 'Popover Escape unexpectedly closed the main clipboard panel');
        chord([Clutter.KEY_Escape]);
        await waitFor(fullyClosed, 'Escape left a clipboard actor or modal grab behind');
        print('PT_KEYBOARD_ESCAPE_PASS actor and both modal grabs released');

        // Overlay transitions must dismiss an already-open panel too. A modal
        // popup must not remain trapped above GNOME's overview/apps grid.
        await open();
        chord([Clutter.KEY_Super_L]);
        await waitFor(fullyClosed, 'Super while clipboard was open left the popup visible or grabbed');
        await Scripting.sleep(150);
        if (Main.overview.visible) {
            chord([Clutter.KEY_Escape]);
            await Scripting.sleep(150);
            if (Main.overview.visible) chord([Clutter.KEY_Escape]);
            await waitFor(() => !Main.overview.visible, 'Could not leave overview after dismissing clipboard');
        }
        await open();
        await click(panel.filterButton);
        await waitFor(() => Boolean(panel._popover), 'Final filter popover did not open');
        chord([Clutter.KEY_Super_L]);
        await waitFor(fullyClosed, 'Super inside a nested popover left the popup visible or grabbed');
        assert(unexpectedVisibility === 0, `Closed panel became visible ${unexpectedVisibility} times`);
        await capture('super-dismissed-clipboard');
        print('PT_KEYBOARD_SUPER_OPEN_PASS panel and nested-popover modal grabs both released');
        if (Main.overview.visible) {
            chord([Clutter.KEY_Escape]);
            await Scripting.sleep(150);
            if (Main.overview.visible) chord([Clutter.KEY_Escape]);
            await waitFor(() => !Main.overview.visible, 'Could not leave overview before custom shortcut test');
        }
        const configured = extension._configureShortcuts({...originalShortcuts, clipboardShortcut: '<Super><Shift>v'});
        assert(configured.ok, 'Custom Super+Shift+V shortcut could not be registered');
        await Scripting.sleep(150);
        const showsBefore = visibleTransitions;
        const hidesBefore = hiddenTransitions;
        chord([Clutter.KEY_Super_L, Clutter.KEY_Shift_L, Clutter.KEY_v]);
        await waitFor(() => panel.opened && panel.actor.visible && panel._grab, 'Custom Super+Shift+V did not open clipboard');
        await Scripting.sleep(250);
        assert(visibleTransitions === showsBefore + 1 && hiddenTransitions === hidesBefore,
            'Custom Super shortcut opened or closed the panel more than once');
        chord([Clutter.KEY_Super_L, Clutter.KEY_Shift_L, Clutter.KEY_v]);
        await waitFor(fullyClosed, 'Repeated custom Super+Shift+V did not close clipboard');
        await Scripting.sleep(250);
        assert(fullyClosed() && visibleTransitions === showsBefore + 1 && hiddenTransitions === hidesBefore + 1,
            'Repeated custom Super shortcut closed then reopened clipboard');
        extension._configureShortcuts(originalShortcuts);
        await Scripting.sleep(150);
        await open();
        chord([Clutter.KEY_Escape]);
        await waitFor(fullyClosed, 'Restored Ctrl+Shift+V shortcut did not close after opening');
        print('PT_KEYBOARD_CUSTOM_SUPER_SHORTCUT_PASS opens once; repeated chord closes once; Ctrl+Shift+V restored');
        print('PT_KEYBOARD_REGRESSION_PASS real Super, Ctrl+Shift+V, Ctrl+F and Escape events');
    } finally {
        global.display.disconnect(overlaySignal);
        panel.actor.disconnect(visibilitySignal);
        panel.close();
        Main.overview.hide();
        extension._configureShortcuts(originalShortcuts);
        targetProcess?.force_exit();
    }
}
