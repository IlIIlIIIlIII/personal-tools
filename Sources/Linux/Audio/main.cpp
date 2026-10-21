#include "Engine.hpp"
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <sys/file.h>
#include <fcntl.h>
#include <unistd.h>
#include <poll.h>
#include <signal.h>
#include <cerrno>
#include <cstring>
#include <filesystem>
#include <iostream>
#include <fstream>

using namespace personaltools::linuxaudio;
namespace {
volatile sig_atomic_t interrupted=0;
void interrupt(int){interrupted=1;}
struct Socket {
    int fd=-1;std::filesystem::path path;bool owns=false;
    ~Socket(){if(fd>=0)close(fd);if(owns)unlink(path.c_str());}
    void open(const std::filesystem::path& value){
        path=value;auto parent=path.parent_path();std::filesystem::create_directories(parent);
        struct stat info{};
        if(lstat(parent.c_str(),&info)<0||!S_ISDIR(info.st_mode)||info.st_uid!=getuid())throw std::runtime_error("Audio socket directory is not owned by this user");
        chmod(parent.c_str(),0700);
        if(lstat(path.c_str(),&info)==0){
            if(!S_ISSOCK(info.st_mode)||info.st_uid!=getuid())throw std::runtime_error("Refusing to replace an unrelated socket path");
            const int probe=socket(AF_UNIX,SOCK_STREAM|SOCK_CLOEXEC,0);sockaddr_un test{};test.sun_family=AF_UNIX;
            if(path.string().size()>=sizeof(test.sun_path)){close(probe);throw std::runtime_error("Socket path is too long");}
            std::strcpy(test.sun_path,path.c_str());const int result=connect(probe,reinterpret_cast<sockaddr*>(&test),sizeof(test));close(probe);
            if(result==0)throw std::runtime_error("Audio engine is already running");
            if(errno!=ECONNREFUSED&&errno!=ENOENT)throw std::runtime_error("Existing audio socket cannot be checked");
            if(unlink(path.c_str())<0)throw std::runtime_error("Cannot remove stale audio socket");
        }
        fd=socket(AF_UNIX,SOCK_STREAM|SOCK_CLOEXEC|SOCK_NONBLOCK,0);if(fd<0)throw std::runtime_error("Cannot create control socket");
        sockaddr_un address{};address.sun_family=AF_UNIX;
        if(path.string().size()>=sizeof(address.sun_path))throw std::runtime_error("Socket path is too long");
        std::strcpy(address.sun_path,path.c_str());
        const mode_t mask=umask(0077);const int result=bind(fd,reinterpret_cast<sockaddr*>(&address),sizeof(address));umask(mask);
        if(result<0)throw std::runtime_error(std::string("Cannot bind control socket: ")+std::strerror(errno));
        owns=true;
        if(chmod(path.c_str(),0600)<0||listen(fd,8)<0)throw std::runtime_error("Cannot listen on private control socket");
    }
};
struct EngineLock {
    int fd=-1;
    explicit EngineLock(const std::filesystem::path& runtime){
        const auto directory=runtime/"personal-tools";
        std::filesystem::create_directories(directory);
        struct stat info{};
        if(lstat(directory.c_str(),&info)<0||!S_ISDIR(info.st_mode)||info.st_uid!=getuid())
            throw std::runtime_error("Audio runtime directory is not owned by this user");
        chmod(directory.c_str(),0700);
        fd=::open((directory/"audio-engine.lock").c_str(),O_CREAT|O_CLOEXEC|O_NOFOLLOW|O_RDWR,0600);
        if(fd<0)throw std::runtime_error("Cannot open audio engine lock");
        if(fstat(fd,&info)<0||!S_ISREG(info.st_mode)||info.st_uid!=getuid()||flock(fd,LOCK_EX|LOCK_NB)<0){
            close(fd);fd=-1;throw std::runtime_error("An audio engine is already running for this session");
        }
    }
    ~EngineLock(){if(fd>=0)close(fd);}
};
Json response(Engine& engine,const std::string& line){
    Json id=nullptr;
    try{
        const auto request=Json::parse(line);
        if(!request.is_object()||!request.contains("id")||!request.contains("command")||!request.value("args",Json::object()).is_object())throw std::invalid_argument("Expected {id,command,args}");
        id=request.at("id");if(!id.is_string()&&!id.is_number_integer())throw std::invalid_argument("id must be a string or integer");
        return {{"id",id},{"ok",true},{"data",engine.execute(request.at("command").get<std::string>(),request.value("args",Json::object()))}};
    }catch(const std::invalid_argument& e){return {{"id",id},{"ok",false},{"error",{{"code","invalid-argument"},{"message",e.what()}}}};}
    catch(const Json::exception& e){return {{"id",id},{"ok",false},{"error",{{"code","invalid-json"},{"message",e.what()}}}};}
    catch(const std::exception& e){return {{"id",id},{"ok",false},{"error",{{"code","audio-error"},{"message",e.what()}}}};}
}
int probe(const char* models,const char* runtime){
    try{
        DtlnNpu model;model.open(models,runtime);std::array<float,128>mic{},ref{},out{};double peak=0;
        const auto start=std::chrono::steady_clock::now();
        for(unsigned hop=0;hop<125;++hop){for(unsigned i=0;i<128;++i){ref[i]=.01f*std::sin((hop*128+i)*.04f);mic[i]=.5f*ref[i];}model.process(mic.data(),ref.data(),out.data());for(float value:out){if(!std::isfinite(value))throw std::runtime_error("NPU produced nonfinite samples");peak=std::max(peak,static_cast<double>(std::abs(value)));}}
        std::cout<<Json({{"ok",true},{"device","NPU"},{"inferences",250},{"hops",125},{"audioSeconds",1.0},{"elapsedSeconds",std::chrono::duration<double>(std::chrono::steady_clock::now()-start).count()},{"outputPeak",peak},{"qualityValidated",false}}).dump()<<'\n';return 0;
    }catch(const std::exception& e){std::cout<<Json({{"ok",false},{"device","NPU"},{"error",e.what()}}).dump()<<'\n';return 1;}
}
}
int main(int argc,char** argv){
    if(argc==4&&std::string(argv[1])=="--probe-npu")return probe(argv[2],argv[3]);
    if(argc==2&&std::string(argv[1])=="--help"){std::cout<<"PersonalToolsAudio [--socket ABSOLUTE_PATH]\nPersonalToolsAudio --probe-npu MODEL_DIRECTORY OPENVINO_C_LIBRARY\n";return 0;}
    try{
        const auto* runtime=std::getenv("XDG_RUNTIME_DIR");if(!runtime)throw std::runtime_error("XDG_RUNTIME_DIR is required");
        std::filesystem::path path=std::filesystem::path(runtime)/"personal-tools/audio.sock";
        if(argc==3&&std::string(argv[1])=="--socket")path=argv[2];else if(argc!=1)throw std::invalid_argument("Invalid arguments; use --help");
        if(!path.is_absolute())throw std::invalid_argument("Socket path must be absolute");
        // Public PipeWire endpoint names must have one owner even when a caller
        // supplies a different control socket. Private test servers use their
        // own XDG_RUNTIME_DIR and therefore an independent lock.
        EngineLock ownership(runtime);
        Socket server;server.open(path);Engine engine;
        signal(SIGINT,interrupt);signal(SIGTERM,interrupt);signal(SIGPIPE,SIG_IGN);
        struct Client{int fd;std::string in,out;std::uint64_t last;};std::vector<Client>clients;
        const auto milliseconds=[](){return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());};
        while(!interrupted&&!engine.quitRequested()){
            // A disconnected PipeWire core cannot be reused. The user service
            // restarts us, then replays settings once the new socket is ready.
            if(!engine.connected())throw std::runtime_error("PipeWire disconnected; audio engine requires restart");
            engine.reconcile();std::vector<pollfd>fds{{server.fd,POLLIN,0}};
            for(auto& c:clients)fds.push_back({c.fd,static_cast<short>(POLLIN|(c.out.empty()?0:POLLOUT)),0});
            const auto result=poll(fds.data(),fds.size(),100);if(result<0){if(errno==EINTR)continue;throw std::runtime_error("Control socket poll failed");}
            if(fds[0].revents&POLLIN){
                const int fd=accept4(server.fd,nullptr,nullptr,SOCK_CLOEXEC|SOCK_NONBLOCK);
                if(fd>=0){ucred credential{};socklen_t length=sizeof(credential);
                    if(clients.size()<8&&getsockopt(fd,SOL_SOCKET,SO_PEERCRED,&credential,&length)==0&&credential.uid==getuid())clients.push_back({fd,{},{},milliseconds()});else close(fd);}
            }
            for(std::size_t index=fds.size()-1;index>0;--index){
                auto& c=clients[index-1];bool closed=fds[index].revents&(POLLERR|POLLNVAL);
                if(fds[index].revents&POLLIN){char buffer[8192];const auto count=recv(c.fd,buffer,sizeof(buffer),0);if(count<=0){if(count==0||errno!=EAGAIN)closed=true;}else{c.last=milliseconds();c.in.append(buffer,count);}}
                if(c.in.size()>1024*1024){closed=true;}else{
                    std::size_t newline;unsigned requests=0;
                    while(!closed&&requests++<16&&(newline=c.in.find('\n'))!=std::string::npos){c.out+=response(engine,c.in.substr(0,newline)).dump()+"\n";c.in.erase(0,newline+1);if(c.out.size()>2*1024*1024)closed=true;}
                }
                if(!closed&&!c.out.empty()){const auto written=send(c.fd,c.out.data(),c.out.size(),MSG_NOSIGNAL);if(written>0)c.out.erase(0,written);else if(errno!=EAGAIN)closed=true;}
                if(milliseconds()-c.last>30000||fds[index].revents&POLLHUP)closed=true;
                if(closed){close(c.fd);clients.erase(clients.begin()+index-1);}
            }
        }
        if(!engine.quitRequested())engine.execute("shutdown",Json::object());
        for(auto& c:clients)close(c.fd);
        return 0;
    }catch(const std::exception& e){std::cerr<<"PersonalToolsAudio: "<<e.what()<<'\n';return 1;}
}
