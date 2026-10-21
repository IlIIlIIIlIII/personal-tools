#!/usr/bin/gjs -m
// Read-only live check: installed GTK page -> model button -> session service -> CLI.
import Gtk from 'gi://Gtk?version=4.0';
import GLib from 'gi://GLib';
import Adw from 'gi://Adw?version=1';
import {aiPage} from 'file:///usr/share/personal-tools/App/pages/ai.js';
import {Backend} from 'file:///usr/share/personal-tools/App/backend.js';
Adw.init();
function assert(value,message){if(!value)throw new Error(message);}
function find(root,predicate){const found=[];function visit(w){if(predicate(w))found.push(w);for(let c=w.get_first_child();c;c=c.get_next_sibling())visit(c);}visit(root);return found;}
const settle=()=>new Promise(resolve=>GLib.timeout_add(GLib.PRIORITY_DEFAULT,100,()=>{resolve();return GLib.SOURCE_REMOVE;}));
const client=new Backend();let page,pending;
const execute=client.execute.bind(client);
client.execute=(domain,action,payload)=>{
    assert(domain==='antigravity'&&action==='models','Unexpected live mutation');
    pending=execute(domain,action,payload);return pending;
};
const ctx={client,get snapshot(){return client.snapshot;},viewState:{aiTab:'antigravity'},width:1488,
    window:new Gtk.Window({title:'모델 확인 실제 연동 검수',default_width:1488,default_height:900}),
    command:()=>{throw new Error('Unexpected settings mutation');},toast:()=>{},
    render:()=>{page=aiPage(ctx);ctx.window.set_child(page.widget);}};
try{
    // Also verify opening before the initial service snapshot has arrived.
    ctx.render();await client.start();ctx.render();ctx.window.present();await settle();
    const check=()=>find(page.widget,w=>w instanceof Gtk.Button).find(b=>find(b,w=>w instanceof Gtk.Label).some(l=>l.label.startsWith('모델 확인')));
    check().emit('clicked');assert(!check().sensitive,'Busy state absent');
    const result=await pending;await settle();
    assert(!ctx.viewState.modelLookup.error,ctx.viewState.modelLookup.error);
    const dropdown=find(page.widget,w=>w instanceof Gtk.DropDown)[0];
    const actual=Array.from({length:dropdown.model.get_n_items()},(_,i)=>dropdown.model.get_item(i).string);
    for(const id of result.models)assert(actual.includes(id==='antigravity'?'CLI 기본 모델':id),`Missing returned model ${id}`);
    const count=result.models.filter(id=>id!=='antigravity').length;
    assert(count>0,'No named live models returned');
    assert(find(page.widget,w=>w instanceof Gtk.Label).some(l=>l.label===`사용 가능한 모델 ${count}개를 확인했습니다.`),'Completion feedback absent');
    assert(check().sensitive,'Button did not re-enable');
    ctx.render();assert(ctx.viewState.modelLookup.models.length===result.models.length,'Refresh lost models');
    print(JSON.stringify({ok:true,installedPage:true,liveButton:true,namedModels:count,dropdownItems:actual.length,busyAndCompletionVerified:true}));
}finally{ctx.window.destroy();}
