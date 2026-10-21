import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GioUnix from 'gi://GioUnix';
import Pango from 'gi://Pango';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {shortcutLabel} from './model.js';

const brandIcon = Gio.FileIcon.new(Gio.File.new_for_uri(import.meta.url).get_parent().get_child('icons').get_child('personal-tools-symbolic.svg'));
const icon = (name, size = 24) => new St.Icon({...(name === 'personal-tools-symbolic' ? {gicon:brandIcon} : {icon_name:name}), icon_size:size});
const label = (text, style = '') => new St.Label({text, style_class:style, y_align:Clutter.ActorAlign.CENTER});
function fitTextRow(item, textBox) {
    const fit = () => {
        if (textBox.width <= 0) return;
        const [, preferred] = textBox.get_preferred_height(textBox.width);
        const children = textBox.get_children();
        // St preferred sizes are actor coordinates; Pango pixel sizes include
        // the texture resource scale and would double rows on HiDPI displays.
        const rendered = children.reduce((sum, child) => sum + child.get_preferred_height(child.width || textBox.width)[1], 0);
        const height = Math.max(preferred, rendered + textBox.get_theme_node().get_length('spacing') * Math.max(0,children.length-1));
        const scale = St.ThemeContext.get_for_stage(global.stage).scale_factor;
        const style = `min-height: ${Math.ceil(height / scale)}px;`;
        if (item.style !== style) item.set_style(style);
    };
    // The scroll viewport may shrink rows to their CSS minimum. Preserve the
    // actual wrapped title + caption height rather than overlapping separators.
    textBox.connect('notify::width', fit);
    textBox.connect('style-changed', fit);
    for (const child of textBox.get_children()) {
        child.connect('notify::width',fit);
        child.connect('resource-scale-changed',fit);
    }
}

export class QuickMenu {
    constructor(client, showClipboard, openApp) {
        this.client = client; this.showClipboard = showClipboard; this.openApp = openApp;
        this.button = new PanelMenu.Button(0, 'Personal Tools', false);
        this.button.add_style_class_name('pt-status-button');
        this.button.add_child(new St.Icon({gicon:brandIcon, style_class:'system-status-icon pt-accent-icon'}));
        this.button.menu.actor.add_style_class_name('pt-quick-menu');
        this.scroll = new St.ScrollView({hscrollbar_policy:St.PolicyType.NEVER,vscrollbar_policy:St.PolicyType.AUTOMATIC,overlay_scrollbars:true});
        this.button.menu._boxPointer.bin.set_child(null);
        this.scroll.set_child(this.button.menu.box);
        this.button.menu._boxPointer.bin.set_child(this.scroll);
        Main.panel.addToStatusArea('personal-tools', this.button);
        this.button.menu.connect('open-state-changed', (_menu, opened) => {
            if (opened) {
                const monitor = Main.layoutManager.findMonitorForActor(this.button) ?? Main.layoutManager.primaryMonitor;
                const scale = St.ThemeContext.get_for_stage(global.stage).scale_factor;
                this.scroll.set_style(`max-height: ${Math.max(160,(monitor.height-Main.panel.height)/scale-32)}px;`);
                this.client.refresh().catch(error => this.unavailable(error.message));
            }
        });
        this.unavailable('Personal Tools 서비스에 연결하는 중입니다.');
    }

    _action(domain, action, payload) {
        this.client.execute(domain, action, payload).catch(error => {
            Main.notify('Personal Tools', error.message);
            if (this.client.snapshot) this.update(this.client.snapshot);
        });
    }

    _title(text, iconName, brand = false) {
        const item = new PopupMenu.PopupBaseMenuItem({reactive:false, can_focus:false, style_class:brand ? 'pt-quick-brand' : 'pt-quick-heading'});
        const graphic = icon(iconName, brand ? 36 : 26); if (brand) graphic.add_style_class_name('pt-accent-icon');
        item.add_child(graphic); const title = label(text); title.x_expand = true; item.add_child(title);
        if (brand) {
            const settings = new St.Button({child:icon('emblem-system-symbolic'), can_focus:true, reactive:true, accessible_name:'설정', style_class:'pt-quick-icon-button'});
            settings.connect('clicked', () => { this.button.menu.close(); this.openApp('settings'); }); item.add_child(settings);
        }
        this.button.menu.addMenuItem(item);
    }

    _group() {
        const group = new PopupMenu.PopupMenuSection(); group.actor.add_style_class_name('pt-quick-group');
        this.button.menu.addMenuItem(group); return group;
    }

    _append(parent,item) {
        if (parent !== this.button.menu) {
            if (parent._lastStyledRow) {
                parent._lastStyledRow.remove_style_class_name('pt-row-last');
                parent.addMenuItem(new PopupMenu.PopupBaseMenuItem({reactive:false,can_focus:false,style_class:'pt-quick-divider'}));
            }
            item.add_style_class_name('pt-row-last');parent._lastStyledRow=item;
        }
        parent.addMenuItem(item);
    }

