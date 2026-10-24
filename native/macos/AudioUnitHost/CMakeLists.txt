cmake_minimum_required(VERSION 3.20)
set(CMAKE_OSX_DEPLOYMENT_TARGET "26.0" CACHE STRING "Minimum supported macOS version")
project(TuneLabAudioUnitHost LANGUAGES CXX OBJCXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

add_library(TuneLabAudioUnitHost SHARED AudioUnitHost.mm)
target_link_libraries(TuneLabAudioUnitHost PRIVATE "-framework AudioToolbox" "-framework CoreAudio" "-framework CoreFoundation")
set_target_properties(TuneLabAudioUnitHost PROPERTIES
    OUTPUT_NAME TuneLabAudioUnitHost
    MACOSX_RPATH ON
    INSTALL_NAME_DIR "@rpath")

add_executable(auhost-smoke auhost-smoke.cpp)
target_link_libraries(auhost-smoke PRIVATE TuneLabAudioUnitHost)

enable_testing()
add_test(NAME auhost-smoke COMMAND auhost-smoke)
