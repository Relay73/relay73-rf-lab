#include "IonosChannel.h"
#include "IonosGaussianTaps.h"
#include <algorithm>
#include <cmath>
#include <memory>
#include <stdexcept>

namespace fakeradio::ionos {
namespace {
constexpr double pi = 3.141592653589793238462643383279502884;
constexpr int center = 64;
constexpr int taps = 129;
double norm(const std::array<double,128>& coeff) {
    double power = 0.0;
    for (double value : coeff) power += value * value;
    return std::sqrt(power);
}
}

double Channel::spread_hz(Mode mode) {
    switch(mode) {
        case Mode::WGN: return 0.0;
        case Mode::MPG: return 0.1;
        case Mode::MPM: return 0.5;
        case Mode::MPP: return 1.0;
        case Mode::MPD: return 2.0;
    }
    throw std::invalid_argument("Unknown channel mode");
}

double Channel::delay_ms(Mode mode) {
    switch(mode) {
        case Mode::WGN: return 0.0;
        case Mode::MPG: return 0.5;
        case Mode::MPM: return 1.0;
        case Mode::MPP: return 2.0;
        case Mode::MPD: return 4.0;
    }
    throw std::invalid_argument("Unknown channel mode");
}

Channel::Channel(Settings settings) : config_(settings), rng_(settings.seed) {
    if (config_.sample_rate < 8000 || config_.sample_rate > 192000 ||
        (config_.paths != 2 && config_.paths != 4) ||
        !std::isfinite(config_.offset_hz) || std::abs(config_.offset_hz) > 200.0 ||
        (!std::isinf(config_.snr_db) && (!std::isfinite(config_.snr_db) ||
          config_.snr_db < -40.0 || config_.snr_db > 120.0)))
        throw std::invalid_argument("Invalid IONOS channel settings");
    const double spread = spread_hz(config_.mode);
    const double delay = delay_ms(config_.mode);
    coeff_step_ = 64.0 * spread / config_.sample_rate;
    power_alpha_ = 1.0 - std::exp(-1.0 / (0.05 * config_.sample_rate));
    tone_step_ = 2.0 * pi * config_.offset_hz / config_.sample_rate;
    const double tapNorm = norm(kIonosGaussianTaps);
    if (!(tapNorm > 0.0)) throw std::invalid_argument("Gaussian FIR taps are invalid");
    coeff_normalizer_ = 1.0 / (std::sqrt(2.0) * tapNorm);
    // Windowed Hilbert transform: quadrature audio with 64-sample group delay.
    // The I path is delayed by the same amount to keep the phases aligned.
    for (int i = 0; i < taps; ++i) {
        const int n = i - center;
        if (n != 0 && (std::abs(n) % 2 == 1)) {
            const double window = 0.54 - 0.46 * std::cos(2.0*pi*i/(taps-1));
            hilbert_taps_[i] = 2.0/(pi*n) * window;
        }
    }
    const double nominalSpacing = config_.paths == 2 ? delay : (delay / 2.0);
    int maxDelay = 0;
    for (int p = 0; p < config_.paths; ++p) {
        // IONOS two-path profiles use 0 and D ms. Four-path profiles
        // use 0, D/2, D, 3D/2 ms, including the quarter-ms MPG steps.
        paths_[p].delay = (int)std::lround(p*nominalSpacing*config_.sample_rate/1000.0);
        maxDelay = std::max(maxDelay, paths_[p].delay);
    }
    delayed_i_.assign(maxDelay+1,0.0);
    delayed_q_.assign(maxDelay+1,0.0);
    if (spread > 0) {
        // Warm the Gaussian filter histories so the fading process begins in
        // statistical steady state rather than ramping up from zero.
        for (int tick = 0; tick < 512; ++tick)
            for (int p = 0; p < config_.paths; ++p)
                next_coefficients(paths_[p], paths_[p].current);
        for (int p = 0; p < config_.paths; ++p)
            next_coefficients(paths_[p], paths_[p].future);
    }
}

void Channel::next_coefficients(Path& path, std::array<double,2>& result) {
    // IONOS uses a 128-tap Gaussian Doppler-shaping FIR at 64x spread.
    // Here the input random stream is std::normal_distribution rather
    // than the Teensy random() / Box-Muller quantization, so output is
    // statistically comparable, not bit-identical to Teensy hardware.
    path.head = (path.head + 1) & 127;
    path.hist_i[path.head] = normal_(rng_);
    path.hist_q[path.head] = normal_(rng_);
    double i = 0.0, q = 0.0;
    for (int k=0; k<128; ++k) {
        const int ix = (path.head - k) & 127;
        i += kIonosGaussianTaps[k] * path.hist_i[ix];
        q += kIonosGaussianTaps[k] * path.hist_q[ix];
    }
    result = {i * coeff_normalizer_, q * coeff_normalizer_};
}

void Channel::tick_coefficients() {
    if (coeff_step_ <= 0.0) return;
    if (coeff_phase_ >= 1.0) {
        coeff_phase_ -= 1.0;
        for (int p=0; p<config_.paths; ++p) {
            paths_[p].current = paths_[p].future;
            next_coefficients(paths_[p], paths_[p].future);
        }
    }
}

void Channel::process(const float* input, float* output, size_t samples) {
    if ((!input || !output) && samples) throw std::invalid_argument("Null audio buffer");
    const bool fading = config_.mode != Mode::WGN;
    const bool noise = !std::isinf(config_.snr_db);
    const double noiseGain = noise ? std::pow(10.0, -config_.snr_db / 20.0) : 0.0;
    const double pathGain = 1.0 / std::sqrt((double)config_.paths);
    for (size_t j=0; j<samples; ++j) {
        const double x = input[j];
        // Apply constant-smoothing SNR reference before fading. As with IONOS
        // hardware, calibration is defined over the input, not the faded output.
        signal_power_ += power_alpha_ * (x*x - signal_power_);
        double y = x;
        if (fading || config_.offset_hz != 0.0) {
            analytic_input_[analytic_head_] = x;
            double q = 0.0;
            for (int k=0; k<taps; ++k) {
                const int ix = (analytic_head_-k+taps)%taps;
                q += hilbert_taps_[k]*analytic_input_[ix];
            }
            const double i = analytic_input_[(analytic_head_-center+taps)%taps];
            analytic_head_ = (analytic_head_ + 1)%taps;
            if (fading) {
                tick_coefficients();
                delayed_i_[ring_pos_] = i;
                delayed_q_[ring_pos_] = q;
                y = 0.0;
                const double cosOffset = std::cos(tone_phase_);
                const double sinOffset = std::sin(tone_phase_);
                for (int p=0; p<config_.paths; ++p) {
                    const auto& path = paths_[p];
                    const size_t index = (ring_pos_+delayed_i_.size()-path.delay)%delayed_i_.size();
                    const double pdI=delayed_i_[index], pdQ=delayed_q_[index];
                    const double hI = path.current[0] + coeff_phase_ * (path.future[0]-path.current[0]);
                    const double hQ = path.current[1] + coeff_phase_ * (path.future[1]-path.current[1]);
                    // The real part of the complex fading coefficient times analytic audio.
                    const double u = hI*pdI - hQ*pdQ;
                    const double v = hI*pdQ + hQ*pdI;
                    y += (u*cosOffset - v*sinOffset)*pathGain;
                }
                ring_pos_ = (ring_pos_+1)%delayed_i_.size();
                coeff_phase_ += coeff_step_;
            } else {
                y = i*std::cos(tone_phase_)-q*std::sin(tone_phase_);
            }
            tone_phase_ += tone_step_;
            if (tone_phase_ > pi || tone_phase_ < -pi)
                tone_phase_ = std::remainder(tone_phase_,2.0*pi);
        }
        if (noise)
            y += std::sqrt(std::max(0.0,signal_power_)) * noiseGain * normal_(rng_);
        // Saturation follows the floating PCM link's normal limits.
        output[j] = (float)std::clamp(y, -1.0, 1.0);
    }
}

extern "C" {
void* ionos_create(int sample_rate, int mode, int paths, double snr_db,
                   uint32_t seed, double offset_hz) {
    try {
        if (mode < 0 || mode > 4) return nullptr;
        return new Channel(Settings{sample_rate, (Mode)mode, paths, snr_db, seed, offset_hz});
    } catch (...) { return nullptr; }
}
int ionos_process(void* handle, const float* input, float* output, int samples) {
    if (!handle || samples < 0 || (samples>0 && (!input || !output))) return -1;
    try {
        reinterpret_cast<Channel*>(handle)->process(input,output,(size_t)samples);
        return 0;
    } catch (...) { return -1; }
}
void ionos_destroy(void* handle) { delete reinterpret_cast<Channel*>(handle); }
int ionos_abi_version() { return 1; }
}

} // namespace fakeradio::ionos
