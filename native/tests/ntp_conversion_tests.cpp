#include <winsock2.h>
#include <chrono>
#include <thread>
#include <cstring>
#include <mutex>
#include <vector>
#include <atomic>
#include <array>
#include <cmath>
extern "C" {
#include "raop.h"
#include "raop_ntp.h"
#include "media_clock.h"
}
#include "test_report.h"
#include "loopback_fixture.h"

namespace {
logger_t* fixture_logger = nullptr;
std::mutex log_mutex;
std::vector<std::string> error_logs;
void log_callback(void*, int level, const char* message) {
    if (level <= LOGGER_ERR) {
        std::lock_guard<std::mutex> lock(log_mutex);
        error_logs.emplace_back(message ? message : "");
    }
}
std::atomic<unsigned int> fallback_datagrams{0};
std::atomic<unsigned int> timing_responses{0};
std::atomic<unsigned int> full_client_refs{0};
raop_ntp_t* create_clock(timing_protocol_t protocol, unsigned short remote_port = 0) {
    raop_callbacks_t callbacks;
    std::memset(&callbacks, 0, sizeof(callbacks));
    raop_ntp_t* clock = raop_ntp_init(fixture_logger, &callbacks,
        "127.0.0.1", 4, remote_port, &protocol);
    require_true(clock != nullptr, "The production NTP object could not be initialized");
    return clock;
}

class ClockObject {
    raop_ntp_t* clock = nullptr;
public:
    explicit ClockObject(timing_protocol_t protocol, unsigned short remote_port = 0)
        : clock(create_clock(protocol, remote_port)) {}
    ~ClockObject() { if (clock) raop_ntp_destroy(clock); }
    raop_ntp_t* get() const { return clock; }
};

class FallbackSession {
    kernel_timestamp_session_t* session = nullptr;
public:
    FallbackSession(raop_ntp_t* clock, PrivateLoopbackSocket& receiver) {
        session = kernel_timestamp_session_create(clock, static_cast<int>(receiver.get()));
        require_true(session != nullptr, "Cannot create a production timestamp session");
        (void)receiver.release();
        // Controlled absence of optional capability on this fixture's own socket.
        session->pWSARecvMsg_ptr = nullptr;
    }
    ~FallbackSession() { if (session) kernel_timestamp_session_destroy(session); }
    kernel_timestamp_session_t* get() const { return session; }
    void close() { kernel_timestamp_session_destroy(session); session = nullptr; }
};

void send_fixed_datagram(PrivateLoopbackSocket& sender, const sockaddr_in& destination) {
    const char data[4] = {'T', 'E', 'S', 'T'};
    require_true(sendto(sender.get(), data, sizeof(data), 0,
                     reinterpret_cast<const sockaddr*>(&destination), sizeof(destination)) == sizeof(data),
                 "The private fixed test datagram could not be sent");
}

uint64_t raw_client_timestamp(std::chrono::steady_clock::time_point start) {
    uint64_t elapsed = static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now() - start).count());
    uint64_t seconds = 2208988800ULL + 1234567ULL + elapsed / 1000000000ULL;
    uint64_t fraction = ((elapsed % 1000000000ULL) << 32) / 1000000000ULL;
    return (seconds << 32) | fraction;
}
void put_big_endian(char* destination, uint64_t value) {
    for (int index = 7; index >= 0; --index) { destination[index] = static_cast<char>(value); value >>= 8; }
}

