#include "NeuralProcessor.hpp"
#include "AudioStream.hpp"
#include <memory>
#include <iostream>
#include <chrono>
#include <thread>
using namespace personaltools::linuxaudio;
int main(){
    auto neural=std::make_unique<NeuralProcessor>("/nonexistent/model","/nonexistent/libopenvino_c.so");
    for(unsigned i=0;i<200&&neural->state()>=0;++i)std::this_thread::sleep_for(std::chrono::milliseconds(1));
    if(neural->state()!=-1||neural->error().empty()){std::cerr<<"Missing runtime must report a precise failure\n";return 1;}
    std::array<float,257>mic{},ref{};
    for(unsigned i=0;i<10000;++i){
        if(i%257==0)neural->submit(mic.data(),ref.data(),257,10000000ull+i*10000000ull/48000,false);
        const float expected=float(i%997)/2000.f;
        if(neural->output(expected,true)!=expected){std::cerr<<"Unavailable NPU changed DSP fallback\n";return 1;}
    }
    if(neural->completed()!=0){std::cerr<<"Missing runtime reported inference\n";return 1;}
    if(neural->misses()==0){std::cerr<<"Missing runtime did not count unavailable output\n";return 1;}
    std::cout<<"NPU error honesty, exact DSP fallback, and missing-output counters passed\n";
}
