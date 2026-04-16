#define GL_GLEXT_PROTOTYPES
#define GLX_GLXEXT_PROTOTYPES

#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/Xatom.h>
#include <X11/extensions/Xcomposite.h>
#include <X11/extensions/Xdamage.h>
#include <X11/extensions/Xfixes.h>
#include <X11/extensions/shape.h>

#include <GL/gl.h>
#include <GL/glext.h>
#include <GL/glx.h>
#include <GL/glxext.h>

#include <EGL/egl.h>
#include <EGL/eglext.h>

#include <pipewire/pipewire.h>
#include <spa/param/video/format-utils.h>
#include <spa/debug/types.h>
#include <spa/utils/result.h>
#include <dbus/dbus.h>

#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <poll.h>
#include <fcntl.h>
#include <unistd.h>
#include <stdarg.h>
#include <cmath>

#include <algorithm>

#ifndef GLX_TEXTURE_FORMAT_EXT
#define GLX_TEXTURE_FORMAT_EXT 0x20D5
#endif
#ifndef GLX_TEXTURE_FORMAT_RGBA_EXT
#define GLX_TEXTURE_FORMAT_RGBA_EXT 0x20D9
#endif
#ifndef GLX_TEXTURE_FORMAT_RGB_EXT
#define GLX_TEXTURE_FORMAT_RGB_EXT 0x20D8
#endif
#ifndef GLX_TEXTURE_TARGET_EXT
#define GLX_TEXTURE_TARGET_EXT 0x20D6
#endif
#ifndef GLX_TEXTURE_2D_EXT
#define GLX_TEXTURE_2D_EXT 0x20DC
#endif
#ifndef GLX_BIND_TO_TEXTURE_RGB_EXT
#define GLX_BIND_TO_TEXTURE_RGB_EXT 0x20D0
#endif
#ifndef GLX_BIND_TO_TEXTURE_RGBA_EXT
#define GLX_BIND_TO_TEXTURE_RGBA_EXT 0x20D1
#endif
#ifndef GLX_BIND_TO_TEXTURE_TARGETS_EXT
#define GLX_BIND_TO_TEXTURE_TARGETS_EXT 0x20D3
#endif
#ifndef GLX_TEXTURE_2D_BIT_EXT
#define GLX_TEXTURE_2D_BIT_EXT 0x00000002
#endif

enum LinuxCaptureBackendMode
{
    BackendNone           = 0,
    BackendGpuComposite   = 1,
    BackendReparentFallback = 2,
    BackendPipeWire       = 3   // DMA-BUF frames via PipeWire xdg-desktop-portal
};

typedef struct
{
    Display* display;
    int screen;
    Window window;
    Colormap colormap;

    Window target;
    int active;
    int initializing;
    int use_pipewire;

    float brightness;
    float saturation;
    float tint[4];
    int stretch;
    int crop[4];
    int hide_target;
    int target_hidden_offscreen;
    Window hidden_window;
    unsigned long target_saved_opacity;
    int target_saved_opacity_valid;
    int target_saved_x;
    int target_saved_y;
    unsigned int target_saved_w;
    unsigned int target_saved_h;
    int target_saved_geometry_valid;
    Window proxy_window;
    int last_proxy_x;
    int last_proxy_y;
    int last_proxy_w;
    int last_proxy_h;
    int target_skip_taskbar_applied;
    int target_input_passthrough_applied;
    int disable_vsync;

    int damage_event_base;
    int damage_error_base;
    Damage damage;

    double fps;
    double frame_time_ms;
    double present_fps;
    double present_frame_time_ms;
    double source_fps;
    double source_frame_time_ms;
    uint64_t last_sample_time_ns;

    char status_text[512];
    char gpu_renderer[256];
    char gpu_vendor[256];

    char shader_path[1024];

    int last_target_x;
    int last_target_y;
    unsigned int last_target_w;
    unsigned int last_target_h;
    int has_target_geometry;
    int host_geometry_dirty;
    int target_geometry_dirty;
    int target_viewable;
    int cached_host_w;
    int cached_host_h;
    int cached_target_w;
    int cached_target_h;

    int backend_mode;
    char backend_detail[256];

    int gl_supported;
    GLXFBConfig fb_config;
    GLXContext glx_context;
    Pixmap composite_pixmap;
    GLXPixmap glx_pixmap;
    GLuint gl_texture;
    GLuint shader_program;
    int shader_dirty;
    GLint shader_u_tex;
    GLint shader_u_brightness;
    GLint shader_u_saturation;
    GLint shader_u_tint;
    GLint shader_u_source_size;
    GLint shader_u_output_size;
    int texture_params_initialized;

    PFNGLXBINDTEXIMAGEEXTPROC glx_bind_tex_image_ext;
    PFNGLXRELEASETEXIMAGEEXTPROC glx_release_tex_image_ext;
    PFNGLXSWAPINTERVALEXTPROC glx_swap_interval_ext;
    PFNGLXSWAPINTERVALMESAPROC glx_swap_interval_mesa;
    PFNGLXSWAPINTERVALSGIPROC glx_swap_interval_sgi;
    int has_swap_control;
    int has_swap_control_tear;
    int applied_disable_vsync;

    pthread_t render_thread;
    int render_thread_started;
    int stop_render_thread;
    pthread_mutex_t mutex;

    int context_is_current;  // non-zero if the GLX context is already current on the render thread

    // ――― PipeWire / DMA-BUF backend ―――
    struct pw_thread_loop*   pw_loop;
    struct pw_stream*        pw_stream;
    struct spa_hook          pw_stream_hook;
    uint32_t                 pw_node_id;
    int                      pw_fd;          // fd from OpenPipeWireRemote
    int                      pw_active;
    int                      pw_width;
    int                      pw_height;
    uint32_t                 pw_spa_format;  // negotiated SPA video format
    uint32_t                 pw_modifier;    // DRM modifier (0 = linear)
    pthread_mutex_t          pw_frame_mutex;
    pthread_cond_t           pw_frame_cond;
    struct pw_buffer*        pw_pending_buf; // set by PW thread, consumed by render thread
    int                      pw_frame_ready; // 1 when pw_pending_buf is valid
    int                      pw_init_requested; // set by set_target, cleared by render thread

    // ――― EGL context (used by PipeWire backend for DMA-BUF import) ―――
    EGLDisplay               egl_display;
    EGLContext               egl_context;
    EGLSurface               egl_surface;
    GLuint                   egl_texture;    // recycled per-frame
    PFNEGLCREATEIMAGEKHRPROC             egl_create_image;
    PFNEGLDESTROYIMAGEKHRPROC            egl_destroy_image;
    PFNGLEGLIMAGETARGETTEXTURE2DOESPROC  egl_image_target_tex;
    int                      egl_initialized;

    uint64_t fps_window_start_ns;
    int fps_window_frames;
    uint64_t source_last_event_ns;
    uint64_t last_present_sample_ns;
    uint64_t last_render_ns;
    int gpu_frame_pending;
} LinuxCapture;

