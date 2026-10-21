import {box,label,text,icon,button,spacer,heading,preference,scroller,table,formatTime,group,status,dropdown} from '../ui.js';
import {audioDiagnostics} from './sound.js';
import {openFolder} from './settings.js';

export function diagnosticsPage(ctx){
    const s=ctx.snapshot;const body=box(true,20,'diagnostics-content');
    ctx.headerActions=[button('새로 고침','view-refresh-symbolic',()=>ctx.command('diagnostics','refresh',{})),button('진단 내보내기','document-save-symbolic',async()=>{try{const result=await ctx.client.execute('diagnostics','export',{});ctx.toast(`진단을 저장했습니다: ${result.path}`);}catch(e){ctx.toast(e.message);}},'primary')];
    body.append(heading('서비스 상태','연결 상태와 최근 동작을 확인합니다.'));
    let components=s.diagnostics.components||[];
    if(!components.length)components=[{name:'Personal Tools',status:s.service.status==='running'?'실행 중':'연결 필요',detail:'사용자 서비스'},{name:'GNOME 확장',status:s.settings.extensionConnected?'연결됨':'연결 필요',detail:'빠른 패널 · 단축키'},{name:'PipeWire',status:s.audio.status,detail:'출력 · 마이크'},{name:'NPU 오디오 처리',status:s.audio.npu?.status||'미확인',detail:s.audio.npu?.device||'Intel NPU'},{name:'AI 서비스',status:s.ai.vllm.status,detail:'vLLM'}];
    const icons=['edit-paste-symbolic','application-x-addon-symbolic','sound-wave-symbolic','applications-engineering-symbolic','network-server-symbolic'];
    body.append(table(['구성 요소','상태','세부 정보'],components.map((c,i)=>{const dot=icon('media-record-symbolic',12);dot.add_css_class(/error|failed|오류|실패/.test(c.status)?'error':'accent');return[box(false,18,'',[icon(icons[i%icons.length],26),label(c.name)]),box(false,12,'',[dot,label(c.status)]),String(c.detail||'')];}),(ctx.effectiveWidth??ctx.width)>=1300?[420,308]:[280,200]));
    const levels=['all','error','warning','info'];const filter=dropdown(['전체','오류','경고','정보'],Math.max(0,levels.indexOf(ctx.viewState.eventLevel||'all')),index=>{ctx.viewState.eventLevel=levels[index];ctx.render();});filter.hexpand=false;filter.width_request=172;body.append(heading('최근 이벤트','',filter));const events=(s.diagnostics.events||[]).filter(e=>!ctx.viewState.eventLevel||ctx.viewState.eventLevel==='all'||e.level===ctx.viewState.eventLevel).slice().sort((a,b)=>Date.parse(b.time)-Date.parse(a.time)).slice(0,8);
    body.append(table(['','시각','구성 요소','내용'],events.map(e=>[icon(e.level==='error'?'dialog-error-symbolic':e.level==='warning'?'dialog-warning-symbolic':'dialog-information-symbolic',20),formatTime(e.time,true),e.component,String(e.message)]),(ctx.effectiveWidth??ctx.width)>=1300?[48,180,220]:[24,120,160]));if(!events.length)body.append(label('아직 기록된 이벤트가 없습니다.','caption'));
    const audio=button('오디오 상세 진단','utilities-system-monitor-symbolic',()=>audioDiagnostics(ctx));audio.hexpand=true;const utilities=group();utilities.append(preference('오디오 상세 진단','처리 지연, 참조 신호와 프레임 상태',button('열기','go-next-symbolic',()=>audioDiagnostics(ctx)),'utilities-system-monitor-symbolic'));
    utilities.append(preference('로그 폴더 열기','',button('열기','go-next-symbolic',()=>openFolder(ctx,s.settings.logPath)),'folder-symbolic'));body.append(utilities);return{widget:scroller(body)};
}
