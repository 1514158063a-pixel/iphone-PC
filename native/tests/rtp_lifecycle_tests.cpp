#include <winsock2.h>
#include <atomic>
#include <chrono>
#include <cstring>
#include <functional>
#include <mutex>
#include <thread>
#include <vector>
extern "C" {
#include "raop_rtp.h"
}
#include "loopback_fixture.h"

namespace {
logger_t* fixture_logger = nullptr;
std::mutex log_mutex;
std::vector<std::string> error_logs;
std::atomic<unsigned int> unexpected_media_callbacks{0};
unsigned int started_sessions = 0;
unsigned int verified_closed_sockets = 0;
unsigned int orderly_unjoined_restarts = 0;
unsigned int control_sync_datagrams = 0;

void log_callback(void*, int level, const char* message) {
    if (level <= LOGGER_ERR) {
        std::lock_guard<std::mutex> lock(log_mutex);
        error_logs.emplace_back(message ? message : "");
    }
}
void audio_process(void*, raop_ntp_t*, audio_decode_struct*) { ++unexpected_media_callbacks; }
void wait_for(const std::function<bool()>& predicate, const std::string& message) {
    auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    while (std::chrono::steady_clock::now() < deadline) {
        if (predicate()) return;
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    require_true(predicate(), message);
}
class StreamObject {
    raop_ntp_t* clock = nullptr;
    raop_rtp_t* stream = nullptr;
public:
    StreamObject() {
        raop_callbacks_t callbacks{};
        callbacks.audio_process = audio_process;
        timing_protocol_t protocol = NTP;
        clock = raop_ntp_init(fixture_logger, &callbacks, "127.0.0.1", 4, 0, &protocol);
        require_true(clock != nullptr, "Cannot create the private timing object");
        const unsigned char fixed_zero_key[16]{};
        const unsigned char fixed_zero_iv[16]{};
        stream = raop_rtp_init(fixture_logger, &callbacks, clock, "127.0.0.1", 4,
                               fixed_zero_key, fixed_zero_iv);
        if (!stream) {
            raop_ntp_destroy(clock);
            clock = nullptr;
            throw std::runtime_error("Cannot create the actual production RTP object");
        }
    }
    ~StreamObject() {
        if (stream) raop_rtp_destroy(stream);
        if (clock) raop_ntp_destroy(clock);
    }
    raop_rtp_t* get() const { return stream; }
    void destroy() { raop_rtp_destroy(stream); stream = nullptr; }
};
raop_rtp_test_state state_of(raop_rtp_t* stream) {
    raop_rtp_test_state state{};
    raop_rtp_test_get_state(stream, &state);
    return state;
}
void assert_loopback_socket(int handle, unsigned short port) {
    sockaddr_in endpoint{};
    int length = sizeof(endpoint);
    require_true(getsockname(static_cast<SOCKET>(handle), reinterpret_cast<sockaddr*>(&endpoint), &length) == 0,
                 "A production RTP socket is not open");
    require_true(endpoint.sin_addr.s_addr == htonl(INADDR_LOOPBACK) && ntohs(endpoint.sin_port) == port,
                 "A fixture RTP socket escaped the private loopback endpoint");
}
void assert_closed(int handle) {
    require_true(private_socket_closed(handle), "Production stop/destroy leaked an owned UDP socket");
    ++verified_closed_sockets;
}
void assert_clean_stopped(raop_rtp_t* stream) {
    auto state = state_of(stream);
    require_true(!state.running && state.joined == 1 && state.csock == -1 && state.dsock == -1
                 && !state.has_timestamp_session, "The stopped production stream retains thread/socket ownership");
    require_true(!state.initial_sync && state.rtp_sync == 0 && state.client_ntp_sync == 0 && state.flush == -42,
                 "The stopped stream retains previous synchronization or pending flush state");
}
raop_rtp_test_state start(raop_rtp_t* stream, unsigned short remote_control_port,
                          unsigned short* control_port = nullptr, unsigned short* data_port = nullptr) {
    unsigned short control = 0, data = 0;
    unsigned char type = 8;
    unsigned int rate = 44100;
    raop_rtp_start_audio(stream, &remote_control_port, &control, &data, &type, &rate);
    wait_for([&] { return raop_rtp_is_running(stream) != 0; }, "The real RTP worker did not start");
    auto state = state_of(stream);
    require_true(control && data && control != data && state.joined == 0 && state.has_timestamp_session,
                 "The running production stream did not acquire both data/control resources");
    assert_loopback_socket(state.csock, control);
    assert_loopback_socket(state.dsock, data);
    if (control_port) *control_port = control;
    if (data_port) *data_port = data;
    ++started_sessions;
    return state;
}
void put32(unsigned char* destination, uint32_t value) {
    for (int index = 3; index >= 0; --index) { destination[index] = static_cast<unsigned char>(value); value >>= 8; }
}
void send_control_sync(PrivateLoopbackSocket& sender, unsigned short destination_port) {
    unsigned char packet[20]{};
    packet[0] = 0x90;
    packet[1] = 0xd4;
    packet[3] = 4;
    put32(packet + 4, 123456);
    put32(packet + 8, 2208988800U + 1234567U);
    put32(packet + 12, 0x40000000U);
    put32(packet + 16, 123456 + 7497);
    auto destination = sender.address();
    destination.sin_port = htons(destination_port);
    require_true(sendto(sender.get(), reinterpret_cast<const char*>(packet), sizeof(packet), 0,
                    reinterpret_cast<const sockaddr*>(&destination), sizeof(destination)) == sizeof(packet),
                 "A valid private control timestamp could not be sent");
    ++control_sync_datagrams;
}
}

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    TestReport report;
    WSADATA winsock;
    if (WSAStartup(MAKEWORD(2, 2), &winsock) != 0) return 3;
    fixture_logger = logger_init();
    logger_set_callback(fixture_logger, log_callback, nullptr);
    logger_set_level(fixture_logger, LOGGER_INFO);
    raop_ntp_global_init();

