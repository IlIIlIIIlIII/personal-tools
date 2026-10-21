// Popup regressions on a private compositor with a paging fixture service:
// each open resets search/selection, cards keep one vertical rhythm whatever
// script their text uses, and browsing pages through the whole archive.
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {FixtureClient} from './fixtures.js';
import {automation, prepareEnvironment} from './automation.js';

export const METRICS = {};
export const {init, run} = automation(popup, METRICS);
const assert = (condition, message) => { if (!condition) throw new Error(message); };
const ARCHIVE_SIZE = 150;
const samples = [
    {kind: 'url', title: 'https://help.openai.com/en/articles/8555545', preview: 'https://help.openai.com/en/articles/8555545', sourceApp: 'ChatGPT'},
    {kind: 'text', title: "curl --fail-with-body -sS 'https://translate-api.example.test/translate'",
        preview: "curl --fail-with-body -sS 'https://translate-api.example.test/translate' \\\n  -H 'Authorization: Bearer test_token_0123456789abcdefghijklmnop' \\\n  -H 'Content-Type: application/json' \\\n  -d '{\"text\":\"hello\",\"target\":\"ko\"}'"},
    {kind: 'text', title: '① 폰을 DRM 서버로 만들기 — ✅ 완성 폰에 커스텀 앱 설치',
        preview: '① 폰을 DRM 서버로 만들기 — ✅ 완성 폰에 커스텀 앱 설치 (패키지명 com.example.mediaclient) → TCP로 Widevine 도전장 생성/복호화/라이선스 주입 중계 L1 복호화 byte-exact 검증 완료 ② 다음 단계 정리'},
];

class PagingClient extends FixtureClient {
    constructor() {
        super();
        this.archive = Array.from({length: ARCHIVE_SIZE}, (_, index) => ({...samples[index % samples.length], id: `entry-${index}`,
            sourceAttribution: 'foreground-observed', createdAt: new Date(Date.UTC(2026, 9, 13, 12) - index * 60000).toISOString(),
            pinned: false, categoryIds: [], tags: [], attachments: []}));
        this.queries = [];
    }

    async execute(domain, action, payload = {}) {
        if (domain !== 'clipboard' || action !== 'query') return super.execute(domain, action, payload);
        this.queries.push(payload);
        const text = (payload.query ?? '').toLowerCase();
        const matches = this.archive.filter(entry => !text || entry.preview.toLowerCase().includes(text));
        const offset = payload.offset ?? 0, limit = payload.limit ?? 60;
        return {entries: matches.slice(offset, offset + limit), total: matches.length};
    }
}

async function waitFor(predicate, message) {
    for (let n = 0; n < 100; n++) {
        if (predicate()) return;
        await Scripting.sleep(50);
    }
    throw new Error(message);
}

async function capture(name) {
    await Scripting.sleep(300);
    const stream = Gio.File.new_for_path(`${GLib.getenv('PERSONALTOOLS_TEST_OUTPUT')}/${name}.png`).replace(null, false, Gio.FileCreateFlags.REPLACE_DESTINATION, null);
    await new Shell.Screenshot().screenshot(false, stream);
    stream.close(null);
    print(`PT_CAPTURE ${name}`);
}

function cardGeometry(card) {
    const [cardX, cardY] = card.get_transformed_position();
    const [heading, preview, source] = card.get_child().get_last_child().get_children();
    const top = actor => actor.get_transformed_position()[1] - cardY;
    return {kind: card._entry.kind, x: cardX, card: card.height, heading: heading.height, preview: top(preview),
        sourceTop: top(source), sourceBottom: top(source) + source.height};
}

