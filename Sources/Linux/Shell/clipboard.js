import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GioUnix from 'gi://GioUnix';
import GLib from 'gi://GLib';
import GIRepository from 'gi://GIRepository?version=3.0';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import {allowedMimes, isExcluded, isTerminal, refusedFormat, RevisionGate} from './model.js';

export function loadNative(extensionPath) {
    const repository = GIRepository.Repository.dup_default();
    repository.prepend_search_path(`${extensionPath}/native`);
    repository.prepend_library_path(`${extensionPath}/native`);
    imports.gi.versions.PtClipboard = '1.0';
    return imports.gi.PtClipboard;
}

export class ClipboardBridge {
    constructor(client, native, onError, holdPasteBindings = () => () => {}) {
        this.client = client;
        this.native = native;
        this.onError = onError;
        this._holdPasteBindings = holdPasteBindings;
        this._selection = global.display.get_selection();
        this._tracker = Shell.WindowTracker.get_default();
        this._captureGate = new RevisionGate();
        this._restoreGate = new RevisionGate();
        this._destroyed = false;
        this._source = null;
        this._keyboard = Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);
        this._selectionSignal = this._selection.connect('owner-changed', (_selection, type, owner) => {
            if (type !== Meta.SelectionType.SELECTION_CLIPBOARD) return;
            this._captureCancel?.cancel();
            const generation = this._captureGate.next();
            if (!owner || owner === this._source || this._destroyed) return;
            const settings = this.client.snapshot?.clipboard;
            if (!settings || settings.paused || global.screen_width <= 0) return;
            const window = global.display.focus_window;
            const app = window ? this._tracker.get_window_app(window) : null;
            const observedId = app?.get_id() ?? '';
            // Shell invents window:<id> identifiers for unmatched windows. Those
            // are not application identities and must remain unknown to users.
            const sourceApp = observedId.endsWith('.desktop') ? observedId : '';
            if (isExcluded(sourceApp, settings.settings?.excludedApps)) return;
            this._captureCancel = new Gio.Cancellable();
            this._capture(owner, generation, sourceApp, this._captureCancel).catch(error => {
                if (!this._destroyed && this._captureGate.current(generation) && !this._captureCancel?.is_cancelled()) this.onError(error.message);
            });
        });
    }

    async _capture(owner, generation, sourceApp, cancellable) {
        const mimes = allowedMimes(owner.get_mimetypes());
        if (!mimes.length) return;
        const requestedLimit = this.client.snapshot?.clipboard.settings?.itemLimitBytes ?? 104857600;
        const maxBytes = Math.max(1, Math.min(1024 * 1024 * 1024, requestedLimit));
        const fds = new Gio.UnixFDList();
        const metadata = {generation, sourceApp, sourceAttribution: sourceApp ? 'foreground-observed' : 'unknown', mimes: []};
        const handles = [];
        let total = 0;
        for (const mime of mimes) {
            if (cancellable.is_cancelled() || !this._captureGate.current(generation)) return;
            const fd = this.native.create_transfer_fd();
            try {
                const output = new GioUnix.OutputStream({fd, close_fd: false});
                // Mutter pins the current source for this transfer. The generation
                // check rejects the whole capture if it changes between MIME reads.
                try {
                    await new Promise((resolve, reject) => {
                        this._selection.transfer_async(Meta.SelectionType.SELECTION_CLIPBOARD, mime,
                            maxBytes - total + 1, output, cancellable, (selection, result) => {
                                try { selection.transfer_finish(result); resolve(); } catch (error) { reject(error); }
                            });
                    });
                } catch (error) {
                    // A format the owner offered but refused is skipped; the
                    // formats it does deliver still make up the entry.
                    if (refusedFormat(error)) continue;
                    throw error;
                } finally { output.close(null); }
                if (cancellable.is_cancelled() || !this._captureGate.current(generation)) return;
                this.native.seal_transfer_fd(fd, maxBytes - total);
                const length = this.native.fd_size(fd);
                const fdIndex = fds.append(fd);
                handles.push(fdIndex);
                metadata.mimes.push({mime, fdIndex, length});
                total += length;
            } finally { this.native.close_fd(fd); }
        }
        if (metadata.mimes.length && !cancellable.is_cancelled() && this._captureGate.current(generation))
            await this.client.capture(metadata, handles, fds, cancellable);
    }

    async put(entryId, {paste = false, target = null, stillCurrent = () => true, closePanel = () => {}} = {}) {
        const generation = this._restoreGate.next();
        this._restoreCancel?.cancel();
        const cancellable = new Gio.Cancellable();
        this._restoreCancel = cancellable;
        const {metadata, handles, fds} = await this.client.openClipboard(entryId, cancellable);
        if (!stillCurrent() || !this._restoreGate.current(generation) || this._destroyed) throw new Error('클립보드 작업을 취소했습니다.');
        if (!fds || !Array.isArray(handles)) throw new Error('클립보드 데이터가 없습니다.');
        const offered = new Set(handles);
        const formats = metadata.mimes.map(format => {
            if (!offered.has(format.fdIndex) || !Number.isSafeInteger(format.length) || format.length < 0) throw new Error('클립보드 파일 정보가 올바르지 않습니다.');
            const fd = fds.get(format.fdIndex);
            try { if (this.native.fd_size(fd) !== format.length) throw new Error('클립보드 파일 크기가 변경되었습니다.'); }
            finally { this.native.close_fd(fd); }
            return [format.mime, format.fdIndex];
        });
        const limit = this.client.snapshot?.clipboard.settings?.itemLimitBytes ?? 104857600;
        const source = this.native.Source.new(new GLib.Variant('a(sh)', formats), fds, Math.min(limit, 1024 * 1024 * 1024));
        // The C source is held by Mutter, so hiding/disabling the extension and
        // stopping the daemon cannot invalidate a later paste of this selection.
        this._source = source;
        this._selection.set_owner(Meta.SelectionType.SELECTION_CLIPBOARD, source);
        if (!paste) return {ok: true, copied: true};
        if (!target || !target.get_compositor_private()) return {ok: false, copied: true, error: {code: 'target-gone', message: '클립보드에 복사했습니다. 대상 앱에서 직접 붙여넣어 주세요.'}};
        closePanel();
        if (!stillCurrent()) return {ok: true, copied: true, pasted: false};
        target.activate(global.get_current_time());
        const focused = await this._waitForTarget(target, generation, stillCurrent);
        if (!focused) return {ok: false, copied: true, error: {code: 'focus-changed', message: '클립보드에 복사했습니다. 대상 앱에서 직접 붙여넣어 주세요.'}};
        const app = this._tracker.get_window_app(target);
        await this._sendPaste(isTerminal(app?.get_id() ?? '') || isTerminal(target.get_wm_class() ?? ''));
        return {ok: true, copied: true, pasted: true};
    }

    _waitForTarget(target, generation, stillCurrent) {
        return new Promise(resolve => {
            let attempts = 0;
            const timer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 20, () => {
                const valid = !this._destroyed && this._restoreGate.current(generation) && stillCurrent() && target.get_compositor_private();
                if (!valid || ++attempts >= 25) { this._pasteTimer = 0; resolve(false); return GLib.SOURCE_REMOVE; }
                const [, , modifiers] = global.get_pointer();
                const chordMask = Clutter.ModifierType.SHIFT_MASK | Clutter.ModifierType.CONTROL_MASK | Clutter.ModifierType.MOD1_MASK | Clutter.ModifierType.SUPER_MASK;
                if (global.display.focus_window === target && !(modifiers & chordMask)) {
                    this._pasteTimer = 0; resolve(true); return GLib.SOURCE_REMOVE;
                }
                return GLib.SOURCE_CONTINUE;
            });
            this._pasteTimer = timer;
        });
    }

    async _sendPaste(terminal) {
        // Ctrl+Shift+V is also our default global shortcut. Merely ignoring
        // its callback still swallows the key; release the compositor grabs
        // until it has dispatched the queued virtual key press/release events.
        const release = this._holdPasteBindings();
        try {
            const time = GLib.get_monotonic_time();
            const keys = [Clutter.KEY_Control_L, ...(terminal ? [Clutter.KEY_Shift_L] : []), Clutter.KEY_v];
            for (const key of keys) this._keyboard.notify_keyval(time, key, Clutter.KeyState.PRESSED);
            for (const key of keys.reverse()) this._keyboard.notify_keyval(time + 1, key, Clutter.KeyState.RELEASED);
            await new Promise(resolve => GLib.timeout_add(GLib.PRIORITY_DEFAULT, 100, () => {
                resolve();
                return GLib.SOURCE_REMOVE;
            }));
        } finally { release(); }
    }

    cancelPendingPaste() { this._restoreGate.next(); this._restoreCancel?.cancel(); }

    destroy() {
        this._destroyed = true;
        this._captureGate.next();
        this.cancelPendingPaste();
        this._captureCancel?.cancel();
        this._selection.disconnect(this._selectionSignal);
        this._keyboard = null;
        // Do not unset Mutter's selection owner. The native source owns its FDs.
        this._source = null;
    }
}
