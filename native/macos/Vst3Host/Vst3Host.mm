#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/utility/stringconvert.h"
#include "pluginterfaces/base/ipluginbase.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <vector>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {

struct PluginInfo {
    std::string classId;
    std::string name;
    std::string vendor;
    std::string path;
};

struct PluginInstance {
    VST3::Hosting::Module::Ptr module;
    std::unique_ptr<PlugProvider> provider;
    IPtr<IComponent> component;
    IPtr<IEditController> controller;
    IPtr<IAudioProcessor> processor;
    int32 inputChannels = 0;
    int32 outputChannels = 0;
    int32 maximumFrames = 0;
    ParameterChanges parameterChanges;
    bool active = false;
    bool processing = false;
};

std::mutex gPluginMutex;
HostApplication* gHostApplication = nullptr;
bool gPluginsScanned = false;

std::vector<PluginInfo>& Plugins() {
    static auto* plugins = new std::vector<PluginInfo>();
    return *plugins;
}

void EnsureHost() {
    if (gHostApplication)
        return;
    gHostApplication = new HostApplication();
    PluginContextFactory::instance().setPluginContext(gHostApplication);
}

template <size_t N>
void CopyText(const std::string& text, char (&destination)[N]) {
    std::strncpy(destination, text.c_str(), N - 1);
    destination[N - 1] = '\0';
}

VST3::Hosting::Module::PathList ModulePaths() {
    const char* overridePaths = std::getenv("TUNELAB_VST3_PATHS");
    if (!overridePaths || !*overridePaths)
        return VST3::Hosting::Module::getModulePaths();

    VST3::Hosting::Module::PathList paths;
    std::stringstream stream(overridePaths);
    std::string path;
    while (std::getline(stream, path, ':')) {
        if (!path.empty())
            paths.push_back(path);
    }
    return paths;
}

bool ScanPlugins(std::string& error) {
    std::lock_guard<std::mutex> lock(gPluginMutex);
    if (gPluginsScanned)
        return true;
    gPluginsScanned = true;

    EnsureHost();
    for (const auto& path : ModulePaths()) {
        std::string moduleError;
        auto module = VST3::Hosting::Module::create(path, moduleError);
        if (!module)
            continue;
        auto factory = module->getFactory();
        factory.setHostContext(gHostApplication);
        for (auto& classInfo : factory.classInfos()) {
            if (classInfo.category() != kVstAudioEffectClass)
                continue;
            const auto& subCategories = classInfo.subCategories();
            // TuneLab exposes insert effects only. VST3 also permits an effect
            // class to advertise Fx|Instrument when it can act as a generator;
            // those classes must stay out of the effect picker.
            const bool startsAsEffect = !subCategories.empty() && subCategories.front() == "Fx";
            const bool isInstrumentCapable = std::any_of(subCategories.begin(), subCategories.end(),
                [](const std::string& category) { return category == "Instrument"; });
            const bool isEffect = startsAsEffect && !isInstrumentCapable;
            if (!isEffect)
                continue;
            Plugins().push_back({classInfo.ID().toString(), classInfo.name(), classInfo.vendor(), path});
        }
    }
    return true;
}

bool ResolvePlugin(const char* classId, PluginInfo& output) {
    if (!classId)
        return false;
    std::lock_guard<std::mutex> lock(gPluginMutex);
    for (const auto& plugin : Plugins()) {
        if (plugin.classId == classId) {
            output = plugin;
            return true;
        }
    }
    return false;
}

void DestroyInstance(PluginInstance* instance) {
    if (!instance)
        return;
    if (instance->processing) {
        instance->processor->setProcessing(false);
        instance->processing = false;
    }
    if (instance->active) {
        instance->component->setActive(false);
        instance->active = false;
    }
    delete instance;
}

} // namespace

