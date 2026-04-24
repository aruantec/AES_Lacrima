using AES_Controls.EmuGrabbing.ShaderHandling;
using AES_Emulation.Windows.API;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.Windows;

[SupportedOSPlatform("windows")]
public class WgcCaptureControl : OpenGlControlBase
{
    #region Private Fields
    private const int GL_STATIC_DRAW = 0x88E4;
    private const int GL_DYNAMIC_DRAW = 0x88E8;
    private const int GL_STREAM_DRAW = 0x88E0;
    private const int GL_TEXTURE1 = 0x84C1;
    private const int GL_TEXTURE_WRAP_S = 0x2802;
    private const int GL_TEXTURE_WRAP_T = 0x2803;
    private const int GL_CLAMP_TO_EDGE = 0x812F;
    private const int GL_PIXEL_UNPACK_BUFFER = 0x88EC;
    private const uint GL_MAP_WRITE_BIT = 0x0002u;
    private const uint GL_MAP_INVALIDATE_BUFFER_BIT = 0x0004u;
    private const int GL_UNPACK_ALIGNMENT = 0x0CF5;
    private const uint GL_DYNAMIC_STORAGE_BIT = 0x0100u;
    private const uint GL_MAP_PERSISTENT_BIT = 0x0040u;
    private const uint GL_MAP_COHERENT_BIT = 0x0080u;
    private const int MAX_TEXTURE_DIMENSION = 4096;

    // Texture Swizzle Constants (GL 3.3+ / GLES 3.0+)
    private const int GL_TEXTURE_SWIZZLE_R = 0x8E42;
    private const int GL_TEXTURE_SWIZZLE_G = 0x8E43;
    private const int GL_TEXTURE_SWIZZLE_B = 0x8E44;
    private const int GL_TEXTURE_SWIZZLE_A = 0x8E45;
    private const int GL_RED = 0x1903;
    private const int GL_GREEN = 0x1904;
    private const int GL_BLUE = 0x1905;
    private const int GL_ALPHA = 0x1906;

    private static bool _nativeAcquireSupported = true;

    private readonly MouseTunnelHelper _mouseTunnel;
    private readonly object _sessionLock = new();
    private nint _session = nint.Zero;
    private byte[] _pixelBuffer = new byte[2048 * 2048 * 4];
    private GCHandle _bufferHandle;
    private int _textureId, _program, _vbo;
    private int _bgVbo = 0;
    private int _letterboxTexId = 0;
    private bool _isEs;
    private bool _letterboxNeedsUpdate = false;
    private byte[]? _letterboxPixels = null;
    private int _letterboxWidth = 0;
    private int _letterboxHeight = 0;
    private int _letterboxStride = 0;
    private nint _hostHandle = nint.Zero;

    private IntPtr _glMapBufferRangePtr = IntPtr.Zero;
    private IntPtr _glUnmapBufferPtr = IntPtr.Zero;
    private IntPtr _glTexSubImage2DPtr = IntPtr.Zero;
    private IntPtr _glBufferStoragePtr = IntPtr.Zero;
    private IntPtr _glPixelStoreiPtr = IntPtr.Zero;
    private IntPtr _glTexParameterivPtr = IntPtr.Zero;
    private IntPtr _glUniform1IPtr = IntPtr.Zero;
    private IntPtr _glUniform1FPtr = IntPtr.Zero;
    private IntPtr _glUniform4fvPtr = IntPtr.Zero;
    private Dictionary<string, int>? _uniformLocationCache;
    private int _uTexLoc = -1, _uFallbackColorLoc = -1;
    private int _uBrightnessLoc = -1, _uSaturationLoc = -1, _uColorTintLoc = -1;
    private float[] _quadBuffer = new float[16];
    private IntPtr[] _pboMappedPtr = new IntPtr[2] { IntPtr.Zero, IntPtr.Zero };
    private bool _pboPersistentSupported = false;
    private IntPtr _glFlushMappedBufferRangePtr = IntPtr.Zero;
    private long[] _pboCapacity = new long[2] { 0, 0 };

    private int _texWidth = 0;
    private int _texHeight = 0;
    private int[] _pbo = new int[2] { 0, 0 };
    private int _pboIndex = 0;
    private bool _pboInitialized = false;
    private DispatcherTimer? _heartbeatTimer;
    private DispatcherTimer? _uiHeartbeat;
    private bool _dxInteropAvailable = false;
    private bool _usingDxInterop = false;
    private bool _injectionActive = false;
    private MemoryMappedFile? _injectionMemoryMappedFile;
    private MemoryMappedViewAccessor? _injectionAccessor;
    private long _lastInjectionFrameCount = -1;
    private int _injectionEmptyReadCount = 0;
    private int _injectionHeaderReadFailedCount = 0;
    private long _lastWgcRestartTicks = 0;
    private long _lastInjectionSuccessTicks = 0;
    private bool _injectionLockIn = false;
    private int _injectionStableCount = 0;
    private bool _injectionPermanentlyFailed = false;
    private long _lastUiUpdateTicks = 0;
    private double _smoothedFps = 0;
    private double _smoothedFrameTimeMs = 0;
    private const int InjectionHeaderSize = 128;
    private const int InjectionMaxFrameBytes = 128 * 1024 * 1024;
    private const uint InjectionMagic = 0x41534145;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct InjectionFrameHeader
    {
        public uint Magic;
        public uint Sequence1;
        public uint Sequence2;
        public uint Width;
        public uint Height;
        public uint Stride;
        public uint FrameCounter;
        public uint Reserved0;
        public uint Reserved1;
        public uint Reserved2;
        public uint Reserved3;
        public uint Reserved4;
        public uint Reserved5;
        public uint Reserved6;
        public uint Reserved7;
        public uint Reserved8;
        public uint Reserved9;
    }

    private WindowHandler? _windowHandler;
    private TopLevel? _hostTopLevel;

    // GPU interop state
    private IntPtr _wglDeviceHandle = IntPtr.Zero;
    private IntPtr _wglObjectHandle = IntPtr.Zero;
    private IntPtr _dxDevicePtr = IntPtr.Zero;
    private IntPtr _dxTexturePtr = IntPtr.Zero;
    private int _lastCropX, _lastCropY, _lastCropW, _lastCropH;


    // Delegates for WGL_NV_DX_interop (resolved from context)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr wglDXOpenDeviceNVDel(IntPtr dxDevice);
    private wglDXOpenDeviceNVDel? _wglDXOpenDeviceNV;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool wglDXCloseDeviceNVDel(IntPtr hDevice);
    private wglDXCloseDeviceNVDel? _wglDXCloseDeviceNV;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr wglDXRegisterObjectNVDel(IntPtr hDevice, IntPtr dxResource, uint name, uint type, uint access);
    private wglDXRegisterObjectNVDel? _wglDXRegisterObjectNV;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool wglDXUnregisterObjectNVDel(IntPtr hDevice, IntPtr hObject);
    private wglDXUnregisterObjectNVDel? _wglDXUnregisterObjectNV;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool wglDXLockObjectsNVDel(IntPtr hDevice, int count, IntPtr[] handles);
    private wglDXLockObjectsNVDel? _wglDXLockObjectsNV;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool wglDXUnlockObjectsNVDel(IntPtr hDevice, int count, IntPtr[] handles);
    private wglDXUnlockObjectsNVDel? _wglDXUnlockObjectsNV;

    private const uint WGL_ACCESS_READ_ONLY_NV = 0x00000000u;
    private const uint WGL_ACCESS_READ_WRITE_NV = 0x00000001u;

    // EGL/ANGLE interop state and delegates
    private IntPtr _eglDisplay = IntPtr.Zero;
    private IntPtr _eglContext = IntPtr.Zero;
    private IntPtr _eglImage = IntPtr.Zero;
    private IntPtr _currentSharedHandle = IntPtr.Zero;
    private bool _usingAngleInterop = false;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr eglGetCurrentDisplayDel();
    private eglGetCurrentDisplayDel? _eglGetCurrentDisplay;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr eglGetCurrentContextDel();
    private eglGetCurrentContextDel? _eglGetCurrentContext;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr eglCreateImageKHRDel(IntPtr dpy, IntPtr ctx, uint target, IntPtr buffer, IntPtr attrib_list);
    private eglCreateImageKHRDel? _eglCreateImageKHR;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool eglDestroyImageKHRDel(IntPtr dpy, IntPtr image);
    private eglDestroyImageKHRDel? _eglDestroyImageKHR;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void glEGLImageTargetTexture2DOESDel(uint target, IntPtr image);
    private glEGLImageTargetTexture2DOESDel? _glEGLImageTargetTexture2DOES;

    // EGL target for ANGLE D3D share handle
    private const uint EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE = 0x33A0;

    private static readonly float[] _colorTransparent = { 0, 0, 0, 0 };
    private float[] _tempColor = new float[4];
    private SlangShaderPipeline? _shaderPipeline;
    private string? _currentShaderPath;

    // frame tracking
    private long _lastNativeFrameCount = -1;
    private long _lastFrameTicks = 0;
    private readonly double[] _frameTimeHistory = new double[120];
    private int _frameTimeHistoryPtr = 0;
    #endregion

    #region Styled Properties
    public static readonly StyledProperty<IntPtr> TargetHwndProperty =
        AvaloniaProperty.Register<WgcCaptureControl, IntPtr>(nameof(TargetHwnd));

    public static readonly StyledProperty<int> TargetProcessIdProperty =
        AvaloniaProperty.Register<WgcCaptureControl, int>(nameof(TargetProcessId), 0);

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<WgcCaptureControl, Stretch>(nameof(Stretch), Stretch.Uniform);

    public static readonly StyledProperty<string?> RetroarchShaderFileProperty =
        AvaloniaProperty.Register<WgcCaptureControl, string?>(nameof(RetroarchShaderFile), null);

    public static readonly StyledProperty<int> MaxCaptureHeightProperty =
        AvaloniaProperty.Register<WgcCaptureControl, int>(nameof(MaxCaptureHeight), 1080);

    public static readonly StyledProperty<bool> DisableDownscaleProperty =
        AvaloniaProperty.Register<WgcCaptureControl, bool>(nameof(DisableDownscale), false);

    public static readonly StyledProperty<int> HeartbeatIntervalMsProperty =
        AvaloniaProperty.Register<WgcCaptureControl, int>(nameof(HeartbeatIntervalMs), 250);

