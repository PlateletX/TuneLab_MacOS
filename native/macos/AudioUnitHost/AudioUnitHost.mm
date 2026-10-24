#include <AudioToolbox/AudioToolbox.h>
#include <CoreFoundation/CoreFoundation.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <memory>
#include <new>
#include <vector>

namespace {

constexpr UInt32 kStereoChannels = 2;

struct AudioUnitInstance {
    AudioUnit unit = nullptr;
    const Float32 *inputLeft = nullptr;
    const Float32 *inputRight = nullptr;
    UInt32 sampleTime = 0;
    UInt32 maximumFrames = 0;
};

AudioStreamBasicDescription MakeStreamFormat(double sampleRate) {
    AudioStreamBasicDescription format{};
    format.mSampleRate = sampleRate;
    format.mFormatID = kAudioFormatLinearPCM;
    format.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved;
    format.mBytesPerPacket = sizeof(Float32);
    format.mFramesPerPacket = 1;
    format.mBytesPerFrame = sizeof(Float32);
    format.mChannelsPerFrame = kStereoChannels;
    format.mBitsPerChannel = sizeof(Float32) * 8;
    return format;
}

OSStatus RenderInput(void *refCon,
                     AudioUnitRenderActionFlags *,
                     const AudioTimeStamp *,
                     UInt32 busNumber,
                     UInt32 frameCount,
                     AudioBufferList *ioData) {
    if (busNumber != 0 || refCon == nullptr || ioData == nullptr || ioData->mNumberBuffers < kStereoChannels)
        return kAudio_ParamError;

    auto *instance = static_cast<AudioUnitInstance *>(refCon);
    ioData->mBuffers[0].mNumberChannels = 1;
    ioData->mBuffers[0].mDataByteSize = frameCount * sizeof(Float32);
    ioData->mBuffers[0].mData = const_cast<Float32 *>(instance->inputLeft);
    ioData->mBuffers[1].mNumberChannels = 1;
    ioData->mBuffers[1].mDataByteSize = frameCount * sizeof(Float32);
    ioData->mBuffers[1].mData = const_cast<Float32 *>(instance->inputRight);
    return noErr;
}

AudioComponent FindEffect(UInt32 componentType, UInt32 subtype, UInt32 manufacturer) {
    AudioComponentDescription description{};
    description.componentType = componentType;
    description.componentSubType = subtype;
    description.componentManufacturer = manufacturer;
    description.componentFlags = 0;
    description.componentFlagsMask = 0;
    return AudioComponentFindNext(nullptr, &description);
}

} // namespace

