// Portable HF audio channel derived from ARSFI IONOS / Watterson-style principles.
// IONOS Gaussian FIR coefficients in IonosGaussianTaps.h are MIT licensed.
// This implementation does not use the Teensy Audio graph or hardware libraries.
#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <random>
#include <vector>
#include <limits>

namespace fakeradio::ionos {

enum class Mode : int { WGN=0, MPG=1, MPM=2, MPP=3, MPD=4 };

struct Settings {
    int sample_rate = 48000;
    Mode mode = Mode::MPM;
    int paths = 2;
    double snr_db = 25.0;
    uint32_t seed = 0x494f4e4f;
    double offset_hz = 0.0;
};

class Channel final {
public:
    explicit Channel(Settings settings);
    Channel(const Channel&) = delete;
    Channel& operator=(const Channel&) = delete;
    void process(const float* input, float* output, size_t samples);
    Settings settings() const { return config_; }
    static double spread_hz(Mode mode);
    static double delay_ms(Mode mode);
private:
    struct Path {
        std::array<double,128> hist_i{};
        std::array<double,128> hist_q{};
        std::array<double,2> current{};
        std::array<double,2> future{};
        int head = 0;
        int delay = 0;
    };
    Settings config_;
    std::mt19937 rng_;
    std::normal_distribution<double> normal_{0.0,1.0};
    std::array<Path,4> paths_{};
    std::array<double,129> hilbert_taps_{};
    std::array<double,129> analytic_input_{};
    int analytic_head_ = 0;
    std::vector<double> delayed_i_;
    std::vector<double> delayed_q_;
    size_t ring_pos_ = 0;
    double coeff_phase_ = 0.0;
    double coeff_step_ = 0.0;
    double coeff_normalizer_ = 1.0;
    double signal_power_ = 0.0;
    double power_alpha_ = 0.0;
    double tone_phase_ = 0.0;
    double tone_step_ = 0.0;
    void next_coefficients(Path& path, std::array<double,2>& result);
    void tick_coefficients();
};

// The C ABI is intended for managed FakeRadio and other modem labs.
extern "C" {
#if defined(_WIN32)
#define IONOS_EXPORT __declspec(dllexport)
#else
#define IONOS_EXPORT __attribute__((visibility("default")))
#endif
IONOS_EXPORT void* ionos_create(int sample_rate, int mode, int paths, double snr_db,
                                uint32_t seed, double offset_hz);
IONOS_EXPORT int ionos_process(void* handle, const float* input, float* output, int samples);
IONOS_EXPORT void ionos_destroy(void* handle);
IONOS_EXPORT int ionos_abi_version();
}

} // namespace fakeradio::ionos