    public static readonly StyledProperty<Color> LetterBoxColorProperty =
        AvaloniaProperty.Register<WgcCaptureControl, Color>(nameof(LetterBoxColor), Colors.Black);

    public static readonly StyledProperty<Bitmap?> LetterBoxBitmapProperty =
        AvaloniaProperty.Register<WgcCaptureControl, Bitmap?>(nameof(LetterBoxBitmap), null);

    public static readonly StyledProperty<double> BrightnessProperty =
        AvaloniaProperty.Register<WgcCaptureControl, double>(nameof(Brightness), 1.0);

    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<WgcCaptureControl, double>(nameof(Saturation), 1.0);

    public static readonly StyledProperty<Color> ColorTintProperty =
        AvaloniaProperty.Register<WgcCaptureControl, Color>(nameof(ColorTint), Colors.White);

    public static readonly StyledProperty<int> FrameNumberProperty =
        AvaloniaProperty.Register<WgcCaptureControl, int>(nameof(FrameNumber), 0);

    public static readonly StyledProperty<double> FpsProperty =
        AvaloniaProperty.Register<WgcCaptureControl, double>(nameof(Fps), 0.0);

    public static readonly StyledProperty<double> FrameTimeMsProperty =
        AvaloniaProperty.Register<WgcCaptureControl, double>(nameof(FrameTimeMs), 0.0);

    public static readonly StyledProperty<int> LastFrameWidthProperty =
        AvaloniaProperty.Register<WgcCaptureControl, int>(nameof(LastFrameWidth), 0);

    public static readonly StyledProperty<int> LastFrameHeightProperty =
        AvaloniaProperty.Register<WgcCaptureControl, int>(nameof(LastFrameHeight), 0);

    public static readonly StyledProperty<double> FpsSmoothingFactorProperty =
        AvaloniaProperty.Register<WgcCaptureControl, double>(nameof(FpsSmoothingFactor), 0.85);

    public double FpsSmoothingFactor
    {
        get => GetValue(FpsSmoothingFactorProperty);
        set => SetValue(FpsSmoothingFactorProperty, value);
    }

    // Allow disabling VSync (enabled by default)
    public static readonly StyledProperty<bool> DisableVSyncProperty =
        AvaloniaProperty.Register<WgcCaptureControl, bool>(nameof(DisableVSync), true);

    public bool DisableVSync
    {
        get => GetValue(DisableVSyncProperty);
        set => SetValue(DisableVSyncProperty, value);
    }

    public static readonly StyledProperty<bool> ForceUseTargetClientSizeProperty =
        AvaloniaProperty.Register<WgcCaptureControl, bool>(nameof(ForceUseTargetClientSize), false);

    public static readonly StyledProperty<bool> RequestStopSessionProperty =
        AvaloniaProperty.Register<WgcCaptureControl, bool>(nameof(RequestStopSession), false);

    public static readonly StyledProperty<bool> ShowStatisticsOverlayProperty =
        AvaloniaProperty.Register<WgcCaptureControl, bool>(nameof(ShowStatisticsOverlay), false);

    public static readonly StyledProperty<bool> ShowFrametimeGraphProperty =
        AvaloniaProperty.Register<WgcCaptureControl, bool>(nameof(ShowFrametimeGraph), false);

    public static readonly StyledProperty<bool> ShowDetailedGpuInfoProperty =
        AvaloniaProperty.Register<WgcCaptureControl, bool>(nameof(ShowDetailedGpuInfo), false);

    public static readonly StyledProperty<double> OverlayOpacityProperty =
        AvaloniaProperty.Register<WgcCaptureControl, double>(nameof(OverlayOpacity), 0.55);

    public static readonly StyledProperty<string> BackendNameProperty =
        AvaloniaProperty.Register<WgcCaptureControl, string>(nameof(BackendName), "OpenGL");

    public static readonly StyledProperty<string> GpuRendererProperty =
        AvaloniaProperty.Register<WgcCaptureControl, string>(nameof(GpuRenderer), "Unknown");

    public static readonly StyledProperty<string> GpuVendorProperty =
        AvaloniaProperty.Register<WgcCaptureControl, string>(nameof(GpuVendor), "Unknown");

    public static readonly StyledProperty<Geometry?> FrametimeGraphGeometryProperty =
        AvaloniaProperty.Register<WgcCaptureControl, Geometry?>(nameof(FrametimeGraphGeometry), null);

    public static readonly StyledProperty<double> FrametimeGraphWidthProperty =
        AvaloniaProperty.Register<WgcCaptureControl, double>(nameof(FrametimeGraphWidth), 180);
    #endregion



    #region Constructors
    static WgcCaptureControl()
    {
        TargetHwndProperty.Changed.AddClassHandler<WgcCaptureControl>((x, e) => x.OnTargetHwndChanged(e));
        TargetProcessIdProperty.Changed.AddClassHandler<WgcCaptureControl>((x, e) => x.OnTargetProcessIdChanged(e));
        LetterBoxBitmapProperty.Changed.AddClassHandler<WgcCaptureControl>((x, e) => x.OnLetterboxBitmapChanged((Bitmap?)e.NewValue));
        LetterBoxColorProperty.Changed.AddClassHandler<WgcCaptureControl>((x, e) => x.OnLetterboxColorChanged(e));
        StretchProperty.Changed.AddClassHandler<WgcCaptureControl>((x, e) => { if (e.NewValue is Stretch s) x.OnStretchChanged(s); });
        RetroarchShaderFileProperty.Changed.AddClassHandler<WgcCaptureControl>((x, e) => x.OnRetroarchShaderFileChanged(e));
        ForceUseTargetClientSizeProperty.Changed.AddClassHandler<WgcCaptureControl>((x, e) => x.OnForceUseTargetClientSizeChanged(e));
        RequestStopSessionProperty.Changed.AddClassHandler<WgcCaptureControl>((x, e) => x.OnRequestStopSessionChanged(e));
    }


    public WgcCaptureControl()
    {
        _mouseTunnel = new MouseTunnelHelper(this);
        _mouseTunnel.TunnelMouse = true;
        IsHitTestVisible = true;
        Focusable = true;
    }
    #endregion

