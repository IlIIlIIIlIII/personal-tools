import Gio from 'gi://Gio';
import GioUnix from 'gi://GioUnix';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import St from 'gi://St';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {DaemonClient} from './client.js';
import {ClipboardBridge, loadNative} from './clipboard.js';
import {ClipboardPanel} from './panel.js';
import {QuickMenu} from './quickMenu.js';
import {parseJson, shortcutValid} from './model.js';
import {canonicalShortcut} from './shortcuts.js';
import {WindowShortcuts, isWindowShortcut} from './windowShortcuts.js';

const SHELL_BUS = 'io.griplabs.PersonalTools.Shell';
const SHELL_PATH = '/io/griplabs/PersonalTools/Shell';
const SHELL_XML = `<node><interface name="io.griplabs.PersonalTools.Shell1">
<method name="ShowClipboard"/>
<method name="CloseClipboard"/>
<method name="PutClipboard"><arg type="s" direction="in"/><arg type="b" direction="in"/><arg type="s" direction="out"/></method>
<method name="GetStatus"><arg type="s" direction="out"/></method>
<method name="GetWindowShortcuts"><arg type="s" direction="out"/></method>
<signal name="WindowShortcutsChanged"><arg type="s"/></signal>
<method name="ConfigureShortcuts"><arg type="s" direction="in"/><arg type="s" direction="out"/></method>
</interface></node>`;

