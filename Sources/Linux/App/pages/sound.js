import Gtk from 'gi://Gtk?version=4.0';
import GLib from 'gi://GLib';
import {box,label,text,icon,button,iconButton,toggle,number,dropdown,spacer,group,heading,preference,scroller,separator,fileChooser,modal,status,clear} from '../ui.js';

export function responseDb(filters,frequency,rate=48000){
    let result=0;const w=2*Math.PI*frequency/rate;const c1=Math.cos(w),s1=Math.sin(w),c2=Math.cos(2*w),s2=Math.sin(2*w);
    for(const f of filters){if(f.enabled===false)continue;const gain=Number(f.gain),q=Number(f.q),hz=Number(f.frequency);if(!Number.isFinite(gain)||!(q>0)||!(hz>0))continue;
        const a=10**(gain/40),w0=2*Math.PI*hz/rate,alpha=Math.sin(w0)/(2*q),cos=Math.cos(w0);
        const b0=1+alpha*a,b1=-2*cos,b2=1-alpha*a,a0=1+alpha/a,a1=-2*cos,a2=1-alpha/a;
        const nr=b0+b1*c1+b2*c2,ni=-b1*s1-b2*s2,dr=a0+a1*c1+a2*c2,di=-a1*s1-a2*s2;
        result+=10*Math.log10(Math.max(1e-20,(nr*nr+ni*ni)/(dr*dr+di*di)));
    }return result;
}
function eqPlot(filters,light=false){
    const area=new Gtk.DrawingArea({height_request:140,hexpand:true});
    area.set_draw_func((_area,cr,w,h)=>{
        const left=38,top=10,right=w-10,bottom=h-28;const px=f=>left+Math.log10(f/20)/3*(right-left),py=db=>top+(12-db)/48*(bottom-top);
        cr.setSourceRGB(...(light?[0.96,0.96,0.95]:[0.15,0.15,0.15]));cr.paint();cr.setLineWidth(1);cr.selectFontFace('Ubuntu Sans',0,0);cr.setFontSize(11);
        for(const db of[12,0,-12,-24,-36]){cr.setSourceRGB(...(light?[0.80,0.80,0.78]:[0.27,0.27,0.26]));cr.moveTo(left,py(db));cr.lineTo(right,py(db));cr.stroke();cr.setSourceRGB(...(light?[0.35,0.37,0.31]:[0.7,0.71,0.67]));cr.moveTo(3,py(db)+4);cr.showText(db>0?`+${db}`:`${db}`);}
        for(const f of[20,50,100,200,500,1000,2000,5000,10000,20000]){cr.setSourceRGB(...(light?[0.83,0.83,0.81]:[0.25,0.25,0.24]));cr.moveTo(px(f),top);cr.lineTo(px(f),bottom);cr.stroke();cr.setSourceRGB(...(light?[0.35,0.37,0.31]:[0.7,0.71,0.67]));cr.moveTo(px(f)-8,h-7);cr.showText(f>=1000?`${f/1000}k`:`${f}`);}
        cr.setSourceRGB(0.61,0.66,0.31);cr.setLineWidth(2);for(let x=0;x<=right-left;x++){const f=20*10**(x/(right-left)*3),y=py(Math.max(-36,Math.min(12,responseDb(filters,f))));if(x===0)cr.moveTo(left+x,y);else cr.lineTo(left+x,y);}cr.stroke();
        for(const f of filters){if(f.enabled===false)continue;cr.arc(px(f.frequency),py(responseDb(filters,f.frequency)),5,0,Math.PI*2);cr.fill();}
    });return area;
}
function channel(ctx,side){
    const state=ctx.snapshot.audio;const filters=(state[`${side}Filters`]||[]).map(f=>({...f}));const panel=box(true,6,'group sound-channel');panel.hexpand=true;
    const name=side==='left'?'왼쪽 채널':'오른쪽 채널';
    const path=state[`${side}File`];const filename=label(path?GLib.path_get_basename(path):'미등록','caption');filename.tooltip_text=path||'등록된 EQ 파일이 없습니다.';
    panel.append(box(false,12,'',[label(name,'strong'),spacer(),filename,button('파일 가져오기','folder-open-symbolic',()=>fileChooser(ctx.window,`${name} REW 파일`,path=>ctx.command('audio','import-eq',{channel:side,path})))]));
    const headers=new Gtk.Grid({column_spacing:12,column_homogeneous:true});['주파수','게인','Q'].forEach((s,i)=>headers.attach(label(s,'caption'),i,0,1,1));panel.append(headers);
    filters.forEach((filter,index)=>{
        const patch=(key,value)=>{filters[index][key]=value;const next=filters.map(f=>({...f}));ctx.command('audio','configure',{[`${side}Filters`]:next});};
        const row=box(false,10);const frequency=number(filter.frequency,20,20000,1,0,v=>patch('frequency',v));const gain=number(filter.gain,-24,12,0.1,1,v=>patch('gain',v));const q=number(filter.q,0.1,30,0.01,2,v=>patch('q',v));
        for(const control of[frequency,gain,q]){control.hexpand=true;control.width_chars=5;row.append(control);}row.append(iconButton('user-trash-symbolic','필터 삭제',()=>ctx.command('audio','configure',{[`${side}Filters`]:filters.filter((_f,i)=>i!==index)}),'flat'));panel.append(row);
    });
    if(!filters.length)panel.append(text('REW 파일을 가져오거나 필터를 추가하세요.','caption'));
    const add=button('필터 추가','list-add-symbolic',()=>ctx.command('audio','configure',{[`${side}Filters`]:[...filters,{frequency:1000,gain:0,q:1,enabled:true}]}),'flat accent');add.halign=Gtk.Align.START;panel.append(add);
    panel.append(eqPlot(filters,ctx.light));return panel;
}
// Runtime telemetry changes frequently; only configuration changes need new controls.
function configurationKey(audio){
    return JSON.stringify([audio.outputs,audio.inputs,audio.outputId,audio.inputId,
        audio.eqEnabled,audio.aecEnabled,audio.leftFilters,audio.rightFilters,
        audio.leftFile,audio.rightFile,audio.muted,audio.routing]);
}
export function soundPage(ctx){
    const a=ctx.snapshot.audio;const config=configurationKey(a);const body=box(true,12,'sound-content');
    const viewState=ctx.viewState??= {};const saved=viewState.sound??= {scroll:0};
    const runtimeStatus=box();ctx.headerStatus=runtimeStatus;
    const messages=box(true,12);body.append(messages);
    const speakerTitle=box(false,18,'',[icon('audio-volume-high-symbolic',32),label('스피커','section-title'),text('출력 장치와 좌우 채널 보정 설정을 관리합니다.','caption')]);speakerTitle.valign=Gtk.Align.END;
    const detail=button('상세 진단','utilities-system-monitor-symbolic',()=>audioDiagnostics(ctx));detail.valign=Gtk.Align.START;
    const top=box(false,16,'',[speakerTitle,spacer(),detail]);top.height_request=76;body.append(top);
    const outputs=dropdown(['출력 장치 선택',...(a.outputs||[]).map(d=>d.name)],(a.outputs||[]).findIndex(d=>d.id===a.outputId)+1,index=>{if(index>0)ctx.command('audio','configure',{outputId:a.outputs[index-1].id});});
    const speaker=box(true,14,'group sound-speaker');speaker.append(box(false,24,'sound-device-row',[label('출력 장치','strong'),outputs,spacer(),label('좌우 EQ','strong'),toggle(a.eqEnabled,v=>ctx.command('audio','configure',{eqEnabled:v}))]));
    const channels=box((ctx.effectiveWidth??ctx.width)<1250,16,'',[channel(ctx,'left'),channel(ctx,'right')]);channels.homogeneous=true;speaker.append(channels);body.append(speaker);
    const micTitle=box(false,18,'',[icon('audio-input-microphone-symbolic',32),label('마이크','section-title'),text('입력 장치와 에코 제거 설정을 관리합니다.','caption')]);micTitle.margin_top=16;body.append(micTitle);
    const mic=box(true,22,'group group-pad');const inputs=dropdown(['입력 장치 선택',...(a.inputs||[]).map(d=>d.name)],(a.inputs||[]).findIndex(d=>d.id===a.inputId)+1,i=>{if(i>0)ctx.command('audio','configure',{inputId:a.inputs[i-1].id});});
    mic.append(box(false,24,'',[label('입력 장치','strong'),inputs,spacer(),label('에코 제거','strong'),toggle(a.aecEnabled,v=>ctx.command('audio','configure',{aecEnabled:v,aecBackend:'npu'}))]));mic.append(separator());
    const latency=label('','caption'),npuState=label('','caption'),npuDevice=label('','strong');
    const left=box(true,16);left.hexpand=true;left.append(box(false,24,'',[label('처리 장치'),spacer(),npuDevice]));const micRoute=a.routing?.microphone;const route=button(micRoute?.state==='active'?'이전 마이크로 복원':'기본 마이크로 사용',null,()=>ctx.command('audio','route-microphone',{enabled:micRoute?.state!=='active'}),'flat');left.append(box(false,12,'',[label('PersonalTools Mic'),spacer(),route]));if(micRoute?.error)left.append(text(micRoute.error,'error'));if(a.routing?.output?.state==='manual-override')speaker.append(text('기본 출력이 사용자가 선택한 장치로 유지되고 있습니다. EQ를 다시 켜면 Personal Tools 출력으로 전환합니다.','caption'));left.append(box(false,20,'',[label('처리 지연','caption'),latency,spacer(),npuState]));
    const right=box(true,16);right.hexpand=true;right.append(label('입력 레벨','caption'));const level=new Gtk.LevelBar({min_value:0,max_value:40,value:a.inputLevelDb==null?0:Math.max(0,Math.min(40,(a.inputLevelDb+60)/60*40)),mode:Gtk.LevelBarMode.DISCRETE,css_classes:['meter']});right.append(level);right.append(button(a.muted?'마이크 음소거 해제':'마이크 음소거',a.muted?'microphone-sensitivity-muted-symbolic':'audio-input-microphone-symbolic',()=>ctx.command('audio','mute',{})));right.append(text('스피커 소리가 마이크로 되돌아오는 에코를 줄입니다.','caption'));
    mic.append(box(false,32,'',[left,right]));body.append(mic);
    let messageKey=null;
    const updateLayout=()=>{channels.orientation=(ctx.effectiveWidth??ctx.width)<1250?Gtk.Orientation.VERTICAL:Gtk.Orientation.HORIZONTAL;};
    const updateSnapshot=()=>{
        const current=ctx.snapshot.audio;
        const connected=['running','ready','idle'].includes(current.status);
        clear(runtimeStatus);runtimeStatus.append(status(current.error?'error':current.status,current.error||current.status==='error'?'엔진 오류':connected?'엔진 연결됨':'엔진 연결 대기'));
        const standby=current.status==='idle'&&current.npu?.status==='ready'&&current.captureConnected&&current.referenceConnected&&!current.npu?.error;
        const nextMessageKey=JSON.stringify([current.error,standby,current.degraded,current.degradedReason]);
        if(nextMessageKey!==messageKey){
            messageKey=nextMessageKey;clear(messages);
            if(current.error)messages.append(text(typeof current.error==='string'?current.error:current.error.message,'failure-banner'));
            if(current.degraded&&!standby)messages.append(text(current.degradedReason||'오디오 처리 지연이 발생했습니다. 상세 진단을 확인하세요.','failure-banner'));
            if(standby)messages.append(text('앱이 마이크를 사용하면 NPU 처리를 시작합니다.','caption'));
            messages.visible=Boolean(messages.get_first_child());
        }
        latency.label=current.latencyMs==null?'측정 대기':`${Number(current.latencyMs).toFixed(1)} ms`;
        npuState.label=['running','ready'].includes(current.npu?.status)?'NPU 실행 확인됨':'NPU 연결 확인 필요';
        npuDevice.label=current.npu?.device||'Intel NPU';
        level.value=current.inputLevelDb==null?0:Math.max(0,Math.min(40,(current.inputLevelDb+60)/60*40));
        return configurationKey(current)===config;
    };
    updateSnapshot();
    const widget=scroller(body);
    // Restore after GTK has allocated the new content, not against an empty adjustment.
    let restore=0;
    if(saved.scroll>0)restore=widget.add_tick_callback(()=>{
        const adjustment=widget.get_vadjustment();
        if(adjustment.page_size<=0)return GLib.SOURCE_CONTINUE;
        adjustment.value=Math.min(saved.scroll,Math.max(0,adjustment.upper-adjustment.page_size));
        restore=0;return GLib.SOURCE_REMOVE;
    });
    return{widget,updateSnapshot,updateLayout,dispose(){
        if(restore)widget.remove_tick_callback(restore);
        else saved.scroll=widget.get_vadjustment().value;
    }};
}
export async function audioDiagnostics(ctx){
    try{const data=await ctx.client.execute('diagnostics','audio',{});const content=box(true,14);const keys=[['처리 상태',data.status],['요청한 처리 방식',data.requestedBackend||data.aecBackend],['실제 처리 방식',data.activeBackend],['처리 지연',data.latencyMs==null?'측정 대기':`${data.latencyMs} ms`],['NPU 장치',data.npu?.device],['NPU 추론 횟수',data.npu?.inferences],['에러',data.error?.message||data.error||data.npu?.error]];
        keys.filter(([,v])=>v!=null).forEach(([k,v])=>content.append(preference(k,'',text(String(v),'caption'))));modal(ctx.window,'오디오 상세 진단',scroller(content),[],780,580);
    }catch(error){ctx.toast(error.message);}
}