    #region Public Methods
    public void StartCapture(bool forceWgc = false)
    {
        lock (_sessionLock)
        {
            try
            {
                if (_session != nint.Zero) { WgcBridgeApi.DestroyCaptureSession(_session); _session = nint.Zero; }
                ResetCaptureFrameState();
                if (TargetHwnd == IntPtr.Zero) return;

                // Only postpone WGC if injection is currently being attempted AND hasn't clearly failed yet.
                // If forceWgc is true, we skip this check (used for fallbacks).
                bool injectionAttempting = _injectionActive && _injectionEmptyReadCount < 120 && !_injectionPermanentlyFailed;
                if (!forceWgc && (injectionAttempting || (TargetProcessId != 0 && !_injectionActive && _injectionEmptyReadCount < 120 && !_injectionPermanentlyFailed)))
                {
                    if (_injectionEmptyReadCount % 60 == 0)
                        Debug.WriteLine("[WGC] Postponing WGC session start; prioritizing injected hook.");
                    return;
                }

                _lastNativeFrameCount = -1;
                _dxTexturePtr = IntPtr.Zero; // Reset interop pointer

                if (!_bufferHandle.IsAllocated)
                    _bufferHandle = GCHandle.Alloc(_pixelBuffer, GCHandleType.Pinned);

                _session = WgcBridgeApi.CreateCaptureSession(TargetHwnd);
                if (_session == nint.Zero)
                {
                    Debug.WriteLine($"[WGC] Failed to create capture session for HWND: {TargetHwnd}. Check if target is running with higher privileges.");
                    return;
                }
                
                WgcBridgeApi.SetCaptureMaxResolution(_session, 4096, MaxCaptureHeight);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WGC] CRITICAL ERROR in StartCapture: {ex.Message}");
                _session = nint.Zero;
            }
        }
    }

    private void ResetCaptureFrameState()
    {
        _lastNativeFrameCount = -1;
        _texWidth = 0;
        _texHeight = 0;
        _dxTexturePtr = IntPtr.Zero;
        _lastFrameTicks = 0;
        _lastUiUpdateTicks = 0;

        for (int i = 0; i < _frameTimeHistory.Length; i++)
            _frameTimeHistory[i] = 0.0;
        _frameTimeHistoryPtr = 0;
        _smoothedFps = 0.0;
        _smoothedFrameTimeMs = 0.0;

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                FrameNumber = 0;
                LastFrameWidth = 0;
                LastFrameHeight = 0;
                Fps = 0.0;
                FrameTimeMs = 0.0;
                FrametimeGraphGeometry = null;
            }, DispatcherPriority.Render);
        }
        catch
        {
            // Best effort UI reset.
        }
    }

    public void ForwardFocusToTarget()
    {
        if (TargetHwnd == IntPtr.Zero || _hostHandle == IntPtr.Zero)
            return;

        try
        {
            Win32API.ForceEmulatorFocus(TargetHwnd, _hostHandle, 200);
        }
        catch
        {
            // Best effort focus transfer for prototype mode.
        }
    }
    #endregion

    #region Private/Protected Methods
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TryResolveHostHandle();

        if (TargetHwnd != IntPtr.Zero)
            TryAttachTargetWindow();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachHostTopLevel();
        _hostHandle = IntPtr.Zero;
        CleanupInjectionSession();
        base.OnDetachedFromVisualTree(e);
    }

    protected override unsafe void OnOpenGlInit(GlInterface gl)
    {
        var shaderInfo = GlHelper.GetShaderVersion(gl);
        string shaderVersion = shaderInfo.Item1;
        _isEs = shaderInfo.Item2;

        BackendName = _isEs ? "OpenGL ES" : "OpenGL";
        try
        {
            GpuRenderer = gl.GetString(0x1F01) ?? "Unknown"; // GL_RENDERER
            GpuVendor = gl.GetString(0x1F00) ?? "Unknown";   // GL_VENDOR
        }
        catch
        {
            GpuRenderer = "Unknown";
            GpuVendor = "Unknown";
        }

        _textureId = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureId);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);

        if (_isEs)
        {
            // On OpenGL ES, we typically use GL_RGBA (0x1908) for upload even if source is BGRA.
            // We use hardware swizzle to fix the color channels if supported.
            gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GL_TEXTURE_SWIZZLE_R, GL_BLUE);
            gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GL_TEXTURE_SWIZZLE_G, GL_GREEN);
            gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GL_TEXTURE_SWIZZLE_B, GL_RED);
            gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GL_TEXTURE_SWIZZLE_A, GL_ALPHA);
        }
        else
        {
            // On Desktop GL, we use GL_BGRA (0x80E1) in glTexSubImage2D which handles the swap.
            // Ensure swizzle is reset to identity.
            gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GL_TEXTURE_SWIZZLE_R, GL_RED);
            gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GL_TEXTURE_SWIZZLE_G, GL_GREEN);
            gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GL_TEXTURE_SWIZZLE_B, GL_BLUE);
            gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GL_TEXTURE_SWIZZLE_A, GL_ALPHA);
        }


        var pixelStoreProc = gl.GetProcAddress("glPixelStorei");
        if (pixelStoreProc != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<int, int, void>)pixelStoreProc)(GL_UNPACK_ALIGNMENT, 1);

        _glMapBufferRangePtr = gl.GetProcAddress("glMapBufferRange");
        _glUnmapBufferPtr = gl.GetProcAddress("glUnmapBuffer");
        _glTexSubImage2DPtr = gl.GetProcAddress("glTexSubImage2D");
        _glBufferStoragePtr = gl.GetProcAddress("glBufferStorage");
        _glPixelStoreiPtr = pixelStoreProc;
        _glTexParameterivPtr = gl.GetProcAddress("glTexParameteriv");
        _glUniform1IPtr = gl.GetProcAddress("glUniform1i");
        _glUniform1FPtr = gl.GetProcAddress("glUniform1f");
        _glUniform4fvPtr = gl.GetProcAddress("glUniform4fv");
        _uniformLocationCache = new Dictionary<string, int>(8);

        string vSrc = $@"{shaderVersion}
            layout(location = 0) in vec2 aPos;
            layout(location = 1) in vec2 aTex;
            out vec2 vTex;
            void main() {{
                vTex = aTex;
                gl_Position = vec4(aPos, 0.0, 1.0);
            }}";

        string fSrc = $@"{shaderVersion}
            {(_isEs ? "precision mediump float;" : "")}
            uniform sampler2D uTex;
            uniform vec4 uFallbackColor;
            uniform float uBrightness;
            uniform float uSaturation;
            uniform vec4 uColorTint;
            in vec2 vTex;
            out vec4 fragColor;
            void main() {{
                vec4 col = texture(uTex, vTex);
                if(uFallbackColor.a > 0.5) fragColor = uFallbackColor;
                else {{
                    // Apply Brightness
                    col.rgb *= uBrightness;
                    
                    // Apply Saturation (Luminance preserving)
                    float gray = dot(col.rgb, vec3(0.299, 0.587, 0.114));
                    col.rgb = mix(vec3(gray), col.rgb, uSaturation);
                    
                    // Apply Tint
                    col *= uColorTint;
                    
                    fragColor = col;
                }}
            }}";

        _program = CreateProgram(gl, vSrc, fSrc);

        // Cache uniform locations to avoid dictionary lookups in render loop
        fixed (byte* pName = "uTex\0"u8) _uTexLoc = gl.GetUniformLocation(_program, (IntPtr)pName);
        fixed (byte* pName = "uFallbackColor\0"u8) _uFallbackColorLoc = gl.GetUniformLocation(_program, (IntPtr)pName);
        fixed (byte* pName = "uBrightness\0"u8) _uBrightnessLoc = gl.GetUniformLocation(_program, (IntPtr)pName);
        fixed (byte* pName = "uSaturation\0"u8) _uSaturationLoc = gl.GetUniformLocation(_program, (IntPtr)pName);
        fixed (byte* pName = "uColorTint\0"u8) _uColorTintLoc = gl.GetUniformLocation(_program, (IntPtr)pName);

        float[] vertices = { -1f, 1f, 0f, 0f, -1f, -1f, 0f, 1f, 1f, 1f, 1f, 0f, 1f, -1f, 1f, 1f };
        _vbo = gl.GenBuffer();
        UploadFloatBuffer(gl, _vbo, vertices, GL_STATIC_DRAW);

        _bgVbo = gl.GenBuffer();
        UploadFloatBuffer(gl, _bgVbo, vertices, GL_STATIC_DRAW);

        _letterboxTexId = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _letterboxTexId);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);
        try { gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE); gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE); } catch { }

        _pbo[0] = gl.GenBuffer();
        _pbo[1] = gl.GenBuffer();
        _pboIndex = 0;
        _pboInitialized = false;

        // NOTE: Disabled PBO persistent mapping - it was causing memory leaks due to unmapped buffers
        // Falling back to non-persistent mapping which is simpler and safer
        _pboPersistentSupported = false;

        _heartbeatTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(HeartbeatIntervalMs), DispatcherPriority.Background, (_, _) => RequestNextFrameRendering());
        _heartbeatTimer.Start();

        // 250ms safety heartbeat is enough when continuous redraw is active.
        _uiHeartbeat = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            if (IsVisible && (_session != nint.Zero || _injectionActive))
                Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
        });
        _uiHeartbeat.Start();

        // Try to initialize DX interop now that GL context is present
        TryInitDxInterop(gl);
        // Try ANGLE/EGL import path
        TryInitAngleInterop(gl);

        // Attempt to disable VSync so control can render unlocked when possible if requested
        if (DisableVSync) TryDisableVSync(gl);
    }

    // Delegates to disable VSync
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int eglSwapIntervalDel(IntPtr dpy, int interval);
    private eglSwapIntervalDel? _eglSwapInterval;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool wglSwapIntervalEXTDel(int interval);
    private wglSwapIntervalEXTDel? _wglSwapIntervalEXT;

    private bool TryReadInjectedFrame(out InjectionFrameHeader frameHeader, out IntPtr pixelData)
    {
        frameHeader = default;
        pixelData = IntPtr.Zero;

        if (!_injectionActive || _injectionAccessor == null)
            return false;

        unsafe
        {
            byte* basePtr = null;
            try
            {
                _injectionAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                if (basePtr == null)
                    return false;

                var header = (InjectionFrameHeader*)basePtr;
                
                // Consistency check: Sequence1 must match Sequence2 for a complete frame.
                // If they don't match, or seq1 is 0, a write is in progress.
                // We don't sleep here to keep the UI thread responsive.
                uint seq1 = header->Sequence1;
                if (seq1 == 0 || seq1 != header->Sequence2)
                    return false;

                InjectionFrameHeader localHeader = *header;

                    if (localHeader.Magic != InjectionMagic)
                    {
                        // Bad magic usually means memory isn't initialized yet or was corrupted
                        return false;
                    }

                    if (localHeader.Width == 0 || localHeader.Height == 0)
                    {
                        return false;
                    }

                    if ((ulong)localHeader.Height * localHeader.Stride > InjectionMaxFrameBytes)
                    {
                        Debug.WriteLine($"[WGC] Injected frame size too large {localHeader.Width}x{localHeader.Height}");
                        return false;
                    }

                    frameHeader = localHeader;
                    pixelData = (IntPtr)(basePtr + InjectionHeaderSize);
                    
                    // Periodic diagnostic log
                    if (frameHeader.FrameCounter % 1000 == 0)
                        Debug.WriteLine($"[WGC] Injected frame active: #{frameHeader.FrameCounter} ({frameHeader.Width}x{frameHeader.Height})");
                        
                    return true;
                }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WGC] Injected frame read exception: {ex.Message}");
                return false;
            }
            finally
            {
                if (basePtr != null)
                    _injectionAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    private void CleanupInjectionSession()
    {
        _injectionActive = false;
        _lastInjectionFrameCount = -1;
        _injectionEmptyReadCount = 0;
        try
        {
            _injectionAccessor?.Dispose();
            _injectionAccessor = null;
        }
        catch { }

        try
        {
            _injectionMemoryMappedFile?.Dispose();
            _injectionMemoryMappedFile = null;
        }
        catch { }
    }

    private void TryDisableVSync(GlInterface gl)
    {
        try
        {
            // Try WGL extension first (desktop GL)
            IntPtr pWglSwap = gl.GetProcAddress("wglSwapIntervalEXT");
            if (pWglSwap != IntPtr.Zero)
            {
                _wglSwapIntervalEXT = Marshal.GetDelegateForFunctionPointer<wglSwapIntervalEXTDel>(pWglSwap);
                try { _wglSwapIntervalEXT?.Invoke(0); }
                catch (Exception ex) { Debug.WriteLine($"[WGC] wglSwapIntervalEXT failed: {ex.Message}"); }
            }

            // Try EGL swap interval (ANGLE / EGL)
            IntPtr pEglSwap = gl.GetProcAddress("eglSwapInterval");
            if (pEglSwap != IntPtr.Zero)
            {
                _eglSwapInterval = Marshal.GetDelegateForFunctionPointer<eglSwapIntervalDel>(pEglSwap);
                try
                {
                    // Use cached display if available
                    IntPtr dpy = _eglGetCurrentDisplay?.Invoke() ?? IntPtr.Zero;
                    if (_eglSwapInterval?.Invoke(dpy, 0) == 0)
                        Debug.WriteLine("[WGC] EGL VSync disabled successfully.");
                }
                catch (Exception ex) { Debug.WriteLine($"[WGC] eglSwapInterval failed: {ex.Message}"); }
            }
            else if (pWglSwap != IntPtr.Zero)
            {
                Debug.WriteLine("[WGC] WGL VSync disabled successfully.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WGC] TryDisableVSync exception: {ex.Message}");
        }
    }

    private bool _vsyncDisabledOnce = false;
    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        var renderStart = Stopwatch.GetTimestamp();
        var renderNowTicks = renderStart;

        if (!IsVisible) return;

        if (ForceUseTargetClientSize && _session != nint.Zero && TargetHwnd != IntPtr.Zero)
        {
            if (Win32API.GetClientAreaOffsets(TargetHwnd, out int cx, out int cy, out int cw, out int ch))
            {
                if (cx != _lastCropX || cy != _lastCropY || cw != _lastCropW || ch != _lastCropH)
                {
                    WgcBridgeApi.SetCaptureCropRect(_session, cx, cy, cw, ch);
                    _lastCropX = cx; _lastCropY = cy; _lastCropW = cw; _lastCropH = ch;
                }
            }
        }

        if (DisableVSync && !_vsyncDisabledOnce)

        {
            TryDisableVSync(gl);
            _vsyncDisabledOnce = true;
        }

        if (RetroarchShaderFile != _currentShaderPath) LoadShaderPreset(gl, RetroarchShaderFile);

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int viewW = (int)(Bounds.Width * scaling);
        int viewH = (int)(Bounds.Height * scaling);

        // COMPOSITION FPS TRACKING: Calculate FPS based on composition rate, not capture arrival
        renderNowTicks = Stopwatch.GetTimestamp();
        if (_lastFrameTicks != 0)
        {
            double dt = (double)(renderNowTicks - _lastFrameTicks) / Stopwatch.Frequency;
            double instantFps = dt > 0 ? (1.0 / dt) : 0.0;
            double frameMs = dt * 1000.0;

            double smoothing = Math.Clamp(FpsSmoothingFactor, 0.0, 0.999);
            _smoothedFps = (_smoothedFps <= 0.0) ? instantFps : (_smoothedFps * smoothing) + (instantFps * (1.0 - smoothing));
            _smoothedFrameTimeMs = (_smoothedFrameTimeMs <= 0.0) ? frameMs : (_smoothedFrameTimeMs * smoothing) + (frameMs * (1.0 - smoothing));

            _frameTimeHistory[_frameTimeHistoryPtr] = frameMs;
            _frameTimeHistoryPtr++;
            if (_frameTimeHistoryPtr >= _frameTimeHistory.Length)
                _frameTimeHistoryPtr = 0;
        }
        _lastFrameTicks = renderNowTicks;

        // Pass 1: Background/Clear
        gl.Viewport(0, 0, Math.Max(1, viewW), Math.Max(1, viewH));
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);

        bool hasFrame = false;
        bool interopSuccess = false;
        int w = 0, h = 0;

        if (_injectionActive)
        {
            if (TryReadInjectedFrame(out var frameHeader, out var pixelData))
            {
                _injectionHeaderReadFailedCount = 0; // Reset "unreachable" counter
                w = (int)frameHeader.Width;
                h = (int)frameHeader.Height;

                if (frameHeader.FrameCounter != _lastInjectionFrameCount)
                {
                    _lastInjectionFrameCount = frameHeader.FrameCounter;
                    _injectionEmptyReadCount = 0;
                    _lastInjectionSuccessTicks = renderNowTicks;
                    _injectionStableCount++;
                    if (_injectionStableCount > 60) _injectionLockIn = true;

                    hasFrame = true;

                    // Only close WGC if injection is stable (Locked-in)
                    if (_session != nint.Zero && _injectionLockIn)
                    {
                        lock (_sessionLock)
                        {
                            if (_session != nint.Zero)
                            {
                                Debug.WriteLine("[WGC] Closing fallback WGC session; direct hook is now stable.");
                                WgcBridgeApi.DestroyCaptureSession(_session);
                                _session = nint.Zero;
                            }
                        }
                    }
                }
                else
                {
                    // Same frame or torn frame - injection is alive, don't fallback yet
                    hasFrame = true; 
                    _injectionEmptyReadCount++; 
                }

                if (_glPixelStoreiPtr != IntPtr.Zero && hasFrame)
                    ((delegate* unmanaged[Stdcall]<int, int, void>)_glPixelStoreiPtr)(GL_UNPACK_ALIGNMENT, 1);

                gl.ActiveTexture(GlConsts.GL_TEXTURE0);
                gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureId);

                if (w != _texWidth || h != _texHeight)
                {
                    if (w > 0 && h > 0)
                    {
                        _texWidth = w;
                        _texHeight = h;
                        gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, _isEs ? 0x1908 : (int)GlConsts.GL_RGBA, w, h, 0, _isEs ? 0x1908 : 0x80E1, GlConsts.GL_UNSIGNED_BYTE, IntPtr.Zero);
                    }
                }

                if (hasFrame && w > 0 && h > 0)
                {
                    var texSub = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, uint, int, IntPtr, void>)_glTexSubImage2DPtr;
                    texSub(GlConsts.GL_TEXTURE_2D, 0, 0, 0, w, h, _isEs ? 0x1908u : 0x80E1u, GlConsts.GL_UNSIGNED_BYTE, pixelData);
                }
            }
            else
            {
                _injectionHeaderReadFailedCount++;
                _injectionEmptyReadCount++;

                if (_injectionHeaderReadFailedCount >= 1800 && _session == nint.Zero) // 30 seconds at 60fps
                {
                    Debug.WriteLine("[WGC] Injection session timed out (No shared memory) after 1800 attempts.");
                    CleanupInjectionSession();
                }
                else if (_session != nint.Zero && _injectionEmptyReadCount > 301)
                {
                    // If WGC is active, cap the counters so we don't kill the session while waiting
                    _injectionEmptyReadCount = 301;
                    _injectionHeaderReadFailedCount = Math.Min(_injectionHeaderReadFailedCount, 301);
                }
            }
        }

        // Fallback to WGC if injection is not active OR has truly timed out.
        // GRACE PERIOD: Wait at least 2 seconds of inactivity before considering injection "timed out"
        // to avoid rapid switching during loading screens or static menus.
        bool injectionTimedOut = _injectionActive && 
                                (double)(renderNowTicks - _lastInjectionSuccessTicks) / Stopwatch.Frequency >= 2.0;
                                
        if (!hasFrame && (!_injectionActive || injectionTimedOut) && TargetHwnd != IntPtr.Zero)
        {
            if (_session == nint.Zero)
            {
                // Fallback logic:
                // 1. injectionFailed: The hook is active but hasn't sent a NEW frame for ~30s (likely hung hook).
                // 2. injectionSilent: The shared memory is unreachable/garbage for ~5s (hook crashed).
                // 3. injectionNotWorking: Injection failed to start or process died.

                // If we are Locked-in, we are much more patient with static frames.
                double timeoutSeconds = _injectionLockIn ? 60.0 : 30.0;
                bool injectionFailed = _injectionActive && (double)(renderNowTicks - _lastInjectionSuccessTicks) / Stopwatch.Frequency >= timeoutSeconds;
                bool injectionSilent = _injectionActive && _injectionHeaderReadFailedCount > 600; // Increased to 10s at 60fps equivalent
                
                bool injectionNotWorking = !_injectionActive && TargetProcessId != 0;
                bool noInjection = TargetProcessId == 0;

                // Restrict WGC restarts to once every 10 seconds to prevent session thrashing
                if ((injectionFailed || injectionSilent || injectionNotWorking || noInjection) && (double)(renderNowTicks - _lastWgcRestartTicks) / Stopwatch.Frequency >= 10.0)
                {
                    if (injectionFailed)
                        Debug.WriteLine($"[WGC] Injection session static for {timeoutSeconds}s; falling back to WGC.");
                    else if (injectionSilent || (_injectionEmptyReadCount > 300 && _session == nint.Zero))
                    {
                        Debug.WriteLine("[WGC] Injection shared memory unreachable; falling back to WGC and disabling injection retry.");
                        _injectionPermanentlyFailed = true;
                    }

                    _lastWgcRestartTicks = renderNowTicks;
                    _injectionLockIn = false;
                    _injectionStableCount = 0;
                    Dispatcher.UIThread.Post(() => StartCapture(true), DispatcherPriority.Background);
                }
            }

            lock (_sessionLock)
            {
                if (_session != nint.Zero)
                {
                    int nativeCount = 0;
                    int readerCount = 0;
                    try
                    {
                        nativeCount = WgcBridgeApi.GetCaptureStatus(_session);
                        readerCount = WgcBridgeApi.GetReaderCount(_session);
                    }
                    catch { nativeCount = 0; }

                    bool isNewFrame = nativeCount > 0 && nativeCount != _lastNativeFrameCount;

                    // Log every 500 frames or if stuck for diagnostic purposes
                    if (nativeCount % 500 == 0 || (isNewFrame && nativeCount < 5))
                    {
                        Debug.WriteLine($"[WGC] Render loop status: nativeCount={nativeCount}, last={_lastNativeFrameCount}, readers={readerCount}, isNew={isNewFrame}");
                    }

                    if (isNewFrame)
                    {
                        if (!_bufferHandle.IsAllocated) EnsureBufferPinned();

                        nuint requiredSize;
                        bool peekOk = WgcBridgeApi.PeekLatestFrame(_session, out int peekW, out int peekH, out requiredSize);

                        if (peekOk && requiredSize > (nuint)_pixelBuffer.Length)
                        {
                            EnsureBufferCapacity((int)requiredSize);
                        }

                        nint bufferPtr = _bufferHandle.AddrOfPinnedObject();
                        bool releaseNative = false;

                        try
                        {
                            // 1. Try Desktop GL DX Interop (NV_DX_interop) - Best for NVIDIA/AMD on Desktop GL
                            if (!hasFrame && _usingDxInterop && _wglDXLockObjectsNV != null)
                            {
                                try
                                {
                                    IntPtr dxTex = WgcBridgeApi.GetLatestD3DTexture(_session);
                                    if (dxTex != IntPtr.Zero)
                                    {
                                        if (_dxTexturePtr != dxTex)
                                        {
                                            if (_wglObjectHandle != IntPtr.Zero) _wglDXUnregisterObjectNV?.Invoke(_wglDeviceHandle, _wglObjectHandle);
                                            _wglObjectHandle = _wglDXRegisterObjectNV?.Invoke(_wglDeviceHandle, dxTex, (uint)_textureId, (uint)GlConsts.GL_TEXTURE_2D, WGL_ACCESS_READ_ONLY_NV) ?? IntPtr.Zero;
                                            _dxTexturePtr = dxTex;
                                        }

                                        if (_wglObjectHandle != IntPtr.Zero)
                                        {
                                            if (_wglDXLockObjectsNV(_wglDeviceHandle, 1, new[] { _wglObjectHandle }))
                                            {
                                                _wglDXUnlockObjectsNV?.Invoke(_wglDeviceHandle, 1, new[] { _wglObjectHandle });
                                                WgcBridgeApi.PeekLatestFrame(_session, out w, out h, out _);
                                                hasFrame = true;
                                                interopSuccess = true;
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex) { Debug.WriteLine($"[WGC] DX Interop failed: {ex.Message}"); }
                            }

                            // 2. Try ANGLE/EGL Texture Import - Best for Intel/AMD on ANGLE (EGL)
                            if (!hasFrame && _usingAngleInterop && _eglCreateImageKHR != null && _glEGLImageTargetTexture2DOES != null)
                            {
                                try
                                {
                                    IntPtr shared = WgcBridgeApi.GetSharedHandle(_session);
                                    if (shared != IntPtr.Zero)
                                    {
                                        WgcBridgeApi.PeekLatestFrame(_session, out int peekW2, out int peekH2, out _);
                                        w = peekW2; h = peekH2;

                                        if (_currentSharedHandle != shared)
                                        {
                                            CleanupAngleInterop();
                                            var dpy = _eglGetCurrentDisplay?.Invoke() ?? IntPtr.Zero;
                                            var ctx = _eglGetCurrentContext?.Invoke() ?? IntPtr.Zero;
                                            if (dpy != IntPtr.Zero && ctx != IntPtr.Zero)
                                            {
                                                IntPtr img = _eglCreateImageKHR(dpy, ctx, EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE, shared, IntPtr.Zero);
                                                if (img != IntPtr.Zero)
                                                {
                                                    gl.ActiveTexture(GlConsts.GL_TEXTURE0);
                                                    gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureId);
                                                    _glEGLImageTargetTexture2DOES((uint)GlConsts.GL_TEXTURE_2D, img);
                                                    _eglImage = img;
                                                    _currentSharedHandle = shared;
                                                    hasFrame = true;
                                                    interopSuccess = true;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            hasFrame = true;
                                            interopSuccess = true;
                                        }
                                    }
                                }
                                catch (Exception ex) { Debug.WriteLine($"[WGC] Angle Interop failed: {ex.Message}"); }
                            }

                            // 3. Try Native Pointer Acquisition (CPU Readback but zero-copy to C#)
                            if (!hasFrame && _nativeAcquireSupported)
                            {
                                if (WgcBridgeApi.AcquireLatestFrame(_session, out IntPtr nativePtr, out nuint nativeSize, out int frameW, out int frameH))
                                {
                                    releaseNative = true;
                                    bufferPtr = nativePtr;
                                    w = frameW; h = frameH;
                                    hasFrame = true;
                                }
                            }

                            // 4. Fallback to Copy Buffer (Worst case: double CPU copy)
                            if (!hasFrame)
                            {
                                hasFrame = WgcBridgeApi.GetLatestFrame(_session, bufferPtr, (nuint)_pixelBuffer.Length, out w, out h);
                            }

                            if (hasFrame && w > 0 && h > 0)
                            {
                                _lastNativeFrameCount = nativeCount;

                                Dispatcher.UIThread.Post(() =>
                                {
                                    FrameNumber = nativeCount;
                                    LastFrameWidth = w;
                                    LastFrameHeight = h;
                                }, DispatcherPriority.Render);

                                // CPU fallback upload if no Interop worked
                                if (!interopSuccess)
                                {
                                    gl.ActiveTexture(GlConsts.GL_TEXTURE0);
                                    gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureId);

                                    if (w != _texWidth || h != _texHeight)
                                    {
                                        _texWidth = w; _texHeight = h;
                                        gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, _isEs ? 0x1908 : (int)GlConsts.GL_RGBA, w, h, 0, _isEs ? 0x1908 : 0x80E1, GlConsts.GL_UNSIGNED_BYTE, IntPtr.Zero);
                                    }

                                    // Upload directly from acquired bufferPtr for maximum reliability
                                    if (_glPixelStoreiPtr != IntPtr.Zero)
                                        ((delegate* unmanaged[Stdcall]<int, int, void>)_glPixelStoreiPtr)(GL_UNPACK_ALIGNMENT, 1);

                                    var texSub = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, uint, int, IntPtr, void>)_glTexSubImage2DPtr;
                                    texSub(GlConsts.GL_TEXTURE_2D, 0, 0, 0, w, h, _isEs ? 0x1908u : 0x80E1u, GlConsts.GL_UNSIGNED_BYTE, bufferPtr);
                                }
                            }
                        }
                        catch (Exception ex) { Debug.WriteLine($"[WGC] Exception in new frame processing: {ex.Message}"); }
                        finally
                        {
                            if (releaseNative) WgcBridgeApi.ReleaseLatestFrame(_session);
                        }
                    }
                }
            }
        }

        // Always render what we have (background + last frame or new frame)
        if (_program != 0)
        {
            // THROTTLE UI UPDATES: Only update Fps and diagnostic props every ~33ms (30 FPS)
            // to prevent UI thread saturation at extremely high framerates.
            long now = Stopwatch.GetTimestamp();
            if ((double)(now - _lastUiUpdateTicks) / Stopwatch.Frequency >= 0.033)
            {
                _lastUiUpdateTicks = now;
                Geometry? frametimeGeometry = null;
                double graphWidth = ShowDetailedGpuInfo ? 330.0 : 180.0;
                if (ShowFrametimeGraph)
                    frametimeGeometry = BuildFrametimeGraphGeometry(graphWidth, 40.0, 50.0);

                Dispatcher.UIThread.Post(() =>
                {
                    Fps = Math.Round(_smoothedFps, 1);
                    FrameTimeMs = Math.Round(_smoothedFrameTimeMs, 2);
                    FrametimeGraphWidth = graphWidth;
                    if (ShowFrametimeGraph)
                        FrametimeGraphGeometry = frametimeGeometry;
                    else if (FrametimeGraphGeometry != null)
                        FrametimeGraphGeometry = null;

                    if (_session != nint.Zero)
                    {
                        try
                        {
                            FrameNumber = (int)WgcBridgeApi.GetCaptureStatus(_session);
                            nuint required;
                            if (WgcBridgeApi.PeekLatestFrame(_session, out int pw, out int ph, out required))
                            {
                                LastFrameWidth = pw;
                                LastFrameHeight = ph;
                            }
                        }
                        catch { }
                    }

                    // Update BackendName with current live capture state
                    bool usingInjected = _injectionActive && hasFrame;
                    string method = usingInjected ? "Direct Hook (Injected)" : "Windows Graphics Capture (WGC)";
                    if (!usingInjected)
                    {
                        if (_usingDxInterop) method += " [DX Interop]";
                        else if (_usingAngleInterop) method += " [ANGLE/DirectX]";
                        else if (_nativeAcquireSupported) method += " [Native DXGI]";
                        else method += " [CPU Copy]";
                    }
                    else
                    {
                        // Update injected stats
                        FrameNumber = (int)_lastInjectionFrameCount;
                    }
                    BackendName = method;
                }, DispatcherPriority.Background);
            }

            // --- PASS 1: BACKGROUND / LETTERBOX ---
            if (_letterboxNeedsUpdate && _letterboxPixels != null)
            {
                gl.ActiveTexture(GL_TEXTURE1);
                gl.BindTexture(GlConsts.GL_TEXTURE_2D, _letterboxTexId);
                fixed (byte* p = _letterboxPixels)
                    gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlConsts.GL_RGBA, _letterboxWidth, _letterboxHeight, 0, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, (IntPtr)p);
                _letterboxNeedsUpdate = false;
            }

            gl.Disable(0x0C11); // GL_SCISSOR_TEST
            gl.UseProgram(_program);
            if (_letterboxPixels != null)
            {
                gl.ActiveTexture(GL_TEXTURE1); gl.BindTexture(GlConsts.GL_TEXTURE_2D, _letterboxTexId);
                SetUniform1I(gl, _program, "uTex", 1);
                SetUniform4fv(gl, _program, "uFallbackColor", _colorTransparent);
            }
            else
            {
                var c = LetterBoxColor;
                _tempColor[0] = c.R / 255f; _tempColor[1] = c.G / 255f; _tempColor[2] = c.B / 255f; _tempColor[3] = 1.0f;
                SetUniform4fv(gl, _program, "uFallbackColor", _tempColor);
                gl.ActiveTexture(GlConsts.GL_TEXTURE0);
                SetUniform1I(gl, _program, "uTex", 0);
            }

            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _bgVbo);
            gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 2, GlConsts.GL_FLOAT, 0, 16, IntPtr.Zero);
            gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 2, GlConsts.GL_FLOAT, 0, 16, (IntPtr)8);
            gl.DrawArrays(0x0005, 0, 4);

            // --- PASS 2: ACTUAL GAME FRAME ---
            int currentW = hasFrame ? w : _texWidth;
            int currentH = hasFrame ? h : _texHeight;

            if (currentW > 0 && currentH > 0)
            {
                if (_shaderPipeline != null && _shaderPipeline.HasActiveShader)
                {
                    // Calculate target rectangle for aspect-correct rendering
                    var targetRect = CalculateAspectRect(viewW, viewH, currentW, currentH);
                    
                    if (FrameNumber % 1000 == 0)
                    {
                        Debug.WriteLine($"[WGC] Render Pass 2: Viewport({targetRect.X}, {targetRect.Y}, {targetRect.Width}, {targetRect.Height}) | View({viewW}x{viewH}) | Frame({currentW}x{currentH})");
                    }

                    gl.Viewport(targetRect.X, targetRect.Y, targetRect.Width, targetRect.Height);
                    
                    // Update shader pipeline with current settings
                    _shaderPipeline.Brightness = (float)Brightness;
                    _shaderPipeline.Saturation = (float)Saturation;
                    _shaderPipeline.ColorTint = new float[] { ColorTint.R / 255f, ColorTint.G / 255f, ColorTint.B / 255f, ColorTint.A / 255f };

                    _shaderPipeline.Process(_textureId, currentW, currentH, fb, targetRect.X, targetRect.Y, targetRect.Width, targetRect.Height);
                    if (hasFrame) _shaderPipeline.CaptureFrameToHistory(targetRect.X, targetRect.Y, targetRect.Width, targetRect.Height, fb);
                }
                else
                {
                    // Full viewport rendering for fallback path; aspect handled by dynamic vertices for best sub-pixel quality
                    gl.Viewport(0, 0, Math.Max(1, viewW), Math.Max(1, viewH));
                    UpdateVertices(viewW, viewH, currentW, currentH);
                    UploadFloatBuffer(gl, _vbo, _quadBuffer, GL_DYNAMIC_DRAW);

                    gl.UseProgram(_program);
                    gl.ActiveTexture(GlConsts.GL_TEXTURE0); gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureId);
                    SetUniform1I(gl, _program, "uTex", 0);
                    SetUniform4fv(gl, _program, "uFallbackColor", _colorTransparent);

                    // Pass current brightness, saturation and tint to the fallback shader
                    SetUniform1f(gl, _program, "uBrightness", (float)Brightness);
                    SetUniform1f(gl, _program, "uSaturation", (float)Saturation);
                    _tempColor[0] = ColorTint.R / 255f; _tempColor[1] = ColorTint.G / 255f; _tempColor[2] = ColorTint.B / 255f; _tempColor[3] = ColorTint.A / 255f;
                    SetUniform4fv(gl, _program, "uColorTint", _tempColor);

                    gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vbo);
                    gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 2, GlConsts.GL_FLOAT, 0, 16, IntPtr.Zero);
                    gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 2, GlConsts.GL_FLOAT, 0, 16, (IntPtr)8);
                    gl.DrawArrays(0x0005, 0, 4);
                }
            }
        }

        // CONTINUOUS REDRAW: Request next frame immediately to run as fast as possible.
        // We do this if either a WGC session is active OR if the injection is active.
        if (_session != nint.Zero || _injectionActive)
        {
            RequestNextFrameRendering();
        }

        var renderEnd = Stopwatch.GetTimestamp();
        double renderMs = (renderEnd - renderStart) * 1000.0 / Stopwatch.Frequency;
        if (renderMs > 20.0)
        {
            Debug.WriteLine($"[WGC] SLOW RENDER: {renderMs:F2}ms");
        }
    }

    private PixelRect CalculateAspectRect(int viewW, int viewH, int frameW, int frameH)
    {
        var stretch = Stretch;
        if (stretch == Stretch.Fill) return new PixelRect(0, 0, viewW, viewH);

        float viewAspect = (float)viewW / viewH;
        float frameAspect = (float)frameW / frameH;

        if (stretch == Stretch.Uniform)
        {
            if (frameAspect > viewAspect)
            {
                int h = (int)(viewW / frameAspect);
                return new PixelRect(0, (viewH - h) / 2, viewW, h);
            }
            else
            {
                int w = (int)(viewH * frameAspect);
                return new PixelRect((viewW - w) / 2, 0, w, viewH);
            }
        }
        else if (stretch == Stretch.UniformToFill)
        {
            if (frameAspect > viewAspect)
            {
                int w = (int)(viewH * frameAspect);
                return new PixelRect((viewW - w) / 2, 0, w, viewH);
            }
            else
            {
                int h = (int)(viewW / frameAspect);
                return new PixelRect(0, (viewH - h) / 2, viewW, h);
            }
        }

        return new PixelRect((viewW - frameW) / 2, (viewH - frameH) / 2, frameW, frameH);
    }

    private Geometry BuildFrametimeGraphGeometry(double width, double height, double maxMs)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            int count = _frameTimeHistory.Length;
            if (count <= 1)
                return geometry;

            double step = width / (count - 1);
            int idx = _frameTimeHistoryPtr;
            bool started = false;

            for (int i = 0; i < count; i++)
            {
                double ms = _frameTimeHistory[idx];
                ms = Math.Clamp(ms, 0.0, maxMs);
                double x = i * step;
                double y = height - ((ms / maxMs) * height);
                var point = new Point(x, y);

                if (!started)
                {
                    ctx.BeginFigure(point, false);
                    started = true;
                }
                else
                {
                    ctx.LineTo(point);
                }

                idx++;
                if (idx >= count)
                    idx = 0;
            }
        }

        return geometry;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _mouseTunnel.Dispose();
        _uiHeartbeat?.Stop();

        // Cleanup host event subscriptions
        DetachHostTopLevel();

        CleanupTargetWindow(TargetHwnd);

        lock (_sessionLock)
        {
            if (_bufferHandle.IsAllocated) _bufferHandle.Free();
            if (_session != nint.Zero) { WgcBridgeApi.DestroyCaptureSession(_session); _session = nint.Zero; }
            if (_vbo != 0) gl.DeleteBuffer(_vbo);
            if (_bgVbo != 0) gl.DeleteBuffer(_bgVbo);
            if (_program != 0) gl.DeleteProgram(_program);
            if (_textureId != 0) gl.DeleteTexture(_textureId);
            if (_letterboxTexId != 0) gl.DeleteTexture(_letterboxTexId);
            for (int i = 0; i < 2; i++) if (_pbo[i] != 0) gl.DeleteBuffer(_pbo[i]);
            CleanupDxInterop();
            CleanupAngleInterop();
            _heartbeatTimer?.Stop();
        }
        base.OnOpenGlDeinit(gl);
    }

    private void OnStretchChanged(Stretch newVal)
    {
        try { Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render); } catch { }
    }

    private void OnTargetHwndChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is IntPtr oldHwnd && oldHwnd != IntPtr.Zero)
        {
            CleanupTargetWindow(oldHwnd);
        }

        if (e.NewValue is IntPtr hwnd && hwnd != IntPtr.Zero)
        {
            ResetCaptureFrameState();
            try
            {
                Win32API.MoveAway(hwnd);
            }
            catch { }

            TryAttachTargetWindow();
        }
        else
        {
            _mouseTunnel.TargetHwnd = IntPtr.Zero;
            lock (_sessionLock)
            {
                if (_session != nint.Zero) { WgcBridgeApi.DestroyCaptureSession(_session); _session = nint.Zero; }
                ResetCaptureFrameState();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private void OnTargetProcessIdChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is int oldProcessId && oldProcessId != 0)
        {
            Debug.WriteLine($"[WGC] TargetProcessId changed from {oldProcessId} to {e.NewValue}");
            CleanupInjectionSession();
        }

        if (e.NewValue is int processId && processId != 0)
        {
            Debug.WriteLine($"[WGC] TargetProcessId changed, starting injection for PID {processId}");
            _injectionPermanentlyFailed = false;
            ResetCaptureFrameState();
            _ = TryStartInjectionSessionAsync(processId);
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task TryStartInjectionSessionAsync(int processId)
    {
        Debug.WriteLine($"[WGC] TryStartInjectionSessionAsync(processId={processId})");
        CleanupInjectionSession();

        if (TargetHwnd != IntPtr.Zero)
        {
            TryAttachTargetWindow();
        }

        try
        {
            MemoryMappedFile? mmf = null;
            MemoryMappedViewAccessor? accessor = null;
            var success = await Task.Run(() => InjectionBridgeApi.TryInjectProcess(processId, TimeSpan.FromSeconds(30), out mmf, out accessor));
            Debug.WriteLine($"[WGC] TryInjectProcess returned {success} for PID {processId}");
            if (!success)
            {
                _injectionActive = false;
                return;
            }

            _injectionMemoryMappedFile = mmf;
            _injectionAccessor = accessor;
            _injectionActive = true;
            await Dispatcher.UIThread.InvokeAsync(() => BackendName = "Injected capture");
            Debug.WriteLine($"[WGC] Injection session established for PID {processId}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WGC] TryStartInjectionSessionAsync threw: {ex}");
            _injectionActive = false;
            CleanupInjectionSession();
        }
    }

    private void OnRequestStopSessionChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not bool requested || !requested)
            return;

        lock (_sessionLock)
        {
            if (_session != nint.Zero)
            {
                WgcBridgeApi.DestroyCaptureSession(_session);
                _session = nint.Zero;
            }
            ResetCaptureFrameState();
        }

        if (_injectionActive)
            CleanupInjectionSession();

        SetValue(RequestStopSessionProperty, false);
    }

    private void WgcCaptureControl_Loaded(object? sender, RoutedEventArgs e)
    {
        if (TargetHwnd == IntPtr.Zero) return;
        TryAttachTargetWindow();
    }

    private void Mw_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Win32API.ForceEmulatorFocus(TargetHwnd, _hostHandle, 200);
    }

    private void CleanupTargetWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        if (_pboPersistentSupported || _usingDxInterop || _dxInteropAvailable || _pboInitialized || _pboIndex != 0)
        {
            Debug.WriteLine($"[WGC] CleanupTargetWindow flags: pboPersistent={_pboPersistentSupported}, pboInitialized={_pboInitialized}, pboIndex={_pboIndex}, usingDxInterop={_usingDxInterop}, dxAvailable={_dxInteropAvailable}");
        }

        try
        {
            _windowHandler?.Stop();
            _windowHandler?.RestoreOriginalPosition();
            _windowHandler = null;

            Win32API.RestoreWindowDecorations(hwnd);
            Win32API.SetWindowOpacity(hwnd, 255);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WGC] Error during CleanupTargetWindow: {ex.Message}");
        }
    }

    private bool TryResolveHostHandle()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return false;

        if (!ReferenceEquals(_hostTopLevel, topLevel))
        {
            DetachHostTopLevel();
            _hostTopLevel = topLevel;
            _hostTopLevel.PointerReleased -= Mw_PointerReleased;
            _hostTopLevel.PointerReleased += Mw_PointerReleased;
        }

        if (topLevel.TryGetPlatformHandle() is not IPlatformHandle platform || platform.Handle == IntPtr.Zero)
            return false;

        _hostHandle = platform.Handle;
        return true;
    }

    private void DetachHostTopLevel()
    {
        if (_hostTopLevel == null)
            return;

        _hostTopLevel.PointerReleased -= Mw_PointerReleased;
        _hostTopLevel = null;
    }

    private void TryAttachTargetWindow()
    {
        if (TargetHwnd == IntPtr.Zero)
            return;

        if (!TryResolveHostHandle())
        {
            Loaded -= WgcCaptureControl_Loaded;
            Loaded += WgcCaptureControl_Loaded;
            return;
        }

        Loaded -= WgcCaptureControl_Loaded;

        _windowHandler?.Stop();
        _windowHandler = new WindowHandler(10, 4, 4, 4, 4);
        _windowHandler.EnableRoundedCorners(44);
        _windowHandler.SetMoveToHost(false);
        _windowHandler.Start(_hostHandle, TargetHwnd);

        try
        {
            Win32API.RestoreWindow(TargetHwnd);
        }
        catch { }

        // Aggressive window manipulations can cause DX12 swapchain creation failures in emulators like Xenia.
        // We delay these calls to ensure the emulator has finished its initial window setup.
        IntPtr currentHwnd = TargetHwnd;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500); // 1.5s delay for stability
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (TargetHwnd == currentHwnd && currentHwnd != IntPtr.Zero)
                {
                    Win32API.RemoveWindowDecorations(currentHwnd);
                    Win32API.MoveAway(currentHwnd);
                    Debug.WriteLine("[WGC] Applied window decorations and moved away after stability delay.");
                }
            });
        });

        StartCapture();
    }

    private void OnRetroarchShaderFileChanged(AvaloniaPropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
    }

    private void OnForceUseTargetClientSizeChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is bool enabled && !enabled && _session != nint.Zero)
        {
            lock (_sessionLock)
            {
                if (_session != nint.Zero)
                {
                    WgcBridgeApi.SetCaptureCropRect(_session, 0, 0, 0, 0);
                    _lastCropX = _lastCropY = _lastCropW = _lastCropH = 0;
                }
            }
        }
    }

    private void LoadShaderPreset(GlInterface gl, string? path)

    {
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                _shaderPipeline?.Dispose();
                _shaderPipeline = null;
                _currentShaderPath = path;
                Debug.WriteLine("[WGC] Shader preset cleared.");
                return;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".glsl" || ext == ".glslp" || ext == ".slang" || ext == ".slangp" || ext == ".cgp")
            {
                string shaderToLoad = path;

                // For OpenGL compatibility (WgcCaptureControl is an OpenGlControlBase), 
                // try to redirect .slang to .glsl equivalents if they exist.
                if (ext.Contains(".slang"))
                {
                    string glslExt = ext.Replace(".slang", ".glsl");
                    string? glslPath = path.Replace(".slang", ".glsl")
                                          .Replace("\\slang\\", "\\glsl\\")
                                          .Replace("/slang/", "/glsl/");

                    if (File.Exists(glslPath))
                    {
                        Debug.WriteLine($"[WGC] Redirecting .slang shader to .glsl for OpenGL compatibility: {glslPath}");
                        shaderToLoad = glslPath;
                    }
                }

                _shaderPipeline?.Dispose();
                _shaderPipeline = new SlangShaderPipeline(gl);
                _shaderPipeline.LoadShaderPreset(shaderToLoad);
                _currentShaderPath = path; // Keep original path as current so UI/comparison works
                Debug.WriteLine($"[WGC] Shader preset loaded: '{shaderToLoad}'.");
            }
            else
            {
                _shaderPipeline?.Dispose();
                _shaderPipeline = null;
                _currentShaderPath = null;
                Debug.WriteLine($"[WGC] Shader preset ignored due to unsupported extension: '{path}'.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WGC] LoadShaderPreset exception: {ex}");
            _shaderPipeline?.Dispose();
            _shaderPipeline = null;
            _currentShaderPath = null;
        }
    }

    private unsafe void SetUniform1f(GlInterface gl, int prog, string name, float val)
    {
        if (_glUniform1FPtr == IntPtr.Zero) return;

        int loc = -1;
        if (name == "uBrightness") loc = _uBrightnessLoc;
        else if (name == "uSaturation") loc = _uSaturationLoc;
        else if (_uniformLocationCache != null)
        {
            if (!_uniformLocationCache.TryGetValue(name, out loc))
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(name + "\0");
                fixed (byte* pName = nameBytes) loc = gl.GetUniformLocation(prog, (IntPtr)pName);
                _uniformLocationCache[name] = loc;
            }
        }

        if (loc != -1) ((delegate* unmanaged[Stdcall]<int, float, void>)_glUniform1FPtr)(loc, val);
    }

    private unsafe void SetUniform1I(GlInterface gl, int prog, string name, int val)
    {
        if (_glUniform1IPtr == IntPtr.Zero) return;

        int loc = -1;
        if (name == "uTex") loc = _uTexLoc;
        else if (_uniformLocationCache != null)
        {
            if (!_uniformLocationCache.TryGetValue(name, out loc))
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(name + "\0");
                fixed (byte* pName = nameBytes) loc = gl.GetUniformLocation(prog, (IntPtr)pName);
                _uniformLocationCache[name] = loc;
            }
        }

        if (loc != -1) ((delegate* unmanaged[Stdcall]<int, int, void>)_glUniform1IPtr)(loc, val);
    }

    private unsafe void SetUniform4fv(GlInterface gl, int prog, string name, float[] vals)
    {
        if (_glUniform4fvPtr == IntPtr.Zero) return;

        int loc = -1;
        if (name == "uFallbackColor") loc = _uFallbackColorLoc;
        else if (name == "uColorTint") loc = _uColorTintLoc;
        else if (_uniformLocationCache != null)
        {
            if (!_uniformLocationCache.TryGetValue(name, out loc))
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(name + "\0");
                fixed (byte* pName = nameBytes) loc = gl.GetUniformLocation(prog, (IntPtr)pName);
                _uniformLocationCache[name] = loc;
            }
        }

        if (loc != -1) fixed (float* pVals = vals) ((delegate* unmanaged[Stdcall]<int, int, float*, void>)_glUniform4fvPtr)(loc, 1, pVals);
    }

    private int CreateProgram(GlInterface gl, string vsSrc, string fsSrc)
    {
        int vs = gl.CreateShader(GlConsts.GL_VERTEX_SHADER); CompileShader(gl, vs, vsSrc);
        int fs = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER); CompileShader(gl, fs, fsSrc);
        int prog = gl.CreateProgram();
        gl.AttachShader(prog, vs); gl.AttachShader(prog, fs); gl.LinkProgram(prog);
        gl.DeleteShader(vs); gl.DeleteShader(fs);
        return prog;
    }

    private unsafe void CompileShader(GlInterface gl, int shader, string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* ptr = bytes) { sbyte* pStr = (sbyte*)ptr; sbyte** ppStr = &pStr; int len = bytes.Length; gl.ShaderSource(shader, 1, (IntPtr)ppStr, (IntPtr)(&len)); }
        gl.CompileShader(shader);
    }

    private unsafe void UploadFloatBuffer(GlInterface gl, int buffer, float[] data, int usage)
    {
        int size = data.Length * sizeof(float);
        fixed (float* pData = data) { gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, buffer); gl.BufferData(GlConsts.GL_ARRAY_BUFFER, size, (IntPtr)pData, usage); }
    }

    private unsafe void SwizzleBgraToRgba(byte* p, long pixels)
    {
        long pi = 0;
        if (Vector128.IsHardwareAccelerated && pixels >= 4)
        {
            var shuffleMask = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
            long vectorCount = pixels / 4;
            for (long vi = 0; vi < vectorCount; vi++)
            {
                byte* ptr = p + vi * 16;
                var v = Vector128.Load(ptr);
                var swizzled = Vector128.Shuffle(v, shuffleMask);
                swizzled.Store(ptr);
            }
            pi = vectorCount * 4;
        }

        for (; pi < pixels; pi++)
        {
            long idx = pi * 4;
            byte t = p[idx]; p[idx] = p[idx + 2]; p[idx + 2] = t;
        }
    }

    private void EnsureBufferPinned()
    {
        if (_bufferHandle.IsAllocated) _bufferHandle.Free();
        _bufferHandle = GCHandle.Alloc(_pixelBuffer, GCHandleType.Pinned);
    }

    private void EnsureBufferCapacity(int requiredBytes)
    {
        const int THRESHOLD = 1024 * 1024;
        if (requiredBytes <= _pixelBuffer.Length || requiredBytes <= _pixelBuffer.Length + THRESHOLD)
            return;

        int newSize = requiredBytes + (requiredBytes / 4);
        _pixelBuffer = new byte[newSize];
        if (_bufferHandle.IsAllocated) _bufferHandle.Free();
        _bufferHandle = GCHandle.Alloc(_pixelBuffer, GCHandleType.Pinned);
    }

    private unsafe void EnsurePboCapacity(GlInterface gl, long requiredBytes)
    {
        for (int i = 0; i < 2; i++)
        {
            if (_pboCapacity[i] < requiredBytes)
            {
                if (_pbo[i] != 0) gl.DeleteBuffer(_pbo[i]);
                _pbo[i] = gl.GenBuffer();
                gl.BindBuffer(GL_PIXEL_UNPACK_BUFFER, _pbo[i]);
                gl.BufferData(GL_PIXEL_UNPACK_BUFFER, (int)requiredBytes, IntPtr.Zero, GL_STREAM_DRAW);
                _pboCapacity[i] = requiredBytes;
            }
        }
        gl.BindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);
    }

    private void OnLetterboxColorChanged(AvaloniaPropertyChangedEventArgs e) => Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);

    private void OnLetterboxBitmapChanged(Bitmap? newBitmap)
    {
        if (newBitmap == null) { _letterboxPixels = null; _letterboxNeedsUpdate = true; return; }

        _letterboxWidth = newBitmap.PixelSize.Width;
        _letterboxHeight = newBitmap.PixelSize.Height;
        _letterboxStride = _letterboxWidth * 4;
        int bufferSize = _letterboxHeight * _letterboxStride;

        if (_letterboxPixels == null || _letterboxPixels.Length != bufferSize)
        {
            _letterboxPixels = new byte[bufferSize];
        }

        var ptr = Marshal.AllocHGlobal(bufferSize);
        try
        {
            newBitmap.CopyPixels(new PixelRect(0, 0, _letterboxWidth, _letterboxHeight), ptr, bufferSize, _letterboxStride);
            Marshal.Copy(ptr, _letterboxPixels, 0, bufferSize);

            unsafe
            {
                fixed (byte* p = _letterboxPixels)
                {
                    SwizzleBgraToRgba(p, bufferSize / 4);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        _letterboxNeedsUpdate = true;
        RequestNextFrameRendering();
    }

    private void TryInitDxInterop(GlInterface gl)
    {
        CleanupDxInterop();
        if (_session == nint.Zero) return;
        try { WgcBridgeApi.SetInteropEnabled(_session, 1); } catch { }
        nint devPtr = WgcBridgeApi.GetD3D11Device(_session);
        if (devPtr == nint.Zero) { _usingDxInterop = false; return; }
        _dxDevicePtr = (IntPtr)devPtr;

        IntPtr proc_open = gl.GetProcAddress("wglDXOpenDeviceNV");
        IntPtr proc_close = gl.GetProcAddress("wglDXCloseDeviceNV");
        IntPtr proc_reg = gl.GetProcAddress("wglDXRegisterObjectNV");
        IntPtr proc_unreg = gl.GetProcAddress("wglDXUnregisterObjectNV");
        IntPtr proc_lock = gl.GetProcAddress("wglDXLockObjectsNV");
        IntPtr proc_unlock = gl.GetProcAddress("wglDXUnlockObjectsNV");

        if (proc_open == IntPtr.Zero || proc_close == IntPtr.Zero || proc_reg == IntPtr.Zero || proc_unreg == IntPtr.Zero || proc_lock == IntPtr.Zero || proc_unlock == IntPtr.Zero)
        {
            _usingDxInterop = false;
            return;
        }

        _wglDXOpenDeviceNV = Marshal.GetDelegateForFunctionPointer<wglDXOpenDeviceNVDel>(proc_open);
        _wglDXCloseDeviceNV = Marshal.GetDelegateForFunctionPointer<wglDXCloseDeviceNVDel>(proc_close);
        _wglDXRegisterObjectNV = Marshal.GetDelegateForFunctionPointer<wglDXRegisterObjectNVDel>(proc_reg);
        _wglDXUnregisterObjectNV = Marshal.GetDelegateForFunctionPointer<wglDXUnregisterObjectNVDel>(proc_unreg);
        _wglDXLockObjectsNV = Marshal.GetDelegateForFunctionPointer<wglDXLockObjectsNVDel>(proc_lock);
        _wglDXUnlockObjectsNV = Marshal.GetDelegateForFunctionPointer<wglDXUnlockObjectsNVDel>(proc_unlock);

        try
        {
            _wglDeviceHandle = _wglDXOpenDeviceNV?.Invoke(_dxDevicePtr) ?? IntPtr.Zero;
            if (_wglDeviceHandle == IntPtr.Zero) { _usingDxInterop = false; return; }
            Debug.WriteLine("[WGC] Desktop GL DX Interop (NV_DX_interop) initialized successfully.");
        }
        catch (Exception ex) 
        { 
            Debug.WriteLine($"[WGC] Desktop GL DX Interop initialization failed: {ex.Message}");
            _usingDxInterop = false; 
            return; 
        }
        _usingDxInterop = true;
    }

    private void CleanupDxInterop()
    {
        try { if (_wglObjectHandle != IntPtr.Zero && _wglDeviceHandle != IntPtr.Zero && _wglDXUnregisterObjectNV != null) _wglDXUnregisterObjectNV(_wglDeviceHandle, _wglObjectHandle); } catch { }
        _wglObjectHandle = IntPtr.Zero;
        try { if (_wglDeviceHandle != IntPtr.Zero && _wglDXCloseDeviceNV != null) _wglDXCloseDeviceNV(_wglDeviceHandle); } catch { }
        _wglDeviceHandle = IntPtr.Zero;
        _dxDevicePtr = IntPtr.Zero;
        _dxTexturePtr = IntPtr.Zero;
        _usingDxInterop = false;
    }

    private void TryInitAngleInterop(GlInterface gl)
    {
        try
        {
            IntPtr pGetDisplay = gl.GetProcAddress("eglGetCurrentDisplay");
            IntPtr pGetContext = gl.GetProcAddress("eglGetCurrentContext");
            IntPtr pCreate = gl.GetProcAddress("eglCreateImageKHR");
            IntPtr pDestroy = gl.GetProcAddress("eglDestroyImageKHR");
            IntPtr pGlEGL = gl.GetProcAddress("glEGLImageTargetTexture2DOES");
            if (pGetDisplay == IntPtr.Zero || pGetContext == IntPtr.Zero || pCreate == IntPtr.Zero || pDestroy == IntPtr.Zero || pGlEGL == IntPtr.Zero)
            {
                _usingAngleInterop = false;
                return;
            }
            _eglGetCurrentDisplay = Marshal.GetDelegateForFunctionPointer<eglGetCurrentDisplayDel>(pGetDisplay);
            _eglGetCurrentContext = Marshal.GetDelegateForFunctionPointer<eglGetCurrentContextDel>(pGetContext);
            _eglCreateImageKHR = Marshal.GetDelegateForFunctionPointer<eglCreateImageKHRDel>(pCreate);
            _eglDestroyImageKHR = Marshal.GetDelegateForFunctionPointer<eglDestroyImageKHRDel>(pDestroy);
            _glEGLImageTargetTexture2DOES = Marshal.GetDelegateForFunctionPointer<glEGLImageTargetTexture2DOESDel>(pGlEGL);
            _eglDisplay = _eglGetCurrentDisplay?.Invoke() ?? IntPtr.Zero;
            _eglContext = _eglGetCurrentContext?.Invoke() ?? IntPtr.Zero;
            if (_eglDisplay == IntPtr.Zero || _eglContext == IntPtr.Zero) { _usingAngleInterop = false; return; }
            
            Debug.WriteLine("[WGC] ANGLE/EGL Interop initialized successfully.");
            _usingAngleInterop = true;
        }
        catch (Exception ex) 
        { 
            Debug.WriteLine($"[WGC] ANGLE/EGL Interop initialization failed: {ex.Message}");
            _usingAngleInterop = false; 
        }
    }

    private void CleanupAngleInterop()
    {
        try
        {
            if (_eglImage != IntPtr.Zero && _eglDestroyImageKHR != null && _eglGetCurrentDisplay != null)
            {
                var dpy = _eglGetCurrentDisplay?.Invoke() ?? IntPtr.Zero;
                if (dpy != IntPtr.Zero) _eglDestroyImageKHR?.Invoke(dpy, _eglImage);
            }
        }
        catch { }
        _eglImage = IntPtr.Zero;
        _currentSharedHandle = IntPtr.Zero;
        _eglDisplay = IntPtr.Zero;
        _eglContext = IntPtr.Zero;
        _usingAngleInterop = false;
    }

    private void UpdateVertices(int viewW, int viewH, int frameW, int frameH)
    {
        if (viewW <= 0 || viewH <= 0 || frameW <= 0 || frameH <= 0) return;
        float x = 1.0f, y = 1.0f;
        var stretch = Stretch;
        if (stretch == Stretch.Uniform)
        {
            float viewAspect = (float)viewW / viewH;
            float frameAspect = (float)frameW / frameH;
            if (frameAspect > viewAspect) y = viewAspect / frameAspect;
            else x = frameAspect / viewAspect;
        }
        else if (stretch == Stretch.UniformToFill)
        {
            float viewAspect = (float)viewW / viewH;
            float frameAspect = (float)frameW / frameH;
            if (frameAspect > viewAspect) x = frameAspect / viewAspect;
            else y = viewAspect / frameAspect;
        }
        else if (stretch == Stretch.None)
        {
            x = (float)frameW / viewW;
            y = (float)frameH / viewH;
        }
        _quadBuffer[0] = -x; _quadBuffer[1] = y; _quadBuffer[2] = 0; _quadBuffer[3] = 0;
        _quadBuffer[4] = -x; _quadBuffer[5] = -y; _quadBuffer[6] = 0; _quadBuffer[7] = 1;
        _quadBuffer[8] = x; _quadBuffer[9] = y; _quadBuffer[10] = 1; _quadBuffer[11] = 0;
        _quadBuffer[12] = x; _quadBuffer[13] = -y; _quadBuffer[14] = 1; _quadBuffer[15] = 1;
    }
    #endregion

    #region CLR Property Wrappers
    public IntPtr TargetHwnd { get => GetValue(TargetHwndProperty); set => SetValue(TargetHwndProperty, value); }
    public int TargetProcessId { get => GetValue(TargetProcessIdProperty); set => SetValue(TargetProcessIdProperty, value); }
    public Stretch Stretch { get => GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    public string? RetroarchShaderFile { get => GetValue(RetroarchShaderFileProperty); set => SetValue(RetroarchShaderFileProperty, value); }
    public int MaxCaptureHeight { get => GetValue(MaxCaptureHeightProperty); set => SetValue(MaxCaptureHeightProperty, value); }
    public bool DisableDownscale { get => GetValue(DisableDownscaleProperty); set => SetValue(DisableDownscaleProperty, value); }
    public int HeartbeatIntervalMs { get => GetValue(HeartbeatIntervalMsProperty); set => SetValue(HeartbeatIntervalMsProperty, value); }
    public Color LetterBoxColor { get => GetValue(LetterBoxColorProperty); set => SetValue(LetterBoxColorProperty, value); }
    public Bitmap? LetterBoxBitmap { get => GetValue(LetterBoxBitmapProperty); set => SetValue(LetterBoxBitmapProperty, value); }
    public double Brightness { get => GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
    public double Saturation { get => GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }
    public Color ColorTint { get => GetValue(ColorTintProperty); set => SetValue(ColorTintProperty, value); }
    public bool ForceUseTargetClientSize { get => GetValue(ForceUseTargetClientSizeProperty); set => SetValue(ForceUseTargetClientSizeProperty, value); }
    public bool RequestStopSession { get => GetValue(RequestStopSessionProperty); set => SetValue(RequestStopSessionProperty, value); }
    public bool ShowStatisticsOverlay { get => GetValue(ShowStatisticsOverlayProperty); set => SetValue(ShowStatisticsOverlayProperty, value); }
    public bool ShowFrametimeGraph { get => GetValue(ShowFrametimeGraphProperty); set => SetValue(ShowFrametimeGraphProperty, value); }
    public bool ShowDetailedGpuInfo { get => GetValue(ShowDetailedGpuInfoProperty); set => SetValue(ShowDetailedGpuInfoProperty, value); }
    public double OverlayOpacity { get => GetValue(OverlayOpacityProperty); set => SetValue(OverlayOpacityProperty, value); }
    public string BackendName { get => GetValue(BackendNameProperty); private set => SetValue(BackendNameProperty, value); }
    public string GpuRenderer { get => GetValue(GpuRendererProperty); private set => SetValue(GpuRendererProperty, value); }
    public string GpuVendor { get => GetValue(GpuVendorProperty); private set => SetValue(GpuVendorProperty, value); }
    public Geometry? FrametimeGraphGeometry { get => GetValue(FrametimeGraphGeometryProperty); private set => SetValue(FrametimeGraphGeometryProperty, value); }
    public double FrametimeGraphWidth { get => GetValue(FrametimeGraphWidthProperty); private set => SetValue(FrametimeGraphWidthProperty, value); }
    public int FrameNumber { get => GetValue(FrameNumberProperty); private set => SetValue(FrameNumberProperty, value); }

    public double Fps { get => GetValue(FpsProperty); private set => SetValue(FpsProperty, value); }
    public double FrameTimeMs { get => GetValue(FrameTimeMsProperty); private set => SetValue(FrameTimeMsProperty, value); }
    public int LastFrameWidth { get => GetValue(LastFrameWidthProperty); private set => SetValue(LastFrameWidthProperty, value); }
    public int LastFrameHeight { get => GetValue(LastFrameHeightProperty); private set => SetValue(LastFrameHeightProperty, value); }
    #endregion
}

