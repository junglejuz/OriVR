cmake_minimum_required(VERSION 3.20)
project(OriVRNative CXX)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
set(OPENXR_DIR "C:/VRLab/bin/openxr_loader_1.1.65" CACHE PATH "Khronos OpenXR loader folder")
add_library(OriVRNative SHARED OriVRNative.cpp)
target_include_directories(OriVRNative PRIVATE ${OPENXR_DIR}/include)
target_link_libraries(OriVRNative PRIVATE d3d11 dxgi)
set_target_properties(OriVRNative PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_SOURCE_DIR}/../../build")
add_custom_command(TARGET OriVRNative POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${OPENXR_DIR}/Win32/bin/openxr_loader.dll" "${CMAKE_SOURCE_DIR}/../../build/openxr_loader.dll")
