// A private Wayland client for real keyboard dispatch through GTK's editable
// children. The compositor harness supplies an isolated output path.
import Gtk from 'gi://Gtk?version=4.0';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gdk from 'gi://Gdk?version=4.0';
import {installSearchShortcut} from '../../../Sources/Linux/App/keyboard.js';

const output = ARGV[0];
const app = new Gtk.Application({application_id: 'io.griplabs.PersonalTools.Preview.Target', flags: Gio.ApplicationFlags.NON_UNIQUE});
app.connect('activate', () => {
    const window = new Gtk.ApplicationWindow({application: app, title: 'Search shortcut input regression', default_width: 640, default_height: 480});
    const box = new Gtk.Box({orientation: Gtk.Orientation.VERTICAL, spacing: 16, margin_top: 24, margin_bottom: 24, margin_start: 24, margin_end: 24});
    const search = new Gtk.Entry({text: '검색어', placeholder_text: 'Search'});
    const editor = new Gtk.Entry({text: 'editable child'});
    const spin = new Gtk.SpinButton({adjustment: new Gtk.Adjustment({lower: 0, upper: 100, value: 50, step_increment: 1})});
    const card = new Gtk.Button({label: 'Clipboard card'});
    const text = new Gtk.TextView({vexpand: true});text.buffer.text = 'text view';
    const widgets = {search, editor, spin, card, text};
    for (const widget of Object.values(widgets)) box.append(widget);
    window.set_child(box);
    let activations = 0;
    installSearchShortcut(window, () => { activations++;search.grab_focus();search.select_region(0, -1); });
    // The test driver can deterministically focus real controls via F1-F5;
    // Ctrl+F itself still travels through the compositor and production helper.
    const setup = new Gtk.EventControllerKey({propagation_phase: Gtk.PropagationPhase.CAPTURE});
    setup.connect('key-pressed', (_controller, key) => {
        const widget = Object.values(widgets)[key - Gdk.KEY_F1];
        if (!widget) return false;
        widget.grab_focus();return true;
    });
    window.add_controller(setup);
    GLib.timeout_add(GLib.PRIORITY_DEFAULT, 30, () => {
        const focus = window.get_focus();
        const focused = Object.entries(widgets).find(([, widget]) => focus === widget || focus?.is_ancestor(widget))?.[0] ?? 'unknown';
        GLib.file_set_contents(output, JSON.stringify({activations, focused, selection: search.get_selection_bounds(), text: search.text, editor: editor.text}));
        return GLib.SOURCE_CONTINUE;
    });
    window.present();editor.grab_focus();
});
app.run([]);
