#include "pch.h"
#include <d3dcompiler.h>
#include <atomic>
#include <algorithm>
#include <condition_variable>
#include <dxgi1_2.h>
#include <thread>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <string>
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

namespace rt = winrt;
namespace wgc = winrt::Windows::Graphics::Capture;
namespace d3d = winrt::Windows::Graphics::DirectX::Direct3D11;

/*
 * WgcBridge native implementation
 * ------------------------------
 * This native component creates and manages a Windows Graphics Capture
 * session for a specified HWND. It captures frames from the desktop or a
 * window into D3D11 textures, optionally performs GPU scaling/cropping, and
 * exposes APIs for managed code to retrieve frames, access GPU objects for
 * interop, and control capture settings.
 *
 * The core type is `CaptureSession` which holds D3D11 resources, a frame
 * pool and session objects, temporary buffers for CPU readback, and state
 * used to synchronize readers in managed code. The exported C APIs at the
 * bottom of this file provide simple entry points to create/destroy a
 * capture session and to retrieve frames or GPU handles.
 */

// CaptureSession
// Encapsulates all native state required for a single capture session.
// Responsibilities:
// - Create and manage D3D11 device and context used for capture and optional GPU processing
// - Hold the WinRT capture item, frame pool and active session
// - Provide thread-safe access to the last captured frame for managed callers
// - Perform optional GPU cropping and GPU-based scaling when configured
// - Expose simple C-callable functions for managed interop
struct CaptureSession
{
    struct DcompVertex
    {
        float x, y, z;
        float u, v;
    };

    struct DcompConstants
    {
        float brightness;
        float saturation;
        float sourceWidth;
        float sourceHeight;
        float tint[4];
        float outputWidth;
        float outputHeight;
        float padding0;
        float padding1;
    };

    wgc::GraphicsCaptureItem item{ nullptr };
    wgc::Direct3D11CaptureFramePool framePool{ nullptr };
    wgc::GraphicsCaptureSession session{ nullptr };
    d3d::IDirect3DDevice winrtDevice{ nullptr };

    // D3D11 device/context and temporary staging texture used for CPU readback
    rt::com_ptr<ID3D11Device> d3dDevice;
    rt::com_ptr<ID3D11DeviceContext> d3dContext;
    rt::com_ptr<ID3D11Texture2D> stagingTexture;

    // Pixel buffers
    std::vector<unsigned char> latestData; // most recent frame (RGB/A bytes)
    std::vector<unsigned char> backBuffer; // staging/back-buffer used during processing
    std::mutex poolMutex;

    // Reader synchronization and frame metadata
    std::atomic<int> readers{ 0 };
    std::mutex dataMutex;
    std::atomic<int> width{ 0 };
    std::atomic<int> height{ 0 };
    std::atomic<int> frameCount{ 0 };
    rt::event_token frameToken;
    rt::event_token closeToken;
    // Max capture size for downscaling (0 = disabled)
    int maxWidth = 0;
    int maxHeight = 0;
    // Crop rect in source texture coordinates (pixels). If width/height == 0, no crop.
    std::atomic<int> cropX{0};
    std::atomic<int> cropY{0};
    std::atomic<int> cropW{0};
    std::atomic<int> cropH{0};

    // Session control flags
    // Indicate session is closing to avoid races with frame callback
    std::atomic<bool> closing{ false };

    // When true, do not perform CPU staging/readback; rely on GPU-GPU interop
    std::atomic<bool> interopEnabled{ false };
    struct InjectionFrameHeader;
    HANDLE injectionMapping{ nullptr };
    InjectionFrameHeader* injectionHeader{ nullptr };
    uint8_t* injectionPixels{ nullptr };
    std::atomic<uint32_t> injectionFrameCounter{ 0 };

    // GPU scaler resources (cached)
    rt::com_ptr<ID3D11VertexShader> vs;
    rt::com_ptr<ID3D11PixelShader> ps;
    rt::com_ptr<ID3D11InputLayout> inputLayout;
    rt::com_ptr<ID3D11Buffer> quadVB;
    rt::com_ptr<ID3D11SamplerState> samplerState;
    rt::com_ptr<ID3D11Texture2D> scaledTexture;
    D3D11_TEXTURE2D_DESC scaledTextureDesc{};
    rt::com_ptr<ID3D11RenderTargetView> scaledRTV;
    rt::com_ptr<ID3D11ShaderResourceView> tempSRV;
    
    rt::com_ptr<ID3D11Texture2D> croppedTexture;
    int croppedTextureWidth = 0;
    int croppedTextureHeight = 0;

    // GPU interop and staging info
    rt::com_ptr<ID3D11Texture2D> latestGpuTexture; // most recent GPU texture

    int stagingTextureWidth = 0;
    int stagingTextureHeight = 0;

    // Optional DXGI swapchain used to influence VRR timing
    rt::com_ptr<IDXGISwapChain1> swapChain;
    bool vrrEnabled = false;

    // DirectComposition presentation path for NativeControlHost testing
    HWND presentationHwnd = nullptr;
    rt::com_ptr<IDXGIFactory2> dxgiFactory;
    rt::com_ptr<IDCompositionDevice> dcompDevice;
    rt::com_ptr<IDCompositionTarget> dcompTarget;
    rt::com_ptr<IDCompositionVisual> dcompVisual;
    rt::com_ptr<IDCompositionScaleTransform> dcompScaleTransform;
    rt::com_ptr<IDXGISwapChain1> dcompSwapChain;
    rt::com_ptr<ID3D11RenderTargetView> dcompRenderTargetView;
    rt::com_ptr<ID3D11VertexShader> dcompVertexShader;
    rt::com_ptr<ID3D11PixelShader> dcompPixelShader;
    rt::com_ptr<ID3D11InputLayout> dcompInputLayout;
    rt::com_ptr<ID3D11Buffer> dcompVertexBuffer;
    rt::com_ptr<ID3D11Buffer> dcompConstantBuffer;
    rt::com_ptr<ID3D11SamplerState> dcompSamplerState;
    rt::com_ptr<ID3D11RasterizerState> dcompRasterizerState;
    int dcompWidth = 0;
    int dcompHeight = 0;
    std::atomic<int> dcompState{ 0 }; // 0=disabled, 1=initializing, 2=active, -1=failed
    std::atomic<int> dcompPresentCount{ 0 };
    std::atomic<int> dcompStretch{ 2 }; // 0=fill, 1=uniform, 2=uniformToFill
    std::atomic<float> dcompBrightness{ 1.0f };
    std::atomic<float> dcompSaturation{ 1.0f };
    std::atomic<float> dcompTintR{ 1.0f };
    std::atomic<float> dcompTintG{ 1.0f };
    std::atomic<float> dcompTintB{ 1.0f };
    std::atomic<float> dcompTintA{ 1.0f };
    std::atomic<bool> dcompDisableVsync{ false };
    std::wstring dcompShaderPath;
    std::mutex dcompShaderMutex;
    std::atomic<bool> dcompShaderDirty{ false };
    std::wstring adapterDescription;
    std::wstring adapterVendor;
    std::string dcompLastError;
    std::mutex dcompErrorMutex;
    std::mutex dcompWorkerMutex;
    std::condition_variable dcompWorkerCv;
    std::thread dcompWorker;
    std::atomic<bool> dcompWorkerRunning{ false };
    rt::com_ptr<ID3D11Texture2D> dcompPendingTexture;
    int dcompPendingWidth = 0;
    int dcompPendingHeight = 0;
    bool dcompHasPendingFrame = false;

    // Injection read path (Host side)
    std::atomic<int> injectionPid{ 0 };
    HANDLE injectionReadMapping{ nullptr };
    InjectionFrameHeader* injectionReadHeader{ nullptr };
    uint8_t* injectionReadPixels{ nullptr };
    uint32_t lastReadInjectionSequence = 0;
    std::thread injectionWorker;
    std::atomic<bool> injectionWorkerRunning{ false };
    std::atomic<long long> lastInjectionFrameTime{ 0 };
    rt::com_ptr<ID3D11Texture2D> injectionGpuTexture;
    int injectionGpuWidth = 0;
    int injectionGpuHeight = 0;

    void StartInjectionWorker()
    {
        if (injectionWorkerRunning.load()) return;
        injectionWorkerRunning.store(true);
        injectionWorker = std::thread([this]() { InjectionWorkerLoop(); });
    }

    void StopInjectionWorker()
    {
        injectionWorkerRunning.store(false);
        if (injectionWorker.joinable()) injectionWorker.join();
        
        if (injectionReadHeader) UnmapViewOfFile(injectionReadHeader);
        if (injectionReadMapping) CloseHandle(injectionReadMapping);
        injectionReadHeader = nullptr;
        injectionReadMapping = nullptr;
        injectionReadPixels = nullptr;
    }

    void InjectionWorkerLoop()
    {
        while (injectionWorkerRunning.load())
        {
            int pid = injectionPid.load();
            if (pid <= 0)
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                continue;
            }

            if (!injectionReadMapping)
            {
                auto mapName = MakeInjectionSharedMemoryName(pid);
                injectionReadMapping = OpenFileMappingW(FILE_MAP_READ, FALSE, mapName.c_str());
                if (injectionReadMapping)
                {
                    void* view = MapViewOfFile(injectionReadMapping, FILE_MAP_READ, 0, 0, InjectionHeaderSize + InjectionMaxFrameBytes);
                    if (view)
                    {
                        injectionReadHeader = reinterpret_cast<InjectionFrameHeader*>(view);
                        injectionReadPixels = reinterpret_cast<uint8_t*>(view) + InjectionHeaderSize;
                        OutputDebugStringA("[WGC_NATIVE] Injection worker opened shared memory\n");
                    }
                    else
                    {
                        CloseHandle(injectionReadMapping);
                        injectionReadMapping = nullptr;
                    }
                }
            }

            if (injectionReadHeader && injectionReadHeader->Magic == InjectionMagic)
            {
                uint32_t seq1 = InterlockedExchange(reinterpret_cast<volatile LONG*>(&injectionReadHeader->Sequence1), injectionReadHeader->Sequence1);
                uint32_t seq2 = InterlockedExchange(reinterpret_cast<volatile LONG*>(&injectionReadHeader->Sequence2), injectionReadHeader->Sequence2);

                if (seq1 > 0 && seq1 == seq2 && seq1 != lastReadInjectionSequence)
                {
                    lastReadInjectionSequence = seq1;
                    int w = (int)injectionReadHeader->Width;
                    int h = (int)injectionReadHeader->Height;
                    int stride = (int)injectionReadHeader->Stride;

                    if (w > 0 && h > 0 && w <= 8192 && h <= 8192)
                    {
                        if (!injectionGpuTexture || injectionGpuWidth != w || injectionGpuHeight != h)
                        {
                            D3D11_TEXTURE2D_DESC td = {};
                            td.Width = (UINT)w;
                            td.Height = (UINT)h;
                            td.MipLevels = 1;
                            td.ArraySize = 1;
                            td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                            td.SampleDesc.Count = 1;
                            td.Usage = D3D11_USAGE_DEFAULT;
                            td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                            
                            if (SUCCEEDED(d3dDevice->CreateTexture2D(&td, nullptr, injectionGpuTexture.put())))
                            {
                                injectionGpuWidth = w;
                                injectionGpuHeight = h;
                            }
                        }

                        if (injectionGpuTexture)
                        {
                            d3dContext->UpdateSubresource(injectionGpuTexture.get(), 0, nullptr, injectionReadPixels, stride, 0);
                            
                            LARGE_INTEGER qpc;
                            QueryPerformanceCounter(&qpc);
                            lastInjectionFrameTime.store(qpc.QuadPart);

                            if (presentationHwnd)
                            {
                                QueueDirectCompositionFrame(injectionGpuTexture.get(), w, h);
                                frameCount.fetch_add(1);
                            }
                        }
                    }
                }
            }

            // Sleep a tiny bit to avoid saturating a core, but keep it low for latency.
            std::this_thread::sleep_for(std::chrono::microseconds(500));
        }
    }

    static std::wstring GetShaderCompilerErrorLogPath()
    {
        DWORD required = GetEnvironmentVariableW(L"LOCALAPPDATA", nullptr, 0);
        if (required > 1)
        {
            std::wstring path(required - 1, L'\0');
            if (GetEnvironmentVariableW(L"LOCALAPPDATA", path.data(), required) > 0)
            {
                path += L"\\AES_Lacrima\\Logs\\shaderCompilerError.txt";
                return path;
            }
        }

        wchar_t modulePath[MAX_PATH] = {};
        if (GetModuleFileNameW(nullptr, modulePath, static_cast<DWORD>(std::size(modulePath))) > 0)
        {
            std::filesystem::path path(modulePath);
            path = path.parent_path() / L"shaderCompilerError.txt";
            return path.wstring();
        }

        return L"shaderCompilerError.txt";
    }

    static void EnsureParentDirectoryExists(std::wstring const& filePath)
    {
        try
        {
            std::filesystem::path path(filePath);
            auto parent = path.parent_path();
            if (!parent.empty())
                std::filesystem::create_directories(parent);
        }
        catch (...)
        {
            auto lastSlash = filePath.find_last_of(L"\\/");
            if (lastSlash == std::wstring::npos)
                return;

            std::wstring directory = filePath.substr(0, lastSlash);
            if (directory.empty())
                return;

            CreateDirectoryW(directory.c_str(), nullptr);
        }
    }

    static void WriteShaderCompilerErrorFile(char const* message)
    {
        try
        {
            auto path = GetShaderCompilerErrorLogPath();
            EnsureParentDirectoryExists(path);

            std::ofstream stream(std::filesystem::path(path), std::ios::out | std::ios::trunc | std::ios::binary);
            if (stream.is_open())
                stream << (message ? message : "");
        }
        catch (...)
        {
        }
    }

    static void ClearShaderCompilerErrorFile()
    {
        try
        {
            auto path = GetShaderCompilerErrorLogPath();
            std::error_code ec;
            std::filesystem::remove(std::filesystem::path(path), ec);
        }
        catch (...)
        {
        }
    }

    static constexpr size_t InjectionMaxFrameBytes = 128ull * 1024ull * 1024ull;
    static constexpr size_t InjectionHeaderSize = 128;
    static constexpr uint32_t InjectionMagic = 0x41534145;

