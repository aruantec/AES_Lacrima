#define _WIN32_WINNT 0x0A00
#define WINVER 0x0A00
#include <windows.h>
#include <mmdeviceapi.h>
#include <audiopolicy.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <stdio.h>
#include <vector>
#include <atomic>

#pragma comment(lib, "ole32.lib")

static void CaptureDebugLog(char const* message)
{
    OutputDebugStringA(message);
}

static const WCHAR VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK_NAME[] = L"VAD\\Process_Loopback";

static const DWORD kCaptureSampleRate = 48000;
static const WORD kCaptureChannels = 2;
static const WORD kCaptureBitsPerSample = 16;

struct CaptureRingBuffer
{
    CRITICAL_SECTION cs;
    std::vector<BYTE> data;
    size_t readPos = 0;
    size_t writePos = 0;
    size_t usedBytes = 0;

    void Reset(size_t capacity)
    {
        data.assign(capacity, 0);
        readPos = writePos = usedBytes = 0;
    }

    bool Write(const BYTE* src, size_t count)
    {
        if (count == 0)
            return true;

        if (usedBytes + count > data.size())
            return false;

        size_t first = min(count, data.size() - writePos);
        memcpy(data.data() + writePos, src, first);
        if (count > first)
            memcpy(data.data(), src + first, count - first);

        writePos = (writePos + count) % data.size();
        usedBytes += count;
        return true;
    }

    size_t Read(BYTE* dest, size_t maxCount)
    {
        size_t toRead = min(maxCount, usedBytes);
        if (toRead == 0)
            return 0;

        size_t first = min(toRead, data.size() - readPos);
        memcpy(dest, data.data() + readPos, first);
        if (toRead > first)
            memcpy(dest + first, data.data(), toRead - first);

        readPos = (readPos + toRead) % data.size();
        usedBytes -= toRead;
        return toRead;
    }
};

class ActivateCompletionHandler : public IActivateAudioInterfaceCompletionHandler
{
    LONG _refCount = 1;
public:
    HANDLE completedEvent = nullptr;
    HRESULT activateHr = E_FAIL;
    IAudioClient* audioClient = nullptr;

    STDMETHOD(QueryInterface)(REFIID riid, void** ppvObject) override
    {
        if (!ppvObject)
            return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IActivateAudioInterfaceCompletionHandler))
        {
            *ppvObject = static_cast<IActivateAudioInterfaceCompletionHandler*>(this);
            AddRef();
            return S_OK;
        }
        *ppvObject = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHOD_(ULONG, AddRef)() override { return InterlockedIncrement(&_refCount); }
    STDMETHOD_(ULONG, Release)() override
    {
        ULONG count = InterlockedDecrement(&_refCount);
        if (count == 0)
            delete this;
        return count;
    }

    STDMETHOD(ActivateCompleted)(IActivateAudioInterfaceAsyncOperation* operation) override
    {
        activateHr = E_FAIL;
        if (operation)
        {
            HRESULT hr = E_FAIL;
            IUnknown* punk = nullptr;
            operation->GetActivateResult(&hr, &punk);
            activateHr = hr;
            if (SUCCEEDED(hr) && punk)
            {
                punk->QueryInterface(__uuidof(IAudioClient), reinterpret_cast<void**>(&audioClient));
                punk->Release();
            }
        }

        if (completedEvent)
            SetEvent(completedEvent);
        return S_OK;
    }
};

static void StopCaptureThread();

static struct ProcessCaptureContext
{
    std::atomic<bool> running{ false };
    std::atomic<bool> stopRequested{ false };
    bool ringCsInitialized = false;
    HANDLE captureThread = nullptr;
    HANDLE sampleReadyEvent = nullptr;
    IAudioClient* audioClient = nullptr;
    IAudioCaptureClient* captureClient = nullptr;
    WAVEFORMATEX waveFormat{};
    CaptureRingBuffer ring;
    DWORD targetPid = 0;
} g_ctx;