async function popup() {
    await prepareEnvironment();
    Main.overview.hide();
    await Scripting.sleep(300);
    const extension = Main.extensionManager.lookup('personal-tools@griplabs.io')?.stateObj;
    assert(extension?._panel, 'Extension panel did not load');
    extension._bridge?.destroy(); extension._bridge = null;
    extension._client.destroy();
    const client = new PagingClient();
    extension._client = client;
    const panel = extension._panel;
    panel.client = client; panel.bridge = null;
    extension._menu.client = client;
    extension._onSnapshot(client.snapshot);
    const settle = async () => {
        await waitFor(() => panel._renderedRevision === panel._gate.value && !panel._moreCancel, 'Popup query did not settle');
        await Scripting.sleep(100);
    };

    panel.open(); await settle();
    assert(panel._cards.length === 60 && panel._total === ARCHIVE_SIZE, `First page should hold 60 of ${ARCHIVE_SIZE}, got ${panel._cards.length}/${panel._total}`);

    // Cards share one vertical rhythm, and the source row stays inside the card.
    const geometry = panel._cards.slice(0, samples.length).map(cardGeometry);
    print(`PT_POPUP_CARD_GEOMETRY ${JSON.stringify(geometry)}`);
    for (const value of geometry) assert(value.sourceBottom <= value.card, `${value.kind} source row overflows the card: ${JSON.stringify(value)}`);
    const text = geometry.filter(value => value.kind === 'text');
    assert(new Set(geometry.map(value => value.heading)).size === 1, `Card headings differ in height: ${JSON.stringify(geometry)}`);
    assert(new Set(text.map(value => value.preview)).size === 1, `Text previews start at different heights: ${JSON.stringify(text)}`);
    assert(new Set(geometry.map(value => value.sourceBottom)).size === 1, `Source rows end at different heights: ${JSON.stringify(geometry)}`);
    await capture('popup-cards');
    print('PT_POPUP_LAYOUT_PASS');

    // Browsing toward the end loads the rest of the archive page by page.
    for (let pages = 0; panel._cards.length < ARCHIVE_SIZE && pages < 5; pages++) {
        const loaded = panel._cards.length;
        panel._select(loaded - 1, true);
        await waitFor(() => panel._cards.length > loaded, `Browsing past ${loaded} cards did not load the next page`);
    }
    assert(panel._cards.length === ARCHIVE_SIZE && new Set(panel.entries.map(entry => entry.id)).size === ARCHIVE_SIZE, `Paging reached ${panel._cards.length} unique cards`);
    const oldest = `entry-${ARCHIVE_SIZE - 1}`;
    assert(panel.entries.at(-1).id === oldest, 'Oldest archived record is unreachable');

    // A refresh of the same query keeps the browsed pages and the selection.
    panel._select(ARCHIVE_SIZE - 1, true);
    await panel.query(); await settle();
    assert(panel._cards.length === ARCHIVE_SIZE && panel.entries[panel._selected]?.id === oldest, 'Refresh collapsed browsed pages or lost selection');
    print('PT_POPUP_PAGING_PASS');

    // Reopening starts fresh: no search text, normal mode, newest card selected.
    panel.search.set_text('curl'); await Scripting.sleep(350); await settle();
    panel.category = '*'; panel.filters = {formats: ['text'], apps: []};
    panel._select(5, true);
    panel.close();
    panel.open(); await settle();
    assert(panel.search.get_text() === '' && !panel.aiMode && panel.category === '' && panel.filters.formats.length === 0, 'Reopen kept search state');
    assert(panel._selected === 0 && panel.entries[0]?.id === 'entry-0', `Reopen selected ${panel.entries[panel._selected]?.id}`);
    assert(panel._cards[0].has_style_class_name('selected') && panel._cards[0].contains(global.stage.key_focus), 'First card is not focused after reopen');
    assert(panel.scroll.hadjustment.value === 0, 'Reopen kept the previous scroll offset');
    assert(panel._cards.length === 60, `Reopen should start from the first page, got ${panel._cards.length}`);
    assert(!client.queries.at(-1).query && !client.queries.at(-1).filter?.pinnedOnly, `Reopen queried stale scope: ${JSON.stringify(client.queries.at(-1))}`);
    print('PT_POPUP_RESET_PASS');
    panel.close();
}
