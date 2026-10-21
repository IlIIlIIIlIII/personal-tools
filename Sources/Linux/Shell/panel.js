import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GioUnix from 'gi://GioUnix';
import GLib from 'gi://GLib';
import Pango from 'gi://Pango';
import Shell from 'gi://Shell';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {monitorAtPoint, panelBounds, RevisionGate, scopePayload, shortcutLabel} from './model.js';

// The service returns 60 records per query by default and accepts at most 500.
// Browsing loads further pages so every archived record stays reachable.
const PAGE_SIZE = 60;
const QUERY_LIMIT = 500;
const LOAD_AHEAD = 10;

const icon = (name, size = 24) => new St.Icon({icon_name: name, icon_size: size});
const toggleClass = (actor, name, enabled) => enabled ? actor.add_style_class_name(name) : actor.remove_style_class_name(name);
function containsEventPoint(actor, event) {
    if (!actor?.visible) return false;
    const [px, py] = event.get_coords();
    const [x, y] = actor.get_transformed_position();
    const [width, height] = actor.get_transformed_size();
    return px >= x && py >= y && px < x + width && py < y + height;
}
function label(text, style = '') {
    return new St.Label({text, style_class: style, y_align: Clutter.ActorAlign.CENTER});
}
function button(text, callback, {name = null, style = '', accessible = text} = {}) {
    const actor = new St.Button({style_class: `pt-button ${style}`, can_focus: true, reactive: true, accessible_name: accessible});
    const row = new St.BoxLayout({style_class: 'pt-button-content'});
    if (name) row.add_child(icon(name, 20));
    if (text) row.add_child(label(text));
    actor.set_child(row);
    actor.connect('clicked', callback);
    return actor;
}

export class ClipboardPanel {
    constructor(client, bridge, openApp, tokens = {}) {
        this.client = client;
        this.bridge = bridge;
        this.openApp = openApp;
        this.tokens = tokens;
        this.opened = false;
        this.category = '';
        this.aiMode = false;
        this.filters = {formats: [], apps: []};
        this.entries = [];
        this._total = 0;
        this._queryKey = null;
        this._gate = new RevisionGate();
        this._presentation = 0;
        this._cards = [];
        this.actor = new St.BoxLayout({vertical: true, style_class: 'pt-panel', reactive: true, can_focus: true, visible: false, accessible_name: '클립보드 빠른 패널'});
        this.actor.set_clip_to_allocation(true);
        this._build();
        // Popup visibility belongs to open()/close(). Shell's fullscreen
        // tracking would force this hidden actor visible on overview changes.
        Main.layoutManager.addTopChrome(this.actor);
        this._overviewSignal = Main.overview.connect('showing', () => this.close());
        this._monitorSignal = Main.layoutManager.connect('monitors-changed', () => { if (this.opened) this._place(); });
        this._workareaSignal = global.display.connect('workareas-changed', () => { if (this.opened) this._place(); });
        this._focusSignal = global.display.connect('notify::focus-window', () => {
            if (this.opened && global.display.focus_window && global.display.focus_window !== this.target) this.close();
        });
        this.actor.connect('key-press-event', (_actor, event) => this._key(event));
    }

