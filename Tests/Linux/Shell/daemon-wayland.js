import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Clutter from 'gi://Clutter';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {automation,prepareEnvironment} from './automation.js';

export const METRICS = {};
export const {init,run} = automation(integration,METRICS);
const assert = (condition,message) => { if (!condition) throw new Error(message); };
const encode = value => GLib.base64_encode(new TextEncoder().encode(value));
const decode = value => new TextDecoder().decode(GLib.base64_decode(value));
const call = (role,method,args = null) => new Promise((resolve,reject) => Gio.DBus.session.call(`io.griplabs.PersonalTools.Preview.${role}`,
    '/io/griplabs/PersonalTools/Test','io.griplabs.PersonalTools.Test1',method,args,null,Gio.DBusCallFlags.NONE,5000,null,
    (connection,result) => { try { resolve(connection.call_finish(result).deepUnpack()); } catch(error) { reject(error); } }));
// This connection survives disabling the extension and its own DaemonClient.
const daemonCall = (method,parameters = null) => new Promise((resolve,reject) => Gio.DBus.session.call('io.griplabs.PersonalTools',
    '/io/griplabs/PersonalTools','io.griplabs.PersonalTools1',method,parameters,null,Gio.DBusCallFlags.NO_AUTO_START,15000,null,
    (connection,result) => { try { resolve(JSON.parse(connection.call_finish(result).deepUnpack()[0])); } catch(error) { reject(error); } }));
