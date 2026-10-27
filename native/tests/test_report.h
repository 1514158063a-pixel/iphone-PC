#ifndef CLOCK_AUDIO_TEST_REPORT_H
#define CLOCK_AUDIO_TEST_REPORT_H

#include <chrono>
#include <fstream>
#include <functional>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

inline std::string json_string(const std::string& value) {
    std::ostringstream stream;
    stream << '"';
    for (unsigned char character : value) {
        switch (character) {
        case '"': stream << "\\\""; break;
        case '\\': stream << "\\\\"; break;
        case '\n': stream << "\\n"; break;
        case '\r': stream << "\\r"; break;
        case '\t': stream << "\\t"; break;
        default:
            if (character < 32) stream << '?';
            else stream << character;
        }
    }
    stream << '"';
    return stream.str();
}

inline void require_true(bool condition, const std::string& message) {
    if (!condition) throw std::runtime_error(message);
}

class TestReport {
    struct Result { std::string name, message; bool passed; long long milliseconds; };
    std::vector<Result> results;
public:
    void check(const std::string& name, const std::function<void()>& body) {
        auto began = std::chrono::steady_clock::now();
        Result result{name, "Passed", true, 0};
        try { body(); }
        catch (const std::exception& error) { result.passed = false; result.message = error.what(); }
        catch (...) { result.passed = false; result.message = "Unknown test exception"; }
        result.milliseconds = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - began).count();
        results.push_back(result);
        std::cout << (result.passed ? "PASS " : "FAIL ") << name << ": " << result.message << '\n';
    }
    bool passed() const {
        for (const auto& result : results) if (!result.passed) return false;
        return !results.empty();
    }
    void write(const std::string& path, const std::string& suite,
               const std::string& observations, const std::vector<std::string>& limits) const {
        std::ofstream output(path, std::ios::binary | std::ios::trunc);
        if (!output) throw std::runtime_error("Cannot write test report: " + path);
        output << "{\n  \"suite\": " << json_string(suite)
               << ",\n  \"passed\": " << (passed() ? "true" : "false")
               << ",\n  \"checkCount\": " << results.size()
               << ",\n  \"productionSourceUsed\": true,\n  \"airplayServiceStarted\": false,"
               << "\n  \"realReceiverTouched\": false,\n  \"tests\": [\n";
        for (size_t index = 0; index < results.size(); ++index) {
            const auto& result = results[index];
            output << "    {\"name\": " << json_string(result.name)
                   << ", \"passed\": " << (result.passed ? "true" : "false")
                   << ", \"milliseconds\": " << result.milliseconds
                   << ", \"message\": " << json_string(result.message) << "}"
                   << (index + 1 < results.size() ? "," : "") << '\n';
        }
        output << "  ],\n  \"observations\": " << observations << ",\n  \"limits\": [";
        for (size_t index = 0; index < limits.size(); ++index)
            output << (index ? ", " : "") << json_string(limits[index]);
        output << "]\n}\n";
    }
};

#endif