#pragma pack(push, 1)
    struct InjectionFrameHeader
    {
        uint32_t Magic;
        uint32_t Sequence1;
        uint32_t Sequence2;
        uint32_t Width;
        uint32_t Height;
        uint32_t Stride;
        uint32_t FrameCounter;
        uint32_t Reserved0;
        uint32_t Reserved1;
        uint32_t Reserved2;
        uint32_t Reserved3;
        uint32_t Reserved4;
        uint32_t Reserved5;
        uint32_t Reserved6;
        uint32_t Reserved7;
        uint32_t Reserved8;
        uint32_t Reserved9;
    };
#pragma pack(pop)

    static std::wstring MakeInjectionRequestEventName(DWORD pid)
    {
        return std::wstring(L"Local\\AES_Lacrima_Injector_Request_") + std::to_wstring(pid);
    }

    static std::wstring MakeInjectionReadyEventName(DWORD pid)
    {
        return std::wstring(L"Local\\AES_Lacrima_Injector_Ready_") + std::to_wstring(pid);
    }

    static std::wstring MakeInjectionSharedMemoryName(DWORD pid)
    {
        return std::wstring(L"Local\\AES_Lacrima_Injector_Shared_") + std::to_wstring(pid);
    }

    static bool CreateInjectionSecurityAttributes(SECURITY_ATTRIBUTES& sa, PSECURITY_DESCRIPTOR& securityDescriptor)
    {
        ZeroMemory(&sa, sizeof(sa));
        sa.nLength = sizeof(sa);
        sa.bInheritHandle = FALSE;
        securityDescriptor = nullptr;

        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                L"D:(A;;GA;;;WD)",
                SDDL_REVISION_1,
                &securityDescriptor,
                nullptr))
        {
            OutputDebugStringA("[WGC_NATIVE] ConvertStringSecurityDescriptorToSecurityDescriptorW failed\n");
            return false;
        }

        sa.lpSecurityDescriptor = securityDescriptor;
        return true;
    }

    struct SwapChainHook
    {
        void** vtable{};
        void** vtable1{};
        void* originalPresent{};
        void* originalPresent1{};
        rt::com_ptr<ID3D11Device> device;
        rt::com_ptr<ID3D11DeviceContext> context;
        rt::com_ptr<ID3D11Texture2D> stagingTextures[3];
        int currentIndex = -1;
        DXGI_FORMAT format{};
        int width = 0;
        int height = 0;
        long long lastCaptureTicks = 0;
    };

    static inline std::mutex g_hookMutex;
    static inline std::vector<SwapChainHook> g_swapChainHooks;
    static inline void* g_originalD3D11CreateDeviceAndSwapChain = nullptr;
    static inline void* g_originalD3D11CreateDeviceAndSwapChain1 = nullptr;
    static inline void* g_originalD3D11CreateDevice = nullptr;
    static inline void* g_originalCreateDXGIFactory = nullptr;
    static inline void* g_originalCreateDXGIFactory1 = nullptr;
    static inline void* g_originalCreateDXGIFactory2 = nullptr;
    static inline void* g_originalCreateDXGIFactoryForHwnd = nullptr;
    static inline void* g_originalCreateDXGIFactoryForCoreWindow = nullptr;
    static inline void* g_originalCreateSwapChain = nullptr;
    static inline void* g_originalCreateSwapChainForHwnd = nullptr;
    static inline void* g_originalCreateSwapChainForCoreWindow = nullptr;
    static inline HANDLE g_injectionMapping = nullptr;
    static inline InjectionFrameHeader* g_injectionHeader = nullptr;
    static inline uint8_t* g_injectionPixels = nullptr;


    static bool HookDxgiFactory(IUnknown* factory)
    {
        if (!factory)
            return false;

        void** vtable = *reinterpret_cast<void***>(factory);
        if (!vtable)
            return false;

        std::lock_guard<std::mutex> lock(g_hookMutex);
        bool hooked = false;

        // Offset 10: CreateSwapChain (IDXGIFactory)
        if (vtable[10] && vtable[10] != Hooked_CreateSwapChain)
        {
            if (!g_originalCreateSwapChain) g_originalCreateSwapChain = vtable[10];
            DWORD oldProtect = 0;
            if (VirtualProtect(&vtable[10], sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtect))
            {
                vtable[10] = reinterpret_cast<void*>(Hooked_CreateSwapChain);
                FlushInstructionCache(GetCurrentProcess(), &vtable[10], sizeof(void*));
                VirtualProtect(&vtable[10], sizeof(void*), oldProtect, &oldProtect);
                DebugLog("[WGC_NATIVE] VTable patched IDXGIFactory::CreateSwapChain (offset 10)");
                hooked = true;
            }
        }

        // Offset 15: CreateSwapChainForHwnd (IDXGIFactory2)
        if (vtable[15] && vtable[15] != Hooked_CreateSwapChainForHwnd)
        {
            if (!g_originalCreateSwapChainForHwnd) g_originalCreateSwapChainForHwnd = vtable[15];
            DWORD oldProtect = 0;
            if (VirtualProtect(&vtable[15], sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtect))
            {
                vtable[15] = reinterpret_cast<void*>(Hooked_CreateSwapChainForHwnd);
                FlushInstructionCache(GetCurrentProcess(), &vtable[15], sizeof(void*));
                VirtualProtect(&vtable[15], sizeof(void*), oldProtect, &oldProtect);
                DebugLog("[WGC_NATIVE] VTable patched IDXGIFactory2::CreateSwapChainForHwnd (offset 15)");
                hooked = true;
            }
        }

        // Offset 16: CreateSwapChainForCoreWindow (IDXGIFactory2)
        if (vtable[16] && vtable[16] != Hooked_CreateSwapChainForCoreWindow)
        {
            if (!g_originalCreateSwapChainForCoreWindow) g_originalCreateSwapChainForCoreWindow = vtable[16];
            DWORD oldProtect = 0;
            if (VirtualProtect(&vtable[16], sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtect))
            {
                vtable[16] = reinterpret_cast<void*>(Hooked_CreateSwapChainForCoreWindow);
                FlushInstructionCache(GetCurrentProcess(), &vtable[16], sizeof(void*));
                VirtualProtect(&vtable[16], sizeof(void*), oldProtect, &oldProtect);
                DebugLog("[WGC_NATIVE] VTable patched IDXGIFactory2::CreateSwapChainForCoreWindow (offset 16)");
                hooked = true;
            }
        }

        return hooked;
    }

    static HRESULT WINAPI Hooked_CreateSwapChain(IDXGIFactory* This, IUnknown* pDevice, DXGI_SWAP_CHAIN_DESC* pDesc, IDXGISwapChain** ppSwapChain)
    {
        using CreateSwapChainFn = HRESULT(WINAPI*)(IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);
        auto originalFn = reinterpret_cast<CreateSwapChainFn>(g_originalCreateSwapChain);
        if (!originalFn)
            return E_FAIL;

        HRESULT hr = originalFn(This, pDevice, pDesc, ppSwapChain);
        char buf[256];
        sprintf_s(buf, "[WGC_NATIVE] Hooked_CreateSwapChain called hr=0x%08X ppSwapChain=%p\n", static_cast<unsigned>(hr), ppSwapChain ? *ppSwapChain : nullptr);
        DebugLog(buf);
        if (SUCCEEDED(hr) && ppSwapChain && *ppSwapChain)
        {
            if (HookSwapChain(*ppSwapChain))
                DebugLog("[WGC_NATIVE] Hooked_CreateSwapChain successfully hooked swapchain\n");
            else
                DebugLog("[WGC_NATIVE] Hooked_CreateSwapChain failed to hook swapchain\n");
        }

        return hr;
    }

    static HRESULT WINAPI Hooked_CreateSwapChainForHwnd(IDXGIFactory2* This, IUnknown* pDevice, HWND hWnd, DXGI_SWAP_CHAIN_DESC1 const* pDesc, DXGI_SWAP_CHAIN_FULLSCREEN_DESC const* pFullscreenDesc, IDXGIOutput* pRestrictToOutput, IDXGISwapChain1** ppSwapChain)
    {
        using CreateSwapChainForHwndFn = HRESULT(WINAPI*)(IDXGIFactory2*, IUnknown*, HWND, DXGI_SWAP_CHAIN_DESC1 const*, DXGI_SWAP_CHAIN_FULLSCREEN_DESC const*, IDXGIOutput*, IDXGISwapChain1**);
        auto originalFn = reinterpret_cast<CreateSwapChainForHwndFn>(g_originalCreateSwapChainForHwnd);
        if (!originalFn)
            return E_FAIL;

        HRESULT hr = originalFn(This, pDevice, hWnd, pDesc, pFullscreenDesc, pRestrictToOutput, ppSwapChain);
        char buf[256];
        sprintf_s(buf, "[WGC_NATIVE] Hooked_CreateSwapChainForHwnd called hr=0x%08X ppSwapChain=%p\n", static_cast<unsigned>(hr), ppSwapChain ? *ppSwapChain : nullptr);
        DebugLog(buf);
        if (SUCCEEDED(hr) && ppSwapChain && *ppSwapChain)
        {
            if (HookSwapChain(*reinterpret_cast<IDXGISwapChain**>(ppSwapChain)))
                DebugLog("[WGC_NATIVE] Hooked_CreateSwapChainForHwnd successfully hooked swapchain\n");
            else
                DebugLog("[WGC_NATIVE] Hooked_CreateSwapChainForHwnd failed to hook swapchain\n");
        }

        return hr;
    }

    static HRESULT WINAPI Hooked_CreateSwapChainForCoreWindow(IDXGIFactory2* This, IUnknown* pDevice, IUnknown* pWindow, DXGI_SWAP_CHAIN_DESC1 const* pDesc, IDXGIOutput* pRestrictToOutput, IDXGISwapChain1** ppSwapChain)
    {
        using CreateSwapChainForCoreWindowFn = HRESULT(WINAPI*)(IDXGIFactory2*, IUnknown*, IUnknown*, DXGI_SWAP_CHAIN_DESC1 const*, IDXGIOutput*, IDXGISwapChain1**);
        auto originalFn = reinterpret_cast<CreateSwapChainForCoreWindowFn>(g_originalCreateSwapChainForCoreWindow);
        if (!originalFn)
            return E_FAIL;

        HRESULT hr = originalFn(This, pDevice, pWindow, pDesc, pRestrictToOutput, ppSwapChain);
        char buf[256];
        sprintf_s(buf, "[WGC_NATIVE] Hooked_CreateSwapChainForCoreWindow called hr=0x%08X ppSwapChain=%p\n", static_cast<unsigned>(hr), ppSwapChain ? *ppSwapChain : nullptr);
        DebugLog(buf);
        if (SUCCEEDED(hr) && ppSwapChain && *ppSwapChain)
        {
            if (HookSwapChain(*reinterpret_cast<IDXGISwapChain**>(ppSwapChain)))
                DebugLog("[WGC_NATIVE] Hooked_CreateSwapChainForCoreWindow successfully hooked swapchain\n");
            else
                DebugLog("[WGC_NATIVE] Hooked_CreateSwapChainForCoreWindow failed to hook swapchain\n");
        }

        return hr;
    }

    static HRESULT WINAPI Hooked_D3D11CreateDevice(
        IDXGIAdapter* pAdapter,
        D3D_DRIVER_TYPE DriverType,
        HMODULE Software,
        UINT Flags,
        const D3D_FEATURE_LEVEL* pFeatureLevels,
        UINT FeatureLevels,
        UINT SDKVersion,
        ID3D11Device** ppDevice,
        D3D_FEATURE_LEVEL* pFeatureLevel,
        ID3D11DeviceContext** ppImmediateContext)
    {
        using CreateDeviceFn = HRESULT(WINAPI*)(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT, const D3D_FEATURE_LEVEL*, UINT, UINT, ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);
        auto originalFn = reinterpret_cast<CreateDeviceFn>(g_originalD3D11CreateDevice);
        if (!originalFn)
            return E_FAIL;

        HRESULT hr = originalFn(pAdapter, DriverType, Software, Flags, pFeatureLevels, FeatureLevels, SDKVersion, ppDevice, pFeatureLevel, ppImmediateContext);
        if (SUCCEEDED(hr) && ppDevice && *ppDevice)
        {
            rt::com_ptr<IDXGIDevice> dxgiDevice;
            if (SUCCEEDED((*ppDevice)->QueryInterface(IID_PPV_ARGS(dxgiDevice.put()))))
            {
                rt::com_ptr<IDXGIAdapter> adapter;
                if (SUCCEEDED(dxgiDevice->GetAdapter(adapter.put())))
                {
                    rt::com_ptr<IDXGIFactory> factory;
                    if (SUCCEEDED(adapter->GetParent(IID_PPV_ARGS(factory.put()))))
                    {
                        HookDxgiFactory(reinterpret_cast<IUnknown*>(factory.get()));
                    }
                }
            }
        }
        return hr;
    }

    static HRESULT WINAPI Hooked_CreateDXGIFactory(REFIID riid, void** ppFactory)
    {
        using CreateDXGIFactoryFn = HRESULT(WINAPI*)(REFIID, void**);
        auto originalFn = reinterpret_cast<CreateDXGIFactoryFn>(g_originalCreateDXGIFactory);
        if (!originalFn)
            return E_FAIL;

        HRESULT hr = originalFn(riid, ppFactory);
        char buf[256];
        sprintf_s(buf, "[WGC_NATIVE] Hooked_CreateDXGIFactory called hr=0x%08X factory=%p\n", static_cast<unsigned>(hr), ppFactory ? *ppFactory : nullptr);
        DebugLog(buf);
        if (SUCCEEDED(hr) && ppFactory && *ppFactory)
            HookDxgiFactory(reinterpret_cast<IUnknown*>(*ppFactory));

        return hr;
    }

    static HRESULT WINAPI Hooked_CreateDXGIFactory1(REFIID riid, void** ppFactory)
    {
        using CreateDXGIFactory1Fn = HRESULT(WINAPI*)(REFIID, void**);
        auto originalFn = reinterpret_cast<CreateDXGIFactory1Fn>(g_originalCreateDXGIFactory1);
        if (!originalFn)
            return E_FAIL;

        HRESULT hr = originalFn(riid, ppFactory);
        char buf[256];
        sprintf_s(buf, "[WGC_NATIVE] Hooked_CreateDXGIFactory1 called hr=0x%08X factory=%p\n", static_cast<unsigned>(hr), ppFactory ? *ppFactory : nullptr);
        DebugLog(buf);
        if (SUCCEEDED(hr) && ppFactory && *ppFactory)
            HookDxgiFactory(reinterpret_cast<IUnknown*>(*ppFactory));

        return hr;
    }

    static HRESULT WINAPI Hooked_CreateDXGIFactory2(UINT Flags, REFIID riid, void** ppFactory)
    {
        using CreateDXGIFactory2Fn = HRESULT(WINAPI*)(UINT, REFIID, void**);
        auto originalFn = reinterpret_cast<CreateDXGIFactory2Fn>(g_originalCreateDXGIFactory2);
        if (!originalFn)
            return E_FAIL;

        HRESULT hr = originalFn(Flags, riid, ppFactory);
        char buf[256];
        sprintf_s(buf, "[WGC_NATIVE] Hooked_CreateDXGIFactory2 called hr=0x%08X factory=%p\n", static_cast<unsigned>(hr), ppFactory ? *ppFactory : nullptr);
        DebugLog(buf);
        if (SUCCEEDED(hr) && ppFactory && *ppFactory)
            HookDxgiFactory(reinterpret_cast<IUnknown*>(*ppFactory));

        return hr;
    }

    static HRESULT WINAPI Hooked_CreateDXGIFactoryForHwnd(UINT Flags, HWND hWnd, REFIID riid, void** ppFactory)
    {
        using CreateDXGIFactoryForHwndFn = HRESULT(WINAPI*)(UINT, HWND, REFIID, void**);
        auto originalFn = reinterpret_cast<CreateDXGIFactoryForHwndFn>(g_originalCreateDXGIFactoryForHwnd);
        if (!originalFn)
            return E_FAIL;

        HRESULT hr = originalFn(Flags, hWnd, riid, ppFactory);
        if (SUCCEEDED(hr) && ppFactory && *ppFactory)
            HookDxgiFactory(reinterpret_cast<IUnknown*>(*ppFactory));

        return hr;
    }

    static HRESULT WINAPI Hooked_CreateDXGIFactoryForCoreWindow(UINT Flags, IUnknown* pWindow, REFIID riid, void** ppFactory)
    {
        using CreateDXGIFactoryForCoreWindowFn = HRESULT(WINAPI*)(UINT, IUnknown*, REFIID, void**);
        auto originalFn = reinterpret_cast<CreateDXGIFactoryForCoreWindowFn>(g_originalCreateDXGIFactoryForCoreWindow);
        if (!originalFn)
            return E_FAIL;

        HRESULT hr = originalFn(Flags, pWindow, riid, ppFactory);
        if (SUCCEEDED(hr) && ppFactory && *ppFactory)
            HookDxgiFactory(reinterpret_cast<IUnknown*>(*ppFactory));

        return hr;
    }

    static void* GetOriginalPresentForSwapChain(IDXGISwapChain* swapChain)
    {
        if (!swapChain)
            return nullptr;

        void** vtable = *reinterpret_cast<void***>(swapChain);
        std::lock_guard<std::mutex> lock(g_hookMutex);
        for (auto& hook : g_swapChainHooks)
        {
            if (hook.vtable == vtable)
                return hook.originalPresent;
        }

        return nullptr;
    }

    static bool HookSwapChain(IDXGISwapChain* swapChain)
    {
        if (!swapChain)
            return false;

        void** vtable = *reinterpret_cast<void***>(swapChain);
        if (!vtable)
            return false;

        std::lock_guard<std::mutex> lock(g_hookMutex);
        for (auto& hook : g_swapChainHooks)
        {
            if (hook.vtable == vtable)
                return true;
        }

        char buf[256];
        sprintf_s(buf, "[WGC_NATIVE] HookSwapChain called swapChain=%p\n", swapChain);
        DebugLog(buf);

        void* originalPresent = vtable[8];
        if (!originalPresent)
        {
            DebugLog("[WGC_NATIVE] HookSwapChain failed: no original Present\n");
            return false;
        }

        SwapChainHook hook{};
        hook.vtable = vtable;
        hook.originalPresent = originalPresent;

        DWORD oldProtect = 0;
        if (!VirtualProtect(&vtable[8], sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtect))
        {
            DebugLog("[WGC_NATIVE] HookSwapChain failed: VirtualProtect for Present\n");
            return false;
        }

        vtable[8] = reinterpret_cast<void*>(Hooked_Present);
        FlushInstructionCache(GetCurrentProcess(), &vtable[8], sizeof(void*));
        VirtualProtect(&vtable[8], sizeof(void*), oldProtect, &oldProtect);

        rt::com_ptr<IDXGISwapChain1> swapChain1;
        if (SUCCEEDED(swapChain->QueryInterface(IID_PPV_ARGS(swapChain1.put()))))
        {
            void** vtable1 = *reinterpret_cast<void***>(swapChain1.get());
            if (vtable1)
            {
                void* originalPresent1 = vtable1[22];
                if (originalPresent1)
                {
                    DWORD oldProtect1 = 0;
                    if (VirtualProtect(&vtable1[22], sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtect1))
                    {
                        vtable1[22] = reinterpret_cast<void*>(Hooked_Present1);
                        FlushInstructionCache(GetCurrentProcess(), &vtable1[22], sizeof(void*));
                        VirtualProtect(&vtable1[22], sizeof(void*), oldProtect1, &oldProtect1);
                        hook.originalPresent1 = originalPresent1;
                        hook.vtable1 = vtable1;
                    }
                }
            }
        }

        g_swapChainHooks.push_back(std::move(hook));
        return true;
    }

    static void CopyFrameDataToSharedMemory(uint8_t const* src, size_t rowBytes, int width, int height, DXGI_FORMAT format)
    {
        if (!g_injectionHeader || !g_injectionPixels)
            return;

        const bool isBgra = format == DXGI_FORMAT_B8G8R8A8_UNORM || format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
        const size_t dstStride = static_cast<size_t>(width) * 4;
        const size_t totalSize = dstStride * height;

        // CRITICAL: Prevent buffer overrun if resolution is unexpectedly high
        if (totalSize > InjectionMaxFrameBytes)
            return;

        g_injectionHeader->Sequence1 = 0;
        g_injectionHeader->Width = width;
        g_injectionHeader->Height = height;
        g_injectionHeader->Stride = static_cast<uint32_t>(dstStride);
        uint32_t currentCounter = g_injectionHeader->FrameCounter + 1;
        if (currentCounter == 0) currentCounter = 1;
        g_injectionHeader->FrameCounter = currentCounter;
        g_injectionHeader->Magic = InjectionMagic;

        if (isBgra && dstStride == rowBytes)
        {
            memcpy(g_injectionPixels, src, totalSize);
        }
        else
        {
            for (int y = 0; y < height; y++)
            {
                uint8_t const* srcRow = src + static_cast<size_t>(rowBytes) * y;
                uint8_t* dstRow = g_injectionPixels + dstStride * y;

                if (isBgra)
                {
                    memcpy(dstRow, srcRow, dstStride);
                }
                else
                {
                    for (int x = 0; x < width; x++)
                    {
                        dstRow[0] = srcRow[2]; // B
                        dstRow[1] = srcRow[1]; // G
                        dstRow[2] = srcRow[0]; // R
                        dstRow[3] = srcRow[3]; // A
                        srcRow += 4;
                        dstRow += 4;
                    }
                }
            }
        }

        g_injectionHeader->Sequence2 = currentCounter;
        g_injectionHeader->Sequence1 = currentCounter;
    }

    static void CaptureSwapChainFrame(IDXGISwapChain* swapChain)
    {
        if (!swapChain || !g_injectionHeader)
            return;

        void** vtable = *reinterpret_cast<void***>(swapChain);
        std::lock_guard<std::mutex> lock(g_hookMutex);
        auto it = std::find_if(g_swapChainHooks.begin(), g_swapChainHooks.end(), [vtable](SwapChainHook const& h)
        {
            return h.vtable == vtable;
        });

        if (it == g_swapChainHooks.end())
            return;

        auto& hook = *it;

        // Rate limit injection capture to ~120 FPS to prevent memory bandwidth saturation and crashes.
        LARGE_INTEGER qpc;
        QueryPerformanceCounter(&qpc);
        LARGE_INTEGER freq;
        QueryPerformanceFrequency(&freq);
        
        long long now = qpc.QuadPart;
        if (hook.lastCaptureTicks > 0)
        {
            double elapsed = static_cast<double>(now - hook.lastCaptureTicks) / freq.QuadPart;
            if (elapsed < (1.0 / 130.0)) // 130 FPS cap to reduce jitter at 120 FPS
                return;
        }
        hook.lastCaptureTicks = now;

        rt::com_ptr<ID3D11Texture2D> backBuffer;
        if (FAILED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(backBuffer.put()))) || !backBuffer)
            return;

        if (!hook.device)
        {
            backBuffer->GetDevice(hook.device.put());
            if (!hook.device) return;
            hook.device->GetImmediateContext(hook.context.put());
            if (!hook.context) return;
        }

        D3D11_TEXTURE2D_DESC desc = {};
        backBuffer->GetDesc(&desc);
        
        // Basic validation
        if (desc.SampleDesc.Count > 1) return;
        if (desc.Format != DXGI_FORMAT_R8G8B8A8_UNORM && desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM &&
            desc.Format != DXGI_FORMAT_R8G8B8A8_UNORM_SRGB && desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)
            return;

        // Initialize staging textures if resolution changed
        if (!hook.stagingTextures[0] || hook.width != (int)desc.Width || hook.height != (int)desc.Height || hook.format != desc.Format)
        {
            hook.format = desc.Format;
            hook.width = (int)desc.Width;
            hook.height = (int)desc.Height;

            D3D11_TEXTURE2D_DESC stagingDesc = desc;
            stagingDesc.Usage = D3D11_USAGE_STAGING;
            stagingDesc.BindFlags = 0;
            stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            stagingDesc.MiscFlags = 0;

            for(int i=0; i<3; i++) {
                hook.stagingTextures[i] = nullptr;
                hook.device->CreateTexture2D(&stagingDesc, nullptr, hook.stagingTextures[i].put());
            }
        }

        // 1. Copy current frame into the next staging buffer.
        int nextIndex = (hook.currentIndex + 1) % 3;
        hook.context->CopyResource(hook.stagingTextures[nextIndex].get(), backBuffer.get());

        D3D11_MAPPED_SUBRESOURCE mapped = {};
        // Removing DO_NOT_WAIT. With 3 staging textures, the oldest should be ready.
        // If it isn't, we wait to ensure we don't drop frames during high GPU load (shader compilation).
        if (hook.currentIndex >= 0)
        {
            HRESULT hr = hook.context->Map(hook.stagingTextures[hook.currentIndex].get(), 0, D3D11_MAP_READ, 0, &mapped);
            if (SUCCEEDED(hr) && mapped.pData)
            {
                CopyFrameDataToSharedMemory(static_cast<uint8_t const*>(mapped.pData), mapped.RowPitch, hook.width, hook.height, hook.format);
                hook.context->Unmap(hook.stagingTextures[hook.currentIndex].get(), 0);
            }
        }

        hook.currentIndex = nextIndex;
    }

    static bool CreateInjectionFileMapping(DWORD pid)
    {
        SECURITY_ATTRIBUTES sa{};
        PSECURITY_DESCRIPTOR sd = nullptr;
        if (!CreateInjectionSecurityAttributes(sa, sd))
            return false;

        if (g_injectionMapping && g_injectionHeader && g_injectionPixels)
        {
            if (sd) LocalFree(sd);
            return true;
        }

        auto mapName = MakeInjectionSharedMemoryName(pid);
        char buf[256];
        sprintf_s(buf, "[WGC_NATIVE] InitializeGlobalInjection creating shared memory for PID %lu name=%S\n", pid, mapName.c_str());
        OutputDebugStringA(buf);

        HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, 0, static_cast<DWORD>(InjectionHeaderSize + InjectionMaxFrameBytes), mapName.c_str());
        if (!mapping)
        {
            DWORD err = GetLastError();
            sprintf_s(buf, "[WGC_NATIVE] InitializeGlobalInjection CreateFileMappingW failed err=%lu\n", err);
            OutputDebugStringA(buf);
            if (sd)
                LocalFree(sd);
            return false;
        }

        void* view = MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, InjectionHeaderSize + InjectionMaxFrameBytes);
        if (!view)
        {
            DWORD err = GetLastError();
            sprintf_s(buf, "[WGC_NATIVE] InitializeGlobalInjection MapViewOfFile failed err=%lu\n", err);
            OutputDebugStringA(buf);
            CloseHandle(mapping);
            if (sd)
                LocalFree(sd);
            return false;
        }

        g_injectionMapping = mapping;
        g_injectionHeader = reinterpret_cast<InjectionFrameHeader*>(view);
        g_injectionPixels = reinterpret_cast<uint8_t*>(view) + InjectionHeaderSize;
        
        // Zero only if clean or contains garbage
        if (g_injectionHeader->Magic != InjectionMagic)
        {
            ZeroMemory(g_injectionHeader, InjectionHeaderSize);
            g_injectionHeader->Magic = InjectionMagic;
            g_injectionHeader->Sequence1 = 1;
            g_injectionHeader->Sequence2 = 1;
            OutputDebugStringA("[WGC_NATIVE] InitializeGlobalInjection: Initialized new header\n");
        }
        else
        {
            OutputDebugStringA("[WGC_NATIVE] InitializeGlobalInjection: Attached to existing valid header\n");
        }

        if (sd)
            LocalFree(sd);

        OutputDebugStringA("[WGC_NATIVE] InitializeGlobalInjection succeeded\n");
        return true;
    }

    static bool InitializeGlobalInjection(DWORD pid)
    {
        return CreateInjectionFileMapping(pid);
    }

    static bool WaitForFirstInjectionFrame(int timeoutMs)
    {
        auto start = std::chrono::steady_clock::now();
        while (std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count() < timeoutMs)
        {
            if (g_injectionHeader && g_injectionHeader->Sequence1 != 0)
            {
                uint32_t seq1 = g_injectionHeader->Sequence1;
                uint32_t seq2 = g_injectionHeader->Sequence2;
                if (seq1 == seq2 && g_injectionHeader->Magic == InjectionMagic && g_injectionHeader->Width > 0 && g_injectionHeader->Height > 0)
                    return true;
            }

            Sleep(50);
        }

        return false;
    }

    static bool InitializeDirectHookCaptureInternal(DWORD pid)
    {
        if (!InitializeGlobalInjection(pid))
            return false;

        const int maxWaitMs = 5000;
        const int retryDelayMs = 100;
        auto start = std::chrono::steady_clock::now();
        bool anyHookInstalled = false;

        while (std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count() < maxWaitMs)
        {
            bool installed = InstallGraphicsHooks();
            anyHookInstalled = anyHookInstalled || installed;
            if (anyHookInstalled && GetModuleHandleW(L"dxgi.dll") && GetModuleHandleW(L"d3d11.dll"))
                break;

            if (anyHookInstalled)
            {
                // Keep trying while the process continues loading graphics modules.
                DebugLog("[WGC_NATIVE] Graphics hooks installed, continuing retry loop for late-loading modules");
            }
            else
            {
                DebugLog("[WGC_NATIVE] Waiting for dxgi/d3d11 modules to load for hook installation");
            }

            Sleep(retryDelayMs);
        }

        if (anyHookInstalled)
        {
            DebugLog("[WGC_NATIVE] InitializeDirectHookCapture: Graphics hooks installed successfully");
        }
        else
        {
            DebugLog("[WGC_NATIVE] InitializeDirectHookCapture: No graphics modules found to hook after waiting. Attempting one last install...");
            anyHookInstalled = InstallGraphicsHooks();
        }
        
        if (!anyHookInstalled)
        {
            DebugLog("[WGC_NATIVE] InstallGraphicsHooks: No graphics hooks placed (disabled or failed). Handshake will continue.");
        }

        DebugLog("[WGC_NATIVE] InitializeDirectHookCapture: Shared memory initialized. Handshake complete.");
        return true;
    }

    static bool TryHookExistingGraphics()
    {
        DebugLog("[WGC_NATIVE] TryHookExistingGraphics: Locating d3d11.dll...");
        HMODULE d3d11 = GetModuleHandleW(L"d3d11.dll");
        if (!d3d11) 
        {
            DebugLog("[WGC_NATIVE] TryHookExistingGraphics: d3d11.dll not loaded in target process");
            return false;
        }

        typedef HRESULT (WINAPI* PFN_D3D11_CREATE_DEVICE)(
            IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT, const D3D_FEATURE_LEVEL*, UINT, UINT, ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);

        // ALWAYS use the direct GetProcAddress pointer here to avoid any hook/trampoline logic
        PFN_D3D11_CREATE_DEVICE pCreateDevice = reinterpret_cast<PFN_D3D11_CREATE_DEVICE>(GetProcAddress(d3d11, "D3D11CreateDevice"));

        if (!pCreateDevice) 
        {
            DebugLog("[WGC_NATIVE] TryHookExistingGraphics: D3D11CreateDevice function not available");
            return false;
        }

        WNDCLASSA wc = {};
        wc.lpfnWndProc = DefWindowProcA;
        wc.hInstance = GetModuleHandle(nullptr);
        wc.lpszClassName = "AES_Dummy_Capture_Class";
        if (!GetClassInfoA(wc.hInstance, wc.lpszClassName, &wc))
            RegisterClassA(&wc);

        HWND dummyHwnd = CreateWindowA(wc.lpszClassName, "AES_Dummy", 0, 0, 0, 1, 1, nullptr, nullptr, wc.hInstance, nullptr);
        if (!dummyHwnd)
        {
            DebugLog("[WGC_NATIVE] TryHookExistingGraphics: Failed to create dummy window");
            return false;
        }

        rt::com_ptr<ID3D11Device> device;
        rt::com_ptr<ID3D11DeviceContext> context;
        D3D_FEATURE_LEVEL featureLevel;

        DebugLog("[WGC_NATIVE] TryHookExistingGraphics: Creating D3D11 Device...");
        HRESULT hr = pCreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, device.put(), &featureLevel, context.put());
        
        if (FAILED(hr))
        {
            hr = pCreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, device.put(), &featureLevel, context.put());
        }

        bool hooked = false;
        if (SUCCEEDED(hr) && device)
        {
            rt::com_ptr<IDXGIDevice> dxgiDevice;
            if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(dxgiDevice.put()))))
            {
                rt::com_ptr<IDXGIAdapter> adapter;
                if (SUCCEEDED(dxgiDevice->GetAdapter(adapter.put())))
                {
                    rt::com_ptr<IDXGIFactory> factory;
                    if (SUCCEEDED(adapter->GetParent(IID_PPV_ARGS(factory.put()))))
                    {
                        HookDxgiFactory(reinterpret_cast<IUnknown*>(factory.get()));
                        
                        DXGI_SWAP_CHAIN_DESC desc = {};
                        desc.BufferCount = 1;
                        desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
                        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
                        desc.OutputWindow = dummyHwnd;
                        desc.SampleDesc.Count = 1;
                        desc.Windowed = TRUE;

                        rt::com_ptr<IDXGISwapChain> swapChain;
                        DebugLog("[WGC_NATIVE] TryHookExistingGraphics: Creating dummy swapchain...");
                        if (SUCCEEDED(factory->CreateSwapChain(device.get(), &desc, swapChain.put())))
                        {
                            DebugLog("[WGC_NATIVE] TryHookExistingGraphics: Dummy swapchain created, hooking vtable...");
                            hooked = HookSwapChain(swapChain.get());
                        }
                    }
                }
            }
        }
        
        DestroyWindow(dummyHwnd);
        UnregisterClassA("AES_Dummy_Capture_Class", GetModuleHandle(nullptr));
        return hooked;
    }

    void SetDirectCompositionStatus(char const* message)
    {
        std::lock_guard<std::mutex> lock(dcompErrorMutex);
        if (message)
            dcompLastError = message;
        else
            dcompLastError.clear();
    }

    static bool InstallDxgiHooks()
    {
        // We no longer use inline hooks on CreateDXGIFactory because they are unstable.
        // VTable hooking via TryHookExistingGraphics is sufficient to catch all swapchains.
        return true;
    }

    static bool InstallD3D11Hooks()
    {
        // Inline hooks on D3D11 exports are unstable without a length disassembler.
        // We rely on VTable hooking via a dummy swapchain instead.
        return true;
    }

    static bool InstallGraphicsHooks()
    {
        // Re-enabling VTable hooking. Inline hooks remain disabled for stability.
        // TryHookExistingGraphics creates a dummy D3D11 swapchain to find the VTable 
        // and patches the Present entries.
        return TryHookExistingGraphics(); 
    }

    static void* GetOriginalPresent1ForSwapChain(IDXGISwapChain* swapChain)
    {
        if (!swapChain)
            return nullptr;

        void** vtable = *reinterpret_cast<void***>(swapChain);
        std::lock_guard<std::mutex> lock(g_hookMutex);
        for (auto& hook : g_swapChainHooks)
        {
            if (hook.vtable == vtable || hook.vtable1 == vtable)
                return hook.originalPresent1;
        }

        return nullptr;
    }

    static HRESULT WINAPI Hooked_Present(IDXGISwapChain* This, UINT SyncInterval, UINT Flags)
    {
        auto original = GetOriginalPresentForSwapChain(This);
        if (!original)
            return E_FAIL;

        CaptureSwapChainFrame(This);

        auto originalFn = reinterpret_cast<HRESULT(WINAPI*)(IDXGISwapChain*, UINT, UINT)>(original);
        return originalFn(This, SyncInterval, Flags);
    }

    static HRESULT WINAPI Hooked_Present1(IDXGISwapChain* This, UINT SyncInterval, UINT PresentFlags, DXGI_PRESENT_PARAMETERS const* pPresentParameters)
    {
        auto original = GetOriginalPresent1ForSwapChain(This);
        if (!original)
            return E_FAIL;

        CaptureSwapChainFrame(This);

        auto originalFn = reinterpret_cast<HRESULT(WINAPI*)(IDXGISwapChain*, UINT, UINT, DXGI_PRESENT_PARAMETERS const*)>(original);
        return originalFn(This, SyncInterval, PresentFlags, pPresentParameters);
    }

    static HRESULT WINAPI Hooked_D3D11CreateDeviceAndSwapChain(
        IDXGIAdapter* pAdapter,
        D3D_DRIVER_TYPE DriverType,
        HMODULE Software,
        UINT Flags,
        const D3D_FEATURE_LEVEL* pFeatureLevels,
        UINT FeatureLevels,
        UINT SDKVersion,
        const DXGI_SWAP_CHAIN_DESC* pSwapChainDesc,
        IDXGISwapChain** ppSwapChain,
        ID3D11Device** ppDevice,
        D3D_FEATURE_LEVEL* pFeatureLevel,
        ID3D11DeviceContext** ppImmediateContext)
    {
        using CreateDeviceAndSwapChainFn = HRESULT(WINAPI*)(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT, const D3D_FEATURE_LEVEL*, UINT, UINT, const DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**, ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);
        auto originalFn = reinterpret_cast<CreateDeviceAndSwapChainFn>(g_originalD3D11CreateDeviceAndSwapChain);
        if (!originalFn)
            return E_FAIL;

        HRESULT hr = originalFn(pAdapter, DriverType, Software, Flags, pFeatureLevels, FeatureLevels, SDKVersion, pSwapChainDesc, ppSwapChain, ppDevice, pFeatureLevel, ppImmediateContext);
        char buf[256];
        sprintf_s(buf, "[WGC_NATIVE] Hooked_D3D11CreateDeviceAndSwapChain returned hr=0x%08X ppSwapChain=%p\n", static_cast<unsigned>(hr), ppSwapChain ? *ppSwapChain : nullptr);
        DebugLog(buf);
        if (SUCCEEDED(hr) && ppSwapChain && *ppSwapChain)
        {
            if (HookSwapChain(*ppSwapChain))
                DebugLog("[WGC_NATIVE] Hooked_D3D11CreateDeviceAndSwapChain hooked swapchain\n");
            else
                DebugLog("[WGC_NATIVE] Hooked_D3D11CreateDeviceAndSwapChain failed to hook swapchain\n");

            // Also ensure factory is hooked if we have a device
            if (ppDevice && *ppDevice)
            {
                rt::com_ptr<IDXGIDevice> dxgiDevice;
                if (SUCCEEDED((*ppDevice)->QueryInterface(IID_PPV_ARGS(dxgiDevice.put()))))
                {
                    rt::com_ptr<IDXGIAdapter> adapter;
                    if (SUCCEEDED(dxgiDevice->GetAdapter(adapter.put())))
                    {
                        rt::com_ptr<IDXGIFactory> factory;
                        if (SUCCEEDED(adapter->GetParent(IID_PPV_ARGS(factory.put()))))
                        {
                            HookDxgiFactory(reinterpret_cast<IUnknown*>(factory.get()));
                        }
                    }
                }
            }
        }

        return hr;
    }

    static HRESULT WINAPI Hooked_D3D11CreateDeviceAndSwapChain1(
        IDXGIAdapter* pAdapter,
        D3D_DRIVER_TYPE DriverType,
        HMODULE Software,
        UINT Flags,
        const D3D_FEATURE_LEVEL* pFeatureLevels,
        UINT FeatureLevels,
        UINT SDKVersion,
        const DXGI_SWAP_CHAIN_DESC1* pSwapChainDesc,
        IDXGISwapChain1** ppSwapChain,
        ID3D11Device** ppDevice,
        D3D_FEATURE_LEVEL* pFeatureLevel,
        ID3D11DeviceContext** ppImmediateContext)
    {
        using CreateDeviceAndSwapChain1Fn = HRESULT(WINAPI*)(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT, const D3D_FEATURE_LEVEL*, UINT, UINT, const DXGI_SWAP_CHAIN_DESC1*, IDXGISwapChain1**, ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);
        auto originalFn = reinterpret_cast<CreateDeviceAndSwapChain1Fn>(g_originalD3D11CreateDeviceAndSwapChain1);
        if (!originalFn)
            return E_FAIL;

        HRESULT hr = originalFn(pAdapter, DriverType, Software, Flags, pFeatureLevels, FeatureLevels, SDKVersion, pSwapChainDesc, ppSwapChain, ppDevice, pFeatureLevel, ppImmediateContext);
        char buf[256];
        sprintf_s(buf, "[WGC_NATIVE] Hooked_D3D11CreateDeviceAndSwapChain1 returned hr=0x%08X ppSwapChain=%p\n", static_cast<unsigned>(hr), ppSwapChain ? *ppSwapChain : nullptr);
        DebugLog(buf);
        if (SUCCEEDED(hr) && ppSwapChain && *ppSwapChain)
        {
            if (HookSwapChain(*reinterpret_cast<IDXGISwapChain**>(*ppSwapChain)))
                DebugLog("[WGC_NATIVE] Hooked_D3D11CreateDeviceAndSwapChain1 hooked swapchain\n");
            else
                DebugLog("[WGC_NATIVE] Hooked_D3D11CreateDeviceAndSwapChain1 failed to hook swapchain\n");
        }

        return hr;
    }

    void MarkDirectCompositionFailed(char const* message)
    {
        dcompState.store(-1);
        if (message)
        {
            SetDirectCompositionStatus(message);
            OutputDebugStringA("[WGC_NATIVE] DirectComposition failure: ");
            OutputDebugStringA(message);
            OutputDebugStringA("\n");
        }
    }

    bool InitializeDirectComposition()
    {
        if (!presentationHwnd)
            return false;

        dcompState.store(1);

        if (!dxgiFactory || !d3dDevice)
        {
            MarkDirectCompositionFailed("missing DXGI factory or D3D11 device");
            return false;
        }

        auto dxgiDevice = d3dDevice.as<IDXGIDevice>();
        HRESULT hr = DCompositionCreateDevice(
            dxgiDevice.get(),
            __uuidof(IDCompositionDevice),
            reinterpret_cast<void**>(dcompDevice.put_void()));
        if (FAILED(hr) || !dcompDevice)
        {
            MarkDirectCompositionFailed("DCompositionCreateDevice failed");
            return false;
        }

        hr = dcompDevice->CreateTargetForHwnd(presentationHwnd, TRUE, dcompTarget.put());
        if (FAILED(hr) || !dcompTarget)
        {
            MarkDirectCompositionFailed("CreateTargetForHwnd failed");
            return false;
        }

        hr = dcompDevice->CreateVisual(dcompVisual.put());
        if (FAILED(hr) || !dcompVisual)
        {
            MarkDirectCompositionFailed("CreateVisual failed");
            return false;
        }

        hr = dcompDevice->CreateScaleTransform(dcompScaleTransform.put());
        if (FAILED(hr) || !dcompScaleTransform)
        {
            MarkDirectCompositionFailed("CreateScaleTransform failed");
            return false;
        }

        hr = dcompVisual->SetTransform(dcompScaleTransform.get());
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("SetTransform failed");
            return false;
        }

        hr = dcompTarget->SetRoot(dcompVisual.get());
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("SetRoot failed");
            return false;
        }

        hr = dcompDevice->Commit();
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("initial Commit failed");
            return false;
        }

        StartDirectCompositionWorker();
        if (!dcompWorkerRunning.load())
            return false;

        StartInjectionWorker();
        return true;
    }

    void StartDirectCompositionWorker()
    {
        if (!presentationHwnd)
            return;

        bool expected = false;
        if (!dcompWorkerRunning.compare_exchange_strong(expected, true))
            return;

        try
        {
            dcompWorker = std::thread([this]() { DirectCompositionWorkerLoop(); });
        }
        catch (...)
        {
            dcompWorkerRunning.store(false);
            MarkDirectCompositionFailed("failed to start DirectComposition worker thread");
        }
    }

    void StopDirectCompositionWorker()
    {
        bool wasRunning = dcompWorkerRunning.exchange(false);
        if (wasRunning)
        {
            dcompWorkerCv.notify_all();
        }

        if (dcompWorker.joinable())
            dcompWorker.join();

        std::lock_guard<std::mutex> lock(dcompWorkerMutex);
        dcompPendingTexture = nullptr;
        dcompPendingWidth = 0;
        dcompPendingHeight = 0;
        dcompHasPendingFrame = false;
    }

    void QueueDirectCompositionFrame(ID3D11Texture2D* texture, int width, int height)
    {
        if (!presentationHwnd || !texture || !dcompWorkerRunning.load())
            return;

        {
            std::lock_guard<std::mutex> lock(dcompWorkerMutex);
            dcompPendingTexture.copy_from(texture);
            dcompPendingWidth = width;
            dcompPendingHeight = height;
            dcompHasPendingFrame = true;
        }

        dcompWorkerCv.notify_one();
    }

    void DirectCompositionWorkerLoop()
    {
        while (dcompWorkerRunning.load())
        {
            rt::com_ptr<ID3D11Texture2D> texture;
            int width = 0;
            int height = 0;
            bool hadFrame = false;

            {
                std::unique_lock<std::mutex> lock(dcompWorkerMutex);
                dcompWorkerCv.wait(lock, [this]()
                {
                    return !dcompWorkerRunning.load() || dcompHasPendingFrame || dcompShaderDirty.load();
                });

                if (!dcompWorkerRunning.load())
                    break;

                hadFrame = dcompHasPendingFrame;
                if (hadFrame)
                {
                    texture = dcompPendingTexture;
                    width = dcompPendingWidth;
                    height = dcompPendingHeight;
                    dcompHasPendingFrame = false;
                }
            }

            if (dcompShaderDirty.load())
            {
                if (!EnsureDirectCompositionRenderer())
                    continue;
            }

            if (hadFrame && texture)
            {
                PresentToDirectComposition(texture.get(), width, height);
            }
        }
    }

    bool EnsureDirectCompositionRenderer()
    {
        bool shaderDirty = dcompShaderDirty.exchange(false);
        if (dcompVertexShader && dcompPixelShader && dcompInputLayout && dcompVertexBuffer && dcompConstantBuffer && dcompSamplerState && dcompRasterizerState && !shaderDirty)
            return true;

        if (shaderDirty)
        {
            dcompPixelShader = nullptr;
        }

        const char* vsSrc =
            "struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD; };"
            "struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD; };"
            "VSOut main(VSIn input) { VSOut o; o.pos = float4(input.pos, 1.0); o.uv = input.uv; return o; }";

        const char* psDefaultSrc =
            "cbuffer Params : register(b0) { float brightness; float saturation; float sourceWidth; float sourceHeight; float4 tint; float outputWidth; float outputHeight; float padding0; float padding1; };"
            "Texture2D src : register(t0);"
            "SamplerState samp : register(s0);"
            "struct PSIn { float4 pos : SV_POSITION; float2 uv : TEXCOORD; };"
            "float4 main(PSIn input) : SV_TARGET {"
            "  float4 col = src.Sample(samp, input.uv);"
            "  col.rgb *= brightness;"
            "  float gray = dot(col.rgb, float3(0.299, 0.587, 0.114));"
            "  col.rgb = lerp(float3(gray, gray, gray), col.rgb, saturation);"
            "  col *= tint;"
            "  return col;"
            "}";

        rt::com_ptr<ID3DBlob> vsBlob;
        rt::com_ptr<ID3DBlob> psBlob;
        rt::com_ptr<ID3DBlob> errBlob;

        HRESULT hr = S_OK;
        if (!dcompVertexShader)
        {
            hr = D3DCompile(vsSrc, (SIZE_T)strlen(vsSrc), nullptr, nullptr, nullptr, "main", "vs_4_0", 0, 0, vsBlob.put(), errBlob.put());
            if (FAILED(hr) || !vsBlob)
            {
                MarkDirectCompositionFailed("vertex shader compile failed");
                return false;
            }

            hr = d3dDevice->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, dcompVertexShader.put());
            if (FAILED(hr))
            {
                MarkDirectCompositionFailed("CreateVertexShader failed");
                return false;
            }

            D3D11_INPUT_ELEMENT_DESC elems[] =
            {
                { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
                { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 12, D3D11_INPUT_PER_VERTEX_DATA, 0 }
            };

            hr = d3dDevice->CreateInputLayout(elems, 2, vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), dcompInputLayout.put());
            if (FAILED(hr))
            {
                MarkDirectCompositionFailed("CreateInputLayout failed");
                return false;
            }
        }

        if (!dcompPixelShader)
        {
            std::wstring path;
            {
                std::lock_guard<std::mutex> lock(dcompShaderMutex);
                path = dcompShaderPath;
            }

            bool compiled = false;
            if (!path.empty())
            {
                std::wstring dbgPath = L"[WGC_NATIVE] Compiling DirectComposition pixel shader from '";
                dbgPath += path;
                dbgPath += L"'\n";
                OutputDebugStringW(dbgPath.c_str());

                hr = D3DCompileFromFile(path.c_str(), nullptr, D3D_COMPILE_STANDARD_FILE_INCLUDE, "main", "ps_4_0", 0, 0, psBlob.put(), errBlob.put());
                if (SUCCEEDED(hr) && psBlob)
                {
                    SetDirectCompositionStatus("Custom DirectComposition shader compile succeeded");
                    ClearShaderCompilerErrorFile();
                    OutputDebugStringA("[WGC_NATIVE] DirectComposition pixel shader compile succeeded\n");
                    compiled = true;
                }
                else if (errBlob)
                {
                    char const* errMsg = (char const*)errBlob->GetBufferPointer();
                    std::string fullMsg = "Pixel shader compile error: ";
                    fullMsg += errMsg;
                    WriteShaderCompilerErrorFile(fullMsg.c_str());
                    OutputDebugStringA(fullMsg.c_str());
                    OutputDebugStringA("\n");
                    MarkDirectCompositionFailed(fullMsg.c_str());
                    // Fallback to default
                }
                else
                {
                    SetDirectCompositionStatus("Custom DirectComposition shader compile failed without compiler error blob");
                    WriteShaderCompilerErrorFile("Custom DirectComposition shader compile failed without compiler error blob");
                    OutputDebugStringA("[WGC_NATIVE] DirectComposition pixel shader compile failed without compiler error blob\n");
                }
            }

            if (!compiled)
            {
                if (path.empty())
                {
                    SetDirectCompositionStatus("Using default DirectComposition pixel shader (no custom shader selected)");
                    ClearShaderCompilerErrorFile();
                }
                else if (dcompState.load() != -1)
                    SetDirectCompositionStatus("Falling back to default DirectComposition pixel shader");
                OutputDebugStringA("[WGC_NATIVE] Falling back to default DirectComposition pixel shader\n");
                hr = D3DCompile(psDefaultSrc, (SIZE_T)strlen(psDefaultSrc), nullptr, nullptr, nullptr, "main", "ps_4_0", 0, 0, psBlob.put(), errBlob.put());
                if (FAILED(hr) || !psBlob)
                {
                    MarkDirectCompositionFailed("default pixel shader compile failed");
                    return false;
                }
            }

            hr = d3dDevice->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, dcompPixelShader.put());
            if (FAILED(hr))
            {
                MarkDirectCompositionFailed("CreatePixelShader failed");
                return false;
            }
        }

        if (!dcompVertexBuffer)
        {
            D3D11_BUFFER_DESC vbDesc = {};
            vbDesc.ByteWidth = sizeof(DcompVertex) * 4;
            vbDesc.Usage = D3D11_USAGE_DYNAMIC;
            vbDesc.BindFlags = D3D11_BIND_VERTEX_BUFFER;
            vbDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
            hr = d3dDevice->CreateBuffer(&vbDesc, nullptr, dcompVertexBuffer.put());
            if (FAILED(hr))
            {
                MarkDirectCompositionFailed("CreateBuffer vertex failed");
                return false;
            }
        }

        D3D11_BUFFER_DESC cbDesc = {};
        cbDesc.Usage = D3D11_USAGE_DEFAULT;
        cbDesc.ByteWidth = sizeof(DcompConstants);
        cbDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        hr = d3dDevice->CreateBuffer(&cbDesc, nullptr, dcompConstantBuffer.put());
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("CreateBuffer constant failed");
            return false;
        }

        D3D11_SAMPLER_DESC samplerDesc = {};
        samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        samplerDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.MaxLOD = D3D11_FLOAT32_MAX;
        hr = d3dDevice->CreateSamplerState(&samplerDesc, dcompSamplerState.put());
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("CreateSamplerState failed");
            return false;
        }

        D3D11_RASTERIZER_DESC rasterizerDesc = {};
        rasterizerDesc.FillMode = D3D11_FILL_SOLID;
        rasterizerDesc.CullMode = D3D11_CULL_NONE;
        rasterizerDesc.FrontCounterClockwise = FALSE;
        rasterizerDesc.DepthClipEnable = TRUE;
        rasterizerDesc.ScissorEnable = FALSE;
        rasterizerDesc.MultisampleEnable = FALSE;
        rasterizerDesc.AntialiasedLineEnable = FALSE;
        hr = d3dDevice->CreateRasterizerState(&rasterizerDesc, dcompRasterizerState.put());
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("CreateRasterizerState failed");
            return false;
        }

        return true;
    }

    bool EnsureDirectCompositionSwapChain(int width, int height)
    {
        if (!presentationHwnd || !dcompDevice || !dxgiFactory)
            return false;

        RECT rc{};
        if (!GetClientRect(presentationHwnd, &rc))
            return false;

        int clientWidth = static_cast<int>(rc.right - rc.left);
        int clientHeight = static_cast<int>(rc.bottom - rc.top);
        int targetWidth = (std::max)(1, clientWidth);
        int targetHeight = (std::max)(1, clientHeight);

        if (dcompScaleTransform)
        {
            dcompScaleTransform->SetScaleX(1.0f);
            dcompScaleTransform->SetScaleY(1.0f);
            dcompScaleTransform->SetCenterX(0.0f);
            dcompScaleTransform->SetCenterY(0.0f);
        }

        if (dcompSwapChain && dcompWidth == targetWidth && dcompHeight == targetHeight)
            return true;

        dcompSwapChain = nullptr;
        dcompRenderTargetView = nullptr;
        dcompWidth = 0;
        dcompHeight = 0;

        DXGI_SWAP_CHAIN_DESC1 desc = {};
        desc.Width = targetWidth;
        desc.Height = targetHeight;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.SampleDesc.Count = 1;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        desc.Scaling = DXGI_SCALING_STRETCH;
        desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

        HRESULT hr = dxgiFactory->CreateSwapChainForComposition(
            d3dDevice.get(),
            &desc,
            nullptr,
            dcompSwapChain.put());
        if (FAILED(hr) || !dcompSwapChain)
        {
            MarkDirectCompositionFailed("CreateSwapChainForComposition failed");
            return false;
        }

        hr = dcompVisual->SetContent(dcompSwapChain.get());
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("SetContent failed");
            dcompSwapChain = nullptr;
            return false;
        }

        hr = dcompDevice->Commit();
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("Commit after swapchain creation failed");
            dcompSwapChain = nullptr;
            dcompRenderTargetView = nullptr;
            return false;
        }

        rt::com_ptr<ID3D11Texture2D> backBuffer;
        hr = dcompSwapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), backBuffer.put_void());
        if (FAILED(hr) || !backBuffer)
        {
            MarkDirectCompositionFailed("GetBuffer for cached RTV failed");
            dcompSwapChain = nullptr;
            dcompRenderTargetView = nullptr;
            return false;
        }

        hr = d3dDevice->CreateRenderTargetView(backBuffer.get(), nullptr, dcompRenderTargetView.put());
        if (FAILED(hr) || !dcompRenderTargetView)
        {
            MarkDirectCompositionFailed("CreateRenderTargetView for cached RTV failed");
            dcompSwapChain = nullptr;
            dcompRenderTargetView = nullptr;
            return false;
        }

        dcompWidth = targetWidth;
        dcompHeight = targetHeight;
        return true;
    }

    void PresentToDirectComposition(ID3D11Texture2D* texture, int width, int height)
    {
        if (!presentationHwnd || !texture || !d3dContext)
            return;

        if (!EnsureDirectCompositionSwapChain(width, height))
            return;

        if (!EnsureDirectCompositionRenderer())
            return;

        if (!dcompRenderTargetView)
        {
            MarkDirectCompositionFailed("cached render target view missing");
            return;
        }

        HRESULT hr = S_OK;

        D3D11_TEXTURE2D_DESC texDesc{};
        texture->GetDesc(&texDesc);

        rt::com_ptr<ID3D11ShaderResourceView> srv;
        hr = d3dDevice->CreateShaderResourceView(texture, nullptr, srv.put());
        if (FAILED(hr) || !srv)
        {
            MarkDirectCompositionFailed("CreateShaderResourceView failed");
            return;
        }

        float viewAspect = dcompHeight > 0 ? static_cast<float>(dcompWidth) / static_cast<float>(dcompHeight) : 1.0f;
        float frameAspect = height > 0 ? static_cast<float>(width) / static_cast<float>(height) : 1.0f;
        float left = -1.0f;
        float right = 1.0f;
        float top = 1.0f;
        float bottom = -1.0f;
        float u0 = 0.0f;
        float v0 = 0.0f;
        float u1 = 1.0f;
        float v1 = 1.0f;
        int stretch = dcompStretch.load();

        if (stretch == 1)
        {
            if (frameAspect > viewAspect)
            {
                float scaleY = viewAspect / frameAspect;
                top = scaleY;
                bottom = -scaleY;
            }
            else
            {
                float scaleX = frameAspect / viewAspect;
                left = -scaleX;
                right = scaleX;
            }
        }
        else if (stretch == 2)
        {
            if (frameAspect > viewAspect)
            {
                float crop = (1.0f - (viewAspect / frameAspect)) * 0.5f;
                u0 = crop;
                u1 = 1.0f - crop;
            }
            else
            {
                float crop = (1.0f - (frameAspect / viewAspect)) * 0.5f;
                v0 = crop;
                v1 = 1.0f - crop;
            }
        }

        DcompVertex vertices[4] =
        {
            { left,  top,    0.0f, u0, v0 },
            { right, top,    0.0f, u1, v0 },
            { left,  bottom, 0.0f, u0, v1 },
            { right, bottom, 0.0f, u1, v1 }
        };

        D3D11_MAPPED_SUBRESOURCE mapped{};
        hr = d3dContext->Map(dcompVertexBuffer.get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("Map vertex buffer failed");
            return;
        }

        memcpy(mapped.pData, vertices, sizeof(vertices));
        d3dContext->Unmap(dcompVertexBuffer.get(), 0);

        DcompConstants constants{};
        constants.brightness = dcompBrightness.load();
        constants.saturation = dcompSaturation.load();
        constants.sourceWidth = static_cast<float>(width);
        constants.sourceHeight = static_cast<float>(height);
        constants.tint[0] = dcompTintR.load();
        constants.tint[1] = dcompTintG.load();
        constants.tint[2] = dcompTintB.load();
        constants.tint[3] = dcompTintA.load();
        constants.outputWidth = static_cast<float>(dcompWidth);
        constants.outputHeight = static_cast<float>(dcompHeight);
        d3dContext->UpdateSubresource(dcompConstantBuffer.get(), 0, nullptr, &constants, 0, 0);

        D3D11_VIEWPORT viewport{};
        viewport.Width = static_cast<float>(dcompWidth);
        viewport.Height = static_cast<float>(dcompHeight);
        viewport.MinDepth = 0.0f;
        viewport.MaxDepth = 1.0f;

        float clearColor[4] = { 0, 0, 0, 1 };
        ID3D11RenderTargetView* rtvPtr = dcompRenderTargetView.get();
        d3dContext->OMSetRenderTargets(1, &rtvPtr, nullptr);
        d3dContext->ClearRenderTargetView(dcompRenderTargetView.get(), clearColor);
        d3dContext->RSSetState(dcompRasterizerState.get());
        d3dContext->RSSetViewports(1, &viewport);

        UINT stride = sizeof(DcompVertex);
        UINT offset = 0;
        ID3D11Buffer* vb = dcompVertexBuffer.get();
        ID3D11ShaderResourceView* srvPtr = srv.get();
        ID3D11SamplerState* samplerPtr = dcompSamplerState.get();
        ID3D11Buffer* cbPtr = dcompConstantBuffer.get();

        d3dContext->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        d3dContext->IASetInputLayout(dcompInputLayout.get());
        d3dContext->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
        d3dContext->VSSetShader(dcompVertexShader.get(), nullptr, 0);
        d3dContext->PSSetShader(dcompPixelShader.get(), nullptr, 0);
        d3dContext->PSSetShaderResources(0, 1, &srvPtr);
        d3dContext->PSSetSamplers(0, 1, &samplerPtr);
        d3dContext->PSSetConstantBuffers(0, 1, &cbPtr);
        d3dContext->Draw(4, 0);

        ID3D11ShaderResourceView* nullSrv = nullptr;
        d3dContext->PSSetShaderResources(0, 1, &nullSrv);

        hr = dcompSwapChain->Present(1, 0);
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("Present failed");
            return;
        }

        hr = dcompDevice->Commit();
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("Commit after present failed");
            return;
        }

        dcompState.store(2);
        dcompPresentCount.fetch_add(1);
    }

    // OnFrameArrived
    // Called by the WinRT frame pool when a new frame is available.
    // This method performs the following steps:
    //  - Acquire the D3D11 texture for the incoming frame
    //  - Optionally crop the texture into an internal cropped texture
    //  - Optionally scale the source using a GPU shader pipeline
    //  - If scaling is used, perform a GPU->CPU readback via a staging
    //    texture and copy pixels into the internal back buffer.
    //  - Otherwise, use a CPU staging texture to map and copy pixels.
    //  - Swap the prepared pixel buffer into `latestData` when there are
    //    no active readers to ensure safe access from managed code.
    // The method avoids blocking managed readers and drops frames if the
    // buffer is in use by callers.
    void OnFrameArrived(wgc::Direct3D11CaptureFramePool const& sender)
    {
        if (closing.load())
        {
            return;
        }

        int localCropX = cropX.load();
        int localCropY = cropY.load();
        int localCropW = cropW.load();
        int localCropH = cropH.load();

        auto frame = sender.TryGetNextFrame();
        if (!frame)
        {
            return;
        }

        try
        {
            auto surface = frame.Surface();
            if (!surface)
            {
                frame.Close();
                return;
            }

            auto access = surface.as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            if (!access)
            {
                frame.Close();
                return;
            }

            rt::com_ptr<ID3D11Texture2D> gpuTexture;
            if (FAILED(access->GetInterface(rt::guid_of<ID3D11Texture2D>(), gpuTexture.put_void())))
            {
                frame.Close();
                return;
            }

            D3D11_TEXTURE2D_DESC desc;
            gpuTexture->GetDesc(&desc);

            if ((int)desc.Width <= 0 || (int)desc.Height <= 0)
            {
                frame.Close();
                return;
            }

            if ((int)desc.Width > 8192 || (int)desc.Height > 8192)
            {
                frame.Close();
                return;
            }

            rt::com_ptr<ID3D11Texture2D> currentGpu = gpuTexture;
            int currentW = desc.Width;
            int currentH = desc.Height;

            // GPU Crop if requested
            if (localCropW > 0 && localCropH > 0)
            {
                if (!croppedTexture || croppedTextureWidth != localCropW || croppedTextureHeight != localCropH)
                {
                    D3D11_TEXTURE2D_DESC cd = desc;
                    cd.Width = localCropW;
                    cd.Height = localCropH;
                    cd.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
                    cd.MiscFlags = D3D11_RESOURCE_MISC_SHARED;

                    if (SUCCEEDED(d3dDevice->CreateTexture2D(&cd, nullptr, croppedTexture.put())))
                    {
                        croppedTextureWidth = localCropW;
                        croppedTextureHeight = localCropH;
                    }
                }

                if (croppedTexture)
                {
                    D3D11_BOX box = { (UINT)localCropX, (UINT)localCropY, 0, (UINT)(localCropX + localCropW), (UINT)(localCropY + localCropH), 1 };
                    d3dContext->CopySubresourceRegion(croppedTexture.get(), 0, 0, 0, 0, gpuTexture.get(), 0, &box);
                    currentGpu = croppedTexture;
                    currentW = localCropW;
                    currentH = localCropH;
                }
            }

            latestGpuTexture = currentGpu;

            if (presentationHwnd)
            {
                QueueDirectCompositionFrame(currentGpu.get(), currentW, currentH);
            }

            // Interop-only fast path:
            // when enabled, keep everything on GPU and avoid CPU staging/readback work.
            if (interopEnabled.load(std::memory_order_relaxed))
            {
                width.store(currentW);
                height.store(currentH);
                frameCount.fetch_add(1);

                if (swapChain && vrrEnabled)
                {
                    // Optional VRR timing signal; keep disabled unless explicitly requested.
                    swapChain->Present(0, DXGI_PRESENT_ALLOW_TEARING);
                }

                frame.Close();
                return;
            }

            bool frameUpdated = false;
            bool useScaler = (maxWidth > 0 && maxHeight > 0 && (currentW > maxWidth || currentH > maxHeight));

            if (useScaler)
            {
                // Calculate target dimensions while preserving aspect ratio
                // Use (std::min/max) to avoid conflict with windows.h min/max macros
                float scale = (std::min)((float)maxWidth / currentW, (float)maxHeight / currentH);
                int targetW = (std::max)(1, (int)(currentW * scale));
                int targetH = (std::max)(1, (int)(currentH * scale));

                try
                {
                    if (!vs || !ps)
                    {
                        const char* vsSrc = "struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD; }; struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD; }; VSOut main(VSIn input) { VSOut o; o.pos = float4(input.pos, 1.0); o.uv = input.uv; return o; }";
                        const char* psSrc = "Texture2D src : register(t0); SamplerState samp : register(s0); struct PSIn { float4 pos : SV_POSITION; float2 uv : TEXCOORD; }; float4 main(PSIn i) : SV_TARGET { return src.Sample(samp, i.uv); }";

                        rt::com_ptr<ID3DBlob> vsBlob, psBlob, errBlob;

                        D3DCompile(vsSrc, (SIZE_T)strlen(vsSrc), nullptr, nullptr, nullptr, "main", "vs_4_0", 0, 0, vsBlob.put(), errBlob.put());
                        D3DCompile(psSrc, (SIZE_T)strlen(psSrc), nullptr, nullptr, nullptr, "main", "ps_4_0", 0, 0, psBlob.put(), errBlob.put());

                        d3dDevice->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, vs.put());
                        d3dDevice->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, ps.put());

                        D3D11_INPUT_ELEMENT_DESC elems[] =
                        {
                            { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
                            { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 12, D3D11_INPUT_PER_VERTEX_DATA, 0 }
                        };

                        d3dDevice->CreateInputLayout(elems, 2, vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), inputLayout.put());

                        struct V { float x, y, z, u, v; } quad[6] =
                        {
                            { -1, -1, 0, 0, 1 }, { -1, 1, 0, 0, 0 }, { 1, 1, 0, 1, 0 },
                            { -1, -1, 0, 0, 1 }, { 1, 1, 0, 1, 0 }, { 1, -1, 0, 1, 1 }
                        };

                        D3D11_BUFFER_DESC bd = {};
                        bd.ByteWidth = sizeof(quad);
                        bd.Usage = D3D11_USAGE_DEFAULT;
                        bd.BindFlags = D3D11_BIND_VERTEX_BUFFER;

                        D3D11_SUBRESOURCE_DATA sd = { quad, 0, 0 };
                        d3dDevice->CreateBuffer(&bd, &sd, quadVB.put());

                        D3D11_SAMPLER_DESC sdsc = {};
                        sdsc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
                        sdsc.AddressU = sdsc.AddressV = sdsc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
                        d3dDevice->CreateSamplerState(&sdsc, samplerState.put());
                    }

                    if (!scaledTexture || (int)scaledTextureDesc.Width != targetW || (int)scaledTextureDesc.Height != targetH)
                    {
                        D3D11_TEXTURE2D_DESC td = {};
                        td.Width = (UINT)targetW;
                        td.Height = (UINT)targetH;
                        td.MipLevels = 1;
                        td.ArraySize = 1;
                        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                        td.SampleDesc.Count = 1;
                        td.Usage = D3D11_USAGE_DEFAULT;
                        td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
                        td.MiscFlags = D3D11_RESOURCE_MISC_SHARED;

                        d3dDevice->CreateTexture2D(&td, nullptr, scaledTexture.put());
                        d3dDevice->CreateRenderTargetView(scaledTexture.get(), nullptr, scaledRTV.put());
                        scaledTextureDesc = td;
                    }

                    d3dDevice->CreateShaderResourceView(currentGpu.get(), nullptr, tempSRV.put());

                    ID3D11RenderTargetView* rtv = scaledRTV.get();
                    d3dContext->OMSetRenderTargets(1, &rtv, nullptr);

                    D3D11_VIEWPORT vp = { 0, 0, (FLOAT)targetW, (FLOAT)targetH, 0, 1 };
                    d3dContext->RSSetViewports(1, &vp);

                    UINT stride = sizeof(float) * 5;
                    UINT offset = 0;
                    ID3D11Buffer* vb = quadVB.get();
                    d3dContext->IASetVertexBuffers(0, 1, &vb, &stride, &offset);

                    d3dContext->IASetInputLayout(inputLayout.get());
                    d3dContext->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                    d3dContext->VSSetShader(vs.get(), nullptr, 0);
                    d3dContext->PSSetShader(ps.get(), nullptr, 0);

                    ID3D11ShaderResourceView* srv = tempSRV.get();
                    d3dContext->PSSetShaderResources(0, 1, &srv);

                    ID3D11SamplerState* samp = samplerState.get();
                    d3dContext->PSSetSamplers(0, 1, &samp);

                    d3dContext->Draw(6, 0);

                    ID3D11ShaderResourceView* nullSrv = nullptr;
                    d3dContext->PSSetShaderResources(0, 1, &nullSrv);

                    latestGpuTexture = scaledTexture;

                    D3D11_TEXTURE2D_DESC rd = scaledTextureDesc;
                    rd.Usage = D3D11_USAGE_STAGING;
                    rd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                    rd.BindFlags = 0;
                    rd.MiscFlags = 0; // Staging cannot be shared

                    rt::com_ptr<ID3D11Texture2D> readback;
                    if (SUCCEEDED(d3dDevice->CreateTexture2D(&rd, nullptr, readback.put())))
                    {
                        d3dContext->CopyResource(readback.get(), scaledTexture.get());

                        D3D11_MAPPED_SUBRESOURCE m;
                        if (SUCCEEDED(d3dContext->Map(readback.get(), 0, D3D11_MAP_READ, 0, &m)))
                        {
                            size_t rs = (size_t)targetW * 4;
                            size_t ts = rs * targetH;

                            if (backBuffer.capacity() < ts)
                                backBuffer.reserve(ts);

                            backBuffer.resize(ts);

                            if ((size_t)m.RowPitch == rs)
                            {
                                memcpy(backBuffer.data(), m.pData, ts);
                            }
                            else
                            {
                                for (int y = 0; y < targetH; y++)
                                    memcpy(backBuffer.data() + (y * rs), (uint8_t*)m.pData + (y * m.RowPitch), rs);
                            }

                            d3dContext->Unmap(readback.get(), 0);

                            int curReaders = readers.load(std::memory_order_acquire);
                            if (curReaders == 0)
                            {
                                WriteInjectionFrame(backBuffer.data(), ts, targetW, targetH, static_cast<int>(rs));
                                std::lock_guard<std::mutex> lock(dataMutex);
                                if (readers.load(std::memory_order_relaxed) == 0)
                                {
                                    latestData.swap(backBuffer);
                                    width.store(targetW);
                                    height.store(targetH);
                                    frameCount.fetch_add(1);
                                    frameUpdated = true;
                                }
                            }
                            else
                            {
                                char rlog[128];
                                sprintf_s(rlog, "[WGC_NATIVE] Frame dropped (scaler): readers=%d\n", curReaders);
                                OutputDebugStringA(rlog);
                            }
                        }
                    }

                    tempSRV = nullptr;
                }
                catch (...) { tempSRV = nullptr; }
            }

            if (!frameUpdated)
            {
                int copyW = currentW;
                int copyH = currentH;

                // Use a local staging copy of the data before swapping to avoid reader contention
                std::vector<unsigned char> localBuffer;

                if (!stagingTexture || stagingTextureWidth != copyW || stagingTextureHeight != copyH)
                {
                    D3D11_TEXTURE2D_DESC sd = desc;
                    sd.Usage = D3D11_USAGE_STAGING;
                    sd.BindFlags = 0;
                    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                    sd.MiscFlags = 0;
                    sd.Width = copyW;
                    sd.Height = copyH;

                    if (FAILED(d3dDevice->CreateTexture2D(&sd, nullptr, stagingTexture.put())))
                    {
                        frame.Close();
                        return;
                    }

                    stagingTextureWidth = copyW;
                    stagingTextureHeight = copyH;
                }

                d3dContext->CopyResource(stagingTexture.get(), currentGpu.get());

                D3D11_MAPPED_SUBRESOURCE m;
                if (SUCCEEDED(d3dContext->Map(stagingTexture.get(), 0, D3D11_MAP_READ, 0, &m)))
                {
                    size_t rs = (size_t)copyW * 4;
                    size_t ts = rs * copyH;

                    localBuffer.resize(ts);

                    if ((size_t)m.RowPitch == rs)
                    {
                        memcpy(localBuffer.data(), m.pData, ts);
                    }
                    else
                    {
                        for (int y = 0; y < copyH; y++)
                            memcpy(localBuffer.data() + (y * rs), (uint8_t*)m.pData + (y * m.RowPitch), rs);
                    }

                    d3dContext->Unmap(stagingTexture.get(), 0);

                    // Only swap if no readers are currently looking at the data
                    if (readers.load(std::memory_order_acquire) == 0)
                    {
                        WriteInjectionFrame(localBuffer.data(), ts, copyW, copyH, static_cast<int>(rs));
                        std::lock_guard<std::mutex> lock(dataMutex);
                        if (readers.load(std::memory_order_relaxed) == 0)
                        {
                            latestData.swap(localBuffer);
                            width.store(copyW);
                            height.store(copyH);
                            frameCount.fetch_add(1);
                        }
                    }
                }
            }

            if (swapChain && vrrEnabled) {
                // Presenting on a dedicated swapchain for the current monitor 
                // tells the DWM/GPU about our desired refresh rate for VRR.
                // We don't care about the backbuffer content, just the timing.
                swapChain->Present(0, DXGI_PRESENT_ALLOW_TEARING);
            }

        } catch (...) { }
        frame.Close();
    }

    bool InitializeInjection(DWORD pid)
    {
        if (injectionMapping || injectionHeader)
            return true;

        auto mapName = MakeInjectionSharedMemoryName(pid);
        OutputDebugStringA("[WGC_NATIVE] InitializeInjection creating shared memory\n");
        HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, static_cast<DWORD>(InjectionHeaderSize + InjectionMaxFrameBytes), mapName.c_str());
        if (!mapping)
        {
            OutputDebugStringA("[WGC_NATIVE] InitializeInjection CreateFileMappingW failed\n");
            return false;
        }

        void* view = MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, InjectionHeaderSize + InjectionMaxFrameBytes);
        if (!view)
        {
            OutputDebugStringA("[WGC_NATIVE] InitializeInjection MapViewOfFile failed\n");
            CloseHandle(mapping);
            return false;
        }

        injectionMapping = mapping;
        injectionHeader = reinterpret_cast<InjectionFrameHeader*>(view);
        injectionPixels = reinterpret_cast<uint8_t*>(view) + InjectionHeaderSize;
        ZeroMemory(injectionHeader, InjectionHeaderSize);
        injectionHeader->Magic = InjectionMagic;
        OutputDebugStringA("[WGC_NATIVE] InitializeInjection succeeded\n");
        return true;
    }

    void WriteInjectionFrame(void const* data, size_t size, int width, int height, int stride)
    {
        if (!injectionHeader || !data || size == 0 || width <= 0 || height <= 0)
            return;

        if (size > InjectionMaxFrameBytes)
            return;

        uint32_t seq = static_cast<uint32_t>(injectionFrameCounter.fetch_add(1, std::memory_order_relaxed) + 1);
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&injectionHeader->Sequence1), 0);
        injectionHeader->Width = static_cast<uint32_t>(width);
        injectionHeader->Height = static_cast<uint32_t>(height);
        injectionHeader->Stride = static_cast<uint32_t>(stride);
        injectionHeader->FrameCounter = seq;
        memcpy(injectionPixels, data, size);
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&injectionHeader->Sequence2), seq);
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&injectionHeader->Sequence1), seq);
    }
};