extern "C" {

static pthread_once_t g_x11_threads_once = PTHREAD_ONCE_INIT;
static char g_pw_restore_token[256] = {0};
static int g_pw_token_loaded = 0;

static void LoadRestoreToken() {
    if (g_pw_token_loaded) return;
    const char* home = getenv("HOME");
    if (!home) return;
    char path[512];
    snprintf(path, sizeof(path), "%s/.aes_lacrima_pw_token", home);
    FILE* f = fopen(path, "r");
    if (f) {
        if (fgets(g_pw_restore_token, sizeof(g_pw_restore_token), f)) {
            size_t len = strlen(g_pw_restore_token);
            if (len > 0 && g_pw_restore_token[len-1] == '\n') g_pw_restore_token[len-1] = '\0';
        }
        fclose(f);
    }
    g_pw_token_loaded = 1;
}

static void SaveRestoreToken() {
    const char* home = getenv("HOME");
    if (!home) return;
    char path[512];
    snprintf(path, sizeof(path), "%s/.aes_lacrima_pw_token", home);
    FILE* f = fopen(path, "w");
    if (f) {
        fputs(g_pw_restore_token, f);
        fclose(f);
    }
}

static void InitX11ThreadsOnce()
{
    XInitThreads();
}

static void LogNative(const char* fmt, ...)
{
    if (!fmt)
        return;

    char message[1024];
    va_list args;
    va_start(args, fmt);
    vsnprintf(message, sizeof(message), fmt, args);
    va_end(args);

    timespec ts{};
    clock_gettime(CLOCK_REALTIME, &ts);
    tm localTm{};
    localtime_r(&ts.tv_sec, &localTm);

    char stamp[64];
    strftime(stamp, sizeof(stamp), "%Y-%m-%d %H:%M:%S", &localTm);

    FILE* f = fopen("/tmp/aes_linux_capture_bridge.log", "a");
    if (!f)
        return;

    fprintf(f, "[%s.%03ld] %s\n", stamp, ts.tv_nsec / 1000000L, message);
    fclose(f);
}

static uint64_t MonotonicNowNs()
{
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<uint64_t>(ts.tv_sec) * 1000000000ULL + static_cast<uint64_t>(ts.tv_nsec);
}

static double StabilizeReportedFps(double fps)
{
    if (fps <= 0.0)
        return 0.0;

    const double candidates[] = { 30.0, 60.0, 120.0 };
    for (double candidate : candidates)
    {
        // Snap to common emulator rates when very close.
        if (fabs(fps - candidate) <= candidate * 0.04)
            return candidate;
    }

    return fps;
}

static void SamplePresentMetrics(LinuxCapture* cap, uint64_t now)
{
    if (!cap || now == 0)
        return;

    if (cap->last_present_sample_ns != 0 && now > cap->last_present_sample_ns)
    {
        const double dt = static_cast<double>(now - cap->last_present_sample_ns) / 1000000000.0;
        if (dt > 0.0)
        {
            const double instantFps = std::clamp(1.0 / dt, 1.0, 240.0);
            const double alpha = dt >= 0.025 ? 0.22 : 0.14;
            cap->present_fps = cap->present_fps > 0.0
                ? (cap->present_fps * (1.0 - alpha)) + (instantFps * alpha)
                : instantFps;
            cap->present_frame_time_ms = cap->present_fps > 0.0 ? 1000.0 / cap->present_fps : 0.0;
        }
    }

    cap->last_present_sample_ns = now;
    cap->fps = cap->present_fps;
    cap->frame_time_ms = cap->present_frame_time_ms;
}

static void SetStatusText(LinuxCapture* cap, const char* text)
{
    if (!cap)
        return;

    if (!text)
        text = "";

    strncpy(cap->status_text, text, sizeof(cap->status_text) - 1);
    cap->status_text[sizeof(cap->status_text) - 1] = '\0';
}

static void SetGpuInfo(LinuxCapture* cap, const char* renderer, const char* vendor)
{
    if (!cap)
        return;

    strncpy(cap->gpu_renderer, renderer ? renderer : "Unknown", sizeof(cap->gpu_renderer) - 1);
    cap->gpu_renderer[sizeof(cap->gpu_renderer) - 1] = '\0';
    strncpy(cap->gpu_vendor, vendor ? vendor : "Unknown", sizeof(cap->gpu_vendor) - 1);
    cap->gpu_vendor[sizeof(cap->gpu_vendor) - 1] = '\0';
}

static void SetBackendDetail(LinuxCapture* cap, const char* detail)
{
    if (!cap)
        return;

    if (!detail)
        detail = "";

    strncpy(cap->backend_detail, detail, sizeof(cap->backend_detail) - 1);
    cap->backend_detail[sizeof(cap->backend_detail) - 1] = '\0';
    LogNative("backend detail: %s", cap->backend_detail);
}

static Window GetParentWindow(Display* display, void* parentHandle)
{
    if (!display || parentHandle == nullptr)
        return RootWindow(display, DefaultScreen(display));

    return reinterpret_cast<Window>(parentHandle);
}

static Window GetTopLevelWindow(Display* display, Window window)
{
    if (!display || window == 0)
        return window;

    Window root = DefaultRootWindow(display);
    Window current = window;

    for (int depth = 0; depth < 64; depth++)
    {
        Window rootReturn = 0;
        Window parentReturn = 0;
        Window* children = nullptr;
        unsigned int childCount = 0;

        if (XQueryTree(display, current, &rootReturn, &parentReturn, &children, &childCount) == 0)
            break;

        if (children)
            XFree(children);

        if (parentReturn == 0 || parentReturn == root || parentReturn == current)
            break;

        current = parentReturn;
    }

    return current;
}

static bool TryGetWindowOpacity(Display* display, Window window, unsigned long* outOpacity)
{
    if (!display || window == 0 || !outOpacity)
        return false;

    Atom opacityAtom = XInternAtom(display, "_NET_WM_WINDOW_OPACITY", True);
    if (opacityAtom == None)
        return false;

    Atom actualType = None;
    int actualFormat = 0;
    unsigned long nitems = 0;
    unsigned long bytesAfter = 0;
    unsigned char* prop = nullptr;

    if (XGetWindowProperty(
            display,
            window,
            opacityAtom,
            0,
            1,
            False,
            XA_CARDINAL,
            &actualType,
            &actualFormat,
            &nitems,
            &bytesAfter,
            &prop) != Success)
    {
        return false;
    }

    if (!prop || nitems < 1)
    {
        if (prop)
            XFree(prop);
        return false;
    }

    *outOpacity = *reinterpret_cast<unsigned long*>(prop);
    XFree(prop);
    return true;
}

static bool SetWindowOpacity(Display* display, Window window, unsigned long opacity)
{
    if (!display || window == 0)
        return false;

    Atom opacityAtom = XInternAtom(display, "_NET_WM_WINDOW_OPACITY", False);
    if (opacityAtom == None)
        return false;

    XChangeProperty(
        display,
        window,
        opacityAtom,
        XA_CARDINAL,
        32,
        PropModeReplace,
        reinterpret_cast<unsigned char*>(&opacity),
        1);
    XFlush(display);
    return true;
}

static void ClearWindowOpacity(Display* display, Window window)
{
    if (!display || window == 0)
        return;

    Atom opacityAtom = XInternAtom(display, "_NET_WM_WINDOW_OPACITY", True);
    if (opacityAtom == None)
        return;

    XDeleteProperty(display, window, opacityAtom);
    XFlush(display);
}

static bool SetWindowInputPassthrough(Display* display, Window window, bool passthrough)
{
    if (!display || window == 0)
        return false;

    int eventBase = 0;
    int errorBase = 0;
    if (!XFixesQueryExtension(display, &eventBase, &errorBase))
        return false;

    XserverRegion region = None;
    if (passthrough)
    {
        region = XFixesCreateRegion(display, nullptr, 0);
    }
    else
    {
        XWindowAttributes attr{};
        if (XGetWindowAttributes(display, window, &attr) == 0)
            return false;

        XRectangle rect{};
        rect.x = 0;
        rect.y = 0;
        rect.width = static_cast<unsigned short>(std::max(1, attr.width));
        rect.height = static_cast<unsigned short>(std::max(1, attr.height));
        region = XFixesCreateRegion(display, &rect, 1);
    }

    if (region == None)
        return false;

    XFixesSetWindowShapeRegion(display, window, ShapeInput, 0, 0, region);
    XFixesDestroyRegion(display, region);
    XFlush(display);
    return true;
}

static void SetWindowSkipTaskbar(Display* display, Window window, bool skip)
{
    if (!display || window == 0)
        return;

    Atom netWmState = XInternAtom(display, "_NET_WM_STATE", False);
    Atom skipTaskbar = XInternAtom(display, "_NET_WM_STATE_SKIP_TASKBAR", False);
    Atom skipPager = XInternAtom(display, "_NET_WM_STATE_SKIP_PAGER", False);
    if (netWmState == None || skipTaskbar == None || skipPager == None)
        return;

    XEvent ev{};
    ev.xclient.type = ClientMessage;
    ev.xclient.window = window;
    ev.xclient.message_type = netWmState;
    ev.xclient.format = 32;
    ev.xclient.data.l[0] = skip ? 1 : 0; // _NET_WM_STATE_ADD / REMOVE
    ev.xclient.data.l[1] = static_cast<long>(skipTaskbar);
    ev.xclient.data.l[2] = static_cast<long>(skipPager);
    ev.xclient.data.l[3] = 1;
    ev.xclient.data.l[4] = 0;

    Window root = DefaultRootWindow(display);
    XSendEvent(display, root, False, SubstructureRedirectMask | SubstructureNotifyMask, &ev);
    XFlush(display);
}

static bool GetWindowTitle(Display* display, Window window, char* buffer, int size)
{
    if (!display || !buffer || size <= 0)
        return false;

    Atom atoms[] = { XInternAtom(display, "_NET_WM_NAME", True), XA_WM_NAME };
    for (int i = 0; i < 2; i++)
    {
        Atom atom = atoms[i];
        if (atom == None)
            continue;

        XTextProperty prop;
        if (XGetTextProperty(display, window, &prop, atom) && prop.value)
        {
            bool success = false;

            if (prop.encoding == XA_STRING)
            {
                strncpy(buffer, reinterpret_cast<char*>(prop.value), size - 1);
                buffer[size - 1] = '\0';
                success = true;
            }
            else
            {
                char** list = nullptr;
                int count = 0;
                if (XmbTextPropertyToTextList(display, &prop, &list, &count) >= Success && list && count > 0 && list[0])
                {
                    strncpy(buffer, list[0], size - 1);
                    buffer[size - 1] = '\0';
                    XFreeStringList(list);
                    success = true;
                }
            }

            XFree(prop.value);
            if (success)
                return true;
        }
    }

    buffer[0] = '\0';
    return false;
}

static pid_t GetWindowPid(Display* display, Window window)
{
    Atom pidAtom = XInternAtom(display, "_NET_WM_PID", True);
    if (pidAtom == None)
        return 0;

    Atom actualType;
    int actualFormat;
    unsigned long nitems, bytesAfter;
    unsigned char* prop = nullptr;

    if (XGetWindowProperty(display, window, pidAtom, 0, 1, False, XA_CARDINAL,
                           &actualType, &actualFormat, &nitems, &bytesAfter, &prop) == Success && prop)
    {
        uint32_t pidValue = *reinterpret_cast<uint32_t*>(prop);
        XFree(prop);
        return static_cast<pid_t>(pidValue);
    }

    return 0;
}

static bool TitleMatches(const char* windowTitle, const char* titleHint)
{
    if (!titleHint || titleHint[0] == '\0')
        return true;
    if (!windowTitle)
        return false;
    return strstr(windowTitle, titleHint) != nullptr;
}

static Window FindWindowByTitle(Display* display, Window root, const char* titleHint)
{
    Window rootReturn;
    Window parentReturn;
    Window* children = nullptr;
    unsigned int childCount = 0;

    if (!display)
        return 0;

    if (XQueryTree(display, root, &rootReturn, &parentReturn, &children, &childCount) == 0)
        return 0;

    for (unsigned int i = 0; i < childCount; i++)
    {
        Window child = children[i];
        char title[512] = {0};
        GetWindowTitle(display, child, title, sizeof(title));
        if (TitleMatches(title, titleHint))
        {
            if (children)
                XFree(children);
            return child;
        }

        Window found = FindWindowByTitle(display, child, titleHint);
        if (found != 0)
        {
            if (children)
                XFree(children);
            return found;
        }
    }

    if (children)
        XFree(children);

    return 0;
}

static void GetWindowExtents(Display* display, Window window, int* outLeft, int* outRight, int* outTop, int* outBottom)
{
    *outLeft = 0; *outRight = 0; *outTop = 0; *outBottom = 0;
    if (!display || !window) return;

    Atom netFrameExtents = XInternAtom(display, "_NET_FRAME_EXTENTS", True);
    if (netFrameExtents == None) return;

    Atom actualType;
    int actualFormat;
    unsigned long nItems, bytesAfter;
    unsigned char* propData = nullptr;

    if (XGetWindowProperty(display, window, netFrameExtents, 0, 4, False,
                           XA_CARDINAL, &actualType, &actualFormat,
                           &nItems, &bytesAfter, &propData) == Success)
    {
        if (actualType == XA_CARDINAL && actualFormat == 32 && nItems >= 4 && propData)
        {
            long* extents = reinterpret_cast<long*>(propData);
            *outLeft   = extents[0];
            *outRight  = extents[1];
            *outTop    = extents[2];
            *outBottom = extents[3];
        }
        if (propData)
            XFree(propData);
    }
}

static Window FindWindowByPid(Display* display, Window root, pid_t pid, const char* titleHint, bool requireTitleMatch)
{
    if (pid <= 0)
        return 0;

    Window rootReturn;
    Window parentReturn;
    Window* children = nullptr;
    unsigned int childCount = 0;

    if (!display)
        return 0;

    if (XQueryTree(display, root, &rootReturn, &parentReturn, &children, &childCount) == 0)
        return 0;

    for (unsigned int i = 0; i < childCount; i++)
    {
        Window child = children[i];
        pid_t childPid = GetWindowPid(display, child);
        if (childPid == pid)
        {
            if (!requireTitleMatch)
            {
                if (children)
                    XFree(children);
                return child;
            }

            char title[512] = {0};
            GetWindowTitle(display, child, title, sizeof(title));
            if (TitleMatches(title, titleHint))
            {
                if (children)
                    XFree(children);
                return child;
            }
        }

        Window found = FindWindowByPid(display, child, pid, titleHint, requireTitleMatch);
        if (found != 0)
        {
            if (children)
                XFree(children);
            return found;
        }
    }

    if (children)
        XFree(children);

    return 0;
}

static bool LoadTextFile(const char* path, char** outText)
{
    if (!outText)
        return false;

    *outText = nullptr;
    if (!path || path[0] == '\0')
        return false;

    FILE* f = fopen(path, "rb");
    if (!f)
        return false;

    if (fseek(f, 0, SEEK_END) != 0)
    {
        fclose(f);
        return false;
    }

    long len = ftell(f);
    if (len <= 0)
    {
        fclose(f);
        return false;
    }

    rewind(f);
    char* buf = static_cast<char*>(malloc(static_cast<size_t>(len) + 1));
    if (!buf)
    {
        fclose(f);
        return false;
    }

    size_t read = fread(buf, 1, static_cast<size_t>(len), f);
    fclose(f);
    buf[read] = '\0';

    *outText = buf;
    return true;
}

static GLuint CompileShader(GLenum type, const char* source)
{
    GLuint shader = glCreateShader(type);
    if (!shader)
        return 0;

    glShaderSource(shader, 1, &source, nullptr);
    glCompileShader(shader);

    GLint ok = GL_FALSE;
    glGetShaderiv(shader, GL_COMPILE_STATUS, &ok);
    if (ok == GL_TRUE)
        return shader;

    glDeleteShader(shader);
    return 0;
}

static GLuint BuildShaderProgram(const char* fragmentSource)
{
    const char* vertexSource =
        "#version 120\n"
        "varying vec2 vTex;\n"
        "void main(){\n"
        "  gl_Position = gl_Vertex;\n"
        "  vTex = gl_MultiTexCoord0.xy;\n"
        "}\n";

    const char* defaultFragment =
        "#version 120\n"
        "uniform sampler2D uTex;\n"
        "uniform float uBrightness;\n"
        "uniform float uSaturation;\n"
        "uniform vec4 uTint;\n"
        "uniform vec2 uSourceSize;\n"
        "uniform vec2 uOutputSize;\n"
        "varying vec2 vTex;\n"
        "void main(){\n"
        "  vec4 c = texture2D(uTex, vTex);\n"
        "  c.rgb *= uBrightness;\n"
        "  float gray = dot(c.rgb, vec3(0.299, 0.587, 0.114));\n"
        "  c.rgb = mix(vec3(gray), c.rgb, uSaturation);\n"
        "  c *= uTint;\n"
        "  gl_FragColor = c;\n"
        "}\n";

    const char* frag = fragmentSource && fragmentSource[0] != '\0' ? fragmentSource : defaultFragment;

    GLuint vs = CompileShader(GL_VERTEX_SHADER, vertexSource);
    if (!vs)
        return 0;

    GLuint fs = CompileShader(GL_FRAGMENT_SHADER, frag);
    if (!fs)
    {
        glDeleteShader(vs);
        if (frag != defaultFragment)
            fs = CompileShader(GL_FRAGMENT_SHADER, defaultFragment);

        if (!fs)
            return 0;

        vs = CompileShader(GL_VERTEX_SHADER, vertexSource);
        if (!vs)
        {
            glDeleteShader(fs);
            return 0;
        }
    }

    GLuint program = glCreateProgram();
    if (!program)
    {
        glDeleteShader(vs);
        glDeleteShader(fs);
        return 0;
    }

    glAttachShader(program, vs);
    glAttachShader(program, fs);
    glLinkProgram(program);

    GLint linked = GL_FALSE;
    glGetProgramiv(program, GL_LINK_STATUS, &linked);

    glDeleteShader(vs);
    glDeleteShader(fs);

    if (linked != GL_TRUE)
    {
        glDeleteProgram(program);
        return 0;
    }

    return program;
}

static bool EnsureShaderProgram(LinuxCapture* cap)
{
    if (!cap)
        return false;

    if (!cap->shader_dirty && cap->shader_program != 0)
        return true;

    char* customFragment = nullptr;
    LoadTextFile(cap->shader_path, &customFragment);

    GLuint program = BuildShaderProgram(customFragment);
    if (customFragment)
        free(customFragment);

    if (!program)
    {
        SetStatusText(cap, "GPU shader compilation failed; using fallback pipeline");
        return false;
    }

    if (cap->shader_program)
        glDeleteProgram(cap->shader_program);

    cap->shader_program = program;
    cap->shader_u_tex = glGetUniformLocation(program, "uTex");
    cap->shader_u_brightness = glGetUniformLocation(program, "uBrightness");
    cap->shader_u_saturation = glGetUniformLocation(program, "uSaturation");
    cap->shader_u_tint = glGetUniformLocation(program, "uTint");
    cap->shader_u_source_size = glGetUniformLocation(program, "uSourceSize");
    cap->shader_u_output_size = glGetUniformLocation(program, "uOutputSize");
    cap->shader_dirty = 0;
    return true;
}

static void DestroyCompositeResources(LinuxCapture* cap)
{
    if (!cap || !cap->display)
        return;

    // Do NOT release the GLX context here — the render thread keeps it current
    // for its entire lifetime to avoid the expensive re-bind cost on every
    // target change. The context is released only when the thread exits or when
    // CleanupGlObjects destroys it.

    if (cap->glx_pixmap)
    {
        glXDestroyPixmap(cap->display, cap->glx_pixmap);
        cap->glx_pixmap = 0;
    }

    if (cap->composite_pixmap)
    {
        XFreePixmap(cap->display, cap->composite_pixmap);
        cap->composite_pixmap = 0;
    }

    if (cap->target != 0)
    {
        XCompositeUnredirectWindow(cap->display, cap->target, CompositeRedirectAutomatic);
    }
}

static void HideTargetOffscreenIfRequested(LinuxCapture* cap)
{
    if (!cap || !cap->display || cap->target == 0 || !cap->hide_target || cap->target_hidden_offscreen)
        return;

    Window hideWindow = GetTopLevelWindow(cap->display, cap->target);
    if (hideWindow == 0)
        hideWindow = cap->target;

    XWindowAttributes attr{};
    if (XGetWindowAttributes(cap->display, hideWindow, &attr) == 0)
        return;

    cap->hidden_window = hideWindow;
    cap->target_saved_x = attr.x;
    cap->target_saved_y = attr.y;
    cap->target_saved_w = static_cast<unsigned int>(std::max(1, attr.width));
    cap->target_saved_h = static_cast<unsigned int>(std::max(1, attr.height));
    cap->target_saved_geometry_valid = 1;
    cap->target_saved_opacity_valid = TryGetWindowOpacity(cap->display, hideWindow, &cap->target_saved_opacity) ? 1 : 0;

    if (cap->backend_mode == BackendPipeWire || cap->use_pipewire)
    {
        if (cap->proxy_window) {
            Status status = XReparentWindow(cap->display, hideWindow, cap->proxy_window, 0, 0);
            LogNative("target 0x%lx reparented to proxy_window 0x%lx, status=%d", hideWindow, cap->proxy_window, status);
            XMapWindow(cap->display, hideWindow);
            XLowerWindow(cap->display, hideWindow);
            
            // Immediately fill the proxy surface using our clean last known dimensions
            int targetW = cap->last_proxy_w > 0 ? cap->last_proxy_w : 320;
            int targetH = cap->last_proxy_h > 0 ? cap->last_proxy_h : 240;
            XMoveResizeWindow(cap->display, hideWindow, 0, 0, targetW, targetH);
        } else {
            XLowerWindow(cap->display, hideWindow);
            LogNative("target 0x%lx lowered (no proxy_window)", hideWindow);
        }
        cap->target_hidden_offscreen = 1;
        return;
    }

    const unsigned long fullyTransparent = 0x00000000UL;
    if (!SetWindowOpacity(cap->display, hideWindow, fullyTransparent))
    {
        LogNative("opacity hide unsupported for window 0x%lx; leaving window visible", hideWindow);
        return;
    }

    SetWindowSkipTaskbar(cap->display, hideWindow, true);
    cap->target_skip_taskbar_applied = 1;
    if (SetWindowInputPassthrough(cap->display, hideWindow, true))
    {
        cap->target_input_passthrough_applied = 1;
        LogNative("target 0x%lx hidden window input passthrough enabled", cap->target);
    }
    cap->target_hidden_offscreen = 1;
    LogNative("target 0x%lx hidden via top-level 0x%lx opacity", cap->target, hideWindow);
}

static void RestoreTargetFromOffscreenIfNeeded(LinuxCapture* cap)
{
    if (!cap || !cap->display || !cap->target_hidden_offscreen)
        return;

    Window restoreWindow = cap->hidden_window;
    if (restoreWindow == 0 && cap->target != 0)
        restoreWindow = GetTopLevelWindow(cap->display, cap->target);
    if (restoreWindow == 0)
    {
        cap->target_hidden_offscreen = 0;
        cap->target_saved_geometry_valid = 0;
        return;
    }

    if (cap->backend_mode == BackendPipeWire || cap->use_pipewire)
    {
        if (cap->proxy_window) {
            Window root = DefaultRootWindow(cap->display);
            XReparentWindow(cap->display, restoreWindow, root, cap->target_saved_x, cap->target_saved_y);
        }
    }

    if (cap->target_saved_geometry_valid)
    {
        XMoveResizeWindow(
            cap->display,
            restoreWindow,
            cap->target_saved_x,
            cap->target_saved_y,
            cap->target_saved_w,
            cap->target_saved_h);
        XRaiseWindow(cap->display, restoreWindow);
    }
    else
    {
        XMoveWindow(cap->display, restoreWindow, 0, 0);
        XRaiseWindow(cap->display, restoreWindow);
    }

    if (cap->target_saved_opacity_valid)
        SetWindowOpacity(cap->display, restoreWindow, cap->target_saved_opacity);
    else
        ClearWindowOpacity(cap->display, restoreWindow);

    if (cap->target_skip_taskbar_applied)
        SetWindowSkipTaskbar(cap->display, restoreWindow, false);
    if (cap->target_input_passthrough_applied)
        SetWindowInputPassthrough(cap->display, restoreWindow, false);

    XFlush(cap->display);
    cap->target_hidden_offscreen = 0;
    cap->target_saved_geometry_valid = 0;
    cap->target_saved_opacity_valid = 0;
    cap->target_skip_taskbar_applied = 0;
    cap->target_input_passthrough_applied = 0;
    LogNative("target restore via top-level 0x%lx (opacity restored)", restoreWindow);
    cap->hidden_window = 0;
}

static void UpdateFallbackTargetGeometry(LinuxCapture* cap)
{
    if (!cap || !cap->display || cap->window == 0 || cap->target == 0)
        return;

    XWindowAttributes attributes;
    if (XGetWindowAttributes(cap->display, cap->window, &attributes) == 0)
        return;

    const int hostWidth = attributes.width > 0 ? attributes.width : 1;
    const int hostHeight = attributes.height > 0 ? attributes.height : 1;
    const int left = cap->crop[0] > 0 ? cap->crop[0] : 0;
    const int top = cap->crop[1] > 0 ? cap->crop[1] : 0;
    const int right = cap->crop[2] > 0 ? cap->crop[2] : 0;
    const int bottom = cap->crop[3] > 0 ? cap->crop[3] : 0;

    const unsigned int width = static_cast<unsigned int>(hostWidth + left + right);
    const unsigned int height = static_cast<unsigned int>(hostHeight + top + bottom);
    const int x = -left;
    const int y = -top;

    if (cap->has_target_geometry &&
        cap->last_target_x == x &&
        cap->last_target_y == y &&
        cap->last_target_w == width &&
        cap->last_target_h == height)
    {
        return;
    }

    XMoveResizeWindow(cap->display, cap->target, x, y, width, height);
    XFlush(cap->display);

    cap->last_target_x = x;
    cap->last_target_y = y;
    cap->last_target_w = width;
    cap->last_target_h = height;
    cap->has_target_geometry = 1;
}

static bool SetupCompositeTarget(LinuxCapture* cap, Window target)
{
    if (!cap || !cap->display || !cap->gl_supported || !cap->glx_context)
    {
        SetBackendDetail(cap, "OpenGL composite unavailable on this session");
        return false;
    }

    DestroyCompositeResources(cap);

    XCompositeRedirectWindow(cap->display, target, CompositeRedirectAutomatic);
    XSync(cap->display, False);
    XSelectInput(cap->display, target, StructureNotifyMask);

    cap->composite_pixmap = XCompositeNameWindowPixmap(cap->display, target);
    if (cap->composite_pixmap == 0)
    {
        SetBackendDetail(cap, "XCompositeNameWindowPixmap failed");
        return false;
    }

    int attrsRgba[] = {
        GLX_TEXTURE_TARGET_EXT, GLX_TEXTURE_2D_EXT,
        GLX_TEXTURE_FORMAT_EXT, GLX_TEXTURE_FORMAT_RGBA_EXT,
        None
    };
    int attrsRgb[] = {
        GLX_TEXTURE_TARGET_EXT, GLX_TEXTURE_2D_EXT,
        GLX_TEXTURE_FORMAT_EXT, GLX_TEXTURE_FORMAT_RGB_EXT,
        None
    };

    cap->glx_pixmap = glXCreatePixmap(cap->display, cap->fb_config, cap->composite_pixmap, attrsRgba);
    if (cap->glx_pixmap == 0)
    {
        LogNative("glXCreatePixmap RGBA failed for target=0x%lx, retrying RGB", target);
        cap->glx_pixmap = glXCreatePixmap(cap->display, cap->fb_config, cap->composite_pixmap, attrsRgb);
    }

    if (cap->glx_pixmap == 0)
    {
        SetBackendDetail(cap, "glXCreatePixmap failed (RGBA/RGB unsupported by target visual)");
        DestroyCompositeResources(cap);
        return false;
    }

    cap->target = target;
    cap->backend_mode = BackendGpuComposite;
    SetBackendDetail(cap, "X11/XWayland GPU composite");

        cap->host_geometry_dirty = 1;
        cap->target_geometry_dirty = 1;
        cap->target_viewable = 1;
        cap->cached_host_w = 0;
        cap->cached_host_h = 0;
        cap->cached_target_w = 0;
        cap->cached_target_h = 0;

    SetGpuInfo(cap, "OpenGL (GLX) composite", "Linux/X11");
    SetStatusText(cap, "Capturing (X11/XWayland GPU composite)");
    LogNative("GPU composite active for target=0x%lx", target);

    return true;
}

static void SetSwapInterval(LinuxCapture* cap, int disableVsync)
{
    if (!cap || !cap->display)
        return;

    // Use adaptive VSync (interval -1) when the driver supports it
    // (GLX_EXT_swap_control_tear). This presents at the NEAREST VBI rather
    // than waiting for the one AFTER our submission, halving worst-case swap
    // latency and eliminating the phase-drift stutter that occurs when the
    // emulator's swap cycle and ours drift relative to each other.
    // When adaptive is unsupported, fall back to classic VSync (interval 1).
    int interval = disableVsync ? 0 : (cap->has_swap_control_tear ? -1 : 1);
    if (cap->applied_disable_vsync == disableVsync)
        return;

    if (cap->glx_swap_interval_ext)
    {
        cap->glx_swap_interval_ext(cap->display, cap->window, interval);
    }
    else if (cap->glx_swap_interval_mesa)
    {
        const int res = cap->glx_swap_interval_mesa(interval);
        if (res != 0 && interval < 0)
            cap->glx_swap_interval_mesa(1);
    }
    else if (cap->glx_swap_interval_sgi && interval > 0)
    {
        cap->glx_swap_interval_sgi(interval);
    }

    cap->applied_disable_vsync = disableVsync;
}

static void RenderCompositeFrame(LinuxCapture* cap)
{
    if (!cap || !cap->display || cap->backend_mode != BackendGpuComposite || cap->target == 0)
        return;

    if (!cap->glx_bind_tex_image_ext || !cap->glx_release_tex_image_ext)
        return;

    // The render thread keeps the context current for its entire lifetime;
    // only bind it here when it somehow isn't (e.g. first call before thread init).
    if (!cap->context_is_current)
    {
        if (glXMakeCurrent(cap->display, cap->window, cap->glx_context) != True)
            return;
        cap->context_is_current = 1;
    }

    if (!EnsureShaderProgram(cap))
        return;

    SetSwapInterval(cap, cap->disable_vsync);

    if (cap->host_geometry_dirty || cap->cached_host_w <= 0 || cap->cached_host_h <= 0)
    {
        XWindowAttributes hostAttr{};
        if (XGetWindowAttributes(cap->display, cap->window, &hostAttr) == 0)
            return;

        cap->cached_host_w = std::max(1, hostAttr.width);
        cap->cached_host_h = std::max(1, hostAttr.height);
        cap->host_geometry_dirty = 0;
    }

    if (cap->target_geometry_dirty || cap->cached_target_w <= 0 || cap->cached_target_h <= 0)
    {
        XWindowAttributes targetAttr{};
        if (XGetWindowAttributes(cap->display, cap->target, &targetAttr) == 0)
            return;

        cap->cached_target_w = std::max(1, targetAttr.width);
        cap->cached_target_h = std::max(1, targetAttr.height);
        cap->target_viewable = targetAttr.map_state == IsViewable ? 1 : 0;
        cap->target_geometry_dirty = 0;
    }

    if (!cap->target_viewable && cap->hide_target)
    {
        Window restoreWindow = cap->hidden_window ? cap->hidden_window : cap->target;
        XMapRaised(cap->display, restoreWindow);
        if (cap->target_hidden_offscreen)
        {
            const unsigned long fullyTransparent = 0x00000000UL;
            SetWindowOpacity(cap->display, restoreWindow, fullyTransparent);
            if (cap->target_skip_taskbar_applied)
                SetWindowSkipTaskbar(cap->display, restoreWindow, true);
        }
        XFlush(cap->display);
    }

    int hostW = std::max(1, cap->cached_host_w);
    int hostH = std::max(1, cap->cached_host_h);

    int srcW = std::max(1, cap->cached_target_w - cap->crop[0] - cap->crop[2]);
    int srcH = std::max(1, cap->cached_target_h - cap->crop[1] - cap->crop[3]);

    float u0 = static_cast<float>(std::max(0, cap->crop[0])) / static_cast<float>(std::max(1, cap->cached_target_w));
    float v0 = static_cast<float>(std::max(0, cap->crop[1])) / static_cast<float>(std::max(1, cap->cached_target_h));
    float u1 = 1.0f - static_cast<float>(std::max(0, cap->crop[2])) / static_cast<float>(std::max(1, cap->cached_target_w));
    float v1 = 1.0f - static_cast<float>(std::max(0, cap->crop[3])) / static_cast<float>(std::max(1, cap->cached_target_h));

    u0 = std::clamp(u0, 0.0f, 1.0f);
    v0 = std::clamp(v0, 0.0f, 1.0f);
    u1 = std::clamp(u1, 0.0f, 1.0f);
    v1 = std::clamp(v1, 0.0f, 1.0f);

    int vpX = 0;
    int vpY = 0;
    int vpW = hostW;
    int vpH = hostH;

    const double srcAspect = static_cast<double>(srcW) / static_cast<double>(srcH);
    const double dstAspect = static_cast<double>(hostW) / static_cast<double>(hostH);

    if (cap->stretch == 0) // None (pixel-size where possible, centered)
    {
        vpW = std::min(hostW, srcW);
        vpH = std::min(hostH, srcH);
        vpX = (hostW - vpW) / 2;
        vpY = (hostH - vpH) / 2;
    }
    else if (cap->stretch == 2) // Uniform
    {
        if (srcAspect > dstAspect)
        {
            vpW = hostW;
            vpH = std::max(1, static_cast<int>(hostW / srcAspect));
        }
        else
        {
            vpH = hostH;
            vpW = std::max(1, static_cast<int>(hostH * srcAspect));
        }
        vpX = (hostW - vpW) / 2;
        vpY = (hostH - vpH) / 2;
    }
    else if (cap->stretch == 3) // UniformToFill (center crop)
    {
        if (srcAspect > dstAspect)
        {
            const double desiredSrcW = static_cast<double>(srcH) * dstAspect;
            const double trim = std::max(0.0, (static_cast<double>(srcW) - desiredSrcW) * 0.5);
            const float du = static_cast<float>(trim / static_cast<double>(std::max(1, cap->cached_target_w)));
            u0 += du;
            u1 -= du;
        }
        else
        {
            const double desiredSrcH = static_cast<double>(srcW) / dstAspect;
            const double trim = std::max(0.0, (static_cast<double>(srcH) - desiredSrcH) * 0.5);
            const float dv = static_cast<float>(trim / static_cast<double>(std::max(1, cap->cached_target_h)));
            v0 += dv;
            v1 -= dv;
        }
    }

    u0 = std::clamp(u0, 0.0f, 1.0f);
    v0 = std::clamp(v0, 0.0f, 1.0f);
    u1 = std::clamp(u1, 0.0f, 1.0f);
    v1 = std::clamp(v1, 0.0f, 1.0f);

    glViewport(vpX, vpY, std::max(1, vpW), std::max(1, vpH));
    glDisable(GL_DEPTH_TEST);
    glClearColor(0.f, 0.f, 0.f, 1.f);
    glClear(GL_COLOR_BUFFER_BIT);

    glActiveTexture(GL_TEXTURE0);

    if (!cap->gl_texture)
    {
        glGenTextures(1, &cap->gl_texture);
        if (!cap->gl_texture)
        {
            GLenum err = glGetError();
            char detail[128];
            snprintf(detail, sizeof(detail), "OpenGL texture allocation failed (glError=0x%04x)", (unsigned int)err);
            SetBackendDetail(cap, detail);
            SetStatusText(cap, "Capturing degraded: GPU texture allocation failed");
            LogNative("%s", detail);
            return;
        }
    }

    glBindTexture(GL_TEXTURE_2D, cap->gl_texture);
    cap->glx_bind_tex_image_ext(cap->display, cap->glx_pixmap, GLX_FRONT_LEFT_EXT, nullptr);

    if (!cap->texture_params_initialized)
    {
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        cap->texture_params_initialized = 1;
    }

    glUseProgram(cap->shader_program);

    if (cap->shader_u_tex >= 0)
        glUniform1i(cap->shader_u_tex, 0);
    if (cap->shader_u_brightness >= 0)
        glUniform1f(cap->shader_u_brightness, cap->brightness);
    if (cap->shader_u_saturation >= 0)
        glUniform1f(cap->shader_u_saturation, cap->saturation);
    if (cap->shader_u_tint >= 0)
        glUniform4f(cap->shader_u_tint, cap->tint[0], cap->tint[1], cap->tint[2], cap->tint[3]);
    if (cap->shader_u_source_size >= 0)
        glUniform2f(cap->shader_u_source_size, static_cast<float>(srcW), static_cast<float>(srcH));
    if (cap->shader_u_output_size >= 0)
        glUniform2f(cap->shader_u_output_size, static_cast<float>(std::max(1, vpW)), static_cast<float>(std::max(1, vpH)));

    glBegin(GL_TRIANGLE_STRIP);
    glTexCoord2f(u0, v1); glVertex2f(-1.0f, -1.0f);
    glTexCoord2f(u1, v1); glVertex2f( 1.0f, -1.0f);
    glTexCoord2f(u0, v0); glVertex2f(-1.0f,  1.0f);
    glTexCoord2f(u1, v0); glVertex2f( 1.0f,  1.0f);
    glEnd();

    glUseProgram(0);

    // Flush all GL commands into the pipeline before releasing the texture,
    // so they are guaranteed to execute before the next swap.
    glFlush();

    cap->glx_release_tex_image_ext(cap->display, cap->glx_pixmap, GLX_FRONT_LEFT_EXT);

    // NOTE: glXSwapBuffers is intentionally NOT called here.
    // The render thread (RenderThreadMain) handles the swap outside the mutex
    // so the 0-16 ms VBI wait does not block main-thread API calls.
}

static void PumpXEventsLocked(LinuxCapture* cap)
{
    if (!cap || !cap->display)
        return;

    int processed = 0;
    constexpr int maxEventsPerTick = 512;
    while (XPending(cap->display) > 0 && processed < maxEventsPerTick)
    {
        XEvent ev{};
        XNextEvent(cap->display, &ev);
        processed++;

        if (cap->backend_mode == BackendReparentFallback &&
            ev.type == ConfigureNotify &&
            ev.xconfigure.window == cap->window)
        {
            UpdateFallbackTargetGeometry(cap);
            continue;
        }

        if (cap->backend_mode == BackendGpuComposite || cap->backend_mode == BackendPipeWire)
        {
            if (ev.type == ConfigureNotify && ev.xconfigure.window == cap->window)
                cap->host_geometry_dirty = 1;
            else if (ev.type == ConfigureNotify && ev.xconfigure.window == cap->target)
                cap->target_geometry_dirty = 1;
            else if (ev.type == MapNotify && ev.xmap.window == cap->target)
            {
                cap->target_viewable = 1;
                cap->target_geometry_dirty = 1;
            }
            else if (ev.type == UnmapNotify && ev.xunmap.window == cap->target)
            {
                cap->target_viewable = 0;
                cap->target_geometry_dirty = 1;
            }
        }

        if (cap->damage != 0 && cap->damage_event_base >= 0 && ev.type == cap->damage_event_base + XDamageNotify)
        {
            XDamageNotifyEvent* damageEv = reinterpret_cast<XDamageNotifyEvent*>(&ev);
            if (damageEv->damage != cap->damage)
                continue;

            XDamageSubtract(cap->display, cap->damage, None, None);

            const uint64_t nowEvent = MonotonicNowNs();
            if (cap->source_last_event_ns != 0 && nowEvent > cap->source_last_event_ns)
            {
                const uint64_t dtNs = nowEvent - cap->source_last_event_ns;

                // XDamage can emit duplicate notifies for a single source frame.
                // Ignore deltas that are implausibly short relative to current cadence.
                // Floor reduced to 4ms (250fps) and factor tightened to 0.40 to pass
                // real back-to-back frames at 120fps without spurious drops.
                double minAcceptedNs = 4000000.0; // 4 ms baseline duplicate filter
                if (cap->source_frame_time_ms > 0.0)
                {
                    const double expectedNs = cap->source_frame_time_ms * 1000000.0;
                    minAcceptedNs = std::max(4000000.0, expectedNs * 0.40);
                }

                if (static_cast<double>(dtNs) >= minAcceptedNs)
                {
                    const double dt = static_cast<double>(dtNs) / 1000000000.0;
                    const double instantFps = std::clamp(1.0 / dt, 1.0, 240.0);
                    const double alpha = dt >= 0.025 ? 0.22 : 0.14;
                    cap->source_fps = cap->source_fps > 0.0
                        ? (cap->source_fps * (1.0 - alpha)) + (instantFps * alpha)
                        : instantFps;
                    cap->source_frame_time_ms = cap->source_fps > 0.0 ? 1000.0 / cap->source_fps : 0.0;
                    cap->source_last_event_ns = nowEvent;
                }
            }
            else
            {
                cap->source_last_event_ns = nowEvent;
            }

            cap->gpu_frame_pending = 1;
        }
    }

    if (cap->proxy_window != 0 && cap->window != 0)
    {
        XWindowAttributes hostAttrs;
        if (XGetWindowAttributes(cap->display, cap->window, &hostAttrs))
        {
            // Sync mapped status (minimize/restore with app)
            if (hostAttrs.map_state == IsViewable) {
                XMapWindow(cap->display, cap->proxy_window);
            } else {
                XUnmapWindow(cap->display, cap->proxy_window);
            }

            Window child;
            int x = 0, y = 0;
            if (XTranslateCoordinates(cap->display, cap->window, DefaultRootWindow(cap->display), 0, 0, &x, &y, &child))
            {
                int w = hostAttrs.width;
                int h = hostAttrs.height;
                
                // Relaxed huge check: only clamp if dimensions are zero or ridiculously huge (> desktop + 4k)
                int screen_w = DisplayWidth(cap->display, cap->screen);
                int screen_h = DisplayHeight(cap->display, cap->screen);
                if (w <= 0 || h <= 0 || w > screen_w + 4096 || h > screen_h + 4096) {
                    w = 320; h = 240;
                }

                if (cap->last_proxy_x != x || cap->last_proxy_y != y || 
                    cap->last_proxy_w != w || cap->last_proxy_h != h)
                {
                    // "Bypass Limits" fix: If moving out of bounds (left/bottom), 
                    // some WMs require a staging move off-screen to allow it.
                    if (x < 0 || y + h > screen_h) {
                        XMoveResizeWindow(cap->display, cap->proxy_window, screen_w + 100, y, w, h);
                        XFlush(cap->display);
                    }

                    XSizeHints* sh = XAllocSizeHints();
                    if (sh) {
                        sh->flags = PPosition | PSize | PMinSize | PMaxSize;
                        sh->x = x; sh->y = y; sh->width = w; sh->height = h;
                        sh->min_width = w; sh->min_height = h;
                        sh->max_width = w; sh->max_height = h;
                        XSetWMNormalHints(cap->display, cap->proxy_window, sh);
                        XFree(sh);
                    }

                    XMoveResizeWindow(cap->display, cap->proxy_window, x, y, w, h);
                    XLowerWindow(cap->display, cap->proxy_window);
                    
                    XFlush(cap->display);
                    cap->last_proxy_x = x;
                    cap->last_proxy_y = y;
                    cap->last_proxy_w = w;
                    cap->last_proxy_h = h;
                }

                // FORCE embedded emulator to always match current proxy dimensions, 
                // even if proxy size hasn't changed (fixes huge window on game relaunch)
                if (cap->target_hidden_offscreen && cap->hidden_window && (cap->backend_mode == BackendPipeWire || cap->use_pipewire)) {
                    XMoveResizeWindow(cap->display, cap->hidden_window, 0, 0, w, h);
                }
            }
        }
    }
}

// Forward declarations for PipeWire / EGL / D-Bus functions defined later
static bool DbusAppendStringVariant(DBusMessageIter*, const char*, const char*);
static bool DbusAppendUint32Variant(DBusMessageIter*, const char*, dbus_uint32_t);
static int DbusWaitForResponse(DBusConnection*, const char*, uint32_t*);
static int DbusWaitForResponseWithString(DBusConnection*, const char*, const char*, char*, int, uint32_t*);
static int PortalOpenScreenCast(uint32_t*);
static bool InitEglContext(LinuxCapture*);
static void CleanupEglContext(LinuxCapture*);
static bool InitPipeWireBackend(LinuxCapture*);
static void DestroyPipeWireBackend(LinuxCapture*);
static void RenderPipeWireFrame(LinuxCapture*, struct pw_buffer*);

static void* RenderThreadMain(void* arg)
{
    LinuxCapture* cap = static_cast<LinuxCapture*>(arg);
    if (!cap)
        return nullptr;

    // Make the GLX context current once on this thread and keep it that way.
    // All rendering happens here; we never release it until the thread exits.
    if (cap->gl_supported && cap->glx_context && cap->window)
    {
        if (glXMakeCurrent(cap->display, cap->window, cap->glx_context) == True)
            cap->context_is_current = 1;
    }

    // ----------------------------------------------------------------
    // PipeWire render path — entered only when backend_mode is already
    // BackendPipeWire (set by InitPipeWireBackend called from the
    // XComposite loop below when pw_init_requested is signalled).
    // ----------------------------------------------------------------
    auto EnterPwRenderLoop = [&]() {
        if (!cap->egl_initialized)
            return;

        // Make EGL current on THIS thread (the render thread).
        // This is safe because we're not on Avalonia's thread.
        if (!eglMakeCurrent(cap->egl_display, cap->egl_surface,
                            cap->egl_surface, cap->egl_context))
        {
            LogNative("PipeWire: eglMakeCurrent on render thread failed (0x%x)", eglGetError());
            return;
        }
        eglSwapInterval(cap->egl_display, 0);

        // Lazy-create the capture texture now that the context is current.
        if (!cap->egl_texture)
        {
            glGenTextures(1, &cap->egl_texture);
            glBindTexture(GL_TEXTURE_2D, cap->egl_texture);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
            glBindTexture(GL_TEXTURE_2D, 0);
        }
        cap->shader_program = 0;
        cap->shader_dirty = 1;
        LogNative("PipeWire: render thread entered PW mode (GL %s)", glGetString(GL_VERSION));

        while (!cap->stop_render_thread && cap->pw_active)
        {
            struct pw_buffer* pbuf = nullptr;

            pthread_mutex_lock(&cap->pw_frame_mutex);
            struct timespec ts;
            clock_gettime(CLOCK_REALTIME, &ts);
            ts.tv_nsec += 10000000; // 10 ms
            if (ts.tv_nsec >= 1000000000)
            {
                ts.tv_sec++;
                ts.tv_nsec -= 1000000000;
            }
            while (!cap->pw_frame_ready && cap->pw_active && !cap->stop_render_thread)
            {
                int ret = pthread_cond_timedwait(&cap->pw_frame_cond, &cap->pw_frame_mutex, &ts);
                if (ret == ETIMEDOUT)
                    break;
            }
            if (cap->pw_frame_ready)
            {
                pbuf = cap->pw_pending_buf;
                cap->pw_pending_buf = nullptr;
                cap->pw_frame_ready = 0;
            }
            pthread_mutex_unlock(&cap->pw_frame_mutex);

            pthread_mutex_lock(&cap->mutex);
            PumpXEventsLocked(cap);
            pthread_mutex_unlock(&cap->mutex);

            if (pbuf)
            {
                RenderPipeWireFrame(cap, pbuf);
                pw_thread_loop_lock(cap->pw_loop);
                pw_stream_queue_buffer(cap->pw_stream, pbuf);
                pw_thread_loop_unlock(cap->pw_loop);
            }
        }

        eglMakeCurrent(cap->egl_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
    };

    if (cap->backend_mode == BackendPipeWire && cap->egl_initialized)
    {
        // Was already initialized before the render thread started
        // (shouldn't normally happen with the new flow, but handle gracefully).
        EnterPwRenderLoop();
        return nullptr;
    }

    // ----------------------------------------------------------------
    // XComposite render loop (also handles switching to PipeWire on
    // demand: when pw_init_requested is set by aes_linux_capture_set_target,
    // the loop breaks, InitPipeWireBackend is called on the RENDER THREAD
    // (never on Avalonia's thread), and then the PW render loop runs).
    // ----------------------------------------------------------------
    const int xfd = cap->display ? XConnectionNumber(cap->display) : -1;

    while (!cap->stop_render_thread)
    {
        // Check if a PipeWire backend was requested since the last iteration.
        int do_pw_init = 0;
        pthread_mutex_lock(&cap->mutex);
        if (cap->pw_init_requested)
        {
            cap->pw_init_requested = 0;
            do_pw_init = 1;
        }
        pthread_mutex_unlock(&cap->mutex);

        if (do_pw_init)
        {
            LogNative("PipeWire: pw_init_requested on render thread — starting portal handshake");
            // Release GLX before taking EGL on this thread
            if (cap->context_is_current)
            {
                glXMakeCurrent(cap->display, None, nullptr);
                cap->context_is_current = 0;
            }
            
            bool pw_ok = InitPipeWireBackend(cap);
            
            // Portal picker dialog is closed. 
            // Emulator is already hidden/reparented immediately in set_target.
            pthread_mutex_lock(&cap->mutex);
            XFlush(cap->display);
            pthread_mutex_unlock(&cap->mutex);

            if (pw_ok)
            {
                EnterPwRenderLoop();
                // Return to X11 idle loop once PipeWire stops
                cap->shader_program = 0;
                cap->shader_dirty = 1;
                cap->backend_mode = BackendNone;
                continue;
            }
            // PW init failed — log and stay in XComposite mode
            LogNative("PipeWire: InitPipeWireBackend failed on render thread; staying XComposite");
            if (cap->gl_supported && cap->glx_context && cap->window)
                if (glXMakeCurrent(cap->display, cap->window, cap->glx_context) == True)
                    cap->context_is_current = 1;
        }

        // ----------------------------------------------------------------
        // Phase 1: Lock, pump X events, decide what to render.
        // ----------------------------------------------------------------
        pthread_mutex_lock(&cap->mutex);
        PumpXEventsLocked(cap);

        const uint64_t now = MonotonicNowNs();

        // Decay source FPS estimate when no new damage events have arrived.
        if (cap->source_last_event_ns != 0)
        {
            const double elapsed = static_cast<double>(now - cap->source_last_event_ns) / 1000000000.0;
            if (elapsed >= 0.05)
            {
                cap->source_fps *= 0.94;
                if (cap->source_fps < 0.1)
                    cap->source_fps = 0.0;
                cap->source_frame_time_ms = cap->source_fps > 0.0 ? 1000.0 / cap->source_fps : 0.0;
                cap->source_last_event_ns = now;
            }
        }

        cap->fps = StabilizeReportedFps(cap->source_fps);
        cap->frame_time_ms = cap->fps > 0.0 ? 1000.0 / cap->fps : 0.0;

        bool shouldRender  = cap->active && cap->backend_mode == BackendGpuComposite && cap->target != 0;
        bool disableVsync  = cap->disable_vsync != 0;
        bool pendingFrame  = cap->gpu_frame_pending != 0;

        const uint64_t periodicNs       = disableVsync ? 8333333ULL : 16666666ULL;
        const uint64_t silenceThreshold = periodicNs * 2;
        bool sourceSilent  = shouldRender &&
                             cap->source_fps > 0.0 &&
                             cap->source_last_event_ns != 0 &&
                             (now - cap->source_last_event_ns) >= silenceThreshold;
        bool forcePeriodic = shouldRender && (cap->last_render_ns == 0 || sourceSilent);
        bool renderNow     = shouldRender && (pendingFrame || forcePeriodic);

        if (renderNow)
            cap->gpu_frame_pending = 0;

        if (renderNow)
            RenderCompositeFrame(cap);

        pthread_mutex_unlock(&cap->mutex);

        if (renderNow)
        {
            glXSwapBuffers(cap->display, cap->window);

            const uint64_t renderNs = MonotonicNowNs();
            pthread_mutex_lock(&cap->mutex);
            cap->last_render_ns = renderNs;
            SamplePresentMetrics(cap, renderNs);
            pthread_mutex_unlock(&cap->mutex);
        }
        else
        {
            if (xfd >= 0)
            {
                struct pollfd pfd;
                pfd.fd      = xfd;
                pfd.events  = POLLIN;
                pfd.revents = 0;
                poll(&pfd, 1, 4);
            }
            else
            {
                usleep(2000);
            }
        }
    }

    // Release the GL context before the thread exits.
    if (cap->context_is_current && cap->display)
    {
        glXMakeCurrent(cap->display, None, nullptr);
        cap->context_is_current = 0;
    }

    return nullptr;
}

// ── D-Bus portal helpers ──────────────────────────────────────────────────────

static bool DbusAppendStringVariant(DBusMessageIter* dict, const char* key, const char* val)
{
    DBusMessageIter entry, var;
    dbus_message_iter_open_container(dict, DBUS_TYPE_DICT_ENTRY, nullptr, &entry);
    dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &key);
    dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, "s", &var);
    dbus_message_iter_append_basic(&var, DBUS_TYPE_STRING, &val);
    dbus_message_iter_close_container(&entry, &var);
    dbus_message_iter_close_container(dict, &entry);
    return true;
}

