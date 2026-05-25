#define _WIN32_WINNT 0x0A00
#define WINVER 0x0A00
#include <windows.h>
#include <mmdeviceapi.h>
#include <audiopolicy.h>
#include <audioclient.h>
#include <stdio.h>

#pragma comment(lib, "ole32.lib")

static void DebugLog(char const* message)
{
    OutputDebugStringA(message);

    char tempPath[MAX_PATH];
    DWORD len = GetEnvironmentVariableA("TEMP", tempPath, MAX_PATH);
    if (len == 0 || len >= MAX_PATH)
        return;

    char filePath[MAX_PATH];
    _snprintf_s(filePath, sizeof(filePath), _TRUNCATE, "%s\\aes_audio_%lu.log", tempPath, GetCurrentProcessId());

    HANDLE file = CreateFileA(filePath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return;

    SetFilePointer(file, 0, nullptr, FILE_END);
    DWORD written = 0;
    WriteFile(file, message, static_cast<DWORD>(strlen(message)), &written, nullptr);
    WriteFile(file, "\r\n", 2, &written, nullptr);
    CloseHandle(file);
}

// Finds the audio session for a given PID across ALL audio render endpoints
// and returns the master volume. Uses native WASAPI directly (not C# COM interop)
// to avoid CLR marshaling issues with FxSound-intercepted COM interfaces.
extern "C" __declspec(dllexport) HRESULT WINAPI AudioBridge_FindSessionAndGetVolume(DWORD pid, float* volume)
{
    if (!volume)
        return E_POINTER;

    *volume = 1.0f;

    char buf[256];
    _snprintf_s(buf, sizeof(buf), _TRUNCATE, "[AudioBridge] FindSessionAndGetVolume: PID=%lu\n", pid);
    DebugLog(buf);

    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE)
    {
        _snprintf_s(buf, sizeof(buf), _TRUNCATE, "[AudioBridge] CoInitializeEx failed: 0x%08X\n", static_cast<unsigned>(hr));
        DebugLog(buf);
        return hr;
    }

    IMMDeviceEnumerator* pEnumerator = nullptr;
    hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER, __uuidof(IMMDeviceEnumerator), (void**)&pEnumerator);
    if (FAILED(hr) || !pEnumerator)
    {
        _snprintf_s(buf, sizeof(buf), _TRUNCATE, "[AudioBridge] CoCreateInstance MMDeviceEnumerator failed: 0x%08X\n", static_cast<unsigned>(hr));
        DebugLog(buf);
        return hr;
    }

    // Enumerate all active render endpoints
    IMMDeviceCollection* pCollection = nullptr;
    hr = pEnumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &pCollection);
    if (FAILED(hr) || !pCollection)
    {
        _snprintf_s(buf, sizeof(buf), _TRUNCATE, "[AudioBridge] EnumAudioEndpoints failed: 0x%08X\n", static_cast<unsigned>(hr));
        DebugLog(buf);
        pEnumerator->Release();
        return hr;
    }

    UINT deviceCount = 0;
    pCollection->GetCount(&deviceCount);
    _snprintf_s(buf, sizeof(buf), _TRUNCATE, "[AudioBridge] Found %u render devices\n", deviceCount);
    DebugLog(buf);

    for (UINT i = 0; i < deviceCount; i++)
    {
        IMMDevice* pDevice = nullptr;
        hr = pCollection->Item(i, &pDevice);
        if (FAILED(hr) || !pDevice)
            continue;

        // Also try the default endpoint under each role
        IMMDevice* pDefaultDevices[3] = {};
        int roles[] = { eConsole, eMultimedia, eCommunications };
        for (int r = 0; r < 3; r++)
        {
            IMMDevice* pDefault = nullptr;
            if (SUCCEEDED(pEnumerator->GetDefaultAudioEndpoint(eRender, (ERole)roles[r], &pDefault)) && pDefault)
            {
                pDefaultDevices[r] = pDefault;
            }
        }

        // Check each device: the enumerated one and each default role
        IMMDevice* devicesToCheck[4] = { pDevice, pDefaultDevices[0], pDefaultDevices[1], pDefaultDevices[2] };
        for (int d = 0; d < 4; d++)
        {
            IMMDevice* pCheckDevice = devicesToCheck[d];
            if (!pCheckDevice)
                continue;

            // Skip if this device was already checked (same pointer as pDevice or another default)
            bool alreadyChecked = false;
            for (int prev = 0; prev < d; prev++)
            {
                if (devicesToCheck[prev] == pCheckDevice)
                {
                    alreadyChecked = true;
                    break;
                }
            }
            if (alreadyChecked)
                continue;

            IAudioSessionManager2* pSessionManager = nullptr;
            hr = pCheckDevice->Activate(__uuidof(IAudioSessionManager2), CLSCTX_INPROC_SERVER, nullptr, (void**)&pSessionManager);
            if (FAILED(hr) || !pSessionManager)
            {
                _snprintf_s(buf, sizeof(buf), _TRUNCATE, "[AudioBridge] Device %u: Activate IAudioSessionManager2 failed: 0x%08X\n", i, static_cast<unsigned>(hr));
                DebugLog(buf);
                continue;
            }

            IAudioSessionEnumerator* pSessionEnum = nullptr;
            hr = pSessionManager->GetSessionEnumerator(&pSessionEnum);
            if (FAILED(hr) || !pSessionEnum)
            {
                DebugLog("[AudioBridge] GetSessionEnumerator failed\n");
                pSessionManager->Release();
                continue;
            }

            int sessionCount = 0;
            pSessionEnum->GetCount(&sessionCount);
            _snprintf_s(buf, sizeof(buf), _TRUNCATE, "[AudioBridge] Device %u: %d sessions\n", i, sessionCount);
            DebugLog(buf);

            for (int j = 0; j < sessionCount; j++)
            {
                IAudioSessionControl* pSessionCtrl = nullptr;
                hr = pSessionEnum->GetSession(j, &pSessionCtrl);
                if (FAILED(hr) || !pSessionCtrl)
                    continue;

                IAudioSessionControl2* pSessionCtrl2 = nullptr;
                hr = pSessionCtrl->QueryInterface(__uuidof(IAudioSessionControl2), (void**)&pSessionCtrl2);
                if (SUCCEEDED(hr) && pSessionCtrl2)
                {
                    DWORD sessionPid = 0;
                    pSessionCtrl2->GetProcessId(&sessionPid);
                    _snprintf_s(buf, sizeof(buf), _TRUNCATE, "[AudioBridge] Session %d: PID=%lu (target=%lu)\n", j, sessionPid, pid);

                    if (sessionPid == pid)
                    {
                        ISimpleAudioVolume* pVolume = nullptr;
                        hr = pSessionCtrl->QueryInterface(__uuidof(ISimpleAudioVolume), (void**)&pVolume);
                        if (SUCCEEDED(hr) && pVolume)
                        {
                            float vol = 1.0f;
                            pVolume->GetMasterVolume(&vol);
                            *volume = vol;
                            _snprintf_s(buf, sizeof(buf), _TRUNCATE, "[AudioBridge] Found session for PID %lu, volume=%.3f\n", pid, vol);
                            DebugLog(buf);
                            pVolume->Release();
                        }
                        pSessionCtrl2->Release();
                        pSessionCtrl->Release();
                        pSessionEnum->Release();
                        pSessionManager->Release();
                        for (int r = 0; r < 3; r++)
                            if (pDefaultDevices[r]) pDefaultDevices[r]->Release();
                        pDevice->Release();
                        pCollection->Release();
                        pEnumerator->Release();
                        return S_OK;
                    }
                    DebugLog(buf);
                    pSessionCtrl2->Release();
                }
                pSessionCtrl->Release();
            }

            pSessionEnum->Release();
            pSessionManager->Release();
        }

        // Cleanup default devices
        for (int r = 0; r < 3; r++)
            if (pDefaultDevices[r]) pDefaultDevices[r]->Release();

        pDevice->Release();
    }

    pCollection->Release();
    pEnumerator->Release();
    DebugLog("[AudioBridge] No session found for PID\n");
    return E_FAIL;
}