class PrivateTimingResponder {
    PrivateLoopbackSocket socket;
    std::atomic<bool> stop{false};
    std::atomic<bool> failed{false};
    std::thread worker;
public:
    PrivateTimingResponder() : worker([this] {
        auto start = std::chrono::steady_clock::now();
        char last_client_timestamp[8]{};
        bool previous_response_sent = false;
        while (!stop) {
            char request[32]{};
            sockaddr_in source{};
            int source_length = sizeof(source);
            int received = recvfrom(socket.get(), request, sizeof(request), 0,
                reinterpret_cast<sockaddr*>(&source), &source_length);
            if (received == SOCKET_ERROR) {
                if (WSAGetLastError() != WSAETIMEDOUT) failed = true;
                continue;
            }
            if (received != sizeof(request) || source.sin_addr.s_addr != htonl(INADDR_LOOPBACK)) {
                failed = true;
                continue;
            }
            if (previous_response_sent) {
                if (std::memcmp(request + 8, last_client_timestamp, sizeof(last_client_timestamp)) != 0)
                    failed = true;
                else ++full_client_refs;
            }
            char response[32]{};
            response[0] = static_cast<char>(0x80);
            response[1] = static_cast<char>(0xd3);
            std::memcpy(response + 2, request + 2, 2);
            std::memcpy(response + 8, request + 24, 8);
            put_big_endian(response + 16, raw_client_timestamp(start));
            std::this_thread::sleep_for(std::chrono::microseconds(100));
            put_big_endian(response + 24, raw_client_timestamp(start));
            if (sendto(socket.get(), response, sizeof(response), 0,
                    reinterpret_cast<const sockaddr*>(&source), sizeof(source)) != sizeof(response)) failed = true;
            else {
                std::memcpy(last_client_timestamp, response + 24, sizeof(last_client_timestamp));
                previous_response_sent = true;
                ++timing_responses;
            }
        }
    }) {}
    ~PrivateTimingResponder() { stop = true; worker.join(); }
    unsigned short port() const { return socket.port(); }
    bool healthy() const { return !failed; }
};
}

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    TestReport report;
    WSADATA winsock;
    if (WSAStartup(MAKEWORD(2, 2), &winsock) != 0) return 3;
    fixture_logger = logger_init();
    logger_set_callback(fixture_logger, log_callback, nullptr);
    logger_set_level(fixture_logger, LOGGER_INFO);
    report.check("windows.global-clock-initialization-repeatable", [] {
        raop_ntp_global_init();
        raop_ntp_global_init();
    });
    report.check("ntp.production-dispersion-weights-match-eight-fixed-values", [] {
        const std::array<double, 8> expected{0.5, 0.25, 0.125, 0.0625,
                                            0.03125, 0.015625, 0.0078125, 0.00390625};
        double sum = 0;
        for (unsigned int index = 0; index < expected.size(); ++index) {
            double actual = raop_ntp_test_filter_weight(index);
            require_true(std::isfinite(actual) && actual == expected[index],
                         "An actual production NTP filter weight differs from the fixed reference");
            sum += actual;
        }
        require_true(sum == 0.99609375, "Production NTP filter weight sum differs from 255/256");
    });
    report.check("ntp.epoch-and-fraction-conversion", [] {
        raop_ntp_t* clock = create_clock(NTP);
        const uint64_t raw = ((2208988800ULL + 1) << 32) | 0x80000000ULL;
        require_true(raop_ntp_timestamp_to_nano_seconds(clock, raw) == 1500000000ULL,
                     "NTP epoch/fraction conversion is incorrect");
        raop_ntp_destroy(clock);
    });
    report.check("non-ntp.raw-fraction-conversion", [] {
        raop_ntp_t* clock = create_clock(TP_NONE);
        const uint64_t raw = (7ULL << 32) | 0x40000000ULL;
        require_true(raop_ntp_timestamp_to_nano_seconds(clock, raw) == 7250000000ULL,
                     "Non-NTP fraction conversion is incorrect");
        raop_ntp_destroy(clock);
    });
    report.check("unsynchronized-clock-does-not-invent-remote-time", [] {
        raop_ntp_t* clock = create_clock(NTP);
        require_true(raop_ntp_get_remote_time(clock) == 0, "A new object has a stale remote time");
        require_true(raop_ntp_convert_remote_time(clock, 1000000000ULL) == 0,
                     "A new object converted time before receiving synchronization");
        require_true(raop_ntp_convert_local_time(clock, 1000000000ULL) == 0,
                     "A new object converted time before receiving synchronization");
        require_true(raop_ntp_get_video_arrival_offset(clock) == 0,
                     "A new object has a stale video-arrival offset");
        raop_ntp_destroy(clock);
    });
    report.check("fixed-offset-normalizes-client-origin-and-preserves-spacing", [] {
        raop_ntp_t* clock = create_clock(NTP);
        uint64_t before = raop_ntp_get_local_time();
        const q32_32_t client_origin = (12345ULL << 32) | 0x80000000ULL;
        q32_32_t adjusted = raop_ntp_adjust_remote_timestamp_offset(clock, client_origin, false);
        uint64_t normalized = raop_ntp_timestamp_to_nano_seconds(clock, adjusted);
        uint64_t after = raop_ntp_get_local_time();
        require_true(normalized >= before - 1000 && normalized <= after + 1000,
                     "The client's arbitrary origin was not normalized to the local wall-clock domain");
        q32_32_t next = raop_ntp_adjust_remote_timestamp_offset(clock, client_origin + (1ULL << 32), false);
        require_true(raop_ntp_timestamp_to_nano_seconds(clock, next) - normalized == 1000000000ULL,
                     "Normal client timestamp spacing changed under the fixed offset");
        raop_ntp_destroy(clock);
    });
    report.check("new-session-relearns-an-unrelated-client-origin", [] {
        raop_ntp_t* first = create_clock(NTP);
        const q32_32_t first_origin = (12345ULL << 32);
        (void)raop_ntp_adjust_remote_timestamp_offset(first, first_origin, false);
        uint64_t nonzero = 123456789ULL;
        raop_ntp_set_video_arrival_offset(first, &nonzero);
        raop_ntp_destroy(first);
        raop_ntp_t* second = create_clock(NTP);
        require_true(raop_ntp_get_video_arrival_offset(second) == 0,
                     "The preceding session's video offset leaked into the new session");
        uint64_t before = raop_ntp_get_local_time();
        q32_32_t adjusted = raop_ntp_adjust_remote_timestamp_offset(second, (456789ULL << 32), false);
        uint64_t normalized = raop_ntp_timestamp_to_nano_seconds(second, adjusted);
        uint64_t after = raop_ntp_get_local_time();
        require_true(normalized >= before - 1000 && normalized <= after + 1000,
                     "A new session retained the previous client's time origin");
        raop_ntp_stop(second);
        raop_ntp_stop(second);
        raop_ntp_destroy(second);
    });
    report.check("audio-and-video-epoch-calls-share-one-reference", [] {
        for (bool video_first : {false, true}) {
            ClockObject owner(NTP);
            raop_ntp_t* clock = owner.get();
            const q32_32_t audio_raw = (1234567ULL << 32) | 0x40000000ULL;
            const q32_32_t video_raw = audio_raw - (2208988800ULL << 32);
            q32_32_t audio_adjusted, video_adjusted;
            if (video_first) {
                video_adjusted = raop_ntp_adjust_remote_timestamp_offset(clock, video_raw, true);
                audio_adjusted = raop_ntp_adjust_remote_timestamp_offset(clock, audio_raw, false);
            } else {
                audio_adjusted = raop_ntp_adjust_remote_timestamp_offset(clock, audio_raw, false);
                video_adjusted = raop_ntp_adjust_remote_timestamp_offset(clock, video_raw, true);
            }
            require_true(audio_adjusted == video_adjusted,
                         "Audio/video epoch flags did not map equivalent timestamps to the same domain");
            require_true(raop_ntp_timestamp_to_nano_seconds(clock, audio_adjusted)
                         == raop_ntp_timestamp_to_nano_seconds(clock, video_adjusted),
                         "Audio/video nanosecond conversion diverged");
        }
    });
    report.check("fixed-client-reference-preserves-spacing-across-q32-wrap", [] {
        ClockObject owner(NTP);
        auto* clock = owner.get();
        const q32_32_t raw = UINT64_MAX - (1ULL << 31);
        q32_32_t first = raop_ntp_adjust_remote_timestamp_offset(clock, raw, false);
        q32_32_t next = raop_ntp_adjust_remote_timestamp_offset(clock, raw + (1ULL << 32), false);
        require_true(raop_ntp_timestamp_to_nano_seconds(clock, next)
                     - raop_ntp_timestamp_to_nano_seconds(clock, first) == 1000000000ULL,
                     "A wrapped arbitrary client origin broke one-second timestamp spacing");
    });
    report.check("windows.no-kernel-fallback-data-address-nanoseconds-and-close", [] {
        ClockObject clock(NTP);
        PrivateLoopbackSocket receiver, sender;
        sockaddr_in destination = receiver.address();
        FallbackSession owner(clock.get(), receiver);
        auto* session = owner.get();
        require_true(session->qpc_frequency > 0, "Windows QPC initialization was not applied to the session");
        send_fixed_datagram(sender, destination);
        char data[4]{};
        sockaddr_in source{};
        int source_length = sizeof(source);
        uint64_t kernel = 777, clock_time = 777;
        uint64_t before = raop_ntp_get_local_time();
        ssize_t received = kernel_timestamp_session_recv(session, data, sizeof(data), &source, &source_length,
                                                         &kernel, &clock_time);
        uint64_t after = raop_ntp_get_local_time();
        require_true(received == sizeof(data) && std::memcmp(data, "TEST", sizeof(data)) == 0,
                     "The production fallback changed the fixed test datagram");
        require_true(source_length == sizeof(sockaddr_in) && source.sin_addr.s_addr == htonl(INADDR_LOOPBACK)
                     && ntohs(source.sin_port) == sender.port(),
                     "The production fallback returned the wrong source address or address length");
        require_true(kernel == 0 && clock_time >= before && clock_time <= after,
                     "Fallback timestamp is not in the production local nanosecond domain");
        ++fallback_datagrams;
        send_fixed_datagram(sender, destination);
        require_true(kernel_timestamp_session_recv(session, data, sizeof(data), nullptr, nullptr,
                                                  nullptr, nullptr) == sizeof(data),
                     "Fallback with absent timestamp/address outputs failed");
        ++fallback_datagrams;
        send_fixed_datagram(sender, destination);
        clock_time = 0;
        require_true(kernel_timestamp_session_recv(session, data, sizeof(data), nullptr, nullptr,
                                                  nullptr, &clock_time) == sizeof(data) && clock_time > 0,
                     "Clock-only fallback output failed");
        ++fallback_datagrams;
        int owned_handle = session->sock_fd;
        owner.close();
        require_true(private_socket_closed(owned_handle), "Timestamp session destruction did not close its owned socket");
    });
    report.check("accepted-loopback-samples-enable-conversion-apis", [] {
        PrivateTimingResponder responder;
        ClockObject owner(NTP, responder.port());
        auto* clock = owner.get();
        unsigned short local_port = 0;
        raop_ntp_start(clock, &local_port);
        require_true(local_port != 0 && raop_ntp_get_port(clock) == local_port,
                     "The production timing worker did not acquire a private dynamic port");
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
        while ((raop_ntp_get_remote_time(clock) == 0 || timing_responses.load() < 3)
               && std::chrono::steady_clock::now() < deadline)
            std::this_thread::sleep_for(std::chrono::milliseconds(5));
        require_true(raop_ntp_get_remote_time(clock) != 0,
                     "Valid real timing responses never enabled the public remote clock API");
        require_true(timing_responses.load() >= 3 && full_client_refs.load() >= 2,
                     "The real timing worker did not echo the full 64-bit prior client timestamp");
        int64_t offset = 0;
        double delay = 0;
        raop_ntp_get_sync_params(clock, &offset, &delay);
        require_true(delay > 0 && delay < 1.0, "Accepted loopback timing delay is not expressed in seconds");
        uint64_t local = raop_ntp_get_local_time();
        uint64_t remote = raop_ntp_convert_local_time(clock, local);
        require_true(remote != 0, "Accepted timing samples did not enable local-to-remote conversion");
        require_true(raop_ntp_convert_remote_time(clock, remote) == local,
                     "The common offset is not reversible through the real public APIs");
        require_true(static_cast<int64_t>(remote - local) == offset,
                     "Public conversion did not apply the reported nanosecond offset");
        unsigned short repeated_port = 0;
        raop_ntp_start(clock, &repeated_port);
        require_true(repeated_port == local_port, "Repeated start disturbed the healthy timing service");
        raop_ntp_stop(clock);
        raop_ntp_stop(clock);
        require_true(responder.healthy(), "The private timing responder observed a socket error");
    });
    report.check("media-clock.valid-ntp-local-time-takes-priority-for-both-streams", [] {
        media_clock_reset();
        require_true(media_clock_map(1000, 9000, 50000) == 9000,
                     "A valid local audio timestamp lost priority to receive-time fallback");
        require_true(media_clock_map(2000, 10000, 80000) == 10000,
                     "A valid local video timestamp lost priority to receive-time fallback");
        media_clock_adjust(250);
        require_true(media_clock_map(3000, 11000, 0) == 11250
                     && media_clock_map(4000, 12000, 0) == 12250,
                     "Audio/video did not share the presentation adjustment");
    });
    report.check("media-clock.fallback-reference-reused-between-streams", [] {
        media_clock_reset();
        require_true(media_clock_map(1000, 0, 9000) == 9000, "Fallback reference was not established");
        require_true(media_clock_map(1500, 0, 40000) == 9500,
                     "Fallback mapping was rebuilt for a later stream/callback");
        media_clock_adjust(300);
        require_true(media_clock_map(2000, 0, 50000) == 10300,
                     "Fallback mapping ignored the shared presentation adjustment");
        require_true(media_clock_map(2500, 20000, 60000) == 20300,
                     "Validated NTP did not replace receive-time fallback");
    });
    report.check("media-clock.video-retry-does-not-reapply-adjustment-to-already-mapped-time", [] {
        media_clock_reset();
        const uint64_t original_remote = 5000, original_local = 9000;
        require_true(media_clock_map(original_remote, original_local, 0) == 9000, "Initial map failed");
        media_clock_adjust(600);
        for (int retry = 0; retry < 10; ++retry)
            require_true(media_clock_map(original_remote, original_local, 0) == 9600,
                         "Mapping the original source timestamp accumulated the adjustment on retry");
        require_true(media_clock_map(6000, 10000, 0) == 10600,
                     "The other stream did not receive the same correction");
    });
    report.check("media-clock.reset-clears-fallback-and-adjustment", [] {
        media_clock_reset();
        media_clock_adjust(700);
        (void)media_clock_map(1000, 0, 9000);
        media_clock_reset();
        require_true(media_clock_map(100, 0, 5000) == 5000,
                     "A new session inherited a previous fallback/reference adjustment");
        media_clock_reset();
        const uint64_t remote = UINT64_MAX - 100;
        require_true(media_clock_map(remote, 0, 100) == 100
                     && media_clock_map(remote + 50, 0, 4000) == 150,
                     "Unsigned fallback offset did not preserve signed-origin spacing");
        media_clock_reset();
    });
    report.check("local-wall-clock-samples-progress-without-system-clock-change", [] {
        uint64_t previous = raop_ntp_get_local_time();
        uint64_t first = previous;
        for (int sample = 0; sample < 20; ++sample) {
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
            uint64_t current = raop_ntp_get_local_time();
            require_true(current >= previous, "A local timestamp went backwards during this short fixture");
            previous = current;
        }
        require_true(previous > first, "The production local clock did not advance");
    });
    report.check("ntp.no-native-error-events", [] {
        std::lock_guard<std::mutex> lock(log_mutex);
        require_true(error_logs.empty(), error_logs.empty() ? "" : error_logs.front());
    });
    logger_destroy(fixture_logger);
    WSACleanup();
    std::ostringstream observed;
    observed << "{\"nativeErrorLogs\": " << error_logs.size()
             << ", \"ntpThreadsStarted\": true, \"socketsBound\": true,"
             << " \"privateLoopbackOnly\": true, \"fallbackDatagrams\": " << fallback_datagrams.load()
             << ", \"timingResponses\": " << timing_responses.load()
             << ", \"full64BitClientRefs\": " << full_client_refs.load()
             << ", \"productionBindCallsWrapped\": " << fixture_bindings.load()
             << ", \"productionBindRequestsRejected\": " << fixture_binding_rejections.load() << ','
             << " \"systemClockOrAdapterSettingsChanged\": false}";
    report.write(argv[1], "production NTP exchange/conversion and shared media clock", observed.str(), {
        "All sockets bind only self-owned 127.0.0.1 dynamic UDP ports. A linker wrapper restricts production binding; production NTP exchange and conversion code run unchanged.",
        "Windows fallback is exercised by removing optional capability on this fixture's own session. Real adapter kernel timestamp capability and physical LAN behavior are not measured.",
        "The production API is a wall clock. Short-run nondecreasing samples are not a guarantee across operating-system clock adjustments.",
        "Shared media-clock fixed samples test the actual helper; runtime integration at the iPhone callbacks and physical A/V synchronization still require a real client.",
        "New-object reset is verified; in-place RTP audio restart state is implemented in another production module and is not claimed here."
    });
    return report.passed() ? 0 : 1;
}
