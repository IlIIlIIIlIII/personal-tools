import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';

export function installSearchShortcut(window, focusSearch) {
    // Handle the accelerator before editable children use Ctrl+F for cursor
    // movement or their input method consumes it. GTK's native trigger also
    // matches Caps Lock and alternate keyboard groups correctly.
    const controller = new Gtk.ShortcutController({propagation_phase: Gtk.PropagationPhase.CAPTURE});
    controller.add_shortcut(new Gtk.Shortcut({
        trigger: Gtk.KeyvalTrigger.new(Gdk.KEY_f, Gdk.ModifierType.CONTROL_MASK),
        action: Gtk.CallbackAction.new(() => { focusSearch(); return true; }),
    }));
    window.add_controller(controller);
    return controller;
}
