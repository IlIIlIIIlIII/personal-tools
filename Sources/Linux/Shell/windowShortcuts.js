import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {canonicalShortcut} from './shortcuts.js';

export const WINDOW_SLOT_KEYS = ['1', '2', '3', '4', 'q', 'w', 'e', 'r'];
const accelerators = new Set(WINDOW_SLOT_KEYS.flatMap(key => [`<Super>${key}`, `<Super><Shift>${key}`]));
const backupKey = 'window-shortcut-backup';
const conflictSchemas = [
    'org.gnome.shell.keybindings',
    'org.gnome.desktop.wm.keybindings',
    'org.gnome.mutter.keybindings',
    'org.gnome.mutter.wayland.keybindings',
    'org.gnome.shell.extensions.dash-to-dock',
];
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

export function isWindowShortcut(value) {
    try { return accelerators.has(canonicalShortcut(value)); }
    catch { return false; }
}

// Keep the original user values (including "use the schema default") before
// yielding only the requested accelerators. A persisted journal also survives
// a Shell crash. Never overwrite a setting the user changed while we held it.
class ShortcutOverrides {
    constructor(settings) { this.settings = settings; }

    _settings(schemaId) {
        const schema = Gio.SettingsSchemaSource.get_default().lookup(schemaId, true);
        return schema ? new Gio.Settings({settings_schema: schema}) : null;
    }

    restore() {
        const pending = [];
        for (const entry of JSON.parse(this.settings.get_string(backupKey))) {
            const settings = this._settings(entry.schema);
            if (!settings?.settings_schema.has_key(entry.key)) { pending.push(entry); continue; }
            if (!same(settings.get_strv(entry.key), entry.applied)) continue;
            if (!settings.is_writable(entry.key)) { pending.push(entry); continue; }
            if (entry.user === null) settings.reset(entry.key);
            else if (!settings.set_strv(entry.key, entry.user)) pending.push(entry);
        }
        Gio.Settings.sync();
        this.settings.set_string(backupKey, JSON.stringify(pending));
        Gio.Settings.sync();
        if (pending.length) throw new Error('이전 창 전환 단축키 설정을 복원할 수 없습니다.');
    }

    acquire() {
        this.restore();
        const entries = [];
        for (const schema of conflictSchemas) {
            const settings = this._settings(schema);
            if (!settings) continue;
            for (const key of settings.settings_schema.list_keys()) {
                const value = settings.get_value(key);
                if (value.get_type_string() !== 'as') continue;
                const original = value.deepUnpack();
                const applied = original.filter(accelerator => !isWindowShortcut(accelerator));
                if (same(original, applied)) continue;
                if (!settings.is_writable(key)) throw new Error(`단축키 설정을 변경할 수 없습니다: ${key}`);
                entries.push({schema, key, user: settings.get_user_value(key)?.deepUnpack() ?? null, applied});
            }
        }
        if (!this.settings.set_string(backupKey, JSON.stringify(entries)))
            throw new Error('기존 단축키 설정을 보관할 수 없습니다.');
        Gio.Settings.sync();
        for (const entry of entries)
            if (!this._settings(entry.schema).set_strv(entry.key, entry.applied))
                throw new Error(`단축키를 등록할 수 없습니다: ${entry.key}`);
        Gio.Settings.sync();
    }
}

export class WindowShortcuts {
    constructor(settings, slots, beforeActivate, changed = () => {}) {
        this._settings = settings;
        this._slots = slots;
        this._beforeActivate = beforeActivate;
        this._onChanged = changed;
        this._overrides = new ShortcutOverrides(settings);
        this._bindings = [];
        this._windowSignals = new Map();
        this.ready = false;
        this.error = null;
        const windows = new Set(global.get_window_actors().map(actor => actor.meta_window));
        for (let index = 0; index < WINDOW_SLOT_KEYS.length; index++) {
            const window = slots[index];
            slots[index] = null;
            if (windows.has(window)) this._assign(index, window);
        }
        try {
            for (const key of ['clipboard-shortcut', 'microphone-shortcut'])
                if (settings.get_strv(key).some(isWindowShortcut))
                    throw new Error('클립보드 또는 마이크 단축키가 창 빠른 전환 키와 겹칩니다. 설정에서 변경해 주세요.');
            this._overrides.acquire();
            // Mutter rebuilds bindings at DEFAULT_IDLE + 10 (prefs notify).
            // Probe after that rebuild, not at DEFAULT_IDLE with stale grabs.
            this._installSource = GLib.idle_add(GLib.PRIORITY_LOW, () => {
                this._installSource = 0;
                try { this._install(); }
                catch (error) { this._failed(error); }
                return GLib.SOURCE_REMOVE;
            });
        } catch (error) { this._failed(error); }
    }

