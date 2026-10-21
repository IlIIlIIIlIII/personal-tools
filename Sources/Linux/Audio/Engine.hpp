#pragma once
#include "AudioStream.hpp"
#include "DSP.hpp"
#include "NeuralProcessor.hpp"
#include <pipewire/pipewire.h>
#include <pipewire/impl.h>
#include <pipewire/filter.h>
#include <spa/param/audio/raw-utils.h>
#include <spa/param/props.h>
#include <spa/param/latency-utils.h>
#include <nlohmann/json.hpp>
#include <array>
#include <atomic>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

namespace personaltools::linuxaudio {
using Json=nlohmann::json;
constexpr unsigned maxFrames=16384;
struct Settings {
    std::string outputId,inputId,aecBackend="npu",npuModelDirectory,npuRuntimeLibrary;
    bool eqEnabled=false,aecEnabled=false;
    std::vector<PEQFilter>left,right;
    std::string leftFile,rightFile;
};
class Engine {
public:
    Engine();~Engine();
    Json execute(const std::string& command,const Json& args);
    void reconcile();
    bool quitRequested()const{return quit_;}
    bool connected()const{return connected_.load();}
private:
    struct EqState {
        StereoDSP dsp;bool enabled=false;
        std::array<double,maxFrames>left{},right{};
        EqState(const Settings& config);
    };
    struct MicState {
        EchoStream echo;std::unique_ptr<NeuralProcessor>neural;
        bool enabled=false;std::string requested;
        static constexpr unsigned extraDelay=NeuralProcessor::latencyFrames-EchoStream::latencyFrames;
        std::array<float,extraDelay>delay{};unsigned position=0;
        std::array<float,maxFrames>mic{},ref{},processed{};
        std::array<float,maxFrames*2>stereo{};
        MicState(const Settings& config,spa_thread_utils* threadUtils);
    };
    struct Node {
        Engine* owner;std::uint32_t id;std::map<std::string,std::string>props;
        pw_node* proxy=nullptr;spa_hook listener{};std::optional<bool>mute;
        ~Node();
    };
    struct Link {pw_link* proxy=nullptr;spa_hook listener{};pw_link_state state=PW_LINK_STATE_INIT;std::string error;~Link();};
    struct Port {std::uint32_t id,node,index;std::string direction,channel,name;bool monitor=false;};
    pw_thread_loop* loop_=nullptr;pw_context* context_=nullptr;pw_core* core_=nullptr;
    pw_registry* registry_=nullptr;spa_hook registryHook_{},coreHook_{},eqHook_{},micHook_{};
    pw_impl_module* outputModule_=nullptr;pw_impl_module* micModule_=nullptr;
    spa_hook outputModuleHook_{},micModuleHook_{};
    pw_filter* eqFilter_=nullptr;pw_filter* micFilter_=nullptr;
    std::array<void*,4>eqPorts_{};std::array<void*,4>micPorts_{};
    std::map<std::uint32_t,std::unique_ptr<Node>>nodes_;
    std::map<std::uint32_t,Port>ports_;
    std::map<std::pair<std::uint32_t,std::uint32_t>,std::unique_ptr<Link>>links_;
    std::atomic<EqState*>eqState_{nullptr};std::atomic<MicState*>micState_{nullptr};
    std::atomic<unsigned>eqReaders_{0},micReaders_{0};
    std::vector<std::unique_ptr<EqState>>retiredEq_;
    std::vector<std::unique_ptr<MicState>>retiredMic_;
    std::map<std::string,bool>originalMutes_,appliedMutes_;
    std::set<std::uint32_t>mutedNodes_;
    Settings settings_;bool quit_=false;std::atomic<bool>connected_{true};
    std::atomic<bool>muted_{false},referenceConnected_{false},captureConnected_{false};
    std::atomic<std::uint64_t>eqCallbacks_{0},micCallbacks_{0},frames_{0},badRate_{0},oversize_{0},maxCallbackNs_{0},lastCallbackNs_{0};
    std::atomic<float>inputLevel_{-120.f};std::atomic<unsigned>sampleRate_{48000};
    int syncSequence_=0,completedSequence_=0;
    std::string error_;
    Json devicesUnlocked()const;Json status();
    Json configure(const Json& args,bool restoring=false);Json mute(const Json& args);Json importEq(const Json& args);
    bool captureNode(const Node& node)const;
    bool setMute(Node& node,bool value);
    void publish(const Settings& value);
    void initializeFilters();
    void publishEndpoints();
    void sync();
    void connectPorts(const Port& from,const Port& to);
    std::optional<Port> port(std::uint32_t node,const std::string& direction,const std::string& channel,bool monitor=false)const;
    std::uint32_t findNode(const std::string& name)const;
    void updateLatency(unsigned frames);
    static void global(void*,std::uint32_t,std::uint32_t,const char*,std::uint32_t,const spa_dict*);
    static void globalRemove(void*,std::uint32_t);
    static void linkInfo(void*,const pw_link_info*);
    static void nodeParam(void*,int,std::uint32_t,std::uint32_t,std::uint32_t,const spa_pod*);
    static void nodeInfo(void*,const pw_node_info*);
    static void done(void*,std::uint32_t,int);
    static void coreError(void*,std::uint32_t,int,int,const char*);
    static void eqProcess(void*,spa_io_position*);
    static void micProcess(void*,spa_io_position*);
};
Json filterJson(const std::vector<PEQFilter>& filters);
std::vector<PEQFilter> parseFilters(const Json& value);
}
