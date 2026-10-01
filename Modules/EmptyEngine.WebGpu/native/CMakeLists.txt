cmake_minimum_required(VERSION 3.25)
project(EmptyEngineDawn LANGUAGES C CXX)

include(FetchContent)

set(EMPTYENGINE_DAWN_REVISION_FILE "${CMAKE_CURRENT_LIST_DIR}/dawn-revision.txt")
file(STRINGS "${EMPTYENGINE_DAWN_REVISION_FILE}" _revision_lines)
foreach(_line IN LISTS _revision_lines)
    if(_line MATCHES "^DAWN_REVISION=(.+)$")
        set(EMPTYENGINE_DAWN_REVISION "${CMAKE_MATCH_1}")
    endif()
endforeach()
if(NOT EMPTYENGINE_DAWN_REVISION)
    message(FATAL_ERROR "DAWN_REVISION is missing from ${EMPTYENGINE_DAWN_REVISION_FILE}")
endif()

get_filename_component(EMPTYENGINE_WEBGPU_ROOT "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)
set(EMPTYENGINE_DAWN_SOURCE_DIR "${CMAKE_BINARY_DIR}/_deps/dawn-src")
FetchContent_Declare(dawn
    URL "https://github.com/google/dawn/archive/${EMPTYENGINE_DAWN_REVISION}.tar.gz"
    URL_HASH SHA256=8138234160ec3992b5cada91b98934a55f5b57c078cd04cc3f1af8cbc53fe4bf
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    SOURCE_SUBDIR _emptyengine_patch_before_add)
FetchContent_MakeAvailable(dawn)

file(GLOB _dawn_patches "${CMAKE_CURRENT_LIST_DIR}/patches/*.patch")
get_filename_component(_dawn_source_parent "${dawn_SOURCE_DIR}" DIRECTORY)
set(_git_apply
    "${CMAKE_COMMAND}" -E env "GIT_CEILING_DIRECTORIES=${_dawn_source_parent}"
    git -C "${dawn_SOURCE_DIR}" apply --no-index)
foreach(_patch IN LISTS _dawn_patches)
    execute_process(COMMAND ${_git_apply} --reverse --check "${_patch}"
        RESULT_VARIABLE _already_applied OUTPUT_QUIET ERROR_QUIET)
    if(NOT _already_applied EQUAL 0)
        execute_process(COMMAND ${_git_apply} "${_patch}" COMMAND_ERROR_IS_FATAL ANY)
    endif()
endforeach()

# Keep private build paths out of Dawn's validation strings.
if(MSVC)
    add_compile_options("/d1trimfile:${EMPTYENGINE_DAWN_SOURCE_DIR}" "/d1trimfile:${CMAKE_BINARY_DIR}")
else()
    add_compile_options(
        "-ffile-prefix-map=${EMPTYENGINE_DAWN_SOURCE_DIR}="
        "-ffile-prefix-map=${CMAKE_BINARY_DIR}=")
endif()

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
add_subdirectory("${dawn_SOURCE_DIR}" "${dawn_BINARY_DIR}")

# Allow new compiler warnings without breaking the pinned Tint build.
if(NOT MSVC)
    function(emptyengine_demote_warnings directory)
        get_property(targets DIRECTORY "${directory}" PROPERTY BUILDSYSTEM_TARGETS)
        foreach(target IN LISTS targets)
            get_target_property(target_type ${target} TYPE)
            if(NOT target_type STREQUAL "INTERFACE_LIBRARY" AND NOT target_type STREQUAL "UTILITY")
                target_compile_options(${target} PRIVATE -Wno-error)
            endif()
        endforeach()
        get_property(subdirectories DIRECTORY "${directory}" PROPERTY SUBDIRECTORIES)
        foreach(subdirectory IN LISTS subdirectories)
            emptyengine_demote_warnings("${subdirectory}")
        endforeach()
    endfunction()
    emptyengine_demote_warnings("${EMPTYENGINE_DAWN_SOURCE_DIR}")
endif()

if(CMAKE_BUILD_TYPE)
    set(_configuration "${CMAKE_BUILD_TYPE}")
else()
    set(_configuration "$<CONFIG>")
endif()
file(GENERATE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/BUILT-FROM-$<CONFIG>.txt"
    CONTENT "$<JOIN:${_revision_lines},\n>\nCONFIGURATION=${_configuration}\n")

function(emptyengine_record_build target runtime_directory)
    add_custom_command(TARGET ${target} POST_BUILD
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${runtime_directory}"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different
            "${CMAKE_CURRENT_BINARY_DIR}/BUILT-FROM-$<CONFIG>.txt"
            "${runtime_directory}/BUILT-FROM.txt"
        VERBATIM)
endfunction()

if(WIN32)
    if(CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64")
        set(_rid win-arm64)
    else()
        set(_rid win-x64)
    endif()
    set(_runtime_directory "${EMPTYENGINE_WEBGPU_ROOT}/runtimes/${_rid}/native")
    add_library(emptyengine_webgpu_dawn SHARED emptyengine_webgpu_dawn.cpp webgpu_dawn.def)
    target_link_libraries(emptyengine_webgpu_dawn PRIVATE webgpu_dawn)
    target_link_options(emptyengine_webgpu_dawn PRIVATE "/WHOLEARCHIVE:$<TARGET_FILE:webgpu_dawn>")
    set_target_properties(emptyengine_webgpu_dawn PROPERTIES
        OUTPUT_NAME webgpu_dawn
        WINDOWS_EXPORT_ALL_SYMBOLS ON
        RUNTIME_OUTPUT_DIRECTORY "${_runtime_directory}"
        RUNTIME_OUTPUT_DIRECTORY_DEBUG "${_runtime_directory}"
        RUNTIME_OUTPUT_DIRECTORY_RELEASE "${_runtime_directory}")
    emptyengine_record_build(emptyengine_webgpu_dawn "${_runtime_directory}")
elseif(CMAKE_SYSTEM_NAME STREQUAL "Darwin")
    if(CMAKE_OSX_ARCHITECTURES STREQUAL "x86_64")
        set(_rid osx-x64)
    else()
        set(_rid osx-arm64)
    endif()
    set(_runtime_directory "${EMPTYENGINE_WEBGPU_ROOT}/runtimes/${_rid}/native")
    add_library(emptyengine_webgpu_dawn SHARED emptyengine_webgpu_dawn.cpp)
    target_link_libraries(emptyengine_webgpu_dawn PRIVATE webgpu_dawn)
    target_link_options(emptyengine_webgpu_dawn PRIVATE "-Wl,-force_load,$<TARGET_FILE:webgpu_dawn>")
    set_target_properties(emptyengine_webgpu_dawn PROPERTIES
        OUTPUT_NAME webgpu_dawn
        INSTALL_NAME_DIR "@rpath"
        LIBRARY_OUTPUT_DIRECTORY "${_runtime_directory}")
    emptyengine_record_build(emptyengine_webgpu_dawn "${_runtime_directory}")
elseif(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    set(_runtime_directory "${EMPTYENGINE_WEBGPU_ROOT}/runtimes/ios-arm64/native")
    set(_ios_archive "${_runtime_directory}/libemptyengine_webgpu_dawn.a")
    add_custom_command(OUTPUT "${_ios_archive}"
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${_runtime_directory}"
        COMMAND bash "${CMAKE_CURRENT_LIST_DIR}/merge-ios-archives.sh"
            "${CMAKE_BINARY_DIR}" "${_ios_archive}"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different
            "${CMAKE_CURRENT_BINARY_DIR}/BUILT-FROM-$<CONFIG>.txt"
            "${_runtime_directory}/BUILT-FROM.txt"
        DEPENDS webgpu_dawn
        COMMENT "Merging Dawn's static dependency closure"
        VERBATIM)
    add_custom_target(emptyengine_webgpu_dawn ALL DEPENDS "${_ios_archive}")
else()
    message(FATAL_ERROR "Only Windows, macOS and iOS are supported")
endif()
