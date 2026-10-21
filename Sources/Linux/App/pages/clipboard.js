import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import GLib from 'gi://GLib';
import Adw from 'gi://Adw?version=1';
import Pango from 'gi://Pango';
import {box,label,text,icon,button,iconButton,entry,toggle,number,spacer,group,heading,preference,scroller,segmented,picture,openUri,keycaps,shortcutKeys,clear,modal,separator,fileChooser,formatTime} from '../ui.js';

const formats=[['text','텍스트'],['url','URL'],['image','이미지'],['files','파일']];
const typeIcons={text:'text-x-generic-symbolic',url:'insert-link-symbolic',image:'image-x-generic-symbolic',files:'text-x-generic-symbolic'};
export class ClipboardPage {
    constructor(ctx){
        this.ctx=ctx;this.scene=ctx.scene;
        this.state=ctx.viewState.clipboard??={query:'',mode:'normal',filter:{formats:[],apps:[]},selectedId:null};
        this.widget=box(true,20,'page-content');this.widget.vexpand=true;
        const tabs=this.tabs=box(false,0,'segmented');
        this.buildTabs();
        const add=iconButton('list-add-symbolic','모음 관리',()=>this.collections());
        this.filterBadge=label('','caption');this.updateFilterBadge();const filterButton=button('필터','nautilus-search-filters-symbolic',()=>this.filters(filterButton));
        const tabViewport=scroller(tabs,true);tabViewport.vexpand=false;tabViewport.hexpand=false;tabViewport.propagate_natural_width=true;tabViewport.max_content_width=Math.max(240,ctx.width-550);tabViewport.min_content_height=46;this.widget.append(box(false,16,'',[tabViewport,add,spacer(),this.filterBadge,filterButton]));
        this.pausedBanner=box(false,16,'status-banner',[label('기록이 일시정지되었습니다.'),spacer(),button('다시 시작',null,()=>ctx.command('clipboard','pause',{paused:false}))]);this.pausedBanner.visible=ctx.snapshot.clipboard.paused;this.widget.append(this.pausedBanner);this.dataFingerprint=JSON.stringify(ctx.snapshot.clipboard.entries);this.collectionsFingerprint=JSON.stringify(ctx.snapshot.clipboard.collections);
        this.results=box(true,12);this.results.vexpand=true;
        this.widget.append(this.results);
        this.bar=box(false,16,'selection-bar');this.widget.append(this.bar);
        this.widget.append(box(false,12,'',[icon('input-keyboard-symbolic',20),label('Enter 복사 · Ctrl+F 검색','caption'),spacer(),ctx.fixture?label('디자인 검수용 예시 상태','small'):label('기록은 이 컴퓨터에 보관됩니다.','small')]));
        this.refresh();
        const keys=new Gtk.EventControllerKey();keys.connect('key-pressed',(_k,key)=>{let focus=ctx.window.get_focus();while(focus&&!focus.has_css_class('clip-card'))focus=focus.get_parent();if(!focus)return false;const id=[...(this.cards||[])].find(([,card])=>card===focus)?.[0];const item=this.entries?.find(e=>e.id===id);if(!item)return false;if(key===Gdk.KEY_Delete){this.select(item);this.removeSelected();return true;}if(key===Gdk.KEY_Return){this.select(item);this.copy(item);return true;}return false;});this.widget.add_controller(keys);
        if(ctx.scene==='05-filter')GLib.idle_add(GLib.PRIORITY_DEFAULT_IDLE,()=>{this.filters(filterButton);return GLib.SOURCE_REMOVE;});
        if(ctx.scene==='15-collections')GLib.idle_add(GLib.PRIORITY_DEFAULT_IDLE,()=>{this.collections();return GLib.SOURCE_REMOVE;});
        if(ctx.scene==='04-detail')GLib.idle_add(GLib.PRIORITY_DEFAULT_IDLE,()=>{this.preview(ctx.snapshot.clipboard.entries.find(e=>e.kind==='image'));return GLib.SOURCE_REMOVE;});
    }
    updateFilterBadge(){const f=this.state.filter;const count=(f.apps?.length?1:0)+(f.formats?.length?1:0)+(f.from||f.through?1:0);this.filterBadge.label=count?`필터 ${count}개`:'';this.filterBadge.visible=count>0;}
    buildTabs(){
        clear(this.tabs);
        const definitions=[{id:null,name:'전체'},{id:'pinned',name:'고정'},...this.ctx.snapshot.clipboard.collections];
        definitions.forEach(c=>{const b=button(c.name,null,()=>{this.state.categoryId=c.id==='pinned'?null:c.id;this.state.pinnedOnly=c.id==='pinned';this.ctx.render();});if((c.id==='pinned'&&this.state.pinnedOnly)||(!this.state.pinnedOnly&&c.id===(this.state.categoryId??null)))b.add_css_class('selected');this.tabs.append(b);});
    }
    updateSnapshot(){
        const snapshot=this.ctx.snapshot.clipboard;this.pausedBanner.visible=snapshot.paused;
        const collections=JSON.stringify(snapshot.collections);if(collections!==this.collectionsFingerprint){this.collectionsFingerprint=collections;this.buildTabs();}
        const data=JSON.stringify(snapshot.entries);if(data!==this.dataFingerprint){this.dataFingerprint=data;if(!this.aiRequest)this.refresh();}
    }
    async search(query,mode){this.state.query=query;this.state.mode=mode;await this.refresh();}
    async refresh(append=false){
        const previousScroll=append?this.resultScroll?.vadjustment.value||0:0;
        if(this.aiRequest){this.ctx.client.execute('clipboard','cancel-search',{requestId:this.aiRequest}).catch(()=>{});this.aiRequest=null;}
        const revision=this.request=(this.request??0)+1;
        let entries=[],summary=null,total=0;
        try{
            if(this.state.mode==='ai'&&this.state.query){
                this.showMessage('관련 기록을 찾고 있습니다.','선택한 모음의 기록을 검색합니다.','content-loading-symbolic',false,true);
                const requestId=GLib.uuid_string_random();this.aiRequest=requestId;
                const result=await this.ctx.client.execute('clipboard','ai-search',{query:this.state.query,categoryId:this.state.categoryId,requestId});
                entries=result?.entries??[];summary=result?.summary;
            }else{const result=await this.ctx.client.execute('clipboard','query',{query:this.state.query,offset:append?(this.entries?.length||0):0,limit:60,filter:{...this.state.filter,categoryId:this.state.categoryId,pinnedOnly:this.state.pinnedOnly}});entries=result?.entries??[];total=result?.total??entries.length;}
        }catch(error){if(revision===this.request){this.aiRequest=null;this.showMessage(this.state.mode==='ai'?'AI 서비스에 연결할 수 없어요':'기록을 불러올 수 없어요',error.message,'dialog-warning-symbolic',true);}return;}
        if(revision!==this.request)return;
        this.aiRequest=null;this.summary=summary;
        if(append)entries=[...(this.entries||[]),...entries];this.entries=entries;clear(this.results);
        if(!entries.length){this.showMessage(this.state.query||this.state.categoryId?'일치하는 기록이 없어요':'아직 복사한 기록이 없어요',this.state.query?'검색어나 필터를 바꿔 다시 찾아보세요.':'텍스트, 이미지, 파일을 복사하면\n여기에 차곡차곡 보관됩니다.','edit-paste-symbolic');this.bar.visible=false;return;}
        if(this.summary)this.results.append(text(this.summary,'status-banner'));
        const today=new Date().toDateString();const allToday=this.ctx.fixture||entries.every(e=>new Date(e.createdAt).toDateString()===today);
        this.results.append(label(this.state.query?'검색 결과':allToday?'오늘':'최근 기록','strong'));
        const columns=(this.ctx.effectiveWidth??this.ctx.width)>=1300?3:2;
        this.grid=new Gtk.Grid({column_homogeneous:true,column_spacing:20,row_spacing:20,halign:Gtk.Align.FILL,valign:Gtk.Align.START,css_classes:['clip-grid']});
        this.cards=new Map();
        entries.forEach((item,index)=>{const card=this.card(item,index);this.cards.set(item.id,card);this.grid.attach(card,index%columns,Math.floor(index/columns),1,1);});
        const list=box(true,16,'',[this.grid]);if(entries.length<total){const more=button(`더 보기 · ${entries.length} / ${total}`,null,()=>this.refresh(true));more.halign=Gtk.Align.CENTER;list.append(more);}this.resultScroll=scroller(list);this.results.append(this.resultScroll);if(append)GLib.idle_add(GLib.PRIORITY_DEFAULT_IDLE,()=>{if(revision===this.request)this.resultScroll.vadjustment.value=previousScroll;return GLib.SOURCE_REMOVE;});
        this.select(entries.find(e=>e.id===this.state.selectedId)||entries[0]);
    }
    showMessage(title,description,iconName,retry=false,loading=false){
        clear(this.results);const area=box(true,retry||loading?24:36);area.halign=Gtk.Align.CENTER;area.valign=Gtk.Align.CENTER;area.vexpand=true;
        const symbol=icon(iconName,retry||loading?96:128);symbol.add_css_class(retry?'warning':'empty-icon');area.append(symbol);
        area.append(label(title,'empty-title',{xalign:0.5}));const detail=text(description,'muted');detail.justify=Gtk.Justification.CENTER;detail.max_width_chars=56;area.append(detail);
        if(retry){area.append(box(false,12,'',[button('다시 시도',null,()=>this.refresh(),'primary'),button('일반 검색으로 전환',null,()=>{this.state.mode='normal';this.ctx.render();}),button('연결 설정',null,()=>this.ctx.navigate('ai'))]));}
        else if(loading){area.append(new Gtk.Spinner({spinning:true}));area.append(button('검색 취소',null,()=>{this.state.mode='normal';this.refresh();}));}
        else{area.append(keycaps(shortcutKeys(this.ctx.snapshot.settings.clipboardShortcut)));area.append(label('어디서든 빠르게 열기','caption',{xalign:0.5}));area.append(button('클립보드 설정','emblem-system-symbolic',()=>this.ctx.navigate('clipboard-settings')));}
        for(let child=area.get_first_child();child;child=child.get_next_sibling())child.halign=Gtk.Align.CENTER;this.results.append(area);this.bar.visible=false;
    }
    card(item,index){
        const content=box(true,8);const title=label(item.title||item.preview.split('\n')[0],'card-title',{ellipsize:Pango.EllipsizeMode.END,hexpand:true,max_width_chars:24});
        content.append(box(false,14,'',[icon(typeIcons[item.kind]||typeIcons.text),title,item.pinned?icon('view-pin-symbolic',20):null]));
        if(item.kind==='image'&&item.thumbnailUri){const img=picture(item.thumbnailUri,(this.ctx.effectiveWidth??this.ctx.width)<1300?218:index<3?138:110);img.vexpand=true;content.append(img);}
        else if(item.kind==='url'&&item.preview.includes('documentation.ubuntu')){
            const ubuntu=icon('distributor-logo',48);const site=text('Ubuntu documentation');site.max_width_chars=24;const description=text('The official Ubuntu documentation','caption',1);description.max_width_chars=28;content.append(box(true,18,'clip-content',[box(false,18,'',[ubuntu,box(true,6,'',[site,description])]),label('documentation.ubuntu.com','caption')]));
        }else{const preview=text(item.preview,item.id==='command'||item.kind==='files'?'mono':'',item.kind==='files'?3:4);preview.valign=Gtk.Align.START;preview.vexpand=true;const inset=box(true,0,'clip-content',[preview]);inset.vexpand=true;content.append(inset);}
        const sourceIcon=(item.sourceApp||'').toLowerCase().includes('firefox')?icon('firefox',20):null;
        content.append(box(false,8,'',[sourceIcon,label(`${item.sourceApp||'출처 미확인'} · ${formatTime(item.createdAt)}`,'caption')]));
        const card=new Gtk.Button({child:content,css_classes:['clip-card'],width_request:280,height_request:index<3?246:index<6?184:218,hexpand:true});
        card.tooltip_text=item.sourceAttribution==='foreground-observed'?`복사 시 전경에 있던 앱: ${item.sourceApp||'미확인'}`:item.sourceApp||'출처 미확인';card.connect('clicked',()=>this.select(item));
        const double=new Gtk.GestureClick();double.connect('released',(_g,n)=>{if(n===2)this.copy(item);});card.add_controller(double);
        const right=new Gtk.GestureClick({button:3});right.connect('pressed',()=>this.contextMenu(card,item));card.add_controller(right);
        return card;
    }
    select(item){this.selected=item;this.state.selectedId=item.id;for(const [id,widget]of this.cards??[])widget[id===item.id?'add_css_class':'remove_css_class']('selected');clear(this.bar);this.bar.visible=true;
        const description=box(true,6,'',[label(item.title,'strong'),label(item.preview.replace(/\n/g,' · '),'caption',{ellipsize:Pango.EllipsizeMode.END,max_width_chars:62})]);description.hexpand=true;
        this.bar.append(icon(typeIcons[item.kind]||typeIcons.text,28));this.bar.append(description);
        this.bar.append(button('복사','edit-copy-symbolic',()=>this.copy(item),'primary'));
        this.bar.append(button('미리보기','view-reveal-symbolic',()=>this.preview(item)));
        this.bar.append(iconButton('view-pin-symbolic',item.pinned?'고정 해제':'고정',()=>this.ctx.command('clipboard','pin',{id:item.id,pinned:!item.pinned})));
        const more=iconButton('view-more-symbolic','더 보기',()=>this.contextMenu(more,item));this.bar.append(more);
    }
    async copy(item=this.selected,paste=false){if(!item)return;try{await this.ctx.client.putClipboard(item.id,paste);this.ctx.toast(this.ctx.fixture?'검수 모드에서 복사 동작을 확인했습니다.':'클립보드에 복사했습니다.');}catch(error){this.ctx.toast(error.message);}}
    removeSelected(){if(!this.selected)return;const item=this.selected;const content=text('이 기록을 보관함에서 삭제할까요?');let dialog;dialog=modal(this.ctx.window,'기록 삭제',content,[button('취소',null,()=>dialog.close()),button('삭제',null,async()=>{const result=await this.ctx.command('clipboard','delete',{id:item.id});if(result!==null)dialog.close();},'danger')],460);}
    contextMenu(anchor,item){const pop=new Gtk.Popover();pop.set_parent(anchor);const items=box(true,4);
        for(const [title,ic,callback]of [['복사','edit-copy-symbolic',()=>this.copy(item)],['붙여넣기','edit-paste-symbolic',()=>this.copy(item,true)],['미리보기','view-reveal-symbolic',()=>this.preview(item)],['모음 지정','folder-symbolic',()=>this.assignCollections(item)],[item.pinned?'고정 해제':'고정','view-pin-symbolic',()=>this.ctx.command('clipboard','pin',{id:item.id,pinned:!item.pinned})],['삭제','user-trash-symbolic',()=>{this.selected=item;this.removeSelected();}]])items.append(button(title,ic,()=>{pop.popdown();callback();},'flat'));
        pop.set_child(items);pop.connect('closed',()=>pop.unparent());pop.popup();}
    async preview(item){
        if(!item)return;
        try{
            const detail=await this.ctx.client.execute('clipboard','detail',{id:item.id});
            const wide=(this.ctx.effectiveWidth??this.ctx.width)>=1100;const previewArea=box(true,16);previewArea.hexpand=true;
            previewArea.append(label(item.title,'caption'));
            let preview;
            if(item.kind==='image'){preview=picture(detail?.imageUri||item.thumbnailUri,wide?376:240);preview.content_fit=Gtk.ContentFit.COVER;}
            else{preview=scroller(text(detail?.text||item.preview,'mono'));preview.min_content_height=300;}
            if(wide)previewArea.width_request=638;previewArea.append(preview);if(detail?.textTruncated)previewArea.append(box(false,12,'',[text('큰 텍스트의 일부를 표시합니다.','caption'),button('원문 열기','document-open-symbolic',()=>{if(!this.ctx.fixture&&detail.textPath)openUri(GLib.filename_to_uri(detail.textPath,null));})]));
            const metadata=box(true,8,'detail-metadata');metadata.width_request=288;metadata.hexpand=false;
            const attachment=item.attachments?.[0];
            metadata.append(box(false,14,'',[icon(typeIcons[item.kind]||typeIcons.text,24),label(item.kind==='image'?'PNG 이미지':formats.find(f=>f[0]===item.kind)?.[1]||'기록')]));
            if(detail?.width&&detail?.height)metadata.append(label(`${detail.width} × ${detail.height}`,'caption'));
            if(attachment)metadata.append(label(`${(attachment.length/1048576).toFixed(1)} MiB`,'caption'));
            metadata.append(label(`${item.sourceApp||'출처 미확인'} · 오늘 ${formatTime(item.createdAt)}`,'caption'));
            metadata.append(separator());metadata.append(label('모음','caption'));
            const selected=new Set(item.categoryIds||[]);
            for(const collection of this.ctx.snapshot.clipboard.collections){
                const check=new Gtk.CheckButton({label:collection.name,active:selected.has(collection.id)});
                check.connect('toggled',async()=>{if(check._restoring)return;const previous=new Set(selected);check.active?selected.add(collection.id):selected.delete(collection.id);const result=await this.ctx.command('clipboard','set-collections',{id:item.id,categoryIds:[...selected]});if(result===null){selected.clear();previous.forEach(id=>selected.add(id));check._restoring=true;check.set_active(previous.has(collection.id));check._restoring=false;}});metadata.append(check);
            }
            if(item.kind==='image'){
                metadata.append(separator());metadata.append(label('이미지 태그','caption'));metadata.append(text((item.tags||[]).join(' · ')||'태그가 없습니다.','',2));
                const tagging=detail?.entry?.tagState||item.tagState;
                metadata.append(label(tagging==='pending'?'태깅 대기 중':tagging==='processing'?'이미지 분석 중':tagging==='failed'?'태깅 실패':'자동 태깅','caption'));
                if(tagging==='failed')metadata.append(button('태깅 다시 시도','view-refresh-symbolic',()=>this.ctx.command('clipboard','tag',{id:item.id})));
                metadata.append(separator());metadata.append(label('이미지 설명','caption'));metadata.append(text(detail?.entry?.description||detail?.description||item.description||item.preview,'',4));
            }
            for(let child=metadata.get_first_child();child;child=child.get_next_sibling()){if(child instanceof Gtk.Label){child.max_width_chars=24;child.wrap=true;}}const columns=box(!wide,24,'',[previewArea,metadata]);columns.vexpand=true;
            let dialog;
            const pin=toggle(item.pinned,v=>this.ctx.command('clipboard','pin',{id:item.id,pinned:v}));
            const footer=box(false,12,'detail-footer',[icon('view-pin-symbolic',20),pin,label('고정','caption'),spacer(),button('붙여넣기','edit-paste-symbolic',()=>this.copy(item,true)),button('복사','edit-copy-symbolic',()=>this.copy(item),'primary')]);
            dialog=modal(this.ctx.window,'미리보기',columns,[footer],Math.min(1008,this.ctx.width-80),610);dialog.add_css_class('detail-dialog');
        }catch(error){this.ctx.toast(error.message);}
    }
    filters(anchor){
        const pop=new Gtk.Popover({has_arrow:false,halign:Gtk.Align.END});pop.set_parent(anchor);pop.add_css_class('filter-popover');
        const body=box(true,12);body.width_request=368;
        const sample=this.ctx.fixture&&this.scene==='05-filter';
        const current=sample?{formats:['text','url'],apps:['Firefox','텍스트 편집기'],from:'2026-10-01',through:'2026-10-03'}:this.state.filter;
        const chosenFormats=new Set(current.formats||[]),chosenApps=new Set(current.apps||[]);const checks=[];
        const from=entry(current.from||'','YYYY-MM-DD');const through=entry(current.through||'','YYYY-MM-DD');from.width_chars=10;through.width_chars=10;from.max_width_chars=10;through.max_width_chars=10;
        from.secondary_icon_name='x-office-calendar-symbolic';through.secondary_icon_name='x-office-calendar-symbolic';
        body.append(box(false,12,'',[label('필터','section-title'),spacer(),button('초기화','view-refresh-symbolic',()=>{checks.forEach(c=>c.active=false);from.text='';through.text='';},'flat')]));body.append(separator());
        body.append(label('복사한 앱','strong'));const appList=box(true,8);
        const apps=[...new Set(this.ctx.snapshot.clipboard.entries.map(e=>e.sourceApp||'출처 미확인'))].sort((a,b)=>a==='Firefox'?-1:b==='Firefox'?1:0);
        for(const app of apps){const check=new Gtk.CheckButton({label:app,active:chosenApps.has(app)});check.connect('toggled',()=>check.active?chosenApps.add(app):chosenApps.delete(app));checks.push(check);appList.append(check);}
        const appsScroll=scroller(appList);appsScroll.vexpand=false;appsScroll.max_content_height=180;appsScroll.propagate_natural_height=true;body.append(appsScroll);
        body.append(separator());body.append(label('콘텐츠 종류','strong'));
        const types=new Gtk.Grid({column_homogeneous:true,column_spacing:24,row_spacing:8});
        formats.forEach(([id,title],index)=>{const check=new Gtk.CheckButton({label:title,active:chosenFormats.has(id)});check.connect('toggled',()=>check.active?chosenFormats.add(id):chosenFormats.delete(id));checks.push(check);types.attach(check,index%2,Math.floor(index/2),1,1);});body.append(types);
        body.append(separator());body.append(label('복사한 날짜','strong'));body.append(box(false,8,'filter-dates',[from,label('부터','caption'),through]));body.append(label('시작일과 종료일을 포함합니다.','small'));
        const cancel=button('취소',null,()=>pop.popdown());cancel.hexpand=true;
        const apply=button('적용',null,()=>{for(const value of[from.text,through.text])if(value&&(!/^\d{4}-\d{2}-\d{2}$/.test(value)||Number.isNaN(Date.parse(value)))){this.ctx.toast('날짜는 YYYY-MM-DD로 입력해 주세요.');return;}if(from.text&&through.text&&from.text>through.text){this.ctx.toast('종료일은 시작일 이후여야 합니다.');return;}this.state.filter={formats:[...chosenFormats],apps:[...chosenApps],from:from.text||undefined,through:through.text||undefined};this.updateFilterBadge();pop.popdown();this.refresh();},'primary');apply.hexpand=true;body.append(box(false,16,'',[cancel,apply]));
        pop.set_child(body);pop.connect('closed',()=>pop.unparent());pop.popup();
    }
    collections(){
        const content=box(true,16,'collections-content');content.append(label('기록을 주제별로 묶어 보관합니다.','muted'));const list=box(true,8);content.append(list);
        let selectedId=this.ctx.snapshot.clipboard.collections[0]?.id||null;const name=entry(this.ctx.snapshot.clipboard.collections[0]?.name||'');let dialog;
        const refresh=()=>{clear(list);for(const category of this.ctx.snapshot.clipboard.collections){const row=box(false,16,'collection-row group',[icon('folder-symbolic'),label(category.name,'strong',{hexpand:true}),iconButton('document-edit-symbolic','이름 변경',()=>{selectedId=category.id;name.text=category.name;refresh();name.grab_focus();},'flat'),iconButton('user-trash-symbolic','모음 삭제',async()=>{const result=await this.ctx.command('clipboard','delete-collection',{id:category.id});if(result!==null){if(selectedId===category.id){selectedId=null;name.text='';}refresh();}},'flat')]);if(category.id===selectedId)row.add_css_class('selected');list.append(row);}};
        refresh();const add=button('새 모음','list-add-symbolic',()=>{selectedId=null;name.text='';refresh();name.grab_focus();});add.halign=Gtk.Align.START;content.append(add);content.append(separator());
        const save=button('이름 변경',null,async()=>{try{await this.ctx.client.execute('clipboard','save-collection',{id:selectedId||undefined,name:name.text});await this.ctx.client.refresh();refresh();}catch(e){this.ctx.toast(e.message);}});
        content.append(box(true,8,'',[label('모음 이름','caption'),box(false,16,'',[name,save])]));content.append(separator());content.append(box(false,16,'',[icon('dialog-information-symbolic',20),text('모음을 삭제해도 기록은 유지됩니다.','caption')]));
        const done=button('완료',null,()=>{dialog.close();this.ctx.render();},'primary');done.width_request=106;
        dialog=modal(this.ctx.window,'모음 관리',content,[done],588);dialog.add_css_class('collections-dialog');
    }
    assignCollections(item){const content=box(true,12);const selected=new Set(item.categoryIds);this.ctx.snapshot.clipboard.collections.forEach(c=>{const check=new Gtk.CheckButton({label:c.name,active:selected.has(c.id)});check.connect('toggled',()=>check.active?selected.add(c.id):selected.delete(c.id));content.append(check);});if(!this.ctx.snapshot.clipboard.collections.length)content.append(text('먼저 모음을 만들어 주세요.','caption'));let dialog;dialog=modal(this.ctx.window,'모음 지정',content,[button('완료',null,async()=>{const result=await this.ctx.command('clipboard','set-collections',{id:item.id,categoryIds:[...selected]});if(result!==null)dialog.close();},'primary')],440);}
    dispose(){if(this.aiRequest)this.ctx.client.execute('clipboard','cancel-search',{requestId:this.aiRequest}).catch(()=>{});this.request=(this.request??0)+1;}
}