    _toggle(parent, text, state, callback, {available = true, name = 'audio-volume-high-symbolic', description = '', wrap = false} = {}) {
        const item = new PopupMenu.PopupSwitchMenuItem(text, Boolean(state));
        item.add_style_class_name(`pt-quick-row ${description ? 'pt-quick-described' : ''}`);
        // Preserve the native switch accessibility and keyboard behaviour.
        item.insert_child_at_index(icon(name), 1);
        if (description) {
            const textBox = new St.BoxLayout({vertical:true, x_expand:true, style_class:'pt-quick-row-text'});
            item.label.y_expand = false;
            item.remove_child(item.label); textBox.add_child(item.label); textBox.add_child(label(description, 'pt-quick-caption'));
            item.insert_child_at_index(textBox, 2);
            fitTextRow(item,textBox);
        }
        if (wrap) {
            item.label.clutter_text.line_wrap = true;
            item.label.clutter_text.line_wrap_mode = Pango.WrapMode.WORD_CHAR;
            item.label.clutter_text.ellipsize = Pango.EllipsizeMode.NONE;
            item.label.x_expand = true;
            item._statusBin.x_expand = false;
        }
        item.setSensitive(available);
        item.connect('toggled', (_item, enabled) => callback(enabled)); this._append(parent,item);
    }

    _link(parent, text, callback, {sensitive = true, style = '', name = null, end = null, description = '', gicon = null} = {}) {
        const item = new PopupMenu.PopupBaseMenuItem({style_class:`pt-quick-row ${style}`});
        if (gicon) item.add_child(new St.Icon({gicon, icon_size:24}));
        else if (name) item.add_child(icon(name));
        const title = label(text); title.x_expand = true;
        if (description) {
            const textBox = new St.BoxLayout({vertical:true, x_expand:true, style_class:'pt-quick-row-text'});
            const detail = label(description, 'pt-quick-caption');
            detail.clutter_text.ellipsize = Pango.EllipsizeMode.END;
            textBox.add_child(title);textBox.add_child(detail);item.add_child(textBox);
            fitTextRow(item,textBox);
        } else item.add_child(title);
        if (end) item.add_child(typeof end === 'string' ? label(end, 'pt-quick-caption') : end);
        item.setSensitive(sensitive); item.connect('activate', callback); this._append(parent,item); return item;
    }

