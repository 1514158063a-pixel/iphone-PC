cmake_minimum_required(VERSION 3.16)
project(ClockAudioRegression LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(NO_MARCH_NATIVE ON CACHE BOOL "Use portable target code" FORCE)
if(NOT NATIVE_CORE_DIR)
  get_filename_component(NATIVE_CORE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../source/uxplay-windows/libuxplay" ABSOLUTE)
endif()
if(NOT EXISTS "${NATIVE_CORE_DIR}/renderers/audio_renderer.c")
  message(FATAL_ERROR "The candidate production core is not available")
endif()

# Build the real candidate libraries, without the Qt application or an AirPlay
# listener. The audio fixture recompiles only the actual renderer with its
# explicit test-only PCM/state inspection switch.
add_subdirectory("${NATIVE_CORE_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/core")

add_executable(audio_lifecycle_tests audio_lifecycle_tests.cpp
  "${NATIVE_CORE_DIR}/renderers/audio_renderer.c")
target_compile_definitions(audio_lifecycle_tests PRIVATE UXPLAY_AUDIO_LIFECYCLE_TESTS=1 WIN32)
target_include_directories(audio_lifecycle_tests PRIVATE
  "${NATIVE_CORE_DIR}/renderers" "${NATIVE_CORE_DIR}/lib")
target_link_libraries(audio_lifecycle_tests PRIVATE renderers)
set_target_properties(audio_lifecycle_tests PROPERTIES CXX_STANDARD 17)

add_executable(ntp_conversion_tests ntp_conversion_tests.cpp
  "${NATIVE_CORE_DIR}/lib/raop_ntp.c")
target_compile_definitions(ntp_conversion_tests PRIVATE WIN32 UXPLAY_NTP_LIFECYCLE_TESTS=1)
target_include_directories(ntp_conversion_tests PRIVATE "${NATIVE_CORE_DIR}/lib")
target_link_libraries(ntp_conversion_tests PRIVATE airplay)
target_link_options(ntp_conversion_tests PRIVATE "-Wl,--wrap=netutils_init_socket")
set_target_properties(ntp_conversion_tests PROPERTIES CXX_STANDARD 17)

add_executable(rtp_lifecycle_tests rtp_lifecycle_tests.cpp
  "${NATIVE_CORE_DIR}/lib/raop_rtp.c")
target_compile_definitions(rtp_lifecycle_tests PRIVATE UXPLAY_RTP_LIFECYCLE_TESTS=1 WIN32)
target_include_directories(rtp_lifecycle_tests PRIVATE "${NATIVE_CORE_DIR}/lib")
target_link_libraries(rtp_lifecycle_tests PRIVATE airplay)
target_link_options(rtp_lifecycle_tests PRIVATE "-Wl,--wrap=netutils_init_socket")
set_target_properties(rtp_lifecycle_tests PROPERTIES CXX_STANDARD 17)
