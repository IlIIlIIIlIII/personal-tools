// This module is copied nowhere into installed extension packages.
import GLib from 'gi://GLib';
const root = GLib.getenv('PERSONALTOOLS_TEST_ROOT');
const entry = (id, title, preview, kind = 'text', extra = {}) => ({id, title, preview, kind,
    sourceApp: '텍스트 편집기', sourceAttribution:'foreground-observed', createdAt:'2026-10-03T01:24:00Z',
    pinned:false, categoryIds:[],tags:[],attachments:[], ...extra});
const uri = name => GLib.filename_to_uri(`${root}/Sources/Linux/App/assets/${name}.png`, null);
export const entries = [
    entry('memo','회의 메모','다음 주 작업 정리\n클립보드 검색 흐름 점검\n오디오 설정 정리\n디스플레이 자동화 확인','text',{pinned:true}),
    entry('mountain','바탕화면 참고 이미지.png','산과 호수가 있는 풍경 이미지','image',{sourceApp:'파일',thumbnailUri:uri('mountain'),createdAt:'2026-10-03T01:12:00Z'}),
    entry('ubuntu','Ubuntu 개발 문서','https://documentation.ubuntu.com','url',{sourceApp:'firefox_firefox.desktop',linkTitle:'Ubuntu documentation',linkDescription:'The official Ubuntu documentation',createdAt:'2026-10-03T01:18:00Z'}),
    entry('eq','EQ 설정.txt','66 Hz    −12 dB\n140 Hz   −12 dB','files',{sourceApp:'파일',createdAt:'2026-10-03T00:56:00Z'}),
    entry('command','프로젝트 실행 명령','cmake --build build\nctest --test-dir build','text',{sourceApp:'터미널',createdAt:'2026-10-03T00:42:00Z'}),
];
export const aiEntries = [entries[1],
    entry('leaves','잎사귀 클로즈업.jpg','자연, 질감, 배경 참고 이미지','image',{sourceApp:'JPG 이미지',thumbnailUri:uri('leaves'),createdAt:'2026-10-03T00:28:00Z'}),
    entry('yaru','Yaru 디자인 가이드','https://design.ubuntu.com','url',{sourceApp:'URL',url:'https://design.ubuntu.com',linkDescription:'Ubuntu 공식 디자인 지침',createdAt:'2026-10-03T00:14:00Z'}),
];
export class FixtureClient {
    constructor() {
        this.snapshot = {version:1,revision:1,clipboard:{paused:false,entries,collections:[{id:'work',name:'작업'},{id:'reference',name:'참고 자료'}],settings:{itemLimitBytes:104857600}},
            audio:{status:'running',muted:false,eqEnabled:true,aecEnabled:true,inputs:[{id:'motu',name:'MOTU M2'}],inputId:'motu',npu:{device:'Intel NPU'}},
            display:{monitorPower:{available:true,enabled:true,running:true,status:'running',error:null,timeoutSeconds:60},monitors:[{id:'a',name:'MO32U24',connector:'HDMI-1',primary:true,hdrEnabled:true,hdrSupported:true},{id:'b',name:'MO32U24',connector:'DP-4',primary:false,hdrEnabled:false,hdrSupported:true}],inhibitorStatus:'ready',inhibitors:[{id:'firefox',appId:'org.mozilla.firefox',appName:'Firefox',reason:'동영상 재생 중'}]},
            ai:{vllm:{status:'stopped'}},settings:{clipboardShortcut:'<Control><Shift>v',microphoneShortcut:'<Super><Alt>m'}};
    }
    async refresh() { return this.snapshot; }
    async execute(domain, action) {
        if (domain === 'clipboard' && action === 'query') return {entries};
        if (domain === 'clipboard' && action === 'ai-search') {
            if (this.error) throw new Error('Antigravity 연결 상태를 확인한 뒤 다시 시도해 주세요.');
            return {entries:aiEntries,summary:'참고 자료에서 관련 이미지 2개를 찾았습니다.　 |　 검색 완료'};
        }
        return {ok:true};
    }
}
