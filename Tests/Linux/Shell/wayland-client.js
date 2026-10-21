// A real GTK/Wayland publisher and paste target used only on the private test bus.
import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const role = ARGV[0] || 'Source';
const terminal = ARGV[1] === 'terminal';
const app = new Gtk.Application({application_id:`io.griplabs.PersonalTools.Preview.${terminal ? 'ptyxis' : role}`,flags:Gio.ApplicationFlags.NON_UNIQUE});
const xml = `<node><interface name="io.griplabs.PersonalTools.Test1">
 <method name="Publish"><arg type="s" direction="in"/></method>
 <method name="Read"><arg type="s" direction="in"/><arg type="s" direction="out"/></method>
 <method name="Text"><arg type="s" direction="out"/></method>
 <method name="Clear"/><method name="Focus"/>
</interface></node>`;
let exported, owner, win, text;
app.connect('activate', () => {
    Gtk.Settings.get_default().gtk_application_prefer_dark_theme = true;
    win = new Gtk.ApplicationWindow({application:app,title:`Personal Tools Wayland ${role}`,default_width:640,default_height:320});
    text = new Gtk.TextView({left_margin:20,right_margin:20,top_margin:20,bottom_margin:20,wrap_mode:Gtk.WrapMode.WORD_CHAR});
    if (terminal) {
        // Match terminal key semantics on a real Wayland client: Ctrl+V is
        // not paste; Ctrl+Shift+V must make it past Mutter's global bindings.
        const keys = new Gtk.EventControllerKey();
        keys.set_propagation_phase(Gtk.PropagationPhase.CAPTURE);
        keys.connect('key-pressed', (_controller, key, _code, state) => {
            if ((key === Gdk.KEY_v || key === Gdk.KEY_V) && (state & Gdk.ModifierType.CONTROL_MASK)) {
                if (state & Gdk.ModifierType.SHIFT_MASK)
                    text.emit('paste-clipboard');
                return true;
            }
            return false;
        });
        text.add_controller(keys);
    }
    win.set_child(text); win.present(); text.grab_focus();
    const clipboard = Gdk.Display.get_default().get_clipboard();
    const api = {
        Publish(json) {
            const providers = Object.entries(JSON.parse(json)).map(([mime,base64]) => Gdk.ContentProvider.new_for_bytes(mime,new GLib.Bytes(GLib.base64_decode(base64))));
            clipboard.set_content(Gdk.ContentProvider.new_union(providers));
        },
        ReadAsync([mime], invocation) {
            clipboard.read_async([mime],GLib.PRIORITY_DEFAULT,null, (clip,result) => {
                try {
                    const [stream] = clip.read_finish(result);
                    const chunks = [];
                    const next = () => stream.read_bytes_async(4096,GLib.PRIORITY_DEFAULT,null,(input,res) => {
                        try {
                            const bytes = input.read_bytes_finish(res).get_data();
                            if(bytes.length) { chunks.push(...bytes); next(); }
                            else { stream.close(null); invocation.return_value(new GLib.Variant('(s)',[GLib.base64_encode(Uint8Array.from(chunks))])); }
                        } catch(error) { invocation.return_dbus_error('io.griplabs.PersonalTools.TestError',error.message); }
                    }); next();
                } catch(error) { invocation.return_dbus_error('io.griplabs.PersonalTools.TestError',error.message); }
            });
        },
        Text() { return text.buffer.text; },
        Clear() { text.buffer.text = ''; },
        Focus() { win.present(); text.grab_focus(); },
    };
    exported = Gio.DBusExportedObject.wrapJSObject(xml,api); exported.export(Gio.DBus.session,'/io/griplabs/PersonalTools/Test');
    owner = Gio.bus_own_name_on_connection(Gio.DBus.session,`io.griplabs.PersonalTools.Preview.${role}`,Gio.BusNameOwnerFlags.NONE,null,null);
});
app.run([]);