    _install() {
        const probes = [];
        try {
            for (const accelerator of accelerators) {
                const action = global.display.grab_accelerator(accelerator, Meta.KeyBindingFlags.NONE);
                if (action === Meta.KeyBindingAction.NONE)
                    throw new Error(`창 빠른 전환 단축키가 다른 기능과 겹칩니다: ${accelerator}`);
                probes.push(action);
            }
        } finally { for (const action of probes) global.display.ungrab_accelerator(action); }
        for (let index = 0; index < WINDOW_SLOT_KEYS.length; index++) {
            for (const register of [false, true]) {
                const name = `window-${register ? 'register' : 'focus'}-${WINDOW_SLOT_KEYS[index]}`;
                const modes = register ? Shell.ActionMode.NORMAL : Shell.ActionMode.NORMAL | Shell.ActionMode.OVERVIEW;
                const action = Main.wm.addKeybinding(name, this._settings, Meta.KeyBindingFlags.IGNORE_AUTOREPEAT,
                    modes, () => register ? this.register(index) : this.activate(index));
                if (action === Meta.KeyBindingAction.NONE) throw new Error(`창 빠른 전환 키를 등록할 수 없습니다: ${name}`);
                this._bindings.push(name);
            }
        }
        this.ready = true;
        this._changed();
    }

    _removeBindings() {
        for (const name of this._bindings) Main.wm.removeKeybinding(name);
        this._bindings = [];
        this.ready = false;
    }

    _failed(error) {
        this._removeBindings();
        try { this._overrides.restore(); }
        catch (restoreError) { error = new Error(`${error.message} ${restoreError.message}`); }
        this.error = error.message;
        this._changed();
        console.error(`Personal Tools window shortcuts: ${this.error}`);
        Main.notify('Personal Tools · 창 빠른 전환', this.error);
    }

    _assign(index, window) {
        const previous = this._slots[index];
        const signals = this._windowSignals.get(index) ?? [];
        for (const signal of signals) previous.disconnect(signal);
        this._windowSignals.delete(index);
        this._slots[index] = window;
        if (window) this._windowSignals.set(index, [
            window.connect('unmanaged', () => this._assign(index, null)),
            window.connect('notify::title', () => this._changed()),
        ]);
        this._changed();
    }

    _changed() {
        if (this._changedSource) return;
        this._changedSource = GLib.idle_add(GLib.PRIORITY_DEFAULT_IDLE, () => {
            this._changedSource = 0;
            this._onChanged(this.details);
            return GLib.SOURCE_REMOVE;
        });
    }

    _show(message) {
        Main.osdWindowManager.showOne(global.display.get_current_monitor(),
            new Gio.ThemedIcon({name: 'focus-windows-symbolic'}), message, null, null);
    }

    register(index) {
        const window = global.display.focus_window;
        if (!window || window.is_override_redirect() ||
            ![Meta.WindowType.NORMAL, Meta.WindowType.DIALOG, Meta.WindowType.MODAL_DIALOG].includes(window.get_window_type())) {
            this._show('등록할 창을 먼저 선택해 주세요.');
            return;
        }
        this._assign(index, window);
        this._show(`Super+${WINDOW_SLOT_KEYS[index].toUpperCase()} 등록 · ${(window.get_title() || '제목 없는 창').slice(0, 60)}`);
    }

    activate(index) {
        const window = this._slots[index];
        if (!window) {
            this._show(`빈 슬롯 · Super+Shift+${WINDOW_SLOT_KEYS[index].toUpperCase()}로 창 등록`);
            return;
        }
        this._beforeActivate();
        if (window.minimized) window.unminimize();
        Main.activateWindow(window);
    }

    get status() {
        return {ready: this.ready, error: this.error, registered: this._slots.filter(Boolean).length};
    }

    get details() {
        return {...this.status, slots: WINDOW_SLOT_KEYS.map((key, index) => {
            const window = this._slots[index];
            const app = window ? Shell.WindowTracker.get_default().get_window_app(window) : null;
            return {key, registered: Boolean(window), title: window?.get_title() || '',
                appName: app?.get_name() || window?.get_wm_class() || ''};
        })};
    }

    destroy() {
        if (this._installSource) GLib.source_remove(this._installSource);
        this._installSource = 0;
        if (this._changedSource) GLib.source_remove(this._changedSource);
        this._changedSource = 0;
        this._removeBindings();
        for (const [index, signals] of this._windowSignals)
            for (const signal of signals) this._slots[index].disconnect(signal);
        this._windowSignals.clear();
        // Keep live references on the Extension instance across lock/unlock;
        // enable() discards any windows closed while the extension was disabled.
        this._overrides.restore();
    }
}
