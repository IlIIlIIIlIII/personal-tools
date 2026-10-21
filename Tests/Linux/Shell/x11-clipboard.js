import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {automation,prepareEnvironment} from './automation.js';

export const METRICS = {};
export const {init,run} = automation(x11Clipboard,METRICS);
function assert(condition,message) { if (!condition) throw new Error(message); }
async function waitFor(predicate,message) {
    for(let n=0;n<100;n++) { if(await predicate())return; await Scripting.sleep(50); }
    throw new Error(message);
}

// An Xwayland owner that lists a format but refuses it must not surface
// Mutter's "Format %s not supported" error, and its other formats are kept.
async function x11Clipboard() {
    await prepareEnvironment();
    Main.overview.hide(); await Scripting.sleep(400);
    const extension = Main.extensionManager.lookup('personal-tools@griplabs.io')?.stateObj;
    assert(extension?._bridge,'Native bridge not loaded');
    extension._client.destroy();
    const captures = [];
    const client = {
        snapshot:{clipboard:{paused:false,settings:{itemLimitBytes:104857600,excludedApps:[]}}},
        async capture(metadata,handles,fds) { captures.push({metadata,handles,fds}); return {ok:true}; },
        async execute() { return {}; }, destroy() {},
    };
    extension._client = client; extension._bridge.client = client; extension._panel.client = client;
    const errors = []; extension._bridge.onError = message => {errors.push(message);print(`PT_CAPTURE_ERROR ${message}`);};
    let owners = 0;
    const selection = global.display.get_selection();
    const ownerSignal = selection.connect('owner-changed',(_selection,type,owner) => {
        if (type !== Meta.SelectionType.SELECTION_CLIPBOARD || !owner) return;
        owners++; print(`PT_OWNER ${owner.get_mimetypes()}`);
    });
    const children = [];
    const text = 'Xwayland 거절 형식\n두 번째 줄';
    const own = async mode => {
        const before = owners;
        children.push(Gio.Subprocess.new(['/usr/bin/python3',`${GLib.getenv('PERSONALTOOLS_TEST_ROOT')}/Tests/Linux/Shell/x11-clipboard-owner.py`,mode,text],
            Gio.SubprocessFlags.NONE));
        await waitFor(() => owners > before,`X11 ${mode} owner did not take the clipboard`);
    };
    try {
        await own('partial');
        await waitFor(() => captures.length > 0 || errors.length > 0,'Partial X11 capture timed out');
        assert(errors.length === 0,`Refused format surfaced: ${errors}`);
        const formats = captures[0].metadata.mimes;
        const mimes = formats.map(format => format.mime);
        assert(!mimes.includes('text/plain;charset=UTF-8'),'Refused format was captured');
        assert(formats.find(format => format.mime === 'text/plain')?.length === new TextEncoder().encode(text).length,
            `text/plain was not kept: ${mimes}`);
        print(`PT_X11_REFUSED_FORMAT_SKIPPED_PASS ${mimes.join(',')}`);
        await own('refuse-all');
        await Scripting.sleep(1000);
        assert(errors.length === 0,`All-refused owner surfaced: ${errors}`);
        assert(captures.length === 1,'All-refused owner produced an entry');
        print('PT_X11_ALL_REFUSED_SILENT_PASS');
    } finally { selection.disconnect(ownerSignal); for(const child of children) child.force_exit(); }
}