static bool DbusAppendUint32Variant(DBusMessageIter* dict, const char* key, dbus_uint32_t val)
{
    DBusMessageIter entry, var;
    dbus_message_iter_open_container(dict, DBUS_TYPE_DICT_ENTRY, nullptr, &entry);
    dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &key);
    dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, "u", &var);
    dbus_message_iter_append_basic(&var, DBUS_TYPE_UINT32, &val);
    dbus_message_iter_close_container(&entry, &var);
    dbus_message_iter_close_container(dict, &entry);
    return true;
}

// Wait for org.freedesktop.portal.Request::Response on `request_path`.
// Returns the response code (0 = success), or -1 on timeout/error.
// If out_node_id is non-null and the response contains "streams", the first
// stream's node-id is written there.
static int DbusWaitForResponse(DBusConnection* conn, const char* request_path,
                               uint32_t* out_node_id)
{
    char match[512];
    snprintf(match, sizeof(match),
        "type='signal',interface='org.freedesktop.portal.Request',"
        "member='Response',path='%s'", request_path);
    dbus_bus_add_match(conn, match, nullptr);
    dbus_connection_flush(conn);

    const int timeout_ms = 60000; // 60 s — user needs time to pick window
    const int64_t deadline_us =
        static_cast<int64_t>(MonotonicNowNs() / 1000) + timeout_ms * 1000LL;

    int result = -1;
    while (static_cast<int64_t>(MonotonicNowNs() / 1000) < deadline_us)
    {
        dbus_connection_read_write(conn, 50);
        DBusMessage* msg = dbus_connection_pop_message(conn);
        if (!msg)
            continue;

        if (dbus_message_is_signal(msg, "org.freedesktop.portal.Request", "Response") &&
            strcmp(dbus_message_get_path(msg), request_path) == 0)
        {
            DBusMessageIter it;
            dbus_uint32_t code = 1;
            if (dbus_message_iter_init(msg, &it) &&
                dbus_message_iter_get_arg_type(&it) == DBUS_TYPE_UINT32)
                dbus_message_iter_get_basic(&it, &code);

            result = static_cast<int>(code);

            // If caller wants node_id, dig into the results dict
            if (out_node_id && result == 0 && dbus_message_iter_next(&it))
            {
                DBusMessageIter results, sub;
                dbus_message_iter_recurse(&it, &results);
                while (dbus_message_iter_get_arg_type(&results) == DBUS_TYPE_DICT_ENTRY)
                {
                    dbus_message_iter_recurse(&results, &sub);
                    const char* rkey = nullptr;
                    dbus_message_iter_get_basic(&sub, &rkey);
                    if (rkey && strcmp(rkey, "streams") == 0)
                    {
                        // streams: a(ua{sv}) — take first stream node id
                        dbus_message_iter_next(&sub);
                        DBusMessageIter var2, arr, stream;
                        dbus_message_iter_recurse(&sub, &var2);
                        dbus_message_iter_recurse(&var2, &arr);
                        if (dbus_message_iter_get_arg_type(&arr) == DBUS_TYPE_STRUCT)
                        {
                            dbus_message_iter_recurse(&arr, &stream);
                            if (dbus_message_iter_get_arg_type(&stream) == DBUS_TYPE_UINT32)
                            {
                                dbus_uint32_t node = 0;
                                dbus_message_iter_get_basic(&stream, &node);
                                *out_node_id = node;
                            }
                        }
                        break;
                    }
                    dbus_message_iter_next(&results);
                }
            }

            dbus_message_unref(msg);
            break;
        }
        dbus_message_unref(msg);
    }

    dbus_bus_remove_match(conn, match, nullptr);
    return result;
}

