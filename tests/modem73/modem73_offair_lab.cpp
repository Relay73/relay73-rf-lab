// Offline Modem73 TX -> IONOS-derived channel -> Modem73 RX laboratory.
// Upstream DSP is pinned and unchanged in the Relay73 checkout.
// NO audio devices, sockets, rig control, PTT, RMS gateway, or over-the-air access.
#include "relay73_modem73_rx.cpp" // upstream includes require a single TU for the Modem73 receiver
#include "IonosChannel.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

using fakeradio::ionos::Channel;
using fakeradio::ionos::Mode;
using fakeradio::ionos::Settings;

namespace {
struct Reception {
    std::vector<uint8_t> bytes;
    CqdM73FrameInfo info{};
};
void received(void* ctx,const uint8_t* data,size_t size,const CqdM73FrameInfo* info) {
    if (!ctx || !data || !info || info->struct_size!=sizeof(CqdM73FrameInfo))
        throw std::runtime_error("Modem73 decoder returned invalid callback data");
    static_cast<std::vector<Reception>*>(ctx)->push_back({
        std::vector<uint8_t>(data,data+size),*info});
}
void write16(std::ostream& out,uint16_t v) {
    out.put(static_cast<char>(v & 0xff));out.put(static_cast<char>(v>>8));
}
void write32(std::ostream& out,uint32_t v) {
    write16(out,static_cast<uint16_t>(v));write16(out,static_cast<uint16_t>(v>>16));
}
void wav(const std::string& path,const std::vector<float>& x) {
    if (x.size()>10000000u)throw std::runtime_error("WAV exceeds bounded test limit");
    std::ofstream out(path,std::ios::binary|std::ios::trunc);
    if(!out)throw std::runtime_error("Unable to write "+path);
    out.write("RIFF",4);write32(out,36+(uint32_t)x.size()*2u);
    out.write("WAVEfmt ",8);write32(out,16);
    write16(out,1);write16(out,1);write32(out,48000);write32(out,96000);
    write16(out,2);write16(out,16);out.write("data",4);write32(out,(uint32_t)x.size()*2u);
    for(float v:x) {
        // Audio previews are reference TX waveform or actual impaired samples;
        // never normalize per scenario, so volume changes are comparable.
        const double limited=std::clamp((double)v,-1.0,32767.0/32768.0);
        const int16_t s=static_cast<int16_t>(std::lround(limited*32768.0));
        write16(out,static_cast<uint16_t>(s));
    }
    if(!out)throw std::runtime_error("Failed to write WAV");
}
struct Trial {
    std::string mode;
    int paths;
    double snr;
    uint32_t seed;
    double freq_offset;
};
Mode channelMode(const std::string& m) {
    if (m=="WGN")return Mode::WGN;
    if (m=="MPG")return Mode::MPG;
    if (m=="MPM")return Mode::MPM;
    if (m=="MPP")return Mode::MPP;
    if (m=="MPD")return Mode::MPD;
    throw std::runtime_error("Unknown mode");
}
void feed(CqdM73Rx* rx,const std::vector<float>& samples) {
    // Exercise 5 ms frame transport but do not assume packet boundary is
    // aligned with any Modem73 OFDM symbols or frame boundaries.
    constexpr size_t frame=240;
    for(size_t at=0;at<samples.size();at+=frame) {
        const size_t count=std::min(frame,samples.size()-at);
        if(cqd_m73_rx_feed(rx,samples.data()+at,count)!=CQD_M73_OK)
            throw std::runtime_error("Receiver rejected valid float PCM");
    }
}
void run(const std::vector<float>& original,const std::vector<uint8_t>& expected,
         int phy_mode,const Trial& t,const std::string& savePath) {
    std::vector<float> impaired(original.size());
    if(t.mode=="CLEAN") impaired=original;
    else {
        Channel channel(Settings{48000,channelMode(t.mode),t.paths,t.snr,t.seed,t.freq_offset});
        for(size_t at=0;at<original.size();at+=240) {
            const size_t n=std::min((size_t)240,original.size()-at);
            channel.process(original.data()+at,impaired.data()+at,n);
        }
    }
    std::vector<Reception> results;
    CqdM73Options opts{sizeof(CqdM73Options),48000,1500,CQD_M73_FAMILY_OFDM};
    CqdM73Rx* rx=cqd_m73_rx_create(&opts,received,&results);
    if(!rx)throw std::runtime_error("Cannot create real Modem73 RX");
    try {
        feed(rx,impaired);
        // Drain with silence, as in existing Relay73 native offline test.
        const std::vector<float> drain(48000,0.0f);
        feed(rx,drain);
    }catch(...){cqd_m73_rx_destroy(rx);throw;}
    const size_t frames=cqd_m73_rx_frames(rx);
    cqd_m73_rx_destroy(rx);
    const bool ok=results.size()==1 && results[0].bytes==expected &&
        results[0].info.mode_index==phy_mode && results[0].info.family==CQD_M73_FAMILY_OFDM;
    double rms=0.0;
    for(const float x:impaired)rms+=(double)x*x;
    rms=std::sqrt(rms/impaired.size());
    if(!savePath.empty())wav(savePath,impaired);
    std::cout<<t.mode<<","<<t.paths<<",";
    if(std::isinf(t.snr))std::cout<<"OFF";else std::cout<<std::fixed<<std::setprecision(1)<<t.snr;
    std::cout<<","<<t.seed<<","<<std::fixed<<std::setprecision(1)<<t.freq_offset<<","
             <<(ok?"PASS":"FAIL")<<","<<results.size()<<","<<frames<<","
             <<std::fixed<<std::setprecision(7)<<rms<<",";
    if(results.empty())std::cout<<"NA,NA";
    else std::cout<<std::fixed<<std::setprecision(2)<<results.front().info.snr_db
                  <<","<<results.front().info.ber_percent;
    std::cout<<"\n";
}
}
int main(int argc,char** argv) {
    try {
        // All output paths are explicit. Optional WAV prefix is used only for
        // one baseline seed to avoid writing large unrequested corpora.
        if(argc!=2)throw std::runtime_error("usage: modem73-ionos-offair OUTPUT_WAV_PREFIX");
        std::string prefix=argv[1];
        const std::string message="Relay73 IONOS actual Modem73 TX test 2026";
        const int mode=ModemConfig::encode_mode("QPSK","1/2",0);
        const int64_t station=ModemConfig::encode_callsign("N0CALL");
        if(mode!=16 || station<0)throw std::runtime_error("Pinned upstream Modem73 mode 16 unavailable");
        std::vector<uint8_t> phy{(uint8_t)(message.size()>>8),(uint8_t)message.size()};
        phy.insert(phy.end(),message.begin(),message.end());
        Encoder48k encoder;
        const auto pcm=encoder.encode(phy.data(),phy.size(),1500,station,mode);
        if(pcm.size()<24000 || pcm.size()>48000*10)
            throw std::runtime_error("Modem73 encoder returned implausible waveform length");
        std::cout<<"MOD73_REAL_WAVE=1 PHY_MODE="<<mode<<" ORIGINAL_SAMPLES="<<pcm.size()
                 <<" PAYLOAD_BYTES="<<message.size()<<" SEED_COUNT=3\n";
        std::cout<<"channel,paths,nominal_snr_db,seed,offset_hz,payload,result_frames,rx_frame_counter,output_rms,decoded_snr_db,decoded_ber_percent\n";
        std::vector<Trial> cases;
        cases.push_back({"CLEAN",2,std::numeric_limits<double>::infinity(),2026,0});
        for(const uint32_t seed:{2026u,2027u,2028u}) {
            for(const auto& m:{"WGN","MPG","MPM","MPP","MPD"}) {
                cases.push_back({m,2,15.0,seed,0.0});
            }
        }
        for(const auto& m:{"MPM","MPD"})
            cases.push_back({m,4,15.0,2026,0.0});
        for(const auto& m:{"WGN","MPM","MPD"})
            cases.push_back({m,2,5.0,2026,0.0});
        cases.push_back({"MPM",2,15.0,2026,20.0});
        for(const auto& t:cases) {
            const bool save=(t.mode=="CLEAN" || (t.seed==2026 && t.snr==15 && t.paths==2 && t.freq_offset==0) ||
                (t.mode=="MPD" && t.paths==4 && t.seed==2026) ||
                (t.mode=="MPM" && t.snr==5 && t.seed==2026));
            std::string filename="";
            if(save) {
                filename=prefix+t.mode+"_"+
                    ((t.mode=="CLEAN")?std::string("reference"):
                    (std::to_string(t.paths)+"path_"+std::to_string((int)t.snr)+"db"))+".wav";
            }
            // Every trial verifies original packet payload received by the
            // existing upstream-derived real Modem73 decoder.
            run(pcm,std::vector<uint8_t>(message.begin(),message.end()),mode,t,filename);
        }
        std::cout<<"MOD73_IONOS_TEST_COMPLETE cases="<<cases.size()
                 <<" channel_models=5 no_rf=1 no_pat=1 no_kiss=1\n";
        return 0;
    } catch(const std::exception& e){
        std::cerr<<"MOD73_IONOS_TEST_ERROR "<<e.what()<<"\n";return 1;
    }
}
