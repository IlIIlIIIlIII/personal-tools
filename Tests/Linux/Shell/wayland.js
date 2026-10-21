import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Clutter from 'gi://Clutter';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {automation,prepareEnvironment} from './automation.js';

export const METRICS = {};
export const {init,run} = automation(wayland,METRICS);
function assert(condition,message) { if (!condition) throw new Error(message); }
const call = (role,method,args = null) => new Promise((resolve,reject) => Gio.DBus.session.call(`io.griplabs.PersonalTools.Preview.${role}`,
    '/io/griplabs/PersonalTools/Test','io.griplabs.PersonalTools.Test1',method,args,null,Gio.DBusCallFlags.NONE,5000,null,
    (connection,result) => { try { resolve(connection.call_finish(result).deepUnpack()); } catch(error) { reject(error); } }));
async function waitFor(predicate,message) {
    for(let n=0;n<100;n++) { if(await predicate())return; await Scripting.sleep(50); }
    throw new Error(message);
}
const encode = value => GLib.base64_encode(new TextEncoder().encode(value));
// Keep the virtual pointer alive for the whole compositor test. Destroying it
// between focus and a hotkey can restore the seat's previous pointer position.
let testPointer;
function pointerDevice() {
    return testPointer ??= Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.POINTER_DEVICE);
}
async function focus(win) {
    win.raise(); win.activate(global.display.get_current_time_roundtrip());
    await Scripting.sleep(200);
    const pointer=pointerDevice(),rect=win.get_frame_rect();
    pointer.notify_absolute_motion(GLib.get_monotonic_time(),rect.x+100,rect.y+100);
    await Scripting.sleep(100);
    pointer.notify_button(GLib.get_monotonic_time(),Clutter.BUTTON_PRIMARY,Clutter.ButtonState.PRESSED);
    await Scripting.sleep(50);
    pointer.notify_button(GLib.get_monotonic_time(),Clutter.BUTTON_PRIMARY,Clutter.ButtonState.RELEASED);
    await Scripting.sleep(150);
    if(global.display.focus_window!==win){win.raise();win.activate(global.display.get_current_time_roundtrip());await Scripting.sleep(200);}
    assert(global.display.focus_window===win,`Could not focus ${win.title}; active=${global.display.focus_window?.title}; pointer=${global.get_pointer()}; frame=${JSON.stringify(rect)}`);
}

