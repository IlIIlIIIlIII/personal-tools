import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import {box,label,text,icon,button,iconButton,entry,toggle,spacer,group,heading,preference,scroller,segmented,separator,modal,fileChooser,table,formatTime} from '../ui.js';

export function displayPage(ctx){
    const d=ctx.snapshot.display;const current=ctx.viewState.displayTab||'hdr';const body=box(true,20,'page-content display-content');
    ctx.headerActions=[button('새로 고침','view-refresh-symbolic',()=>ctx.command('display','refresh',{}))];
    body.append(segmented([{id:'hdr',title:'HDR'},{id:'power',title:'화면 꺼짐 방지'}],current,id=>{ctx.viewState.displayTab=id;ctx.render();}));
    const power=d.monitorPower??{};
    const powerToggle=toggle(Boolean(power.enabled),enabled=>ctx.command('display','monitor-power',{enabled}));
    powerToggle.sensitive=Boolean(power.available);
    const powerGroup=group();
    powerGroup.append(preference('MO32U24 자동 화면 끄기','두 대만 · 마우스·키보드 입력 시 켜기 · 입력 없으면 1분 후 끄기',powerToggle,'display-brightness-symbolic'));
    powerGroup.append(preference('앱의 화면 유지 요청과 무관하게 동작','화면 배치는 유지합니다. 이 기능을 끄면 두 화면을 켭니다.',label(power.enabled?'사용 중':'꺼짐','caption')));
    body.append(powerGroup);
    if(power.error)body.append(text(power.error,'failure-banner'));
    if(current==='hdr'&&d.error)body.append(text(typeof d.error==='string'?d.error:d.error.message,'failure-banner'));
    if(current==='hdr'){
        body.append(heading('연결된 모니터','모니터별 HDR 상태를 조정합니다.'));
        const monitors=group();for(const monitor of d.monitors||[]){const info=box(true,8,'',[label(`${monitor.name}${monitor.primary?' · 주 모니터':''}`,'strong'),label(monitor.connector,'caption')]);info.valign=Gtk.Align.CENTER;info.width_request=(ctx.effectiveWidth??ctx.width)>=1400?320:180;
            const state=toggle(monitor.hdrEnabled,v=>ctx.command('display','hdr',{id:monitor.id,enabled:v}));state.sensitive=monitor.hdrSupported;
            const row=box(false,(ctx.effectiveWidth??ctx.width)>=1400?28:16,'preference-row',[icon(monitor.connector?.startsWith('eDP')?'computer-symbolic':'video-display-symbolic',38),info,label(`${monitor.width} × ${monitor.height} · ${Number(monitor.refreshRate).toFixed(0)} Hz`,'caption'),spacer(),label(monitor.hdrSupported?'HDR 지원':'HDR 미지원','caption'),label('HDR'),state]);row.height_request=88;monitors.append(row);}
        if(!(d.monitors||[]).length)monitors.append(preference('모니터 정보를 확인할 수 없습니다.','GNOME 세션 연결과 진단 화면을 확인해 주세요.',button('진단 열기',null,()=>ctx.navigate('diagnostics'))));body.append(monitors);
        body.append(separator());body.append(heading('자동 HDR','등록한 프로그램을 실행하면 주 모니터 HDR을 켜고,\n프로그램이 종료되면 이전 상태로 되돌립니다.',toggle(d.autoHdr,v=>ctx.command('display','auto-hdr',{enabled:v}))));
        const rules=group();if(!d.rules?.length){const empty=box(true,16,'group-pad',[icon('view-app-grid-symbolic',40),label('자동 HDR 프로그램을 추가하세요.', '',{xalign:0.5}),button('프로그램 추가','list-add-symbolic',()=>ruleDialog(ctx),'primary')]);empty.halign=Gtk.Align.CENTER;empty.margin_top=12;empty.margin_bottom=12;rules.append(empty);}else{for(const rule of d.rules)rules.append(preference(rule.name,rule.executable,box(false,10,'',[iconButton('document-edit-symbolic','규칙 수정',()=>ruleDialog(ctx,rule),'flat'),iconButton('user-trash-symbolic','규칙 삭제',()=>ctx.command('display','delete-rule',{id:rule.id}),'flat')]),'application-x-executable-symbolic'));rules.append(preference('프로그램 추가','',button('추가','list-add-symbolic',()=>ruleDialog(ctx))));}body.append(rules);body.append(box(false,12,'',[icon('dialog-information-symbolic',20),text('사용자가 직접 바꾼 HDR 설정은 유지합니다.','caption')]));
    }else{
        const items=d.inhibitors||[];const sleep=d.sleepInhibitors||[];
        body.append(heading('화면을 켜 두는 앱','현재 화면 꺼짐을 막고 있는 앱과 이유를 확인합니다.',label(`요청 ${items.length}개`,'caption')));
        sourceState(body,d.inhibitorStatus,d.inhibitorError,d.inhibitorsObservedAt,items,'화면을 켜 두는');
        const requests=table(['앱','요청 이유','출처'],items.map(item=>[box(false,16,'',[icon(item.appName==='Firefox'?'firefox':'application-x-executable-symbolic',34),box(true,6,'',[label(item.appName,'strong'),label(item.appId||'','caption')])]),item.reason,item.source]),(ctx.effectiveWidth??ctx.width)>=1300?[440,360]:[280,240]);
        let requestRow=requests.get_first_child()?.get_next_sibling();
        const selected=items.find(i=>i.id===ctx.viewState.inhibitorId)||items[0];
        for(const item of items){
            const row=requestRow;requestRow=row.get_next_sibling();row.focusable=true;
            if(item===selected)row.add_css_class('selected');
            const select=()=>{ctx.viewState.inhibitorId=item.id;ctx.render();};
            const click=new Gtk.GestureClick();click.connect('released',select);row.add_controller(click);
            const keys=new Gtk.EventControllerKey();keys.connect('key-pressed',(_c,key)=>{if(key===Gdk.KEY_Return||key===Gdk.KEY_space){select();return true;}return false;});row.add_controller(keys);
        }
        body.append(requests);
        if(items.length){const item=items.find(i=>i.id===ctx.viewState.inhibitorId)||items[0];const details=box(true,0,'group group-pad inhibitor-details');details.append(heading('요청 상세'));
            for(const [title,value]of[['앱 ID',item.appId||'미확인'],['요청 범위',item.scope],['요청 이유',item.reason],['확인 시각',formatTime(d.inhibitorsObservedAt||d.observedAt)]]){const name=label(title,'caption');name.width_request=(ctx.effectiveWidth??ctx.width)>=1300?280:180;details.append(box(false,24,'inhibitor-detail-row',[name,text(value||'미확인')]));}body.append(details);}
        body.append(heading('시스템 절전 요청','컴퓨터의 절전 요청은 화면 꺼짐 요청과 별도로 표시됩니다.',label(`요청 ${sleep.length}개`,'caption')));
        sourceState(body,d.sleepInhibitorStatus,d.sleepInhibitorError,d.sleepInhibitorsObservedAt,sleep,'시스템 절전을 막는');
        body.append(table(['앱','요청 이유','출처'],sleep.map(item=>[item.appName,item.reason,item.source]),(ctx.effectiveWidth??ctx.width)>=1300?[440,360]:[280,240]));
        body.append(label(`최근 확인 · ${d.inhibitorsObservedAt?formatTime(d.inhibitorsObservedAt):'확인 전'}`,'caption'));

    }
    return{widget:scroller(body)};
}
function sourceState(body,status,error,observed,items,description){
    const message=typeof error==='string'?error:error?.message;
    const stale=status==='stale'||(observed&&Date.now()-Date.parse(observed)>60000&&status!=='ready');
    if(status==='error'||status==='unavailable'||stale){body.append(text(`${stale||items.length?'이전 조회 결과입니다. ':''}${message||'요청을 조회할 수 없습니다.'}${observed?` · 마지막 확인 ${formatTime(observed)}`:''}`,'failure-banner'));}
    else if(status==='ready'&&!items.length)body.append(label(`현재 ${description} 요청이 없습니다.`,'caption'));
    else if(!status||status==='loading'||status==='unknown')body.append(label('요청 정보를 확인하고 있습니다.','caption'));
}
function ruleDialog(ctx,rule={}){const name=entry(rule.name||'','프로그램 이름');const path=entry(rule.executable||'','실행 파일 경로');const desktopId=entry(rule.desktopId||'','선택 사항 · desktop ID');let dialog;const content=box(true,16,'',[label('프로그램 이름','caption'),name,label('실행 파일','caption'),box(false,12,'',[path,button('찾아보기','folder-open-symbolic',()=>fileChooser(ctx.window,'자동 HDR 프로그램 선택',value=>path.text=value))]),label('desktop ID','caption'),desktopId]);dialog=modal(ctx.window,rule.id?'자동 HDR 프로그램 수정':'자동 HDR 프로그램 추가',content,[button('취소',null,()=>dialog.close()),button('저장',null,async()=>{try{await ctx.client.execute('display','save-rule',{id:rule.id,name:name.text,executable:path.text,desktopId:desktopId.text,enabled:true});dialog.close();ctx.render();}catch(e){ctx.toast(e.message);}},'primary')],680);}
