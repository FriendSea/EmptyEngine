cmake_minimum_required(VERSION 3.25)
project(EmptyEngineBasisTranscoder LANGUAGES CXX)

# Basis Universal トランスコーダと C ABI ラッパーをビルドする。

get_filename_component(EMPTYENGINE_GRAPHICS_ROOT "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)

include("${CMAKE_CURRENT_LIST_DIR}/basis_universal.cmake")
emptyengine_fetch_basis_universal()

set(EMPTYENGINE_BASIS_SOURCES
    basis_transcoder.cpp
    basis_universal/transcoder/basisu_transcoder.cpp)

if(WIN32)
    if(CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64")
        set(_rid win-arm64)
    else()
        set(_rid win-x64)
    endif()
elseif(CMAKE_SYSTEM_NAME STREQUAL "Darwin")
    if(CMAKE_OSX_ARCHITECTURES STREQUAL "x86_64")
        set(_rid osx-x64)
    else()
        set(_rid osx-arm64)
    endif()
elseif(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    set(_rid ios-arm64)
else()
    message(FATAL_ERROR "Only Windows, macOS and iOS are supported")
endif()

set(_runtime_directory "${EMPTYENGINE_GRAPHICS_ROOT}/runtimes/${_rid}/native")

if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    # iOS アプリへ静的リンクするライブラリ。
    add_library(basisu_transcoder STATIC ${EMPTYENGINE_BASIS_SOURCES})
    set_target_properties(basisu_transcoder PROPERTIES
        POSITION_INDEPENDENT_CODE ON
        ARCHIVE_OUTPUT_DIRECTORY "${_runtime_directory}"
        ARCHIVE_OUTPUT_DIRECTORY_DEBUG "${_runtime_directory}"
        ARCHIVE_OUTPUT_DIRECTORY_RELEASE "${_runtime_directory}")
else()
    add_library(basisu_transcoder SHARED ${EMPTYENGINE_BASIS_SOURCES})
    # 実行時に必要な共有ライブラリを runtimes 配下へ出力する。
    set_target_properties(basisu_transcoder PROPERTIES
        INSTALL_NAME_DIR "@rpath"
        RUNTIME_OUTPUT_DIRECTORY "${_runtime_directory}"
        RUNTIME_OUTPUT_DIRECTORY_DEBUG "${_runtime_directory}"
        RUNTIME_OUTPUT_DIRECTORY_RELEASE "${_runtime_directory}"
        LIBRARY_OUTPUT_DIRECTORY "${_runtime_directory}"
        LIBRARY_OUTPUT_DIRECTORY_DEBUG "${_runtime_directory}"
        LIBRARY_OUTPUT_DIRECTORY_RELEASE "${_runtime_directory}")
endif()

target_compile_features(basisu_transcoder PRIVATE cxx_std_17)
# Zstd を同梱しないため、KTX2 の supercompression を無効にする。
target_compile_definitions(basisu_transcoder PRIVATE BASISD_SUPPORT_KTX2_ZSTD=0)
if(MSVC)
    target_compile_options(basisu_transcoder PRIVATE /utf-8)
else()
    target_compile_options(basisu_transcoder PRIVATE -fno-strict-aliasing)
endif()
