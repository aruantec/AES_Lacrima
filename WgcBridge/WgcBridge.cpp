#include "pch.h"
#include <d3dcompiler.h>
#include <atomic>
#include <algorithm>
#include <dxgi1_2.h>
#include <thread>
#include <chrono>
#include <cstdio>
#include <string>

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
        float padding0;
        float padding1;
        float tint[4];
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
    std::wstring adapterDescription;
    std::wstring adapterVendor;
    std::string dcompLastError;
    std::mutex dcompErrorMutex;

    void MarkDirectCompositionFailed(char const* message)
    {
        dcompState.store(-1);
        if (message)
        {
            {
                std::lock_guard<std::mutex> lock(dcompErrorMutex);
                dcompLastError = message;
            }
            char buf[256];
            sprintf_s(buf, "[WGC_NATIVE] DirectComposition failure: %s\n", message);
            OutputDebugStringA(buf);
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

        OutputDebugStringA("[WGC_NATIVE] DirectComposition initialized\n");
        return true;
    }

    bool EnsureDirectCompositionRenderer()
    {
        if (dcompVertexShader && dcompPixelShader && dcompInputLayout && dcompVertexBuffer && dcompConstantBuffer && dcompSamplerState && dcompRasterizerState)
            return true;

        const char* vsSrc =
            "struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD; };"
            "struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD; };"
            "VSOut main(VSIn input) { VSOut o; o.pos = float4(input.pos, 1.0); o.uv = input.uv; return o; }";

        const char* psSrc =
            "cbuffer Params : register(b0) { float brightness; float saturation; float4 tint; };"
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

        HRESULT hr = D3DCompile(vsSrc, (SIZE_T)strlen(vsSrc), nullptr, nullptr, nullptr, "main", "vs_4_0", 0, 0, vsBlob.put(), errBlob.put());
        if (FAILED(hr) || !vsBlob)
        {
            MarkDirectCompositionFailed("vertex shader compile failed");
            return false;
        }

        hr = D3DCompile(psSrc, (SIZE_T)strlen(psSrc), nullptr, nullptr, nullptr, "main", "ps_4_0", 0, 0, psBlob.put(), errBlob.put());
        if (FAILED(hr) || !psBlob)
        {
            MarkDirectCompositionFailed("pixel shader compile failed");
            return false;
        }

        hr = d3dDevice->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, dcompVertexShader.put());
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("CreateVertexShader failed");
            return false;
        }

        hr = d3dDevice->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, dcompPixelShader.put());
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("CreatePixelShader failed");
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

        D3D11_BUFFER_DESC vbDesc = {};
        vbDesc.Usage = D3D11_USAGE_DYNAMIC;
        vbDesc.ByteWidth = sizeof(DcompVertex) * 4;
        vbDesc.BindFlags = D3D11_BIND_VERTEX_BUFFER;
        vbDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        hr = d3dDevice->CreateBuffer(&vbDesc, nullptr, dcompVertexBuffer.put());
        if (FAILED(hr))
        {
            MarkDirectCompositionFailed("CreateBuffer vertex failed");
            return false;
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

        rt::com_ptr<ID3D11Texture2D> backBuffer;
        HRESULT hr = dcompSwapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), backBuffer.put_void());
        if (FAILED(hr) || !backBuffer)
        {
            MarkDirectCompositionFailed("GetBuffer failed");
            return;
        }

        rt::com_ptr<ID3D11RenderTargetView> rtv;
        hr = d3dDevice->CreateRenderTargetView(backBuffer.get(), nullptr, rtv.put());
        if (FAILED(hr) || !rtv)
        {
            MarkDirectCompositionFailed("CreateRenderTargetView failed");
            return;
        }

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
        constants.tint[0] = dcompTintR.load();
        constants.tint[1] = dcompTintG.load();
        constants.tint[2] = dcompTintB.load();
        constants.tint[3] = dcompTintA.load();
        d3dContext->UpdateSubresource(dcompConstantBuffer.get(), 0, nullptr, &constants, 0, 0);

        D3D11_VIEWPORT viewport{};
        viewport.Width = static_cast<float>(dcompWidth);
        viewport.Height = static_cast<float>(dcompHeight);
        viewport.MinDepth = 0.0f;
        viewport.MaxDepth = 1.0f;

        float clearColor[4] = { 0, 0, 0, 1 };
        ID3D11RenderTargetView* rtvPtr = rtv.get();
        d3dContext->OMSetRenderTargets(1, &rtvPtr, nullptr);
        d3dContext->ClearRenderTargetView(rtv.get(), clearColor);
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
                PresentToDirectComposition(currentGpu.get(), currentW, currentH);
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
};

extern "C" {
    static void* CreateCaptureSessionInternal(HWND targetHwnd, HWND presentationHwnd)
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
                sprintf_s(buf, "[WGC_NATIVE] D3D11CreateDevice failed: 0x%08X\n", (unsigned)hr);
                OutputDebugStringA(buf);
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
                    sprintf_s(buf, "[WGC_NATIVE] CreateForWindow failed: 0x%08X\n", (unsigned)createHr);
                    OutputDebugStringA(buf);
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
        } catch (...) {}
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
