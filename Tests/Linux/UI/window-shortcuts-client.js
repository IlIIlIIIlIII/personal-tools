import Gtk from 'gi://Gtk?version=4.0';
import Adw from 'gi://Adw?version=1';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {WindowShortcutClient} from '../../../Sources/Linux/App/windowShortcuts.js';
import {windowsPage} from '../../../Sources/Linux/App/pages/windows.js';

const output = ARGV[0];
const app = new Adw.Application({application_id: 'io.griplabs.PersonalTools.Preview', flags: Gio.ApplicationFlags.NON_UNIQUE});
app.connect('activate', () => {
    let page;
    const labels = widget => {
        const result = widget instanceof Gtk.Label ? [widget.label] : [];
        for (let child = widget.get_first_child(); child; child = child.get_next_sibling()) result.push(...labels(child));
        return result;
    };
    const client = {windowShortcuts: new WindowShortcutClient(() => {
        page?.updateSnapshot();
        if (page) GLib.file_set_contents(output, JSON.stringify({state: client.windowShortcuts.state, labels: labels(page.widget)}));
    })};
    page = windowsPage({client, navigate: () => {}, fixture: false});
    const window = new Gtk.ApplicationWindow({application: app, title: 'Window slots live view', default_width: 1000, default_height: 900});
    window.set_child(page.widget); window.present();
    client.windowShortcuts.start();
});
app.run([]);
