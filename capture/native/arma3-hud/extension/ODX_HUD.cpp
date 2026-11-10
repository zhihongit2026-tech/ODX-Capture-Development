#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <algorithm>
#include <cstring>
#include <string>

namespace
{
    constexpr char PipeName[] = R"(\\.\pipe\ODX_HUD_BRIDGE)";
    constexpr DWORD ConnectTimeoutMs = 75;
    constexpr size_t MaxResponse = 4096;

    void CopyOutput(char* output, unsigned int outputSize, const std::string& value)
    {
        if (output == nullptr || outputSize == 0) return;
        const size_t count = (std::min)(static_cast<size_t>(outputSize - 1), value.size());
        std::memcpy(output, value.data(), count);
        output[count] = '\0';
    }

    std::string Exchange(const char* function, const char* payload)
    {
        if (function == nullptr) return "BAD_REQUEST";
        if (!WaitNamedPipeA(PipeName, ConnectTimeoutMs)) return "NO_BRIDGE";

        HANDLE pipe = CreateFileA(PipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) return "NO_BRIDGE";

        std::string request(function);
        request.push_back('\n');
        if (payload != nullptr) request.append(payload);
        request.push_back('\n');

        DWORD written = 0;
        const BOOL sent = WriteFile(pipe, request.data(), static_cast<DWORD>(request.size()), &written, nullptr);
        if (!sent || written != request.size())
        {
            CloseHandle(pipe);
            return "PIPE_WRITE_ERROR";
        }

        std::string response;
        response.reserve(64);
        char buffer[256];
        DWORD read = 0;
        while (response.size() < MaxResponse)
        {
            if (!ReadFile(pipe, buffer, sizeof(buffer), &read, nullptr) || read == 0) break;
            response.append(buffer, read);
            const auto newline = response.find('\n');
            if (newline != std::string::npos)
            {
                response.resize(newline);
                if (!response.empty() && response.back() == '\\r') response.pop_back();
                break;
            }
        }
        CloseHandle(pipe);
        if (response.empty()) return "PIPE_EMPTY_RESPONSE";
        return response;
    }
}

extern "C"
{
    __declspec(dllexport) void __stdcall RVExtensionVersion(char* output, unsigned int outputSize)
    {
        CopyOutput(output, outputSize, "0.3.4");
    }

    __declspec(dllexport) void __stdcall RVExtension(char* output, int outputSize, const char* function)
    {
        const std::string result = Exchange(function, "");
        CopyOutput(output, outputSize > 0 ? static_cast<unsigned int>(outputSize) : 0, result);
    }

    __declspec(dllexport) int __stdcall RVExtensionArgs(char* output, unsigned int outputSize,
        const char* function, const char** argv, unsigned int argc)
    {
        const char* payload = (argc > 0 && argv != nullptr && argv[0] != nullptr) ? argv[0] : "";
        const std::string result = Exchange(function, payload);
        CopyOutput(output, outputSize, result);
        return result == "NO_BRIDGE" ? 1 : 0;
    }
}