// Like DbusWaitForResponse but additionally extracts a string value from the
// results dict (used to read "session_handle" from the CreateSession response).
static int DbusWaitForResponseWithString(DBusConnection* conn, const char* request_path,
                                         const char* result_key, char* out_str, int out_len,
                                         uint32_t* out_node_id)
{
    char match[512];
    snprintf(match, sizeof(match),
        "type='signal',interface='org.freedesktop.portal.Request',"
        "member='Response',path='%s'", request_path);
    dbus_bus_add_match(conn, match, nullptr);
    dbus_connection_flush(conn);

    const int timeout_ms = 60000;
    const int64_t deadline_us =
        static_cast<int64_t>(MonotonicNowNs() / 1000) + timeout_ms * 1000LL;

    int result = -1;
    while (static_cast<int64_t>(MonotonicNowNs() / 1000) < deadline_us)
    {
        dbus_connection_read_write(conn, 50);
        DBusMessage* msg = dbus_connection_pop_message(conn);
        if (!msg)
            continue;

        if (dbus_message_is_signal(msg, "org.freedesktop.portal.Request", "Response") &&
            strcmp(dbus_message_get_path(msg), request_path) == 0)
        {
            DBusMessageIter it;
            dbus_uint32_t code = 1;
            if (dbus_message_iter_init(msg, &it) &&
                dbus_message_iter_get_arg_type(&it) == DBUS_TYPE_UINT32)
                dbus_message_iter_get_basic(&it, &code);

            result = static_cast<int>(code);

            if (result == 0 && dbus_message_iter_next(&it))
            {
                DBusMessageIter results, sub;
                dbus_message_iter_recurse(&it, &results);
                while (dbus_message_iter_get_arg_type(&results) == DBUS_TYPE_DICT_ENTRY)
                {
                    dbus_message_iter_recurse(&results, &sub);
                    const char* rkey = nullptr;
                    dbus_message_iter_get_basic(&sub, &rkey);
                    if (rkey && result_key && strcmp(rkey, result_key) == 0 && out_str)
                    {
                        dbus_message_iter_next(&sub);
                        DBusMessageIter var;
                        dbus_message_iter_recurse(&sub, &var);
                        if (dbus_message_iter_get_arg_type(&var) == DBUS_TYPE_OBJECT_PATH ||
                            dbus_message_iter_get_arg_type(&var) == DBUS_TYPE_STRING)
                        {
                            const char* s = nullptr;
                            dbus_message_iter_get_basic(&var, &s);
                            if (s) strncpy(out_str, s, out_len - 1);
                        }
                    }
                    if (rkey && out_node_id && strcmp(rkey, "streams") == 0)
                    {
                        dbus_message_iter_next(&sub);
                        DBusMessageIter var2, arr, stream;
                        dbus_message_iter_recurse(&sub, &var2);
                        dbus_message_iter_recurse(&var2, &arr);
                        if (dbus_message_iter_get_arg_type(&arr) == DBUS_TYPE_STRUCT)
                        {
                            dbus_message_iter_recurse(&arr, &stream);
                            if (dbus_message_iter_get_arg_type(&stream) == DBUS_TYPE_UINT32)
                            {
                                dbus_uint32_t node = 0;
                                dbus_message_iter_get_basic(&stream, &node);
                                *out_node_id = node;
                            }
                        }
                    }
                    dbus_message_iter_next(&results);
                }
            }

            dbus_message_unref(msg);
            break;
        }
        dbus_message_unref(msg);
    }

    dbus_bus_remove_match(conn, match, nullptr);
    return result;
}