export function clipboardSettings(ctx){
    const settings=ctx.viewState.clipboardSettings??=JSON.parse(JSON.stringify(ctx.snapshot.clipboard.settings));const body=box(true,8,'clipboard-settings-content');
    const hotkey=box(false,12,'',[keycaps(shortcutKeys(ctx.snapshot.settings.clipboardShortcut)),button('변경',null,()=>ctx.navigate('settings'))]);
    let excludedApps=[...(settings.excludedApps||[])];
    const exclusionButton=button('앱 선택',null,()=>{const input=entry(excludedApps.join(', '),'앱 desktop ID를 쉼표로 구분');let dialog;dialog=modal(ctx.window,'기록 제외 앱',box(true,16,'',[text('앱 desktop ID를 쉼표로 구분해 입력하세요. 제외 앱이 전경에 있으면 기록하지 않습니다.','caption'),input]),[button('완료',null,()=>{excludedApps=input.text.split(',').map(s=>s.trim()).filter(Boolean);settings.excludedApps=excludedApps;dialog.close();},'primary')],640);});
    body.append(heading('기록과 단축키'));body.append(group([preference('클립보드 기록','복사한 텍스트, 이미지, 파일을 보관합니다.',toggle(!ctx.snapshot.clipboard.paused,v=>ctx.command('clipboard','pause',{paused:!v})),'text-x-generic-symbolic'),preference('빠른 패널 단축키','클립보드 빠른 패널을 여는 단축키를 설정합니다.',hotkey,'input-keyboard-symbolic'),preference('기록 제외 앱','선택한 앱의 내용은 보관하지 않습니다.',exclusionButton,'action-unavailable-symbolic')]));
    const path=entry(settings.archivePath||'');path.editable=false;path.width_request=330;const choose=button('찾아보기','folder-open-symbolic',()=>fileChooser(ctx.window,'보관함 저장 폴더',value=>{path.text=value;},true));
    const item=number((settings.itemLimitBytes||104857600)/1048576,1,512);const total=number((settings.totalLimitBytes||10737418240)/1073741824,1,1024);item.width_chars=3;total.width_chars=3;
    path.connect('notify::text',()=>settings.archivePath=path.text);item.connect('value-changed',()=>settings.itemLimitBytes=Math.round(item.value*1048576));total.connect('value-changed',()=>settings.totalLimitBytes=Math.round(total.value*1073741824));
    body.append(heading('저장 공간'));const storage=group([preference('저장 폴더','클립보드 기록이 저장되는 위치입니다.',box(false,12,'',[path,choose]),'folder-symbolic'),preference('항목당 최대 크기','각 항목의 최대 저장 크기를 제한합니다.',box(false,12,'',[item,label('MiB','caption')]),'drive-harddisk-symbolic'),preference('전체 보관 용량','전체 기록의 저장 용량을 제한합니다.',box(false,12,'',[total,label('GiB','caption')]),'drive-harddisk-symbolic'),box(false,16,'settings-note',[icon('dialog-information-symbolic',20),text('저장 폴더를 옮기면 기록을 복사하고 확인합니다. 기존 사본은 유지됩니다.','caption')])]);storage.add_css_class('storage-group');body.append(storage);
    const ai=toggle(settings.aiEnabled,v=>settings.aiEnabled=v);const tags=toggle(settings.taggingEnabled,v=>settings.taggingEnabled=v);body.append(heading('AI 기능'));body.append(group([preference('AI 검색','클립보드 내용을 AI로 검색하고 요약합니다.',box(false,16,'',[label('Antigravity'),ai]),'starred-symbolic'),preference('이미지 자동 태깅','이미지 내용을 분석해 Antigravity로 태그와 설명을 만듭니다.',tags,'image-x-generic-symbolic'),box(false,16,'settings-note',[icon('dialog-information-symbolic',20),text('모델 연결은 AI 서비스에서 관리합니다.','caption'),spacer(),button('AI 서비스 열기','go-next-symbolic',()=>ctx.navigate('ai'),'flat')])]));
    const save=button('저장',null,async()=>{try{await ctx.client.execute('clipboard','settings',{archivePath:path.text,itemLimitBytes:Math.round(item.value*1048576),totalLimitBytes:Math.round(total.value*1073741824),excludedApps,aiEnabled:ai.active,taggingEnabled:tags.active});delete ctx.viewState.clipboardSettings;ctx.toast('클립보드 설정을 저장했습니다.');ctx.navigate('clipboard');}catch(e){ctx.toast(e.message);}},'primary');
    ctx.headerActions=[save];
    const clamp=new Adw.Clamp({maximum_size:1152,tightening_threshold:1152,child:body});return{widget:scroller(clamp)};
}