bool InitializeDirectHookCapture(DWORD pid)
{
    return CaptureSession::InitializeDirectHookCaptureInternal(pid);
}

void* s_injectionSession = nullptr;

struct InjectionWindowSearchData
{
    DWORD processId;
    HWND bestHwnd;
    long bestScore;
};

static HWND FindBestCaptureWindowForCurrentProcess()
{
    InjectionWindowSearchData data{ GetCurrentProcessId(), nullptr, LONG_MIN };

    EnumWindows([](HWND hwnd, LPARAM lParam) -> BOOL
    {
        auto searchData = reinterpret_cast<InjectionWindowSearchData*>(lParam);
        DWORD windowPid = 0;
        if (!GetWindowThreadProcessId(hwnd, &windowPid) || windowPid != searchData->processId)
            return TRUE;

        RECT rect;
        if (!GetWindowRect(hwnd, &rect))
            return TRUE;

        int width = rect.right - rect.left;
        int height = rect.bottom - rect.top;
        if (width <= 0 || height <= 0)
            return TRUE;

        long score = (long)width * height;
        if (IsWindowVisible(hwnd))
            score += 100000;
        if (GetWindow(hwnd, GW_OWNER) == nullptr)
            score += 1000000;
        if (hwnd == GetForegroundWindow())
            score += 500000;

        if (score > searchData->bestScore)
        {
            searchData->bestScore = score;
            searchData->bestHwnd = hwnd;
        }

        return TRUE;
    }, reinterpret_cast<LPARAM>(&data));

    return data.bestHwnd;
}

