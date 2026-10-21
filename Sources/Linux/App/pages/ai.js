import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import {box,label,text,icon,button,iconButton,entry,number,dropdown,spacer,group,heading,preference,scroller,segmented,separator,modal,fileChooser,status} from '../ui.js';

const stateNames={running:'서비스 실행 중',ready:'준비 완료',starting:'서버 시작 중',stopping:'중지 중',stopped:'중지됨',error:'오류',unavailable:'사용할 수 없음',unknown:'확인 중'};
export function aiPage(ctx){
    const current=ctx.viewState.aiTab||'antigravity';const body=box(true,24,'page-content ai-content');
    body.append(segmented([{id:'antigravity',title:'Antigravity'},{id:'vllm',title:'vLLM'}],current,id=>{ctx.viewState.aiTab=id;ctx.render();}));
    if(current==='antigravity')bridge(ctx,body);else vllm(ctx,body);
    return{widget:scroller(body)};
}
function bridge(ctx,body){
    const s=ctx.snapshot.ai.antigravity;const running=s.status==='running';
    ctx.headerStatus=status(s.status,stateNames[s.status]||s.status);ctx.headerActions=[button(running?'서비스 중지':'서비스 시작',running?'media-playback-stop-symbolic':'media-playback-start-symbolic',()=>ctx.command('antigravity',running?'stop':'start',{}),running?'':'primary')];
    body.append(box(true,10,'',[label('Antigravity','page-title'),label('CLI 연결을 로컬 API로 사용합니다.')]));
    if(s.error)body.append(text(s.error.message||s.error,'failure-banner'));
    const left=box(true,18);left.hexpand=true;const connection=box(true,16,'group group-pad');connection.append(heading('연결','',null,'insert-link-symbolic'));connection.append(separator());
    const path=entry(s.cliPath||'','Antigravity CLI 경로');const browse=button('찾아보기',null,()=>fileChooser(ctx.window,'Antigravity CLI 선택',async value=>{path.text=value;await ctx.command('antigravity','configure',{cliPath:value});}));
    path.connect('activate',()=>ctx.command('antigravity','configure',{cliPath:path.text}));
    connection.append(form('CLI 경로',box(false,10,'',[path,browse])));
    connection.append(form('로그인',box(false,12,'',[button('CLI 열기 · 로그인','external-link-symbolic',()=>ctx.command('antigravity','login',{})),text('CLI에서 계정으로 로그인합니다.','caption')])));
    // Keep lookup state across polling renders and navigation, and consume the
    // command result directly instead of waiting for a snapshot-driven rebuild.
    if(!ctx.viewState.modelLookup||ctx.viewState.modelLookup.cliPath!==s.cliPath)ctx.viewState.modelLookup={cliPath:s.cliPath,busy:false};
    const lookup=ctx.viewState.modelLookup;
    const available=lookup.models??s.models;
    const models=available?.length?available:[s.model||'antigravity'];const model=dropdown(models.map(x=>x==='antigravity'?'CLI 기본 모델':x),Math.max(0,models.indexOf(s.model)),i=>ctx.command('antigravity','configure',{model:models[i]}));
    const checkModels=button(lookup.busy?'모델 확인 중…':'모델 확인','view-refresh-symbolic',async()=>{
        if(lookup.busy)return;
        lookup.busy=true;lookup.error='';lookup.message='';ctx.render();
        try{
            const result=await ctx.client.execute('antigravity','models',{});
            if(!Array.isArray(result?.models)||result.models.some(id=>typeof id!=='string'||!id.trim()))throw new Error('모델 목록 응답을 확인할 수 없습니다. 다시 시도해 주세요.');
            lookup.models=[...new Set(result.models)];
            const count=lookup.models.filter(id=>id!=='antigravity').length;
            lookup.message=count?`사용 가능한 모델 ${count}개를 확인했습니다.`:'개별 모델 목록이 없습니다. CLI 기본 모델을 사용합니다.';
        }catch(e){lookup.error=e.message;}
        finally{lookup.busy=false;ctx.render();}
    });checkModels.sensitive=!lookup.busy;
    connection.append(form('모델',box(false,12,'',[model,checkModels])));
    if(lookup.busy||lookup.error||lookup.message)connection.append(text(lookup.busy?'CLI에서 모델 목록을 조회하고 있습니다…':lookup.error||lookup.message,lookup.error?'error':'caption'));
    connection.append(text('CLI에 설정된 계정과 모델을 사용합니다.','caption'));left.append(connection);
    const api=box(true,16,'group group-pad');api.append(heading('로컬 API','',null,'network-server-symbolic'));api.append(separator());const address=entry(s.baseUrl||'http://127.0.0.1:8877/v1');address.editable=false;
    api.append(form('API 주소',box(false,12,'',[address,iconButton('edit-copy-symbolic','API 주소 복사',()=>copyText(ctx,address.text))])));
    const key=entry(s.keyConfigured?'••••••••••••••••••••':'아직 생성되지 않음');key.editable=false;let revealed=false;
    api.append(form('API 키',box(false,10,'',[key,iconButton('view-reveal-symbolic','API 키 표시',async()=>{try{if(revealed){key.text='••••••••••••••••••••';revealed=false;}else{const data=await ctx.client.execute('antigravity','api-key',{});key.text=data.apiKey;revealed=true;}}catch(e){ctx.toast(e.message);}}),iconButton('edit-copy-symbolic','API 키 복사',async()=>{try{const data=await ctx.client.execute('antigravity','api-key',{});copyText(ctx,data.apiKey);}catch(e){ctx.toast(e.message);}})])));
    const port=number(s.port||8877,1024,65535,1,0,v=>ctx.command('antigravity','configure',{port:v}));const timeout=number(s.timeoutSeconds||120,10,600,10,0,v=>ctx.command('antigravity','configure',{timeoutSeconds:v}));port.hexpand=false;port.width_request=188;const portBox=box(false,0,'',[port,spacer()]);api.append(form('포트',portBox));api.append(form('요청 시간 제한',box(false,12,'',[timeout,label('초','caption')])));api.append(text('로컬에서 실행되는 API 서버에 연결합니다.','caption'));left.append(api);
    const test=box(true,16,'group group-pad');test.width_request=(ctx.effectiveWidth??ctx.width)>=1250?Math.round((ctx.width-326)*0.378):370;test.hexpand=false;test.append(heading('연결 테스트','',null,'media-playback-start-symbolic'));test.append(label('프롬프트','caption'));
    const state=ctx.viewState;const prompt=new Gtk.TextView({wrap_mode:Gtk.WrapMode.WORD_CHAR,vexpand:false});prompt.buffer.text=state.testPrompt??'안녕하세요. 짧게 인사해 주세요.';prompt.buffer.connect('changed',()=>state.testPrompt=prompt.buffer.text);const promptArea=scroller(prompt);promptArea.vexpand=false;promptArea.min_content_height=112;promptArea.max_content_height=112;test.append(promptArea);
    const response=text(state.testBusy?'응답을 기다리고 있습니다…':state.testError||state.testResponse||(ctx.fixture?'안녕하세요! 무엇을 도와드릴까요?':''),'',12);response.valign=Gtk.Align.START;const responseBox=box(true,0,'inset',[response]);responseBox.height_request=156;
    const run=button('테스트 실행','media-playback-start-symbolic',async()=>{
        if(state.testBusy)return;
        const request=state.testRequest=(state.testRequest||0)+1;const submitted=prompt.buffer.text;
        state.testBusy=true;state.testError='';state.testResponse='';state.testPrompt=submitted;ctx.render();
        try{const result=await ctx.client.execute('antigravity','test',{prompt:submitted});if(state.testRequest===request)state.testResponse=result.response||'빈 응답을 받았습니다.';}
        catch(e){if(state.testRequest===request)state.testError=e.message;}
        finally{if(state.testRequest===request){state.testBusy=false;ctx.render();}}
    },'primary');run.sensitive=!state.testBusy;test.append(run);test.append(separator());test.append(label('응답'));test.append(responseBox);const testStatus=label('응답을 받았습니다.','caption accent');testStatus.visible=!state.testBusy&&!state.testError&&Boolean(ctx.fixture||state.testResponse);test.append(testStatus);test.append(spacer(true));test.append(separator());test.append(text('텍스트 대화 · 스트리밍 지원','caption'));
    body.append(box((ctx.effectiveWidth??ctx.width)<1250,18,'',[left,test]));
}
function form(title,control){const l=label(title,'strong');l.width_request=120;control.hexpand=true;return box(false,18,'',[l,control]);}
function copyText(ctx,value){if(ctx.fixture){ctx.toast('검수 모드입니다. 실제 클립보드는 변경하지 않았습니다.');return;}Gdk.Display.get_default().get_clipboard().set_content(Gdk.ContentProvider.new_for_value(String(value)));ctx.toast('클립보드에 복사했습니다.');}
function vllm(ctx,body){
    const s=ctx.snapshot.ai.vllm;const busy=['starting','stopping'].includes(s.status);const running=['running','ready'].includes(s.status);
    const action=button(busy?stateNames[s.status]:running?'서버 중지':'서버 시작',busy?'content-loading-symbolic':running?'media-playback-stop-symbolic':'media-playback-start-symbolic',()=>ctx.command('vllm',running?'stop':'start',{}),running?'':'primary');action.sensitive=!busy;
    ctx.headerStatus=status(s.status,stateNames[s.status]||s.status);ctx.headerActions=[action];
    body.append(box(true,10,'',[label('Qwen3.8 · 27B','page-title'),label('로컬에서 실행하는 언어 모델')]));body.append(label(`${s.quantization||'INT4'}   ·   ${s.kvCache||'FP8'} KV   ·   컨텍스트 ${(s.context||8192).toLocaleString('ko-KR')}`));
    if(s.error)body.append(text(s.error.message||s.error,'failure-banner'));
    const timeline=box(true,12,'group group-pad');timeline.hexpand=true;timeline.append(heading('서버 시작','',null,'network-server-symbolic'));timeline.append(separator());
    const defaults=[['service','서비스 시작'],['weights','모델 가중치 로딩'],['engine','엔진 준비'],['api','API 확인'],['warmup','대화 워밍업'],['ready','준비 완료']];
    const steps=s.steps?.length?s.steps:defaults.map(([id,name])=>({id,label:name,state:'pending'}));
    steps.forEach((step,index)=>{const number=step.state==='done'?icon('object-select-symbolic',22):label(`${index+1}`,'',{xalign:0.5});number.add_css_class('step-number');if(step.state==='done')number.add_css_class('done');if(step.state==='running')number.add_css_class('running');number.valign=Gtk.Align.START;
        const details=box(true,9,'',[label(step.label||defaults[index]?.[1]||step.id,'strong'),text(step.detail||(step.state==='pending'?'시작 대기':''),'caption')]);details.hexpand=true;if(step.state==='pending')details.opacity=0.55;
        if(step.state==='running'){if(step.total)details.append(label(`${step.current||0} / ${step.total}   ·   경과 시간 ${elapsed(s.elapsedSeconds)}`,'caption',{xalign:1}));if(step.progress!=null)details.append(new Gtk.ProgressBar({fraction:Math.max(0,Math.min(1,step.progress>1?step.progress/100:step.progress))}));else details.append(box(false,12,'',[new Gtk.Spinner({spinning:true}),label('처리 중','caption')]));}
        const marker=box(true,4);marker.append(number);if(index<steps.length-1){const line=new Gtk.Separator({orientation:Gtk.Orientation.VERTICAL,halign:Gtk.Align.CENTER,vexpand:true,css_classes:['step-line']});marker.append(line);}timeline.append(box(false,24,'step-row',[marker,details]));});
    timeline.append(button('시작 로그 보기','utilities-terminal-symbolic',async()=>{try{const result=await ctx.client.execute('vllm','logs',{});const view=new Gtk.TextView({editable:false,monospace:true,wrap_mode:Gtk.WrapMode.WORD_CHAR});view.buffer.text=result.text||'아직 기록된 로그가 없습니다.';modal(ctx.window,'vLLM 시작 로그',scroller(view),[],900,600);}catch(e){ctx.toast(e.message);}}));
    const settings=box(true,24,'group group-pad');settings.hexpand=false;settings.width_request=(ctx.effectiveWidth??ctx.width)>=1250?Math.round((ctx.width-326)*0.414):400;settings.append(heading('실행 설정','',null,'emblem-system-symbolic'));settings.append(separator());
    settings.append(form('모델',text(s.checkpoint||'RedHatAI/Qwen3.8-27B-INT4','',3)));settings.append(form('GPU',text(s.gpu||'장치 확인 대기','',3)));const address=entry(s.baseUrl);address.editable=false;settings.append(form('API 주소',box(false,12,'',[address,iconButton('edit-copy-symbolic','API 주소 복사',()=>copyText(ctx,s.baseUrl))])));settings.append(form('동시 요청 수',number(s.maxSequences||1,1,1,1,0,v=>ctx.command('vllm','configure',{maxSequences:v}))));settings.append(separator());settings.append(button('고급 설정','go-next-symbolic',()=>vllmSettings(ctx)));settings.append(separator());settings.append(text('창을 닫아도 서버는 계속 실행됩니다.','caption'));settings.append(spacer(true));
    body.append(box((ctx.effectiveWidth??ctx.width)<1250,18,'',[timeline,settings]));
}
function elapsed(value=0){return`${Math.floor(value/60).toString().padStart(2,'0')}:${Math.floor(value%60).toString().padStart(2,'0')}`;}
function vllmSettings(ctx){const s=ctx.snapshot.ai.vllm;const sizes=[8192,16384,32768,65536];const context=dropdown(sizes.map(n=>n.toLocaleString('ko-KR')),Math.max(0,sizes.indexOf(s.context)));const offloads=[0,2,4,6,8];const offload=dropdown(offloads.map(n=>`${n} GiB`),Math.max(0,offloads.indexOf(s.cpuOffloadGiB||0)));const content=box(true,18,'',[text('검증된 프로필만 사용하세요. 설정을 바꾸려면 서버를 먼저 중지하세요.','caption'),preference('컨텍스트','',context),preference('가중치 양자화','',label(s.quantization||'INT4')),preference('KV 캐시','',label(s.kvCache||'FP8')),preference('CPU 가중치 오프로드','GPU 여유를 확보하지만 응답 속도가 느려질 수 있습니다. 추론은 CUDA에서 실행합니다.',offload)]);let dialog;dialog=modal(ctx.window,'vLLM 고급 설정',content,[button('저장',null,async()=>{try{await ctx.client.execute('vllm','configure',{context:sizes[context.selected],cpuOffloadGiB:offloads[offload.selected]});dialog.close();ctx.render();}catch(e){ctx.toast(e.message);}},'primary')],680);}
