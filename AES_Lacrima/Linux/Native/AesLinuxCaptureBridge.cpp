#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/Xatom.h>
#include <X11/extensions/Xcomposite.h>
#include <X11/extensions/Xdamage.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

// Mirroring the structure expected by the C# side
typedef struct {
    Display* display;
    Window window;
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
    int damage_event_base;
    int damage_error_base;
    Damage damage;
    double fps;
    double frame_time_ms;
    uint64_t last_sample_time_ns;
    uint64_t last_damage_event_ns;
    int last_target_x;
    int last_target_y;
    unsigned int last_target_w;
    unsigned int last_target_h;
    int has_target_geometry;
} LinuxCapture;

extern "C" {

static uint64_t MonotonicNowNs()
{
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<uint64_t>(ts.tv_sec) * 1000000000ULL + static_cast<uint64_t>(ts.tv_nsec);
}

static void UpdateMetrics(LinuxCapture* cap);

static Window GetParentWindow(Display* display, void* parentHandle)
{
    if (!display || parentHandle == nullptr)
        return RootWindow(display, DefaultScreen(display));

    return reinterpret_cast<Window>(parentHandle);
}

static bool GetWindowTitle(Display* display, Window window, char* buffer, int size)
{
    if (!display || !buffer || size <= 0)
    {
        return false;
    }

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
                if (XmbTextPropertyToTextList(display, &prop, &list, &count) >= Success && list != nullptr && count > 0 && list[0])
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
                           &actualType, &actualFormat, &nitems, &bytesAfter, &prop) == Success &&
        prop != nullptr)
    {
        uint32_t pidValue = *reinterpret_cast<uint32_t*>(prop);
        pid_t pid = static_cast<pid_t>(pidValue);
        XFree(prop);
        return pid;
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

static void ResizeTargetToHost(LinuxCapture* cap)
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

LinuxCapture* aes_linux_capture_create(void* parentHandle) {
    LinuxCapture* cap = (LinuxCapture*)calloc(1, sizeof(LinuxCapture));
    cap->display = XOpenDisplay(NULL);
    if (!cap->display) {
        free(cap);
        return NULL;
    }

    Window parent = GetParentWindow(cap->display, parentHandle);
    cap->window = XCreateSimpleWindow(cap->display, parent,
                                      0, 0, 1, 1, 0,
                                      BlackPixel(cap->display, DefaultScreen(cap->display)),
                                      BlackPixel(cap->display, DefaultScreen(cap->display)));

    XSelectInput(cap->display, cap->window, StructureNotifyMask);
    XMapWindow(cap->display, cap->window);
    XFlush(cap->display);

    cap->use_pipewire = 1; // Default to PipeWire
    cap->brightness = 1.0f;
    cap->saturation = 1.0f;
    cap->damage_event_base = -1;
    cap->damage_error_base = -1;
    cap->damage = 0;
    cap->fps = 0.0;
    cap->frame_time_ms = 0.0;
    cap->last_sample_time_ns = MonotonicNowNs();
    cap->last_damage_event_ns = 0;
    cap->has_target_geometry = 0;
    cap->last_target_x = 0;
    cap->last_target_y = 0;
    cap->last_target_w = 0;
    cap->last_target_h = 0;
    for(int i=0; i<4; i++) cap->tint[i] = 1.0f;
    XDamageQueryExtension(cap->display, &cap->damage_event_base, &cap->damage_error_base);

    return cap;
}

void aes_linux_capture_set_use_pipewire(LinuxCapture* cap, int use) {
    if (cap) cap->use_pipewire = use;
}

void aes_linux_capture_destroy(LinuxCapture* cap) {
    if (!cap) return;
    if (cap->display) {
        if (cap->damage != 0) {
            XDamageDestroy(cap->display, cap->damage);
            cap->damage = 0;
        }
        XDestroyWindow(cap->display, cap->window);
        XCloseDisplay(cap->display);
    }
    free(cap);
}

void* aes_linux_capture_get_view(LinuxCapture* cap) {
    return cap ? (void*)cap->window : NULL;
}

void aes_linux_capture_set_target(LinuxCapture* cap, int processId, const char* windowTitleHint) {
    if (!cap || !cap->display)
        return;

    Window root = DefaultRootWindow(cap->display);
    Window target = 0;

    if (windowTitleHint != nullptr && windowTitleHint[0] != '\0')
    {
        target = FindWindowByPid(cap->display, root, processId, windowTitleHint, true);
    }

    if (target == 0)
    {
        target = FindWindowByPid(cap->display, root, processId, windowTitleHint, false);
    }

    if (target == 0 && windowTitleHint != nullptr && windowTitleHint[0] != '\0')
        target = FindWindowByTitle(cap->display, root, windowTitleHint);

    if (target == 0)
        return;

    if (cap->target == target)
    {
        cap->active = 1;
        UpdateMetrics(cap);
        return;
    }

    if (cap->target != 0)
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

    cap->target = target;
    cap->initializing = 1;
    cap->fps = 0.0;
    cap->frame_time_ms = 0.0;
    cap->last_sample_time_ns = MonotonicNowNs();
    cap->last_damage_event_ns = 0;
    cap->has_target_geometry = 0;

    XUnmapWindow(cap->display, target);
    XReparentWindow(cap->display, target, cap->window, 0, 0);
    ResizeTargetToHost(cap);
    XMapWindow(cap->display, target);
    XFlush(cap->display);

    if (cap->damage_event_base >= 0)
    {
        cap->damage = XDamageCreate(cap->display, target, XDamageReportNonEmpty);
    }

    cap->active = 1;
    cap->initializing = 0;
}

void aes_linux_capture_stop(LinuxCapture* cap) {
    if (!cap)
        return;

    if (cap->display && cap->target != 0)
    {
        Window root = DefaultRootWindow(cap->display);
        if (cap->damage != 0)
        {
            XDamageDestroy(cap->display, cap->damage);
            cap->damage = 0;
        }
        XReparentWindow(cap->display, cap->target, root, 0, 0);
        XMapWindow(cap->display, cap->target);
        XFlush(cap->display);
        cap->target = 0;
    }

    cap->active = 0;
    cap->initializing = 0;
    cap->fps = 0.0;
    cap->frame_time_ms = 0.0;
    cap->last_sample_time_ns = MonotonicNowNs();
    cap->last_damage_event_ns = 0;
    cap->has_target_geometry = 0;
}

void aes_linux_capture_forward_focus(LinuxCapture* cap) {
    if (!cap || !cap->target) return;
    XSetInputFocus(cap->display, cap->target, RevertToParent, CurrentTime);
    XRaiseWindow(cap->display, cap->target);
    XFlush(cap->display);
}

void aes_linux_capture_set_stretch(LinuxCapture* cap, int stretch) {
    if (cap) {
        cap->stretch = stretch;
        ResizeTargetToHost(cap);
    }
}

void aes_linux_capture_set_render_options(LinuxCapture* cap, float b, float s, float tr, float tg, float tb, float ta) {
    if (!cap) return;
    cap->brightness = b;
    cap->saturation = s;
    cap->tint[0] = tr; cap->tint[1] = tg; cap->tint[2] = tb; cap->tint[3] = ta;
}

void aes_linux_capture_set_crop_insets(LinuxCapture* cap, int l, int t, int r, int b) {
    if (!cap) return;
    cap->crop[0] = l; cap->crop[1] = t; cap->crop[2] = r; cap->crop[3] = b;
    ResizeTargetToHost(cap);
}

void aes_linux_capture_set_capture_behavior(LinuxCapture* cap, int hide) {
    if (cap) cap->hide_target = hide;
}

void aes_linux_capture_reveal_window(void* handle) {
    Display* d = XOpenDisplay(NULL);
    if (!d) return;
    Window w = (Window)handle;
    XMapWindow(d, w);
    XRaiseWindow(d, w);
    XFlush(d);
    XCloseDisplay(d);
}

void aes_linux_capture_hide_window(void* handle) {
    Display* d = XOpenDisplay(NULL);
    if (!d) return;
    Window w = (Window)handle;
    XUnmapWindow(d, w);
    XFlush(d);
    XCloseDisplay(d);
}

void* aes_linux_capture_find_window_by_pid(int pid, const char* titleHint) {
    Display* d = XOpenDisplay(NULL);
    if (!d) return nullptr;
    Window root = RootWindow(d, DefaultScreen(d));
    Window found = FindWindowByPid(d, root, pid, titleHint, false);
    XCloseDisplay(d);
    return (void*)found;
}

int aes_linux_capture_is_active(LinuxCapture* cap) { return cap ? cap->active : 0; }
int aes_linux_capture_is_initializing(LinuxCapture* cap) {
    UpdateMetrics(cap);
    return cap ? cap->initializing : 0;
}
double aes_linux_capture_get_fps(LinuxCapture* cap) {
    UpdateMetrics(cap);
    return cap ? cap->fps : 0.0;
}
double aes_linux_capture_get_frame_time_ms(LinuxCapture* cap) {
    UpdateMetrics(cap);
    return cap ? cap->frame_time_ms : 0.0;
}

int aes_linux_capture_get_status_text(LinuxCapture* cap, char* buffer, int size) {
    if (!cap) return 0;
    const char* mode = cap->use_pipewire ? "X11 embed (PipeWire toggle not implemented)" : "X11 embed";
    char txt[256];
    if (cap->active) {
        snprintf(txt, sizeof(txt), "Capturing (%s) - %.1f updates/s est", mode, cap->fps);
    } else {
        snprintf(txt, sizeof(txt), "Linux capture idle (%s)", mode);
    }
    strncpy(buffer, txt, size);
    return strlen(buffer);
}

int aes_linux_capture_get_gpu_renderer(LinuxCapture* cap, char* buffer, int size) {
    strncpy(buffer, "X11 Reparent (zero-copy embed)", size);
    return strlen(buffer);
}

int aes_linux_capture_get_gpu_vendor(LinuxCapture* cap, char* buffer, int size) {
    strncpy(buffer, "Linux", size);
    return strlen(buffer);
}

static void UpdateMetrics(LinuxCapture* cap)
{
    if (!cap || !cap->display)
        return;

    while (XPending(cap->display) > 0)
    {
        XEvent ev{};
        XNextEvent(cap->display, &ev);

        if (ev.type == ConfigureNotify && ev.xconfigure.window == cap->window)
        {
            ResizeTargetToHost(cap);
            continue;
        }

        if (cap->damage != 0 && cap->damage_event_base >= 0 && ev.type == cap->damage_event_base + XDamageNotify)
        {
            XDamageNotifyEvent* damageEv = reinterpret_cast<XDamageNotifyEvent*>(&ev);
            if (damageEv->damage == cap->damage)
            {
                XDamageSubtract(cap->display, cap->damage, None, None);

                const uint64_t nowEvent = MonotonicNowNs();
                if (cap->last_damage_event_ns != 0 && nowEvent > cap->last_damage_event_ns)
                {
                    const double dt = static_cast<double>(nowEvent - cap->last_damage_event_ns) / 1000000000.0;
                    if (dt > 0.0)
                    {
                        const double instantFps = 1.0 / dt;
                        cap->fps = cap->fps > 0.0
                            ? (cap->fps * 0.80) + (instantFps * 0.20)
                            : instantFps;
                        cap->frame_time_ms = 1000.0 / cap->fps;
                    }
                }
                cap->last_damage_event_ns = nowEvent;
            }
        }
    }

    const uint64_t now = MonotonicNowNs();
    if (cap->last_sample_time_ns == 0)
    {
        cap->last_sample_time_ns = now;
        return;
    }

    const double elapsedSeconds = static_cast<double>(now - cap->last_sample_time_ns) / 1000000000.0;
    if (elapsedSeconds < 0.05)
        return;

    if (cap->active)
    {
        cap->fps *= 0.94;
        if (cap->fps < 0.1)
            cap->fps = 0.0;
        cap->frame_time_ms = cap->fps > 0.0 ? (1000.0 / cap->fps) : 0.0;
    }
    else
    {
        cap->fps = 0.0;
        cap->frame_time_ms = 0.0;
    }
    cap->last_sample_time_ns = now;
}

}