static int PortalOpenScreenCast(uint32_t* node_id_out)
{
    LogNative("PipeWire: opening D-Bus portal session");

    DBusError err;
    dbus_error_init(&err);
    DBusConnection* conn = dbus_bus_get(DBUS_BUS_SESSION, &err);
    if (!conn || dbus_error_is_set(&err))
    {
        LogNative("PipeWire: D-Bus session bus unavailable: %s",
                  dbus_error_is_set(&err) ? err.message : "unknown");
        dbus_error_free(&err);
        return -1;
    }

    const char* sender = dbus_bus_get_unique_name(conn);
    char sender_token[64] = {};
    if (sender)
    {
        int j = 0;
        for (int i = 0; sender[i] && j < 63; ++i)
            sender_token[j++] = (sender[i] == ':' || sender[i] == '.') ? '_' : sender[i];
    }

    char handle_token[64];
    snprintf(handle_token, sizeof(handle_token), "aes_%u", (unsigned)getpid());

    // session_handle is filled in from the CreateSession Response signal;
    // pre-initialize so the constructed fallback works correctly.
    char session_handle[256] = {};

    char session_request_path[256];
    snprintf(session_request_path, sizeof(session_request_path),
             "/org/freedesktop/portal/desktop/request/%s/%s_session",
             sender_token, handle_token);

    DBusMessage* msg = dbus_message_new_method_call(
        "org.freedesktop.portal.Desktop",
        "/org/freedesktop/portal/desktop",
        "org.freedesktop.portal.ScreenCast",
        "CreateSession");

    DBusMessageIter args, opts;
    dbus_message_iter_init_append(msg, &args);
    dbus_message_iter_open_container(&args, DBUS_TYPE_ARRAY, "{sv}", &opts);
    char session_token[64];
    snprintf(session_token, sizeof(session_token), "aes_%u_s", (unsigned)getpid());
    DbusAppendStringVariant(&opts, "handle_token",         handle_token);
    DbusAppendStringVariant(&opts, "session_handle_token", session_token);
    dbus_message_iter_close_container(&args, &opts);

    DBusMessage* reply = dbus_connection_send_with_reply_and_block(conn, msg, 5000, &err);
    dbus_message_unref(msg);
    if (!reply || dbus_error_is_set(&err))
    {
        LogNative("PipeWire: CreateSession failed: %s",
                  dbus_error_is_set(&err) ? err.message : "no reply");
        dbus_error_free(&err);
        dbus_connection_unref(conn);
        return -1;
    }

    const char* req_path = nullptr;
    DBusMessageIter ri;
    dbus_message_iter_init(reply, &ri);
    if (dbus_message_iter_get_arg_type(&ri) == DBUS_TYPE_OBJECT_PATH)
        dbus_message_iter_get_basic(&ri, &req_path);
    const char* session_req_path = req_path ? req_path : session_request_path;
    dbus_message_unref(reply);

    if (DbusWaitForResponseWithString(conn, session_req_path,
                                      "session_handle", session_handle,
                                      sizeof(session_handle), nullptr) != 0)
    {
        LogNative("PipeWire: CreateSession Response indicated failure");
        dbus_connection_unref(conn);
        return -1;
    }
    // If the portal didn't return it in the results, fall back to the
    // constructed path (some older portal versions omit it)
    if (session_handle[0] == '\0')
        snprintf(session_handle, sizeof(session_handle),
                 "/org/freedesktop/portal/desktop/session/%s/%s", sender_token, session_token);
    LogNative("PipeWire: session handle: %s", session_handle);

    char sel_handle_token[64];
    snprintf(sel_handle_token, sizeof(sel_handle_token), "aes_%u_sel", (unsigned)getpid());
    char sel_request_path[256];
    snprintf(sel_request_path, sizeof(sel_request_path),
             "/org/freedesktop/portal/desktop/request/%s/%s",
             sender_token, sel_handle_token);

    msg = dbus_message_new_method_call(
        "org.freedesktop.portal.Desktop",
        "/org/freedesktop/portal/desktop",
        "org.freedesktop.portal.ScreenCast",
        "SelectSources");

    dbus_message_iter_init_append(msg, &args);
    const char* sh = session_handle;
    dbus_message_iter_append_basic(&args, DBUS_TYPE_OBJECT_PATH, &sh);
    dbus_message_iter_open_container(&args, DBUS_TYPE_ARRAY, "{sv}", &opts);
    DbusAppendStringVariant(&opts, "handle_token", sel_handle_token);
    // 'types' is a uint32 bitmask: 1=Monitor, 2=Window, 4=Virtual
    dbus_uint32_t types = 7u;
    DbusAppendUint32Variant(&opts, "types", types);
    dbus_uint32_t persist_mode = 2u;
    DbusAppendUint32Variant(&opts, "persist_mode", persist_mode);
    if (g_pw_restore_token[0] != '\0')
    {
        DbusAppendStringVariant(&opts, "restore_token", g_pw_restore_token);
    }
    // 'multiple' is a boolean (portal spec §2.4), NOT a uint32
    {
        DBusMessageIter entry, var;
        const char* mkey = "multiple";
        dbus_bool_t mval = FALSE; // single source
        dbus_message_iter_open_container(&opts, DBUS_TYPE_DICT_ENTRY, nullptr, &entry);
        dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &mkey);
        dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, "b", &var);
        dbus_message_iter_append_basic(&var, DBUS_TYPE_BOOLEAN, &mval);
        dbus_message_iter_close_container(&entry, &var);
        dbus_message_iter_close_container(&opts, &entry);
    }
    dbus_message_iter_close_container(&args, &opts);

    reply = dbus_connection_send_with_reply_and_block(conn, msg, 5000, &err);
    dbus_message_unref(msg);
    if (!reply || dbus_error_is_set(&err))
    {
        LogNative("PipeWire: SelectSources failed: %s",
                  dbus_error_is_set(&err) ? err.message : "no reply");
        dbus_error_free(&err);
        dbus_connection_unref(conn);
        return -1;
    }
    dbus_message_iter_init(reply, &ri);
    if (dbus_message_iter_get_arg_type(&ri) == DBUS_TYPE_OBJECT_PATH)
        dbus_message_iter_get_basic(&ri, &req_path);
    const char* sel_req_actual = req_path ? req_path : sel_request_path;
    dbus_message_unref(reply);

    if (DbusWaitForResponse(conn, sel_req_actual, nullptr) != 0)
    {
        LogNative("PipeWire: SelectSources user cancelled");
        dbus_connection_unref(conn);
        return -1;
    }

    char start_handle_token[64];
    snprintf(start_handle_token, sizeof(start_handle_token), "aes_%u_start", (unsigned)getpid());
    char start_request_path[256];
    snprintf(start_request_path, sizeof(start_request_path),
             "/org/freedesktop/portal/desktop/request/%s/%s",
             sender_token, start_handle_token);

    msg = dbus_message_new_method_call(
        "org.freedesktop.portal.Desktop",
        "/org/freedesktop/portal/desktop",
        "org.freedesktop.portal.ScreenCast",
        "Start");

    dbus_message_iter_init_append(msg, &args);
    dbus_message_iter_append_basic(&args, DBUS_TYPE_OBJECT_PATH, &sh);
    const char* parent_win = "";
    dbus_message_iter_append_basic(&args, DBUS_TYPE_STRING, &parent_win);
    dbus_message_iter_open_container(&args, DBUS_TYPE_ARRAY, "{sv}", &opts);
    DbusAppendStringVariant(&opts, "handle_token", start_handle_token);
    dbus_message_iter_close_container(&args, &opts);

    reply = dbus_connection_send_with_reply_and_block(conn, msg, 5000, &err);
    dbus_message_unref(msg);
    if (!reply || dbus_error_is_set(&err))
    {
        LogNative("PipeWire: Start method failed: %s",
                  dbus_error_is_set(&err) ? err.message : "no reply");
        dbus_error_free(&err);
        dbus_connection_unref(conn);
        return -1;
    }
    dbus_message_iter_init(reply, &ri);
    if (dbus_message_iter_get_arg_type(&ri) == DBUS_TYPE_OBJECT_PATH)
        dbus_message_iter_get_basic(&ri, &req_path);
    const char* start_req_actual = req_path ? req_path : start_request_path;
    dbus_message_unref(reply);

    LogNative("PipeWire: waiting for Start Response (user selects source)...");
    uint32_t node_id = 0;
    if (DbusWaitForResponseWithString(conn, start_req_actual, "restore_token", 
                                      g_pw_restore_token, sizeof(g_pw_restore_token), &node_id) != 0)
    {
        LogNative("PipeWire: Start user cancelled or timeout");
        dbus_connection_unref(conn);
        return -1;
    }
    SaveRestoreToken();
    LogNative("PipeWire: Start succeeded, node_id=%u", node_id);
    *node_id_out = node_id;

    msg = dbus_message_new_method_call(
        "org.freedesktop.portal.Desktop",
        "/org/freedesktop/portal/desktop",
        "org.freedesktop.portal.ScreenCast",
        "OpenPipeWireRemote");

    dbus_message_iter_init_append(msg, &args);
    dbus_message_iter_append_basic(&args, DBUS_TYPE_OBJECT_PATH, &sh);
    dbus_message_iter_open_container(&args, DBUS_TYPE_ARRAY, "{sv}", &opts);
    dbus_message_iter_close_container(&args, &opts);

    reply = dbus_connection_send_with_reply_and_block(conn, msg, 5000, &err);
    dbus_message_unref(msg);
    if (!reply || dbus_error_is_set(&err))
    {
        LogNative("PipeWire: OpenPipeWireRemote failed: %s",
                  dbus_error_is_set(&err) ? err.message : "no reply");
        dbus_error_free(&err);
        dbus_connection_unref(conn);
        return -1;
    }

    int pw_fd = -1;
    dbus_message_iter_init(reply, &ri);
    if (dbus_message_iter_get_arg_type(&ri) == DBUS_TYPE_UNIX_FD)
        dbus_message_iter_get_basic(&ri, &pw_fd);
    dbus_message_unref(reply);

    if (pw_fd < 0)
    {
        LogNative("PipeWire: OpenPipeWireRemote returned invalid fd");
        dbus_connection_unref(conn);
        return -1;
    }

    int pw_fd_dup = dup(pw_fd);
    dbus_connection_unref(conn);
    LogNative("PipeWire: remote fd=%d (dup=%d)", pw_fd, pw_fd_dup);
    return pw_fd_dup;
}

static void PwOnStateChanged(void* userdata, enum pw_stream_state old_state,
                             enum pw_stream_state new_state, const char* error)
{
    LinuxCapture* cap = static_cast<LinuxCapture*>(userdata);
    LogNative("PipeWire: stream state %s → %s%s%s",
              pw_stream_state_as_string(old_state),
              pw_stream_state_as_string(new_state),
              error ? ": " : "", error ? error : "");
    if (new_state == PW_STREAM_STATE_STREAMING)
    {
        pthread_mutex_lock(&cap->pw_frame_mutex);
        cap->pw_active = 1;
        pthread_mutex_unlock(&cap->pw_frame_mutex);
    }
    else if (new_state == PW_STREAM_STATE_ERROR ||
             new_state == PW_STREAM_STATE_UNCONNECTED)
    {
        pthread_mutex_lock(&cap->pw_frame_mutex);
        cap->pw_active = 0;
        pthread_cond_signal(&cap->pw_frame_cond);
        pthread_mutex_unlock(&cap->pw_frame_mutex);
    }
    (void)cap;
}

static void PwOnParamChanged(void* userdata, uint32_t id,
                             const struct spa_pod* param)
{
    LinuxCapture* cap = static_cast<LinuxCapture*>(userdata);
    if (!param || id != SPA_PARAM_Format)
        return;

    struct spa_video_info info{};
    if (spa_format_video_parse(param, &info) < 0)
        return;

    if (info.media_subtype == SPA_MEDIA_SUBTYPE_raw)
    {
        pthread_mutex_lock(&cap->pw_frame_mutex);
        cap->pw_width     = info.info.raw.size.width;
        cap->pw_height    = info.info.raw.size.height;
        cap->pw_spa_format = info.info.raw.format;
        cap->pw_modifier   = info.info.raw.modifier;
        pthread_mutex_unlock(&cap->pw_frame_mutex);
        LogNative("PipeWire: format negotiated %dx%d fmt=%u mod=%lu",
                  info.info.raw.size.width, info.info.raw.size.height,
                  info.info.raw.format, (unsigned long)info.info.raw.modifier);
    }

    struct spa_pod_builder b{};
    uint8_t buf[256];
    spa_pod_builder_init(&b, buf, sizeof(buf));
    const struct spa_pod* params[1];
    params[0] = reinterpret_cast<const struct spa_pod*>(
        spa_pod_builder_add_object(&b,
            SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers,
            SPA_PARAM_BUFFERS_buffers, SPA_POD_CHOICE_RANGE_Int(4, 2, 8),
            SPA_PARAM_BUFFERS_dataType, SPA_POD_CHOICE_FLAGS_Int(
                (1 << SPA_DATA_DmaBuf) | (1 << SPA_DATA_MemFd) | (1 << SPA_DATA_MemPtr))));
    pw_stream_update_params(cap->pw_stream, params, 1);
}

static void PwOnProcess(void* userdata)
{
    LinuxCapture* cap = static_cast<LinuxCapture*>(userdata);
    struct pw_buffer* buf = pw_stream_dequeue_buffer(cap->pw_stream);
    if (!buf)
        return;

    pthread_mutex_lock(&cap->pw_frame_mutex);
    if (cap->pw_pending_buf)
    {
        pw_stream_queue_buffer(cap->pw_stream, cap->pw_pending_buf);
        cap->pw_pending_buf = nullptr;
    }
    cap->pw_pending_buf = buf;
    cap->pw_frame_ready = 1;
    pthread_cond_signal(&cap->pw_frame_cond);
    pthread_mutex_unlock(&cap->pw_frame_mutex);
}

static const struct pw_stream_events g_pw_stream_events = {
    .version       = PW_VERSION_STREAM_EVENTS,
    .destroy       = nullptr,
    .state_changed = PwOnStateChanged,
    .control_info  = nullptr,
    .io_changed    = nullptr,
    .param_changed = PwOnParamChanged,
    .add_buffer    = nullptr,
    .remove_buffer = nullptr,
    .process       = PwOnProcess,
    .drained       = nullptr,
    .command       = nullptr,
    .trigger_done  = nullptr,
};

static bool InitEglContext(LinuxCapture* cap)
{
    cap->egl_display = eglGetDisplay(static_cast<EGLNativeDisplayType>(cap->display));
    if (cap->egl_display == EGL_NO_DISPLAY)
    {
        LogNative("PipeWire: eglGetDisplay failed");
        return false;
    }

    EGLint major = 0, minor = 0;
    if (!eglInitialize(cap->egl_display, &major, &minor))
    {
        LogNative("PipeWire: eglInitialize failed");
        return false;
    }
    LogNative("PipeWire: EGL %d.%d initialized", major, minor);

    const char* exts = eglQueryString(cap->egl_display, EGL_EXTENSIONS);
    if (!exts || !strstr(exts, "EGL_EXT_image_dma_buf_import"))
    {
        LogNative("PipeWire: EGL_EXT_image_dma_buf_import not available");
        eglTerminate(cap->egl_display);
        cap->egl_display = EGL_NO_DISPLAY;
        return false;
    }

    cap->egl_create_image   = reinterpret_cast<PFNEGLCREATEIMAGEKHRPROC>(
                                eglGetProcAddress("eglCreateImageKHR"));
    cap->egl_destroy_image  = reinterpret_cast<PFNEGLDESTROYIMAGEKHRPROC>(
                                eglGetProcAddress("eglDestroyImageKHR"));
    cap->egl_image_target_tex = reinterpret_cast<PFNGLEGLIMAGETARGETTEXTURE2DOESPROC>(
                                eglGetProcAddress("glEGLImageTargetTexture2DOES"));

    if (!cap->egl_create_image || !cap->egl_destroy_image || !cap->egl_image_target_tex)
    {
        LogNative("PipeWire: missing required EGL/GL extension functions");
        eglTerminate(cap->egl_display);
        cap->egl_display = EGL_NO_DISPLAY;
        return false;
    }

    EGLint cfgAttribs[] = {
        EGL_SURFACE_TYPE,    EGL_WINDOW_BIT,
        EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
        EGL_RED_SIZE,   8, EGL_GREEN_SIZE, 8,
        EGL_BLUE_SIZE,  8, EGL_ALPHA_SIZE, 0,
        EGL_DEPTH_SIZE, 0, EGL_NONE
    };
    EGLConfig cfg;
    EGLint numCfg = 0;
    eglBindAPI(EGL_OPENGL_API);
    if (!eglChooseConfig(cap->egl_display, cfgAttribs, &cfg, 1, &numCfg) || numCfg == 0)
    {
        LogNative("PipeWire: eglChooseConfig failed");
        eglTerminate(cap->egl_display);
        cap->egl_display = EGL_NO_DISPLAY;
        return false;
    }

    cap->egl_surface = eglCreateWindowSurface(cap->egl_display, cfg,
                                              static_cast<EGLNativeWindowType>(cap->window),
                                              nullptr);
    if (cap->egl_surface == EGL_NO_SURFACE)
    {
        LogNative("PipeWire: eglCreateWindowSurface failed (0x%x)", eglGetError());
        eglTerminate(cap->egl_display);
        cap->egl_display = EGL_NO_DISPLAY;
        return false;
    }

    EGLint ctxAttribs[] = { EGL_NONE };
    cap->egl_context = eglCreateContext(cap->egl_display, cfg, EGL_NO_CONTEXT, ctxAttribs);
    if (cap->egl_context == EGL_NO_CONTEXT)
    {
        LogNative("PipeWire: eglCreateContext failed (0x%x)", eglGetError());
        eglDestroySurface(cap->egl_display, cap->egl_surface);
        cap->egl_surface = EGL_NO_SURFACE;
        eglTerminate(cap->egl_display);
        cap->egl_display = EGL_NO_DISPLAY;
        return false;
    }

    // EGL context and GL texture are made current/created by the RENDER THREAD
    // (not here — this function must be callable off the render thread to avoid
    //  corrupting Avalonia's GLX context). Call InitEglContextOnRenderThread()
    //  from the render thread after this returns true.
    cap->egl_initialized = 1;
    LogNative("PipeWire: EGL context objects ready (not yet current)");
    return true;
}