static DWORD WINAPI CaptureThreadProc(LPVOID)
{
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    while (!g_ctx.stopRequested.load())
    {
        if (!g_ctx.sampleReadyEvent || !g_ctx.captureClient)
        {
            Sleep(10);
            continue;
        }

        DWORD wait = WaitForSingleObject(g_ctx.sampleReadyEvent, 200);
        if (wait != WAIT_OBJECT_0 && wait != WAIT_TIMEOUT)
            continue;

        UINT32 packetFrames = 0;
        while (SUCCEEDED(g_ctx.captureClient->GetNextPacketSize(&packetFrames)) && packetFrames > 0)
        {
            BYTE* data = nullptr;
            UINT32 frames = 0;
            DWORD flags = 0;
            if (FAILED(g_ctx.captureClient->GetBuffer(&data, &frames, &flags, nullptr, nullptr)))
                break;

            const DWORD bytes = frames * g_ctx.waveFormat.nBlockAlign;
            if (data && bytes > 0)
            {
                EnterCriticalSection(&g_ctx.ring.cs);
                if (!g_ctx.ring.Write(data, bytes))
                {
                    // Drop oldest data when ring is full.
                    const size_t drop = min(bytes, g_ctx.ring.usedBytes);
                    if (drop > 0)
                    {
                        std::vector<BYTE> scratch(drop);
                        g_ctx.ring.Read(scratch.data(), drop);
                        g_ctx.ring.Write(data, bytes);
                    }
                }
                LeaveCriticalSection(&g_ctx.ring.cs);
            }

            g_ctx.captureClient->ReleaseBuffer(frames);
        }
    }

    CoUninitialize();
    return 0;
}

static void StopCaptureThread()
{
    g_ctx.stopRequested.store(true);
    if (g_ctx.captureThread)
    {
        WaitForSingleObject(g_ctx.captureThread, 3000);
        CloseHandle(g_ctx.captureThread);
        g_ctx.captureThread = nullptr;
    }

    if (g_ctx.audioClient)
    {
        g_ctx.audioClient->Stop();
        g_ctx.audioClient->Release();
        g_ctx.audioClient = nullptr;
    }

    if (g_ctx.captureClient)
    {
        g_ctx.captureClient->Release();
        g_ctx.captureClient = nullptr;
    }

    if (g_ctx.sampleReadyEvent)
    {
        CloseHandle(g_ctx.sampleReadyEvent);
        g_ctx.sampleReadyEvent = nullptr;
    }

    g_ctx.running.store(false);
    g_ctx.stopRequested.store(false);
}

static HRESULT BuildCaptureFormat(WAVEFORMATEX* wfx)
{
    if (!wfx)
        return E_POINTER;

    wfx->wFormatTag = WAVE_FORMAT_PCM;
    wfx->nChannels = kCaptureChannels;
    wfx->nSamplesPerSec = kCaptureSampleRate;
    wfx->wBitsPerSample = kCaptureBitsPerSample;
    wfx->nBlockAlign = (wfx->nChannels * wfx->wBitsPerSample) / 8;
    wfx->nAvgBytesPerSec = wfx->nSamplesPerSec * wfx->nBlockAlign;
    wfx->cbSize = 0;
    return S_OK;
}