extern "C" __declspec(dllexport) HRESULT WINAPI AudioBridge_FindSessionAndSetVolume(DWORD pid, float volume)
{
    char buf[256];
    _snprintf_s(buf, sizeof(buf), _TRUNCATE, "[AudioBridge] FindSessionAndSetVolume: PID=%lu, volume=%.3f\n", pid, volume);
    DebugLog(buf);

    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE)
        return hr;

    IMMDeviceEnumerator* pEnumerator = nullptr;
    hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER, __uuidof(IMMDeviceEnumerator), (void**)&pEnumerator);
    if (FAILED(hr) || !pEnumerator)
        return hr;

    IMMDeviceCollection* pCollection = nullptr;
    hr = pEnumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &pCollection);
    if (FAILED(hr) || !pCollection)
    {
        pEnumerator->Release();
        return hr;
    }

    UINT deviceCount = 0;
    pCollection->GetCount(&deviceCount);

    for (UINT i = 0; i < deviceCount; i++)
    {
        IMMDevice* pDevice = nullptr;
        hr = pCollection->Item(i, &pDevice);
        if (FAILED(hr) || !pDevice)
            continue;

        IMMDevice* pDefaultDevices[3] = {};
        int roles[] = { eConsole, eMultimedia, eCommunications };
        for (int r = 0; r < 3; r++)
        {
            IMMDevice* pDefault = nullptr;
            if (SUCCEEDED(pEnumerator->GetDefaultAudioEndpoint(eRender, (ERole)roles[r], &pDefault)) && pDefault)
                pDefaultDevices[r] = pDefault;
        }

        IMMDevice* devicesToCheck[4] = { pDevice, pDefaultDevices[0], pDefaultDevices[1], pDefaultDevices[2] };
        for (int d = 0; d < 4; d++)
        {
            IMMDevice* pCheckDevice = devicesToCheck[d];
            if (!pCheckDevice)
                continue;

            bool alreadyChecked = false;
            for (int prev = 0; prev < d; prev++)
            {
                if (devicesToCheck[prev] == pCheckDevice)
                {
                    alreadyChecked = true;
                    break;
                }
            }
            if (alreadyChecked)
                continue;

            IAudioSessionManager2* pSessionManager = nullptr;
            hr = pCheckDevice->Activate(__uuidof(IAudioSessionManager2), CLSCTX_INPROC_SERVER, nullptr, (void**)&pSessionManager);
            if (FAILED(hr) || !pSessionManager)
                continue;

            IAudioSessionEnumerator* pSessionEnum = nullptr;
            hr = pSessionManager->GetSessionEnumerator(&pSessionEnum);
            if (FAILED(hr) || !pSessionEnum)
            {
                pSessionManager->Release();
                continue;
            }

            int sessionCount = 0;
            pSessionEnum->GetCount(&sessionCount);

            for (int j = 0; j < sessionCount; j++)
            {
                IAudioSessionControl* pSessionCtrl = nullptr;
                hr = pSessionEnum->GetSession(j, &pSessionCtrl);
                if (FAILED(hr) || !pSessionCtrl)
                    continue;

                IAudioSessionControl2* pSessionCtrl2 = nullptr;
                hr = pSessionCtrl->QueryInterface(__uuidof(IAudioSessionControl2), (void**)&pSessionCtrl2);
                if (SUCCEEDED(hr) && pSessionCtrl2)
                {
                    DWORD sessionPid = 0;
                    pSessionCtrl2->GetProcessId(&sessionPid);

                    if (sessionPid == pid)
                    {
                        ISimpleAudioVolume* pVolume = nullptr;
                        hr = pSessionCtrl->QueryInterface(__uuidof(ISimpleAudioVolume), (void**)&pVolume);
                        if (SUCCEEDED(hr) && pVolume)
                        {
                            float clamped = max(0.0f, min(1.0f, volume));
                            GUID guid = GUID_NULL;
                            pVolume->SetMasterVolume(clamped, &guid);
                            _snprintf_s(buf, sizeof(buf), _TRUNCATE, "[AudioBridge] Set volume for PID %lu to %.3f\n", pid, clamped);
                            DebugLog(buf);
                            pVolume->Release();
                        }
                        pSessionCtrl2->Release();
                        pSessionCtrl->Release();
                        pSessionEnum->Release();
                        pSessionManager->Release();
                        for (int r = 0; r < 3; r++)
                            if (pDefaultDevices[r]) pDefaultDevices[r]->Release();
                        pDevice->Release();
                        pCollection->Release();
                        pEnumerator->Release();
                        return S_OK;
                    }
                    pSessionCtrl2->Release();
                }
                pSessionCtrl->Release();
            }

            pSessionEnum->Release();
            pSessionManager->Release();
        }

        for (int r = 0; r < 3; r++)
            if (pDefaultDevices[r]) pDefaultDevices[r]->Release();

        pDevice->Release();
    }

    pCollection->Release();
    pEnumerator->Release();
    DebugLog("[AudioBridge] No session found for PID\n");
    return E_FAIL;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}
