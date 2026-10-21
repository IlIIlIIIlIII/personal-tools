#!/usr/bin/gjs -m
import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import Gsk from 'gi://Gsk?version=4.0';
import Graphene from 'gi://Graphene';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Adw from 'gi://Adw?version=1';
import {ROOT,box,label,icon,button,iconButton,entry,spacer,segmented,clear,textScale} from './ui.js';
import {Backend,FixtureBackend} from './backend.js';
import {ClipboardPage,clipboardSettings} from './pages/clipboard.js';
import {soundPage} from './pages/sound.js';
import {displayPage} from './pages/display.js';
import {aiPage} from './pages/ai.js';
import {settingsPage} from './pages/settings.js';
import {windowsPage} from './pages/windows.js';
import {diagnosticsPage} from './pages/diagnostics.js';
import {pageTitles as titles,resolvePageRoute} from './routes.js';
import {installSearchShortcut} from './keyboard.js';

const flags = [...ARGV];
const option = (name,fallback) => { const at=flags.indexOf(name);return at>=0?flags[at+1]:fallback; };
const fixture=flags.includes('--fixture');
const scene=option('--screen','01-library');
const scenes={'06-clipboard-settings':'clipboard-settings','07-sound':'sound','08-hdr':'display','09-power':'display','10-antigravity':'ai','11-vllm':'ai','12-diagnostics':'diagnostics','13-settings':'settings','18-windows':'windows','19-windows-empty':'windows','20-windows-outdated':'windows'};
const initialRoute=resolvePageRoute(option('--page',null));
let page=initialRoute?.page||scenes[scene]||'clipboard', current, search, pauseButton, window, content, header, overlay, nav;
let renderPending=0, ready=false;
const client=fixture?new FixtureBackend(scene):new Backend();
const app=new Adw.Application({application_id:fixture?'io.griplabs.PersonalTools.Preview':'io.griplabs.PersonalTools.App',flags:fixture?Gio.ApplicationFlags.NON_UNIQUE:Gio.ApplicationFlags.HANDLES_COMMAND_LINE});
const ctx={client,fixture,scene,viewState:{displayTab:initialRoute?.displayTab||(scene==='09-power'?'power':'hdr'),aiTab:scene==='11-vllm'?'vllm':'antigravity'},headerActions:[],get snapshot(){return client.snapshot;},get window(){return window;},get effectiveWidth(){return this.width/textScale();},get width(){return window?.get_mapped()?window.get_width():Number(option('--width','1488'));},light:false,render,navigate,toast,command:async(domain,action,payload)=>{try{const result=await client.execute(domain,action,payload);await client.refresh();scheduleRender();return result??{};}catch(e){toast(e.message);scheduleRender();return null;}}};
const palette=new Gtk.CssProvider();
function theme(){
    const manager=Adw.StyleManager.get_default();
    const choice=option('--theme',ctx.snapshot.settings.theme||'system');
    manager.color_scheme=choice==='dark'?Adw.ColorScheme.FORCE_DARK:choice==='light'?Adw.ColorScheme.FORCE_LIGHT:Adw.ColorScheme.DEFAULT;
    ctx.light=choice==='light'||(choice==='system'&&!manager.dark);
    const cssFile=Gio.File.new_for_path(`${ROOT}/${ctx.light?'style-light':'style'}.css`);const css=new TextDecoder().decode(cssFile.load_contents(null)[1]);palette.load_from_string(css.replace(/font-size:\s*([0-9.]+)px/g,(_match,size)=>`font-size: ${Number(size)*textScale()}px`));
}
function toast(message){overlay?.add_toast(new Adw.Toast({title:String(message),timeout:5}));}
async function quit(){try{if(!fixture)await client.execute('lifecycle','quit',{});app.quit();}catch(e){toast(e.message);}}
function selectPage(destination){const route=resolvePageRoute(destination);if(!route)return false;if(page==='clipboard-settings'&&route.page!==page)delete ctx.viewState.clipboardSettings;page=route.page;if(route.displayTab)ctx.viewState.displayTab=route.displayTab;ctx.scene='';return true;}
function navigate(next){if(selectPage(next))render();}
function openPopover(widget){for(let child=widget?.get_first_child();child;child=child.get_next_sibling()){if(child instanceof Gtk.Popover&&child.visible)return true;if(child.visible&&openPopover(child))return true;}return false;}
function editing(){const focus=window?.get_focus();return Boolean(focus&&(focus instanceof Gtk.Text||focus instanceof Gtk.Entry||focus instanceof Gtk.TextView||focus instanceof Gtk.SpinButton))||app.get_windows().some(w=>w!==window&&w.visible&&w.modal)||openPopover(window);}
function scheduleRender(){if(renderPending)return;renderPending=GLib.timeout_add(GLib.PRIORITY_DEFAULT,250,()=>{if(editing())return GLib.SOURCE_CONTINUE;renderPending=0;if(ready)render();return GLib.SOURCE_REMOVE;});}
function render(){
    if(!window)return;
    theme();current?.dispose?.();clear(content);clear(header);ctx.headerActions=[];ctx.headerStatus=null;
    current=page==='clipboard'?new ClipboardPage(ctx):({ 'clipboard-settings':clipboardSettings,windows:windowsPage,sound:soundPage,display:displayPage,ai:aiPage,diagnostics:diagnosticsPage,settings:settingsPage}[page])(ctx);
    content.append(current.widget);
    for(const [id,b]of nav)b[id===(page==='clipboard-settings'?'clipboard':page)?'add_css_class':'remove_css_class']('selected');
    if(page==='clipboard-settings')header.append(iconButton('go-previous-symbolic','클립보드로 돌아가기',()=>navigate('clipboard')));
    header.spacing=ctx.effectiveWidth<1300?12:24;
    const pageTitle=label(titles[page],'page-title');if(page==='clipboard')pageTitle.width_request=160;if(page!=='clipboard'||ctx.effectiveWidth>=1300)header.append(pageTitle);
    if(ctx.headerStatus)header.append(ctx.headerStatus);
    if(page==='clipboard'){
        const state=ctx.viewState.clipboard;
        search=entry(state.query,'기록 검색','search-field');search.primary_icon_name='system-search-symbolic';search.width_request=ctx.effectiveWidth<1300?200:260;
        const run=mode=>current.search(search.text,mode);
        search.connect('changed',()=>state.query=search.text);search.connect('activate',()=>run(state.mode));
        const key=new Gtk.EventControllerKey();key.connect('key-pressed',(_c,k,_code,mods)=>{if(k===Gdk.KEY_Return){run(mods&Gdk.ModifierType.SHIFT_MASK?'ai':'normal');return true;}return false;});search.add_controller(key);
        const searchSpace=spacer();searchSpace.width_request=22;searchSpace.hexpand=false;if(ctx.effectiveWidth>=1300)header.append(searchSpace);header.append(search);
        header.append(segmented([{id:'normal',title:'일반'},{id:'ai',title:'AI'}],state.mode,id=>{state.mode=id;if(search.text)run(id);}));
        pauseButton=iconButton(ctx.snapshot.clipboard.paused?'media-playback-start-symbolic':'media-playback-pause-symbolic',ctx.snapshot.clipboard.paused?'기록 시작':'기록 일시정지',()=>ctx.command('clipboard','pause',{paused:!ctx.snapshot.clipboard.paused}));header.append(pauseButton);
    }else header.append(spacer());
    ctx.headerActions.forEach(w=>header.append(w));
    const controls=new Gtk.WindowControls({side:Gtk.PackType.END,decoration_layout:':minimize,maximize,close'});header.append(controls);
    window.title=`${titles[page]} — Personal Tools`;
}
function activate(){
    if(window){window.present();return;}
    Gtk.Settings.get_default().gtk_icon_theme_name='Yaru';
    Gtk.StyleContext.add_provider_for_display(Gdk.Display.get_default(),palette,Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION);
    Adw.StyleManager.get_default().connect('notify::dark',()=>{if(ready)theme();});
    window=new Gtk.ApplicationWindow({application:app,title:'Personal Tools',default_width:Number(option('--width','1488')),default_height:Number(option('--height','1056')),css_classes:['pt-app']});
    // A zero-height custom titlebar makes our in-content header the sole titlebar.
    const design=Gio.File.new_for_path(ROOT).get_parent().get_child('Design');
    const fontManifest=JSON.parse(new TextDecoder().decode(design.get_child('fonts.json').load_contents(null)[1]));
    const fontMap=window.get_pango_context().get_font_map();for(const font of fontManifest.fonts)fontMap.add_font_file(design.get_child(font.bundledFile).get_path());
    window.set_titlebar(new Gtk.Box({visible:false}));
    const root=box(false,0);overlay=new Adw.ToastOverlay({child:root});window.set_child(overlay);
    const sidebar=box(true,4,'sidebar');sidebar.width_request=244;
    const brand=new Gtk.Button({child:box(false,20,'brand',[new Gtk.Image({gicon:Gio.FileIcon.new(Gio.File.new_for_path(`${ROOT}/assets/personal-tools-symbolic.svg`)),pixel_size:28}),label('Personal Tools')]),css_classes:['brand-button'],tooltip_text:'앱 메뉴'});
    brand.connect('clicked',()=>{const menu=new Gtk.Popover();menu.set_parent(brand);menu.set_child(box(true,4,'',[button('클립보드 설정','emblem-system-symbolic',()=>{menu.popdown();navigate('clipboard-settings');},'flat'),button('설정','preferences-system-symbolic',()=>{menu.popdown();navigate('settings');},'flat'),button('Personal Tools 종료','application-exit-symbolic',()=>{menu.popdown();quit();},'flat')]));menu.connect('closed',()=>menu.unparent());menu.popup();});sidebar.append(brand);
    nav=new Map();
    for(const [id,ic]of [['clipboard','edit-paste-symbolic'],['windows','focus-windows-symbolic'],['sound','audio-volume-high-symbolic'],['display','video-display-symbolic'],['ai','starred-symbolic'],['diagnostics','utilities-system-monitor-symbolic'],['settings','emblem-system-symbolic']]){
        if(id==='settings')sidebar.append(spacer(true));
        const b=button(titles[id],ic,()=>navigate(id),'nav-button');b.get_child().halign=Gtk.Align.FILL;b.get_child().spacing=24;if(id==='settings')b.margin_bottom=24;nav.set(id,b);sidebar.append(b);
    }
    root.append(sidebar);
    const main=box(true,0);main.hexpand=true;header=box(false,24,'app-header');
    const handle=new Gtk.WindowHandle({child:header});main.append(handle);content=box(true,0);content.vexpand=true;main.append(content);root.append(main);
    installSearchShortcut(window,()=>{if(page!=='clipboard')navigate('clipboard');search.grab_focus();search.select_region(0,-1);});
    window.connect('close-request',()=>{if(fixture)return false;if(ctx.snapshot.settings.background)window.hide();else quit();return true;});
    const interfacePreferences=new Gio.Settings({schema_id:'org.gnome.desktop.interface'});interfacePreferences.connect('changed::text-scaling-factor',()=>scheduleRender());
    let layoutKey='';
    window.add_tick_callback(()=>{
        // Reflow the existing page even while an input or popover has focus.
        current?.updateLayout?.();
        const next=ctx.effectiveWidth>=1300?'wide':ctx.effectiveWidth>=1250?'medium':'small';
        if(ready&&next!==layoutKey){layoutKey=next;if(!current?.updateLayout)scheduleRender();}
        return true;
    });
    client.subscribe(()=>{
        // Window registrations come directly from Shell and must not wait for
        // the independent daemon connection to finish (or time out).
        if(!ready){if(page==='windows')current?.updateSnapshot?.();return;}
        // A true result means all changes were handled without replacing the page.
        const updated=current?.updateSnapshot?.()===true;
        if(page==='clipboard'&&current){
            if(pauseButton){
                pauseButton.get_child().get_first_child().icon_name=ctx.snapshot.clipboard.paused?'media-playback-start-symbolic':'media-playback-pause-symbolic';
                pauseButton.tooltip_text=ctx.snapshot.clipboard.paused?'기록 시작':'기록 일시정지';
            }
        }else if(!updated)scheduleRender();
    });
    ctx.scene='';render();window.present();
    client.start().then(()=>{ready=true;render();if(fixture)GLib.timeout_add(GLib.PRIORITY_DEFAULT,400,()=>{ctx.scene=scene;render();ctx.scene='';return GLib.SOURCE_REMOVE;});const path=option('--screenshot',null);if(path)GLib.timeout_add(GLib.PRIORITY_DEFAULT,1000,()=>{capture(path);return GLib.SOURCE_REMOVE;});});
}
function capture(path){
    let renderer;
    try{
        const paintable=new Gtk.WidgetPaintable({widget:window});const shot=new Gtk.Snapshot();paintable.snapshot(shot,window.get_width(),window.get_height());
        const node=shot.to_node();if(!node)throw new Error('The window has not rendered yet. Connect a display before capture.');renderer=Gsk.Renderer.new_for_surface(window.get_surface());
        const rect=new Graphene.Rect();rect.init(0,0,window.get_width(),window.get_height());
        const texture=renderer.render_texture(node,rect);texture.save_to_png(path);print(`CAPTURE ${path} ${window.get_width()}x${window.get_height()}`);
    }catch(e){console.error(`${e.message}\n${e.stack}`);app.exitStatus=1;}
    finally {renderer?.unrealize();}
    app.quit();
}
app.connect('activate',activate);
app.connect('command-line',(_app,cmd)=>{const args=cmd.get_arguments();const index=args.indexOf('--page');if(index>=0)selectPage(args[index+1]);activate();render();return 0;});
app.run(fixture?[]:['personal-tools',...flags]);
