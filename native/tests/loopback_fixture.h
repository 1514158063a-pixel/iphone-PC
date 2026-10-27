#ifndef CLOCK_AUDIO_LOOPBACK_FIXTURE_H
#define CLOCK_AUDIO_LOOPBACK_FIXTURE_H

#include <winsock2.h>
#include <ws2tcpip.h>
#include <atomic>
#include <climits>
#include "test_report.h"

// The sole replacement in network fixtures is socket binding: every production
// request must ask for IPv4 UDP port 0, and is restricted to 127.0.0.1. This does
// not replace the production thread, packet parsing, timestamp or stop logic.
inline std::atomic<unsigned int> fixture_bindings{0};
inline std::atomic<unsigned int> fixture_binding_rejections{0};
inline SOCKET bind_private_loopback(unsigned short* assigned_port) {
    SOCKET socket_handle = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket_handle == INVALID_SOCKET) return INVALID_SOCKET;
    if (socket_handle > INT_MAX) {
        closesocket(socket_handle);
        WSASetLastError(WSAEMFILE);
        return INVALID_SOCKET;
    }
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = 0;
    if (bind(socket_handle, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR) {
        closesocket(socket_handle);
        return INVALID_SOCKET;
    }
    int length = sizeof(address);
    if (getsockname(socket_handle, reinterpret_cast<sockaddr*>(&address), &length) == SOCKET_ERROR) {
        closesocket(socket_handle);
        return INVALID_SOCKET;
    }
    if (assigned_port) *assigned_port = ntohs(address.sin_port);
    DWORD timeout_ms = 200;
    if (setsockopt(socket_handle, SOL_SOCKET, SO_RCVTIMEO,
                   reinterpret_cast<const char*>(&timeout_ms), sizeof(timeout_ms)) == SOCKET_ERROR) {
        closesocket(socket_handle);
        return INVALID_SOCKET;
    }
    return socket_handle;
}

extern "C" int __wrap_netutils_init_socket(unsigned short* port, int use_ipv6, int use_udp) {
    if (!port || *port != 0 || use_ipv6 || !use_udp) {
        ++fixture_binding_rejections;
        WSASetLastError(WSAEINVAL);
        return -1;
    }
    SOCKET socket_handle = bind_private_loopback(port);
    if (socket_handle != INVALID_SOCKET) ++fixture_bindings;
    return socket_handle == INVALID_SOCKET ? -1 : static_cast<int>(socket_handle);
}

class PrivateLoopbackSocket {
    unsigned short assigned_port = 0;
    SOCKET socket_handle = INVALID_SOCKET;
public:
    PrivateLoopbackSocket() : socket_handle(bind_private_loopback(&assigned_port)) {
        require_true(socket_handle != INVALID_SOCKET && assigned_port != 0,
                     "Cannot bind the private loopback socket to a dynamic port");
    }
    ~PrivateLoopbackSocket() { if (socket_handle != INVALID_SOCKET) closesocket(socket_handle); }
    PrivateLoopbackSocket(const PrivateLoopbackSocket&) = delete;
    SOCKET get() const { return socket_handle; }
    unsigned short port() const { return assigned_port; }
    sockaddr_in address() const {
        sockaddr_in endpoint{};
        endpoint.sin_family = AF_INET;
        endpoint.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        endpoint.sin_port = htons(assigned_port);
        return endpoint;
    }
    SOCKET release() { SOCKET result = socket_handle; socket_handle = INVALID_SOCKET; return result; }
};

inline bool private_socket_closed(int handle) {
    sockaddr_in address{};
    int length = sizeof(address);
    int result = getsockname(static_cast<SOCKET>(handle), reinterpret_cast<sockaddr*>(&address), &length);
    return result == SOCKET_ERROR && WSAGetLastError() == WSAENOTSOCK;
}

#endif
