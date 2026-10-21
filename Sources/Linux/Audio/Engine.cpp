#include "Engine.hpp"
#include <spa/pod/parser.h>
#include <pipewire/impl.h>
#include <spa/utils/result.h>
#include <cerrno>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <thread>

namespace personaltools::linuxaudio {
namespace {
std::string prop(const spa_dict* props,const char* key){const char* p=props?spa_dict_lookup(props,key):nullptr;return p?p:"";}
std::uint64_t nowNs(){return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();}
std::uint32_t number(const std::string& value){try{return static_cast<std::uint32_t>(std::stoul(value));}catch(...){return SPA_ID_INVALID;}}
unsigned rate(const spa_io_position* position){return position->clock.rate.num?position->clock.rate.denom/position->clock.rate.num:0;}
struct LoopLock{pw_thread_loop* loop;explicit LoopLock(pw_thread_loop* p):loop(p){pw_thread_loop_lock(loop);}~LoopLock(){pw_thread_loop_unlock(loop);}};
void validateKeys(const Json& object,std::initializer_list<const char*> allowed){
    if(!object.is_object())throw std::invalid_argument("args must be an object");
    for(auto it=object.begin();it!=object.end();++it){bool found=false;for(auto key:allowed)found=found||it.key()==key;if(!found)throw std::invalid_argument("Unknown setting: "+it.key());}
}
bool sameFilters(const std::vector<PEQFilter>& a,const std::vector<PEQFilter>& b){
    return a.size()==b.size()&&std::equal(a.begin(),a.end(),b.begin(),[](const auto& x,const auto& y){return x.frequencyHz==y.frequencyHz&&x.gainDB==y.gainDB&&x.q==y.q;});
}
}
Json filterJson(const std::vector<PEQFilter>& filters){auto result=Json::array();for(auto& f:filters)result.push_back({{"frequency",f.frequencyHz},{"gain",f.gainDB},{"q",f.q},{"enabled",true}});return result;}
std::vector<PEQFilter> parseFilters(const Json& value){
    if(!value.is_array()||value.size()>32)throw std::invalid_argument("Expected at most 32 EQ filters");
    std::vector<PEQFilter> result;
    for(const auto& f:value){validateKeys(f,{"frequency","gain","q","enabled"});
        PEQFilter filter{f.at("frequency").get<double>(),f.at("gain").get<double>(),f.at("q").get<double>()};
        if(!std::isfinite(filter.frequencyHz)||!std::isfinite(filter.gainDB)||!std::isfinite(filter.q)||filter.frequencyHz<=0||filter.frequencyHz>=24000||filter.q<=0)
            throw std::invalid_argument("EQ values must be finite with 0 < frequency < 24000 and Q > 0");
        if(!f.value("enabled",true))filter.gainDB=0;
        result.push_back(filter);
    }return result;
}
Engine::EqState::EqState(const Settings& c):enabled(c.eqEnabled){
    if(!c.left.empty()&&!c.right.empty()){std::string error;if(!dsp.configure(c.left,c.right,48000,error))throw std::invalid_argument(error);}
    if(enabled&&!dsp.configured())throw std::invalid_argument("Both channel filters are required before enabling EQ");
}
Engine::MicState::MicState(const Settings& c,spa_thread_utils* threadUtils):enabled(c.aecEnabled),requested(c.aecBackend){
    if(!echo.valid())throw std::runtime_error("Unable to create DSP echo canceller");
    if(c.aecBackend=="npu"&&enabled)neural=std::make_unique<NeuralProcessor>(c.npuModelDirectory,c.npuRuntimeLibrary,threadUtils);
}
Engine::Link::~Link(){if(proxy){spa_hook_remove(&listener);pw_proxy_destroy(reinterpret_cast<pw_proxy*>(proxy));}}
void Engine::linkInfo(void* data,const pw_link_info* info){auto& link=*static_cast<Link*>(data);link.state=info->state;link.error=info->error?info->error:"";}
Engine::Node::~Node(){if(proxy){spa_hook_remove(&listener);pw_proxy_destroy(reinterpret_cast<pw_proxy*>(proxy));}}
Engine::Engine(){
    pw_init(nullptr,nullptr);loop_=pw_thread_loop_new("personal-tools-audio",nullptr);
    if(!loop_)throw std::runtime_error("Cannot create PipeWire thread loop");
    context_=pw_context_new(pw_thread_loop_get_loop(loop_),nullptr,0);
    core_=context_?pw_context_connect(context_,nullptr,0):nullptr;
    if(!core_)throw std::runtime_error("Cannot connect to the user PipeWire server");
    static const auto coreEvents=[](){pw_core_events e{};e.version=PW_VERSION_CORE_EVENTS;e.done=done;e.error=coreError;return e;}();
    pw_core_add_listener(core_,&coreHook_,&coreEvents,this);
    registry_=pw_core_get_registry(core_,PW_VERSION_REGISTRY,0);
    static const pw_registry_events events{.version=PW_VERSION_REGISTRY_EVENTS,.global=global,.global_remove=globalRemove};
    pw_registry_add_listener(registry_,&registryHook_,&events,this);
    publish(settings_);initializeFilters();
    if(pw_thread_loop_start(loop_)<0)throw std::runtime_error("Cannot start PipeWire thread loop");
    {LoopLock lock(loop_);sync();}
}
Engine::~Engine(){
    if(loop_)pw_thread_loop_stop(loop_);
    links_.clear();
    if(outputModule_)pw_impl_module_destroy(outputModule_);
    if(micModule_)pw_impl_module_destroy(micModule_);
    if(eqFilter_)pw_filter_destroy(eqFilter_);
    if(micFilter_)pw_filter_destroy(micFilter_);
    delete eqState_.exchange(nullptr);delete micState_.exchange(nullptr);
    retiredEq_.clear();retiredMic_.clear();nodes_.clear();
    if(registry_)pw_proxy_destroy(reinterpret_cast<pw_proxy*>(registry_));
    if(core_)pw_core_disconnect(core_);
    if(context_)pw_context_destroy(context_);
    if(loop_)pw_thread_loop_destroy(loop_);
}
void Engine::sync(){
    syncSequence_=pw_core_sync(core_,PW_ID_CORE,0);
    while(connected_&&completedSequence_!=syncSequence_){if(pw_thread_loop_timed_wait(loop_,2)<0)throw std::runtime_error("PipeWire response timed out");}
    if(!connected_)throw std::runtime_error(error_);
}
void Engine::done(void* data,std::uint32_t id,int sequence){auto& e=*static_cast<Engine*>(data);if(id==PW_ID_CORE){e.completedSequence_=sequence;pw_thread_loop_signal(e.loop_,false);}}
// Only EPIPE on the core means the connection is gone. Other errors are
// recoverable, e.g. a link that lost a race with a hotplugged port and the
// "unknown resource" reply to destroying it; the next reconcile relinks.
void Engine::coreError(void* data,std::uint32_t id,int,int result,const char* message){
    auto& e=*static_cast<Engine*>(data);const char* text=message?message:spa_strerror(result);
    if(id!=PW_ID_CORE||result!=-EPIPE){pw_log_warn("PipeWire error on object %u: %s",id,text);return;}
    e.error_=text;e.connected_=false;pw_thread_loop_signal(e.loop_,false);
}
void Engine::global(void* data,std::uint32_t id,std::uint32_t,const char* type,std::uint32_t,const spa_dict* props){
    auto& e=*static_cast<Engine*>(data);
    if(std::strcmp(type,PW_TYPE_INTERFACE_Node)==0){
        auto node=std::make_unique<Node>();node->owner=&e;node->id=id;
        if(props)for(std::uint32_t i=0;i<props->n_items;++i)node->props[props->items[i].key]=props->items[i].value;
        const auto cls=node->props[PW_KEY_MEDIA_CLASS];
        if(e.captureNode(*node)){
            node->proxy=static_cast<pw_node*>(pw_registry_bind(e.registry_,id,PW_TYPE_INTERFACE_Node,PW_VERSION_NODE,0));
            if(node->proxy){
                static const auto events=[](){pw_node_events e{};e.version=PW_VERSION_NODE_EVENTS;e.info=nodeInfo;e.param=nodeParam;return e;}();
                pw_node_add_listener(node->proxy,&node->listener,&events,node.get());
                std::uint32_t param=SPA_PARAM_Props;pw_node_subscribe_params(node->proxy,&param,1);pw_node_enum_params(node->proxy,0,SPA_PARAM_Props,0,UINT32_MAX,nullptr);
            }
        }
        e.nodes_[id]=std::move(node);
    }else if(std::strcmp(type,PW_TYPE_INTERFACE_Port)==0){
        e.ports_[id]={id,number(prop(props,PW_KEY_NODE_ID)),number(prop(props,PW_KEY_PORT_ID)),prop(props,PW_KEY_PORT_DIRECTION),prop(props,SPA_KEY_AUDIO_CHANNEL),prop(props,PW_KEY_PORT_NAME),prop(props,PW_KEY_PORT_MONITOR)=="true"};
    }
}
void Engine::globalRemove(void* data,std::uint32_t id){auto& e=*static_cast<Engine*>(data);e.nodes_.erase(id);e.ports_.erase(id);e.mutedNodes_.erase(id);
    for(auto i=e.links_.begin();i!=e.links_.end();){if(i->first.first==id||i->first.second==id){i=e.links_.erase(i);}else ++i;}}
void Engine::nodeParam(void* data,int,std::uint32_t id,std::uint32_t,std::uint32_t,const spa_pod* pod){
    if(id!=SPA_PARAM_Props||!pod)return;
    auto& node=*static_cast<Node*>(data);bool muted=false;
    const auto* property=spa_pod_find_prop(pod,nullptr,SPA_PROP_mute);
    if(property&&spa_pod_get_bool(&property->value,&muted)==0)node.mute=muted;
}
void Engine::nodeInfo(void* data,const pw_node_info* info){
    // Registry globals include only exported properties. Monitor classification
    // and later property changes require the node's complete information.
    if(!info->props)return;
    auto& node=*static_cast<Node*>(data);
    for(std::uint32_t i=0;i<info->props->n_items;++i)
        node.props[info->props->items[i].key]=info->props->items[i].value;
}
void Engine::initializeFilters(){
    auto make=[this](const char* name,const char* description,const char* cls){
        return pw_filter_new(core_,name,pw_properties_new(PW_KEY_NODE_NAME,name,PW_KEY_NODE_DESCRIPTION,description,PW_KEY_MEDIA_CLASS,cls,
            "wireplumber.is-virtual","true",PW_KEY_NODE_VIRTUAL,"true",PW_KEY_NODE_AUTOCONNECT,"false",PW_KEY_NODE_WANT_DRIVER,"true",PW_KEY_NODE_RATE,"1/48000",PW_KEY_NODE_LATENCY,"256/48000",nullptr));
    };
    eqFilter_=make("personal-tools-eq-process","Personal Tools EQ processor","Audio/Filter");
    micFilter_=make("personal-tools-mic-process","Personal Tools Mic processor","Audio/Filter");
    if(!eqFilter_||!micFilter_)throw std::runtime_error("Cannot create audio filters");
    static const auto eqEvents=[](){pw_filter_events e{};e.version=PW_VERSION_FILTER_EVENTS;e.process=eqProcess;return e;}();
    static const auto micEvents=[](){pw_filter_events e{};e.version=PW_VERSION_FILTER_EVENTS;e.process=micProcess;return e;}();
    pw_filter_add_listener(eqFilter_,&eqHook_,&eqEvents,this);pw_filter_add_listener(micFilter_,&micHook_,&micEvents,this);
    auto add=[](pw_filter* f,pw_direction direction,const char* name,const char* channel){return pw_filter_add_port(f,direction,PW_FILTER_PORT_FLAG_MAP_BUFFERS,1,
        pw_properties_new(PW_KEY_FORMAT_DSP,"32 bit float mono audio",PW_KEY_PORT_NAME,name,SPA_KEY_AUDIO_CHANNEL,channel,nullptr),nullptr,0);};
    eqPorts_={add(eqFilter_,PW_DIRECTION_INPUT,"input_FL","FL"),add(eqFilter_,PW_DIRECTION_INPUT,"input_FR","FR"),add(eqFilter_,PW_DIRECTION_OUTPUT,"output_FL","FL"),add(eqFilter_,PW_DIRECTION_OUTPUT,"output_FR","FR")};
    micPorts_={add(micFilter_,PW_DIRECTION_INPUT,"microphone","MONO"),add(micFilter_,PW_DIRECTION_INPUT,"reference_FL","FL"),add(micFilter_,PW_DIRECTION_INPUT,"reference_FR","FR"),add(micFilter_,PW_DIRECTION_OUTPUT,"output_MONO","MONO")};
    for(auto p:eqPorts_)if(!p)throw std::runtime_error("Cannot allocate EQ port");
    for(auto p:micPorts_)if(!p)throw std::runtime_error("Cannot allocate microphone port");
    std::array<std::uint8_t,1024> formatBuffer{};
    spa_pod_builder formats=SPA_POD_BUILDER_INIT(formatBuffer.data(),formatBuffer.size());
    spa_audio_info_raw stereo{};stereo.format=SPA_AUDIO_FORMAT_F32P;stereo.rate=48000;stereo.channels=2;stereo.position[0]=SPA_AUDIO_CHANNEL_FL;stereo.position[1]=SPA_AUDIO_CHANNEL_FR;
    const spa_pod* eqParams[]={spa_format_audio_raw_build(&formats,SPA_PARAM_EnumFormat,&stereo)};
    spa_audio_info_raw mono{};mono.format=SPA_AUDIO_FORMAT_F32P;mono.rate=48000;mono.channels=1;mono.position[0]=SPA_AUDIO_CHANNEL_MONO;
    const spa_pod* micParams[]={spa_format_audio_raw_build(&formats,SPA_PARAM_EnumFormat,&mono)};
    if(pw_filter_connect(eqFilter_,PW_FILTER_FLAG_RT_PROCESS,eqParams,1)<0||pw_filter_connect(micFilter_,PW_FILTER_FLAG_RT_PROCESS,micParams,1)<0)throw std::runtime_error("Cannot connect audio filters");
    updateLatency(EchoStream::latencyFrames);
}
void Engine::publishEndpoints(){
    // WirePlumber keeps a configured default for as long as that node exists.
    // An endpoint outliving its physical device would stay the desktop default
    // while playing or capturing silence, so publish each one only while its
    // device exists: unplugging then falls back like hardware, and replugging
    // returns to the configured processed endpoint.
    static const char* outputArgs=R"({ audio.rate=48000 audio.channels=2 audio.position=[ FL FR ] node.latency=256/48000
        capture.props={ node.name=personal-tools-output node.description="Personal Tools Output" media.class=Audio/Sink node.virtual=true node.autoconnect=false priority.session=%d }
        playback.props={ node.name=personal-tools-output-feed node.passive=true node.autoconnect=false } })";
    static const char* micArgs=R"({ audio.rate=48000 audio.channels=1 audio.position=[ MONO ] node.latency=256/48000
        capture.props={ node.name=personal-tools-mic-feed node.passive=true node.autoconnect=false }
        playback.props={ node.name=personal-tools-mic node.description="Personal Tools Mic" media.class=Audio/Source node.virtual=true node.autoconnect=false priority.session=%d } })";
    // Loopback modules destroy themselves when their PipeWire core disappears.
    // Invalidate the handles before normal Engine cleanup can destroy them twice.
    static const auto outputEvents=[](){pw_impl_module_events events{};events.version=PW_VERSION_IMPL_MODULE_EVENTS;
        events.destroy=[](void* data){auto& engine=*static_cast<Engine*>(data);spa_hook_remove(&engine.outputModuleHook_);engine.outputModule_=nullptr;};return events;}();
    static const auto micModuleEvents=[](){pw_impl_module_events events{};events.version=PW_VERSION_IMPL_MODULE_EVENTS;
        events.destroy=[](void* data){auto& engine=*static_cast<Engine*>(data);spa_hook_remove(&engine.micModuleHook_);engine.micModule_=nullptr;};return events;}();
    auto follow=[this](pw_impl_module*& module,spa_hook& hook,const pw_impl_module_events& events,const char* args,const std::string& device){
        const auto id=device.empty()?SPA_ID_INVALID:findNode(device);
        if(id==SPA_ID_INVALID){if(module)pw_impl_module_destroy(module);return;}
        if(module)return;
        // WirePlumber ranks remembered defaults by priority.session plus one
        // point per history position. Outrank the device by less than a
        // priority tier but more than any plausible history depth, so a
        // fallback that would pick the bare device picks its processing instead.
        const auto& props=nodes_.at(id)->props;auto value=props.find(PW_KEY_PRIORITY_SESSION);
        if(value==props.end())value=props.find(PW_KEY_PRIORITY_DRIVER);
        int priority=50;if(value!=props.end())try{priority+=std::stoi(value->second);}catch(...){}
        std::array<char,1024>formatted{};std::snprintf(formatted.data(),formatted.size(),args,priority);
        module=pw_context_load_module(context_,"libpipewire-module-loopback",formatted.data(),nullptr);
        if(!module)throw std::runtime_error("Cannot create virtual audio endpoints");
        pw_impl_module_add_listener(module,&hook,&events,this);
    };
    follow(outputModule_,outputModuleHook_,outputEvents,outputArgs,settings_.outputId);
    follow(micModule_,micModuleHook_,micModuleEvents,micArgs,settings_.inputId);
}
void Engine::updateLatency(unsigned frames){
    std::array<std::uint8_t,256>buffer{};spa_pod_builder builder=SPA_POD_BUILDER_INIT(buffer.data(),buffer.size());
    spa_process_latency_info info{};info.rate=frames;const spa_pod* params[]={spa_process_latency_build(&builder,SPA_PARAM_ProcessLatency,&info)};
    pw_filter_update_params(micFilter_,nullptr,params,1);
}
void Engine::publish(const Settings& value){
    // Validate and build every changed state before publishing either one.
    // EQ edits must not discard the NPU's recurrent state or compile it again.
    const bool replaceEq=!eqState_.load()||settings_.eqEnabled!=value.eqEnabled||!sameFilters(settings_.left,value.left)||!sameFilters(settings_.right,value.right);
    const bool replaceMic=!micState_.load()||settings_.aecEnabled!=value.aecEnabled||settings_.aecBackend!=value.aecBackend||settings_.npuModelDirectory!=value.npuModelDirectory||settings_.npuRuntimeLibrary!=value.npuRuntimeLibrary;
    auto eq=replaceEq?std::make_unique<EqState>(value):nullptr;
    auto mic=replaceMic?std::make_unique<MicState>(value,static_cast<spa_thread_utils*>(pw_context_get_object(context_,SPA_TYPE_INTERFACE_ThreadUtils))):nullptr;
    if(eq)if(auto p=eqState_.exchange(eq.release()))retiredEq_.emplace_back(p);
    if(mic)if(auto p=micState_.exchange(mic.release()))retiredMic_.emplace_back(p);
    settings_=value;
}
void Engine::eqProcess(void* data,spa_io_position* position){
    auto& e=*static_cast<Engine*>(data);const auto n=static_cast<unsigned>(position->clock.duration);
    auto* l=static_cast<float*>(pw_filter_get_dsp_buffer(e.eqPorts_[0],n));auto* r=static_cast<float*>(pw_filter_get_dsp_buffer(e.eqPorts_[1],n));
    auto* outL=static_cast<float*>(pw_filter_get_dsp_buffer(e.eqPorts_[2],n));auto* outR=static_cast<float*>(pw_filter_get_dsp_buffer(e.eqPorts_[3],n));
    if(!outL||!outR)return;
    e.eqReaders_.fetch_add(1);auto* state=e.eqState_.load();
    if(n>maxFrames){std::fill_n(outL,n,0.f);std::fill_n(outR,n,0.f);++e.oversize_;}
    else if(state){
        for(unsigned i=0;i<n;++i){state->left[i]=l&&std::isfinite(l[i])?l[i]:0;state->right[i]=r&&std::isfinite(r[i])?r[i]:0;}
        if(state->enabled&&rate(position)==48000)state->dsp.processPlanar(state->left.data(),state->right.data(),n);
        for(unsigned i=0;i<n;++i){outL[i]=static_cast<float>(state->left[i]);outR[i]=static_cast<float>(state->right[i]);}
    }
    ++e.eqCallbacks_;e.eqReaders_.fetch_sub(1);
}
void Engine::micProcess(void* data,spa_io_position* position){
    auto& e=*static_cast<Engine*>(data);const auto start=nowNs();const auto n=static_cast<unsigned>(position->clock.duration);
    auto* mic=static_cast<float*>(pw_filter_get_dsp_buffer(e.micPorts_[0],n));auto* l=static_cast<float*>(pw_filter_get_dsp_buffer(e.micPorts_[1],n));
    auto* r=static_cast<float*>(pw_filter_get_dsp_buffer(e.micPorts_[2],n));auto* out=static_cast<float*>(pw_filter_get_dsp_buffer(e.micPorts_[3],n));if(!out)return;
    e.micReaders_.fetch_add(1);auto* state=e.micState_.load();e.sampleRate_=rate(position);
    if(n>maxFrames||!state){std::fill_n(out,n,0.f);++e.oversize_;}
    else if(rate(position)!=48000){for(unsigned i=0;i<n;++i)out[i]=mic&&std::isfinite(mic[i])&&!e.muted_?mic[i]:0; ++e.badRate_;}
    else{
        float peak=0;const bool reference=e.referenceConnected_&&l&&r;
        for(unsigned i=0;i<n;++i){
            state->mic[i]=mic&&std::isfinite(mic[i])?mic[i]:0;peak=std::max(peak,std::abs(state->mic[i]));
            state->stereo[i*2]=reference&&std::isfinite(l[i])?l[i]:0;state->stereo[i*2+1]=reference&&std::isfinite(r[i])?r[i]:0;
            state->ref[i]=.5f*(state->stereo[i*2]+state->stereo[i*2+1]);
        }
        const auto time=position->clock.nsec/100;
        if(reference)state->echo.reference(state->stereo.data(),n,2,time,false);
        state->echo.microphone(state->mic.data(),state->processed.data(),n,1,1,time,false,state->enabled&&state->requested!="bypass");
        // Both inputs share this graph clock. Its sample position, unlike the
        // driver's monotonic wake-up timestamp, is not affected by scheduling
        // jitter. Reset neural history on sample discontinuities, not late wakes.
        const auto graphTime=position->clock.position*10000000ull/48000;
        if(state->neural)state->neural->submit(state->mic.data(),state->ref.data(),n,graphTime,reference);
        for(unsigned i=0;i<n;++i){float value=state->processed[i];if(state->neural){float delayed=state->delay[state->position];state->delay[state->position]=value;state->position=(state->position+1)%MicState::extraDelay;value=state->neural->output(delayed,state->enabled);}
            out[i]=e.muted_?0.f:value;
        }e.inputLevel_=20.f*std::log10(std::max(peak,1e-6f));
    }
    ++e.micCallbacks_;e.frames_+=n;e.lastCallbackNs_=start;const auto elapsed=nowNs()-start;
    auto old=e.maxCallbackNs_.load();while(elapsed>old&&!e.maxCallbackNs_.compare_exchange_weak(old,elapsed)){}
    e.micReaders_.fetch_sub(1);
}
std::uint32_t Engine::findNode(const std::string& name)const{
    for(auto& [id,node]:nodes_){auto i=node->props.find(PW_KEY_NODE_NAME);if(i!=node->props.end()&&i->second==name)return id;}return SPA_ID_INVALID;
}
std::optional<Engine::Port> Engine::port(std::uint32_t node,const std::string& direction,const std::string& channel,bool monitor)const{
    std::optional<Port> first;
    for(auto& [id,p]:ports_)if(p.node==node&&p.direction==direction&&p.monitor==monitor){if(p.channel==channel)return p;if(!first||p.index<first->index)first=p;}
    if(channel=="MONO")return first;
    return {};
}
void Engine::connectPorts(const Port& from,const Port& to){
    auto key=std::pair{from.id,to.id};if(links_.contains(key))return;
    auto props=pw_properties_new(PW_KEY_LINK_OUTPUT_NODE,std::to_string(from.node).c_str(),PW_KEY_LINK_OUTPUT_PORT,std::to_string(from.id).c_str(),
        PW_KEY_LINK_INPUT_NODE,std::to_string(to.node).c_str(),PW_KEY_LINK_INPUT_PORT,std::to_string(to.id).c_str(),PW_KEY_LINK_PASSIVE,"true",PW_KEY_OBJECT_LINGER,"false",nullptr);
    auto link=std::make_unique<Link>();link->proxy=static_cast<pw_link*>(pw_core_create_object(core_,"link-factory",PW_TYPE_INTERFACE_Link,PW_VERSION_LINK,&props->dict,0));pw_properties_free(props);
    if(!link->proxy)throw std::runtime_error("Cannot create PipeWire link");
    static const pw_link_events events{.version=PW_VERSION_LINK_EVENTS,.info=linkInfo};
    pw_link_add_listener(link->proxy,&link->listener,&events,link.get());links_[key]=std::move(link);
}
void Engine::reconcile(){
    if(!loop_)return;
    {
        LoopLock lock(loop_);if(!connected_)return;
        publishEndpoints();
        if(muted_)for(auto& [id,node]:nodes_)if(captureNode(*node)&&node->proxy&&node->mute&&!mutedNodes_.contains(id)){
            if(setMute(*node,true))mutedNodes_.insert(id);
        }
        const auto sink=findNode(settings_.outputId),source=findNode(settings_.inputId);
        const auto eq=pw_filter_get_node_id(eqFilter_),mic=pw_filter_get_node_id(micFilter_);
        std::set<std::pair<std::uint32_t,std::uint32_t>>desired;std::array<std::pair<std::uint32_t,std::uint32_t>,2>refs{};std::pair<std::uint32_t,std::uint32_t>capture{};
        auto link=[&](auto from,auto to){std::pair<std::uint32_t,std::uint32_t>key{};if(from&&to){key={from->id,to->id};desired.insert(key);connectPorts(*from,*to);}return key;};
        for(const auto* channel:{"FL","FR"}){
            link(port(eq,"out",channel),port(sink,"in",channel));
            link(port(findNode("personal-tools-output-feed"),"out",channel),port(eq,"in",channel));
        }
        link(port(mic,"out","MONO"),port(findNode("personal-tools-mic-feed"),"in","MONO"));
        capture=link(port(source,"out","MONO"),port(mic,"in","MONO"));
        refs[0]=link(port(sink,"out","FL",true),port(mic,"in","FL"));refs[1]=link(port(sink,"out","FR",true),port(mic,"in","FR"));
        for(auto i=links_.begin();i!=links_.end();){if(!desired.contains(i->first))i=links_.erase(i);else ++i;}
        auto ready=[&](auto key){auto it=links_.find(key);return it!=links_.end()&&it->second->state>=PW_LINK_STATE_PAUSED;};
        referenceConnected_=ready(refs[0])&&ready(refs[1]);captureConnected_=ready(capture);
    }
    // Reclamation happens only on the control thread, after all old RT readers leave.
    if(eqReaders_.load()==0)retiredEq_.clear();
    if(micReaders_.load()==0)retiredMic_.clear();
}
bool Engine::captureNode(const Node& node)const{
    auto get=[&](const char* key){auto it=node.props.find(key);return it==node.props.end()?std::string{}:it->second;};
    return get(PW_KEY_MEDIA_CLASS)=="Audio/Source"&&!get(PW_KEY_NODE_NAME).starts_with("personal-tools-")&&get("device.class")!="monitor"&&get("stream.monitor")!="true"&&!get(PW_KEY_NODE_NAME).ends_with(".monitor");
}
bool Engine::setMute(Node& node,bool value){
    if(!node.proxy||!node.mute)return false;
    const auto name=node.props[PW_KEY_NODE_NAME];
    if(!originalMutes_.contains(name))originalMutes_[name]=*node.mute;
    std::array<std::uint8_t,128>buffer{};spa_pod_builder b=SPA_POD_BUILDER_INIT(buffer.data(),buffer.size());
    const auto* pod=static_cast<const spa_pod*>(spa_pod_builder_add_object(&b,SPA_TYPE_OBJECT_Props,SPA_PARAM_Props,SPA_PROP_mute,SPA_POD_Bool(value)));
    if(pw_node_set_param(node.proxy,SPA_PARAM_Props,0,pod)<0)return false;
    appliedMutes_[name]=value;return true;
}
Json Engine::devicesUnlocked()const{
    Json inputs=Json::array(),outputs=Json::array();
    for(auto& [id,node]:nodes_){
        auto get=[&](const char* k){auto i=node->props.find(k);return i==node->props.end()?std::string{}:i->second;};
        const auto cls=get(PW_KEY_MEDIA_CLASS),name=get(PW_KEY_NODE_NAME);
        if(name=="personal-tools-output"||name=="personal-tools-mic")continue;
        if(cls!="Audio/Source"&&cls!="Audio/Sink")continue;
        if(cls=="Audio/Source"&&!captureNode(*node))continue;
        unsigned channels=0;for(const auto& [portId,p]:ports_)if(p.node==id&&!p.monitor&&p.direction==(cls=="Audio/Source"?"out":"in"))++channels;
        Json item{{"id",name},{"name",get(PW_KEY_NODE_DESCRIPTION)},{"description",get(PW_KEY_NODE_NICK)},{"channels",channels},{"nodeId",id}};
        if(cls=="Audio/Source"){item["muted"]=node->mute?Json(*node->mute):Json(nullptr);inputs.push_back(item);}else outputs.push_back(item);
    }return {{"inputs",inputs},{"outputs",outputs}};
}
Json Engine::status(){
    LoopLock lock(loop_);auto devices=devicesUnlocked();auto* state=micState_.load();
    const bool fresh=lastCallbackNs_.load()!=0&&nowNs()-lastCallbackNs_.load()<2000000000ull;
    std::string stateName=!connected_?"error":settings_.inputId.empty()&&settings_.outputId.empty()?"unconfigured":fresh&&captureConnected_?"running":"idle";
    std::string active=state->enabled?(state->requested=="bypass"?"bypass":"dsp"):"bypass";
    Json npu{{"status","disabled"},{"device",""},{"inferences",0},{"error",nullptr},{"missingFrames",0},{"maxInferenceMs",nullptr}};
    if(state->neural){
        const auto s=state->neural->state();npu["status"]=s<0?"error":s==2?"ready":"loading";
        npu["device"]=s==2?"NPU":"";npu["inferences"]=state->neural->completed()*2;npu["hops"]=state->neural->completed();
        npu["missingFrames"]=state->neural->misses();npu["droppedPackets"]=state->neural->dropped();
        npu["discontinuities"]=state->neural->discontinuities();
        npu["workerRealtime"]=state->neural->realtime();
        npu["recentlyDegraded"]=state->neural->recentlyDegraded();
        npu["maxInferenceMs"]=state->neural->maximumInferenceNs()/1e6;
        if(s<0)npu["error"]=state->neural->error();
        if(s==2&&state->neural->usingNpu()&&fresh&&referenceConnected_&&captureConnected_&&sampleRate_==48000)active="npu";
    }
    const bool degraded=state->enabled&&state->requested=="npu"&&(active!="npu"||(state->neural&&state->neural->recentlyDegraded()));
    const char* degradedReason=!degraded?"":!captureConnected_?"Microphone input is disconnected":!referenceConnected_?"Playback reference is disconnected":sampleRate_!=48000?"Audio graph must run at 48000 Hz":state->neural&&state->neural->state()<0?"NPU inference failed; DSP fallback is active":state->neural&&state->neural->state()!=2?"NPU model is loading; DSP fallback is active":"NPU missed an output deadline within the last second; DSP fallback was used";
    if(fresh&&sampleRate_!=48000)stateName="unsupported-rate";
    Json links=Json::array();for(auto& [key,value]:links_)links.push_back({{"outputPort",key.first},{"inputPort",key.second},{"state",pw_link_state_as_string(value->state)},{"error",value->error}});
    const auto metrics=state->echo.metrics();
    return {{"status",stateName},{"error",error_.empty()?Json(nullptr):Json(error_)},{"outputs",devices["outputs"]},{"inputs",devices["inputs"]},
        {"outputId",settings_.outputId},{"inputId",settings_.inputId},{"eqEnabled",settings_.eqEnabled},{"aecEnabled",settings_.aecEnabled},{"muted",muted_.load()},
        {"leftFilters",filterJson(settings_.left)},{"rightFilters",filterJson(settings_.right)},{"leftFile",settings_.leftFile},{"rightFile",settings_.rightFile},
        {"aecBackend",settings_.aecBackend},{"activeBackend",active},{"degraded",degraded},{"degradedReason",degradedReason},{"npu",npu},
        {"latencyMs",(state->neural?NeuralProcessor::latencyFrames:EchoStream::latencyFrames)*1000.0/48000},{"inputLevelDb",fresh?Json(inputLevel_.load()):Json(nullptr)},
        {"sampleRate",sampleRate_.load()},{"captureConnected",captureConnected_.load()},{"referenceConnected",referenceConnected_.load()},
        {"diagnostics",{{"eqCallbacks",eqCallbacks_.load()},{"captureCallbacks",micCallbacks_.load()},{"frames",frames_.load()},{"referenceUnderruns",metrics.referenceUnderruns},
            {"reductionDb",metrics.reductionDB},{"queueDrops",state->echo.dropped()},{"unsupportedRateCallbacks",badRate_.load()},{"oversizedCallbacks",oversize_.load()},
            {"maxCallbackMs",maxCallbackNs_.load()/1e6},{"links",links}}}};
}
Json Engine::configure(const Json& args,bool restoring){
    validateKeys(args,{"outputId","inputId","eqEnabled","aecEnabled","leftFilters","rightFilters","aecBackend","npuModelDirectory","npuRuntimeLibrary"});
    Settings next=settings_;
    if(args.contains("outputId"))next.outputId=args.at("outputId").get<std::string>();
    if(args.contains("inputId"))next.inputId=args.at("inputId").get<std::string>();
    if(args.contains("eqEnabled"))next.eqEnabled=args.at("eqEnabled").get<bool>();
    if(args.contains("aecEnabled"))next.aecEnabled=args.at("aecEnabled").get<bool>();
    if(args.contains("leftFilters"))next.left=parseFilters(args["leftFilters"]);
    if(args.contains("rightFilters"))next.right=parseFilters(args["rightFilters"]);
    if(args.contains("aecBackend"))next.aecBackend=args["aecBackend"].get<std::string>();
    if(next.aecBackend!="npu"&&next.aecBackend!="dsp"&&next.aecBackend!="bypass")throw std::invalid_argument("aecBackend must be npu, dsp or bypass");
    if(args.contains("npuModelDirectory"))next.npuModelDirectory=args["npuModelDirectory"].get<std::string>();
    if(args.contains("npuRuntimeLibrary"))next.npuRuntimeLibrary=args["npuRuntimeLibrary"].get<std::string>();
    {
        LoopLock lock(loop_);
        auto check=[&](const std::string& name,const char* cls){if(name.empty())return;auto id=findNode(name);if((id==SPA_ID_INVALID&&!restoring)||(id!=SPA_ID_INVALID&&(nodes_.at(id)->props[PW_KEY_MEDIA_CLASS]!=cls||(std::strcmp(cls,"Audio/Source")==0&&!captureNode(*nodes_.at(id)))))||name.starts_with("personal-tools-"))throw std::invalid_argument("Selected audio device is unavailable or invalid: "+name);};
        check(next.outputId,"Audio/Sink");check(next.inputId,"Audio/Source");
    }
    publish(next);{LoopLock lock(loop_);updateLatency(micState_.load()->neural?NeuralProcessor::latencyFrames:EchoStream::latencyFrames);}
    reconcile();{LoopLock lock(loop_);sync();}reconcile();return status();
}
Json Engine::importEq(const Json& args){
    validateKeys(args,{"channel","path"});const auto channel=args.at("channel").get<std::string>();
    if(channel!="left"&&channel!="right")throw std::invalid_argument("channel must be left or right");
    const std::filesystem::path path=args.at("path").get<std::string>();
    if(!path.is_absolute()||!std::filesystem::is_regular_file(path)||std::filesystem::file_size(path)>1024*1024)throw std::invalid_argument("EQ file must be an absolute regular file smaller than 1 MiB");
    const auto parsed=parseREWConfigurablePEQFile(path,channel=="left"?Channel::left:Channel::right);
    if(!parsed)throw std::invalid_argument(parsed.error);
    Settings next=settings_;if(channel=="left"){next.left=parsed.filters;next.leftFile=path.string();}else{next.right=parsed.filters;next.rightFile=path.string();}
    publish(next);return status();
}
Json Engine::mute(const Json& args){
    validateKeys(args,{"muted"});Json affected=Json::array(),failed=Json::array();
    {
        LoopLock lock(loop_);bool all=true,found=false;for(auto& [id,node]:nodes_)if(captureNode(*node)){found=true;all=all&&node->mute.value_or(false);}if(!found)all=muted_.load();
        const bool wanted=args.contains("muted")?args.at("muted").get<bool>():!all;
        for(auto& [id,node]:nodes_)if(captureNode(*node)&&node->proxy){
            const auto name=node->props[PW_KEY_NODE_NAME];if(!node->mute){failed.push_back({{"id",name},{"reason","Mute state could not be observed"}});continue;}
            if(!setMute(*node,wanted))failed.push_back({{"id",name},{"reason","PipeWire rejected mute"}});else{affected.push_back(name);mutedNodes_.insert(id);}
        }
        muted_=wanted;sync();
        for(auto& [id,node]:nodes_)if(node->proxy)pw_node_enum_params(node->proxy,0,SPA_PARAM_Props,0,UINT32_MAX,nullptr);
        sync();
        for(const auto& name:affected){auto id=findNode(name.get<std::string>());if(id==SPA_ID_INVALID||nodes_.at(id)->mute!=wanted)failed.push_back({{"id",name},{"reason","Mute did not match after readback"}});}
    }
    auto result=status();result["muteResult"]={{"affected",affected},{"failed",failed},{"verified",failed.empty()}};return result;
}
Json Engine::execute(const std::string& command,const Json& args){
    if(command=="status")return status();
    if(command=="devices"){LoopLock lock(loop_);return devicesUnlocked();}
    if(command=="configure")return configure(args);
    if(command=="restore")return configure(args,true);
    if(command=="import-eq")return importEq(args);
    if(command=="mute")return mute(args);
    if(command=="shutdown"){
        // Restore only states changed by this engine, never guessed defaults.
        {LoopLock lock(loop_);for(auto& [name,value]:originalMutes_){auto id=findNode(name);if(id==SPA_ID_INVALID||!nodes_.at(id)->proxy||nodes_.at(id)->mute!=appliedMutes_[name])continue;
            std::array<std::uint8_t,128>buffer{};spa_pod_builder b=SPA_POD_BUILDER_INIT(buffer.data(),buffer.size());
            auto* pod=static_cast<spa_pod*>(spa_pod_builder_add_object(&b,SPA_TYPE_OBJECT_Props,SPA_PARAM_Props,SPA_PROP_mute,SPA_POD_Bool(value)));
            pw_node_set_param(nodes_.at(id)->proxy,SPA_PARAM_Props,0,pod);}if(connected_)sync();}
        quit_=true;return {{"shutdown",true}};
    }
    throw std::invalid_argument("Unknown audio command: "+command);
}
}
