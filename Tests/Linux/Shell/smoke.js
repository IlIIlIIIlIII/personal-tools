import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {automation,prepareEnvironment} from './automation.js';

export const METRICS = {};
export const {init,run} = automation(smoke,METRICS);
async function smoke() {
    await prepareEnvironment();
    Main.overview.hide();
    await Scripting.sleep(1500);
    const extension = Main.extensionManager.lookup('personal-tools@griplabs.io');
    if (!extension?.stateObj?._panel) throw new Error(`Extension did not initialize: ${JSON.stringify(extension?.error)}`);
    print(`PT_EXTENSION_STATE ${extension.state}`);
    extension.stateObj._panel.open();
    await Scripting.sleep(500);
    const output = GLib.getenv('PERSONALTOOLS_TEST_OUTPUT');
    const stream = Gio.File.new_for_path(`${output}/smoke.png`).replace(null, false, Gio.FileCreateFlags.REPLACE_DESTINATION, null);
    await new Shell.Screenshot().screenshot(false, stream);
    stream.close(null);
    print(`PT_SCREENSHOT ${global.stage.width}x${global.stage.height}`);
    extension.stateObj._panel.close();
}
