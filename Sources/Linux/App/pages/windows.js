import Gtk from 'gi://Gtk?version=4.0';
import Pango from 'gi://Pango';
import {box, label, text, heading, group, keycaps, scroller, button} from '../ui.js';
import {WINDOW_SLOT_KEYS} from '../windowShortcuts.js';

export function windowsPage(ctx) {
    const body = box(true, 20, 'page-content');
    const count = label('', 'caption');
    body.append(heading('등록된 창', '현재 로그인 세션의 창을 최대 8개까지 등록합니다.', count));
    const help = text('등록할 창에서 Super+Shift+슬롯 키를 누르세요. Super+슬롯 키로 해당 창으로 전환합니다.', 'caption');
    body.append(help);
    const noticeText = text('', 'caption');
    const retry = button('다시 확인', 'view-refresh-symbolic', () => ctx.client.windowShortcuts.refresh());
    const settings = button('설정 열기', 'emblem-system-symbolic', () => ctx.navigate('settings'));
    const notice = box(true, 12, 'inset', [noticeText, box(false, 12, '', [retry, settings])]);
    body.append(notice);
    const rows = WINDOW_SLOT_KEYS.map(key => {
        const title = label('', '', {hexpand: true, ellipsize: Pango.EllipsizeMode.END});
        const detail = label('', 'caption', {hexpand: true, ellipsize: Pango.EllipsizeMode.END});
        const info = box(true, 4, '', [title, detail]); info.hexpand = true; info.valign = Gtk.Align.CENTER;
        const shortcut = keycaps(['Super', key.toUpperCase()]); shortcut.width_request = 154;
        const row = box(false, 24, 'preference-row', [shortcut, info]);
        return {key, title, detail, row};
    });
    body.append(group(rows.map(row => row.row)));
    body.append(text('같은 슬롯에 다시 등록하면 새 창으로 바뀝니다. 창을 닫으면 등록이 해제되고, 로그아웃하면 모든 슬롯이 비워집니다.', 'caption'));
    if (ctx.fixture) body.append(label('디자인 미리보기 · 예시 등록 상태', 'caption'));
    const updateSnapshot = () => {
        const state = ctx.client.windowShortcuts.state;
        const connected = state.state === 'connected';
        const ready = connected && state.ready;
        count.label = connected ? `${state.registered || 0} / 8 등록` : '상태 확인 필요';
        notice.visible = !ready;
        retry.visible = state.state !== 'connecting';
        settings.visible = state.state === 'unavailable';
        noticeText.label = state.state === 'outdated' ? '새 창 전환 기능을 불러오려면 로그아웃 후 다시 로그인해 주세요.' :
            state.state === 'connecting' ? '등록된 창을 확인하고 있습니다.' :
            !connected ? 'GNOME 확장에 연결할 수 없습니다. 설정에서 데스크톱 확장을 켜 주세요.' :
            state.error || '창 전환 단축키를 준비하고 있습니다.';
        noticeText.tooltip_text = state.error || null;
        for (const row of rows) {
            const slot = state.slots?.find(slot => slot.key === row.key);
            row.title.label = !connected ? '확인할 수 없음' : slot?.registered ? slot.title || '제목 없는 창' : '미등록';
            row.title.tooltip_text = slot?.registered ? row.title.label : null;
            row.detail.label = !connected ? '연결되면 등록 상태가 표시됩니다.' : slot?.registered ? slot.appName || '창 등록됨' : `Super+Shift+${row.key.toUpperCase()}로 등록`;
            row.detail.tooltip_text = slot?.registered ? row.detail.label : null;
        }
        return true;
    };
    updateSnapshot();
    return {widget: scroller(body), updateSnapshot};
}