static HRESULT StartCapturePipeline(IAudioClient* audioClient, DWORD targetPidForLog)
{
    if (!audioClient)
        return E_POINTER;

    WAVEFORMATEX wfx{};
    BuildCaptureFormat(&wfx);

    const REFERENCE_TIME bufferDuration = 10000000; // 1 second
    const DWORD streamFlags =
        AUDCLNT_STREAMFLAGS_LOOPBACK |
        AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
        AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM;

    HRESULT hr = audioClient->Initialize(
        AUDCLNT_SHAREMODE_SHARED,
        streamFlags,
        bufferDuration,
        0,
        &wfx,
        nullptr);

    if (FAILED(hr))
    {
        audioClient->Release();
        CaptureDebugLog("[AudioBridge] Capture Initialize failed\n");
        return hr;
    }

    IAudioCaptureClient* captureClient = nullptr;
    hr = audioClient->GetService(__uuidof(IAudioCaptureClient), reinterpret_cast<void**>(&captureClient));
    if (FAILED(hr) || !captureClient)
    {
        audioClient->Release();
        return FAILED(hr) ? hr : E_FAIL;
    }

    HANDLE sampleEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!sampleEvent)
    {
        captureClient->Release();
        audioClient->Release();
        return HRESULT_FROM_WIN32(GetLastError());
    }

    hr = audioClient->SetEventHandle(sampleEvent);
    if (FAILED(hr))
    {
        CloseHandle(sampleEvent);
        captureClient->Release();
        audioClient->Release();
        return hr;
    }

    InitializeCriticalSection(&g_ctx.ring.cs);
    g_ctx.ringCsInitialized = true;
    g_ctx.ring.Reset(1024 * 1024 * 4); // 4 MiB
    g_ctx.waveFormat = wfx;
    g_ctx.audioClient = audioClient;
    g_ctx.captureClient = captureClient;
    g_ctx.sampleReadyEvent = sampleEvent;
    g_ctx.targetPid = targetPidForLog;
    g_ctx.stopRequested.store(false);

    hr = audioClient->Start();
    if (FAILED(hr))
    {
        if (g_ctx.ringCsInitialized)
        {
            DeleteCriticalSection(&g_ctx.ring.cs);
            g_ctx.ringCsInitialized = false;
        }
        StopCaptureThread();
        return hr;
    }

    g_ctx.captureThread = CreateThread(nullptr, 0, CaptureThreadProc, nullptr, 0, nullptr);
    if (!g_ctx.captureThread)
    {
        if (g_ctx.ringCsInitialized)
        {
            DeleteCriticalSection(&g_ctx.ring.cs);
            g_ctx.ringCsInitialized = false;
        }
        StopCaptureThread();
        return HRESULT_FROM_WIN32(GetLastError());
    }

    g_ctx.running.store(true);
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT WINAPI AudioBridge_DeviceCaptureStart(LPCWSTR deviceId)
{
    if (g_ctx.running.load())
        StopCaptureThread();

    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE)
        return hr;

    IMMDeviceEnumerator* enumerator = nullptr;
    hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER, __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(&enumerator));
    if (FAILED(hr) || !enumerator)
        return FAILED(hr) ? hr : E_FAIL;

    IMMDevice* device = nullptr;
    if (deviceId != nullptr && deviceId[0] != L'\0')
        hr = enumerator->GetDevice(deviceId, &device);
    else
        hr = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);

    enumerator->Release();
    if (FAILED(hr) || !device)
    {
        CaptureDebugLog("[AudioBridge] Device capture: could not open render device\n");
        return FAILED(hr) ? hr : E_FAIL;
    }

    IAudioClient* audioClient = nullptr;
    hr = device->Activate(__uuidof(IAudioClient), CLSCTX_INPROC_SERVER, nullptr, reinterpret_cast<void**>(&audioClient));
    device->Release();
    if (FAILED(hr) || !audioClient)
    {
        CaptureDebugLog("[AudioBridge] Device capture: Activate IAudioClient failed\n");
        return FAILED(hr) ? hr : E_FAIL;
    }

    hr = StartCapturePipeline(audioClient, 0);
    if (SUCCEEDED(hr))
        CaptureDebugLog("[AudioBridge] Device loopback capture started\n");
    return hr;
}

