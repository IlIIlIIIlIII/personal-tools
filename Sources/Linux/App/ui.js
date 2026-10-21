import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Pango from 'gi://Pango';

const interfaceSettings=new Gio.Settings({schema_id:'org.gnome.desktop.interface'});
export function textScale(){return Math.max(0.8,Math.min(3,interfaceSettings.get_double('text-scaling-factor')));}
export const ROOT = Gio.File.new_for_uri(import.meta.url).get_parent().get_path();
export const DESIGN=JSON.parse(new TextDecoder().decode(Gio.File.new_for_path(`${ROOT}/../Design/tokens.json`).load_contents(null)[1]));
export function box(vertical = false, spacing = 12, css = '', children = []) {
    const widget = new Gtk.Box({orientation: vertical ? Gtk.Orientation.VERTICAL : Gtk.Orientation.HORIZONTAL, spacing});
    if (css) css.split(' ').filter(Boolean).forEach(c => widget.add_css_class(c));
    children.filter(Boolean).forEach(child => widget.append(child));
    return widget;
}
export function label(text = '', css = '', options = {}) {
    const widget = new Gtk.Label({label: String(text), xalign: 0, ...options});
    css.split(' ').filter(Boolean).forEach(c => widget.add_css_class(c));
    const metrics=DESIGN.type[(css.includes('page-title')||css.includes('empty-title'))?'page':(css.includes('section-title')||css.includes('dialog-title'))?'section':css.includes('caption')?'caption':'body'].lineHeight;
    const attrs=new Pango.AttrList();attrs.insert(Pango.attr_line_height_new_absolute(metrics*textScale()*Pango.SCALE));attrs.insert(Pango.attr_language_new(Pango.Language.from_string('ko-KR')));widget.attributes=attrs;
    return widget;
}
export function text(textValue, css = '', lines = 0) {
    const widget = label(textValue, css, {wrap: true, wrap_mode: Pango.WrapMode.WORD_CHAR, hexpand: true});
    if (lines) { widget.set_lines(lines); widget.set_ellipsize(Pango.EllipsizeMode.END); }
    return widget;
}
export function icon(name, size = 24) { return new Gtk.Image({icon_name: name, pixel_size: size}); }
export function button(title, iconName, callback, css = '') {
    const widget = new Gtk.Button({tooltip_text: title || '',valign:Gtk.Align.CENTER});
    const content = box(false, 10, '', [iconName ? icon(iconName) : null, title ? label(title) : null]);
    content.halign = Gtk.Align.CENTER;
    widget.set_child(content);
    css.split(' ').filter(Boolean).forEach(c => widget.add_css_class(c));
    if (!title) widget.add_css_class('icon-button');
    if (callback) widget.connect('clicked', callback);
    return widget;
}
export function iconButton(name, tooltip, callback, css = '') {
    const widget = button('', name, callback, css); widget.tooltip_text = tooltip; return widget;
}
export function entry(value = '', placeholder = '', css = '') {
    const widget = new Gtk.Entry({text: String(value), placeholder_text: placeholder, hexpand: true,valign:Gtk.Align.CENTER});
    css.split(' ').filter(Boolean).forEach(c => widget.add_css_class(c));
    return widget;
}
export function toggle(active, callback) {
    const widget = new Gtk.Switch({active: Boolean(active), valign: Gtk.Align.CENTER, halign: Gtk.Align.END});
    if (callback) widget.connect('notify::active', () => callback(widget.active));
    return widget;
}
export function dropdown(items, selected = 0, callback) {
    const widget = Gtk.DropDown.new_from_strings(items.length ? items : ['사용 가능한 항목 없음']);
    widget.valign=Gtk.Align.CENTER;
    widget.selected = Math.max(0, selected); widget.hexpand = true;
    widget.sensitive = items.length > 0;
    if (callback) widget.connect('notify::selected', () => callback(widget.selected));
    return widget;
}
export function number(value, min, max, step = 1, digits = 0, callback) {
    const widget = new Gtk.SpinButton({adjustment: new Gtk.Adjustment({value, lower: min, upper: max, step_increment: step, page_increment: step * 10}), digits, numeric: true});
    widget.value=value;widget.valign=Gtk.Align.CENTER;
    if (callback) widget.connect('value-changed', () => callback(widget.value));
    return widget;
}
export function spacer(vertical = false) { return new Gtk.Box(vertical ? {vexpand: true} : {hexpand: true}); }
export function separator() { return new Gtk.Separator({orientation: Gtk.Orientation.HORIZONTAL, css_classes: ['separator']}); }
export function group(children = [], padded = false) { return box(true, 0, padded ? 'group group-pad' : 'group', children); }
export function heading(title, caption = '', trailing = null, iconName = '') {
    const titleBox = box(false, 14, '', [iconName ? icon(iconName, 28) : null, label(title, 'section-title')]);
    const content = box(true, 8, '', [titleBox, caption ? text(caption, 'caption') : null]);
    content.hexpand = true;
    return box(false, 20, '', [content, trailing]);
}
export function preference(title, subtitle = '', control = null, iconName = '') {
    const info = box(true, 4, '', [label(title), subtitle ? text(subtitle, 'caption') : null]); info.hexpand = true;info.valign=Gtk.Align.CENTER;
    if(control)control.valign=Gtk.Align.CENTER;
    return box(false, 18, 'preference-row', [iconName ? icon(iconName) : null, info, control]);
}
export function scroller(child, horizontal = false) {
    const widget = new Gtk.ScrolledWindow({hexpand: true, vexpand: true, hscrollbar_policy: horizontal ? Gtk.PolicyType.AUTOMATIC : Gtk.PolicyType.NEVER, vscrollbar_policy: horizontal ? Gtk.PolicyType.NEVER : Gtk.PolicyType.AUTOMATIC});
    widget.set_child(child); return widget;
}
export function segmented(items, selected, callback) {
    const widget = box(false, 0, 'segmented');widget.valign=Gtk.Align.CENTER;
    items.forEach(item => {
        const b = button(item.title, null, () => {
            for (let c = widget.get_first_child(); c; c = c.get_next_sibling()) c.remove_css_class('selected');
            b.add_css_class('selected'); callback(item.id);
        });
        if (item.id === selected) b.add_css_class('selected'); widget.append(b);
    });
    widget.halign = Gtk.Align.START; return widget;
}
export function picture(uri, height = 150) {
    const widget = new Gtk.Picture({can_shrink: true, content_fit: Gtk.ContentFit.COVER, height_request: height, hexpand: true, css_classes: ['thumbnail']});
    if (uri) widget.set_file(Gio.File.new_for_uri(uri.includes('://') ? uri : GLib.filename_to_uri(uri, null)));
    return widget;
}
export function status(value, title) {
    const widget = box(false, 8, '', [icon(value === 'error' ? 'dialog-warning-symbolic' : 'media-record-symbolic', 12), label(title || value, 'caption')]);
    widget.get_first_child().add_css_class(value === 'error' ? 'error' : 'accent'); return widget;
}
export function shortcutKeys(value, fallback = '<Control><Shift>v') { return (value || fallback).replace(/</g, '').replace(/>/g, ' + ').split(' + ').filter(Boolean).map(key => key === 'Control' || key === 'Primary' ? 'Ctrl' : key.length === 1 ? key.toUpperCase() : key); }
export function keycaps(keys) { const children=[];keys.forEach((key,i)=>{if(i)children.push(label('+','caption'));children.push(label(key,'keycap'));});const result=box(false,8,'',children);result.valign=Gtk.Align.CENTER;return result; }
export function clear(widget) { let child; while ((child = widget.get_first_child())) widget.remove(child); }
export function openUri(uri) { Gio.AppInfo.launch_default_for_uri_async(uri, null, null, (_o, result) => { try { Gio.AppInfo.launch_default_for_uri_finish(result); } catch (error) { console.error(error.message); } }); }
export function fileChooser(parent, title, onChosen, folder = false) {
    const dialog = new Gtk.FileDialog({title});
    const done = (_o, result) => { try { const file = folder ? dialog.select_folder_finish(result) : dialog.open_finish(result); onChosen(file.get_path()); } catch (error) { if (!error.message.includes('dismissed')) console.error(error.message); } };
    if (folder) dialog.select_folder(parent, null, done); else dialog.open(parent, null, done);
}
export function modal(parent, title, content, actions = [], width = 640, height = -1) {
    const window = new Gtk.Window({title, application:parent.get_application(),transient_for: parent, modal: true, default_width: width, resizable: true, css_classes: ['pt-app']});
    if (height > 0) window.default_height = height;
    window.set_titlebar(new Gtk.Box({visible:false}));
    const close = iconButton('window-close-symbolic', '닫기', () => window.close(), 'flat');
    const header = box(false, 16, 'dialog-header', [label(title, 'dialog-title'), spacer(), close]);
    const viewport=scroller(content);viewport.propagate_natural_height=true;viewport.max_content_height=Math.max(240,parent.get_height()-240);
    const body = box(true, 18, 'dialog-content', [viewport]); body.vexpand = true;
    if(actions.length===1&&actions[0].has_css_class('detail-footer'))body.append(actions[0]);
    else if (actions.length) body.append(box(false, 12, 'dialog-actions', [spacer(), ...actions]));
    window.set_child(box(true, 0, '', [header, body]));
    const keys = new Gtk.EventControllerKey(); keys.connect('key-pressed', (_c, key) => { if (key === Gdk.KEY_Escape) {window.close(); return true;} return false; }); window.add_controller(keys);
    window.present(); return window;
}
export function formatTime(value, seconds=false) { const date=new Date(value);if(!Number.isFinite(date.getTime()))return '미확인';return date.toLocaleTimeString('en-GB',{hour:'2-digit',minute:'2-digit',...(seconds?{second:'2-digit'}:{}),hourCycle:'h23'}); }
export function table(columns, rows, widths = []) {
    const root = group();
    const make = (cells, header) => {
        const row = box(false, 20, header ? 'table-head' : 'table-row');
        cells.forEach((cell, index) => { const c = typeof cell === 'string' ? text(cell, header ? 'caption' : '') : cell; c.hexpand = !widths[index]; if (widths[index]) c.width_request = widths[index]; row.append(c); });
        return row;
    };
    root.append(make(columns, true)); rows.forEach(row => root.append(make(row, false))); return root;
}
