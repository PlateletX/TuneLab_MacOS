cmake_minimum_required(VERSION 3.25)
project(TuneLabVst3Host LANGUAGES CXX OBJCXX)

if(NOT DEFINED VST3_SDK_ROOT OR NOT EXISTS "${VST3_SDK_ROOT}/CMakeLists.txt")
    message(FATAL_ERROR "Set VST3_SDK_ROOT to the Steinberg VST3 SDK 3.8.1 source directory")
endif()

set(CMAKE_OSX_DEPLOYMENT_TARGET "26.0" CACHE STRING "Minimum supported macOS version")
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(SMTG_ENABLE_VST3_PLUGIN_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_HOSTING_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VSTGUI_SUPPORT OFF CACHE BOOL "" FORCE)
add_subdirectory("${VST3_SDK_ROOT}" "${CMAKE_BINARY_DIR}/vst3sdk" EXCLUDE_FROM_ALL)

set(VST3_HOSTING_SOURCES
    Vst3Host.mm
    "${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/plugprovider.cpp"
    "${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/module_mac.mm")
set_source_files_properties(
    "${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/module_mac.mm"
    PROPERTIES COMPILE_FLAGS "-fobjc-arc")

add_library(TuneLabVst3Host SHARED ${VST3_HOSTING_SOURCES})
target_link_libraries(TuneLabVst3Host PRIVATE sdk_hosting sdk_common pluginterfaces base "-framework Foundation")
target_compile_definitions(TuneLabVst3Host PRIVATE SMTG_RENAME_ASSERT=1)
set_target_properties(TuneLabVst3Host PROPERTIES
    OUTPUT_NAME TuneLabVst3Host
    MACOSX_RPATH ON
    INSTALL_NAME_DIR "@rpath")