static void CleanupEglContext(LinuxCapture* cap)
{
    if (!cap->egl_initialized)
        return;
    if (cap->egl_texture)
    {
        glDeleteTextures(1, &cap->egl_texture);
        cap->egl_texture = 0;
    }
    eglMakeCurrent(cap->egl_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
    if (cap->egl_context  != EGL_NO_CONTEXT)  eglDestroyContext(cap->egl_display, cap->egl_context);
    if (cap->egl_surface  != EGL_NO_SURFACE)  eglDestroySurface(cap->egl_display, cap->egl_surface);
    if (cap->egl_display  != EGL_NO_DISPLAY)  eglTerminate(cap->egl_display);
    cap->egl_context  = EGL_NO_CONTEXT;
    cap->egl_surface  = EGL_NO_SURFACE;
    cap->egl_display  = EGL_NO_DISPLAY;
    cap->egl_initialized = 0;
    
    // Clear global shader program handle since it belonged to the destroyed context
    cap->shader_program = 0;
}

static bool InitPipeWireBackend(LinuxCapture* cap)
{
    if (!cap || !cap->window)
        return false;

    uint32_t node_id = 0;
    int pw_fd = PortalOpenScreenCast(&node_id);
    if (pw_fd < 0)
    {
        LogNative("PipeWire: portal handshake failed — falling back to XComposite");
        return false;
    }
    cap->pw_fd      = pw_fd;
    cap->pw_node_id = node_id;

    if (!InitEglContext(cap))
    {
        LogNative("PipeWire: EGL init failed — falling back to XComposite");
        close(pw_fd);
        cap->pw_fd = -1;
        return false;
    }

    pw_init(nullptr, nullptr);

    cap->pw_loop = pw_thread_loop_new("aes-pw-capture", nullptr);
    if (!cap->pw_loop)
    {
        LogNative("PipeWire: pw_thread_loop_new failed");
        CleanupEglContext(cap);
        close(pw_fd);
        cap->pw_fd = -1;
        return false;
    }

    struct pw_context* pw_ctx = pw_context_new(
        pw_thread_loop_get_loop(cap->pw_loop), nullptr, 0);
    if (!pw_ctx)
    {
        LogNative("PipeWire: pw_context_new failed");
        pw_thread_loop_destroy(cap->pw_loop);
        cap->pw_loop = nullptr;
        CleanupEglContext(cap);
        close(pw_fd);
        cap->pw_fd = -1;
        return false;
    }

    struct pw_core* pw_core = pw_context_connect_fd(pw_ctx,
                                                     fcntl(pw_fd, F_DUPFD_CLOEXEC, 3),
                                                     nullptr, 0);
    if (!pw_core)
    {
        LogNative("PipeWire: pw_context_connect_fd failed");
        pw_context_destroy(pw_ctx);
        pw_thread_loop_destroy(cap->pw_loop);
        cap->pw_loop = nullptr;
        CleanupEglContext(cap);
        close(pw_fd);
        cap->pw_fd = -1;
        return false;
    }

    cap->pw_stream = pw_stream_new(pw_core, "aes-capture",
        pw_properties_new(
            PW_KEY_MEDIA_TYPE,    "Video",
            PW_KEY_MEDIA_CATEGORY, "Capture",
            PW_KEY_MEDIA_ROLE,    "Screen",
            nullptr));
    if (!cap->pw_stream)
    {
        LogNative("PipeWire: pw_stream_new failed");
        pw_core_disconnect(pw_core);
        pw_context_destroy(pw_ctx);
        pw_thread_loop_destroy(cap->pw_loop);
        cap->pw_loop = nullptr;
        CleanupEglContext(cap);
        close(pw_fd);
        cap->pw_fd = -1;
        return false;
    }

    pw_stream_add_listener(cap->pw_stream, &cap->pw_stream_hook,
                           &g_pw_stream_events, cap);

    uint8_t pbuf[1024];
    struct spa_pod_builder sb{};
    spa_pod_builder_init(&sb, pbuf, sizeof(pbuf));
    const struct spa_pod* params[1];
    struct spa_rectangle defSz  = { 1280, 720 };
    struct spa_rectangle minSz  = { 1, 1 };
    struct spa_rectangle maxSz  = { 7680, 4320 };
    struct spa_fraction  defFps = { 60, 1 };
    struct spa_fraction  minFps = { 0, 1 };
    struct spa_fraction  maxFps = { 240, 1 };
    uint32_t fmts[] = { SPA_VIDEO_FORMAT_BGRA, SPA_VIDEO_FORMAT_BGRx,
                        SPA_VIDEO_FORMAT_RGBA, SPA_VIDEO_FORMAT_RGBx };
    params[0] = reinterpret_cast<const struct spa_pod*>(
        spa_pod_builder_add_object(&sb,
            SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat,
            SPA_FORMAT_mediaType,    SPA_POD_Id(SPA_MEDIA_TYPE_video),
            SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
            SPA_FORMAT_VIDEO_format,
                SPA_POD_CHOICE_ENUM_Id(5,
                    fmts[0], fmts[0], fmts[1], fmts[2], fmts[3]),
            SPA_FORMAT_VIDEO_size,
                SPA_POD_CHOICE_RANGE_Rectangle(&defSz, &minSz, &maxSz),
            SPA_FORMAT_VIDEO_framerate,
                SPA_POD_CHOICE_RANGE_Fraction(&defFps, &minFps, &maxFps)));

    pw_thread_loop_lock(cap->pw_loop);
    pw_thread_loop_start(cap->pw_loop);

    int r = pw_stream_connect(cap->pw_stream,
        PW_DIRECTION_INPUT, cap->pw_node_id,
        static_cast<enum pw_stream_flags>(
            PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS),
        params, 1);
    pw_thread_loop_unlock(cap->pw_loop);

    if (r < 0)
    {
        LogNative("PipeWire: pw_stream_connect failed: %s", spa_strerror(r));
        pw_stream_destroy(cap->pw_stream);
        cap->pw_stream = nullptr;
        pw_core_disconnect(pw_core);
        pw_context_destroy(pw_ctx);
        pw_thread_loop_destroy(cap->pw_loop);
        cap->pw_loop = nullptr;
        CleanupEglContext(cap);
        close(pw_fd);
        cap->pw_fd = -1;
        return false;
    }

    cap->backend_mode = BackendPipeWire;
    cap->pw_active = 1;
    pthread_mutex_init(&cap->pw_frame_mutex, nullptr);
    pthread_cond_init(&cap->pw_frame_cond, nullptr);
    SetBackendDetail(cap, "PipeWire DMA-BUF");
    SetStatusText(cap, "PipeWire capture active");
    LogNative("PipeWire: backend initialised (node=%u)", node_id);
    return true;
}

static void StopPipeWireBackend(LinuxCapture* cap)
{
    if (!cap)
        return;

    if (cap->pw_loop)
    {
        pw_thread_loop_lock(cap->pw_loop);
        if (cap->pw_stream)
        {
            pw_stream_disconnect(cap->pw_stream);
            pw_stream_destroy(cap->pw_stream);
            cap->pw_stream = nullptr;
        }
        pw_thread_loop_unlock(cap->pw_loop);
        pw_thread_loop_stop(cap->pw_loop);
        pw_thread_loop_destroy(cap->pw_loop);
        cap->pw_loop = nullptr;
    }

    pthread_mutex_lock(&cap->pw_frame_mutex);
    cap->pw_active  = 0;
    cap->pw_frame_ready = 0;
    cap->pw_pending_buf = nullptr;
    pthread_cond_signal(&cap->pw_frame_cond);
    pthread_mutex_unlock(&cap->pw_frame_mutex);

    if (cap->pw_fd >= 0)
    {
        close(cap->pw_fd);
        cap->pw_fd = -1;
    }

    CleanupEglContext(cap);
}

static void DestroyPipeWireBackend(LinuxCapture* cap)
{
    if (!cap)
        return;

    StopPipeWireBackend(cap);

    pthread_cond_destroy(&cap->pw_frame_cond);
    pthread_mutex_destroy(&cap->pw_frame_mutex);
}

static void RenderPipeWireFrame(LinuxCapture* cap, struct pw_buffer* pwbuf)
{
    if (!cap || !pwbuf || !cap->egl_initialized)
        return;

    struct spa_buffer* spa_buf = pwbuf->buffer;
    if (!spa_buf || spa_buf->n_datas == 0)
        return;

    struct spa_data* d = &spa_buf->datas[0];
    int dmabuf_fd = -1;
    bool is_dmabuf = false;

    if (d->type == SPA_DATA_DmaBuf)
    {
        dmabuf_fd = static_cast<int>(d->fd);
        is_dmabuf = true;
    }
    else if (d->type == SPA_DATA_MemFd || d->type == SPA_DATA_MemPtr)
    {
        const uint8_t* pixels = d->type == SPA_DATA_MemPtr
            ? static_cast<const uint8_t*>(d->data)
            : static_cast<const uint8_t*>(
                  reinterpret_cast<void*>(reinterpret_cast<uintptr_t>(d->data)));
        if (!pixels)
            return;

        glBindTexture(GL_TEXTURE_2D, cap->egl_texture);
        GLenum fmt = (cap->pw_spa_format == SPA_VIDEO_FORMAT_BGRA ||
                      cap->pw_spa_format == SPA_VIDEO_FORMAT_BGRx)
                     ? GL_BGRA_EXT : GL_RGBA;
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA,
                     cap->pw_width, cap->pw_height, 0,
                     fmt, GL_UNSIGNED_BYTE, pixels + d->chunk->offset);
    }
    else
    {
        return;
    }

    EGLImageKHR img = EGL_NO_IMAGE_KHR;
    if (is_dmabuf)
    {
        uint32_t stride = d->chunk ? d->chunk->stride : (uint32_t)(cap->pw_width * 4);
        EGLint drm_fmt;
        switch (cap->pw_spa_format)
        {
            case SPA_VIDEO_FORMAT_BGRA: drm_fmt = 0x34324742; break;
            case SPA_VIDEO_FORMAT_BGRx: drm_fmt = 0x34324758; break;
            case SPA_VIDEO_FORMAT_RGBA: drm_fmt = 0x34324152; break;
            default:                   drm_fmt = 0x34325241; break;
        }
        EGLint imgAttribs[] = {
            EGL_WIDTH,                          cap->pw_width,
            EGL_HEIGHT,                         cap->pw_height,
            EGL_LINUX_DRM_FOURCC_EXT,           drm_fmt,
            EGL_DMA_BUF_PLANE0_FD_EXT,         dmabuf_fd,
            EGL_DMA_BUF_PLANE0_OFFSET_EXT,     d->chunk ? (EGLint)d->chunk->offset : 0,
            EGL_DMA_BUF_PLANE0_PITCH_EXT,      (EGLint)stride,
            EGL_IMAGE_PRESERVED_KHR,            EGL_FALSE,
            EGL_NONE
        };
        img = cap->egl_create_image(cap->egl_display, EGL_NO_CONTEXT,
                                    EGL_LINUX_DMA_BUF_EXT, nullptr, imgAttribs);
        if (img == EGL_NO_IMAGE_KHR)
        {
            LogNative("PipeWire: eglCreateImageKHR failed (0x%x) — dropping frame", eglGetError());
            return;
        }
        glBindTexture(GL_TEXTURE_2D, cap->egl_texture);
        cap->egl_image_target_tex(GL_TEXTURE_2D, static_cast<GLeglImageOES>(img));
    }

    pthread_mutex_lock(&cap->mutex);
    EnsureShaderProgram(cap);

    if (cap->host_geometry_dirty)
    {
        XWindowAttributes hostAttr{};
        if (XGetWindowAttributes(cap->display, cap->window, &hostAttr) != 0)
        {
            // use a macro if defined, else directly
            cap->cached_host_w = std::max(hostAttr.width, 1);
            cap->cached_host_h = std::max(hostAttr.height, 1);
        }
        cap->host_geometry_dirty = 0;
    }
    pthread_mutex_unlock(&cap->mutex);

    glViewport(0, 0, cap->cached_host_w > 0 ? cap->cached_host_w : 1280,
                      cap->cached_host_h > 0 ? cap->cached_host_h : 720);
    glClearColor(0.0f, 0.0f, 0.0f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT);

    glUseProgram(cap->shader_program);

    if (cap->shader_u_tex >= 0)         glUniform1i(cap->shader_u_tex, 0);
    if (cap->shader_u_brightness >= 0)  glUniform1f(cap->shader_u_brightness, cap->brightness);
    if (cap->shader_u_saturation >= 0)  glUniform1f(cap->shader_u_saturation, cap->saturation);
    if (cap->shader_u_tint >= 0)        glUniform4fv(cap->shader_u_tint, 1, cap->tint);
    if (cap->shader_u_source_size >= 0) glUniform2f(cap->shader_u_source_size,
                                            (float)cap->pw_width, (float)cap->pw_height);
    if (cap->shader_u_output_size >= 0) glUniform2f(cap->shader_u_output_size,
                                            (float)(cap->cached_host_w > 0 ? cap->cached_host_w : 1280),
                                            (float)(cap->cached_host_h > 0 ? cap->cached_host_h : 720));

    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, cap->egl_texture);

    int extLeft = 0, extRight = 0, extTop = 0, extBottom = 0;
    if (cap->target_hidden_offscreen || cap->target)
    {
        Window topLevel = GetTopLevelWindow(cap->display, cap->target);
        if (!topLevel) topLevel = cap->target;
        GetWindowExtents(cap->display, topLevel, &extLeft, &extRight, &extTop, &extBottom);
    }

    int srcW = std::max(1, (int)cap->pw_width - extLeft - extRight);
    int srcH = std::max(1, (int)cap->pw_height - extTop - extBottom);

    float u0 = 0.0f, u1 = 1.0f;
    float v0 = 0.0f, v1 = 1.0f;

    if (cap->pw_width > 0 && cap->pw_height > 0)
    {
        u0 = (float)(extLeft + cap->crop[0]) / cap->pw_width;
        u1 = 1.0f - ((float)(extRight + cap->crop[2]) / cap->pw_width);
        v0 = (float)(extTop + cap->crop[1]) / cap->pw_height;
        v1 = 1.0f - ((float)(extBottom + cap->crop[3]) / cap->pw_height);
    }

    int hostW = std::max(1, cap->cached_host_w);
    int hostH = std::max(1, cap->cached_host_h);
    int vpX = 0, vpY = 0, vpW = hostW, vpH = hostH;

    const double srcAspect = static_cast<double>(srcW) / static_cast<double>(srcH);
    const double dstAspect = static_cast<double>(hostW) / static_cast<double>(hostH);

    if (cap->stretch == 0) // None
    {
        vpW = std::min(hostW, srcW);
        vpH = std::min(hostH, srcH);
        vpX = (hostW - vpW) / 2;
        vpY = (hostH - vpH) / 2;
    }
    else if (cap->stretch == 2) // Uniform
    {
        if (srcAspect > dstAspect) {
            vpW = hostW;
            vpH = std::max(1, static_cast<int>(hostW / srcAspect));
        } else {
            vpH = hostH;
            vpW = std::max(1, static_cast<int>(hostH * srcAspect));
        }
        vpX = (hostW - vpW) / 2;
        vpY = (hostH - vpH) / 2;
    }
    else if (cap->stretch == 3) // UniformToFill
    {
        if (srcAspect > dstAspect) {
            vpH = hostH;
            vpW = std::max(1, static_cast<int>(hostH * srcAspect));
        } else {
            vpW = hostW;
            vpH = std::max(1, static_cast<int>(hostW / srcAspect));
        }
        vpX = (hostW - vpW) / 2;
        vpY = (hostH - vpH) / 2;
    }

    glViewport(vpX, vpY, vpW, vpH);
    glClearColor(0.0f, 0.0f, 0.0f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT);

    glUseProgram(cap->shader_program);

    if (cap->shader_u_tex >= 0)         glUniform1i(cap->shader_u_tex, 0);
    if (cap->shader_u_brightness >= 0)  glUniform1f(cap->shader_u_brightness, cap->brightness);
    if (cap->shader_u_saturation >= 0)  glUniform1f(cap->shader_u_saturation, cap->saturation);
    if (cap->shader_u_tint >= 0)        glUniform4fv(cap->shader_u_tint, 1, cap->tint);
    if (cap->shader_u_source_size >= 0) glUniform2f(cap->shader_u_source_size, (float)srcW, (float)srcH);
    if (cap->shader_u_output_size >= 0) glUniform2f(cap->shader_u_output_size, (float)vpW, (float)vpH);

    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, cap->egl_texture);

    glBegin(GL_TRIANGLE_STRIP);
    glTexCoord2f(u0, v0); glVertex2f(-1.0f,  1.0f);
    glTexCoord2f(u1, v0); glVertex2f( 1.0f,  1.0f);
    glTexCoord2f(u0, v1); glVertex2f(-1.0f, -1.0f);
    glTexCoord2f(u1, v1); glVertex2f( 1.0f, -1.0f);
    glEnd();

    glUseProgram(0);
    glFlush();
    glBindTexture(GL_TEXTURE_2D, 0);

    if (img != EGL_NO_IMAGE_KHR)
        cap->egl_destroy_image(cap->egl_display, img);

    eglSwapBuffers(cap->egl_display, cap->egl_surface);

    const uint64_t renderNs = MonotonicNowNs();
    pthread_mutex_lock(&cap->mutex);
    cap->last_render_ns = renderNs;
    SamplePresentMetrics(cap, renderNs);
    pthread_mutex_unlock(&cap->mutex);
}

static bool InitGlObjects(LinuxCapture* cap, Window parent)
{
    if (!cap || !cap->display)
        return false;

    int fbAttribsTextureStrict[] = {
        GLX_X_RENDERABLE, True,
        GLX_DRAWABLE_TYPE, GLX_WINDOW_BIT | GLX_PIXMAP_BIT,
        GLX_RENDER_TYPE, GLX_RGBA_BIT,
        GLX_X_VISUAL_TYPE, GLX_TRUE_COLOR,
        GLX_RED_SIZE, 8,
        GLX_GREEN_SIZE, 8,
        GLX_BLUE_SIZE, 8,
        GLX_ALPHA_SIZE, 8,
        GLX_DOUBLEBUFFER, True,
        GLX_BIND_TO_TEXTURE_RGB_EXT, True,
        GLX_BIND_TO_TEXTURE_RGBA_EXT, True,
        GLX_BIND_TO_TEXTURE_TARGETS_EXT, GLX_TEXTURE_2D_BIT_EXT,
        None
    };
    int fbAttribsAlpha[] = {
        GLX_X_RENDERABLE, True,
        GLX_DRAWABLE_TYPE, GLX_WINDOW_BIT | GLX_PIXMAP_BIT,
        GLX_RENDER_TYPE, GLX_RGBA_BIT,
        GLX_X_VISUAL_TYPE, GLX_TRUE_COLOR,
        GLX_RED_SIZE, 8,
        GLX_GREEN_SIZE, 8,
        GLX_BLUE_SIZE, 8,
        GLX_ALPHA_SIZE, 8,
        GLX_DOUBLEBUFFER, True,
        None
    };
    int fbAttribsNoAlpha[] = {
        GLX_X_RENDERABLE, True,
        GLX_DRAWABLE_TYPE, GLX_WINDOW_BIT | GLX_PIXMAP_BIT,
        GLX_RENDER_TYPE, GLX_RGBA_BIT,
        GLX_X_VISUAL_TYPE, GLX_TRUE_COLOR,
        GLX_RED_SIZE, 8,
        GLX_GREEN_SIZE, 8,
        GLX_BLUE_SIZE, 8,
        GLX_DOUBLEBUFFER, True,
        None
    };

    int fbCount = 0;
    GLXFBConfig* fbc = glXChooseFBConfig(cap->display, cap->screen, fbAttribsTextureStrict, &fbCount);
    if ((!fbc || fbCount == 0) && fbc)
    {
        XFree(fbc);
        fbc = nullptr;
    }

    if (!fbc || fbCount == 0)
        fbc = glXChooseFBConfig(cap->display, cap->screen, fbAttribsAlpha, &fbCount);
    if ((!fbc || fbCount == 0) && fbc)
    {
        XFree(fbc);
        fbc = nullptr;
    }

    if (!fbc || fbCount == 0)
        fbc = glXChooseFBConfig(cap->display, cap->screen, fbAttribsNoAlpha, &fbCount);

    if (!fbc || fbCount == 0)
    {
        if (fbc)
            XFree(fbc);
        SetBackendDetail(cap, "glXChooseFBConfig failed");
        LogNative("glXChooseFBConfig failed on screen=%d", cap->screen);
        return false;
    }

    cap->fb_config = fbc[0];
    XFree(fbc);

    XVisualInfo* vi = glXGetVisualFromFBConfig(cap->display, cap->fb_config);
    if (!vi)
    {
        SetBackendDetail(cap, "glXGetVisualFromFBConfig failed");
        LogNative("glXGetVisualFromFBConfig failed");
        return false;
    }

    XSetWindowAttributes swa{};
    swa.colormap = XCreateColormap(cap->display, parent, vi->visual, AllocNone);
    swa.border_pixel = 0;
    swa.event_mask = StructureNotifyMask;
    cap->colormap = swa.colormap;

    cap->window = XCreateWindow(cap->display, parent,
        0, 0, 1, 1,
        0,
        vi->depth,
        InputOutput,
        vi->visual,
        CWBorderPixel | CWColormap | CWEventMask,
        &swa);

    XFree(vi);

    if (cap->window == 0)
    {
        SetBackendDetail(cap, "XCreateWindow for capture host failed");
        LogNative("XCreateWindow for capture host failed");
        return false;
    }

    XMapWindow(cap->display, cap->window);
    XFlush(cap->display);

    cap->glx_context = glXCreateNewContext(cap->display, cap->fb_config, GLX_RGBA_TYPE, nullptr, True);
    if (!cap->glx_context)
    {
        SetBackendDetail(cap, "glXCreateNewContext failed");
        LogNative("glXCreateNewContext failed");
        return false;
    }

    cap->glx_bind_tex_image_ext = reinterpret_cast<PFNGLXBINDTEXIMAGEEXTPROC>(glXGetProcAddressARB((const GLubyte*)"glXBindTexImageEXT"));
    cap->glx_release_tex_image_ext = reinterpret_cast<PFNGLXRELEASETEXIMAGEEXTPROC>(glXGetProcAddressARB((const GLubyte*)"glXReleaseTexImageEXT"));
    cap->glx_swap_interval_ext = reinterpret_cast<PFNGLXSWAPINTERVALEXTPROC>(glXGetProcAddressARB((const GLubyte*)"glXSwapIntervalEXT"));
    cap->glx_swap_interval_mesa = reinterpret_cast<PFNGLXSWAPINTERVALMESAPROC>(glXGetProcAddressARB((const GLubyte*)"glXSwapIntervalMESA"));
    cap->glx_swap_interval_sgi = reinterpret_cast<PFNGLXSWAPINTERVALSGIPROC>(glXGetProcAddressARB((const GLubyte*)"glXSwapIntervalSGI"));
    const char* ext = glXQueryExtensionsString(cap->display, cap->screen);
    cap->has_swap_control_tear = (ext && strstr(ext, "GLX_EXT_swap_control_tear")) ? 1 : 0;

    if (!cap->glx_bind_tex_image_ext || !cap->glx_release_tex_image_ext)
    {
        SetBackendDetail(cap, "GLX_EXT_texture_from_pixmap unavailable");
        LogNative("GLX_EXT_texture_from_pixmap unavailable");
        return false;
    }

    cap->gl_supported = 1;
    cap->has_swap_control = (cap->glx_swap_interval_ext || cap->glx_swap_interval_mesa || cap->glx_swap_interval_sgi) ? 1 : 0;
    LogNative("swap control: has=%d tear=%d", cap->has_swap_control, cap->has_swap_control_tear);
    cap->applied_disable_vsync = -1;
    SetBackendDetail(cap, "OpenGL composite initialized");
    LogNative("OpenGL composite initialized");
    return true;
}

