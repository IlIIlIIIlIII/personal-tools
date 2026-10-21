import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import St from 'gi://St';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';

// Automation scripts are imported while Shell is starting. The startup-complete
// signal can occur during that import, before Shell attaches its listener.
// Both entry paths share one promise so the recovery timer cannot run tests twice.
export function automation(body, metrics) {
    let pending;
    const run = () => pending ??= body();
    const init = () => GLib.timeout_add(GLib.PRIORITY_DEFAULT, 2000, () => {
        if (!pending) Scripting.runPerfScript({run, METRICS:metrics}, GLib.getenv('SHELL_PERF_OUTPUT'));
        return GLib.SOURCE_REMOVE;
    });
    return {run,init};
}

const displayCall = (method, args = null) => new Promise((resolve,reject) => Gio.DBus.session.call(
    'org.gnome.Mutter.DisplayConfig','/org/gnome/Mutter/DisplayConfig','org.gnome.Mutter.DisplayConfig',method,args,null,
    Gio.DBusCallFlags.NONE,10000,null,(connection,result) => {
        try { resolve(connection.call_finish(result).deepUnpack()); } catch (error) { reject(error); }
    }));

export async function prepareEnvironment() {
    const settings = new Gio.Settings({schema_id:'org.gnome.desktop.interface'});
    const theme = GLib.getenv('PERSONALTOOLS_TEST_THEME') || 'dark';
    const textScale = Number(GLib.getenv('PERSONALTOOLS_TEST_TEXT_SCALE') || '1');
    const scale = Number(GLib.getenv('PERSONALTOOLS_TEST_SCALE') || '1');
    const monitorScales = (GLib.getenv('PERSONALTOOLS_TEST_MONITOR_SCALES') || '').split(',').filter(Boolean).map(Number);
    settings.set_string('color-scheme', theme === 'light' ? 'prefer-light' : 'prefer-dark');
    settings.set_string('icon-theme','Yaru');
    settings.set_double('text-scaling-factor',textScale);
    if (scale !== 1 || monitorScales.length) {
        const [serial,monitors,logical,properties] = await displayCall('GetCurrentState');
        const config = logical.map(([x,y,,transform,primary,member],index) => [x,y,monitorScales[index] || scale,transform,primary,
            member.map(spec => {
                const monitor = monitors.find(item => item[0][0] === spec[0]);
                const mode = monitor[1].find(item => item[6]['is-current']?.deepUnpack());
                if (!mode) throw new Error(`No current mode for ${spec[0]}`);
                const desired = monitorScales[index] || scale;
                if (!mode[5].some(value => Math.abs(value-desired)<0.001)) throw new Error(`Scale ${desired} unsupported by ${spec[0]}`);
                return [spec[0],mode[0],{}];
            })]);
        await displayCall('ApplyMonitorsConfig',new GLib.Variant('(uua(iiduba(ssa{sv}))a{sv})',[serial,1,config,
            {'layout-mode':properties['layout-mode'] ?? new GLib.Variant('u',2)}]));
    }
    await Scripting.sleep(300);
    const [serial,monitors,logical] = await displayCall('GetCurrentState');
    const evidence = {theme,textScale,requestedScale:scale,stage:{width:global.stage.width,height:global.stage.height},
        shellScale:St.ThemeContext.get_for_stage(global.stage).scale_factor,
        monitors:logical.map(([x,y,scale,transform,primary]) => ({x,y,scale,transform,primary})),serial};
    const path = `${GLib.getenv('PERSONALTOOLS_TEST_OUTPUT')}/environment.json`;
    GLib.file_set_contents(path,JSON.stringify(evidence,null,2));
    print(`PT_ENVIRONMENT ${JSON.stringify(evidence)}`);
}
