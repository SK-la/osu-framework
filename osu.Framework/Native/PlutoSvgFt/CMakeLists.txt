cmake_minimum_required(VERSION 3.15)
project(plutosvgft LANGUAGES C)

# Static plutovg + plutosvg, then wrap FreeType hooks in a shared DLL.
set(BUILD_SHARED_LIBS OFF CACHE BOOL "Build plutovg/plutosvg as static" FORCE)
set(PLUTOVG_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(PLUTOSVG_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(PLUTOSVG_ENABLE_FREETYPE OFF CACHE BOOL "" FORCE)

set(PLUTOSVGFT_DEPS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/deps" CACHE PATH "Local deps (plutosvg clone, FreeType headers)")

if(NOT EXISTS "${PLUTOSVGFT_DEPS_DIR}/plutosvg/CMakeLists.txt")
    include(FetchContent)
    FetchContent_Declare(plutosvg
        GIT_REPOSITORY https://github.com/sammycage/plutosvg.git
        GIT_TAG master
        GIT_SUBMODULES plutovg
        SOURCE_DIR "${PLUTOSVGFT_DEPS_DIR}/plutosvg"
    )
    FetchContent_MakeAvailable(plutosvg)
else()
    add_subdirectory("${PLUTOSVGFT_DEPS_DIR}/plutosvg" "${CMAKE_BINARY_DIR}/_deps/plutosvg-build")
endif()

# FreeType headers (match McEndu.FreeTypeSharp / FreeType 2.14.1)
set(FREETYPE_INCLUDE_DIR "${PLUTOSVGFT_DEPS_DIR}/freetype-2.14.1/include" CACHE PATH "FreeType headers")
if(NOT EXISTS "${FREETYPE_INCLUDE_DIR}/ft2build.h")
    message(FATAL_ERROR "FreeType headers not found at ${FREETYPE_INCLUDE_DIR}. Extract freetype-2.14.1 into deps/.")
endif()

# FreeType import library (generated from nuget freetype.dll via gendef+dlltool)
set(FREETYPE_IMPORT_LIB "${PLUTOSVGFT_DEPS_DIR}/freetype-import/libfreetype.dll.a" CACHE FILEPATH "FreeType import lib")
if(NOT EXISTS "${FREETYPE_IMPORT_LIB}")
    message(FATAL_ERROR "FreeType import library not found: ${FREETYPE_IMPORT_LIB}")
endif()

add_library(plutosvgft SHARED export.c)
set_target_properties(plutosvgft PROPERTIES
    C_STANDARD 99
    C_STANDARD_REQUIRED ON
    OUTPUT_NAME plutosvgft
    PREFIX ""
    WINDOWS_EXPORT_ALL_SYMBOLS OFF
)

target_include_directories(plutosvgft PRIVATE
    "${FREETYPE_INCLUDE_DIR}"
    "${PLUTOSVGFT_DEPS_DIR}/plutosvg/source"
)

target_compile_definitions(plutosvgft PRIVATE
    PLUTOSVG_BUILD_STATIC
    PLUTOVG_BUILD_STATIC
)

target_link_libraries(plutosvgft PRIVATE
    plutosvg::plutosvg
    "${FREETYPE_IMPORT_LIB}"
)

# Ensure the shared DLL can resolve freetype.dll at link time name
target_link_options(plutosvgft PRIVATE "-Wl,--enable-auto-import")

install(TARGETS plutosvgft RUNTIME DESTINATION bin LIBRARY DESTINATION lib)