static void CleanupGlObjects(LinuxCapture* cap)
{
    if (!cap || !cap->display)
        return;

    DestroyCompositeResources(cap);

    if (cap->shader_program)
    {
        glDeleteProgram(cap->shader_program);
        cap->shader_program = 0;
    }

    cap->shader_u_tex = -1;
    cap->shader_u_brightness = -1;
    cap->shader_u_saturation = -1;
    cap->shader_u_tint = -1;
    cap->shader_u_source_size = -1;
    cap->shader_u_output_size = -1;

    if (cap->gl_texture)
    {
        glDeleteTextures(1, &cap->gl_texture);
        cap->gl_texture = 0;
    }
    cap->texture_params_initialized = 0;

    if (cap->glx_context)
    {
        glXDestroyContext(cap->display, cap->glx_context);
        cap->glx_context = nullptr;
    }

    if (cap->window)
    {
        XDestroyWindow(cap->display, cap->window);
        cap->window = 0;
    }

    if (cap->colormap)
    {
        XFreeColormap(cap->display, cap->colormap);
        cap->colormap = 0;
    }

    cap->gl_supported = 0;
}

LinuxCapture* aes_linux_capture_create(void* parentHandle)
{
    pthread_once(&g_x11_threads_once, InitX11ThreadsOnce);

    LinuxCapture* cap = static_cast<LinuxCapture*>(calloc(1, sizeof(LinuxCapture)));
    if (!cap)
        return nullptr;

    LoadRestoreToken();

    cap->display = XOpenDisplay(nullptr);
    if (!cap->display)
    {
        free(cap);
        return nullptr;
    }
    LogNative("capture create: display opened");

    cap->screen = DefaultScreen(cap->display);
    pthread_mutex_init(&cap->mutex, nullptr);
    cap->context_is_current = 0;

    Window parent = GetParentWindow(cap->display, parentHandle);

    if (!InitGlObjects(cap, parent))
    {
        // fallback host window when GLX composite isn't available
        if (cap->window == 0)
        {
            cap->window = XCreateSimpleWindow(cap->display, parent,
                0, 0, 1, 1, 0,
                BlackPixel(cap->display, cap->screen),
                BlackPixel(cap->display, cap->screen));
            XSelectInput(cap->display, cap->window, StructureNotifyMask);
            XMapWindow(cap->display, cap->window);
            XFlush(cap->display);
        }

        cap->gl_supported = 0;
        SetGpuInfo(cap, "X11 Reparent (fallback)", "Linux");
        SetStatusText(cap, "GPU composite unavailable, using fallback pipeline");
        LogNative("fallback host created: GPU composite unavailable");
    }
    else
    {
        SetBackendDetail(cap, "X11/XWayland GPU composite");
        SetGpuInfo(cap, "OpenGL (GLX) composite", "Linux/X11");
        SetStatusText(cap, "Linux capture idle");
    }

    int p_w = 320, p_h = 240, p_x = 0, p_y = 0;
    // Don't trust initial parent size if it's likely uninitialized (fullscreen)
    if (cap->window) {
        Window child;
        if (XTranslateCoordinates(cap->display, cap->window, DefaultRootWindow(cap->display), 0, 0, &p_x, &p_y, &child)) {
            XWindowAttributes attr;
            if (XGetWindowAttributes(cap->display, cap->window, &attr)) {
                int screen_w = DisplayWidth(cap->display, cap->screen);
                int screen_h = DisplayHeight(cap->display, cap->screen);
                if (attr.width > 0 && attr.height > 0 && attr.width < screen_w && attr.height < screen_h) {
                    p_w = attr.width;
                    p_h = attr.height;
                }
            }
        }
    }
    
    cap->proxy_window = XCreateSimpleWindow(cap->display, DefaultRootWindow(cap->display),
        p_x, p_y, p_w > 0 ? p_w : 1, p_h > 0 ? p_h : 1, 0,
        BlackPixel(cap->display, cap->screen),
        BlackPixel(cap->display, cap->screen));
    
    // Prevent focus even when clicked/mapped to avoid jumping to top
    Atom user_time_atom = XInternAtom(cap->display, "_NET_WM_USER_TIME", False);
    unsigned long user_time = 0;
    XChangeProperty(cap->display, cap->proxy_window, user_time_atom, XA_CARDINAL, 32, PropModeReplace, (unsigned char*)&user_time, 1);
        
    XSizeHints* initialSizeHints = XAllocSizeHints();
    if (initialSizeHints) {
        int w = p_w > 0 ? p_w : 1;
        int h = p_h > 0 ? p_h : 1;
        initialSizeHints->flags = PPosition | PSize | PMinSize | PMaxSize;
        initialSizeHints->x = p_x; initialSizeHints->y = p_y; 
        initialSizeHints->width = w; initialSizeHints->height = h;
        initialSizeHints->min_width = w; initialSizeHints->min_height = h;
        initialSizeHints->max_width = w; initialSizeHints->max_height = h;
        XSetWMNormalHints(cap->display, cap->proxy_window, initialSizeHints);
        XFree(initialSizeHints);
    }
        
    struct {
        unsigned long flags;
        unsigned long functions;
        unsigned long decorations;
        long input_mode;
        unsigned long status;
    } hints = {2, 0, 0, 0, 0};
    Atom motif_hints = XInternAtom(cap->display, "_MOTIF_WM_HINTS", False);
    XChangeProperty(cap->display, cap->proxy_window, motif_hints, motif_hints, 32, PropModeReplace, (unsigned char*)&hints, 5);

    XClassHint* classHint = XAllocClassHint();
    if (classHint) {
        classHint->res_name = (char*)"AES_Lacrima";
        classHint->res_class = (char*)"AES_Lacrima";
        XSetClassHint(cap->display, cap->proxy_window, classHint);
        XFree(classHint);
    }
    
    // Set both legacy WM_NAME and modern _NET_WM_NAME
    XStoreName(cap->display, cap->proxy_window, "AES Capture Surface");
    Atom net_wm_name = XInternAtom(cap->display, "_NET_WM_NAME", False);
    Atom utf8_string = XInternAtom(cap->display, "UTF8_STRING", False);
    const char* title = "AES Capture Surface";
    XChangeProperty(cap->display, cap->proxy_window, net_wm_name, utf8_string, 8, PropModeReplace, (const unsigned char*)title, strlen(title));

    // Explicitly set it as a Normal window so the portal definitely lists it
    Atom window_type = XInternAtom(cap->display, "_NET_WM_WINDOW_TYPE", False);
    Atom window_type_normal = XInternAtom(cap->display, "_NET_WM_WINDOW_TYPE_NORMAL", False);
    XChangeProperty(cap->display, cap->proxy_window, window_type, XA_ATOM, 32, PropModeReplace, (unsigned char*)&window_type_normal, 1);
    
    // Pin to background and hide from taskbar/pagers
    Atom wm_state = XInternAtom(cap->display, "_NET_WM_STATE", False);
    Atom wm_state_below = XInternAtom(cap->display, "_NET_WM_STATE_BELOW", False);
    Atom wm_state_skip_taskbar = XInternAtom(cap->display, "_NET_WM_STATE_SKIP_TASKBAR", False);
    Atom wm_state_skip_pager = XInternAtom(cap->display, "_NET_WM_STATE_SKIP_PAGER", False);
    Atom atoms[] = { wm_state_below, wm_state_skip_taskbar, wm_state_skip_pager };
    XChangeProperty(cap->display, cap->proxy_window, wm_state, XA_ATOM, 32, PropModeReplace, (unsigned char*)atoms, 3);

    // Restore full opacity as nearly-zero opacity can cause the compositor to skip rendering
    SetWindowOpacity(cap->display, cap->proxy_window, 0xFFFFFFFFUL);

    XLowerWindow(cap->display, cap->proxy_window);
    XMapWindow(cap->display, cap->proxy_window);
    XFlush(cap->display);
    
    SetWindowInputPassthrough(cap->display, cap->proxy_window, true);
    
    cap->host_geometry_dirty = 1;

    cap->use_pipewire = 1; // enabled; actual init happens in render thread when set_target is called
    cap->brightness = 1.0f;
    cap->saturation = 1.0f;
    cap->tint[0] = 1.0f;
    cap->tint[1] = 1.0f;
    cap->tint[2] = 1.0f;
    cap->tint[3] = 1.0f;
    cap->stretch = 3;
    cap->disable_vsync = 0;
    cap->shader_dirty = 1;
    cap->shader_u_tex = -1;
    cap->shader_u_brightness = -1;
    cap->shader_u_saturation = -1;
    cap->shader_u_tint = -1;
    cap->shader_u_source_size = -1;
    cap->shader_u_output_size = -1;
    cap->texture_params_initialized = 0;

    cap->damage_event_base = -1;
    cap->damage_error_base = -1;
    cap->damage = 0;
    XDamageQueryExtension(cap->display, &cap->damage_event_base, &cap->damage_error_base);

    cap->last_sample_time_ns = MonotonicNowNs();

    cap->stop_render_thread = 0;
    cap->render_thread_started = 1;
    // PipeWire is NOT initialized here — it requires a portal dialog and must
    // run on the render thread (not on Avalonia's UI thread) to avoid corrupting
    // Avalonia's GLX state. pw_init_requested is set by set_target() instead.
    pthread_create(&cap->render_thread, nullptr, RenderThreadMain, cap);
    LogNative("render thread started (PipeWire will be attempted on first set_target)");

    return cap;
}

void aes_linux_capture_destroy(LinuxCapture* cap)
{
    if (!cap)
        return;

    cap->stop_render_thread = 1;
    // Wake any waiting PipeWire frame delivery so the render thread can exit
    if (cap->backend_mode == BackendPipeWire)
    {
        pthread_mutex_lock(&cap->pw_frame_mutex);
        cap->pw_active = 0;
        pthread_cond_signal(&cap->pw_frame_cond);
        pthread_mutex_unlock(&cap->pw_frame_mutex);
    }
    // The render thread's poll() has a 4 ms timeout (XComposite path)
    // or PipeWire cond wait (PipeWire path); both exit within ~1 s.
    if (cap->render_thread_started)
        pthread_join(cap->render_thread, nullptr);

    if (cap->display)
    {
        pthread_mutex_lock(&cap->mutex);

        // Tear down the appropriate backend
        if (cap->backend_mode == BackendPipeWire)
             DestroyPipeWireBackend(cap);

        if (cap->backend_mode == BackendReparentFallback && cap->target != 0)
        {
            Window root = DefaultRootWindow(cap->display);
            XReparentWindow(cap->display, cap->target, root, 0, 0);
            XMapWindow(cap->display, cap->target);
            XFlush(cap->display);
        }

        if (cap->proxy_window)
        {
            XDestroyWindow(cap->display, cap->proxy_window);
            cap->proxy_window = 0;
        }

        RestoreTargetFromOffscreenIfNeeded(cap);

        if (cap->damage != 0)
        {
            XDamageDestroy(cap->display, cap->damage);
            cap->damage = 0;
        }

        CleanupGlObjects(cap);

        XCloseDisplay(cap->display);
        cap->display = nullptr;

        pthread_mutex_unlock(&cap->mutex);
    }

    pthread_mutex_destroy(&cap->mutex);
    free(cap);
}

void* aes_linux_capture_get_view(LinuxCapture* cap)
{
    return cap ? reinterpret_cast<void*>(cap->window) : nullptr;
}

void aes_linux_capture_set_use_pipewire(LinuxCapture* cap, int usePipeWire)
{
    if (!cap)
        return;

    pthread_mutex_lock(&cap->mutex);
    const int normalized = usePipeWire ? 1 : 0;
    if (cap->use_pipewire != normalized)
        cap->use_pipewire = normalized;
    pthread_mutex_unlock(&cap->mutex);
}

void aes_linux_capture_set_disable_vsync(LinuxCapture* cap, int disableVsync)
{
    if (!cap)
        return;

    pthread_mutex_lock(&cap->mutex);
    const int normalized = disableVsync ? 1 : 0;
    if (cap->disable_vsync != normalized)
    {
        cap->disable_vsync = normalized;
        LogNative("set_disable_vsync: %d (swap_control=%d)", cap->disable_vsync, cap->has_swap_control);
        cap->gpu_frame_pending = 1;
    }
    pthread_mutex_unlock(&cap->mutex);
}

void aes_linux_capture_set_shader_path(LinuxCapture* cap, const char* shaderPath)
{
    if (!cap)
        return;

    pthread_mutex_lock(&cap->mutex);
    if (!shaderPath)
        shaderPath = "";

    if (strncmp(cap->shader_path, shaderPath, sizeof(cap->shader_path) - 1) != 0)
    {
        strncpy(cap->shader_path, shaderPath, sizeof(cap->shader_path) - 1);
        cap->shader_path[sizeof(cap->shader_path) - 1] = '\0';
        cap->shader_dirty = 1;
        cap->gpu_frame_pending = 1;
    }
    pthread_mutex_unlock(&cap->mutex);
}

static Window ResolveTargetWindow(LinuxCapture* cap, int processId, const char* windowTitleHint)
{
    if (!cap || !cap->display)
        return 0;

    Window root = DefaultRootWindow(cap->display);

    Window target = 0;
    if (windowTitleHint && windowTitleHint[0] != '\0')
        target = FindWindowByPid(cap->display, root, processId, windowTitleHint, true);

    if (target == 0)
        target = FindWindowByPid(cap->display, root, processId, windowTitleHint, false);

    if (target == 0 && windowTitleHint && windowTitleHint[0] != '\0')
        target = FindWindowByTitle(cap->display, root, windowTitleHint);

    return target;
}

void aes_linux_capture_set_target(LinuxCapture* cap, int processId, const char* windowTitleHint)
{
    if (!cap || !cap->display)
        return;

    pthread_mutex_lock(&cap->mutex);

    Window root = DefaultRootWindow(cap->display);

    if (cap->backend_mode == BackendReparentFallback && cap->target != 0)
    {
        RestoreTargetFromOffscreenIfNeeded(cap);
        if (cap->damage != 0)
        {
            XDamageDestroy(cap->display, cap->damage);
            cap->damage = 0;
        }

        XReparentWindow(cap->display, cap->target, root, 0, 0);
        XMapWindow(cap->display, cap->target);
        XFlush(cap->display);
        cap->target = 0;
        cap->hidden_window = 0;
    }

    if (cap->backend_mode == BackendGpuComposite)
    {
        RestoreTargetFromOffscreenIfNeeded(cap);
        DestroyCompositeResources(cap);
        cap->target = 0;
        cap->hidden_window = 0;
    }

    cap->active = 0;
    cap->initializing = 1;
    cap->backend_mode = BackendNone;

    Window target = ResolveTargetWindow(cap, processId, windowTitleHint);
    if (target == 0)
    {
        cap->initializing = 0;
        // PipeWire doesn't need an X11 target — still request init if enabled
        if (cap->use_pipewire && !cap->pw_loop && !cap->pw_init_requested)
        {
            cap->pw_init_requested = 1;
            LogNative("set_target: no X11 window found; PW init still requested");
        }
        SetStatusText(cap, "No X11/XWayland target window found");
        LogNative("set_target failed: no target for pid=%d hint='%s'", processId, windowTitleHint ? windowTitleHint : "");
        pthread_mutex_unlock(&cap->mutex);
        return;
    }
    LogNative("set_target resolved target=0x%lx for pid=%d hint='%s'", target, processId, windowTitleHint ? windowTitleHint : "");

    bool gpuOk = false;
    if (cap->gl_supported)
        gpuOk = SetupCompositeTarget(cap, target);

    if (gpuOk)
    {
        cap->active = 1;
        cap->initializing = 0;
        cap->target = target;
        cap->fps = 0.0;
        cap->frame_time_ms = 0.0;
        cap->present_fps = 0.0;
        cap->present_frame_time_ms = 0.0;
        cap->fps_window_start_ns = 0;
        cap->fps_window_frames = 0;
        cap->source_fps = 0.0;
        cap->source_frame_time_ms = 0.0;
        cap->source_last_event_ns = MonotonicNowNs();
        cap->last_present_sample_ns = 0;
        cap->last_render_ns = 0;
        cap->gpu_frame_pending = 1;
        cap->shader_dirty = 1;
        if (cap->damage != 0)
        {
            XDamageDestroy(cap->display, cap->damage);
            cap->damage = 0;
        }
        if (cap->damage_event_base >= 0)
            cap->damage = XDamageCreate(cap->display, target, XDamageReportNonEmpty);

        SetStatusText(cap, "Capturing (X11/XWayland GPU composite)");
        LogNative("set_target success: GPU composite target=0x%lx", target);

        // Request PipeWire on the render thread so the picker appears now
        // (emulator is running at this point).
        if (cap->use_pipewire && !cap->pw_loop && !cap->pw_init_requested)
        {
            cap->pw_init_requested = 1;
            LogNative("set_target: PW init requested; portal picker will appear on render thread");
        }

        HideTargetOffscreenIfRequested(cap);
        XLowerWindow(cap->display, cap->proxy_window);
        XFlush(cap->display);
        pthread_mutex_unlock(&cap->mutex);
        return;
    }

    // fallback: current reparent behavior
    cap->target = target;
    cap->backend_mode = BackendReparentFallback;
    if (cap->backend_detail[0] == '\0')
        SetBackendDetail(cap, "OpenGL composite path unavailable");

    cap->fps = 0.0;
    cap->frame_time_ms = 0.0;
    cap->present_fps = 0.0;
    cap->present_frame_time_ms = 0.0;
    cap->source_fps = 0.0;
    cap->source_frame_time_ms = 0.0;
    cap->source_last_event_ns = MonotonicNowNs();
    cap->last_present_sample_ns = 0;
    cap->last_render_ns = 0;
    cap->gpu_frame_pending = 1;
    cap->shader_dirty = 1;
    cap->has_target_geometry = 0;
    cap->target_hidden_offscreen = 0;
    cap->hidden_window = 0;
    cap->target_saved_opacity_valid = 0;
    cap->target_skip_taskbar_applied = 0;
    cap->target_input_passthrough_applied = 0;

    XUnmapWindow(cap->display, target);
    Status status = XReparentWindow(cap->display, target, cap->window, 0, 0);
    LogNative("target 0x%lx reparented to cap->window 0x%lx (fallback), status=%d", target, cap->window, status);
    UpdateFallbackTargetGeometry(cap);
    XMapWindow(cap->display, target);
    XFlush(cap->display);

    if (cap->damage_event_base >= 0)
        cap->damage = XDamageCreate(cap->display, target, XDamageReportNonEmpty);

    cap->active = 1;
    cap->initializing = 0;
    SetGpuInfo(cap, "X11 Reparent (fallback)", "Linux");
    SetStatusText(cap, "Capturing (fallback: X11 reparent, render options limited)");
    pthread_mutex_unlock(&cap->mutex);
}