extern "C" {

struct TLAudioUnitDescription {
    UInt32 componentType;
    UInt32 subtype;
    UInt32 manufacturer;
    UInt32 version;
    char name[256];
};

struct TLAudioUnitParameter {
    AudioUnitParameterID identifier;
    AudioUnitScope scope;
    AudioUnitElement element;
    Float32 minimum;
    Float32 maximum;
    Float32 defaultValue;
    char name[256];
};

size_t tl_au_effect_count() {
    AudioComponentDescription description{};
    description.componentType = kAudioUnitType_Effect;
    AudioComponent component = nullptr;
    size_t count = 0;
    while ((component = AudioComponentFindNext(component, &description)) != nullptr)
        ++count;
    return count;
}

int tl_au_effect_get(size_t index, TLAudioUnitDescription *result) {
    if (result == nullptr)
        return kAudio_ParamError;

    AudioComponentDescription filter{};
    filter.componentType = kAudioUnitType_Effect;
    AudioComponent component = nullptr;
    for (size_t current = 0; (component = AudioComponentFindNext(component, &filter)) != nullptr; ++current) {
        if (current != index)
            continue;

        AudioComponentDescription description{};
        OSStatus status = AudioComponentGetDescription(component, &description);
        if (status != noErr)
            return status;

        result->componentType = description.componentType;
        result->subtype = description.componentSubType;
        result->manufacturer = description.componentManufacturer;
        status = AudioComponentGetVersion(component, &result->version);
        if (status != noErr)
            return status;
        CFStringRef name = nullptr;
        status = AudioComponentCopyName(component, &name);
        if (status != noErr)
            return status;
        const Boolean copied = CFStringGetCString(name, result->name, sizeof(result->name), kCFStringEncodingUTF8);
        CFRelease(name);
        if (!copied)
            result->name[0] = '\0';
        return noErr;
    }

    return kAudio_ParamError;
}

void *tl_au_create(UInt32 subtype, UInt32 manufacturer, double sampleRate, UInt32 maximumFrames, int *error) {
    if (error != nullptr)
        *error = noErr;
    if (sampleRate <= 0 || maximumFrames == 0) {
        if (error != nullptr)
            *error = kAudio_ParamError;
        return nullptr;
    }

    AudioComponent component = FindEffect(kAudioUnitType_Effect, subtype, manufacturer);
    if (component == nullptr) {
        if (error != nullptr)
            *error = kAudio_ParamError;
        return nullptr;
    }

    auto instance = std::unique_ptr<AudioUnitInstance>(new (std::nothrow) AudioUnitInstance());
    if (!instance) {
        if (error != nullptr)
            *error = memFullErr;
        return nullptr;
    }
    instance->maximumFrames = maximumFrames;

    OSStatus status = AudioComponentInstanceNew(component, &instance->unit);
    if (status == noErr) {
        UInt32 maximum = maximumFrames;
        status = AudioUnitSetProperty(instance->unit,
                                      kAudioUnitProperty_MaximumFramesPerSlice,
                                      kAudioUnitScope_Global,
                                      0,
                                      &maximum,
                                      sizeof(maximum));
    }
    if (status == noErr) {
        AudioStreamBasicDescription format = MakeStreamFormat(sampleRate);
        status = AudioUnitSetProperty(instance->unit,
                                      kAudioUnitProperty_StreamFormat,
                                      kAudioUnitScope_Input,
                                      0,
                                      &format,
                                      sizeof(format));
        if (status == noErr)
            status = AudioUnitSetProperty(instance->unit,
                                          kAudioUnitProperty_StreamFormat,
                                          kAudioUnitScope_Output,
                                          0,
                                          &format,
                                          sizeof(format));
    }
    if (status == noErr) {
        AURenderCallbackStruct callback{RenderInput, instance.get()};
        status = AudioUnitSetProperty(instance->unit,
                                      kAudioUnitProperty_SetRenderCallback,
                                      kAudioUnitScope_Input,
                                      0,
                                      &callback,
                                      sizeof(callback));
    }
    if (status == noErr)
        status = AudioUnitInitialize(instance->unit);
    if (status != noErr) {
        if (instance->unit != nullptr)
            AudioComponentInstanceDispose(instance->unit);
        if (error != nullptr)
            *error = status;
        return nullptr;
    }
    return instance.release();
}

void tl_au_destroy(void *handle) {
    auto *instance = static_cast<AudioUnitInstance *>(handle);
    if (instance == nullptr)
        return;
    AudioUnitUninitialize(instance->unit);
    AudioComponentInstanceDispose(instance->unit);
    delete instance;
}

int tl_au_process(void *handle,
                  const float *inputLeft,
                  const float *inputRight,
                  float *outputLeft,
                  float *outputRight,
                  UInt32 frameCount) {
    auto *instance = static_cast<AudioUnitInstance *>(handle);
    if (instance == nullptr || inputLeft == nullptr || inputRight == nullptr || outputLeft == nullptr || outputRight == nullptr || frameCount == 0 || frameCount > instance->maximumFrames)
        return kAudio_ParamError;

    instance->inputLeft = inputLeft;
    instance->inputRight = inputRight;

    struct StereoOutputBufferList {
        UInt32 numberBuffers;
        AudioBuffer buffers[kStereoChannels];
    } output{};
    output.numberBuffers = kStereoChannels;
    output.buffers[0].mNumberChannels = 1;
    output.buffers[0].mDataByteSize = frameCount * sizeof(Float32);
    output.buffers[0].mData = outputLeft;
    output.buffers[1].mNumberChannels = 1;
    output.buffers[1].mDataByteSize = frameCount * sizeof(Float32);
    output.buffers[1].mData = outputRight;

    AudioTimeStamp timestamp{};
    timestamp.mFlags = kAudioTimeStampSampleTimeValid;
    timestamp.mSampleTime = instance->sampleTime;
    AudioUnitRenderActionFlags flags = 0;
    const OSStatus status = AudioUnitRender(instance->unit,
                                            &flags,
                                            &timestamp,
                                            0,
                                            frameCount,
                                            reinterpret_cast<AudioBufferList *>(&output));
    if (status == noErr)
        instance->sampleTime += frameCount;
    return status;
}

size_t tl_au_parameter_count(void *handle) {
    auto *instance = static_cast<AudioUnitInstance *>(handle);
    if (instance == nullptr)
        return 0;

    UInt32 byteSize = 0;
    if (AudioUnitGetPropertyInfo(instance->unit,
                                 kAudioUnitProperty_ParameterList,
                                 kAudioUnitScope_Global,
                                 0,
                                 &byteSize,
                                 nullptr) != noErr)
        return 0;
    return byteSize / sizeof(AudioUnitParameterID);
}

int tl_au_parameter_get(void *handle, size_t index, TLAudioUnitParameter *result) {
    auto *instance = static_cast<AudioUnitInstance *>(handle);
    if (instance == nullptr || result == nullptr)
        return kAudio_ParamError;

    UInt32 byteSize = 0;
    OSStatus status = AudioUnitGetPropertyInfo(instance->unit,
                                               kAudioUnitProperty_ParameterList,
                                               kAudioUnitScope_Global,
                                               0,
                                               &byteSize,
                                               nullptr);
    if (status != noErr)
        return status;
    std::vector<AudioUnitParameterID> identifiers(byteSize / sizeof(AudioUnitParameterID));
    status = AudioUnitGetProperty(instance->unit,
                                  kAudioUnitProperty_ParameterList,
                                  kAudioUnitScope_Global,
                                  0,
                                  identifiers.data(),
                                  &byteSize);
    if (status != noErr)
        return status;
    if (index >= identifiers.size())
        return kAudio_ParamError;

    AudioUnitParameterInfo info{};
    UInt32 infoSize = sizeof(info);
    status = AudioUnitGetProperty(instance->unit,
                                  kAudioUnitProperty_ParameterInfo,
                                  kAudioUnitScope_Global,
                                  identifiers[index],
                                  &info,
                                  &infoSize);
    if (status != noErr)
        return status;

    result->identifier = identifiers[index];
    result->scope = kAudioUnitScope_Global;
    result->element = 0;
    result->minimum = info.minValue;
    result->maximum = info.maxValue;
    result->defaultValue = info.defaultValue;
    std::strncpy(result->name, info.name, sizeof(result->name) - 1);
    result->name[sizeof(result->name) - 1] = '\0';
    return noErr;
}

int tl_au_parameter_set(void *handle, AudioUnitParameterID identifier, Float32 value) {
    auto *instance = static_cast<AudioUnitInstance *>(handle);
    if (instance == nullptr)
        return kAudio_ParamError;
    return AudioUnitSetParameter(instance->unit,
                                 identifier,
                                 kAudioUnitScope_Global,
                                 0,
                                 value,
                                 0);
}

int tl_au_parameter_set_at(void *handle,
                           AudioUnitParameterID identifier,
                           Float32 value,
                           UInt32 frameOffset) {
    auto *instance = static_cast<AudioUnitInstance *>(handle);
    if (instance == nullptr || frameOffset >= instance->maximumFrames)
        return kAudio_ParamError;
    return AudioUnitSetParameter(instance->unit,
                                 identifier,
                                 kAudioUnitScope_Global,
                                 0,
                                 value,
                                 frameOffset);
}

int tl_au_reset(void *handle) {
    auto *instance = static_cast<AudioUnitInstance *>(handle);
    if (instance == nullptr)
        return kAudio_ParamError;
    instance->sampleTime = 0;
    return AudioUnitReset(instance->unit, kAudioUnitScope_Global, 0);
}

} // extern "C"
