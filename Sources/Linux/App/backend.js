import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {fixtureSnapshot} from './fixtures.js';
import {WindowShortcutClient, WINDOW_SLOT_KEYS} from './windowShortcuts.js';

const NAME = 'io.griplabs.PersonalTools';
const PATH = '/io/griplabs/PersonalTools';
const IFACE = 'io.griplabs.PersonalTools1';
export function emptySnapshot() {
    return {version:1,revision:0,service:{status:'unavailable'},clipboard:{paused:false,entries:[],collections:[],settings:{archivePath:'',itemLimitBytes:104857600,totalLimitBytes:10737418240,excludedApps:[]}},audio:{status:'unavailable',outputs:[],inputs:[],leftFilters:[],rightFilters:[],npu:{status:'unavailable'}},display:{status:'unknown',monitors:[],rules:[],inhibitors:[],sleepInhibitors:[]},ai:{antigravity:{status:'stopped',port:8877,timeoutSeconds:120,models:[],baseUrl:'http://127.0.0.1:8877/v1'},vllm:{status:'stopped',model:'qwen3.8-27b',context:8192,steps:[],baseUrl:'http://127.0.0.1:8000/v1'}},settings:{theme:'system',background:true,clipboardShortcut:'<Control><Shift>v',microphoneShortcut:'<Super><Alt>m'},diagnostics:{components:[],events:[]}};
}
function call(proxy, method, variant, timeout = 30000) {
    return new Promise((resolve,reject) => proxy.call(method,variant,Gio.DBusCallFlags.NONE,timeout,null,(_p,result)=>{
        try {resolve(proxy.call_finish(result).deep_unpack());} catch(error){reject(error);}
    }));
}
export class Backend {
    constructor() { this.snapshot=emptySnapshot();this.listeners=new Set();this.error=null;this.fixture=false;this.refreshGeneration=0;this.windowShortcuts=new WindowShortcutClient(()=>this.changed()); }
    changed() {for(const callback of this.listeners) callback(this.snapshot);}
    subscribe(callback) {this.listeners.add(callback);return()=>this.listeners.delete(callback);}
    async start() {
        await Promise.all([this._startService(), this.windowShortcuts.start()]);
    }
    async _startService() {
        try {
            this.proxy=await new Promise((resolve,reject)=>Gio.DBusProxy.new_for_bus(Gio.BusType.SESSION,Gio.DBusProxyFlags.NONE,null,NAME,PATH,IFACE,null,(_o,res)=>{try{resolve(Gio.DBusProxy.new_for_bus_finish(res));}catch(e){reject(e);}}));
            this.proxy.connect('g-signal',(_p,_sender,name,args)=>{try{if(name==='Changed'){const next=JSON.parse(args.deep_unpack()[0]);if(next.revision>=this.snapshot.revision){this.snapshot=next;this.error=null;this.changed();}}else if(name==='ClipboardChanged')this.refresh();}catch(e){console.error(e.message);}});
            this.proxy.connect('notify::g-name-owner',()=>{this.snapshot=emptySnapshot();this.snapshot.revision=-1;this.refreshGeneration++;this.refresh();});
            await this.refresh();
        }catch(error){this.error=error.message;this.changed();}
    }
    async refresh() {
        if(!this.proxy)return;
        const generation=++this.refreshGeneration;
        try{const [json]=await call(this.proxy,'GetSnapshot',null);const next=JSON.parse(json);if(generation!==this.refreshGeneration||next.revision<this.snapshot.revision)return;this.snapshot=next;this.error=null;this.changed();}
        catch(error){if(generation!==this.refreshGeneration)return;this.error=`사용자 서비스에 연결할 수 없습니다. ${error.message}`;this.snapshot.service.status='unavailable';this.changed();}
    }
    async execute(domain,action,payload={}) {
        if(!this.proxy)throw new Error('Personal Tools 사용자 서비스가 실행 중인지 확인해 주세요.');
        const [json]=await call(this.proxy,'Execute',new GLib.Variant('(sss)',[domain,action,JSON.stringify(payload)]),action==='ai-search'?180000:30000);
        const result=JSON.parse(json);if(!result.ok)throw new Error(result.error?.message||'작업을 완료하지 못했습니다.');return result.data;
    }
    async putClipboard(id,paste=false) {
        const shell=Gio.DBusProxy.new_for_bus_sync(Gio.BusType.SESSION,Gio.DBusProxyFlags.NONE,null,`${NAME}.Shell`,`${PATH}/Shell`,`${NAME}.Shell1`,null);
        const [json]=await call(shell,'PutClipboard',new GLib.Variant('(sb)',[id,paste]));
        const result=JSON.parse(json);if(!result.ok)throw new Error(result.error?.message||'GNOME 확장 연결을 확인해 주세요.');return result.data;
    }
}
export class FixtureBackend extends Backend {
    constructor(screen){
        super();this.fixture=true;this.snapshot=fixtureSnapshot(screen);
        const examples=[['personal-tools — 창 전환 기능 구현','코드 편집기'],['프로젝트 문서 — 브라우저','웹 브라우저'],['프로젝트 문서 — 다른 작업공간의 브라우저 창','웹 브라우저'],['문서','파일']];
        this.windowShortcuts.state={state:'connected',ready:true,registered:screen==='19-windows-empty'?0:4,error:null,
            slots:WINDOW_SLOT_KEYS.map((key,index)=>({key,registered:screen!=='19-windows-empty'&&index<4,title:screen==='19-windows-empty'?'':examples[index]?.[0]||'',appName:examples[index]?.[1]||''}))};
        if(screen==='20-windows-outdated')this.windowShortcuts.state={state:'outdated',ready:false,slots:[]};
    }
    async start(){this.changed();}
    async refresh(){this.changed();}
    async putClipboard(){return {fixture:true};}
    async execute(domain,action,p={}) {
        const s=this.snapshot,c=s.clipboard;
        if(domain==='clipboard') {
            const item=c.entries.find(e=>e.id===p.id);
            if(action==='query') {const f=p.filter||{};const entries=c.entries.filter(e=>(!p.query||`${e.title} ${e.preview} ${e.tags.join(' ')}`.toLowerCase().includes(p.query.toLowerCase()))&&(!f.pinnedOnly||e.pinned)&&(!f.categoryId||e.categoryIds.includes(f.categoryId))&&(!f.formats?.length||f.formats.includes(e.kind))&&(!f.apps?.length||f.apps.includes(e.sourceApp))&&(!f.from||new Date(e.createdAt)>=new Date(f.from))&&(!f.through||new Date(e.createdAt)<new Date(new Date(f.through).getTime()+86400000)));return{entries:entries.slice(p.offset||0,(p.offset||0)+(p.limit||60)),total:entries.length};}
            if(action==='pause')c.paused=p.paused;
            if(action==='pin'&&item)item.pinned=p.pinned;
            if(action==='delete')c.entries=c.entries.filter(e=>e.id!==p.id);
            if(action==='settings')Object.assign(c.settings,p);
            if(action==='save-collection'){if(!p.name?.trim())throw new Error('모음 이름을 입력해 주세요.');const id=p.id||GLib.uuid_string_random();const old=c.collections.find(e=>e.id===id);if(old)old.name=p.name;else c.collections.push({id,name:p.name});}
            if(action==='delete-collection'){c.collections=c.collections.filter(e=>e.id!==p.id);c.entries.forEach(e=>e.categoryIds=e.categoryIds.filter(id=>id!==p.id));}
            if(action==='set-collections'&&item)item.categoryIds=p.categoryIds;
            if(action==='detail')return{entry:item,text:item?.preview,imageUri:item?.thumbnailUri,width:item?.kind==='image'?1920:null,height:item?.kind==='image'?1080:null,files:item?.attachments};
            if(action==='tag'&&item){item.tags=['풍경','참고'];item.tagState='done';}
            if(action==='ai-search')return{entries:c.entries.filter(e=>e.kind==='image'),summary:'참고 자료에서 관련 이미지 2개를 찾았습니다.'};
        }
        if(domain==='audio'){if(action==='configure')Object.assign(s.audio,p);if(action==='mute')s.audio.muted=p.muted??!s.audio.muted;if(action==='import-eq')s.audio[`${p.channel}File`]=p.path.split('/').pop();}
        if(domain==='display'){if(action==='hdr'){const m=s.display.monitors.find(e=>e.id===p.id);if(m)m.hdrEnabled=p.enabled;}if(action==='auto-hdr')s.display.autoHdr=p.enabled;if(action==='save-rule')s.display.rules.push({...p,id:p.id||GLib.uuid_string_random()});if(action==='delete-rule')s.display.rules=s.display.rules.filter(e=>e.id!==p.id);}
        if(domain==='antigravity'){if(action==='configure')Object.assign(s.ai.antigravity,p);if(action==='start')s.ai.antigravity.status='running';if(action==='stop')s.ai.antigravity.status='stopped';if(action==='test')return{response:'안녕하세요! 무엇을 도와드릴까요?'};if(action==='models')return{models:['antigravity']};if(action==='api-key')return{apiKey:'디자인 검수용 · 실제 키 없음'};}
        if(domain==='vllm'){if(action==='start')s.ai.vllm.status='starting';if(action==='stop')s.ai.vllm.status='stopped';if(action==='configure')Object.assign(s.ai.vllm,p);if(action==='logs')return{text:'[디자인 검수용]\n모델 가중치를 불러오는 중입니다.'};}
        if(domain==='settings'&&action==='update'){Object.assign(s.settings,p);if('extensionEnabled'in p)s.settings.extensionConnected=p.extensionEnabled;}
        if(domain==='diagnostics'&&action==='audio')return s.audio;
        if(domain==='diagnostics'&&action==='export')return{path:GLib.get_tmp_dir()};
        s.revision++;this.changed();return{};
    }
}
