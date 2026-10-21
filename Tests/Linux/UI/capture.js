import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Shell from 'gi://Shell';
import Clutter from 'gi://Clutter';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {prepareEnvironment} from '../Shell/automation.js';

export const METRICS={};
let runPromise;
export function init(){
    GLib.timeout_add(GLib.PRIORITY_DEFAULT,2000,()=>{
        if(!runPromise)Scripting.runPerfScript({run,METRICS},GLib.getenv('SHELL_PERF_OUTPUT'));
        return GLib.SOURCE_REMOVE;
    });
}
export function run(){return runPromise??=capture();}
async function capture(){
    await prepareEnvironment();
    Main.overview.hide();
    Main.panel.hide();
    const root=GLib.getenv('PERSONALTOOLS_TEST_ROOT');
    const output=GLib.getenv('PERSONALTOOLS_TEST_OUTPUT');
    const scenes=(GLib.getenv('PERSONALTOOLS_UI_SCENES')||'01-library,04-detail,05-filter,06-clipboard-settings,07-sound,08-hdr,09-power,10-antigravity,11-vllm,12-diagnostics,13-settings,15-collections,16-empty').split(',');
    await Scripting.sleep(800);
    for(const scene of scenes){
        const launcher=new Gio.SubprocessLauncher({flags:Gio.SubprocessFlags.NONE});
        launcher.setenv('GDK_BACKEND','wayland',true);
        launcher.setenv('GSK_RENDERER','cairo',true);
        launcher.setenv('LANG','ko_KR.UTF-8',true);
        const child=launcher.spawnv(['gjs','-m',`${root}/Sources/Linux/App/app.js`,'--fixture','--screen',scene,'--theme',GLib.getenv('PERSONALTOOLS_UI_THEME')||GLib.getenv('PERSONALTOOLS_TEST_THEME')||'dark','--width',String(global.stage.width),'--height',String(global.stage.height)]);
        let win;
        for(let n=0;n<60;n++){
            win=global.get_window_actors().map(a=>a.meta_window).find(w=>w.get_pid()===child.get_identifier()*1&&!w.get_transient_for());
            if(win)break;await Scripting.sleep(100);
        }
        if(!win){child.force_exit();throw new Error(`UI did not open ${scene}`);}
        win.make_fullscreen();
        Main.activateWindow(win);
        await Scripting.sleep(1000);
        const stream=Gio.File.new_for_path(`${output}/${scene}.png`).replace(null,false,Gio.FileCreateFlags.REPLACE_DESTINATION,null);
        await new Shell.Screenshot().screenshot(false,stream);stream.close(null);
        const frame=win.get_frame_rect();print(`PT_UI_METRICS ${scene} ${JSON.stringify({stage:{width:global.stage.width,height:global.stage.height},window:{x:frame.x,y:frame.y,width:frame.width,height:frame.height},dialogs:global.get_window_actors().map(a=>a.meta_window).filter(w=>w.get_transient_for()===win).map(w=>{const r=w.get_frame_rect();return{x:r.x,y:r.y,width:r.width,height:r.height};})})}`);
        print(`PT_UI_CAPTURE ${scene} ${global.stage.width}x${global.stage.height}`);
        if (scene.includes('windows')) {
            const pointer = Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.POINTER_DEVICE);
            await Scripting.sleep(100);
            pointer.notify_absolute_motion(GLib.get_monotonic_time(), global.stage.width * 0.75, global.stage.height * 0.7);
            for (let step = 0; step < 20; step++) {
                pointer.notify_discrete_scroll(GLib.get_monotonic_time(), Clutter.ScrollDirection.DOWN, Clutter.ScrollSource.WHEEL);
                await Scripting.sleep(25);
            }
            await Scripting.sleep(300);
            const bottom = Gio.File.new_for_path(`${output}/${scene}-scrolled.png`).replace(null, false, Gio.FileCreateFlags.REPLACE_DESTINATION, null);
            await new Shell.Screenshot().screenshot(false, bottom); bottom.close(null);
            print(`PT_UI_SCROLL_CAPTURE ${scene} mouse-wheel scroll to footer`);
        }
        if(scene==='01-library'&&GLib.getenv('PERSONALTOOLS_UI_INTERACTIONS')==='1'){
            const seat=Clutter.get_default_backend().get_default_seat();
            const keyboard=seat.create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);
            const pointer=seat.create_virtual_device(Clutter.InputDeviceType.POINTER_DEVICE);
            const shot=async name=>{await Scripting.sleep(400);const file=Gio.File.new_for_path(`${output}/${name}.png`).replace(null,false,Gio.FileCreateFlags.REPLACE_DESTINATION,null);await new Shell.Screenshot().screenshot(false,file);file.close(null);print(`PT_UI_INTERACTION ${name}`);};
            Main.activateWindow(win);await Scripting.sleep(100);
            let now=GLib.get_monotonic_time();
            keyboard.notify_keyval(now,Clutter.KEY_Control_L,Clutter.KeyState.PRESSED);keyboard.notify_keyval(now+1,Clutter.KEY_f,Clutter.KeyState.PRESSED);keyboard.notify_keyval(now+2,Clutter.KEY_f,Clutter.KeyState.RELEASED);keyboard.notify_keyval(now+3,Clutter.KEY_Control_L,Clutter.KeyState.RELEASED);
            await shot('library-keyboard-focus');
            pointer.notify_absolute_motion(GLib.get_monotonic_time(),860,300);await shot('library-card-hover');
            pointer.notify_absolute_motion(GLib.get_monotonic_time(),1310,40);await Scripting.sleep(100);pointer.notify_button(GLib.get_monotonic_time(),Clutter.BUTTON_PRIMARY,Clutter.ButtonState.PRESSED);await Scripting.sleep(100);pointer.notify_button(GLib.get_monotonic_time(),Clutter.BUTTON_PRIMARY,Clutter.ButtonState.RELEASED);
            await shot('library-recording-paused');
        }
        if(scene==='05-filter'&&GLib.getenv('PERSONALTOOLS_UI_INTERACTIONS')==='1'){
            const pointer=Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.POINTER_DEVICE);
            const click=async(x,y)=>{pointer.notify_absolute_motion(GLib.get_monotonic_time(),x,y);await Scripting.sleep(100);pointer.notify_button(GLib.get_monotonic_time(),Clutter.BUTTON_PRIMARY,Clutter.ButtonState.PRESSED);await Scripting.sleep(100);pointer.notify_button(GLib.get_monotonic_time(),Clutter.BUTTON_PRIMARY,Clutter.ButtonState.RELEASED);await Scripting.sleep(400);};
            // Reopen through a real input serial; the fixture opens its initial
            // screenshot popup programmatically before any pointer event exists.
            await click(900,170);await click(1400,119);await click(1350,717);
            const file=Gio.File.new_for_path(`${output}/filter-applied.png`).replace(null,false,Gio.FileCreateFlags.REPLACE_DESTINATION,null);await new Shell.Screenshot().screenshot(false,file);file.close(null);print('PT_UI_INTERACTION filter-applied');
        }
        child.force_exit();await Scripting.sleep(150);
    }
}
