// Explicit live-session acceptance test. Uses public Mutter input D-Bus only.
// Clipboard backup stays in process memory; the result contains no user content.
import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import GdkPixbuf from 'gi://GdkPixbuf';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const output = ARGV[0];
if (!output?.startsWith('/')) throw new Error('An absolute result directory is required');
const app = new Gtk.Application({application_id:'io.griplabs.PersonalTools.Preview.LiveSession',flags:Gio.ApplicationFlags.NON_UNIQUE});
const bus = Gio.DBus.session;
const report = {startedAt:new Date().toISOString(),scope:'installed extension in current user Wayland session',checks:[],pass:false};
const saved = new Map(), synthetic = new Set();
let win, field, clipboard, remote, originalPaused, changed=false, keepClipboard=false, restored=false;
const pause = ms => new Promise(resolve => GLib.timeout_add(GLib.PRIORITY_DEFAULT,ms,()=>{resolve();return GLib.SOURCE_REMOVE;}));
const check = (yes,name) => { if(!yes) throw new Error(name); report.checks.push(name); };
const call = (name,path,iface,method,args=null) => new Promise((resolve,reject) => bus.call(name,path,iface,method,args,null,Gio.DBusCallFlags.NO_AUTO_START,10000,null,(c,r)=>{try{resolve(c.call_finish(r).deepUnpack());}catch(e){reject(e);}}));
const daemon = async(method,args=null) => JSON.parse((await call('io.griplabs.PersonalTools','/io/griplabs/PersonalTools','io.griplabs.PersonalTools1',method,args))[0]);
const command = async(action,data={}) => {const r=await daemon('Execute',new GLib.Variant('(sss)',['clipboard',action,JSON.stringify(data)]));if(!r.ok)throw new Error(`clipboard ${action} rejected`);return r.data;};
const shell = async(method,args=null) => JSON.parse((await call('io.griplabs.PersonalTools.Shell','/io/griplabs/PersonalTools/Shell','io.griplabs.PersonalTools.Shell1',method,args))[0]);
const input = (method,args=null) => call('org.gnome.Mutter.RemoteDesktop',remote,'org.gnome.Mutter.RemoteDesktop.Session',method,args);
async function until(predicate,reason,tries=80) {for(let i=0;i<tries;i++){if(await predicate())return;await pause(75);}throw new Error(reason);}
async function chord(keys,scope='window') {
    if(scope==='window'&&!win.is_active)throw new Error('Input safety guard: test window is not focused');
    if(scope==='panel'&&!(await shell('GetStatus')).panelOpen)throw new Error('Input safety guard: panel is not open');
    try {for(const key of keys)await input('NotifyKeyboardKeysym',new GLib.Variant('(ub)',[key,true]));}
    finally {for(const key of [...keys].reverse())await input('NotifyKeyboardKeysym',new GLib.Variant('(ub)',[key,false]));}
    await pause(90);
}
async function read(mime) {
    return await new Promise((resolve,reject)=>{
        const cancel=new Gio.Cancellable();let total=0;
        const timer=GLib.timeout_add(GLib.PRIORITY_DEFAULT,5000,()=>{cancel.cancel();return GLib.SOURCE_REMOVE;});
        clipboard.read_async([mime],GLib.PRIORITY_DEFAULT,cancel,async(c,r)=>{
            let stream;
            try {
                [stream]=c.read_finish(r);const parts=[];
                while(true){const bytes=await new Promise((ok,bad)=>stream.read_bytes_async(65536,GLib.PRIORITY_DEFAULT,cancel,(s,v)=>{try{ok(s.read_bytes_finish(v).get_data());}catch(e){bad(e);}}));
                    if(!bytes.length)break;total+=bytes.length;if(total>100*1024*1024)throw new Error('Clipboard preservation limit exceeded');parts.push(bytes);}
                const bytes=new Uint8Array(total);let offset=0;for(const part of parts){bytes.set(part,offset);offset+=part.length;}resolve(bytes);
            }catch(e){reject(e);}finally{if(stream)stream.close(null);if(!cancel.is_cancelled())GLib.source_remove(timer);}
        });
    });
}
const equal = (a,b) => a.length===b.length&&a.every((v,i)=>v===b[i]);
function publish(payloads){const providers=[...payloads].map(([mime,bytes])=>Gdk.ContentProvider.new_for_bytes(mime,new GLib.Bytes(bytes)));clipboard.set_content(providers.length?Gdk.ContentProvider.new_union(providers):null);}
async function newEntry(before) {
    let entries=[];
    await until(async()=>{entries=(await daemon('GetSnapshot')).clipboard.entries.filter(e=>!before.has(e.id));return entries.length>0;},'Synthetic clipboard capture timed out');
    if(entries.length!==1)throw new Error('Concurrent clipboard change: cannot identify synthetic record safely');
    return entries[0].id;
}
async function run() {
    let before;
    try {
        await until(()=>win.is_active,'Test window never acquired focus; no input sent');
        remote=(await call('org.gnome.Mutter.RemoteDesktop','/org/gnome/Mutter/RemoteDesktop','org.gnome.Mutter.RemoteDesktop','CreateSession'))[0];
        await input('Start');check(true,'public Mutter input session started');
        await chord([0xffe1]); // Harmless Shift establishes a real Wayland input serial.
        const status=await shell('GetStatus');check(status.clipboardReady&&!status.panelOpen,'installed native clipboard bridge ready and panel initially closed');
        const snapshot=await daemon('GetSnapshot');originalPaused=snapshot.clipboard.paused;
        before=new Set(snapshot.clipboard.entries.map(e=>e.id));
        const formats=clipboard.get_formats().get_mime_types();
        for(const mime of formats)saved.set(mime,await read(mime));
        report.originalMimeCount=saved.size;
        check(true,'previous clipboard MIME data preserved in memory only');
        await command('pause',{paused:false});
        const marker=`ptrelogin${GLib.uuid_string_random().replaceAll('-','').slice(0,12)}`;
        const plain=`${marker}\nUbuntu 재로그인 후 실제 붙여넣기 검증`;
        const encode=s=>new TextEncoder().encode(s);
        const payloads=new Map([['text/plain;charset=utf-8',encode(plain)],['text/plain',encode(plain)],['text/html',encode(`<p>${marker} <b>보존된 서식</b></p>`)]]);
        changed=true;publish(payloads);
        const textId=await newEntry(before);synthetic.add(textId);before.add(textId);
        check(true,'text and HTML captured by installed extension and daemon');
        await command('pause',{paused:true});
        check((await shell('PutClipboard',new GLib.Variant('(sb)',[textId,false]))).copied,'archived clipboard restored through sealed native FDs');
        for(const [mime,bytes] of payloads)check(equal(await read(mime),bytes),`${mime} restore byte equality`);
        field.buffer.text='';
        await chord([0xffe3,0x76]);
        await until(()=>field.buffer.text===plain,'Ctrl+V did not reach the focused test window');
        check(true,'actual Ctrl+V pasted original text into GTK test target');
        field.buffer.text='';
        // Paste the ASCII query rather than type keysyms through the user's IME.
        // Recording is paused, so this control selection does not enter history.
        publish(new Map([['text/plain;charset=utf-8',encode(marker)],['text/plain',encode(marker)]]));
        await chord([0xffe3,0xffe1,0x76]);
        await until(async()=>(await shell('GetStatus')).panelOpen,'Ctrl+Shift+V did not open panel');
        check(true,'Ctrl+Shift+V opened installed quick clipboard');
        await chord([0xffe3,0x66],'panel');
        await chord([0xffe3,0x76],'panel');
        await chord([0xff0d],'panel');await pause(450);
        const matched=await command('query',{query:marker,filter:{}});
        check(matched.entries.length===1&&matched.entries[0].id===textId,'matching daemon search identifies only synthetic test record');
        await chord([0xff54],'panel');await chord([0xff0d],'panel');
        try{await until(()=>win.is_active&&field.buffer.text===plain,'Enter did not restore original target focus and paste');}
        catch(error){
            report.afterCardEnter={panelOpen:(await shell('GetStatus')).panelOpen,targetActive:win.is_active,expectedText:field.buffer.text===plain,targetEmpty:field.buffer.text===''};
            if(report.afterCardEnter.panelOpen){
                const rescue=await shell('PutClipboard',new GLib.Variant('(sb)',[textId,true]));
                await pause(700);
                report.directPasteProbe={reportedPasted:rescue.pasted??false,panelOpen:(await shell('GetStatus')).panelOpen,targetActive:win.is_active,expectedText:field.buffer.text===plain};
            }
            report.cardEnterFailure=error.message;
            if(!report.directPasteProbe?.expectedText||!report.directPasteProbe.targetActive)throw error;
        }
        if(!report.cardEnterFailure)check(!(await shell('GetStatus')).panelOpen,'card Enter closed panel, restored original GTK focus, and pasted correct text');
        await chord([0xffe3,0xffe1,0x76]);await until(async()=>(await shell('GetStatus')).panelOpen,'Reopen failed');
        await chord([0xff1b],'panel');await until(async()=>!(await shell('GetStatus')).panelOpen,'Esc did not close panel');
        check(win.is_active,'Escape closed panel and returned target focus');
        await chord([0xffe3,0xffe1,0x76]);await until(async()=>(await shell('GetStatus')).panelOpen,'Toggle reopen failed');
        await chord([0xffe3,0xffe1,0x76],'panel');await until(async()=>!(await shell('GetStatus')).panelOpen,'Repeated hotkey did not close panel');
        check(win.is_active,'repeated global shortcut closed panel and returned target focus');
        const pixbuf=GdkPixbuf.Pixbuf.new(GdkPixbuf.Colorspace.RGB,true,8,2,2);pixbuf.fill(Math.floor(Math.random()*0xffffff)*256+255);
        const [,png]=pixbuf.save_to_bufferv('png',[],[]);
        const imagePayloads=new Map([...payloads,['image/png',png]]);
        await command('pause',{paused:false});publish(imagePayloads);
        const imageId=await newEntry(before);synthetic.add(imageId);before.add(imageId);
        await command('pause',{paused:true});
        check((await shell('PutClipboard',new GLib.Variant('(sb)',[imageId,false]))).copied,'multi-MIME image archive restored');
        for(const [mime,bytes] of imagePayloads)check(equal(await read(mime),bytes),`multi-MIME ${mime} byte equality`);
        report.pass=!report.cardEnterFailure;
    }catch(error){report.error=error.message.replaceAll(output,'[test-output]');}
    finally {
        try{
            if((await shell('GetStatus')).panelOpen&&remote)await chord([0xff1b],'panel');
            if(changed){
                await command('pause',{paused:true});
                for(const id of synthetic)await command('delete',{id});
                check(true,'only identified synthetic archive records removed');
                win.present();field.grab_focus();await until(()=>win.is_active,'Cannot restore clipboard without focused test window');
                await chord([0xffe1]);publish(saved);await pause(350);
                for(const [mime,bytes] of saved)check(equal(await read(mime),bytes),'original clipboard MIME restored byte-for-byte');
                restored=true;keepClipboard=saved.size>0;
                report.clipboardPreserved=true;
            }
        }catch(error){report.pass=false;report.cleanupError=error.message;}
        try{if(originalPaused!==undefined){await command('pause',{paused:originalPaused});check((await daemon('GetSnapshot')).clipboard.paused===originalPaused,'original recording pause setting restored');}}
        catch{report.pauseRestoreFailed=true;report.pass=false;}
        if(remote)try{await input('Stop');}catch{report.inputStopError=true;report.pass=false;}
        report.finishedAt=new Date().toISOString();report.memoryOwnerUntilNextCopy=keepClipboard;report.clipboardRestorePending=changed&&!restored;
        report.syntheticEntriesDeleted=synthetic.size;
        Gio.File.new_for_path(`${output}/clipboard-result.json`).replace_contents(JSON.stringify(report,null,2),null,false,Gio.FileCreateFlags.PRIVATE,null);
        print(JSON.stringify({pass:report.pass,checks:report.checks.length,error:report.error??null,cleanupError:report.cleanupError??null,memoryOwnerUntilNextCopy:keepClipboard}));
        if(changed&&!restored){
            // Never discard a backup when focus restoration failed. Wait for an
            // explicit user activation of this test-only window, then restore.
            app.hold();field.buffer.text='클립보드 보존 상태입니다. 이 테스트 창을 한 번 클릭하면 원래 클립보드를 복원하고 자동으로 닫습니다.';
            const signal=win.connect('notify::is-active',()=>{
                if(!win.is_active)return;win.disconnect(signal);
                command('pause',{paused:true}).then(async()=>{
                    publish(saved);await pause(350);await command('pause',{paused:originalPaused});restored=true;
                    report.clipboardRestorePending=false;report.clipboardPreserved=true;
                    Gio.File.new_for_path(`${output}/clipboard-result.json`).replace_contents(JSON.stringify(report,null,2),null,false,Gio.FileCreateFlags.PRIVATE,null);
                    win.set_visible(false);
                    if(saved.size)clipboard.connect('changed',()=>{if(!clipboard.is_local()){saved.clear();app.release();app.quit();}});
                    else{app.release();app.quit();}
                }).catch(()=>{});
            });
            win.present();
        }else if(keepClipboard){
            app.hold();win.set_visible(false);
            clipboard.connect('changed',()=>{if(!clipboard.is_local()){saved.clear();app.release();app.quit();}});
        }else app.quit();
    }
}
app.connect('activate',()=>{
    win=new Gtk.ApplicationWindow({application:app,title:'Personal Tools — 재로그인 붙여넣기 검증',default_width:680,default_height:320});
    field=new Gtk.TextView({left_margin:24,right_margin:24,top_margin:24,bottom_margin:24,wrap_mode:Gtk.WrapMode.WORD_CHAR});
    field.buffer.text='이 창에서만 단축키와 붙여넣기를 자동 검증합니다. 검증이 끝나면 자동으로 닫힙니다.';
    win.set_child(field);win.present();field.grab_focus();clipboard=Gdk.Display.get_default().get_clipboard();
    GLib.timeout_add(GLib.PRIORITY_DEFAULT,500,()=>{run();return GLib.SOURCE_REMOVE;});
});
app.run([]);