extern "C" {

struct TLVst3PluginInfo {
    char classId[64];
    char name[256];
    char vendor[256];
    char path[1024];
};

struct TLVst3ParameterInfo {
    uint32_t id;
    double minimum;
    double maximum;
    double defaultValue;
    int32_t flags;
    char name[256];
    char units[128];
};

int tl_vst3_scan() {
    std::string error;
    return ScanPlugins(error) ? 0 : -1;
}

size_t tl_vst3_plugin_count() {
    std::lock_guard<std::mutex> lock(gPluginMutex);
    return Plugins().size();
}

int tl_vst3_plugin_get(size_t index, TLVst3PluginInfo* result) {
    if (!result)
        return -1;
    std::lock_guard<std::mutex> lock(gPluginMutex);
    const auto& plugins = Plugins();
    if (index >= plugins.size())
        return -2;
    const auto& plugin = plugins[index];
    std::memset(result, 0, sizeof(*result));
    CopyText(plugin.classId, result->classId);
    CopyText(plugin.name, result->name);
    CopyText(plugin.vendor, result->vendor);
    CopyText(plugin.path, result->path);
    return 0;
}

void* tl_vst3_create(const char* classId, double sampleRate, int32_t maximumFrames, int* error) {
    if (error)
        *error = 0;
    if (!classId || sampleRate <= 0 || maximumFrames <= 0) {
        if (error)
            *error = -1;
        return nullptr;
    }

    PluginInfo plugin;
    if (!ResolvePlugin(classId, plugin)) {
        if (error)
            *error = -2;
        return nullptr;
    }

    EnsureHost();
    auto instance = std::make_unique<PluginInstance>();
    instance->maximumFrames = maximumFrames;
    std::string moduleError;
    instance->module = VST3::Hosting::Module::create(plugin.path, moduleError);
    if (!instance->module) {
        if (error)
            *error = -3;
        return nullptr;
    }
    auto factory = instance->module->getFactory();
    factory.setHostContext(gHostApplication);
    VST3::Hosting::ClassInfo selectedClass;
    bool foundClass = false;
    for (const auto& classInfo : factory.classInfos()) {
        if (classInfo.ID().toString() == plugin.classId) {
            selectedClass = classInfo;
            foundClass = true;
            break;
        }
    }
    if (!foundClass) {
        if (error)
            *error = -4;
        return nullptr;
    }
    instance->provider = std::make_unique<PlugProvider>(factory, selectedClass, true);
    if (!instance->provider->initialize()) {
        if (error)
            *error = -5;
        return nullptr;
    }

    instance->component = instance->provider->getComponentPtr();
    instance->controller = instance->provider->getControllerPtr();
    instance->processor = U::cast<IAudioProcessor>(instance->component);
    if (!instance->component || !instance->controller || !instance->processor) {
        if (error)
            *error = -6;
        return nullptr;
    }

    for (int32 i = 0; i < instance->component->getBusCount(kAudio, kInput); ++i) {
        BusInfo bus{};
        if (instance->component->getBusInfo(kAudio, kInput, i, bus) == kResultTrue &&
            (bus.flags & BusInfo::kDefaultActive)) {
            instance->component->activateBus(kAudio, kInput, i, true);
            if (instance->inputChannels == 0)
                instance->inputChannels = bus.channelCount;
        }
    }
    for (int32 i = 0; i < instance->component->getBusCount(kAudio, kOutput); ++i) {
        BusInfo bus{};
        if (instance->component->getBusInfo(kAudio, kOutput, i, bus) == kResultTrue &&
            (bus.flags & BusInfo::kDefaultActive)) {
            instance->component->activateBus(kAudio, kOutput, i, true);
            if (instance->outputChannels == 0)
                instance->outputChannels = bus.channelCount;
        }
    }
    if (instance->inputChannels != 2 || instance->outputChannels != 2) {
        if (error)
            *error = -7;
        return nullptr;
    }

    ProcessSetup setup{};
    setup.processMode = kOffline;
    setup.symbolicSampleSize = kSample32;
    setup.maxSamplesPerBlock = maximumFrames;
    setup.sampleRate = sampleRate;
    if (instance->processor->setupProcessing(setup) != kResultOk ||
        instance->component->setActive(true) != kResultOk) {
        if (error)
            *error = -8;
        return nullptr;
    }
    instance->active = true;
    if (instance->processor->setProcessing(true) != kResultOk) {
        if (error)
            *error = -9;
        return nullptr;
    }
    instance->processing = true;
    return instance.release();
}

void tl_vst3_destroy(void* handle) {
    DestroyInstance(static_cast<PluginInstance*>(handle));
}

int tl_vst3_reset(void* handle) {
    auto* instance = static_cast<PluginInstance*>(handle);
    if (!instance)
        return -1;
    if (instance->processing && instance->processor->setProcessing(false) != kResultOk)
        return -2;
    instance->processing = false;
    if (instance->active && instance->component->setActive(false) != kResultOk)
        return -3;
    instance->active = false;
    if (instance->component->setActive(true) != kResultOk)
        return -4;
    instance->active = true;
    if (instance->processor->setProcessing(true) != kResultOk)
        return -5;
    instance->processing = true;
    return 0;
}

size_t tl_vst3_parameter_count(void* handle) {
    auto* instance = static_cast<PluginInstance*>(handle);
    return instance ? static_cast<size_t>(instance->controller->getParameterCount()) : 0;
}

int tl_vst3_parameter_get(void* handle, size_t index, TLVst3ParameterInfo* result, double* currentValue) {
    auto* instance = static_cast<PluginInstance*>(handle);
    if (!instance || !result || index >= static_cast<size_t>(instance->controller->getParameterCount()))
        return -1;
    ParameterInfo info{};
    if (instance->controller->getParameterInfo(static_cast<int32>(index), info) != kResultOk)
        return -2;
    std::memset(result, 0, sizeof(*result));
    result->id = info.id;
    result->minimum = 0.0;
    result->maximum = 1.0;
    result->defaultValue = info.defaultNormalizedValue;
    result->flags = info.flags;
    CopyText(StringConvert::convert(info.title), result->name);
    CopyText(StringConvert::convert(info.units), result->units);
    if (currentValue)
        *currentValue = instance->controller->getParamNormalized(info.id);
    return 0;
}

int tl_vst3_parameter_set_at(void* handle, uint32_t parameterId, double value, int32_t sampleOffset) {
    auto* instance = static_cast<PluginInstance*>(handle);
    if (!instance || sampleOffset < 0 || sampleOffset >= instance->maximumFrames)
        return -1;
    const auto normalized = std::clamp(value, 0.0, 1.0);
    if (instance->controller->setParamNormalized(parameterId, normalized) != kResultOk)
        return -2;
    int32 queueIndex = 0;
    auto* queue = instance->parameterChanges.addParameterData(parameterId, queueIndex);
    if (!queue)
        return -3;
    return queue->addPoint(sampleOffset, normalized, queueIndex) == kResultOk ? 0 : -4;
}

int tl_vst3_parameter_set(void* handle, uint32_t parameterId, double value) {
    return tl_vst3_parameter_set_at(handle, parameterId, value, 0);
}

int tl_vst3_process(void* handle, const float* input, float* output, int32_t frameCount) {
    auto* instance = static_cast<PluginInstance*>(handle);
    if (!instance || !input || !output || frameCount < 0)
        return -1;
    if (frameCount == 0)
        return 0;
    std::vector<float> left(static_cast<size_t>(frameCount));
    std::vector<float> right(static_cast<size_t>(frameCount));
    std::vector<float> outputLeft(static_cast<size_t>(frameCount));
    std::vector<float> outputRight(static_cast<size_t>(frameCount));
    for (int32_t i = 0; i < frameCount; ++i)
        left[i] = right[i] = input[i];
    float* inChannels[] = {left.data(), right.data()};
    float* outChannels[] = {outputLeft.data(), outputRight.data()};
    AudioBusBuffers inputBus{};
    inputBus.numChannels = 2;
    inputBus.channelBuffers32 = inChannels;
    AudioBusBuffers outputBus{};
    outputBus.numChannels = 2;
    outputBus.channelBuffers32 = outChannels;
    ProcessData data{};
    data.processMode = kOffline;
    data.symbolicSampleSize = kSample32;
    data.numSamples = frameCount;
    data.numInputs = 1;
    data.numOutputs = 1;
    data.inputs = &inputBus;
    data.outputs = &outputBus;
    data.inputParameterChanges = &instance->parameterChanges;
    const auto processResult = instance->processor->process(data);
    instance->parameterChanges.clearQueue();
    if (processResult != kResultOk)
        return -2;
    for (int32_t i = 0; i < frameCount; ++i)
        output[i] = (outputLeft[i] + outputRight[i]) * 0.5f;
    return 0;
}

} // extern "C"
