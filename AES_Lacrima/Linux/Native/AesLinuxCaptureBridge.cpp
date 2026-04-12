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

#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
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
    BackendNone = 0,
    BackendGpuComposite = 1,
    BackendReparentFallback = 2
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

    uint64_t fps_window_start_ns;
    int fps_window_frames;
    uint64_t source_last_event_ns;
    uint64_t last_present_sample_ns;
    uint64_t last_render_ns;
    int gpu_frame_pending;
} LinuxCapture;

extern "C" {

static pthread_once_t g_x11_threads_once = PTHREAD_ONCE_INIT;

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

    if (cap->glx_context)
        glXMakeCurrent(cap->display, None, nullptr);

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

    if (cap->target_saved_geometry_valid)
    {
        XMoveResizeWindow(
            cap->display,
            restoreWindow,
            cap->target_saved_x,
            cap->target_saved_y,
            cap->target_saved_w,
            cap->target_saved_h);
    }
    else
    {
        XMoveWindow(cap->display, restoreWindow, 0, 0);
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

    // Prefer stable VSync=1 over adaptive -1 to avoid half-rate fallback jitter.
    int interval = disableVsync ? 0 : 1;
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

    if (glXMakeCurrent(cap->display, cap->window, cap->glx_context) != True)
        return;

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

    cap->glx_release_tex_image_ext(cap->display, cap->glx_pixmap, GLX_FRONT_LEFT_EXT);

    glXSwapBuffers(cap->display, cap->window);

    cap->last_render_ns = MonotonicNowNs();
    SamplePresentMetrics(cap, cap->last_render_ns);
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

        if (cap->backend_mode == BackendGpuComposite)
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
                double minAcceptedNs = 8000000.0; // 8 ms baseline duplicate filter
                if (cap->source_frame_time_ms > 0.0)
                {
                    const double expectedNs = cap->source_frame_time_ms * 1000000.0;
                    minAcceptedNs = std::max(5000000.0, expectedNs * 0.55);
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
}

static void* RenderThreadMain(void* arg)
{
    LinuxCapture* cap = static_cast<LinuxCapture*>(arg);
    if (!cap)
        return nullptr;

    while (!cap->stop_render_thread)
    {
        pthread_mutex_lock(&cap->mutex);
        PumpXEventsLocked(cap);

        bool shouldRender = cap->active && cap->backend_mode == BackendGpuComposite && cap->target != 0;
        bool disableVsync = cap->disable_vsync != 0;
        bool hasSwapControl = cap->has_swap_control != 0;
        bool pendingFrame = cap->gpu_frame_pending != 0;

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

        cap->fps = StabilizeReportedFps(cap->source_fps);
        cap->frame_time_ms = cap->fps > 0.0 ? 1000.0 / cap->fps : 0.0;

        // Keep periodic presents for compositor pacing.
        // Do not fall back to 33ms pacing while active: that feels like forced 30fps.
        const uint64_t periodicNs = disableVsync ? 8333333ULL : 16666666ULL;
        bool forcePeriodic = shouldRender && cap->source_fps > 0.0 && (cap->last_render_ns == 0 || (now - cap->last_render_ns) > periodicNs);
        bool renderNow = shouldRender && (pendingFrame || forcePeriodic);
        if (renderNow)
            cap->gpu_frame_pending = 0;
        pthread_mutex_unlock(&cap->mutex);

        if (renderNow)
        {
            pthread_mutex_lock(&cap->mutex);
            RenderCompositeFrame(cap);
            pthread_mutex_unlock(&cap->mutex);

            if (disableVsync)
                usleep(1000);
            else if (!hasSwapControl)
                usleep(2000);
            else
                usleep(500);
        }
        else
        {
            usleep(1000);
        }
    }

    return nullptr;
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

    cap->display = XOpenDisplay(nullptr);
    if (!cap->display)
    {
        free(cap);
        return nullptr;
    }
    LogNative("capture create: display opened");

    cap->screen = DefaultScreen(cap->display);
    pthread_mutex_init(&cap->mutex, nullptr);

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

    cap->use_pipewire = 0; // X11/XWayland implementation
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
    pthread_create(&cap->render_thread, nullptr, RenderThreadMain, cap);
    LogNative("render thread started");

    return cap;
}

void aes_linux_capture_destroy(LinuxCapture* cap)
{
    if (!cap)
        return;

    cap->stop_render_thread = 1;
    if (cap->render_thread_started)
        pthread_join(cap->render_thread, nullptr);

    if (cap->display)
    {
        pthread_mutex_lock(&cap->mutex);

        if (cap->backend_mode == BackendReparentFallback && cap->target != 0)
        {
            Window root = DefaultRootWindow(cap->display);
            XReparentWindow(cap->display, cap->target, root, 0, 0);
            XMapWindow(cap->display, cap->target);
            XFlush(cap->display);
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
        if (cap->damage != 0)
        {
            XDamageDestroy(cap->display, cap->damage);
            cap->damage = 0;
        }
        if (cap->damage_event_base >= 0)
            cap->damage = XDamageCreate(cap->display, target, XDamageReportNonEmpty);
        HideTargetOffscreenIfRequested(cap);
        SetStatusText(cap, "Capturing (X11/XWayland GPU composite)");
        LogNative("set_target success: GPU composite target=0x%lx", target);
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
    cap->has_target_geometry = 0;
    cap->target_hidden_offscreen = 0;
    cap->hidden_window = 0;
    cap->target_saved_opacity_valid = 0;
    cap->target_skip_taskbar_applied = 0;
    cap->target_input_passthrough_applied = 0;

    XUnmapWindow(cap->display, target);
    XReparentWindow(cap->display, target, cap->window, 0, 0);
    UpdateFallbackTargetGeometry(cap);
    XMapWindow(cap->display, target);
    XFlush(cap->display);

    if (cap->damage_event_base >= 0)
        cap->damage = XDamageCreate(cap->display, target, XDamageReportNonEmpty);

    cap->active = 1;
    cap->initializing = 0;
    SetGpuInfo(cap, "X11 Reparent (fallback)", "Linux");
    SetStatusText(cap, "Capturing (fallback: X11 reparent, render options limited)");
    LogNative("set_target fallback: target=0x%lx reason='%s'", target, cap->backend_detail);

    pthread_mutex_unlock(&cap->mutex);
}

void aes_linux_capture_stop(LinuxCapture* cap)
{
    if (!cap || !cap->display)
        return;

    pthread_mutex_lock(&cap->mutex);

    Window root = DefaultRootWindow(cap->display);

    if (cap->backend_mode == BackendGpuComposite)
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

}
