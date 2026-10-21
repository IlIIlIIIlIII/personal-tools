#!/usr/bin/gjs -m
// Real GTK helpers and page behavior, without presenting a window or changing
// the user's clipboard, settings, audio graph, or background services.
import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import GLib from 'gi://GLib';
import Adw from 'gi://Adw?version=1';
import {number} from '../../../Sources/Linux/App/ui.js';
import {Backend,FixtureBackend} from '../../../Sources/Linux/App/backend.js';
import {responseDb,soundPage} from '../../../Sources/Linux/App/pages/sound.js';
import {ClipboardPage,clipboardSettings} from '../../../Sources/Linux/App/pages/clipboard.js';
import {displayPage} from '../../../Sources/Linux/App/pages/display.js';
import {aiPage} from '../../../Sources/Linux/App/pages/ai.js';
import {resolvePageRoute} from '../../../Sources/Linux/App/routes.js';
import {installSearchShortcut} from '../../../Sources/Linux/App/keyboard.js';

Adw.init();
const results=[];
function assert(value,message){if(!value)throw new Error(message);}
function close(actual,expected,tolerance=1e-6){assert(Math.abs(actual-expected)<=tolerance,`Expected ${expected}, observed ${actual}`);}
async function test(name,run){try{await run();results.push({name,ok:true});}catch(error){results.push({name,ok:false,error:error.message});}}
function descendants(widget,predicate){const found=[];function visit(w){if(predicate(w))found.push(w);for(let c=w.get_first_child();c;c=c.get_next_sibling())visit(c);}visit(widget);return found;}
const tick=async()=>{await Promise.resolve();await Promise.resolve();};

await test('search shortcut captures ahead of editable children and invokes the focus action',()=>{
    const window=new Gtk.Window();let focused=0;
    const controller=installSearchShortcut(window,()=>focused++);
    assert(controller.propagation_phase===Gtk.PropagationPhase.CAPTURE,'Editable children can consume Ctrl+F before the search accelerator');
    const shortcut=controller.get_item(0);
    assert(shortcut.trigger.keyval===Gdk.KEY_f&&shortcut.trigger.modifiers===Gdk.ModifierType.CONTROL_MASK,'Search accelerator is not Ctrl+F');
    assert(shortcut.action.activate(Gtk.ShortcutActionFlags.EXCLUSIVE,window,null),'Search focus action was not handled');
    assert(focused===1,'Search focus action was not invoked exactly once');window.destroy();
});

await test('display deep links render the requested tab on first and repeated navigation',()=>{
    const client=new FixtureBackend('09-power');
    const ctx={client,get snapshot(){return client.snapshot;},viewState:{},width:1488,command:()=>{},navigate:()=>{}};
    for(const destination of ['display-power','display','display-power']){
        const route=resolvePageRoute(destination);
        assert(route?.page==='display','Display deep link selected an unknown page');
        Object.assign(ctx.viewState,{displayTab:route.displayTab});
        const labels=descendants(displayPage(ctx).widget,w=>w instanceof Gtk.Label).map(w=>w.label);
        if(destination==='display-power')assert(labels.includes('화면을 켜 두는 앱')&&!labels.includes('연결된 모니터'),'Power destination retained the HDR tab');
        else assert(labels.includes('연결된 모니터')&&!labels.includes('화면을 켜 두는 앱'),'HDR destination retained the power tab');
    }
    for(const destination of [null,undefined,'unknown','toString'])assert(resolvePageRoute(destination)===null,'Invalid destination resolved to a page');
});

await test('monitor power control sends the same command on both display tabs and shows failures',()=>{
    const client=new FixtureBackend('09-power');const commands=[];
    client.snapshot.display.monitorPower={available:true,enabled:true};
    const ctx={client,get snapshot(){return client.snapshot;},viewState:{},width:1488,
        command:(...args)=>commands.push(args),navigate:()=>{}};
    for(const tab of ['hdr','power']){
        ctx.viewState.displayTab=tab;
        const page=displayPage(ctx).widget;
        const control=descendants(page,w=>w instanceof Gtk.Switch)[0];
        assert(control.active&&control.sensitive,'Active monitor power state not shown');
        control.active=false;
        const [domain,action,payload]=commands.at(-1);
        assert(domain==='display'&&action==='monitor-power'&&payload.enabled===false,'Wrong disable command');
    }
    client.snapshot.display.monitorPower={available:false,enabled:false,error:'도우미 없음'};
    const page=displayPage(ctx).widget;
    assert(!descendants(page,w=>w instanceof Gtk.Switch)[0].sensitive,'Unavailable helper is actionable');
    assert(descendants(page,w=>w instanceof Gtk.Label).some(w=>w.label==='도우미 없음'),'Helper failure hidden');
});