void aes_linux_capture_set_target_window(LinuxCapture* cap, Window target)
{
    if (!cap || !cap->display || target == 0)
        return;

    pthread_mutex_lock(&cap->mutex);

    if (cap->backend_mode == BackendReparentFallback && cap->target != 0)
    {
        RestoreTargetFromOffscreenIfNeeded(cap);
        if (cap->damage != 0)
        {
            XDamageDestroy(cap->display, cap->damage);
            cap->damage = 0;
        }

        XReparentWindow(cap->display, cap->target, DefaultRootWindow(cap->display), 0, 0);
        XMapWindow(cap->display, cap->target);
        XFlush(cap->display);
        cap->target = 0;
        cap->hidden_window = 0;
    }

    if (cap->backend_mode == BackendGpuComposite)
    {
        RestoreTargetFromOffscreenIfNeeded(cap);
        DestroyCompositeResources(cap);
        cap->target = 0;
        cap->hidden_window = 0;
    }

    cap->active = 0;
    cap->initializing = 1;
    cap->backend_mode = BackendNone;

    LogNative("set_target_window: target=0x%lx", target);

    bool gpuOk = false;
    if (cap->gl_supported)
        gpuOk = SetupCompositeTarget(cap, target);

    if (gpuOk)
    {
        cap->active = 1;
        cap->initializing = 0;
        cap->target = target;
        cap->fps = 0.0;
        cap->frame_time_ms = 0.0;
        cap->present_fps = 0.0;
        cap->present_frame_time_ms = 0.0;
        cap->fps_window_start_ns = 0;
        cap->fps_window_frames = 0;
        cap->source_fps = 0.0;
        cap->source_frame_time_ms = 0.0;
        cap->source_last_event_ns = MonotonicNowNs();
        cap->last_present_sample_ns = 0;
        cap->last_render_ns = 0;
        cap->gpu_frame_pending = 1;
        cap->shader_dirty = 1;
        if (cap->damage != 0)
        {
            XDamageDestroy(cap->display, cap->damage);
            cap->damage = 0;
        }
        if (cap->damage_event_base >= 0)
            cap->damage = XDamageCreate(cap->display, target, XDamageReportNonEmpty);

        SetStatusText(cap, "Capturing (X11/XWayland GPU composite)");
        LogNative("set_target_window success: GPU composite target=0x%lx", target);

        if (cap->use_pipewire && !cap->pw_loop && !cap->pw_init_requested)
        {
            cap->pw_init_requested = 1;
            LogNative("set_target_window: PW init requested");
        }

        HideTargetOffscreenIfRequested(cap);
        XLowerWindow(cap->display, cap->proxy_window);
        XFlush(cap->display);
        pthread_mutex_unlock(&cap->mutex);
        return;
    }

    // fallback
    cap->target = target;
    cap->backend_mode = BackendReparentFallback;
    cap->active = 1;
    cap->initializing = 0;
    XUnmapWindow(cap->display, target);
    XReparentWindow(cap->display, target, cap->window, 0, 0);
    UpdateFallbackTargetGeometry(cap);
    XMapWindow(cap->display, target);
    XFlush(cap->display);
    
    if (cap->damage_event_base >= 0)
        cap->damage = XDamageCreate(cap->display, target, XDamageReportNonEmpty);

    SetStatusText(cap, "Capturing (fallback: X11 reparent)");
    LogNative("set_target_window fallback: target=0x%lx", target);

    pthread_mutex_unlock(&cap->mutex);
}


void aes_linux_capture_stop(LinuxCapture* cap)
{
    if (!cap || !cap->display)
        return;

    pthread_mutex_lock(&cap->mutex);

    Window root = DefaultRootWindow(cap->display);

    if (cap->backend_mode == BackendPipeWire)
    {
        StopPipeWireBackend(cap);
        cap->pw_init_requested = 0;
    }
    else if (cap->backend_mode == BackendGpuComposite)
    {
        RestoreTargetFromOffscreenIfNeeded(cap);
        DestroyCompositeResources(cap);
        if (cap->damage != 0)
        {
            XDamageDestroy(cap->display, cap->damage);
            cap->damage = 0;
        }
    }
    else if (cap->backend_mode == BackendReparentFallback && cap->target != 0)
    {
        if (cap->damage != 0)
        {
            XDamageDestroy(cap->display, cap->damage);
            cap->damage = 0;
        }
        XReparentWindow(cap->display, cap->target, root, 0, 0);
        XMapWindow(cap->display, cap->target);
        XFlush(cap->display);
    }

    cap->target = 0;
    cap->active = 0;
    cap->initializing = 0;
    cap->backend_mode = BackendNone;
    cap->fps = 0.0;
    cap->frame_time_ms = 0.0;
    cap->present_fps = 0.0;
    cap->present_frame_time_ms = 0.0;
    cap->source_fps = 0.0;
    cap->source_frame_time_ms = 0.0;
    cap->source_last_event_ns = 0;
    cap->fps_window_start_ns = 0;
    cap->fps_window_frames = 0;
    cap->last_present_sample_ns = 0;
    cap->last_render_ns = 0;
    cap->gpu_frame_pending = 0;
    cap->has_target_geometry = 0;
    cap->target_hidden_offscreen = 0;
    cap->hidden_window = 0;
    cap->target_saved_opacity_valid = 0;
    cap->target_skip_taskbar_applied = 0;
    cap->target_input_passthrough_applied = 0;
    SetStatusText(cap, "Linux capture idle");

    pthread_mutex_unlock(&cap->mutex);
}

void aes_linux_capture_forward_focus(LinuxCapture* cap)
{
    if (!cap || !cap->display)
        return;

    pthread_mutex_lock(&cap->mutex);

    if (cap->target != 0)
    {
        XSetInputFocus(cap->display, cap->target, RevertToParent, CurrentTime);
        XRaiseWindow(cap->display, cap->target);
        XFlush(cap->display);
    }

    pthread_mutex_unlock(&cap->mutex);
}

void aes_linux_capture_set_stretch(LinuxCapture* cap, int stretch)
{
    if (!cap)
        return;

    pthread_mutex_lock(&cap->mutex);
    if (cap->stretch != stretch)
    {
        cap->stretch = stretch;
        LogNative("set_stretch: %d", cap->stretch);
        cap->gpu_frame_pending = 1;
        if (cap->backend_mode == BackendReparentFallback)
            UpdateFallbackTargetGeometry(cap);
    }
    pthread_mutex_unlock(&cap->mutex);
}

void aes_linux_capture_set_render_options(LinuxCapture* cap, float brightness, float saturation, float tintR, float tintG, float tintB, float tintA)
{
    if (!cap)
        return;

    pthread_mutex_lock(&cap->mutex);
    if (cap->brightness != brightness ||
        cap->saturation != saturation ||
        cap->tint[0] != tintR ||
        cap->tint[1] != tintG ||
        cap->tint[2] != tintB ||
        cap->tint[3] != tintA)
    {
        cap->brightness = brightness;
        cap->saturation = saturation;
        cap->tint[0] = tintR;
        cap->tint[1] = tintG;
        cap->tint[2] = tintB;
        cap->tint[3] = tintA;
        cap->gpu_frame_pending = 1;
    }
    pthread_mutex_unlock(&cap->mutex);
}

void aes_linux_capture_set_crop_insets(LinuxCapture* cap, int left, int top, int right, int bottom)
{
    if (!cap)
        return;

    pthread_mutex_lock(&cap->mutex);
    if (cap->crop[0] != left || cap->crop[1] != top || cap->crop[2] != right || cap->crop[3] != bottom)
    {
        cap->crop[0] = left;
        cap->crop[1] = top;
        cap->crop[2] = right;
        cap->crop[3] = bottom;
        cap->gpu_frame_pending = 1;
        if (cap->backend_mode == BackendReparentFallback)
            UpdateFallbackTargetGeometry(cap);
    }
    pthread_mutex_unlock(&cap->mutex);
}

void aes_linux_capture_set_capture_behavior(LinuxCapture* cap, int hide)
{
    if (!cap)
        return;

    pthread_mutex_lock(&cap->mutex);
    const int normalized = hide ? 1 : 0;
    if (cap->hide_target != normalized)
    {
        cap->hide_target = normalized;
        cap->gpu_frame_pending = 1;
    }
    pthread_mutex_unlock(&cap->mutex);
}

void aes_linux_capture_reveal_window(void* handle)
{
    Display* d = XOpenDisplay(nullptr);
    if (!d)
        return;

    Window w = reinterpret_cast<Window>(handle);
    XMapWindow(d, w);
    XRaiseWindow(d, w);
    XFlush(d);
    XCloseDisplay(d);
}

void aes_linux_capture_hide_window(void* handle)
{
    Display* d = XOpenDisplay(nullptr);
    if (!d)
        return;

    Window w = reinterpret_cast<Window>(handle);
    XUnmapWindow(d, w);
    XFlush(d);
    XCloseDisplay(d);
}

void* aes_linux_capture_find_window_by_pid(int pid, const char* titleHint)
{
    Display* d = XOpenDisplay(nullptr);
    if (!d)
        return nullptr;

    Window root = RootWindow(d, DefaultScreen(d));
    Window found = FindWindowByPid(d, root, pid, titleHint, false);
    XCloseDisplay(d);
    return reinterpret_cast<void*>(found);
}

static void RefreshStatusForMode(LinuxCapture* cap)
{
    if (!cap)
        return;

    char status[512];

    if (!cap->active)
    {
        snprintf(status, sizeof(status), "Linux capture idle");
    }
    else if (cap->backend_mode == BackendGpuComposite)
    {
        if (!cap->has_swap_control)
        {
            if (cap->source_fps > 0.0)
                snprintf(status, sizeof(status),
                    "Capturing (X11/XWayland GPU composite, VSync control unavailable) - %.1f fps (source %.1f)",
                    cap->fps,
                    cap->source_fps);
            else
                snprintf(status, sizeof(status),
                    "Capturing (X11/XWayland GPU composite, VSync control unavailable) - %.1f fps",
                    cap->fps);
        }
        else if (!cap->disable_vsync && cap->has_swap_control_tear)
        {
            if (cap->source_fps > 0.0)
                snprintf(status, sizeof(status),
                    "Capturing (X11/XWayland GPU composite, adaptive VSync/VRR) - %.1f fps (source %.1f)",
                    cap->fps,
                    cap->source_fps);
            else
                snprintf(status, sizeof(status),
                    "Capturing (X11/XWayland GPU composite, adaptive VSync/VRR) - %.1f fps",
                    cap->fps);
        }
        else
        {
            if (cap->source_fps > 0.0)
                snprintf(status, sizeof(status),
                    "Capturing (X11/XWayland GPU composite, VSync %s) - %.1f fps (source %.1f)",
                    cap->disable_vsync ? "off" : "on",
                    cap->fps,
                    cap->source_fps);
            else
                snprintf(status, sizeof(status),
                    "Capturing (X11/XWayland GPU composite, VSync %s) - %.1f fps",
                    cap->disable_vsync ? "off" : "on",
                    cap->fps);
        }
    }
    else if (cap->backend_mode == BackendReparentFallback)
    {
        if (cap->backend_detail[0] != '\0')
        {
            if (cap->source_fps > 0.0)
                snprintf(status, sizeof(status),
                    "Capturing (fallback: X11 reparent, render options limited | reason: %s) - %.1f fps (source %.1f)",
                    cap->backend_detail,
                    cap->fps,
                    cap->source_fps);
            else
                snprintf(status, sizeof(status),
                    "Capturing (fallback: X11 reparent, render options limited | reason: %s) - %.1f fps",
                    cap->backend_detail,
                    cap->fps);
        }
        else
        {
            if (cap->source_fps > 0.0)
                snprintf(status, sizeof(status), "Capturing (fallback: X11 reparent, render options limited) - %.1f fps (source %.1f)", cap->fps, cap->source_fps);
            else
                snprintf(status, sizeof(status), "Capturing (fallback: X11 reparent, render options limited) - %.1f fps", cap->fps);
        }
    }
    else if (cap->backend_mode == BackendPipeWire)
    {
        if (cap->fps > 0.0)
            snprintf(status, sizeof(status), "Capturing (PipeWire DMA-BUF) - %.1f fps", cap->fps);
        else
            snprintf(status, sizeof(status), "Capturing (PipeWire DMA-BUF)");
    }
    else
    {
        snprintf(status, sizeof(status), "Capturing (unknown backend)");
    }

    strncpy(cap->status_text, status, sizeof(cap->status_text) - 1);
    cap->status_text[sizeof(cap->status_text) - 1] = '\0';
}

static void UpdateMetrics(LinuxCapture* cap)
{
    if (!cap || !cap->display)
        return;

    if (pthread_mutex_trylock(&cap->mutex) != 0)
        return;

    if (cap->backend_mode == BackendReparentFallback || cap->backend_mode == BackendGpuComposite)
    {
        if (cap->backend_mode == BackendReparentFallback)
            PumpXEventsLocked(cap);

        const uint64_t now = MonotonicNowNs();
        if (cap->source_last_event_ns != 0)
        {
            const double elapsed = static_cast<double>(now - cap->source_last_event_ns) / 1000000000.0;
            if (elapsed >= 0.05)
            {
                cap->source_fps *= 0.94;
                if (cap->source_fps < 0.1)
                    cap->source_fps = 0.0;
                cap->source_frame_time_ms = cap->source_fps > 0.0 ? 1000.0 / cap->source_fps : 0.0;
                cap->source_last_event_ns = now;
            }
        }

        if (cap->last_present_sample_ns != 0)
        {
            const double elapsedPresent = static_cast<double>(now - cap->last_present_sample_ns) / 1000000000.0;
            if (elapsedPresent >= 0.05 && cap->present_fps > 0.0)
            {
                cap->present_fps *= 0.94;
                if (cap->present_fps < 0.1)
                    cap->present_fps = 0.0;
                cap->present_frame_time_ms = cap->present_fps > 0.0 ? 1000.0 / cap->present_fps : 0.0;
                cap->last_present_sample_ns = now;
            }
        }

        cap->fps = cap->present_fps;
        cap->frame_time_ms = cap->present_frame_time_ms;
    }

    RefreshStatusForMode(cap);

    pthread_mutex_unlock(&cap->mutex);
}

int aes_linux_capture_is_active(LinuxCapture* cap)
{
    return cap ? cap->active : 0;
}

int aes_linux_capture_is_initializing(LinuxCapture* cap)
{
    return cap ? cap->initializing : 0;
}

double aes_linux_capture_get_fps(LinuxCapture* cap)
{
    if (!cap)
        return 0.0;
    if (pthread_mutex_trylock(&cap->mutex) != 0)
        return cap->fps;
    const double fps = cap->fps;
    pthread_mutex_unlock(&cap->mutex);
    return fps;
}

double aes_linux_capture_get_frame_time_ms(LinuxCapture* cap)
{
    if (!cap)
        return 0.0;
    if (pthread_mutex_trylock(&cap->mutex) != 0)
        return cap->frame_time_ms;
    const double frameTime = cap->frame_time_ms;
    pthread_mutex_unlock(&cap->mutex);
    return frameTime;
}

int aes_linux_capture_get_status_text(LinuxCapture* cap, char* buffer, int size)
{
    if (!cap || !buffer || size <= 0)
        return 0;

    UpdateMetrics(cap);

    if (pthread_mutex_trylock(&cap->mutex) != 0)
        return 0;
    strncpy(buffer, cap->status_text, size - 1);
    buffer[size - 1] = '\0';
    pthread_mutex_unlock(&cap->mutex);

    return static_cast<int>(strlen(buffer));
}

int aes_linux_capture_get_gpu_renderer(LinuxCapture* cap, char* buffer, int size)
{
    if (!cap || !buffer || size <= 0)
        return 0;

    if (pthread_mutex_trylock(&cap->mutex) != 0)
        return 0;
    strncpy(buffer, cap->gpu_renderer, size - 1);
    buffer[size - 1] = '\0';
    pthread_mutex_unlock(&cap->mutex);

    return static_cast<int>(strlen(buffer));
}

int aes_linux_capture_get_gpu_vendor(LinuxCapture* cap, char* buffer, int size)
{
    if (!cap || !buffer || size <= 0)
        return 0;

    if (pthread_mutex_trylock(&cap->mutex) != 0)
        return 0;
    strncpy(buffer, cap->gpu_vendor, size - 1);
    buffer[size - 1] = '\0';
    pthread_mutex_unlock(&cap->mutex);

    return static_cast<int>(strlen(buffer));
}

} // extern "C"

