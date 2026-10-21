import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {FixtureClient} from './fixtures.js';
import {automation,prepareEnvironment} from './automation.js';
export const METRICS={};
export const {init,run}=automation(scenes,METRICS);
function descendants(actor,predicate){const result=[];const visit=a=>{if(predicate(a))result.push(a);a.get_children().forEach(visit);};visit(actor);return result;}
function assert(value,message){if(!value)throw new Error(message);}
async function shot(name){await Scripting.sleep(250);const stream=Gio.File.new_for_path(`${GLib.getenv('PERSONALTOOLS_TEST_OUTPUT')}/${name}.png`).replace(null,false,Gio.FileCreateFlags.REPLACE_DESTINATION,null);await new Shell.Screenshot().screenshot(false,stream);stream.close(null);}
async function scenes(){
 await prepareEnvironment();Main.overview.hide();await Scripting.sleep(300);
 const extension=Main.extensionManager.lookup('personal-tools@griplabs.io').stateObj;
 extension._bridge.destroy();extension._bridge=null;extension._client.destroy();
 const client=new FixtureClient();client.snapshot.settings.theme=GLib.getenv('PERSONALTOOLS_TEST_THEME')||'dark';
 client.snapshot.display.monitors=[
  {id:'internal',name:'내장 디스플레이',connector:'eDP-1',hdrSupported:true,hdrEnabled:false},
  {id:'gigabyte1',name:'GIGA-BYTE TECHNOLOGY CO., LTD. MO32U24',connector:'HDMI-1',primary:true,hdrSupported:true,hdrEnabled:true},
  {id:'gigabyte2',name:'GIGA-BYTE TECHNOLOGY CO., LTD. MO32U24',connector:'DP-4',hdrSupported:true,hdrEnabled:false},
  {id:'fourth',name:'32RTX950',connector:'DP-5',hdrSupported:false,hdrEnabled:false},
 ];
 client.snapshot.display.inhibitorStatus='ready';
 client.snapshot.display.inhibitors=[
  {id:'1',appId:'org.mozilla.firefox',appName:'Firefox',reason:'동영상 재생 중'},
  {id:'2',appId:'org.mozilla.firefox',appName:'Firefox',reason:'화면 공유 중'},
  {id:'3',appId:'org.gnome.Totem',appName:'동영상',reason:'미디어 재생 중'},
  {id:'4',appId:'org.gnome.Presentation',appName:'프레젠테이션',reason:'발표 진행 중'},
  {id:'5',appId:'org.example.Fourth',appName:'네 번째 앱',reason:'다른 작업 진행 중'},
 ];
 extension._client=client;extension._panel.client=client;extension._panel.bridge=null;extension._menu.client=client;
 extension._onSnapshot(client.snapshot);
 assert(!extension._panel.actor.visible&&!extension._panel.opened,'Clipboard panel appeared without being opened');
 Main.overview.show();await Scripting.sleep(100);Main.overview.hide();await Scripting.sleep(200);
 assert(!extension._panel.actor.visible,'Overview made the closed clipboard panel visible');
 const menu=extension._menu;menu.button.menu.open();await Scripting.sleep(300);
 const rows=()=>descendants(menu.button.menu.box,a=>a.has_style_class_name?.('pt-quick-process'));
 const switches=descendants(menu.button.menu.box,a=>a instanceof PopupMenu.PopupSwitchMenuItem);
 assert(switches.length===8,'All four monitor switches were not rendered');assert(rows().length===3,'Inhibitor preview must contain three distinct apps');
 assert(descendants(menu.button.menu.box,a=>a.text==='네 번째 앱').length===0,'Fourth app leaked into preview');
 print(`PT_QUICK_MENU_LAYOUT ${JSON.stringify({buttonWidth:menu.button.width,buttonHeight:menu.button.height,monitors:switches.length-4,previewApps:rows().length,totalApps:4,menuHeight:menu.button.menu.box.height,viewportHeight:menu.scroll.height})}`);
 print(`PT_QUICK_MENU_TEXT_GEOMETRY ${JSON.stringify(switches.slice(3,7).map(item=>({height:item.height,style:item.style,texts:descendants(item,a=>a.clutter_text).map(a=>({text:a.text,width:a.width,height:a.height,preferred:a.get_preferred_height(a.width)[1],textPreferred:a.clutter_text.get_preferred_height(a.width)[1],pixels:a.clutter_text.get_layout().get_pixel_size()[1],resourceScale:a.clutter_text.get_resource_scale()}))})))}`);
 const powerSwitch=switches[7];assert(powerSwitch.state,'Monitor power state missing');
 const actions=[];menu._action=(...args)=>actions.push(args);
 powerSwitch.emit('toggled',false);
 assert(actions[0][0]==='display'&&actions[0][1]==='monitor-power'&&actions[0][2].enabled===false,'Wrong monitor power command');
 menu.update({...client.snapshot,display:{...client.snapshot.display,monitorPower:{available:true,enabled:false}}});
 assert(!descendants(menu.button.menu.box,a=>a instanceof PopupMenu.PopupSwitchMenuItem)[7].state,'External monitor power state did not refresh');
 menu.update(client.snapshot);
 await shot('quick-menu-top');
 const adj=menu.scroll.vadjustment;adj.value=Math.min(440,adj.upper-adj.page_size);await shot('quick-menu-hdr-processes');
 adj.value=adj.upper-adj.page_size;await shot('quick-menu-bottom');
 let opened;menu.openApp=page=>opened=page;rows()[0].emit('activate',null);assert(opened==='display-power','Inhibitor link opens the wrong page');
 const changed={...client.snapshot,display:{...client.snapshot.display,inhibitors:client.snapshot.display.inhibitors.map(v=>v.id==='4'?{...v,appName:'새 프레젠테이션',reason:'발표 자료 변경됨'}:v)}};
 menu.update(changed);assert(descendants(menu.button.menu.box,a=>a.text==='발표 자료 변경됨').length>0,'Same-count request changes did not refresh');
 print('PT_QUICK_MENU_CONTENT_REFRESH_PASS');
 menu.update({...changed,display:{...changed.display,inhibitors:[],inhibitorStatus:'unavailable'}});
 assert(descendants(menu.button.menu.box,a=>a.text==='화면 꺼짐 방지 요청을 조회할 수 없습니다.').length>0,'Failed query is not distinct from empty');
 menu.update({...changed,display:{...changed.display,inhibitors:[],inhibitorStatus:'ready'}});
 assert(descendants(menu.button.menu.box,a=>a.text==='화면 꺼짐을 막는 앱이 없습니다.').length>0,'Successful empty query missing');
 menu.button.menu.close();print('PT_QUICK_MENU_PASS all monitors, three distinct apps, navigation, same-count refresh, error and empty');
}