    _build() {
        this.body = new St.BoxLayout({vertical: true, style_class: 'pt-panel-body', x_expand: true, y_expand: true});
        this.actor.add_child(this.body);
        const header = new St.BoxLayout({style_class: 'pt-panel-header', x_expand: true});
        this.brand = new St.BoxLayout({style_class: 'pt-panel-brand'});
        const logo = icon('edit-paste-symbolic', 32); logo.add_style_class_name('pt-accent-icon');
        this.brand.add_child(logo); this.brand.add_child(label('클립보드', 'pt-panel-title'));
        this.shortcutBadge = label('Ctrl + Shift + V', 'pt-shortcut-badge');
        this.brand.add_child(this.shortcutBadge);
        header.add_child(this.brand);
        this.search = new St.Entry({style_class: 'pt-search', hint_text: '기록 검색', can_focus: true, x_expand: true, accessible_name: '클립보드 검색'});
        this.search.set_primary_icon(icon('edit-find-symbolic', 22));
        this.search.connect('secondary-icon-clicked', () => { this.search.set_text(''); this.search.grab_key_focus(); });
        this.search.clutter_text.connect('text-changed', () => {
            this.search.set_secondary_icon(this.search.get_text() ? icon('edit-clear-symbolic', 18) : null);
            this._cancelSearch();
            if (this._debounce) GLib.source_remove(this._debounce);
            this._debounce = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 200, () => {
                this._debounce = 0;
                if (!this.aiMode) this.query();
                return GLib.SOURCE_REMOVE;
            });
        });
        header.add_child(this.search);
        const modes = new St.BoxLayout({style_class: 'pt-segments'});
        this.normalButton = button('일반', () => this.setMode(false), {style: 'pt-segment'});
        this.aiButton = button('AI', () => this.setMode(true), {style: 'pt-segment'});
        modes.add_child(this.normalButton); modes.add_child(this.aiButton); header.add_child(modes);
        this.filterButton = button('필터', () => this._showFilters(), {name: 'nautilus-search-filters-symbolic'});
        header.add_child(this.filterButton);
        this.pauseButton = button('', () => this._action('pause', {paused: !this.client.snapshot?.clipboard.paused}), {name: 'media-playback-pause-symbolic', accessible: '기록 일시정지'});
        header.add_child(this.pauseButton);
        header.add_child(button('', () => this._openMain('clipboard-settings'), {name: 'emblem-system-symbolic', accessible: '클립보드 설정'}));
        header.add_child(button('', () => this._openMain('clipboard'), {name: 'external-link-symbolic', accessible: '메인 창 열기'}));
        this.body.add_child(header);
        this.categories = new St.BoxLayout({style_class: 'pt-categories'});
        this.categoryScroll = new St.ScrollView({hscrollbar_policy:St.PolicyType.EXTERNAL,vscrollbar_policy:St.PolicyType.NEVER,overlay_scrollbars:true,style_class:'pt-category-scroll'});
        this.categoryScroll.set_child(this.categories);this.body.add_child(this.categoryScroll);
        this.summary = new St.BoxLayout({style_class: 'pt-summary', visible: false});
        this.summary.add_child(icon('starred-symbolic', 20));
        this.summaryText = label('', 'pt-summary-text'); this.summaryText.x_expand = true;
        this.summaryText.clutter_text.ellipsize = Pango.EllipsizeMode.END;
        this.summary.add_child(this.summaryText);
        this.summaryScope = label('선택한 모음 안에서 검색했습니다.', 'pt-summary-scope');
        this.summary.add_child(this.summaryScope);
        this.cancelButton = button('취소', () => { this._cancelSearch(); this._status('검색을 취소했습니다.'); });
        this.summary.add_child(this.cancelButton);
        this.body.add_child(this.summary);
        this.scroll = new St.ScrollView({style_class: 'pt-card-scroll', x_expand: true, y_expand: true, hscrollbar_policy: St.PolicyType.EXTERNAL, vscrollbar_policy: St.PolicyType.NEVER,overlay_scrollbars:true});
        this.cards = new St.BoxLayout({style_class: 'pt-cards'});
        this.scroll.set_child(this.cards);
        this.scroll.hadjustment.connect('notify::value', adjustment => {
            if (adjustment.value + adjustment.page_size >= adjustment.upper - adjustment.page_size / 2) this._loadMore();
        });
        this.body.add_child(this.scroll);
        this.empty = new St.BoxLayout({vertical: true, style_class: 'pt-empty', x_expand: true, y_expand: true, visible: false});
        this.body.add_child(this.empty);
        this._updateMode();
    }

    update(snapshot) {
        if (!snapshot) return;
        this.shortcutBadge.text=shortcutLabel(snapshot.settings?.clipboardShortcut);
        // Daemon health refreshes and read-only command replies can publish a new
        // snapshot without changing clipboard data. Do not restart an in-flight
        // query for those updates, or its response can never win the revision gate.
        const fingerprint = JSON.stringify(snapshot.clipboard);
        const changed = fingerprint !== this._clipboardFingerprint;
        this._clipboardFingerprint = fingerprint;
        const collections = snapshot.clipboard?.collections ?? [];
        const categoriesFingerprint = JSON.stringify([this.category, collections]);
        if (categoriesFingerprint !== this._categoriesFingerprint) {
            this._categoriesFingerprint = categoriesFingerprint;
            this._renderCategories(collections);
        }
        const paused = snapshot.clipboard?.paused;
        this.pauseButton.set_child(icon(paused ? 'media-playback-start-symbolic' : 'media-playback-pause-symbolic'));
        this.pauseButton.accessible_name = paused ? '기록 재개' : '기록 일시정지';
        if (changed && this.opened && !this.aiMode) this.query();
    }

    _renderCategories(collections) {
        this.categories.destroy_all_children();
        const available = new Set(['', '*', ...collections.map(c => c.id)]);
        if (!available.has(this.category)) this.category = '';
        const categories = [{id: '', name: '전체'}, {id: '*', name: '고정'}, ...collections];
        for (const [index, category] of categories.entries()) {
            const tab = button(category.name, () => {
                this.category = category.id; this._cancelSearch(); this._renderCategories(collections); this.query();
            }, {style: `pt-category ${index === 0 ? 'pt-category-first' : ''} ${index === categories.length - 1 ? 'pt-category-last' : ''} ${category.name.length>=3 ? 'pt-category-wide' : ''} ${category.id === this.category ? 'selected' : ''}`});
            if (category.id && category.id !== '*') tab.connect('button-press-event', (_actor, event) => {
                if (event.get_button() !== 3) return Clutter.EVENT_PROPAGATE;
                this._collectionEditor(category); return Clutter.EVENT_STOP;
            });
            this.categories.add_child(tab);
        }
        this.categories.add_child(button('', () => this._collectionEditor(), {name: 'list-add-symbolic', accessible: '모음 추가', style: 'pt-category-add'}));
    }

    _place() {
        const [x, y] = global.get_pointer();
        this._monitor ??= monitorAtPoint(Main.layoutManager.monitors, x, y, Main.layoutManager.primaryIndex);
        const monitors = Main.layoutManager.monitors;
        if (this._monitor >= monitors.length) this._monitor = Main.layoutManager.primaryIndex;
        const area = global.workspace_manager.get_active_workspace().get_work_area_for_monitor(this._monitor);
        const layout = this.tokens.layout ?? {};
        const bounds = panelBounds(area, {...layout, panelHeight: (layout.panelHeight ?? 400) * Math.max(1, this.textScale ?? 1)});
        this.actor.set_position(bounds.x, bounds.y);
        this.actor.set_size(bounds.width, bounds.height);
        this.shortcutBadge.visible = bounds.width >= 1380;
        this.brand.visible = bounds.width >= 1000;
        this.summaryScope.visible = bounds.width >= 1000;
        toggleClass(this.actor, 'pt-compact', bounds.width < 1100);
    }

    open() {
        if (this.opened) return;
        Main.overview.hide();
        this._presentation++;
        this.target = global.display.focus_window;
        this._monitor = null;
        this._place();
        this.opened = true;
        this.actor.show();
        // Keep the Shell overview key available while grabbing panel input.
        // Its normal overview transition closes this popup via `showing`.
        this._grab = Main.pushModal(this.actor, {actionMode: Shell.ActionMode.NORMAL | Shell.ActionMode.POPUP});
        // GNOME 50 modal grabs deliver captured events to the grabbed actor,
        // not its stage ancestor; outside events can have no source actor.
        this._capturedSignal = this.actor.connect('captured-event', (_actor, event) => {
            if (this._captureKey(event)) return Clutter.EVENT_STOP;
            if (event.type() !== Clutter.EventType.BUTTON_PRESS && event.type() !== Clutter.EventType.TOUCH_BEGIN) return Clutter.EVENT_PROPAGATE;
            if (containsEventPoint(this.actor, event)) return Clutter.EVENT_PROPAGATE;
            this.close();
            return Clutter.EVENT_STOP;
        });
        // Opening the clipboard starts card browsing. Search is an explicit
        // Ctrl+F/click action; otherwise arrows and Return must work immediately.
        this.actor.grab_key_focus();
        this._resetView();
        this.update(this.client.snapshot);
        if (!this.client.snapshot) this.unavailable('Personal Tools 서비스에 연결할 수 없습니다.');
        else if (!this.aiMode) this.query();
    }

    // Every popup starts from the newest record: the previous search, AI
    // results, scope and filters are dropped and the first card is selected.
    _resetView() {
        this._cancelSearch();
        this.aiMode = false; this._updateMode();
        this.category = ''; this.filters = {formats: [], apps: []};
        this.search.set_text('');
        if (this._debounce) { GLib.source_remove(this._debounce); this._debounce = 0; }
        this._queryKey = null;
        this._selectFirst = true;
        this._select(0);
        this.scroll.hadjustment.value = 0;
    }

    toggle() { if (this.opened) this.close(); else this.open(); }

    close({pasting = false} = {}) {
        const wasOpened = this.opened;
        this.opened = false;
        // Repair visibility even when an external Shell transition changed it
        // while our logical state was already closed.
        this.actor.hide();
        if (!wasOpened && !this._grab && !this._popover) return;
        if (!pasting) { this._presentation++; this.bridge?.cancelPendingPaste(); }
        this._cancelSearch();
        this._closePopover();
        if (this._capturedSignal) { this.actor.disconnect(this._capturedSignal); this._capturedSignal = 0; }
        if (this._grab) { Main.popModal(this._grab); this._grab = null; }
    }

    setMode(ai) {
        this._cancelSearch(); this.aiMode = ai; this._updateMode();
        if (!ai) this.query();
        else this.search.grab_key_focus();
    }

    _updateMode() {
        toggleClass(this.normalButton, 'selected', !this.aiMode);
        toggleClass(this.aiButton, 'selected', this.aiMode);
        this.filterButton.reactive = !this.aiMode;
        this.filterButton.opacity = this.aiMode ? 100 : 255;
        this.search.hint_text = this.aiMode ? 'AI 검색 · Shift + Enter' : '기록 검색';
    }

    _cancelSearch() {
        this._focusSearchResults = false;
        this._gate.next();
        this._queryCancel?.cancel();
        this._moreCancel?.cancel(); this._moreCancel = null;
        if (this._requestId) this.client.execute('clipboard', 'cancel-search', {requestId: this._requestId}).catch(() => {});
        this._requestId = null;
        this.summary.hide();
    }

    async query() {
        if (!this.opened || this.aiMode) return;
        const revision = this._gate.next();
        this._queryCancel?.cancel();
        this._moreCancel?.cancel(); this._moreCancel = null;
        const cancellable = new Gio.Cancellable(); this._queryCancel = cancellable;
        const request = {query: this.search.get_text(), filter: scopePayload(this.category, this.filters)};
        const key = JSON.stringify(request);
        // A refresh of the same query (new copy, pin, delete) keeps the pages
        // already browsed instead of collapsing back to the first page.
        const limit = key === this._queryKey ? Math.min(QUERY_LIMIT, Math.max(PAGE_SIZE, this.entries.length)) : PAGE_SIZE;
        try {
            const data = await this.client.execute('clipboard', 'query', {...request, limit}, {cancellable});
            if (!this.opened || !this._gate.current(revision)) return;
            const entries = data.entries ?? [];
            this._queryKey = key; this._total = data.total ?? entries.length; this._renderedRevision = revision;
            this._renderEntries(entries);
        } catch (error) { if (this.opened && this._gate.current(revision) && !cancellable.is_cancelled()) this.unavailable(error.message); }
    }

    async _loadMore() {
        // Only extend the result set the latest query rendered; a pending
        // query would otherwise shift the offset under this page.
        const key = this._queryKey, revision = this._gate.value;
        if (!this.opened || this.aiMode || this._moreCancel || key === null || this._renderedRevision !== revision || this.entries.length >= this._total) return;
        const cancellable = new Gio.Cancellable(); this._moreCancel = cancellable;
        try {
            const data = await this.client.execute('clipboard', 'query', {...JSON.parse(key), offset: this.entries.length, limit: PAGE_SIZE}, {cancellable});
            if (this.opened && this._gate.current(revision) && key === this._queryKey) this._appendEntries(data.entries ?? [], data.total);
        } catch (error) { if (this.opened && this._gate.current(revision) && !cancellable.is_cancelled()) this._status(error.message); }
        finally { if (this._moreCancel === cancellable) this._moreCancel = null; }
    }

    async aiSearch({focusResults = false} = {}) {
        this._cancelSearch(); this.aiMode = true; this._updateMode();
        const query = this.search.get_text().trim();
        if (!query) { this._status('검색어를 입력해 주세요.'); return; }
        this._focusSearchResults = focusResults;
        const revision = this._gate.next();
        const cancellable = new Gio.Cancellable(); this._queryCancel = cancellable;
        this._requestId = GLib.uuid_string_random();
        this.summaryText.text = '선택한 모음에서 검색하고 있습니다…';
        this.cancelButton.show(); this.summary.show();
        try {
            const data = await this.client.execute('clipboard', 'ai-search', {query, requestId: this._requestId, ...(this.category && this.category !== '*' ? {categoryId: this.category} : {}), ...(this.category === '*' ? {pinnedOnly: true} : {})}, {cancellable, timeout: 180000});
            if (!this.opened || !this._gate.current(revision)) return;
            this._requestId = null; this.cancelButton.hide();
            this._queryKey = null; this._total = data.entries?.length ?? 0;
            this.summaryText.text = data.summary || `${data.entries?.length ?? 0}개의 관련 기록을 찾았습니다.`;
            this._renderEntries(data.entries ?? [], true); this.summary.show();
        } catch (error) {
            if (this.opened && this._gate.current(revision) && !cancellable.is_cancelled()) this._showAiError(error.message);
        }
    }

    _renderEntries(entries, ai = false) {
        const focus = global.stage.key_focus;
        const focusResults = this._focusSearchResults && this.search.contains(focus) && !this._popover;
        this._focusSearchResults = false;
        const selectFirst = focusResults || this._selectFirst;
        this._selectFirst = false;
        const browsing = focus === this.actor || (focus && this.cards.contains(focus));
        const selectedId = this.entries[this._selected]?.id;
        // Destroying a focused card must not strand focus on the stage. Keep
        // browsing across refreshes without stealing focus from search/popovers.
        if (browsing) this.actor.grab_key_focus();
        this.entries = entries; this.cards.destroy_all_children(); this._cards = [];
        this.empty.hide(); this.scroll.show();
        if (selectFirst) this.scroll.hadjustment.value = 0;
        if (!entries.length) {
            this._showEmpty(this.search.get_text() || this.category ? '일치하는 기록이 없어요' : '아직 복사한 기록이 없어요',
                this.search.get_text() || this.category ? '다른 검색어나 필터로 다시 찾아보세요.' : '텍스트, 이미지, 파일을 복사하면 여기에 보관됩니다.');
            return;
        }
        for (const entry of entries) this._addCard(entry, ai);
        const retained = selectFirst ? -1 : entries.findIndex(entry => entry.id === selectedId);
        this._select(retained >= 0 ? retained : 0, focusResults || browsing);
    }

    _appendEntries(entries, total) {
        const known = new Set(this.entries.map(entry => entry.id));
        const fresh = entries.filter(entry => !known.has(entry.id));
        // An empty or fully duplicated page means the archive changed under
        // the offset; stop paging until the next refresh recounts it.
        this._total = fresh.length ? total ?? this._total : this.entries.length;
        this.entries = [...this.entries, ...fresh];
        for (const entry of fresh) this._addCard(entry, false);
    }

    _addCard(entry, ai) {
        const card = new St.Button({style_class: `pt-card ${ai ? 'pt-ai-card' : ''} ${ai && entry.kind==='url' ? 'pt-ai-url' : ''}`, can_focus: true, reactive: true,y_align:Clutter.ActorAlign.START,
            accessible_name: `${entry.title || entry.preview || '클립보드 기록'}, ${entry.sourceApp || '출처 알 수 없음'}`});
        card._entry = entry;
        card.set_clip_to_allocation(true);
        card.set_child(this._cardContent(entry, ai));
        card.connect('clicked', () => {
            if (!this.opened) return;
            this._select(this._cards.indexOf(card), true);
            this._paste(entry);
        });
        // St.Button consumes Return as "clicked" before it can bubble to
        // the panel. Handle card keyboard actions before that default.
        card.connect('key-press-event', (_actor, event) => this._key(event));
        card.connect('button-press-event', (_actor, event) => {
            const index = this._cards.indexOf(card); this._select(index, true);
            if (event.get_button() === 3) { this._cardMenu(entry); return Clutter.EVENT_STOP; }
            return Clutter.EVENT_PROPAGATE;
        });
        card.connect('key-focus-in', () => this._select(this._cards.indexOf(card), false));
        this.cards.add_child(card); this._cards.push(card);
    }

    _cardContent(entry, ai) {
        const content = new St.BoxLayout({vertical: !ai, style_class: 'pt-card-content', x_expand:true, y_expand:true});
        const details = new St.BoxLayout({vertical: true, style_class: 'pt-card-details', x_expand: true, y_expand:true});
        const heading = new St.BoxLayout({style_class: 'pt-card-heading'});
        heading.add_child(icon(({image: 'image-x-generic-symbolic', files: 'text-x-generic-symbolic', url: 'insert-link-symbolic'})[entry.kind] ?? 'text-x-generic-symbolic', 22));
        const title = label(entry.title || '클립보드 기록', 'pt-card-title'); title.x_expand = true; title.clutter_text.ellipsize = Pango.EllipsizeMode.END;
        heading.add_child(title);
        if (entry.pinned) { const pin = icon('view-pin-symbolic', 18); pin.add_style_class_name('pt-accent-icon'); heading.add_child(pin); }
        details.add_child(heading);
        let preview;
        let linkHost = '';
        if (entry.kind === 'url') {
            const candidate = entry.url || entry.preview?.match(/https?:\/\/\S+|[a-z0-9.-]+\.(?:com|org|net)(?:\/\S*)?/i)?.[0];
            try { if (candidate) linkHost = GLib.Uri.parse(candidate.includes('://') ? candidate : `https://${candidate}`,GLib.UriFlags.NONE).get_host(); } catch {}
        }
        if (entry.thumbnailUri?.startsWith('file://')) {
            preview = new St.Widget({style_class: 'pt-card-image', style:`background-image: url("${entry.thumbnailUri}");`, x_expand:!ai, y_expand:true});
        } else if (entry.kind === 'url' && linkHost) {
            const graphic = icon(linkHost.endsWith('ubuntu.com') ? 'distributor-logo' : 'web-browser-symbolic',ai ? 96 : 44);
            if (ai) {
                preview = new St.Bin({style_class:'pt-link-image',child:graphic,y_align:Clutter.ActorAlign.START});
            } else {
                preview = new St.BoxLayout({vertical:true,style_class:'pt-link-preview',y_expand:true});
                const row = new St.BoxLayout({style_class:'pt-link-row'});row.add_child(graphic);
                const copy = new St.BoxLayout({vertical:true,x_expand:true,style_class:'pt-link-copy'});
                const title = label(entry.linkTitle || entry.title,'pt-link-title');title.clutter_text.ellipsize=Pango.EllipsizeMode.END;copy.add_child(title);
                const description=label(entry.linkDescription || '저장한 웹 페이지','pt-link-description');description.clutter_text.ellipsize=Pango.EllipsizeMode.END;copy.add_child(description);
                row.add_child(copy);preview.add_child(row);preview.add_child(label(linkHost,'pt-link-host'));
            }
        } else {
            const text = label(entry.preview || entry.attachments?.map(a => a.name).join('\n') || '');
            text.clutter_text.line_wrap = true;
            text.clutter_text.line_wrap_mode = Pango.WrapMode.WORD_CHAR;
            // Without ellipsizing, a wrapped label's minimum height is the whole
            // text, which pushes the source caption out of the fixed-height card.
            text.clutter_text.ellipsize = Pango.EllipsizeMode.END;
            text.clutter_text.single_line_mode = false;
            text.y_align = Clutter.ActorAlign.START;
            text.x_align = Clutter.ActorAlign.FILL;
            text.x_expand = true;
            preview = new St.Bin({style_class:'pt-card-preview',child:text,y_expand:true});
            preview.set_clip_to_allocation(true);
        }
        if (ai && (entry.thumbnailUri || linkHost)) content.add_child(preview); else details.add_child(preview);
        const timestamp = entry.createdAt ? GLib.DateTime.new_from_iso8601(entry.createdAt, null)?.to_local().format('%H:%M') : '';
        const app = entry.sourceApp?.endsWith('.desktop') ? GioUnix.DesktopAppInfo.new(entry.sourceApp) : null;
        const caption = new St.BoxLayout({style_class:'pt-card-source'});
        if (app?.get_icon()) caption.add_child(new St.Icon({gicon:app.get_icon(),icon_size:20}));
        caption.add_child(label(`${app?.get_name() || entry.sourceApp || '출처 알 수 없음'}${timestamp ? `  ·  ${timestamp}` : ''}`, 'pt-card-caption'));
        details.add_child(caption);
        if (ai) {
            if (entry.thumbnailUri) { const description = label(entry.preview || '', 'pt-card-caption'); description.clutter_text.ellipsize = Pango.EllipsizeMode.END; details.insert_child_at_index(description, 1); }
            else if (linkHost) {
                details.insert_child_at_index(label(entry.linkDescription || '저장한 웹 페이지','pt-card-caption'),1);
                const address=label(entry.url || linkHost,'pt-link-address');address.clutter_text.ellipsize=Pango.EllipsizeMode.END;details.insert_child_at_index(address,2);
            }
            const actions = new St.BoxLayout({style_class: 'pt-card-actions'});
            actions.add_child(button('붙여넣기', () => this._paste(entry), {name: 'edit-paste-symbolic',style:'pt-card-paste'}));
            actions.add_child(button('', () => this._action('pin', {id: entry.id, pinned: !entry.pinned}), {name: 'view-pin-symbolic', accessible: entry.pinned ? '고정 해제' : '고정'}));
            actions.add_child(button('', () => this._cardMenu(entry), {name: 'view-more-symbolic', accessible: '카드 작업'}));
            details.add_child(actions);
        }
        content.add_child(details);
        return content;
    }

    _select(index, focus = false) {
        if (!this._cards.length) return;
        this._selected = Math.max(0, Math.min(index, this._cards.length - 1));
        this._cards.forEach((card, i) => toggleClass(card, 'selected', i === this._selected));
        const current = this._cards[this._selected];
        if (focus) current.grab_key_focus();
        const adjustment = this.scroll.hadjustment;
        if (adjustment && current.width > 0) {
            const left = current.x, right = current.x + current.width;
            if (left < adjustment.value) adjustment.value = left;
            else if (right > adjustment.value + adjustment.page_size) adjustment.value = right - adjustment.page_size;
        }
        if (this._selected >= this._cards.length - LOAD_AHEAD) this._loadMore();
    }

    async _paste(entry) {
        const presentation = this._presentation;
        try {
            if (!this.bridge) throw new Error('클립보드 전송 모듈을 사용할 수 없습니다.');
            const result = await this.bridge.put(entry.id, {paste: true, target: this.target,
                stillCurrent: () => this._presentation === presentation,
                closePanel: () => this.close({pasting: true})});
            if (!result.ok) Main.notify('Personal Tools', result.error.message);
        } catch (error) { if (this.opened) this._status(error.message); }
    }

    _status(message) { Main.notify('Personal Tools', message); }
    unavailable(message) { if (this.opened) this._showEmpty('서비스를 사용할 수 없어요', message, true); }

    _showEmpty(title, description, retry = false) {
        this._focusSearchResults = false;
        if (global.stage.key_focus && this.cards.contains(global.stage.key_focus)) this.actor.grab_key_focus();
        this.scroll.hide(); this.empty.destroy_all_children();
        this.empty.add_child(icon(retry ? 'dialog-warning-symbolic' : 'edit-paste-symbolic', 36));
        this.empty.add_child(label(title, 'pt-empty-title'));
        const help = label(description, 'pt-muted'); help.clutter_text.line_wrap = true; help.clutter_text.ellipsize = Pango.EllipsizeMode.END;
        this.empty.add_child(help);
        if (retry) this.empty.add_child(button('다시 연결', () => this.client.refresh().catch(error => this._status(error.message))));
        this.empty.show();
    }

    _showAiError(message) {
        this.summary.hide(); this._showEmpty('AI 서비스에 연결할 수 없어요', message || 'Antigravity 연결 상태를 확인한 뒤 다시 시도해 주세요.');
        this.empty.get_first_child().icon_name = 'dialog-warning-symbolic';
        const actions = new St.BoxLayout({style_class: 'pt-error-actions', x_align:Clutter.ActorAlign.CENTER});
        actions.add_child(button('다시 시도', () => this.aiSearch(), {name: 'view-refresh-symbolic', style: 'pt-primary'}));
        actions.add_child(button('일반 검색으로 전환', () => this.setMode(false), {name: 'edit-find-symbolic'}));
        actions.add_child(button('연결 설정 열기', () => this._openMain('ai'), {style: 'pt-flat'}));
        this.empty.add_child(actions);
        this.empty.add_child(label('일반 검색과 기록 복사는 계속 사용할 수 있습니다.', 'pt-error-footnote'));
    }

    _captureKey(event) {
        if (event.type() !== Clutter.EventType.KEY_PRESS) return false;
        const key = event.get_key_symbol();
        const state = event.get_state();
        // Entries implement Ctrl+F as cursor movement. Panel-wide search and
        // Escape must run in capture phase, before editable children consume it.
        if (key === Clutter.KEY_Escape ||
            (!this._popover && this.search.contains(global.stage.key_focus) &&
                (key === Clutter.KEY_Return || key === Clutter.KEY_KP_Enter)) ||
            ((state & Clutter.ModifierType.CONTROL_MASK) && (key === Clutter.KEY_f || key === Clutter.KEY_F)))
            return this._key(event) === Clutter.EVENT_STOP;
        return false;
    }

    _key(event) {
        const key = event.get_key_symbol();
        const state = event.get_state();
        if (key === Clutter.KEY_Escape) { if (this._popover) this._closePopover(); else this.close(); return Clutter.EVENT_STOP; }
        if (state & Clutter.ModifierType.CONTROL_MASK && (key === Clutter.KEY_f || key === Clutter.KEY_F)) {
            this._focusSearchResults = false;
            this._closePopover();
            this.search.grab_key_focus(); this.search.clutter_text.set_selection(0, -1); return Clutter.EVENT_STOP;
        }
        if (this._popover) return Clutter.EVENT_PROPAGATE;
        const focus = global.stage.key_focus;
        const inSearch = this.search.contains(focus);
        const resultsVisible = this.scroll.visible && !this.empty.visible && this._cards.length > 0;
        const browsing = resultsVisible && (focus === this.actor || (focus && this.cards.contains(focus)));
        if (key === Clutter.KEY_Return || key === Clutter.KEY_KP_Enter) {
            if (inSearch) {
                // Explicit submission enters result browsing after the current
                // response arrives. Typing/debounced queries retain text focus.
                if (this._debounce) GLib.source_remove(this._debounce);
                this._debounce = 0;
                if (state & Clutter.ModifierType.SHIFT_MASK) this.aiSearch({focusResults: true});
                else {
                    this._cancelSearch();
                    this.aiMode = false; this._updateMode();
                    this._focusSearchResults = true;
                    this.query();
                }
            }
            else if (browsing && this.entries[this._selected]) this._paste(this.entries[this._selected]);
            else return Clutter.EVENT_PROPAGATE;
            return Clutter.EVENT_STOP;
        }
        if (inSearch && resultsVisible && key === Clutter.KEY_Down) { this._select(this._selected ?? 0, true); return Clutter.EVENT_STOP; }
        if (browsing && (key === Clutter.KEY_Left || key === Clutter.KEY_Right)) { this._select(this._selected + (key === Clutter.KEY_Right ? 1 : -1), true); return Clutter.EVENT_STOP; }
        if (browsing && key === Clutter.KEY_Delete && this.entries[this._selected]) { this._action('delete', {id: this.entries[this._selected].id}); return Clutter.EVENT_STOP; }
        return Clutter.EVENT_PROPAGATE;
    }

    async _action(action, payload) {
        try { await this.client.execute('clipboard', action, payload); this._closePopover(); if (!this.aiMode) this.query(); }
        catch (error) { this._status(error.message); }
    }

    _popoverBox(title) {
        this._closePopover();
        const box = new St.BoxLayout({vertical: true, style_class: `pt-popover ${this.lightTheme ? 'pt-light' : ''}`, reactive: true, can_focus: true});
        box.set_style(`font-size: ${18*(this.textScale || 1)}px;`);
        box.add_child(label(title, 'pt-popover-title'));
        Main.layoutManager.addTopChrome(box);
        this._popover = box;
        const [x, y] = this.actor.get_transformed_position();
        box.set_position(x + Math.max(16, this.actor.width - 390), Math.max(36, y - 210));
        // Popovers are separate top-chrome actors. Give them their own nested
        // grab so their controls receive actual pointer/keyboard events.
        this._popoverGrab = Main.pushModal(box, {actionMode: Shell.ActionMode.NORMAL | Shell.ActionMode.POPUP});
        box.connect('captured-event', (_actor, event) => {
            if (this._captureKey(event)) return Clutter.EVENT_STOP;
            if (event.type() !== Clutter.EventType.BUTTON_PRESS && event.type() !== Clutter.EventType.TOUCH_BEGIN) return Clutter.EVENT_PROPAGATE;
            if (containsEventPoint(box, event)) return Clutter.EVENT_PROPAGATE;
            this._closePopover();
            if (!containsEventPoint(this.actor, event)) this.close();
            return Clutter.EVENT_STOP;
        });
        box.connect('key-press-event', (_actor, event) => this._key(event));
        return box;
    }

    _closePopover() {
        if (this._popoverGrab) { Main.popModal(this._popoverGrab); this._popoverGrab = null; }
        if (this._popover) { Main.layoutManager.removeChrome(this._popover); this._popover.destroy(); this._popover = null; }
    }

    _cardMenu(entry) {
        const box = this._popoverBox('기록 작업');
        box.add_child(button('붙여넣기', () => { this._closePopover(); this._paste(entry); }, {name: 'edit-paste-symbolic'}));
        box.add_child(button('복사', async () => {
            try { await this.bridge.put(entry.id); this._status('클립보드에 복사했습니다.'); this._closePopover(); }
            catch (error) { this._status(error.message); }
        }, {name: 'edit-copy-symbolic'}));
        box.add_child(button('미리보기', () => this._showDetail(entry), {name: 'view-reveal-symbolic'}));
        box.add_child(button('모음 지정', () => this._assignCollections(entry), {name: 'folder-symbolic'}));
        box.add_child(button(entry.pinned ? '고정 해제' : '고정', () => this._action('pin', {id: entry.id, pinned: !entry.pinned}), {name: 'view-pin-symbolic'}));
        box.add_child(button('삭제', () => this._action('delete', {id: entry.id}), {name: 'user-trash-symbolic', style: 'pt-destructive'}));
        box.get_children()[1].grab_key_focus();
    }

    async _showDetail(entry) {
        try {
            const data = await this.client.execute('clipboard', 'detail', {id: entry.id});
            if (!this.opened) return;
            const box = this._popoverBox(entry.title || '미리보기');
            if (data.imageUri?.startsWith('file://')) box.add_child(new St.Icon({gicon: Gio.FileIcon.new(Gio.File.new_for_uri(data.imageUri)), icon_size: 250}));
            const text = label(data.text ?? data.files?.map(f => `${f.name} · ${f.length} bytes`).join('\n') ?? entry.preview ?? '', 'pt-preview-text');
            text.clutter_text.line_wrap = true; text.clutter_text.ellipsize = Pango.EllipsizeMode.END;
            box.add_child(text); box.add_child(button('닫기', () => this._closePopover()));
        } catch (error) { this._status(error.message); }
    }

    _assignCollections(entry) {
        const box = this._popoverBox('모음 지정');
        const selected = new Set(entry.categoryIds ?? []);
        for (const collection of this.client.snapshot?.clipboard.collections ?? []) {
            const toggle = button(collection.name, () => {
                if (selected.has(collection.id)) selected.delete(collection.id); else selected.add(collection.id);
                toggleClass(toggle, 'selected', selected.has(collection.id));
            }, {style: selected.has(collection.id) ? 'selected' : ''});
            box.add_child(toggle);
        }
        box.add_child(button('저장', () => this._action('set-collections', {id: entry.id, categoryIds: [...selected]}), {style: 'pt-primary'}));
    }

    _collectionEditor(collection = null) {
        const box = this._popoverBox(collection ? '모음 이름 변경' : '새 모음');
        const name = new St.Entry({text: collection?.name ?? '', hint_text: '모음 이름', can_focus: true, style_class: 'pt-entry'});
        box.add_child(name);
        box.add_child(button('저장', () => this._action('save-collection', {name: name.get_text(), ...(collection ? {id: collection.id} : {})}), {style: 'pt-primary'}));
        if (collection) {
            box.add_child(label('모음을 삭제해도 기록은 유지됩니다.', 'pt-muted'));
            box.add_child(button('모음 삭제', () => this._action('delete-collection', {id: collection.id}), {style: 'pt-destructive'}));
        }
        name.grab_key_focus();
    }

    _showFilters() {
        const box = this._popoverBox('기록 필터');
        const formats = new Set(this.filters.formats);
        const apps = new Set(this.filters.apps);
        const formatRow = new St.BoxLayout({style_class: 'pt-filter-formats'});
        for (const [kind, text] of [['text', '텍스트'], ['url', 'URL'], ['image', '이미지'], ['files', '파일']]) {
            const toggle = button(text, () => { if (formats.has(kind)) formats.delete(kind); else formats.add(kind); toggleClass(toggle, 'selected', formats.has(kind)); }, {style: formats.has(kind) ? 'selected' : ''});
            formatRow.add_child(toggle);
        }
        box.add_child(formatRow); box.add_child(label('복사한 앱', 'pt-muted'));
        const appScroll = new St.ScrollView({style_class: 'pt-filter-apps', vscrollbar_policy: St.PolicyType.AUTOMATIC, hscrollbar_policy: St.PolicyType.NEVER});
        const appList = new St.BoxLayout({vertical: true});
        for (const app of [...new Set((this.client.snapshot?.clipboard.entries ?? []).map(entry => entry.sourceApp).filter(Boolean))].sort()) {
            const toggle = button(app, () => { if (apps.has(app)) apps.delete(app); else apps.add(app); toggleClass(toggle, 'selected', apps.has(app)); }, {style: apps.has(app) ? 'selected' : ''});
            appList.add_child(toggle);
        }
        appScroll.set_child(appList); box.add_child(appScroll);
        const from = new St.Entry({hint_text: '시작일 YYYY-MM-DD', text: this.filters.from ?? '', can_focus: true, style_class: 'pt-entry'});
        const through = new St.Entry({hint_text: '종료일 YYYY-MM-DD', text: this.filters.through ?? '', can_focus: true, style_class: 'pt-entry'});
        box.add_child(from); box.add_child(through);
        const actions = new St.BoxLayout({style_class: 'pt-filter-formats'});
        actions.add_child(button('초기화', () => { this.filters = {formats: [], apps: []}; this._closePopover(); this.query(); }));
        actions.add_child(button('적용', () => {
            const start = from.get_text().trim(), end = through.get_text().trim();
            if ([start, end].some(value => value && (!/^\d{4}-\d{2}-\d{2}$/.test(value) || !GLib.DateTime.new_from_iso8601(`${value}T00:00:00Z`, null))) || (start && end && start > end)) {
                this._status('시작일과 종료일을 YYYY-MM-DD 형식으로 확인해 주세요.'); return;
            }
            this.filters = {formats: [...formats], apps: [...apps], ...(start ? {from: start} : {}), ...(end ? {through: end} : {})};
            this._closePopover(); this.query();
        }, {style: 'pt-primary'}));
        box.add_child(actions);
    }

    _openMain(page) { this.close(); this.openApp(page); }

    destroy() {
        this.close(); this._cancelSearch();
        if (this._debounce) GLib.source_remove(this._debounce);
        Main.layoutManager.disconnect(this._monitorSignal);
        Main.overview.disconnect(this._overviewSignal);
        global.display.disconnect(this._workareaSignal);
        global.display.disconnect(this._focusSignal);
        Main.layoutManager.removeChrome(this.actor); this.actor.destroy();
    }
}