    report.check("rtp.initial-object-has-no-thread-socket-or-sync", [] {
        StreamObject owner;
        assert_clean_stopped(owner.get());
    });
    report.check("rtp.healthy-duplicate-start-reuses-both-ports-and-thread", [] {
        PrivateLoopbackSocket peer;
        StreamObject owner;
        auto first = start(owner.get(), peer.port());
        auto repeated = start(owner.get(), peer.port());
        require_true(repeated.csock == first.csock && repeated.dsock == first.dsock,
                     "Healthy duplicate SETUP recreated the RTP sockets");
        raop_rtp_stop(owner.get());
        assert_closed(first.csock);
        assert_closed(first.dsock);
        raop_rtp_stop(owner.get());
        assert_clean_stopped(owner.get());
    });
    report.check("rtp.ended-unjoined-worker-is-joined-and-restarted-with-fresh-mapping", [] {
        PrivateLoopbackSocket peer;
        StreamObject owner;
        unsigned short control = 0;
        auto old = start(owner.get(), peer.port(), &control);
        send_control_sync(peer, control);
        wait_for([&] {
            auto state = state_of(owner.get());
            return state.initial_sync && state.rtp_sync == 123456 && state.client_ntp_sync != 0;
        }, "The production control path did not establish the valid timestamp mapping");
        raop_rtp_test_request_exit(owner.get());
        wait_for([&] { return !raop_rtp_is_running(owner.get()); },
                 "The production worker did not finish its orderly private exit request");
        auto ended = state_of(owner.get());
        require_true(ended.joined == 0 && ended.csock == old.csock && ended.dsock == old.dsock,
                     "The fixture did not reach a completed but unjoined worker");
        raop_rtp_flush(owner.get(), 54321);
        require_true(state_of(owner.get()).flush == 54321, "The stopped worker did not retain the test pending flush");
        auto restarted = start(owner.get(), peer.port());
        require_true(!restarted.initial_sync && restarted.rtp_sync == 0 && restarted.client_ntp_sync == 0
                     && restarted.flush == -42, "A restarted audio stream inherited its old mapping/flush");
        ++orderly_unjoined_restarts;
        raop_rtp_stop(owner.get());
        assert_closed(restarted.csock);
        assert_closed(restarted.dsock);
        assert_clean_stopped(owner.get());
    });
    report.check("rtp.stop-start-and-concurrent-stop-ten-cycles", [] {
        PrivateLoopbackSocket peer;
        StreamObject owner;
        for (int cycle = 0; cycle < 10; ++cycle) {
            auto active = start(owner.get(), peer.port());
            std::thread first([&] { raop_rtp_stop(owner.get()); });
            std::thread second([&] { raop_rtp_stop(owner.get()); });
            first.join();
            second.join();
            assert_closed(active.csock);
            assert_closed(active.dsock);
            assert_clean_stopped(owner.get());
        }
    });
    report.check("rtp.destroy-running-stream-releases-owned-resources", [] {
        PrivateLoopbackSocket peer;
        for (int cycle = 0; cycle < 3; ++cycle) {
            StreamObject owner;
            auto active = start(owner.get(), peer.port());
            owner.destroy();
            assert_closed(active.csock);
            assert_closed(active.dsock);
        }
        raop_rtp_destroy(nullptr);
    });
    report.check("rtp.no-media-callbacks-or-native-errors", [] {
        require_true(unexpected_media_callbacks.load() == 0, "The control-only fixture unexpectedly rendered media");
        require_true(fixture_binding_rejections.load() == 0, "Production requested an endpoint outside the fixture contract");
        std::lock_guard<std::mutex> lock(log_mutex);
        require_true(error_logs.empty(), error_logs.empty() ? "" : error_logs.front());
    });
    logger_destroy(fixture_logger);
    WSACleanup();
    std::ostringstream observed;
    observed << "{\"startCalls\": " << started_sessions
             << ", \"verifiedClosedSockets\": " << verified_closed_sockets
             << ", \"completedUnjoinedRestarts\": " << orderly_unjoined_restarts
             << ", \"controlTimestampDatagrams\": " << control_sync_datagrams
             << ", \"productionBindCallsWrapped\": " << fixture_bindings.load()
             << ", \"productionBindRequestsRejected\": " << fixture_binding_rejections.load()
             << ", \"nativeErrorLogs\": " << error_logs.size()
             << ", \"mediaPacketsSent\": false, \"privateLoopbackOnly\": true}";
    report.write(argv[1], "production RTP thread/socket/sync lifecycle", observed.str(), {
        "Only binding is wrapped to self-owned 127.0.0.1 UDP port 0. The production start, stop, join, parsing, reset and destroy code is used.",
        "The test-only request-exit flag asks the actual worker to leave normally; it does not corrupt sockets or reproduce an OS/network error.",
        "One well-formed timing control packet creates a real nonzero synchronization mapping; no encrypted media is supplied or decoded.",
        "These checks do not demonstrate long-pause iPhone behavior, audible recovery, LAN reliability or A/V synchronization."
    });
    return report.passed() ? 0 : 1;
}