extern "C" __declspec(dllexport) HRESULT WINAPI AudioBridge_ProcessCaptureStart(DWORD pid)
{
    if (g_ctx.running.load())
        StopCaptureThread();

    if (pid == 0)
        return E_INVALIDARG;

    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE)
        return hr;

    AUDIOCLIENT_ACTIVATION_PARAMS activationParams{};
    activationParams.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
    activationParams.ProcessLoopbackParams.TargetProcessId = pid;
    activationParams.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;

    PROPVARIANT activateParams{};
    activateParams.vt = VT_BLOB;
    activateParams.blob.cbSize = sizeof(activationParams);
    activateParams.blob.pBlobData = reinterpret_cast<BYTE*>(&activationParams);

    HANDLE completedEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!completedEvent)
        return HRESULT_FROM_WIN32(GetLastError());

    ActivateCompletionHandler* handler = new ActivateCompletionHandler();
    handler->completedEvent = completedEvent;

    IActivateAudioInterfaceAsyncOperation* asyncOp = nullptr;
    hr = ActivateAudioInterfaceAsync(
        VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK_NAME,
        __uuidof(IAudioClient),
        &activateParams,
        handler,
        &asyncOp);

    if (FAILED(hr))
    {
        handler->Release();
        CloseHandle(completedEvent);
        return hr;
    }

    WaitForSingleObject(completedEvent, 10000);
    CloseHandle(completedEvent);

    if (asyncOp)
    {
        asyncOp->Release();
        asyncOp = nullptr;
    }

    IAudioClient* audioClient = handler->audioClient;
    hr = handler->activateHr;
    handler->Release();

    if (FAILED(hr) || !audioClient)
    {
        CaptureDebugLog("[AudioBridge] Process capture activation failed\n");
        return FAILED(hr) ? hr : E_FAIL;
    }

    hr = StartCapturePipeline(audioClient, pid);
    if (SUCCEEDED(hr))
        CaptureDebugLog("[AudioBridge] Process capture started\n");
    return hr;
}

extern "C" __declspec(dllexport) void WINAPI AudioBridge_ProcessCaptureStop()
{
    if (!g_ctx.running.load() && !g_ctx.audioClient)
        return;

    StopCaptureThread();
    if (g_ctx.ringCsInitialized)
    {
        EnterCriticalSection(&g_ctx.ring.cs);
        g_ctx.ring.Reset(0);
        LeaveCriticalSection(&g_ctx.ring.cs);
        DeleteCriticalSection(&g_ctx.ring.cs);
        g_ctx.ringCsInitialized = false;
    }
    g_ctx.targetPid = 0;
    CaptureDebugLog("[AudioBridge] Process capture stopped\n");
}

extern "C" __declspec(dllexport) BOOL WINAPI AudioBridge_ProcessCaptureIsRunning()
{
    return g_ctx.running.load() ? TRUE : FALSE;
}

extern "C" __declspec(dllexport) HRESULT WINAPI AudioBridge_ProcessCaptureGetFormat(
    DWORD* sampleRate,
    WORD* channels,
    WORD* bitsPerSample)
{
    if (!sampleRate || !channels || !bitsPerSample)
        return E_POINTER;

    *sampleRate = g_ctx.waveFormat.nSamplesPerSec ? g_ctx.waveFormat.nSamplesPerSec : kCaptureSampleRate;
    *channels = g_ctx.waveFormat.nChannels ? g_ctx.waveFormat.nChannels : kCaptureChannels;
    *bitsPerSample = g_ctx.waveFormat.wBitsPerSample ? g_ctx.waveFormat.wBitsPerSample : kCaptureBitsPerSample;
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT WINAPI AudioBridge_ProcessCaptureRead(
    BYTE* buffer,
    DWORD bufferSize,
    DWORD* bytesRead)
{
    if (!bytesRead)
        return E_POINTER;

    *bytesRead = 0;
    if (!buffer || bufferSize == 0)
        return S_OK;

    if (!g_ctx.running.load())
        return S_FALSE;

    EnterCriticalSection(&g_ctx.ring.cs);
    const size_t read = g_ctx.ring.Read(buffer, bufferSize);
    LeaveCriticalSection(&g_ctx.ring.cs);

    *bytesRead = static_cast<DWORD>(read);
    return read > 0 ? S_OK : S_FALSE;
}
