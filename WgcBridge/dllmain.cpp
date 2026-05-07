#include "pch.h"
#include <sddl.h>

static void FileDebugLog(char const* message)
{
    char logPath[MAX_PATH];
    DWORD logLen = GetEnvironmentVariableA("AES_LACRIMA_LOG_FILE", logPath, MAX_PATH);
    if (logLen > 0 && logLen < MAX_PATH)
    {
        HANDLE logFile = CreateFileA(logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (logFile != INVALID_HANDLE_VALUE)
        {
            SetFilePointer(logFile, 0, nullptr, FILE_END);
            DWORD written = 0;
            WriteFile(logFile, message, static_cast<DWORD>(strlen(message)), &written, nullptr);
            WriteFile(logFile, "\r\n", 2, &written, nullptr);
            CloseHandle(logFile);
            return;
        }
    }

    char tempPath[MAX_PATH];
    DWORD len = GetEnvironmentVariableA("TEMP", tempPath, MAX_PATH);
    if (len == 0 || len >= MAX_PATH)
        return;

    std::string filePath(tempPath);
    filePath += "\\aes_injection_";
    filePath += std::to_string(GetCurrentProcessId());
    filePath += ".log";

    HANDLE file = CreateFileA(filePath.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return;

    SetFilePointer(file, 0, nullptr, FILE_END);
    DWORD written = 0;
    WriteFile(file, message, static_cast<DWORD>(strlen(message)), &written, nullptr);
    WriteFile(file, "\r\n", 2, &written, nullptr);
    CloseHandle(file);
}

static void DebugLog(char const* message)
{
    OutputDebugStringA(message);
    FileDebugLog(message);
}

static std::wstring MakeInjectionRequestEventName(DWORD pid)
{
    return std::wstring(L"Local\\AES_Lacrima_Injector_Request_") + std::to_wstring(pid);
}

static std::wstring MakeInjectionReadyEventName(DWORD pid)
{
    return std::wstring(L"Local\\AES_Lacrima_Injector_Ready_") + std::to_wstring(pid);
}

BOOL APIENTRY DllMain( HMODULE hModule,
                       DWORD  ul_reason_for_call,
                       LPVOID lpReserved
                     )
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(hModule);
        break;
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

