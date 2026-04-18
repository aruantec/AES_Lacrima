#include "pch.h"
#include <sddl.h>

static void FileDebugLog(char const* message)
{
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

extern "C" void* CreateCaptureSessionInternal(HWND targetHwnd, HWND presentationHwnd);
extern bool InitializeDirectHookCapture(DWORD pid);

static HANDLE CreateInjectionReadyEvent(std::wstring const& name)
{
    SECURITY_ATTRIBUTES sa{};
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:(A;;GA;;;WD)",
            SDDL_REVISION_1,
            &sd,
            nullptr))
    {
        sa.nLength = sizeof(sa);
        sa.bInheritHandle = FALSE;
        sa.lpSecurityDescriptor = sd;
    }

    HANDLE readyEvent = CreateEventW(sd ? &sa : nullptr, TRUE, FALSE, name.c_str());
    if (sd)
        LocalFree(sd);

    return readyEvent;
}

static DWORD WINAPI InjectionWorkerThread(LPVOID lpParameter)
{
    DebugLog("[WGC_NATIVE] InjectionWorkerThread started");
    DWORD pid = GetCurrentProcessId();
    auto requestName = MakeInjectionRequestEventName(pid);
    DebugLog("[WGC_NATIVE] Waiting for injector request event");
    HANDLE requestEvent = OpenEventW(SYNCHRONIZE, FALSE, requestName.c_str());
    if (requestEvent)
    {
        DebugLog("[WGC_NATIVE] Injector request event opened, waiting for host signal");
        DWORD waitResult = WaitForSingleObject(requestEvent, 10000);
        if (waitResult == WAIT_OBJECT_0)
        {
            DebugLog("[WGC_NATIVE] Injector request event signaled");
        }
        else
        {
            char buf[128];
            sprintf_s(buf, "[WGC_NATIVE] WaitForSingleObject(requestEvent) failed result=%lu", waitResult);
            DebugLog(buf);
            CloseHandle(requestEvent);
            return 0;
        }
        CloseHandle(requestEvent);
    }
    else
    {
        DWORD error = GetLastError();
        char buf[128];
        sprintf_s(buf, "[WGC_NATIVE] Injector request event not found, error=%lu", error);
        DebugLog(buf);
        return 0;
    }

    if (InitializeDirectHookCapture(pid))
    {
        DebugLog("[WGC_NATIVE] Direct D3D11 hook capture initialized, signaling ready event");
        auto readyName = MakeInjectionReadyEventName(pid);
        HANDLE readyEvent = CreateInjectionReadyEvent(readyName);
        if (readyEvent)
        {
            SetEvent(readyEvent);
            CloseHandle(readyEvent);
            DebugLog("[WGC_NATIVE] Ready event signaled");
        }
        else
        {
            DWORD error = GetLastError();
            char buf[128];
            sprintf_s(buf, "[WGC_NATIVE] CreateEventW(readyEvent) failed, error=%lu", error);
            DebugLog(buf);
        }
        return 0;
    }

    DebugLog("[WGC_NATIVE] Direct hook failed, aborting injected capture");
    return 0;
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
        {
            HANDLE threadHandle = CreateThread(nullptr, 0, InjectionWorkerThread, nullptr, 0, nullptr);
            if (threadHandle)
            {
                DebugLog("[WGC_NATIVE] Injection worker thread created");
                CloseHandle(threadHandle);
            }
            else
            {
                DWORD error = GetLastError();
                DebugLog("[WGC_NATIVE] Failed to create injection worker thread");
                char buf[128];
                sprintf_s(buf, "[WGC_NATIVE] CreateThread error=%lu", error);
                DebugLog(buf);
            }
        }
        break;
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