await test('numeric controls retain exact initial values without issuing a command',()=>{
    for(const [value,min,max,step,digits] of [[100,1,1024,1,0],[-12,-24,12,.1,1],[3.21,.1,30,.01,2],[8877,1024,65535,1,0]]){
        const events=[];const control=number(value,min,max,step,digits,v=>events.push(v));
        close(control.value,value);assert(events.length===0,'Constructing a control issued a mutation');
        control.value=value+step;close(events.at(-1),value+step);
    }
});
await test('EQ curve obeys center gain, cascade addition, bypass and finite endpoints',()=>{
    const cut={frequency:1000,gain:-12,q:1};
    close(responseDb([cut],1000),-12);
    close(responseDb([cut,{...cut,gain:-6}],1000),-18);
    for(const frequency of [20,66,1000,10000,20000]){
        close(responseDb([{...cut,gain:0}],frequency),0);
        close(responseDb([{...cut,enabled:false}],frequency),0);
        assert(Number.isFinite(responseDb([cut],frequency)),'EQ response is nonfinite');
    }
});
await test('successive EQ field edits retain earlier edits before a rerender',async()=>{
    const client=new FixtureBackend('07-sound');const pending=[];
    const ctx={client,get snapshot(){return client.snapshot;},width:1488,window:new Gtk.Window(),
        command:(domain,action,payload)=>{const request=client.execute(domain,action,payload);pending.push(request);return request;},toast:()=>{}};
    const page=soundPage(ctx);const controls=descendants(page.widget,w=>w instanceof Gtk.SpinButton);
    assert(controls.length===12,`Expected 12 EQ controls, found ${controls.length}`);
    controls[0].value=80;await Promise.all(pending);
    controls[1].value=-6;await Promise.all(pending);
    controls[2].value=2;await Promise.all(pending);
    const filter=client.snapshot.audio.leftFilters[0];
    close(filter.frequency,80);close(filter.gain,-6);close(filter.q,2);
    assert(client.snapshot.audio.rightFilters[0].frequency===65,'Left edits changed the right channel');
    ctx.window.destroy();
});
await test('fresh audio devices remain visibly unselected until an actual choice is made',()=>{
    const client=new FixtureBackend('07-sound'),commands=[];
    client.snapshot.audio.outputId='';client.snapshot.audio.inputId='';
    const ctx={client,get snapshot(){return client.snapshot;},width:1488,window:new Gtk.Window(),
        command:(domain,action,payload)=>{commands.push({domain,action,payload});return Promise.resolve({});},toast:()=>{}};
    const page=soundPage(ctx),controls=descendants(page.widget,w=>w instanceof Gtk.DropDown);
    for(const [index,list,key] of [[0,client.snapshot.audio.outputs,'outputId'],[1,client.snapshot.audio.inputs,'inputId']]){
        const control=controls[index];
        assert(control.get_selected_item()?.string!==list[0].name,'Unconfigured device is falsely shown as selected');
        const model=control.get_model();let position=-1;
        for(let i=0;i<model.get_n_items();i++)if(model.get_item(i).string===list[0].name)position=i;
        assert(position>=0,'Actual hardware is absent from the chooser');control.selected=position;
        assert(commands.at(-1)?.payload[key]===list[0].id,'Choosing the only physical device did not configure it');
    }
    ctx.window.destroy();
});
await test('idle NPU standby stays distinct from real deadline and initialization failures',()=>{
    const client=new FixtureBackend('07-sound');
    const ctx={client,get snapshot(){return client.snapshot;},width:1488,window:new Gtk.Window(),command:()=>Promise.resolve({}),toast:()=>{}};
    const a=client.snapshot.audio;
    Object.assign(a,{status:'idle',captureConnected:true,referenceConnected:true,degraded:true,degradedReason:'TEST_NPU_DEADLINE_FAILURE',npu:{status:'ready',device:'NPU',error:null,inferences:100}});
    const labels=()=>descendants(soundPage(ctx).widget,w=>w instanceof Gtk.Label).map(w=>w.label).join('\n');
    const idle=labels();assert(!idle.includes('TEST_NPU_DEADLINE_FAILURE'),'Normal idle state displays a deadline failure');
    assert(idle.includes('앱이 마이크를 사용하면'),'Idle state does not explain when processing begins');
    a.status='running';assert(labels().includes('TEST_NPU_DEADLINE_FAILURE'),'Running deadline failure is hidden');
    a.status='idle';a.npu.status='error';a.npu.error='TEST_NPU_INITIALIZATION_FAILURE';a.degradedReason=a.npu.error;
    assert(labels().includes('TEST_NPU_INITIALIZATION_FAILURE'),'Real NPU initialization failure is hidden as standby');
    ctx.window.destroy();
});
await test('sound engine recovery updates errors and meters without replacing edited EQ controls',()=>{
    const client=new FixtureBackend('07-sound'),commands=[];
    const ctx={client,get snapshot(){return client.snapshot;},width:1488,window:new Gtk.Window(),command:(...args)=>{commands.push(args);return Promise.resolve({});},toast:()=>{}};
    Object.assign(client.snapshot.audio,{status:'unavailable',error:'TEST_CONNECTION_FAILURE',inputLevelDb:null,latencyMs:null});
    const page=soundPage(ctx);ctx.window.set_child(page.widget);
    const controls=descendants(page.widget,w=>w instanceof Gtk.SpinButton);controls[0].grab_focus();
    const labels=()=>descendants(page.widget,w=>w instanceof Gtk.Label).map(w=>w.label);
    assert(labels().includes('TEST_CONNECTION_FAILURE'),'Initial connection failure was not displayed');
    Object.assign(client.snapshot.audio,{status:'idle',error:null,latencyMs:12.5,inputLevelDb:-30,npu:{status:'ready',device:'NPU'},captureConnected:true,referenceConnected:true});
    page.updateSnapshot();
    assert(!labels().includes('TEST_CONNECTION_FAILURE'),'Recovered engine retained the connection failure');
    assert(descendants(ctx.headerStatus,w=>w instanceof Gtk.Label).some(w=>w.label==='엔진 연결됨'),'Healthy idle engine is displayed as waiting');
    assert(labels().includes('12.5 ms')&&labels().includes('NPU 실행 확인됨'),'Live latency or NPU state did not refresh');
    assert(descendants(page.widget,w=>w instanceof Gtk.LevelBar)[0].value===20,'Input meter did not refresh');
    assert(descendants(page.widget,w=>w instanceof Gtk.SpinButton)[0]===controls[0],'Runtime refresh replaced the edited EQ control');
    assert(commands.length===0,'Runtime refresh issued an audio mutation');ctx.window.destroy();
});
await test('sound polling preserves scroll and only configuration changes request reconstruction',async()=>{
    const client=new FixtureBackend('07-sound');
    const ctx={client,get snapshot(){return client.snapshot;},viewState:{},width:1488,
        window:new Gtk.Window({default_width:1400,default_height:500}),command:()=>{},toast:()=>{}};
    const settle=()=>new Promise(resolve=>GLib.timeout_add(GLib.PRIORITY_DEFAULT,100,()=>{resolve();return GLib.SOURCE_REMOVE;}));
    let page=soundPage(ctx);
    try{
        ctx.window.set_child(page.widget);ctx.window.present();await settle();
        const adjustment=page.widget.get_vadjustment();
        assert(adjustment.upper-adjustment.page_size>100,'Test page is not scrollable');
        adjustment.value=100;await settle();
        const before=adjustment.value;
        const control=descendants(page.widget,w=>w instanceof Gtk.SpinButton)[0];
        for(let i=0;i<5;i++){
            Object.assign(client.snapshot.audio,{inputLevelDb:-30+i,latencyMs:10+i});
            assert(page.updateSnapshot()===true,'Telemetry incorrectly requests a full render');
            await settle();close(adjustment.value,before);
        }
        client.snapshot.audio.leftFilters[0].gain=-5;
        assert(page.updateSnapshot()===false,'EQ changes were ignored');
        page.dispose();ctx.window.set_child(null);page=soundPage(ctx);ctx.window.set_child(page.widget);
        await settle();await settle();close(page.widget.get_vadjustment().value,before);
        assert(descendants(page.widget,w=>w instanceof Gtk.SpinButton)[0]!==control,'Configuration did not rebuild controls');
        close(descendants(page.widget,w=>w instanceof Gtk.SpinButton)[1].value,-5);
        page.dispose();ctx.window.set_child(new Gtk.Label({label:'다른 메뉴'}));await settle();
        page=soundPage(ctx);ctx.window.set_child(page.widget);await settle();await settle();
        close(page.widget.get_vadjustment().value,before);
    }finally{page.dispose();ctx.window.destroy();}
});
await test('sound channels reflow while editing and after revisiting at different widths',()=>{
    const client=new FixtureBackend('07-sound');
    const ctx={client,get snapshot(){return client.snapshot;},viewState:{},width:1488,effectiveWidth:1488,
        window:new Gtk.Window(),command:()=>{},toast:()=>{}};
    let page;
    try{
        for(const initial of [1488,1100,1488]){
            ctx.effectiveWidth=initial;page=soundPage(ctx);ctx.window.set_child(page.widget);
            const panels=descendants(page.widget,w=>w.has_css_class('sound-channel'));
            const channels=panels[0].get_parent();
            assert(channels.orientation===(initial<1250?Gtk.Orientation.VERTICAL:Gtk.Orientation.HORIZONTAL),'Revisited page used stale width');
            const control=descendants(page.widget,w=>w instanceof Gtk.SpinButton)[0];control.grab_focus();
            for(const width of [1100,1488,1249,1250]){
                ctx.effectiveWidth=width;page.updateLayout();
                assert(channels.orientation===(width<1250?Gtk.Orientation.VERTICAL:Gtk.Orientation.HORIZONTAL),'Focused input blocked reflow');
                assert(descendants(page.widget,w=>w instanceof Gtk.SpinButton)[0]===control,'Resize replaced EQ input');
            }
            page.dispose();ctx.window.set_child(new Gtk.Label({label:'다른 메뉴'}));
        }
    }finally{ctx.window.destroy();}
});
await test('fixture queries combine text, pinned, collection, type and source filters',async()=>{
    const client=new FixtureBackend('01-library');
    const query=async p=>(await client.execute('clipboard','query',p)).entries;
    assert((await query({query:'회의',filter:{pinnedOnly:true}})).map(x=>x.id).join()==='memo','Pinned query is incorrect');
    const images=await query({filter:{categoryId:'reference',formats:['image'],apps:['파일']}});
    assert(images.map(x=>x.id).sort().join()==='leaves,mountain','Combined filters are incorrect');
    assert((await query({query:'not-an-existing-word',filter:{}})).length===0,'Unmatched query returned records');
});
await test('unsaved clipboard settings survive snapshot-driven reconstruction and save exact values',async()=>{
    const client=new FixtureBackend('06-clipboard-settings'),navigations=[];
    const ctx={client,get snapshot(){return client.snapshot;},viewState:{},width:1488,fixture:true,
        window:new Gtk.Window(),command:(...args)=>client.execute(...args),toast:()=>{},navigate:page=>navigations.push(page)};
    let page=clipboardSettings(ctx);
    let values=descendants(page.widget,w=>w instanceof Gtk.SpinButton);
    values[0].value=256;values[1].value=50;
    const switches=descendants(page.widget,w=>w instanceof Gtk.Switch);
    const wanted=!switches[1].active;switches[1].active=wanted;
    // A different domain's observation reconstructs this page; no user save yet.
    client.snapshot.revision++;
    page=clipboardSettings(ctx);values=descendants(page.widget,w=>w instanceof Gtk.SpinButton);
    close(values[0].value,256);close(values[1].value,50);
    assert(descendants(page.widget,w=>w instanceof Gtk.Switch)[1].active===wanted,'AI draft reverted during reconstruction');
    ctx.headerActions[0].emit('clicked');await tick();
    close(client.snapshot.clipboard.settings.itemLimitBytes,256*1048576);
    close(client.snapshot.clipboard.settings.totalLimitBytes,50*1073741824);
    assert(client.snapshot.clipboard.settings.aiEnabled===wanted,'Saving changed the selected AI state');
    assert(navigations.at(-1)==='clipboard','Successful save did not return to the archive');
    // A later change from another application surface must be visible on reopen.
    client.snapshot.clipboard.settings.itemLimitBytes=300*1048576;
    page=clipboardSettings(ctx);
    close(descendants(page.widget,w=>w instanceof Gtk.SpinButton)[0].value,300);
    ctx.window.destroy();
});
await test('fixture date range produces the same empty result as a future-only range',async()=>{
    const client=new FixtureBackend('01-library');
    const result=await client.execute('clipboard','query',{filter:{from:'2099-01-01',through:'2099-12-31'}});
    assert(result.entries.length===0,'Date filters were ignored');
});
await test('archive keyboard actions require card focus and use the focused item',async()=>{
    const client=new FixtureBackend('01-library'),copied=[],deleted=[];
    client.putClipboard=async(id,paste)=>copied.push({id,paste});
    const ctx={client,get snapshot(){return client.snapshot;},viewState:{},width:1488,fixture:true,scene:'',
        window:new Gtk.Window(),render:()=>{},toast:()=>{},command:(...args)=>client.execute(...args)};
    const page=new ClipboardPage(ctx);ctx.window.set_child(page.widget);await tick();
    const model=page.widget.observe_controllers();let keys;
    for(let i=0;i<model.get_n_items();i++)if(model.get_item(i) instanceof Gtk.EventControllerKey)keys=model.get_item(i);
    assert(keys,'Archive keyboard handler is absent');
    // Observe the requested deletion without presenting its confirmation dialog.
    page.removeSelected=()=>deleted.push(page.selected.id);
    const filter=descendants(page.widget,w=>w instanceof Gtk.Button).find(w=>descendants(w,c=>c instanceof Gtk.Label).some(c=>c.label==='필터'));
    assert(filter,'Archive filter button is absent');ctx.window.set_focus(filter);
    for(const key of [Gdk.KEY_Return,Gdk.KEY_Delete])assert(!keys.emit('key-pressed',key,0,0),'A filter control key was consumed by the archive');
    assert(copied.length===0&&deleted.length===0,'A filter control triggered a card action');
    const input=new Gtk.Entry({text:'test query'});page.widget.append(input);ctx.window.set_focus(input);
    for(const key of [Gdk.KEY_Return,Gdk.KEY_Delete])assert(!keys.emit('key-pressed',key,0,0),'A text entry key was consumed by the archive');
    const first=page.entries[0],second=page.entries[1];assert(first&&second,'Two fixture entries are required');
    page.select(first);ctx.window.set_focus(page.cards.get(second.id));
    assert(keys.emit('key-pressed',Gdk.KEY_Return,0,0),'Focused card Enter was ignored');await tick();
    assert(copied.length===1&&copied[0].id===second.id&&copied[0].paste===false,'Enter copied the previous selection or requested paste');
    assert(page.selected.id===second.id,'Focus and selection diverged after copying');
    ctx.window.set_focus(page.cards.get(first.id));
    assert(keys.emit('key-pressed',Gdk.KEY_Delete,0,0),'Focused card Delete was ignored');
    assert(deleted.join()===first.id&&page.selected.id===first.id,'Delete targeted the old selection');
    page.dispose();ctx.window.destroy();
});
await test('independent inhibitor failures do not report a successful empty query',()=>{
    const client=new FixtureBackend('09-power');
    Object.assign(client.snapshot.display,{status:'ready',error:null,inhibitors:[],
        inhibitorStatus:'unavailable',inhibitorError:'GNOME_TEST_UNAVAILABLE',
        sleepInhibitorStatus:'stale',sleepInhibitorError:'LOGIND_TEST_STALE',
        sleepInhibitors:[{appName:'Old app',reason:'KEEP_OLD_SLEEP_ROW',source:'logind'}]});
    const ctx={client,get snapshot(){return client.snapshot;},viewState:{displayTab:'power'},command:()=>{},navigate:()=>{}};
    const page=displayPage(ctx);
    const text=descendants(page.widget,w=>w instanceof Gtk.Label).map(w=>w.label).join('\n');
    assert(text.includes('GNOME_TEST_UNAVAILABLE'),'GNOME inhibitor error is hidden');
    assert(text.includes('LOGIND_TEST_STALE'),'Independent logind failure is hidden');
    assert(text.includes('KEEP_OLD_SLEEP_ROW'),'Last observed logind row was discarded');
    assert(!text.includes('현재 화면을 켜 두는 요청이 없습니다.'),'Failed query is presented as a successful empty result');
});
await test('inhibitor rows select matching details with click, Enter and Space',()=>{
    const client=new FixtureBackend('09-power');
    const first={id:'first',appName:'First app',appId:'org.test.first',reason:'FIRST_REASON',scope:'FIRST_SCOPE',source:'GNOME'};
    const second={id:'second',appName:'Second app',appId:'org.test.second',reason:'SECOND_REASON',scope:'SECOND_SCOPE',source:'GNOME'};
    Object.assign(client.snapshot.display,{error:'HDR_ONLY_FAILURE',inhibitors:[first,second]});
    let page,renders=0;
    const ctx={client,get snapshot(){return client.snapshot;},viewState:{displayTab:'power'},width:1488,
        command:()=>{},navigate:()=>{},render:()=>{renders++;page=displayPage(ctx);}};
    ctx.render();
    const rows=()=>descendants(page.widget,w=>w.has_css_class('table-row')).slice(0,2);
    function controller(row,type){const model=row.observe_controllers();for(let i=0;i<model.get_n_items();i++){const item=model.get_item(i);if(item instanceof type)return item;}throw new Error('Input controller is absent');}
    function selected(item){
        assert(ctx.viewState.inhibitorId===item.id,'Selection did not retain the requested inhibitor ID');
        const details=descendants(page.widget,w=>w.has_css_class('inhibitor-details'))[0];
        const labels=descendants(details,w=>w instanceof Gtk.Label).map(w=>w.label).join('\n');
        assert(labels.includes(item.appId)&&labels.includes(item.scope)&&labels.includes(item.reason),'Details do not describe the selected inhibitor');
        assert(rows()[item===first?0:1].has_css_class('selected'),'Selected row is not visibly marked');
        assert(!descendants(page.widget,w=>w instanceof Gtk.Label).some(w=>w.label.includes('HDR_ONLY_FAILURE')),'Monitor error leaked into power requests');
    }
    assert(rows().every(w=>w.focusable),'Inhibitor rows cannot receive keyboard focus');
    controller(rows()[1],Gtk.GestureClick).emit('released',1,0,0);selected(second);
    assert(controller(rows()[0],Gtk.EventControllerKey).emit('key-pressed',Gdk.KEY_Return,0,0),'Enter was not consumed');selected(first);
    assert(controller(rows()[1],Gtk.EventControllerKey).emit('key-pressed',Gdk.KEY_space,0,0),'Space was not consumed');selected(second);
    assert(renders===4,'Selection did not trigger exactly one reconstruction per action');
});
await test('Antigravity test retains its pending state and result across unrelated snapshots',async()=>{
    const client=new FixtureBackend('10-antigravity'),requests=[];
    const execute=client.execute.bind(client);
    client.execute=(domain,action,payload)=>domain==='antigravity'&&action==='test'
        ?new Promise((resolve,reject)=>requests.push({payload,resolve,reject})):execute(domain,action,payload);
    let page;
    const ctx={client,get snapshot(){return client.snapshot;},viewState:{aiTab:'antigravity'},width:1488,fixture:true,
        window:new Gtk.Window(),command:(...args)=>client.execute(...args),toast:()=>{},render:()=>{page=aiPage(ctx);}};
    const run=()=>descendants(page.widget,w=>w instanceof Gtk.Button).find(w=>descendants(w,c=>c instanceof Gtk.Label).some(c=>c.label==='테스트 실행'));
    const labels=()=>descendants(page.widget,w=>w instanceof Gtk.Label).map(w=>w.label).join('\n');
    try{
        ctx.render();const prompt=descendants(page.widget,w=>w instanceof Gtk.TextView)[0];prompt.buffer.text='PERSISTED_TEST_PROMPT';
        run().emit('clicked');await tick();assert(requests.length===1,'Test request did not start exactly once');
        client.snapshot.audio.inputLevelDb=-31;client.snapshot.revision++;ctx.render();
        assert(!run().sensitive,'An unrelated snapshot enabled a duplicate request while the first is pending');
        assert(labels().includes('응답을 기다리고 있습니다'),'Pending response disappeared during reconstruction');
        assert(descendants(page.widget,w=>w instanceof Gtk.TextView)[0].buffer.text==='PERSISTED_TEST_PROMPT','Test prompt changed during reconstruction');
        requests[0].resolve({response:'LATEST_TEST_RESPONSE'});await tick();
        assert(labels().includes('LATEST_TEST_RESPONSE')&&run().sensitive,'Completed response or enabled state did not reach the reconstructed page');
        run().emit('clicked');await tick();assert(requests.length===2,'Retry did not start');
        client.snapshot.revision++;ctx.render();requests[1].reject(new Error('TEST_CONNECTION_FAILURE'));await tick();ctx.render();
        assert(labels().includes('TEST_CONNECTION_FAILURE')&&run().sensitive,'Failed response was lost on the next snapshot');
    }finally{for(const request of requests)request.resolve({response:'cleanup'});await tick();ctx.window.destroy();}
});
await test('model lookup renders busy, results, errors and retry across polling and navigation',async()=>{
    const client=new FixtureBackend('10-antigravity'),requests=[];
    // Deliberately leave snapshot.models stale: the command response must be used.
    client.snapshot.ai.antigravity.models=[];
    client.execute=()=>new Promise((resolve,reject)=>requests.push({resolve,reject}));
    const ctx={client,get snapshot(){return client.snapshot;},viewState:{aiTab:'antigravity'},width:1488,
        window:new Gtk.Window(),command:()=>{},toast:()=>{},render:()=>{page=aiPage(ctx);ctx.window.set_child(page.widget);}};
    let page;
    const labels=()=>descendants(page.widget,w=>w instanceof Gtk.Label).map(w=>w.label);
    const check=()=>descendants(page.widget,w=>w instanceof Gtk.Button).find(b=>descendants(b,w=>w instanceof Gtk.Label).some(l=>l.label.startsWith('모델 확인')));
    const items=()=>{const d=descendants(page.widget,w=>w instanceof Gtk.DropDown)[0].model;return Array.from({length:d.get_n_items()},(_,i)=>d.get_item(i).string);};
    try{
        ctx.render();check().emit('clicked');
        assert(requests.length===1&&!check().sensitive,'Lookup did not start or disable its button');
        check().emit('clicked');assert(requests.length===1,'Duplicate lookup started');
        ctx.render();assert(!check().sensitive&&labels().includes('CLI에서 모델 목록을 조회하고 있습니다…'),'Polling erased pending state');
        ctx.viewState.aiTab='vllm';ctx.render();
        requests[0].resolve({models:['antigravity','test-model-a','test-model-b']});await tick();
        assert(ctx.viewState.aiTab==='vllm','Late result changed tabs');
        ctx.viewState.aiTab='antigravity';ctx.render();
        assert(items().includes('test-model-a')&&items().includes('test-model-b'),'Returned models were not rendered');
        assert(labels().includes('사용 가능한 모델 2개를 확인했습니다.'),'Success has no visible feedback');
        check().emit('clicked');requests[1].reject(new Error('TEST_MODEL_FAILURE'));await tick();ctx.render();
        assert(labels().includes('TEST_MODEL_FAILURE')&&check().sensitive,'Error or retry button missing');
        assert(items().includes('test-model-a'),'Failed lookup discarded the previous list');
        check().emit('clicked');requests[2].resolve({models:['antigravity']});await tick();
        assert(labels().includes('개별 모델 목록이 없습니다. CLI 기본 모델을 사용합니다.'),'Default-only result is silent');
        check().emit('clicked');requests[3].resolve({});await tick();
        assert(labels().includes('모델 목록 응답을 확인할 수 없습니다. 다시 시도해 주세요.'),'Malformed response looks successful');
    }finally{ctx.window.destroy();}
});
await test('an unsent Antigravity prompt survives reconstruction after leaving the editor',()=>{
    const client=new FixtureBackend('10-antigravity');
    const ctx={client,get snapshot(){return client.snapshot;},viewState:{aiTab:'antigravity'},width:1488,fixture:true,
        window:new Gtk.Window(),command:(...args)=>client.execute(...args),toast:()=>{},render:()=>{}};
    try{
        let page=aiPage(ctx);descendants(page.widget,w=>w instanceof Gtk.TextView)[0].buffer.text='UNSENT_PROMPT_DRAFT';
        client.snapshot.revision++;page=aiPage(ctx);
        assert(descendants(page.widget,w=>w instanceof Gtk.TextView)[0].buffer.text==='UNSENT_PROMPT_DRAFT','Unsent prompt was lost once input focus stopped suppressing reconstruction');
    }finally{ctx.window.destroy();}
});
await test('a delayed snapshot reply cannot overwrite a newer revision',async()=>{
    const backend=new Backend(),requests=[];
    backend.proxy={
        g_name_owner:':test-owner',
        get_name_owner:()=>':test-owner',
        call:(_method,_args,_flags,_timeout,_cancel,callback)=>requests.push(callback),
        call_finish:result=>({deep_unpack:()=>[JSON.stringify(result)]}),
    };
    const first=backend.refresh(),second=backend.refresh();
    const base=new FixtureBackend('01-library').snapshot;
    requests[1](backend.proxy,{...base,revision:12});await second;
    requests[0](backend.proxy,{...base,revision:11});await first;
    assert(backend.snapshot.revision===12,'An old snapshot reply replaced revision 12');
});
await test('late and disposed AI requests cannot replace the latest result',async()=>{
    const base=new FixtureBackend('01-library'),requests=[],cancelled=[];
    const client={snapshot:base.snapshot,execute:(_domain,action,payload)=>{
        if(action==='cancel-search'){cancelled.push(payload.requestId);return Promise.resolve({});}
        return new Promise((resolve,reject)=>requests.push({action,payload,resolve,reject}));
    }};
    const ctx={client,get snapshot(){return client.snapshot;},viewState:{},width:1488,fixture:true,scene:'',window:new Gtk.Window(),render:()=>{},toast:()=>{}};
    const page=new ClipboardPage(ctx);requests.shift().resolve({entries:[]});await tick();
    const oldPromise=page.search('old','ai'),old=requests.shift();
    const newestPromise=page.search('new','ai'),newest=requests.shift();
    const newEntry={...base.snapshot.clipboard.entries[0],id:'new-result'};
    newest.resolve({entries:[newEntry],summary:'new summary'});await newestPromise;
    old.resolve({entries:[{...newEntry,id:'old-result'}],summary:'old summary'});await oldPromise;
    assert(page.entries[0].id==='new-result','Stale AI result replaced the latest result');
    assert(page.summary==='new summary','Stale AI summary replaced the latest summary');
    assert(cancelled.includes(old.payload.requestId),'Old AI request was not cancelled');
    const disposedPromise=page.search('disposed','ai'),disposed=requests.shift();page.dispose();
    disposed.resolve({entries:[{...newEntry,id:'disposed-result'}]});await disposedPromise;
    assert(page.entries[0].id==='new-result','Disposed page accepted a late response');
    assert(cancelled.includes(disposed.payload.requestId),'Disposed AI request was not cancelled');
    ctx.window.destroy();
});
print(JSON.stringify({ok:results.every(result=>result.ok),checks:results},null,2));
if(results.some(result=>!result.ok))imports.system.exit(1);
