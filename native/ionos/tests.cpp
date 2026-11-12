#include "IonosChannel.h"
#include <algorithm>
#include <cmath>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <vector>
using fakeradio::ionos::Channel;
using fakeradio::ionos::Mode;
using fakeradio::ionos::Settings;
namespace {
constexpr double pi=3.14159265358979323846;
void require(bool ok,const char* message) {
    if(!ok)throw std::runtime_error(message);
}
std::vector<float> sine(int n,int sr=48000) {
    std::vector<float> wave(n);
    for(int i=0;i<n;++i)wave[i]=0.17f*(float)std::sin(2*pi*1100*i/sr);
    return wave;
}
void test_clean() {
    auto wave=sine(24000);
    Channel c({48000,Mode::WGN,2,std::numeric_limits<double>::infinity(),2026,0});
    std::vector<float> out(wave.size());
    c.process(wave.data(),out.data(),out.size());
    require(out==wave,"WGN no-noise must preserve input exactly");
}
void test_seed_and_chunks() {
    auto wave=sine(48000);
    Settings cfg{48000,Mode::MPM,2,12.0,222,0.0};
    Channel once(cfg),chunks(cfg),different({48000,Mode::MPM,2,12.0,223,0.0});
    std::vector<float> a(wave.size()), b(wave.size()), d(wave.size());
    once.process(wave.data(),a.data(),a.size());
    for(size_t i=0;i<wave.size();i+=240)
        chunks.process(wave.data()+i,b.data()+i,std::min<size_t>(240,wave.size()-i));
    different.process(wave.data(),d.data(),d.size());
    require(a==b,"DSP state changed across packet boundaries");
    require(a!=d,"Changing seed did not affect output");
    require(std::all_of(a.begin(),a.end(),[](float v){return std::isfinite(v)&&v<=1.0f&&v>=-1.0f;}),
        "Clipping/range check failed");
}
void test_gaussian_delays_and_spread() {
    require(Channel::spread_hz(Mode::MPG)==0.1&&Channel::delay_ms(Mode::MPG)==0.5,
        "MPG profile mismatch");
    require(Channel::spread_hz(Mode::MPM)==0.5&&Channel::delay_ms(Mode::MPM)==1.0,
        "MPM profile mismatch");
    require(Channel::spread_hz(Mode::MPP)==1.0&&Channel::delay_ms(Mode::MPP)==2.0,
        "MPP profile mismatch");
    require(Channel::spread_hz(Mode::MPD)==2.0&&Channel::delay_ms(Mode::MPD)==4.0,
        "MPD profile mismatch");
    auto wave=sine(96000);
    for(auto mode:{Mode::MPG,Mode::MPM,Mode::MPP,Mode::MPD}) {
        for(int paths:{2,4}) {
            Channel c({48000,mode,paths,std::numeric_limits<double>::infinity(),1,0});
            std::vector<float> y(wave.size());
            c.process(wave.data(),y.data(),wave.size());
            require(std::any_of(y.begin()+500,y.end(),[](float v){return std::abs(v)>0.0001f;}),
                "Multipath profile produced no usable output");
        }
    }
}
void test_noise_calibration() {
    const int sr=48000;
    const auto wave=sine(sr*4);
    Settings cfg{sr,Mode::WGN,2,12.0,42,0};
    Channel c(cfg);
    std::vector<float> out(wave.size());
    c.process(wave.data(),out.data(),wave.size());
    double signal=0.0,error=0.0;
    for(size_t i=sr;i<wave.size();++i) {
        signal+=wave[i]*wave[i];
        const double e=out[i]-wave[i];
        error+=e*e;
    }
    const double measured=10*std::log10(signal/error);
    std::cout<<"WGN_METER target_db=12 measured_db="<<measured<<"\n";
    require(std::abs(measured-12.0)<0.5,"WGN SNR calibration exceeded 0.5 dB");
}
void test_invalid_abi() {
    using namespace fakeradio::ionos;
    require(ionos_abi_version()==1,"ABI version mismatch");
    require(ionos_create(48000,8,2,20,1,0)==nullptr,"Invalid mode accepted");
    require(ionos_create(48000,2,3,20,1,0)==nullptr,"Invalid paths accepted");
    require(ionos_create(48000,2,2,20,1,300)==nullptr,"Invalid offset accepted");
    auto handle=ionos_create(48000,2,2,20,1,0);
    require(handle!=nullptr,"C ABI create failed");
    float in[3]{0.0f,0.1f,0.2f},out[3]{};
    require(ionos_process(handle,in,out,3)==0,"C ABI process failed");
    require(ionos_process(handle,in,out,-1)==-1,"C ABI should reject negative length");
    ionos_destroy(handle);
}
}
int main() {
    try {
        test_clean();
        test_seed_and_chunks();
        test_gaussian_delays_and_spread();
        test_noise_calibration();
        test_invalid_abi();
        std::cout<<"IONOS_CHANNEL_TEST_PASS 5/5\n";
        return 0;
    }catch(const std::exception& e){
        std::cerr<<"IONOS_CHANNEL_TEST_FAIL "<<e.what()<<"\n";
        return 1;
    }
}
