cmake_minimum_required(VERSION 3.20)
project(ODX_HUD_EXTENSION LANGUAGES CXX)

add_library(ODX_HUD_x64 SHARED ODX_HUD.cpp)
target_compile_features(ODX_HUD_x64 PRIVATE cxx_std_17)
target_compile_definitions(ODX_HUD_x64 PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
set_target_properties(ODX_HUD_x64 PROPERTIES
    PREFIX ""
    OUTPUT_NAME "ODX_HUD_x64"
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
)