async function wayland() {
    await prepareEnvironment();
    Main.overview.hide(); await Scripting.sleep(400);
    const extension = Main.extensionManager.lookup('personal-tools@griplabs.io')?.stateObj;
    assert(extension?._bridge,'Native bridge not loaded');
    extension._client.destroy();
    const captures = [];
    const client = {
        snapshot:{clipboard:{paused:false,settings:{itemLimitBytes:104857600,excludedApps:[]}}},
        async capture(metadata,handles,fds) { captures.push({metadata,handles,fds}); return {ok:true}; },
        async openClipboard() { const value=captures.at(-1); return {...value,metadata:{...value.metadata,entryId:'test'}}; },
        async execute() { return {}; }, destroy() {},
    };
    extension._client = client; extension._bridge.client = client; extension._panel.client = client;
    const errors = []; extension._bridge.onError = message => {errors.push(message);print(`PT_CAPTURE_ERROR ${message}`);};
    const ownerSignal = global.display.get_selection().connect('owner-changed',(_selection,type,owner)=>print(`PT_OWNER ${type} ${owner?.get_mimetypes()} focus=${global.display.focus_window?.title}`));
    const children = [], windows = {};
    const root = GLib.getenv('PERSONALTOOLS_TEST_ROOT');
    try {
        for(const role of ['Source','Target']) {
            const launcher=new Gio.SubprocessLauncher({flags:Gio.SubprocessFlags.NONE});
            launcher.setenv('GDK_BACKEND','wayland',true);
            const child=launcher.spawnv(['gjs','-m',`${root}/Tests/Linux/Shell/wayland-client.js`,role]); children.push(child);
            await waitFor(() => {windows[role]=global.get_window_actors().map(actor=>actor.meta_window).find(win=>win.get_pid()===Number(child.get_identifier()));return windows[role];},`No ${role} Wayland window`);
            windows[role].move_resize_frame(true,role==='Source'?40:740,80,600,320);
        }
        const plain='Wayland 복사와 붙여넣기\n두 번째 줄';
        const payloads={
            'text/plain;charset=utf-8':encode(plain),
            'text/plain':encode(plain),
            'text/html':encode('<p>Wayland <b>복사와 붙여넣기</b></p>'),
            'text/uri-list':encode('file:///tmp/personal-tools-fixture-a.txt\r\nfile:///tmp/personal-tools-fixture-b.png\r\n'),
            'image/png':'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAXpeqz8AAAAASUVORK5CYII=',
        };
        await focus(windows.Source);
        const keyboard=Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);
        const eventTime=GLib.get_monotonic_time();keyboard.notify_keyval(eventTime,Clutter.KEY_space,Clutter.KeyState.PRESSED);keyboard.notify_keyval(eventTime+1,Clutter.KEY_space,Clutter.KeyState.RELEASED);
        await Scripting.sleep(100);
        print(`PT_SOURCE_FOCUS ${global.display.focus_window?.title} ${windows.Source.get_client_type()}`);
        await call('Source','Publish',new GLib.Variant('(s)',[JSON.stringify(payloads)]));
        await waitFor(() => captures.length>0,'Wayland MIME capture timed out');
        assert(errors.length===0,`Capture failed: ${errors}`);
        assert(captures[0].metadata.mimes.length===5,`Expected 5 captured MIME types, got ${captures[0].metadata.mimes.length}`);
        assert(captures[0].metadata.sourceAttribution!=='exact','Foreground app must not be misreported as exact writer');
        await extension._bridge.put('test');
        await focus(windows.Target);
        for(const [mime,expected] of Object.entries(payloads)) {
            const [actual]=await call('Target','Read',new GLib.Variant('(s)',[mime]));
            assert(actual===expected,`Restored ${mime} differs`);
        }
        print('PT_WAYLAND_MULTI_MIME_PASS text html image two-file-uri-list');
        await call('Target','Clear'); await focus(windows.Target);
        extension._panel.open();
        const result=await extension._bridge.put('test',{paste:true,target:windows.Target,closePanel:()=>extension._panel.close({pasting:true})});
        assert(result.pasted,'Native focus restore did not paste');
        await waitFor(async()=> (await call('Target','Text'))[0]===plain,'Ctrl+V did not reach the original Wayland app');
        print('PT_WAYLAND_FOCUS_PASTE_PASS');
        const count=captures.length;
        client.snapshot.clipboard.paused=true;
        await focus(windows.Source);
        await call('Source','Publish',new GLib.Variant('(s)',[JSON.stringify({'text/plain':encode('paused capture')})]));
        await Scripting.sleep(250);assert(captures.length===count,'Paused clipboard captured data');
        client.snapshot.clipboard.paused=false;
        client.openClipboard=async()=>{await Scripting.sleep(100);return {...captures[0],metadata:{...captures[0].metadata,entryId:'test'}};};
        const pending=extension._bridge.put('test');extension._bridge.cancelPendingPaste();
        let canceled=false;try{await pending;}catch{canceled=true;}assert(canceled,'Canceled restore succeeded');
        const gone=await extension._bridge.put('test',{paste:true,target:null});assert(gone.copied&&!gone.pasted&&gone.error.code==='target-gone','Missing target did not produce copy-only result');
        print('PT_WAYLAND_FAILURE_PASS paused capture canceled restore missing target');
        extension._bridge.destroy();extension._bridge=null;
        await focus(windows.Target);
        const [survived]=await call('Target','Read',new GLib.Variant('(s)',['text/plain']));
        assert(survived===payloads['text/plain'],'Native source did not survive bridge shutdown');
        print('PT_WAYLAND_SOURCE_LIFETIME_PASS');
    } finally { global.display.get_selection().disconnect(ownerSignal);for(const child of children) child.force_exit(); }
}