    update(snapshot) {
        const renderedState={audio:{status:snapshot.audio?.status,muted:snapshot.audio?.muted,eqEnabled:snapshot.audio?.eqEnabled,
            aecEnabled:snapshot.audio?.aecEnabled,inputId:snapshot.audio?.inputId,inputs:snapshot.audio?.inputs,npu:snapshot.audio?.npu?.device},
            monitors:snapshot.display?.monitors?.map(({id,name,connector,primary,hdrEnabled,hdrSupported})=>({id,name,connector,primary,hdrEnabled,hdrSupported})),
            monitorPower:snapshot.display?.monitorPower,displayError:snapshot.display?.error,inhibitors:snapshot.display?.inhibitors,
            inhibitorStatus:snapshot.display?.inhibitorStatus,inhibitorError:snapshot.display?.inhibitorError,vllm:snapshot.ai?.vllm?.status,
            shortcut:snapshot.settings?.clipboardShortcut};
        const fingerprint=JSON.stringify(renderedState);
        if(fingerprint===this._fingerprint)return;
        this._fingerprint=fingerprint;
        this.button.menu.removeAll();
        const menu = this.button.menu;
        this._title('Personal Tools', 'personal-tools-symbolic', true);
        this._link(menu, '클립보드 열기', () => this.showClipboard(), {style:'pt-quick-primary', name:'edit-paste-symbolic', end:label(shortcutLabel(snapshot.settings?.clipboardShortcut), 'pt-shortcut-badge')});
        this._title('사운드', 'audio-volume-high-symbolic');
        const audio = snapshot.audio ?? {};
        const available = ['running','ready','connected'].includes(audio.status);
        const sound = this._group();
        sound.actor.add_style_class_name('pt-quick-audio');
        this._toggle(sound, '마이크 음소거', audio.muted, muted => this._action('audio','mute',{muted}), {available, name:'audio-input-microphone-symbolic',description:audio.inputs?.find(input => input.id === audio.inputId)?.name ?? ''});
        this._toggle(sound, '스피커 EQ', audio.eqEnabled, eqEnabled => this._action('audio','configure',{eqEnabled}), {available, name:'multimedia-equalizer-symbolic'});
        this._toggle(sound, '에코 제거', audio.aecEnabled, aecEnabled => this._action('audio','configure',{aecEnabled}), {available, name:'sound-wave-symbolic',description:audio.npu?.device ?? ''});
        this._title('HDR / 디스플레이', 'video-display-symbolic');
        const display = snapshot.display ?? {}, monitors = this._group();
        for (const monitor of display.monitors ?? [])
            this._toggle(monitors, monitor.name || monitor.connector || '모니터', monitor.hdrEnabled,
                enabled => this._action('display','hdr',{id:monitor.id, enabled}), {available:Boolean(monitor.hdrSupported),name:'video-display-symbolic',wrap:true,
                    description:[monitor.connector,monitor.primary?'주 모니터':'',monitor.hdrSupported?'':'HDR 미지원'].filter(Boolean).join(' · ')});
        if (!display.monitors?.length) this._link(monitors, display.error || '연결된 화면을 확인할 수 없습니다.', () => {}, {sensitive:false});
        this._link(monitors, '디스플레이 설정', () => this.openApp('display'), {end:icon('go-next-symbolic',20)});
        const power = display.monitorPower ?? {}, powerGroup = this._group();
        powerGroup.actor.add_style_class_name('pt-quick-spaced');
        this._toggle(powerGroup, 'MO32U24 자동 화면 끄기', Boolean(power.enabled),
            enabled => this._action('display','monitor-power',{enabled}),
            {available:Boolean(power.available),name:'display-brightness-symbolic',wrap:true,
                description:'두 대만 · 입력 없으면 1분 후 끄기'});
        this._link(powerGroup, power.error || '마우스·키보드로 켜기 · 앱의 화면 유지 요청 무시',
            () => this.openApp('display-power'), {style:'pt-quick-caption'});
        const inhibitors = this._group(); inhibitors.actor.add_style_class_name('pt-quick-spaced');
        const apps = new Map();
        for (const request of display.inhibitors ?? []) {
            const key = request.appId || request.appName || request.id;
            const app = apps.get(key) ?? {...request, requests:0};app.requests++;apps.set(key,app);
        }
        const inhibitorStatus = display.inhibitorStatus ?? 'unknown';
        this._link(inhibitors, '화면을 켜 두는 앱', () => this.openApp('display-power'), {name:'display-brightness-symbolic',end:`${inhibitorStatus === 'ready' ? `전체 ${apps.size}` : '—'}  ›`});
        if (inhibitorStatus !== 'ready')
            this._link(inhibitors, inhibitorStatus === 'stale' ? '이전 조회 결과 · 다시 확인이 필요합니다.' : ['error','unavailable'].includes(inhibitorStatus) ? '화면 꺼짐 방지 요청을 조회할 수 없습니다.' : '화면 꺼짐 방지 요청을 확인하고 있습니다.', () => {}, {sensitive:false,style:'pt-quick-caption'});
        else if (!apps.size)
            this._link(inhibitors, '화면 꺼짐을 막는 앱이 없습니다.', () => {}, {sensitive:false,style:'pt-quick-caption'});
        for (const app of [...apps.values()].slice(0,3)) {
            let desktop;
            try { if (app.appId) desktop = GioUnix.DesktopAppInfo.new(app.appId.endsWith('.desktop') ? app.appId : `${app.appId}.desktop`); } catch { /* Unknown app IDs retain the observed name. */ }
            this._link(inhibitors, desktop?.get_display_name() || app.appName || app.appId || '출처 미확인', () => this.openApp('display-power'),
                {name:'application-x-executable-symbolic',gicon:desktop?.get_icon(),style:'pt-quick-process',
                    description:[app.reason || '요청 이유 미확인',app.requests>1?`요청 ${app.requests}개`:''].filter(Boolean).join(' · ')});
        }
        this._title('vLLM', 'computer-chip-symbolic');
        const vllm = snapshot.ai?.vllm ?? {}, state = vllm.status ?? 'unknown';
        const running = ['running','ready','starting','loading','warming'].includes(state);
        const labels = {stopped:'중지됨', running:'실행 중', ready:'준비됨', starting:'시작 중', loading:'모델 로딩 중', warming:'워밍업 중', error:'오류', unavailable:'사용할 수 없음'};
        const ai = this._group();
        const command = new St.Button({label:running ? '중지' : '시작', style_class:'pt-button pt-primary pt-quick-start', reactive:!['unknown','unavailable'].includes(state),can_focus:true});
        command.connect('clicked', () => this._action('vllm',running ? 'stop' : 'start',{}));
        const item = new PopupMenu.PopupBaseMenuItem({reactive:false, can_focus:false,style_class:'pt-quick-row'});
        item.add_child(label('●','pt-accent-icon')); const status = label(labels[state] ?? '상태 확인 중'); status.x_expand = true;
        item.add_child(status); item.add_child(command); ai.addMenuItem(item);
        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        const open = this._group(); this._link(open, 'Personal Tools 열기', () => this.openApp('clipboard'), {name:'external-link-symbolic',end:icon('go-next-symbolic',20)});
    }

    unavailable(message) {
        this._fingerprint=null;
        this.button.menu.removeAll(); this._title('Personal Tools','personal-tools-symbolic',true);
        this._link(this.button.menu,message,() => {},{sensitive:false});
        this._link(this.button.menu,'Personal Tools 열기',() => this.openApp('clipboard'));
    }

    destroy() { this.button.destroy(); }
}
