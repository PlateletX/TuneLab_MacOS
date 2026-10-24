#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

extern "C" {
struct TLAudioUnitDescription {
    uint32_t componentType;
    uint32_t subtype;
    uint32_t manufacturer;
    uint32_t version;
    char name[256];
};

struct TLAudioUnitParameter {
    uint32_t identifier;
    uint32_t scope;
    uint32_t element;
    float minimum;
    float maximum;
    float defaultValue;
    char name[256];
};

size_t tl_au_effect_count();
int tl_au_effect_get(size_t index, TLAudioUnitDescription *result);
void *tl_au_create(uint32_t subtype, uint32_t manufacturer, double sampleRate, uint32_t maximumFrames, int *error);
void tl_au_destroy(void *handle);
int tl_au_process(void *handle, const float *inputLeft, const float *inputRight, float *outputLeft, float *outputRight, uint32_t frameCount);
size_t tl_au_parameter_count(void *handle);
int tl_au_parameter_get(void *handle, size_t index, TLAudioUnitParameter *result);
}

static uint32_t FourCC(const char *text) {
    return (static_cast<uint32_t>(text[0]) << 24) |
           (static_cast<uint32_t>(text[1]) << 16) |
           (static_cast<uint32_t>(text[2]) << 8) |
           static_cast<uint32_t>(text[3]);
}

int main(int argc, char **argv) {
    const uint32_t subtype = argc > 1 ? FourCC(argv[1]) : FourCC("lpas");
    const uint32_t manufacturer = argc > 2 ? FourCC(argv[2]) : FourCC("appl");
    const size_t count = tl_au_effect_count();
    bool found = false;
    for (size_t i = 0; i < count; ++i) {
        TLAudioUnitDescription item{};
        if (tl_au_effect_get(i, &item) != 0)
            continue;
        if (item.subtype == subtype && item.manufacturer == manufacturer) {
            std::printf("Found %s\n", item.name);
            found = true;
        }
    }
    if (!found) {
        std::fprintf(stderr, "Requested Audio Unit was not found\n");
        return 1;
    }

    constexpr uint32_t frames = 4096;
    constexpr double sampleRate = 44100.0;
    std::vector<float> inputLeft(frames), inputRight(frames), outputLeft(frames), outputRight(frames);
    for (uint32_t i = 0; i < frames; ++i) {
        const float sample = std::sin(static_cast<float>(i * 2.0 * 3.141592653589793 * 440.0 / sampleRate)) * 0.1f;
        inputLeft[i] = sample;
        inputRight[i] = sample;
    }

    int error = 0;
    void *unit = tl_au_create(subtype, manufacturer, sampleRate, frames, &error);
    if (unit == nullptr) {
        std::fprintf(stderr, "AudioUnit creation failed: %d\n", error);
        return 1;
    }

    const size_t parameterCount = tl_au_parameter_count(unit);
    TLAudioUnitParameter parameter{};
    if (parameterCount == 0 || tl_au_parameter_get(unit, 0, &parameter) != 0 || parameter.name[0] == '\0') {
        tl_au_destroy(unit);
        std::fprintf(stderr, "AudioUnit parameter enumeration failed\n");
        return 1;
    }

    const int status = tl_au_process(unit, inputLeft.data(), inputRight.data(), outputLeft.data(), outputRight.data(), frames);
    tl_au_destroy(unit);
    if (status != 0) {
        std::fprintf(stderr, "AudioUnit render failed: %d\n", status);
        return 1;
    }

    float peak = 0;
    for (uint32_t i = 0; i < frames; ++i) {
        if (!std::isfinite(outputLeft[i]) || !std::isfinite(outputRight[i])) {
            std::fprintf(stderr, "AudioUnit returned non-finite samples\n");
            return 1;
        }
        peak = std::max(peak, std::max(std::abs(outputLeft[i]), std::abs(outputRight[i])));
    }
    if (peak == 0) {
        std::fprintf(stderr, "AudioUnit returned silence for a non-silent input\n");
        return 1;
    }
    std::printf("Rendered %u stereo frames through AU; parameters=%zu first=%s peak=%f\n",
                frames,
                parameterCount,
                parameter.name,
                peak);
    return 0;
}
