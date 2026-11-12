#include "IonosChannel.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

using fakeradio::ionos::Channel;
using fakeradio::ionos::Mode;
using fakeradio::ionos::Settings;
namespace {
uint16_t u16(std::istream& in) {
    unsigned char b[2]{};
    if (!in.read((char*)b, 2)) throw std::runtime_error("Truncated WAV");
    return (uint16_t)(b[0] | (uint16_t(b[1]) << 8));
}
uint32_t u32(std::istream& in) {
    return uint32_t(u16(in)) | (uint32_t(u16(in))<<16);
}
void w16(std::ostream& out, uint16_t value) {
    out.put(char(value & 255)); out.put(char((value>>8) & 255));
}
void w32(std::ostream& out, uint32_t value) {
    w16(out, uint16_t(value)); w16(out,uint16_t(value>>16));
}
void wavheader(std::ostream& out, uint32_t samples, int sampleRate) {
    out.write("RIFF",4); w32(out, 36 + 2*samples);
    out.write("WAVEfmt ",8); w32(out,16);
    w16(out,1); w16(out,1);
    w32(out,(uint32_t)sampleRate); w32(out,(uint32_t)sampleRate*2);
    w16(out,2); w16(out,16);
    out.write("data",4); w32(out,2*samples);
}
struct WavInfo { int sample_rate=0; uint32_t bytes=0; std::streampos offset{}; };
WavInfo readheader(std::ifstream& in) {
    char tag[4]{};
    if(!in.read(tag,4) || std::memcmp(tag,"RIFF",4)) throw std::runtime_error("Input is not RIFF");
    (void)u32(in);
    if(!in.read(tag,4) || std::memcmp(tag,"WAVE",4)) throw std::runtime_error("Input is not WAV");
    WavInfo info;
    bool format=false, data=false;
    while(in.read(tag,4)) {
        const uint32_t length=u32(in);
        const auto chunkPos = in.tellg();
        if (std::memcmp(tag,"fmt ",4)==0) {
            if(length<16) throw std::runtime_error("Invalid fmt chunk");
            const uint16_t codec=u16(in), channels=u16(in);
            const int sr=(int)u32(in);
            (void)u32(in); (void)u16(in);
            const uint16_t bits=u16(in);
            if(codec!=1 || channels!=1 || bits!=16)
                throw std::runtime_error("Only 16-bit PCM mono WAV is supported");
            info.sample_rate=sr; format=true;
        } else if(std::memcmp(tag,"data",4)==0) {
            info.offset=chunkPos; info.bytes=length; data=true;
        }
        in.seekg(chunkPos+(std::streamoff)(length+(length&1)),std::ios::beg);
        if(!in.good()) throw std::runtime_error("Truncated WAV chunk");
        if(format&&data)break;
    }
    if(!format||!data||info.bytes%2 || info.sample_rate<8000||info.sample_rate>192000)
        throw std::runtime_error("Missing or invalid mono WAV data");
    in.clear();in.seekg(info.offset,std::ios::beg);
    return info;
}
void help() {
    std::cout << "Portable IONOS-derived HF channel WAV processor (MIT attribution included)\n"
              << "Usage: ionos-wav --input source.wav --output processed.wav "
              << "[--mode WGN|MPG|MPM|MPP|MPD|CLEAN] [--paths 2|4] "
              << "[--snr-db 12|OFF] [--seed 12345] [--offset-hz 0]\n"
              << "Only PCM16 mono WAV; sample rate taken from source. "
              << "Presets: MPG 0.1 Hz/0.5ms, MPM 0.5Hz/1ms, MPP 1Hz/2ms, MPD 2Hz/4ms.\n";
}
Mode parse_mode(const std::string& s) {
    if(s=="CLEAN" || s=="WGN")return Mode::WGN;
    if(s=="MPG")return Mode::MPG;
    if(s=="MPM")return Mode::MPM;
    if(s=="MPP")return Mode::MPP;
    if(s=="MPD")return Mode::MPD;
    throw std::invalid_argument("Unknown mode: "+s);
}
} // namespace

int main(int argc,char** argv) {
    try {
        std::string source,destination,modeString="MPM";
        int paths=2;
        double snr_db=25.0,offset_hz=0.0;
        uint32_t seed=0x494f4e4f;
        for(int i=1;i<argc;++i) {
            const std::string arg=argv[i];
            if(arg=="--help"||arg=="-h"){help();return 0;}
            if(i+1>=argc)throw std::invalid_argument("Missing value for "+arg);
            const std::string value=argv[++i];
            if(arg=="--input")source=value;
            else if(arg=="--output")destination=value;
            else if(arg=="--mode")modeString=value;
            else if(arg=="--paths")paths=std::stoi(value);
            else if(arg=="--snr-db")snr_db=value=="OFF"?std::numeric_limits<double>::infinity():std::stod(value);
            else if(arg=="--seed"){
                const unsigned long long parsed=std::stoull(value);
                if(parsed>UINT32_MAX)throw std::invalid_argument("Seed out of range");
                seed=(uint32_t)parsed;
            }
            else if(arg=="--offset-hz")offset_hz=std::stod(value);
            else throw std::invalid_argument("Unknown option "+arg);
        }
        if(source.empty()||destination.empty()||source==destination)
            throw std::invalid_argument("Distinct --input and --output paths are required");
        std::ifstream input(source,std::ios::binary);
        if(!input)throw std::runtime_error("Cannot open input WAV");
        const auto meta=readheader(input);
        Settings settings{meta.sample_rate,parse_mode(modeString),paths,snr_db,seed,offset_hz};
        if(modeString=="CLEAN"){settings.snr_db=std::numeric_limits<double>::infinity();}
        Channel channel(settings);
        std::ofstream output(destination,std::ios::binary|std::ios::trunc);
        if(!output)throw std::runtime_error("Cannot open output WAV");
        wavheader(output,meta.bytes/2,meta.sample_rate);
        constexpr size_t chunk=240;
        std::vector<unsigned char> bytes(chunk*2);
        std::vector<float> inFloat(chunk),outFloat(chunk);
        uint32_t remaining=meta.bytes;
        while(remaining) {
            const size_t block=std::min<size_t>(remaining/2,chunk);
            if(!input.read((char*)bytes.data(),(std::streamsize)(block*2)))
                throw std::runtime_error("WAV data truncated");
            for(size_t j=0;j<block;++j) {
                const uint16_t sample=(uint16_t)(bytes[2*j]|(uint16_t(bytes[2*j+1])<<8));
                inFloat[j]=(float)(int16_t)sample/32768.0f;
            }
            channel.process(inFloat.data(),outFloat.data(),block);
            for(size_t j=0;j<block;++j) {
                const auto sample=(int16_t)std::lround(
                    std::clamp((double)outFloat[j], -1.0, 32767.0/32768.0)*32768.0);
                bytes[2*j]=(unsigned char)(sample&255);
                bytes[2*j+1]=(unsigned char)((uint16_t)sample>>8);
            }
            output.write((char*)bytes.data(),(std::streamsize)(block*2));
            remaining-=(uint32_t)(block*2);
        }
        if(!output)throw std::runtime_error("Failed while writing WAV");
        std::cout<<"IONOS_WAV_PASS mode="<<modeString<<" paths="<<paths<<" snr="<<settings.snr_db
                 <<" seed="<<seed<<" rate="<<meta.sample_rate
                 <<" samples="<<(meta.bytes/2)<<" output="<<destination<<"\n";
        return 0;
    }catch(const std::exception& ex){
        std::cerr<<"IONOS_WAV_FAIL "<<ex.what()<<"\n";
        return 1;
    }
}