extern "C" void* CreateCaptureSessionInternal(HWND targetHwnd, HWND presentationHwnd);

static DWORD WINAPI InjectionThreadMain(LPVOID lpParameter)
{
    DWORD pid = GetCurrentProcessId();
    auto requestName = CaptureSession::MakeInjectionRequestEventName(pid);
    HANDLE requestEvent = OpenEventW(SYNCHRONIZE, FALSE, requestName.c_str());
    if (!requestEvent)
        return 0;

    CloseHandle(requestEvent);

    HWND hwnd = FindBestCaptureWindowForCurrentProcess();
    if (!hwnd)
        return 0;

    void* session = CreateCaptureSessionInternal(hwnd, nullptr);
    if (!session)
        return 0;

    auto readyName = CaptureSession::MakeInjectionReadyEventName(pid);
    HANDLE readyEvent = CreateEventW(nullptr, TRUE, FALSE, readyName.c_str());
    if (readyEvent)
    {
        SetEvent(readyEvent);
        CloseHandle(readyEvent);
    }

    s_injectionSession = session;
    return 0;
}

extern "C" {
    void* CreateCaptureSessionInternal(HWND targetHwnd, HWND presentationHwnd)
    {
        try
        {
            OutputDebugStringA("[WGC_NATIVE] CreateCaptureSession start\n");

            // Ensure WinRT is initialized for this thread (MTA is appropriate for capture APIs)
            try
            {
                rt::init_apartment(rt::apartment_type::multi_threaded);
                OutputDebugStringA("[WGC_NATIVE] winrt::init_apartment(MTA) succeeded\n");
            }
            catch (...)
            {
                OutputDebugStringA("[WGC_NATIVE] winrt::init_apartment failed or already initialized\n");
            }

            auto s = new CaptureSession();
            s->presentationHwnd = presentationHwnd;

            HRESULT hr = D3D11CreateDevice(
                nullptr,
                D3D_DRIVER_TYPE_HARDWARE,
                nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                nullptr,
                0,
                D3D11_SDK_VERSION,
                s->d3dDevice.put(),
                nullptr,
                s->d3dContext.put());

            if (FAILED(hr))
            {
                char buf[256];
                sprintf_s(buf, "[WGC_NATIVE] D3D11CreateDevice failed: 0x%08X (Note: Host may need to run as Admin if target is Admin)\n", (unsigned)hr);
                DebugLog(buf);
                delete s;
                return nullptr;
            }

            OutputDebugStringA("[WGC_NATIVE] D3D11CreateDevice succeeded\n");

            try
            {
                auto mt = s->d3dDevice.as<ID3D11Multithread>();
                if (mt)
                {
                    mt->SetMultithreadProtected(TRUE);
                }

                // Create a background swapchain to support VRR (Variable Refresh Rate)
                // This signals the GPU about frame arrival times.
                rt::com_ptr<IDXGIDevice2> dxgiDevice = s->d3dDevice.as<IDXGIDevice2>();

                try
                {
                    rt::com_ptr<IDXGIAdapter> adapter;
                    dxgiDevice->GetAdapter(adapter.put());
                    DXGI_ADAPTER_DESC adapterDesc{};
                    if (SUCCEEDED(adapter->GetDesc(&adapterDesc)))
                    {
                        s->adapterDescription = adapterDesc.Description;
                        switch (adapterDesc.VendorId)
                        {
                        case 0x10DE: s->adapterVendor = L"NVIDIA"; break;
                        case 0x1002:
                        case 0x1022: s->adapterVendor = L"AMD"; break;
                        case 0x8086: s->adapterVendor = L"Intel"; break;
                        case 0x1414: s->adapterVendor = L"Microsoft"; break;
                        default: s->adapterVendor = L"Unknown"; break;
                        }
                    }

                    rt::com_ptr<IDXGIFactory2> factory;
                    adapter->GetParent(rt::guid_of<IDXGIFactory2>(), factory.put_void());
                    s->dxgiFactory = factory;

                    DXGI_SWAP_CHAIN_DESC1 desc = {};
                    desc.Width = 16;
                    desc.Height = 16; // Smallest possible
                    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
                    desc.BufferCount = 2;
                    desc.Scaling = DXGI_SCALING_STRETCH;
                    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
                    desc.AlphaMode = DXGI_ALPHA_MODE_UNSPECIFIED;
                    desc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;

                    // We use the same targetHwnd. Even though we never show this swapchain,
                    // presenting to it allows us to utilize the tearing flag for VRR.
                    factory->CreateSwapChainForHwnd(s->d3dDevice.get(), targetHwnd, &desc, nullptr, nullptr, s->swapChain.put());
                }
                catch (...) {
                    OutputDebugStringA("[WGC_NATIVE] Failed to create VRR swapchain\n");
                }

                rt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.get(), reinterpret_cast<::IInspectable**>(rt::put_abi(s->winrtDevice))));
                OutputDebugStringA("[WGC_NATIVE] Created winrt D3D device wrapper\n");
            }
            catch (...) {
                OutputDebugStringA("[WGC_NATIVE] CreateDirect3D11DeviceFromDXGIDevice failed\n");
                delete s;
                return nullptr;
            }

            if (s->presentationHwnd)
            {
                s->interopEnabled.store(true, std::memory_order_relaxed);
                s->InitializeDirectComposition();
            }

            // Create capture item for the target HWND
            try
            {
                auto factory = rt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
                if (!factory)
                {
                    OutputDebugStringA("[WGC_NATIVE] Failed to get IGraphicsCaptureItemInterop factory\n");
                    delete s;
                    return nullptr;
                }

                HRESULT createHr = factory->CreateForWindow(targetHwnd, rt::guid_of<ABI::Windows::Graphics::Capture::IGraphicsCaptureItem>(), rt::put_abi(s->item));
                if (FAILED(createHr) || !s->item)
                {
                    char buf[256];
                    sprintf_s(buf, "[WGC_NATIVE] CreateForWindow failed: 0x%08X. Ensure target HWND is valid and process has required permissions.\n", (unsigned)createHr);
                    DebugLog(buf);
                    delete s;
                    return nullptr;
                }

                OutputDebugStringA("[WGC_NATIVE] CreateForWindow succeeded\n");
            }
            catch (...) {
                OutputDebugStringA("[WGC_NATIVE] Exception during CreateForWindow\n");
                delete s;
                return nullptr;
            }

            try
            {
                auto size = s->item.Size();
                char buf[256];
                sprintf_s(buf, "[WGC_NATIVE] Capture item size: %d x %d\n", (int)size.Width, (int)size.Height);
                OutputDebugStringA(buf);

                // Use 10 buffers for high refresh rate stability
                s->framePool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(
                    s->winrtDevice,
                    rt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
                    10,
                    size);

                if (!s->framePool)
                {
                    OutputDebugStringA("[WGC_NATIVE] Failed to create framePool\n");
                    delete s;
                    return nullptr;
                }

                s->frameToken = s->framePool.FrameArrived([s](auto const& sender, auto const&) { s->OnFrameArrived(sender); });

                // Track item closing
                s->closeToken = s->item.Closed([s](auto const&, auto const&)
                {
                    OutputDebugStringA("[WGC_NATIVE] Capture Item CLOSED internally by system\n");
                });

                s->session = s->framePool.CreateCaptureSession(s->item);
                if (!s->session)
                {
                    OutputDebugStringA("[WGC_NATIVE] Failed to create session\n");
                    delete s;
                    return nullptr;
                }

                // Try to disable cursor composition in the capture session if the API is available
                try
                {
                    auto session2 = s->session.try_as<wgc::IGraphicsCaptureSession2>();
                    if (session2)
                    {
                        session2.IsCursorCaptureEnabled(false);
                        OutputDebugStringA("[WGC_NATIVE] Disabled cursor composition via IGraphicsCaptureSession2\n");
                    }
                }
                catch(...) { OutputDebugStringA("[WGC_NATIVE] IGraphicsCaptureSession2 not available or call failed\n"); }

                // Try to disable the yellow border if the API is available (Windows 11 or Windows 10 21H1+)
                try
                {
                    auto session3 = s->session.try_as<wgc::IGraphicsCaptureSession3>();
                    if (session3)
                    {
                        session3.IsBorderRequired(false);
                        OutputDebugStringA("[WGC_NATIVE] Disabled yellow border via IGraphicsCaptureSession3\n");
                    }
                }
                catch(...) { OutputDebugStringA("[WGC_NATIVE] IGraphicsCaptureSession3 not available or call failed\n"); }

                if (!s->InitializeInjection(GetCurrentProcessId()))
                {
                    OutputDebugStringA("[WGC_NATIVE] InitializeInjection failed\n");
                    delete s;
                    return nullptr;
                }

                s->session.StartCapture();
                OutputDebugStringA("[WGC_NATIVE] session.StartCapture() called\n");
                return s;
            }
            catch (...) {
                OutputDebugStringA("[WGC_NATIVE] Exception while setting up framePool/session\n");
                delete s; return nullptr;
            }
        }
        catch (...) {
            OutputDebugStringA("[WGC_NATIVE] CreateCaptureSession unexpected exception\n");
            return nullptr;
        }
    }

    __declspec(dllexport) void* CreateCaptureSession(HWND targetHwnd)
    {
        return CreateCaptureSessionInternal(targetHwnd, nullptr);
    }

    __declspec(dllexport) void* CreateDirectCompositionCaptureSession(HWND targetHwnd, HWND presentationHwnd)
    {
        return CreateCaptureSessionInternal(targetHwnd, presentationHwnd);
    }

    __declspec(dllexport) void DestroyCaptureSession(void* ptr) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return;
        s->closing.store(true);
        try {
            s->StopDirectCompositionWorker();
            if (s->session) s->session.Close();
            if (s->item && s->closeToken) s->item.Closed(s->closeToken);
            if (s->framePool) {
                if (s->frameToken) s->framePool.FrameArrived(s->frameToken); // Unregister
                s->framePool.Close();
            }
            if (s->dcompTarget) {
                s->dcompTarget->SetRoot(nullptr);
            }
            if (s->dcompDevice) {
                s->dcompDevice->Commit();
            }
            if (s->injectionHeader) {
                UnmapViewOfFile(s->injectionHeader);
                s->injectionHeader = nullptr;
                s->injectionPixels = nullptr;
            }
            if (s->injectionMapping) {
                CloseHandle(s->injectionMapping);
                s->injectionMapping = nullptr;
            }
        } catch (...) {}
        s->StopInjectionWorker();
        delete s;
        OutputDebugStringA("[WGC_NATIVE] DestroyCaptureSession finished\n");
    }

    __declspec(dllexport) int GetCaptureStatus(void* ptr) {
        if (!ptr) return -1;
        return static_cast<CaptureSession*>(ptr)->frameCount.load();
    }

    __declspec(dllexport) int GetReaderCount(void* ptr) {
        if (!ptr) return -1;
        return static_cast<CaptureSession*>(ptr)->readers.load();
    }

    __declspec(dllexport) int GetDirectCompositionState(void* ptr) {
        if (!ptr) return -1;
        return static_cast<CaptureSession*>(ptr)->dcompState.load();
    }

    __declspec(dllexport) int GetDirectCompositionPresentCount(void* ptr) {
        if (!ptr) return -1;
        return static_cast<CaptureSession*>(ptr)->dcompPresentCount.load();
    }

    __declspec(dllexport) int GetDirectCompositionLastError(void* ptr, char* buffer, int bufferChars) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s || !buffer || bufferChars <= 0) return 0;

        std::lock_guard<std::mutex> lock(s->dcompErrorMutex);
        if (s->dcompLastError.empty())
        {
            buffer[0] = '\0';
            return 0;
        }

        strncpy_s(buffer, bufferChars, s->dcompLastError.c_str(), _TRUNCATE);
        return 1;
    }

    __declspec(dllexport) void SetDirectCompositionRenderOptions(void* ptr, int stretch, float brightness, float saturation, float tintR, float tintG, float tintB, float tintA, int disableVsync) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return;
        s->dcompStretch.store(stretch);
        s->dcompBrightness.store(brightness);
        s->dcompSaturation.store(saturation);
        s->dcompTintR.store(tintR);
        s->dcompTintG.store(tintG);
        s->dcompTintB.store(tintB);
        s->dcompTintA.store(tintA);
        s->dcompDisableVsync.store(disableVsync != 0);
    }

    __declspec(dllexport) void SetDirectCompositionShader(void* ptr, const wchar_t* shaderPath) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return;
        std::lock_guard<std::mutex> lock(s->dcompShaderMutex);
        if (shaderPath) {
            s->dcompShaderPath = shaderPath;
        } else {
            s->dcompShaderPath.clear();
        }
        s->dcompShaderDirty.store(true);

        if (shaderPath && shaderPath[0] != L'\0')
        {
            std::wstring dbg = L"[WGC_NATIVE] SetDirectCompositionShader path='";
            dbg += shaderPath;
            dbg += L"'\n";
            OutputDebugStringW(dbg.c_str());
        }
        else
        {
            OutputDebugStringA("[WGC_NATIVE] SetDirectCompositionShader path='<none>'\n");
        }

        s->dcompWorkerCv.notify_one();
    }

    __declspec(dllexport) int GetDirectCompositionAdapterInfo(void* ptr, wchar_t* rendererBuffer, int rendererBufferChars, wchar_t* vendorBuffer, int vendorBufferChars) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return 0;

        if (rendererBuffer && rendererBufferChars > 0)
        {
            wcsncpy_s(rendererBuffer, rendererBufferChars, s->adapterDescription.c_str(), _TRUNCATE);
        }

        if (vendorBuffer && vendorBufferChars > 0)
        {
            wcsncpy_s(vendorBuffer, vendorBufferChars, s->adapterVendor.c_str(), _TRUNCATE);
        }

        return 1;
    }

    __declspec(dllexport) void SetCaptureMaxResolution(void* ptr, int maxWidth, int maxHeight) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return;
        s->maxWidth = maxWidth;
        s->maxHeight = maxHeight;
        char buf[256]; sprintf_s(buf, "[WGC_NATIVE] SetCaptureMaxResolution: %dx%d\n", maxWidth, maxHeight); OutputDebugStringA(buf);
    }

    __declspec(dllexport) void SetVrrEnabled(void* ptr, int enabled) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return;
        s->vrrEnabled = enabled != 0;
        char buf[128]; sprintf_s(buf, "[WGC_NATIVE] SetVrrEnabled: %d\n", enabled); OutputDebugStringA(buf);
    }

    __declspec(dllexport) void SetBorderRequired(void* ptr, int required) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s || !s->session) return;
        try {
            auto session3 = s->session.try_as<wgc::IGraphicsCaptureSession3>();
            if (session3)
            {
                session3.IsBorderRequired(required != 0);
                char buf[256]; sprintf_s(buf, "[WGC_NATIVE] SetBorderRequired: %d\n", required); OutputDebugStringA(buf);
            }
        } catch(...) { OutputDebugStringA("[WGC_NATIVE] IGraphicsCaptureSession3 not available for SetBorderRequired\n"); }
    }

    __declspec(dllexport) void SetCaptureCropRect(void* ptr, int x, int y, int width, int height) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return;
        // Sanitize inputs and clamp to reasonable limits before storing
        const int SAFE_MAX = 8192;
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (width < 0) width = 0;
        if (height < 0) height = 0;
        if (width > SAFE_MAX) width = SAFE_MAX;
        if (height > SAFE_MAX) height = SAFE_MAX;
        s->cropX.store(x);
        s->cropY.store(y);
        s->cropW.store(width);
        s->cropH.store(height);
        char buf[256]; sprintf_s(buf, "[WGC_NATIVE] SetCaptureCropRect: %d,%d %dx%d\n", x, y, width, height); OutputDebugStringA(buf);
    }

    __declspec(dllexport) void SetInjectionPid(void* ptr, int pid) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return;
        s->injectionPid.store(pid);
        char buf[128]; sprintf_s(buf, "[WGC_NATIVE] SetInjectionPid: %d\n", pid); OutputDebugStringA(buf);
    }

    __declspec(dllexport) bool GetLatestFrame(void* ptr, unsigned char* outBuffer, size_t bufferSize, int* w, int* h) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return false;

        std::lock_guard<std::mutex> lock(s->dataMutex);
        if (s->latestData.empty()) return false;

        *w = s->width.load();
        *h = s->height.load();

        // If C# buffer is too small, return false so C# knows to resize
        if (s->latestData.size() > bufferSize) return false;

        memcpy(outBuffer, s->latestData.data(), s->latestData.size());
        return true;
    }

    // Diagnostic: return width/height and required buffer size without copying data
    __declspec(dllexport) bool PeekLatestFrame(void* ptr, int* outWidth, int* outHeight, size_t* outRequiredSize)
    {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return false;
        std::lock_guard<std::mutex> lock(s->dataMutex);
        if (s->latestData.empty()) return false;
        if (outWidth) *outWidth = s->width.load();
        if (outHeight) *outHeight = s->height.load();
        if (outRequiredSize) *outRequiredSize = s->latestData.size();
        return true;
    }

    __declspec(dllexport) bool AcquireLatestFrame(void* ptr, unsigned char** outBuffer, size_t* outSize, int* w, int* h) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return false;
        std::lock_guard<std::mutex> lock(s->dataMutex);
        if (s->latestData.empty()) return false;
        s->readers.fetch_add(1, std::memory_order_acq_rel);
        if (outBuffer) *outBuffer = s->latestData.data();
        if (outSize) *outSize = s->latestData.size();
        if (w) *w = s->width.load();
        if (h) *h = s->height.load();
        return true;
    }

    __declspec(dllexport) void ReleaseLatestFrame(void* ptr) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return;
        s->readers.fetch_sub(1, std::memory_order_acq_rel);
    }

    // Return raw ID3D11Device* pointer for interop.
    // Managed code can use this pointer to perform native interop with
    // the D3D11 device created by the capture session. The pointer is
    // returned when available and may be null if the session or device
    // is not initialized.
    __declspec(dllexport) void* GetD3D11Device(void* ptr) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s || !s->d3dDevice) return nullptr;
        return s->d3dDevice.get();
    }

    // Return raw ID3D11Texture2D* pointer of the latest GPU texture for interop.
    // The caller receives a pointer to the most recently produced GPU
    // texture. The pointer may be null if no texture is available. The
    // lifetime of the texture is managed by the native session; callers
    // should not assume the texture remains valid indefinitely.
    __declspec(dllexport) void* GetLatestD3DTexture(void* ptr) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s || !s->latestGpuTexture) return nullptr;
        return s->latestGpuTexture.get();
    }

    __declspec(dllexport) void* GetSharedHandle(void* ptr) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s || !s->latestGpuTexture) return nullptr;
        rt::com_ptr<IDXGIResource> dxgiResource;
        if (FAILED(s->latestGpuTexture->QueryInterface(rt::guid_of<IDXGIResource>(), dxgiResource.put_void()))) return nullptr;
        HANDLE sharedHandle = nullptr;
        if (FAILED(dxgiResource->GetSharedHandle(&sharedHandle))) return nullptr;
        return sharedHandle;
    }

    __declspec(dllexport) void SetInteropEnabled(void* ptr, int enabled) {
        auto s = static_cast<CaptureSession*>(ptr);
        if (!s) return;
        s->interopEnabled.store(enabled != 0);
        char buf[128]; sprintf_s(buf, "[WGC_NATIVE] SetInteropEnabled: %d\n", enabled); OutputDebugStringA(buf);
    }
}
