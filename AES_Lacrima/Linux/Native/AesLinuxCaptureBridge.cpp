#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/Xatom.h>
#include <X11/extensions/Xcomposite.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// Mirroring the structure expected by the C# side
typedef struct {
    Display* display;
    Window window;
    Window target;
    int active;
    int initializing;
    float brightness;
    float saturation;
    float tint[4];
    int stretch;
    int crop[4];
    int hide_target;
} LinuxCapture;

extern "C" {

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

    unsigned int width = attributes.width > 0 ? (unsigned int)attributes.width : 1;
    unsigned int height = attributes.height > 0 ? (unsigned int)attributes.height : 1;
    XMoveResizeWindow(cap->display, cap->target, 0, 0, width, height);
    XFlush(cap->display);
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

    cap->brightness = 1.0f;
    cap->saturation = 1.0f;
    for(int i=0; i<4; i++) cap->tint[i] = 1.0f;

    return cap;
}

void aes_linux_capture_destroy(LinuxCapture* cap) {
    if (!cap) return;
    if (cap->display) {
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
        return;
    }

    if (cap->target != 0)
    {
        XReparentWindow(cap->display, cap->target, root, 0, 0);
        XMapWindow(cap->display, cap->target);
        XFlush(cap->display);
    }

    cap->target = target;
    cap->initializing = 1;

    XUnmapWindow(cap->display, target);
    XReparentWindow(cap->display, target, cap->window, 0, 0);
    ResizeTargetToHost(cap);
    XMapWindow(cap->display, target);
    XFlush(cap->display);

    cap->active = 1;
    cap->initializing = 0;
}

void aes_linux_capture_stop(LinuxCapture* cap) {
    if (!cap)
        return;

    if (cap->display && cap->target != 0)
    {
        Window root = DefaultRootWindow(cap->display);
        XReparentWindow(cap->display, cap->target, root, 0, 0);
        XMapWindow(cap->display, cap->target);
        XFlush(cap->display);
        cap->target = 0;
    }

    cap->active = 0;
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
    ResizeTargetToHost(cap);
    return cap ? cap->initializing : 0;
}
double aes_linux_capture_get_fps(LinuxCapture* cap) { return 60.0; }
double aes_linux_capture_get_frame_time_ms(LinuxCapture* cap) { return 16.6; }

int aes_linux_capture_get_status_text(LinuxCapture* cap, char* buffer, int size) {
    if (!cap) return 0;
    const char* txt = cap->active ? "Capturing (X11)" : "Linux capture idle";
    strncpy(buffer, txt, size);
    return strlen(buffer);
}

int aes_linux_capture_get_gpu_renderer(LinuxCapture* cap, char* buffer, int size) {
    strncpy(buffer, "X11/OpenGL", size);
    return strlen(buffer);
}

int aes_linux_capture_get_gpu_vendor(LinuxCapture* cap, char* buffer, int size) {
    strncpy(buffer, "Linux", size);
    return strlen(buffer);
}

}