async function waitFor(predicate,message) {
    for(let n=0;n<200;n++) { if(await predicate())return; await Scripting.sleep(50); }
    throw new Error(message);
}
async function capture(name) {
    const stream=Gio.File.new_for_path(`${GLib.getenv('PERSONALTOOLS_TEST_OUTPUT')}/${name}.png`).replace(null,false,Gio.FileCreateFlags.REPLACE_DESTINATION,null);
    await new Shell.Screenshot().screenshot(false,stream);stream.close(null);
}
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
async function integration() {
    await prepareEnvironment(); Main.overview.hide(); await Scripting.sleep(300);
    const extension=Main.extensionManager.lookup('personal-tools@griplabs.io')?.stateObj;
    assert(extension?._bridge,'Native clipboard bridge is unavailable');
    const root=GLib.getenv('PERSONALTOOLS_TEST_ROOT'),privateRoot=GLib.getenv('PERSONALTOOLS_TEST_PRIVATE_ROOT');
    const children=[],windows={},errors=[];
    extension._bridge.onError=message=>{errors.push(message);print(`PT_DAEMON_CAPTURE_ERROR ${message}`);};
    const spawnDaemon=()=>{
        const launcher=new Gio.SubprocessLauncher({flags:Gio.SubprocessFlags.NONE});
        for(const [key,dir] of Object.entries({XDG_CONFIG_HOME:'config',XDG_DATA_HOME:'data',XDG_STATE_HOME:'state',XDG_CACHE_HOME:'cache'}))
            launcher.setenv(key,`${privateRoot}/daemon/${dir}`,true);
        const child=launcher.spawnv([`${root}/build/linux/toolchain/dotnet/dotnet`,`${root}/Sources/Linux/Service/bin/Debug/net10.0/personal-tools-service.dll`]);
        children.push(child);return child;
    };
    let daemon;
    try {
        daemon=spawnDaemon();
        await waitFor(()=>extension._client.snapshot?.version===1,'Real daemon did not connect to Shell');
        assert(extension._client.snapshot.clipboard.entries.length===0,'Private archive was not empty');
        for(const role of ['Source','Target']) {
            const launcher=new Gio.SubprocessLauncher({flags:Gio.SubprocessFlags.NONE});launcher.setenv('GDK_BACKEND','wayland',true);
            const child=launcher.spawnv(['gjs','-m',`${root}/Tests/Linux/Shell/wayland-client.js`,role]);children.push(child);
            await waitFor(()=>{windows[role]=global.get_window_actors().map(actor=>actor.meta_window).find(win=>win.get_pid()===Number(child.get_identifier()));return windows[role];},`No ${role} Wayland window`);
            const targetX = Main.layoutManager.monitors.length>1 ? Main.layoutManager.monitors[1].x+60 : 740;
            windows[role].move_resize_frame(true,role==='Source'?40:targetX,80,600,320);
        }
        const plain='실제 .NET 서비스와 Wayland 복사\n보존된 두 번째 줄';
        const payloads={'text/plain;charset=utf-8':encode(plain),'text/plain':encode(plain),
            'text/html':encode('<p>실제 서비스 <b>다중 MIME</b></p>'),
            'image/png':'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAXpeqz8AAAAASUVORK5CYII='};
        await focus(windows.Source);await call('Source','Publish',new GLib.Variant('(s)',[JSON.stringify(payloads)]));
        await waitFor(()=>extension._client.snapshot?.clipboard.entries.length===1,'Real daemon did not archive multi-MIME selection');
        assert(errors.length===0,`Capture failed: ${errors}`);
        const id=extension._client.snapshot.clipboard.entries[0].id;
        await extension._bridge.put(id);await focus(windows.Target);
        for(const [mime,expected] of Object.entries(payloads)) assert((await call('Target','Read',new GLib.Variant('(s)',[mime])))[0]===expected,`Real FD restore changed ${mime}`);
        print('PT_REAL_DAEMON_MULTI_MIME_PASS text html image');
        await call('Target','Clear');await focus(windows.Target);extension._panel.open();
        const result=await extension._bridge.put(id,{paste:true,target:windows.Target,closePanel:()=>extension._panel.close({pasting:true})});
        assert(result.pasted,'Real daemon focus restore failed');
        await waitFor(async()=>(await call('Target','Text'))[0]===plain,'Original target did not receive paste');
        print('PT_REAL_DAEMON_FOCUS_PASTE_PASS');

        const originals=[`${privateRoot}/first.txt`,`${privateRoot}/second.txt`],contents=['first محفوظ 보관','second preserved'];
        originals.forEach((path,index)=>GLib.file_set_contents(path,contents[index]));
        const uris=originals.map(path=>GLib.filename_to_uri(path,null));
        await focus(windows.Source);await call('Source','Publish',new GLib.Variant('(s)',[JSON.stringify({
            'text/uri-list':encode(uris.join('\r\n')+'\r\n'),'x-special/gnome-copied-files':encode('cut\n'+uris.join('\n'))})]));
        await waitFor(()=>extension._client.snapshot?.clipboard.entries.length===2,'Real daemon did not archive two files');
        const filesId=extension._client.snapshot.clipboard.entries.find(item=>item.id!==id).id;
        originals.forEach(path=>Gio.File.new_for_path(path).delete(null));
        await extension._bridge.put(filesId);await focus(windows.Target);
        const restored=decode((await call('Target','Read',new GLib.Variant('(s)',['text/uri-list'])))[0]).trim().split(/\r?\n/);
        assert(restored.length===2,'Restored file count differs');
        restored.forEach((uri,index)=>{
            assert(!uris.includes(uri),'Restored original URI instead of preserved copy');
            const [,bytes]=Gio.File.new_for_uri(uri).load_contents(null);
            assert(new TextDecoder().decode(bytes)===contents[index],'Preserved file contents differ');
        });
        const copy=decode((await call('Target','Read',new GLib.Variant('(s)',['x-special/gnome-copied-files'])))[0]);
        assert(copy.startsWith('copy\n'),'Old cut operation was restored');
        print('PT_REAL_DAEMON_FILES_PASS preserved copies two files cut converted to copy');

        await extension._client.execute('clipboard','pause',{paused:true});await extension._client.refresh();
        await focus(windows.Source);await call('Source','Publish',new GLib.Variant('(s)',[JSON.stringify({'text/plain':encode('must not capture')})]));
        await Scripting.sleep(250);assert(extension._client.snapshot.clipboard.entries.length===2,'Paused collection changed');
        await extension._client.execute('clipboard','pause',{paused:false});
        daemon.force_exit();await waitFor(()=>!extension._client.owner,'Daemon name did not disappear');daemon=spawnDaemon();
        await waitFor(()=>extension._client.snapshot?.clipboard.entries.length===2,'Durable archive did not reconnect after restart');
        let settings=(await daemonCall('GetSnapshot')).settings;
        assert(settings.extensionAvailable && settings.extensionEnabled && settings.extensionCanChange,`Public GNOME extension status unavailable: ${JSON.stringify(settings)}`);
        const disable=await daemonCall('Execute',new GLib.Variant('(sss)',['settings','update',JSON.stringify({extensionEnabled:false})]));
        assert(disable.ok,`Public extension disable failed: ${JSON.stringify(disable.error)}`);
        settings=(await daemonCall('GetSnapshot')).settings;
        assert(!settings.extensionEnabled && settings.extensionState==='disabled' && !extension._panel,`Disabled extension state incorrect: ${JSON.stringify(settings)}`);
        const enable=await daemonCall('Execute',new GLib.Variant('(sss)',['settings','update',JSON.stringify({extensionEnabled:true})]));
        assert(enable.ok,`Public extension enable failed: ${JSON.stringify(enable.error)}`);
        settings=(await daemonCall('GetSnapshot')).settings;
        assert(settings.extensionEnabled && settings.extensionState==='enabled',`Enabled extension state incorrect: ${JSON.stringify(settings)}`);
        await waitFor(()=>extension._client.snapshot?.clipboard.entries.length===2,'Extension did not reconnect after enable');
        print('PT_REAL_DAEMON_EXTENSION_SETTINGS_PASS public GNOME disabled enabled state');
        await focus(windows.Target);
        const keyboard=Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);
        const chord=keys=>{
            const now=GLib.get_monotonic_time();
            keys.forEach(key=>keyboard.notify_keyval(now,key,Clutter.KeyState.PRESSED));
            [...keys].reverse().forEach(key=>keyboard.notify_keyval(now+1,key,Clutter.KeyState.RELEASED));
        };
        chord([Clutter.KEY_Control_L,Clutter.KEY_Shift_L,Clutter.KEY_v]);
        await waitFor(()=>extension._panel.opened,'Ctrl+Shift+V did not open clipboard');
        if(Main.layoutManager.monitors.length>1)assert(extension._panel._monitor===1,`Popup ignored pointer monitor: pointer=${global.get_pointer()}, panel=${extension._panel._monitor}, target=${JSON.stringify(windows.Target.get_frame_rect())}`);
        await Scripting.sleep(400);
        print(`PT_HOTKEY_PANEL ${JSON.stringify({entries:extension._panel.entries.length,cards:extension._panel._cards.length,snapshot:extension._client.snapshot.clipboard.entries.length,empty:extension._panel.empty.visible})}`);
        assert((await extension._client.execute('clipboard','query',{query:'',filter:{}})).entries.length===2,'Read-only archive query differs');
        await waitFor(()=>extension._panel._cards.length===2,'Hotkey panel did not load archive');
        chord([Clutter.KEY_Right]);await Scripting.sleep(60);
        assert(extension._panel._selected===1,'Horizontal keyboard navigation failed');
        chord([Clutter.KEY_Escape]);await waitFor(()=>!extension._panel.opened,'Escape did not close clipboard');
        print('PT_REAL_DAEMON_EXTENSION_RECONNECT_SHORTCUT_PASS Ctrl Shift V arrows Escape');
        await call('Target','Clear');await focus(windows.Target);
        chord([Clutter.KEY_Control_L,Clutter.KEY_Shift_L,Clutter.KEY_v]);
        await waitFor(()=>extension._panel.opened&&extension._panel._cards.length===2,'Card Enter panel did not load');
        await Scripting.sleep(250);
        const textIndex=extension._panel.entries.findIndex(entry=>entry.id===id);
        assert(textIndex>=0,'Text archive entry disappeared before card Enter');
        if(extension._panel._selected!==textIndex){
            chord([extension._panel._selected<textIndex?Clutter.KEY_Right:Clutter.KEY_Left]);
            await Scripting.sleep(60);
        }
        chord([Clutter.KEY_Return]);
        await waitFor(()=>!extension._panel.opened,'Card Return was consumed without closing/pasting');
        await waitFor(async()=>global.display.focus_window===windows.Target&&(await call('Target','Text'))[0]===plain,'Card Return did not paste into the original focused target');
        print('PT_REAL_DAEMON_CARD_ENTER_PASTE_PASS actual Return focus and clipboard');
        chord([Clutter.KEY_Control_L,Clutter.KEY_Shift_L,Clutter.KEY_v]);
        await waitFor(()=>extension._panel.opened,'Repeated hotkey did not open clipboard');
        chord([Clutter.KEY_Control_L,Clutter.KEY_Shift_L,Clutter.KEY_v]);
        await waitFor(()=>!extension._panel.opened,'Repeated hotkey did not close clipboard');
        const pointer=pointerDevice();
        const clickAt=async(x,y)=>{
            pointer.notify_absolute_motion(GLib.get_monotonic_time(),x,y);await Scripting.sleep(70);
            pointer.notify_button(GLib.get_monotonic_time(),Clutter.BUTTON_PRIMARY,Clutter.ButtonState.PRESSED);await Scripting.sleep(70);
            pointer.notify_button(GLib.get_monotonic_time(),Clutter.BUTTON_PRIMARY,Clutter.ButtonState.RELEASED);await Scripting.sleep(70);
        };
        const clickActor=async actor=>{
            const [x,y]=actor.get_transformed_position(),[width,height]=actor.get_transformed_size();
            await clickAt(x+width/2,y+height/2);
        };
        const findAction=(actor,name)=>actor.accessible_name===name?actor:actor.get_children().map(child=>findAction(child,name)).find(Boolean);
        chord([Clutter.KEY_Control_L,Clutter.KEY_Shift_L,Clutter.KEY_v]);
        await waitFor(()=>extension._panel.opened,'Filter interaction test did not open clipboard');
        await Scripting.sleep(200);await clickActor(extension._panel.filterButton);
        await waitFor(()=>extension._panel._popover,'Real filter-button click did not open popover');
        await Scripting.sleep(100);
        await capture('filter-popover-open');
        await clickActor(findAction(extension._panel._popover,'이미지'));
        await capture('filter-image-selected');
        await clickActor(findAction(extension._panel._popover,'적용'));
        await waitFor(()=>!extension._panel._popover && extension._panel.entries.length===1,'Real filter controls did not apply');
        assert(extension._panel.filters.formats.includes('image'),'Image filter selection was lost');
        await clickActor(extension._panel.filterButton);await waitFor(()=>extension._panel._popover,'Reset filter popover did not open');
        await Scripting.sleep(100);await clickActor(findAction(extension._panel._popover,'초기화'));
        await waitFor(()=>!extension._panel._popover && extension._panel.entries.length===2,'Real reset filter did not restore archive');
        await clickActor(extension._panel.filterButton);await waitFor(()=>extension._panel._popover,'Escape popover did not open');
        chord([Clutter.KEY_Escape]);await waitFor(()=>!extension._panel._popover,'Escape did not close nested popover');
        assert(extension._panel.opened,'Popover Escape also closed main panel');
        print('PT_REAL_DAEMON_FILTER_POINTER_PASS real primary-button open select apply reset and nested Escape');
        chord([Clutter.KEY_Escape]);await waitFor(()=>!extension._panel.opened,'Panel did not close before outside-click test');
        const targetRect=windows.Target.get_frame_rect();
        let outsidePress=false;
        const actorClick=extension._panel.actor.connect('captured-event',(_actor,event)=>{
            if(event.type()===Clutter.EventType.BUTTON_PRESS && event.get_button()===Clutter.BUTTON_PRIMARY)outsidePress=true;
            return Clutter.EVENT_PROPAGATE;
        });
        try {
            chord([Clutter.KEY_Control_L,Clutter.KEY_Shift_L,Clutter.KEY_v]);
            await waitFor(()=>extension._panel.opened,'Outside-click test did not open clipboard');
            await clickAt(targetRect.x+100,targetRect.y+70);
            await waitFor(()=>outsidePress,'Real primary-button event was not observed');
            assert(!extension._panel.opened,`Real primary-button outside click did not dismiss clipboard; pointer=${global.get_pointer()}`);
        } finally { extension._panel.actor.disconnect(actorClick); }
        print('PT_REAL_DAEMON_OUTSIDE_CLICK_PASS primary button event observed; repeated hotkey and outside click close');
        await capture('outside-click-dismissed');
        await extension._bridge.put(id);await focus(windows.Target);
        assert(decode((await call('Target','Read',new GLib.Variant('(s)',['text/plain'])))[0])===plain,'Restart lost MIME payload');
        print('PT_REAL_DAEMON_RESTART_PASS durable archive reconnect');
        daemon.force_exit();await waitFor(()=>!extension._client.owner,'Final daemon stop failed');
        extension._bridge.destroy();extension._bridge=null;
        assert(decode((await call('Target','Read',new GLib.Variant('(s)',['text/plain'])))[0])===plain,'Selection did not outlive daemon and bridge');
        print('PT_REAL_DAEMON_SOURCE_LIFETIME_PASS');
    } finally { for(const child of children)try{child.force_exit();}catch{} }
}
