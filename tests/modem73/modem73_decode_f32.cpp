// Real pinned Modem73 RX test for FakeRadio FM-RF simulations.
// No radio, PTT, modem service, KISS socket or network access.
// Include the native Relay73 RX adapter once, with its pinned upstream PHY.
#include "relay73_modem73_rx.cpp"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
struct Received {
    std::vector<uint8_t> bytes;
    CqdM73FrameInfo info {};
};
void on_frame(void *ctx, const uint8_t* bytes, size_t size, const CqdM73FrameInfo* info)
{
    if(!ctx || !bytes || !info || info->struct_size!=sizeof(CqdM73FrameInfo))
        throw std::runtime_error("Modem73 RX callback invalid");
    static_cast<std::vector<Received>*>(ctx)->push_back({
        std::vector<uint8_t>(bytes, bytes+size), *info
    });
}
}
int main(int argc,char** argv)
{
    try {
        if(argc!=2) throw std::invalid_argument("usage: modem73-decode-f32 waveform.f32");
        std::ifstream file(argv[1],std::ios::binary|std::ios::ate);
        if(!file) throw std::runtime_error("Could not open real Modem73 test audio");
        auto length=file.tellg();
        if(length<0 || (length % static_cast<std::streamoff>(sizeof(float)))!=0 ||
            length>48000*60*(std::streamoff)sizeof(float))
            throw std::invalid_argument("Invalid length");
        file.seekg(0,std::ios::beg);
        std::vector<float> pcm((size_t)length/sizeof(float));
        file.read(reinterpret_cast<char*>(pcm.data()), length);
        if(!file) throw std::runtime_error("Cannot read entire PCM fixture");
        for(float value:pcm)
            if(!std::isfinite(value)) throw std::runtime_error("Invalid nonfinite PCM sample");
        std::vector<Received> decoded;
        CqdM73Options opts{sizeof(CqdM73Options),48000,1500,CQD_M73_FAMILY_OFDM};
        CqdM73Rx* rx=cqd_m73_rx_create(&opts,on_frame,&decoded);
        if(!rx)throw std::runtime_error("Could not create real Modem73 RX");
        const std::vector<float> drain(48000,0.f);
        try {
            for(size_t at=0;at<pcm.size();at+=240) {
                size_t n=std::min<size_t>(240, pcm.size()-at);
                if(cqd_m73_rx_feed(rx,pcm.data()+at,n)!=CQD_M73_OK)
                    throw std::runtime_error("RX failed to consume FM audio");
            }
            if(cqd_m73_rx_feed(rx,drain.data(),drain.size())!=CQD_M73_OK)
                throw std::runtime_error("RX failed final drain");
        }catch(...){cqd_m73_rx_destroy(rx);throw;}
        size_t count=cqd_m73_rx_frames(rx);
        cqd_m73_rx_destroy(rx);
        const std::string expected="Relay73 IONOS actual Modem73 TX test 2026";
        const bool match=decoded.size()==1 &&
            decoded[0].bytes==std::vector<uint8_t>(expected.begin(),expected.end()) &&
            decoded[0].info.mode_index==16;
        std::cout<<"FM_MODEM73_PHY_RESULT matched="<<(match?"YES":"NO")
                 <<" callbacks="<<decoded.size()<<" frames="<<count;
        if(decoded.size()) {
            std::cout<<" snr_db="<<std::fixed<<std::setprecision(2)<<decoded[0].info.snr_db
                     <<" ber_percent="<<std::fixed<<std::setprecision(3)<<decoded[0].info.ber_percent;
        }
        std::cout<<" source="<<argv[1]<<"\n";
        // NO means a genuine decode failure for the scenario, not tool failure.
        return match ? 0 : 10;
    }catch(const std::exception& e){
        std::cerr<<"FM_MODEM73_PHY_ERROR "<<e.what()<<"\n";return 2;
    }
}