export default class PersonalToolsExtension extends Extension {
    enable() {
        this._enabled = true;
        this._pasteBindingHolds = 0;
        this._nativeError = null;
        this._settings = this.getSettings();
        this._interfaceSettings = new Gio.Settings({schema_id:'org.gnome.desktop.interface'});
        this._themeSignal = this._interfaceSettings.connect('changed::color-scheme',()=>this._applyTheme());
        this._textScaleSignal = this._interfaceSettings.connect('changed::text-scaling-factor',()=>this._applyTheme());
        this._themeContext = St.ThemeContext.get_for_stage(global.stage);
        this._themeContextSignal = this._themeContext.connect('changed',()=>this._applyTheme());
        this._lifetime = new Gio.Cancellable();
        let native;
        try { native = loadNative(this.path); }
        catch (error) { this._nativeError = `클립보드 전송 모듈을 불러올 수 없습니다: ${error.message}`; console.error(this._nativeError); }
        if (!this._enabled) return;
        let tokens = {};
        try { const [, bytes] = Gio.File.new_for_path(`${this.path}/tokens.json`).load_contents(null); tokens = parseJson(new TextDecoder().decode(bytes)); }
        catch { /* Geometry has the same explicit v1 defaults in model.js. */ }
        this._client = new DaemonClient(snapshot => this._onSnapshot(snapshot), message => this._onUnavailable(message));
        this._bridge = native ? new ClipboardBridge(this._client, native, message => this._panel?._status(message),
            () => this._holdPasteBindings()) : null;
        this._panel = new ClipboardPanel(this._client, this._bridge, page => this._openApp(page), tokens);
        this._menu = new QuickMenu(this._client, () => this._panel.toggle(), page => this._openApp(page));
        this._loadFonts();
        this._applyTheme();
        this._installBindings();
        this._settingsSignal = this._settings.connect('changed', (_settings, key) => {
            if (!this._bindingUpdate && ['clipboard-shortcut', 'microphone-shortcut'].includes(key)) this._installBindings();
        });
        this._windowSlots ??= Array(8).fill(null);
        this._windowShortcuts = new WindowShortcuts(this._settings, this._windowSlots, () => this._panel?.close(), state => {
            if (this._enabled && !Main.sessionMode.isLocked)
                this._exported?.emit_signal('WindowShortcutsChanged', new GLib.Variant('(s)', [JSON.stringify(state)]));
        });
        this._lastExternalWindow = null;
        this._focusSignal = global.display.connect('notify::focus-window', () => {
            const window = global.display.focus_window;
            const app = window ? Shell.WindowTracker.get_default().get_window_app(window) : null;
            if (window && app?.get_id() !== 'io.griplabs.PersonalTools.App.desktop') this._lastExternalWindow = window;
        });
        this._exportService();
        this._heartbeat = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT,30,()=>{
            if (!this._enabled) return GLib.SOURCE_REMOVE;
            if (this._client.owner) this._client.execute('shell','status',{connected:true,version:'1',clipboardReady:Boolean(this._bridge),error:this._nativeError}).catch(()=>{});
            return GLib.SOURCE_CONTINUE;
        });
        console.debug('Personal Tools extension enabled');
    }

    _loadFonts() {
        const testRoot = GLib.getenv('PERSONALTOOLS_TEST_ROOT');
        const paths = [`${this.path}/../Design/fonts`,'/usr/share/personal-tools/Design/fonts'];
        if (testRoot) paths.unshift(`${testRoot}/Sources/Linux/Design/fonts`);
        this._fontFiles=[];
        for (const path of paths) {
            const directory=Gio.File.new_for_path(path);
            if (!directory.query_exists(null)) continue;
            const files=[];
            const entries=directory.enumerate_children('standard::name,standard::type',Gio.FileQueryInfoFlags.NONE,null);
            try {
                let info;
                while ((info=entries.next_file(null)))
                    if (info.get_file_type()===Gio.FileType.REGULAR && /\.(ttf|otf|ttc)$/i.test(info.get_name())) files.push(info.get_name());
            } finally { entries.close(null); }
            if (!files.length) continue;
            const fontMap=this._panel.search.clutter_text.get_layout().get_context().get_font_map();
            for (const name of files.sort()) {
                const filename=`${path}/${name}`;
                try { fontMap.add_font_file(filename);this._fontFiles.push(filename); }
                catch(error){console.error(`Personal Tools font ${name}: ${error.message}`);}
            }
            return;
        }
        console.warn('Personal Tools bundled font files are unavailable.');
    }

    _onSnapshot(snapshot) {
        if (!this._enabled) return;
        this._panel?.update(snapshot);
        this._menu?.update(snapshot);
        const settings = snapshot.settings ?? {};
        this._requestedTheme = settings.theme ?? 'system';
        this._applyTheme();
        if (shortcutValid(settings.clipboardShortcut) && shortcutValid(settings.microphoneShortcut) &&
            (this._settings.get_strv('clipboard-shortcut')[0] !== settings.clipboardShortcut ||
             this._settings.get_strv('microphone-shortcut')[0] !== settings.microphoneShortcut)) {
            try { this._configureShortcuts(settings); }
            catch (error) { this._shortcutError = error.message; }
        }
        if (this._registeredOwner !== this._client.owner) {
            this._registeredOwner = this._client.owner;
            this._client.execute('shell', 'status', {connected: true, version: '1', clipboardReady: Boolean(this._bridge), error: this._nativeError}).catch(error => console.error(error));
        }
    }

    _onUnavailable(message) { this._panel?.unavailable(message); this._menu?.unavailable(message); }

    _applyTheme() {
        const light = this._requestedTheme === 'light' || ((!this._requestedTheme || this._requestedTheme === 'system') && this._interfaceSettings.get_string('color-scheme') !== 'prefer-dark');
        const textScale = this._interfaceSettings.get_double('text-scaling-factor');
        const textScaleChanged = this._panel && this._panel.textScale !== textScale;
        for (const actor of [this._panel?.actor,this._menu?.button.menu.actor,this._panel?._popover])
            if (actor) {
                if (light) actor.add_style_class_name('pt-light');else actor.remove_style_class_name('pt-light');
                const style = `font-size: ${18*textScale}px;`;
                if (actor.style !== style) actor.set_style(style);
            }
        if (this._panel) { this._panel.lightTheme = light;this._panel.textScale = textScale; }
        if (textScaleChanged && this._panel?.opened) this._panel._place();
        const theme = St.ThemeContext.get_for_stage(global.stage).get_theme();
        if (light === this._lightTheme && theme === this._styleTheme) return;
        const previousTheme = this._styleTheme;
        this._lightTheme = light;
        this._styleTheme = theme;
        if (this._lightStylesheet) { previousTheme?.unload_stylesheet(this._lightStylesheet); this._lightStylesheet = null; }
        if (light) {
            const file = Gio.File.new_for_path(`${this.path}/stylesheet-light.css`);
            if (file.query_exists(null)) { theme.load_stylesheet(file); this._lightStylesheet = file; }
        }
    }

    _removeBindings() {
        for (const name of this._installedBindings ?? []) Main.wm.removeKeybinding(name);
        this._installedBindings = [];
    }

    _holdPasteBindings() {
        const lifetime = this._lifetime;
        this._pasteBindingHolds++;
        this._removeBindings();
        let released = false;
        return () => {
            if (released || lifetime !== this._lifetime) return;
            released = true;
            this._pasteBindingHolds--;
            if (this._enabled && !this._pasteBindingHolds) this._installBindings();
        };
    }

    _installBindings() {
        this._removeBindings();
        // Snapshot/settings updates must not re-grab a chord in flight.
        if (this._pasteBindingHolds) return true;
        const modes = Shell.ActionMode.NORMAL | Shell.ActionMode.OVERVIEW | Shell.ActionMode.POPUP;
        const clipboardAction = Main.wm.addKeybinding('clipboard-shortcut', this._settings, Meta.KeyBindingFlags.NONE, modes, () => this._panel?.toggle());
        const microphoneAction = Main.wm.addKeybinding('microphone-shortcut', this._settings, Meta.KeyBindingFlags.NONE, modes,
            () => this._client.execute('audio', 'mute', {}).catch(error => Main.notify('Personal Tools', error.message)));
        this._installedBindings = [
            ...(clipboardAction !== Meta.KeyBindingAction.NONE ? ['clipboard-shortcut'] : []),
            ...(microphoneAction !== Meta.KeyBindingAction.NONE ? ['microphone-shortcut'] : []),
        ];
        if (clipboardAction === Meta.KeyBindingAction.NONE || microphoneAction === Meta.KeyBindingAction.NONE) {
            this._shortcutError = '다른 기능과 단축키가 충돌합니다. Personal Tools 설정에서 변경해 주세요.';
            console.warn(this._shortcutError);
        } else this._shortcutError = null;
        return !this._shortcutError;
    }

    _configureShortcuts(settings) {
        const next = [canonicalShortcut(settings.clipboardShortcut), canonicalShortcut(settings.microphoneShortcut)];
        if (next[0] === next[1]) throw new Error('클립보드와 마이크에는 서로 다른 단축키를 입력해 주세요.');
        if (next.some(isWindowShortcut)) throw new Error('창 빠른 전환에 사용하는 단축키입니다. 다른 조합을 입력해 주세요.');
        const keys = ['clipboard-shortcut', 'microphone-shortcut'];
        const previous = keys.map(key => this._settings.get_strv(key));
        this._bindingUpdate = true;
        this._removeBindings();
        try {
            // Probe while our own bindings are removed, so unchanged keys or a
            // clipboard/microphone swap do not collide with this extension.
            const probes = [];
            try {
                for (const accelerator of next) {
                    const action = global.display.grab_accelerator(accelerator, Meta.KeyBindingFlags.NONE);
                    if (action === Meta.KeyBindingAction.NONE) throw new Error('다른 기능과 단축키가 충돌합니다. 다른 조합을 입력해 주세요.');
                    probes.push(action);
                }
            } finally { for (const action of probes) global.display.ungrab_accelerator(action); }
            keys.forEach((key, index) => {
                if (!this._settings.set_strv(key, [next[index]])) throw new Error('GNOME 단축키 설정을 저장할 수 없습니다.');
            });
            if (!this._installBindings()) throw new Error(this._shortcutError);
            return {ok: true};
        } catch (error) {
            keys.forEach((key, index) => this._settings.set_strv(key, previous[index]));
            if (!this._installBindings()) throw new Error(`${error.message} 이전 단축키 복원도 실패했습니다.`);
            throw error;
        } finally { this._bindingUpdate = false; }
    }

    _openApp(page = 'clipboard') {
        this._panel?.close();
        try {
            const executable = GLib.find_program_in_path('personal-tools');
            if (executable) Gio.Subprocess.new([executable, '--page', page], Gio.SubprocessFlags.NONE);
            else {
                const app = GioUnix.DesktopAppInfo.new('io.griplabs.PersonalTools.App.desktop');
                if (!app) throw new Error('Personal Tools 앱이 설치되어 있지 않습니다.');
                app.launch([], global.create_app_launch_context(0, -1));
            }
        } catch (error) { Main.notify('Personal Tools', error.message); }
    }

    _exportService() {
        const run = async (invocation, action, hasResult) => {
            try {
                await this._authorize(invocation.get_sender());
                const result = await action();
                invocation.return_value(hasResult ? new GLib.Variant('(s)', [JSON.stringify(result)]) : null);
            } catch (error) { invocation.return_dbus_error('io.griplabs.PersonalTools.Error', error.message); }
        };
        const api = {
            ShowClipboardAsync: (_params, invocation) => run(invocation, () => { this._panel.open(); }, false),
            CloseClipboardAsync: (_params, invocation) => run(invocation, () => { this._panel.close(); }, false),
            PutClipboardAsync: ([id, paste], invocation) => run(invocation, async () => {
                if (!this._bridge) throw new Error(this._nativeError);
                const presentation = this._panel._presentation;
                return this._bridge.put(id, {paste, target: this._panel.opened ? this._panel.target : this._lastExternalWindow,
                    stillCurrent: () => this._enabled && presentation === this._panel._presentation,
                    closePanel: () => this._panel.close({pasting: true})});
            }, true),
            GetStatus: () => JSON.stringify({connected: true, version: '1', clipboardReady: Boolean(this._bridge),
                buildVersion:'0.7.0~ubuntu9', error: this._nativeError ?? this._shortcutError,
                windowShortcuts: this._windowShortcuts?.status,
                panelOpen: this._panel.opened, panelVisible:this._panel.actor.visible}),
            GetWindowShortcutsAsync: (_params, invocation) => run(invocation, () => this._windowShortcuts?.details ??
                {ready: false, error: '창 빠른 전환을 준비하고 있습니다.', slots: []}, true),
            ConfigureShortcutsAsync: ([json], invocation) => run(invocation,
                () => this._configureShortcuts(parseJson(json, 4096)), true),
        };
        this._exported = Gio.DBusExportedObject.wrapJSObject(SHELL_XML, api);
        this._exported.export(Gio.DBus.session, SHELL_PATH);
        this._busOwner = Gio.bus_own_name_on_connection(Gio.DBus.session, SHELL_BUS, Gio.BusNameOwnerFlags.NONE, null, null);
    }

    _authorize(sender) {
        return new Promise((resolve, reject) => Gio.DBus.session.call('org.freedesktop.DBus', '/org/freedesktop/DBus',
            'org.freedesktop.DBus', 'GetConnectionUnixUser', new GLib.Variant('(s)', [sender]), new GLib.VariantType('(u)'),
            Gio.DBusCallFlags.NONE, 5000, this._lifetime, (connection, result) => {
                try {
                    const [uid] = connection.call_finish(result).deepUnpack();
                    if (uid !== new Gio.Credentials().get_unix_user()) throw new Error('현재 사용자만 클립보드에 접근할 수 있습니다.');
                    if (!this._enabled || Main.sessionMode.isLocked) throw new Error('현재 세션에서 사용할 수 없습니다.');
                    resolve();
                } catch (error) { reject(error); }
            }));
    }

    disable() {
        this._enabled = false;
        try { this._windowShortcuts?.destroy(); }
        catch (error) { console.error(`Personal Tools window shortcuts: ${error.message}`); }
        this._windowShortcuts = null;
        if (this._heartbeat) GLib.source_remove(this._heartbeat);
        this._heartbeat = 0;
        this._lifetime?.cancel();
        if (this._themeContextSignal) this._themeContext.disconnect(this._themeContextSignal);
        this._themeContextSignal = 0;this._themeContext = null;
        if (this._lightStylesheet) St.ThemeContext.get_for_stage(global.stage).get_theme().unload_stylesheet(this._lightStylesheet);
        this._lightStylesheet = null;this._lightTheme = null;
        this._styleTheme = null;
        if (this._themeSignal) this._interfaceSettings.disconnect(this._themeSignal);
        if (this._textScaleSignal) this._interfaceSettings.disconnect(this._textScaleSignal);
        this._textScaleSignal = 0;
        this._themeSignal = 0;this._interfaceSettings = null;
        if (this._client?.owner) this._client.execute('shell', 'status', {connected: false, version: '1'}).catch(() => {});
        this._panel?.destroy(); this._panel = null;
        this._menu?.destroy(); this._menu = null;
        this._bridge?.destroy(); this._bridge = null;
        this._client?.destroy(); this._client = null;
        this._exported?.unexport(); this._exported = null;
        if (this._busOwner) Gio.bus_unown_name(this._busOwner);
        this._busOwner = 0;
        if (this._focusSignal) global.display.disconnect(this._focusSignal);
        this._focusSignal = 0;
        if (this._settingsSignal) this._settings.disconnect(this._settingsSignal);
        this._settingsSignal = 0;
        this._removeBindings();
        this._registeredOwner = null;
        this._settings = null;
    }
}
