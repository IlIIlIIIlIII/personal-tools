export const pageTitles={clipboard:'클립보드','clipboard-settings':'클립보드 설정',windows:'창 빠른 전환',sound:'사운드',display:'디스플레이',ai:'AI 서비스',diagnostics:'진단',settings:'설정'};

// Shell links name a destination, which may include a tab within a page.
export function resolvePageRoute(destination){
    if(destination==='display-power')return{page:'display',displayTab:'power'};
    if(!Object.hasOwn(pageTitles,destination))return null;
    return destination==='display'?{page:destination,displayTab:'hdr'}:{page:destination};
}
