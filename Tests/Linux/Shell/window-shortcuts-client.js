// Multiple real windows owned by one GTK application, on the private test bus.
import Gtk from 'gi://Gtk?version=4.0';
import Gio from 'gi://Gio';
import Gdk from 'gi://Gdk?version=4.0';

const app = new Gtk.Application({application_id: 'io.griplabs.PersonalTools.Preview.Target', flags: Gio.ApplicationFlags.NON_UNIQUE});
app.connect('activate', () => {
    for (let index = 0; index < (ARGV[0] ? 1 : 9); index++) {
        const window = new Gtk.ApplicationWindow({application: app, title: ARGV[0] || `Window slot test ${index}`,
            default_width: 520, default_height: 280});
        window.set_child(new Gtk.Entry({text: '창 빠른 전환 테스트'}));
        const keys = new Gtk.EventControllerKey();
        keys.connect('key-pressed', (_controller, key) => {
            if (key !== Gdk.KEY_F2) return false;
            window.title += ' · 수정됨'; return true;
        });
        window.add_controller(keys);
        window.present();
    }
});
app.run([]);
