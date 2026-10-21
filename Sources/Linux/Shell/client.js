import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {parseJson} from './model.js';

export const BUS = 'io.griplabs.PersonalTools';
export const PATH = '/io/griplabs/PersonalTools';
export const INTERFACE = 'io.griplabs.PersonalTools1';

export class DaemonClient {
    constructor(onSnapshot, onUnavailable) {
        this.connection = Gio.DBus.session;
        this.snapshot = null;
        this.owner = null;
        this._destroyed = false;
        this._onSnapshot = onSnapshot;
        this._onUnavailable = onUnavailable;
        this._lifetime = new Gio.Cancellable();
        this._signals = this.connection.signal_subscribe(BUS, INTERFACE, null, PATH, null,
            Gio.DBusSignalFlags.NONE, (_c, sender, _p, _i, signal, parameters) => {
                if (sender !== this.owner) return;
                if (signal === 'Changed') {
                    try { this._update(parseJson(parameters.deepUnpack()[0])); }
                    catch (error) { this._onUnavailable(error.message); }
                } else if (signal === 'ClipboardChanged') this.refresh().catch(() => {});
            });
        this._watch = Gio.bus_watch_name_on_connection(this.connection, BUS, Gio.BusNameWatcherFlags.NONE,
            (_c, _name, owner) => { this.owner = owner; this.refresh().catch(error => this._onUnavailable(error.message)); },
            () => { this.owner = null; this.snapshot = null; if (!this._destroyed) this._onUnavailable('Personal Tools 서비스에 연결할 수 없습니다.'); });
    }

    _update(snapshot) {
        if (this._destroyed) return;
        if (snapshot.version !== 1) throw new Error('호환되지 않는 서비스 버전입니다.');
        if (this.snapshot && snapshot.revision < this.snapshot.revision) return;
        this.snapshot = snapshot;
        this._onSnapshot(snapshot);
    }

    call(method, parameters, {fds = null, cancellable = null, timeout = 30000} = {}) {
        return new Promise((resolve, reject) => {
            if (this._destroyed || !this.owner) { reject(new Error('Personal Tools 서비스가 실행 중이 아닙니다.')); return; }
            const owner = this.owner;
            this.connection.call_with_unix_fd_list(owner, PATH, INTERFACE, method, parameters, null,
                Gio.DBusCallFlags.NO_AUTO_START, timeout, fds, cancellable ?? this._lifetime,
                (connection, result) => {
                    try {
                        const [reply, returnedFds] = connection.call_with_unix_fd_list_finish(result);
                        if (this._destroyed || owner !== this.owner) throw new Error('서비스 연결이 변경되었습니다. 다시 시도해 주세요.');
                        resolve({values: reply.deepUnpack(), fds: returnedFds});
                    } catch (error) { reject(error); }
                });
        });
    }

    async refresh() {
        const response = await this.call('GetSnapshot', null);
        this._update(parseJson(response.values[0]));
        return this.snapshot;
    }

    async execute(domain, action, payload = {}, options = {}) {
        const response = await this.call('Execute', new GLib.Variant('(sss)', [domain, action, JSON.stringify(payload)]), options);
        const result = parseJson(response.values[0]);
        if (!result.ok) throw new Error(result.error?.message ?? '요청을 완료하지 못했습니다.');
        return result.data ?? result;
    }

    async capture(metadata, handles, fds, cancellable) {
        const response = await this.call('CaptureClipboard', new GLib.Variant('(sah)', [JSON.stringify(metadata), handles]), {fds, cancellable});
        const result = parseJson(response.values[0]);
        if (!result.ok) throw new Error(result.error?.message ?? '클립보드를 보관하지 못했습니다.');
        return result;
    }

    async openClipboard(id, cancellable) {
        if (typeof id !== 'string' || !id || id.length > 256) throw new Error('클립보드 항목이 올바르지 않습니다.');
        const response = await this.call('OpenClipboard', new GLib.Variant('(s)', [id]), {cancellable});
        const metadata = parseJson(response.values[0]);
        if (!Array.isArray(metadata.mimes) || metadata.entryId !== id || metadata.mimes.length > 9) throw new Error('클립보드 전송 정보가 올바르지 않습니다.');
        return {metadata, handles: response.values[1], fds: response.fds};
    }

    destroy() {
        this._destroyed = true;
        this._lifetime.cancel();
        Gio.bus_unwatch_name(this._watch);
        this.connection.signal_unsubscribe(this._signals);
    }
}
