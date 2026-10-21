import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import {FixtureClient} from './fixtures.js';
import {automation,prepareEnvironment} from './automation.js';

export const METRICS = {};
export const {init,run} = automation(scenes,METRICS);
async function capture(name) {
    await Scripting.sleep(300);
    const output = GLib.getenv('PERSONALTOOLS_TEST_OUTPUT');
    const stream = Gio.File.new_for_path(`${output}/${name}.png`).replace(null,false,Gio.FileCreateFlags.REPLACE_DESTINATION,null);
    await new Shell.Screenshot().screenshot(false,stream);
    stream.close(null);
    print(`PT_CAPTURE ${name}`);
}
async function scenes() {
    await prepareEnvironment();
    Main.overview.hide();
    await Scripting.sleep(300);
    const extension = Main.extensionManager.lookup('personal-tools@griplabs.io')?.stateObj;
    if (!extension?._panel || !extension._bridge) throw new Error('Native extension did not load');
    // Isolated fixture never observes or restores a user clipboard.
    extension._bridge.destroy(); extension._bridge = null;
    extension._client.destroy();
    const client = new FixtureClient();
    client.snapshot.settings.theme = GLib.getenv('PERSONALTOOLS_TEST_THEME') || 'dark';
    extension._client = client;
    const panel = extension._panel;
    panel.client = client; panel.bridge = null;
    extension._menu.client = client;
    extension._onSnapshot(client.snapshot);
    print(`PT_BUNDLED_FONTS ${JSON.stringify(extension._fontFiles)}`);
    panel.open(); await panel.query();
    await Scripting.sleep(250);
    print(`PT_PANEL ${JSON.stringify({opened:panel.opened,visible:panel.actor.visible,x:panel.actor.x,y:panel.actor.y,width:panel.actor.width,height:panel.actor.height,light:extension._lightTheme,background:panel.actor.get_theme_node().get_background_color().to_string()})}`);
    const dimensions = actor => ({type:actor.constructor.name,classes:actor.style_class,width:actor.width,height:actor.height,y:actor.y,children:actor.get_children().map(dimensions)});
    print(`PT_CARD_GEOMETRY ${JSON.stringify(dimensions(panel._cards[0]))}`);
    await capture('02-shortcut');
    panel.category = 'reference'; panel.update(client.snapshot);
    panel.search.set_text('지난번 저장한 디자인 참고 이미지 찾아줘');
    await panel.aiSearch();
    await capture('03-ai-search');
    client.snapshot.clipboard.collections[1].name = '디자인';
    panel.update(client.snapshot);
    panel.search.set_text('지난번 저장한 올리브색 설정 화면 찾아줘');
    client.error = true;
    await panel.aiSearch();
    await Scripting.sleep(100);
    const [emptyX,emptyY]=panel.empty.get_transformed_position();
    if (emptyY+panel.empty.height>panel.actor.y+panel.actor.height-8) throw new Error('AI error actions overflow compact panel');
    print(`PT_COMPACT_ERROR_GEOMETRY ${JSON.stringify({x:emptyX,y:emptyY,width:panel.empty.width,height:panel.empty.height,panelHeight:panel.actor.height})}`);
    await capture('17-ai-error');
    if (!panel.search.get_secondary_icon()) throw new Error('Search clear control is missing');
    panel.search.emit('secondary-icon-clicked');
    if (panel.search.get_text() || panel.search.get_secondary_icon()) throw new Error('Search clear control did not reset the field');
    print('PT_SEARCH_CLEAR_PASS');
    panel.close();
    extension._menu.update(client.snapshot);
    extension._menu.button.menu.open();
    await capture('14-quick-menu');
    const adjustment = extension._menu.scroll.vadjustment;
    if (adjustment.upper > adjustment.page_size+1) {
        adjustment.value=adjustment.upper-adjustment.page_size;
        await capture('14-quick-menu-scrolled');
        const last=extension._menu.button.menu.box.get_last_child();
        const [,y]=last.get_transformed_position();
        if(y+last.height>global.stage.height)throw new Error('Quick menu last action remains outside the work area');
        print('PT_QUICK_MENU_SCROLL_PASS final action visible');
    }
    extension._menu.button.menu.close();
    print('PT_SCENES_PASS');
}
