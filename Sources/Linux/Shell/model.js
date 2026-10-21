// Pure state and geometry; also exercised without an active desktop session.
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

export const MIME_ALLOWLIST = Object.freeze([
    'text/plain;charset=utf-8', 'text/plain;charset=UTF-8', 'UTF8_STRING',
    'text/plain', 'text/html', 'image/png', 'image/jpeg',
    'text/uri-list', 'x-special/gnome-copied-files',
]);

export function panelBounds(area, tokens = {}) {
    const margin = Math.min(tokens.panelMargin ?? 24, Math.max(0, area.width / 8), Math.max(0, area.height / 8));
    const height = Math.max(1, Math.min(tokens.panelHeight ?? 400, area.height - margin * 2));
    return {x: area.x + margin, y: area.y + area.height - height - margin,
        width: Math.max(1, area.width - margin * 2), height};
}

export function monitorAtPoint(monitors, x, y, fallback = 0) {
    const found = monitors.findIndex(m => x >= m.x && x < m.x + m.width && y >= m.y && y < m.y + m.height);
    return found >= 0 ? found : Math.max(0, Math.min(fallback, monitors.length - 1));
}

export function allowedMimes(mimes) {
    // Prefer the lossless offered image; retain all text/file MIME variants.
    const offered = new Set(mimes);
    return MIME_ALLOWLIST.filter(mime => offered.has(mime) && !(mime === 'image/jpeg' && offered.has('image/png')));
}

export function refusedFormat(error) {
    // Xwayland owners may list a target yet answer its conversion with no
    // property; Mutter reports that as NOT_FOUND ("Format %s not supported").
    return error instanceof GLib.Error && error.matches(Gio.IOErrorEnum, Gio.IOErrorEnum.NOT_FOUND);
}

export function isExcluded(app, exclusions = []) {
    if (!app) return false;
    const normalized = app.toLocaleLowerCase();
    const sensitive = ['1password', 'bitwarden', 'keepass', 'org.gnome.seahorse'];
    return sensitive.some(name => normalized.includes(name)) || exclusions.some(name =>
        normalized === String(name).toLocaleLowerCase() || normalized.replace(/\.desktop$/, '') === String(name).toLocaleLowerCase().replace(/\.desktop$/, ''));
}

export function isTerminal(appId = '') {
    return /(^|[.\-/])(gnome-terminal|ptyxis|kgx|konsole|kitty|alacritty|wezterm|foot|tilix|terminator)([.\-/]|$)/i.test(appId);
}

export function shortcutValid(value) {
    return typeof value === 'string' && value.length <= 100 && /^(?:<(?:Super|Control|Ctrl|Shift|Alt)>)+[a-zA-Z0-9]$/.test(value);
}

export function shortcutLabel(value, fallback='Ctrl + Shift + V') {
    if(!shortcutValid(value))return fallback;
    const modifiers=[...value.matchAll(/<([^>]+)>/g)].map(match=>match[1].replace('Control','Ctrl'));
    return [...modifiers,value.at(-1).toUpperCase()].join(' + ');
}

export function scopePayload(category, filter = {}) {
    return {...filter, ...(category === '*' ? {pinnedOnly: true} : category ? {categoryId: category} : {})};
}

export function parseJson(value, limit = 1024 * 1024) {
    if (typeof value !== 'string' || value.length > limit) throw new Error('응답 크기가 허용 범위를 넘었습니다.');
    const result = JSON.parse(value);
    if (!result || typeof result !== 'object') throw new Error('응답 형식이 올바르지 않습니다.');
    return result;
}

export class RevisionGate {
    constructor() { this.value = 0; }
    next() { return ++this.value; }
    current(revision) { return revision === this.value; }
}
