# Basis Universal のトランスコーダだけを公式リポジトリから取得する。
#
# CMakeLists.txt から include() するほか、MSBuild から `cmake -P` で単体実行する
# （ブラウザ向けビルドとパッケージ作成は CMake を通らずにソースを直接使うため）。
#
# blob 抜きの浅いクローン＋スパースチェックアウトで、transcoder/ と LICENSE・NOTICE の
# blob だけを落とす。取得先は native/basis_universal/（.gitignore 済み）。
# 中身はコミットハッシュで固定し、スタンプが一致すれば何もしない。

set(EMPTYENGINE_BASISU_REPOSITORY "https://github.com/BinomialLLC/basis_universal.git")
# リリース v2_50。
set(EMPTYENGINE_BASISU_COMMIT "9bebe16726b3a61c8c213eeee3b7cffb462ef34e")
set(EMPTYENGINE_BASISU_PATHS "/transcoder/" "/LICENSE" "/NOTICE")
set(EMPTYENGINE_BASISU_DIR "${CMAKE_CURRENT_LIST_DIR}/basis_universal")

function(emptyengine_fetch_basis_universal)
    set(_dir "${EMPTYENGINE_BASISU_DIR}")
    set(_stamp "${_dir}/.emptyengine-commit")
    if(EXISTS "${_stamp}")
        file(READ "${_stamp}" _current)
        string(STRIP "${_current}" _current)
        if(_current STREQUAL EMPTYENGINE_BASISU_COMMIT)
            return()
        endif()
    endif()

    find_package(Git QUIET)
    if(NOT GIT_EXECUTABLE)
        message(FATAL_ERROR "git is required to fetch Basis Universal")
    endif()

    message(STATUS "Fetching Basis Universal ${EMPTYENGINE_BASISU_COMMIT}")
    file(REMOVE_RECURSE "${_dir}")
    file(MAKE_DIRECTORY "${_dir}")
    # 親のリポジトリ（EmptyEngine 自身）を拾わないよう、取得先の親で探索を止める。
    get_filename_component(_parent "${_dir}" DIRECTORY)
    set(_git "${CMAKE_COMMAND}" -E env "GIT_CEILING_DIRECTORIES=${_parent}"
        "${GIT_EXECUTABLE}" -C "${_dir}")
    foreach(_args IN ITEMS
            "init;--quiet"
            "config;core.autocrlf;false"
            "remote;add;origin;${EMPTYENGINE_BASISU_REPOSITORY}"
            "sparse-checkout;set;--no-cone;${EMPTYENGINE_BASISU_PATHS}"
            "fetch;--quiet;--depth=1;--filter=blob:none;origin;${EMPTYENGINE_BASISU_COMMIT}"
            "-c;advice.detachedHead=false;checkout;--quiet;FETCH_HEAD")
        execute_process(COMMAND ${_git} ${_args} COMMAND_ERROR_IS_FATAL ANY)
    endforeach()
    # 取得物は作業ツリーだけ使う。.git を残すと IDE や親リポジトリが入れ子リポジトリとして扱う。
    file(REMOVE_RECURSE "${_dir}/.git")
    file(WRITE "${_stamp}" "${EMPTYENGINE_BASISU_COMMIT}\n")
endfunction()

if(CMAKE_SCRIPT_MODE_FILE STREQUAL CMAKE_CURRENT_LIST_FILE)
    emptyengine_fetch_basis_universal()
endif()
