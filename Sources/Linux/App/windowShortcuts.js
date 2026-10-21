import Gio from 'gi://Gio';

export const WINDOW_SLOT_KEYS = ['1', '2', '3', '4', 'q', 'w', 'e', 'r'];
const unavailable = (state, error = null) => ({state, ready: false, registered: 0, slots: [], error});

// This is session state owned by Shell, not persisted daemon configuration.
export class WindowShortcutClient {
    constructor(changed) {
        this.state = unavailable('connecting');
        this._changed = changed;
        this._generation = 0;
    }

    _set(state) { this.state = state; this._changed(); }

    async start() {
        try {
            this._proxy = await new Promise((resolve, reject) => Gio.DBusProxy.new_for_bus(Gio.BusType.SESSION,
                Gio.DBusProxyFlags.DO_NOT_AUTO_START, null, 'io.griplabs.PersonalTools.Shell',
                '/io/griplabs/PersonalTools/Shell', 'io.griplabs.PersonalTools.Shell1', null, (_object, result) => {
                    try { resolve(Gio.DBusProxy.new_for_bus_finish(result)); } catch (error) { reject(error); }
                }));
            this._proxy.connect('g-signal', (_proxy, _sender, name, args) => {
                if (name !== 'WindowShortcutsChanged') return;
                try {
                    const state = JSON.parse(args.deepUnpack()[0]);
                    this._generation++; // A late initial read must not undo a newer event.
                    this._set({...state, state: 'connected'});
                } catch { this.refresh(); }
            });
            this._proxy.connect('notify::g-name-owner', () => {
                this._generation++;
                this._set(unavailable(this._proxy.g_name_owner ? 'connecting' : 'unavailable'));
                this.refresh();
            });
            await this.refresh();
        } catch (error) { this._set(unavailable('unavailable', error.message)); }
    }

    async refresh() {
        const generation = ++this._generation;
        if (!this._proxy?.g_name_owner) { this._set(unavailable('unavailable')); return; }
        try {
            const [json] = await new Promise((resolve, reject) => this._proxy.call('GetWindowShortcuts', null,
                Gio.DBusCallFlags.NONE, 5000, null, (proxy, result) => {
                    try { resolve(proxy.call_finish(result).deepUnpack()); } catch (error) { reject(error); }
                }));
            if (generation === this._generation) this._set({...JSON.parse(json), state: 'connected'});
        } catch (error) {
            if (generation !== this._generation) return;
            const outdated = error.matches(Gio.DBusError, Gio.DBusError.UNKNOWN_METHOD);
            this._set(unavailable(outdated ? 'outdated' : 'unavailable', outdated ? null : error.message));
        }
    }
}
