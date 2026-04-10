#import <AppKit/AppKit.h>
#import <AVFoundation/AVFoundation.h>
#import <CoreGraphics/CoreGraphics.h>
#import <QuartzCore/QuartzCore.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>

static int write_utf8_string(NSString *value, char *buffer, int bufferChars)
{
    if (buffer == NULL || bufferChars <= 0) {
        return 0;
    }

    NSString *text = value ?: @"";
    NSData *data = [text dataUsingEncoding:NSUTF8StringEncoding allowLossyConversion:YES];
    NSUInteger length = MIN((NSUInteger)(bufferChars - 1), data.length);
    memcpy(buffer, data.bytes, length);
    buffer[length] = 0;
    return (int)length;
}

@interface AesCaptureView : NSView
@property(nonatomic, strong) AVSampleBufferDisplayLayer *displayLayer;
@end

@implementation AesCaptureView
- (instancetype)initWithFrame:(NSRect)frameRect
{
    self = [super initWithFrame:frameRect];
    if (self != nil) {
        self.wantsLayer = YES;
        self.layer = [CALayer layer];
        self.layer.backgroundColor = NSColor.blackColor.CGColor;

        _displayLayer = [AVSampleBufferDisplayLayer layer];
        _displayLayer.videoGravity = AVLayerVideoGravityResizeAspectFill;
        _displayLayer.backgroundColor = NSColor.blackColor.CGColor;
        _displayLayer.frame = self.bounds;
        _displayLayer.autoresizingMask = kCALayerWidthSizable | kCALayerHeightSizable;
        [self.layer addSublayer:_displayLayer];
    }

    return self;
}

- (void)layout
{
    [super layout];
    self.displayLayer.frame = self.bounds;
}
@end

API_AVAILABLE(macos(12.3))
@interface AesMacCaptureController : NSObject<SCStreamOutput>
@property(nonatomic, strong) AesCaptureView *view;
@property(nonatomic, strong) dispatch_queue_t captureQueue;
@property(nonatomic, strong) SCStream *stream;
@property(nonatomic, assign) pid_t targetPid;
@property(nonatomic, copy) NSString *targetWindowTitleHint;
@property(nonatomic, assign) NSInteger frameCount;
@property(nonatomic, assign) CFTimeInterval lastFrameTimestamp;
@property(nonatomic, assign) double fps;
@property(nonatomic, assign) double frameTimeMs;
@property(nonatomic, assign) BOOL active;
@property(nonatomic, assign) BOOL initializing;
@property(nonatomic, copy) NSString *statusText;
@property(nonatomic, copy) NSString *gpuRenderer;
@property(nonatomic, copy) NSString *gpuVendor;
@property(nonatomic, assign) NSInteger stretchMode;
@property(nonatomic, assign) NSEdgeInsets cropInsets;
@end

API_AVAILABLE(macos(12.3))
@implementation AesMacCaptureController
- (instancetype)init
{
    self = [super init];
    if (self != nil) {
        _view = [[AesCaptureView alloc] initWithFrame:NSMakeRect(0, 0, 1, 1)];
        _captureQueue = dispatch_queue_create("com.aruantec.aes.capture.mac", DISPATCH_QUEUE_SERIAL);
        _statusText = @"ScreenCaptureKit idle";
        _gpuRenderer = @"AVSampleBufferDisplayLayer";
        _gpuVendor = @"Apple";
        _targetWindowTitleHint = @"";
        _stretchMode = 2;
        _cropInsets = NSEdgeInsetsMake(0, 0, 0, 0);
    }

    return self;
}

- (void)setStretchMode:(NSInteger)stretchMode
{
    _stretchMode = stretchMode;
    switch (stretchMode) {
        case 0:
            self.view.displayLayer.videoGravity = AVLayerVideoGravityResize;
            break;
        case 1:
            self.view.displayLayer.videoGravity = AVLayerVideoGravityResizeAspect;
            break;
        default:
            self.view.displayLayer.videoGravity = AVLayerVideoGravityResizeAspectFill;
            break;
    }
}

- (void)startCaptureForPid:(pid_t)pid
{
    [self stopCapture];
    self.targetPid = pid;

    if (@available(macOS 12.3, *)) {
        if (!CGPreflightScreenCaptureAccess()) {
            self.initializing = NO;
            self.active = NO;
            self.statusText = @"Screen Recording permission required. Grant access and restart the app.";
            CGRequestScreenCaptureAccess();
            return;
        }

        self.initializing = YES;
        self.active = NO;
        self.statusText = @"Finding emulator window...";
        [self discoverAndStartStream];
    } else {
        self.statusText = @"ScreenCaptureKit requires macOS 12.3 or newer.";
    }
}

- (void)discoverAndStartStream API_AVAILABLE(macos(12.3))
{
    pid_t expectedPid = self.targetPid;
    __weak typeof(self) weakSelf = self;
    [SCShareableContent getShareableContentExcludingDesktopWindows:YES
                                             onScreenWindowsOnly:YES
                                               completionHandler:^(SCShareableContent *content, NSError *error) {
        dispatch_async(dispatch_get_main_queue(), ^{
            typeof(self) strongSelf = weakSelf;
            if (strongSelf == nil || strongSelf.targetPid != expectedPid || expectedPid == 0) {
                return;
            }

            if (error != nil) {
                strongSelf.initializing = NO;
                strongSelf.active = NO;
                strongSelf.statusText = [NSString stringWithFormat:@"Failed to enumerate windows: %@", error.localizedDescription];
                return;
            }

            SCWindow *bestWindow = nil;
            CGFloat bestScore = -1.0;
            NSString *hint = strongSelf.targetWindowTitleHint ?: @"";
            NSString *normalizedHint = hint.lowercaseString;
            for (SCWindow *window in content.windows) {
                if (window == nil) {
                    continue;
                }

                CGFloat width = MAX(0.0, CGRectGetWidth(window.frame));
                CGFloat height = MAX(0.0, CGRectGetHeight(window.frame));
                CGFloat area = width * height;
                if (area < 6400.0) {
                    continue;
                }

                NSString *windowTitle = window.title ?: @"";
                NSString *appName = window.owningApplication.applicationName ?: @"";
                NSString *normalizedTitle = windowTitle.lowercaseString;
                NSString *normalizedAppName = appName.lowercaseString;
                BOOL exactPidMatch = window.owningApplication.processID == expectedPid;
                BOOL hintMatch = normalizedHint.length > 0 &&
                                 ([normalizedTitle containsString:normalizedHint] ||
                                  [normalizedAppName containsString:normalizedHint]);
                BOOL ownAppWindow = [normalizedAppName containsString:@"aes lacrima"];

                if (!exactPidMatch && !hintMatch) {
                    continue;
                }

                if (ownAppWindow) {
                    continue;
                }

                CGFloat score = area;
                if (hintMatch) {
                    score += 1000000.0;
                }

                if (exactPidMatch) {
                    score += 2000000.0;
                }

                if (score <= bestScore) {
                    continue;
                }

                bestScore = score;
                bestWindow = window;
            }

            if (bestWindow == nil) {
                strongSelf.statusText = @"Waiting for emulator window...";
                dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.35 * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
                    typeof(self) retrySelf = weakSelf;
                    if (retrySelf != nil && retrySelf.targetPid == expectedPid && retrySelf.stream == nil) {
                        [retrySelf discoverAndStartStream];
                    }
                });
                return;
            }

            [strongSelf createStreamForWindow:bestWindow];
        });
    }];
}

- (void)createStreamForWindow:(SCWindow *)window API_AVAILABLE(macos(12.3))
{
    SCContentFilter *filter = [[SCContentFilter alloc] initWithDesktopIndependentWindow:window];
    SCStreamConfiguration *configuration = [SCStreamConfiguration new];
    configuration.showsCursor = NO;
    configuration.pixelFormat = kCVPixelFormatType_32BGRA;
    configuration.minimumFrameInterval = CMTimeMake(1, 60);

    CGFloat width = MAX(1.0, CGRectGetWidth(window.frame) - self.cropInsets.left - self.cropInsets.right);
    CGFloat height = MAX(1.0, CGRectGetHeight(window.frame) - self.cropInsets.top - self.cropInsets.bottom);
    configuration.width = width;
    configuration.height = height;
    configuration.sourceRect = CGRectMake(
        self.cropInsets.left,
        self.cropInsets.top,
        width,
        height);

    NSError *outputError = nil;
    self.stream = [[SCStream alloc] initWithFilter:filter configuration:configuration delegate:nil];
    [self.stream addStreamOutput:self type:SCStreamOutputTypeScreen sampleHandlerQueue:self.captureQueue error:&outputError];
    if (outputError != nil) {
        self.initializing = NO;
        self.active = NO;
        self.statusText = [NSString stringWithFormat:@"Failed to attach ScreenCaptureKit output: %@", outputError.localizedDescription];
        self.stream = nil;
        return;
    }

    self.lastFrameTimestamp = 0;
    self.frameCount = 0;

    __weak typeof(self) weakSelf = self;
    [self.stream startCaptureWithCompletionHandler:^(NSError *error) {
        dispatch_async(dispatch_get_main_queue(), ^{
            typeof(self) strongSelf = weakSelf;
            if (strongSelf == nil) {
                return;
            }

            if (error != nil) {
                strongSelf.initializing = NO;
                strongSelf.active = NO;
                strongSelf.statusText = [NSString stringWithFormat:@"ScreenCaptureKit start failed: %@", error.localizedDescription];
                strongSelf.stream = nil;
                return;
            }

            strongSelf.initializing = YES;
            strongSelf.active = NO;
            strongSelf.statusText = @"ScreenCaptureKit stream started, waiting for frames...";
        });
    }];
}

- (void)stopCapture
{
    self.targetPid = 0;

    if (self.stream != nil) {
        SCStream *stream = self.stream;
        self.stream = nil;
        [stream stopCaptureWithCompletionHandler:^(__unused NSError *error) {
        }];
    }

    [self.view.displayLayer flushAndRemoveImage];
    self.initializing = NO;
    self.active = NO;
    self.fps = 0;
    self.frameTimeMs = 0;
    self.statusText = @"ScreenCaptureKit idle";
}

- (void)forwardFocus
{
    if (self.targetPid <= 0) {
        return;
    }

    NSRunningApplication *application = [NSRunningApplication runningApplicationWithProcessIdentifier:self.targetPid];
    [application activateWithOptions:NSApplicationActivateIgnoringOtherApps];
}

- (void)applyRenderOptionsWithBrightness:(float)brightness
                              saturation:(float)saturation
                                   tintR:(float)tintR
                                   tintG:(float)tintG
                                   tintB:(float)tintB
                                   tintA:(float)tintA
{
    self.view.displayLayer.opacity = MAX(0.0f, MIN(1.0f, tintA));
    self.view.displayLayer.backgroundColor = [NSColor colorWithSRGBRed:tintR
                                                                  green:tintG
                                                                   blue:tintB
                                                                  alpha:0.12f].CGColor;

    // AVSampleBufferDisplayLayer doesn't expose a simple cross-version color matrix API,
    // so brightness/saturation are stored as future extension points for a Metal path.
    (void)brightness;
    (void)saturation;
}

- (void)stream:(SCStream *)stream didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer ofType:(SCStreamOutputType)type API_AVAILABLE(macos(12.3))
{
    if (stream != self.stream || type != SCStreamOutputTypeScreen || sampleBuffer == NULL || !CMSampleBufferIsValid(sampleBuffer)) {
        return;
    }

    CFRetain(sampleBuffer);
    dispatch_async(dispatch_get_main_queue(), ^{
        if (self.view.displayLayer.status == AVQueuedSampleBufferRenderingStatusFailed) {
            [self.view.displayLayer flush];
        }

        [self.view.displayLayer enqueueSampleBuffer:sampleBuffer];
        CFRelease(sampleBuffer);
    });

    CFTimeInterval now = CACurrentMediaTime();
    if (self.lastFrameTimestamp > 0) {
        CFTimeInterval delta = now - self.lastFrameTimestamp;
        if (delta > 0) {
            double currentFrameTimeMs = delta * 1000.0;
            self.frameTimeMs = self.frameTimeMs <= 0.01 ? currentFrameTimeMs : (self.frameTimeMs * 0.82) + (currentFrameTimeMs * 0.18);
            double currentFps = 1000.0 / MAX(currentFrameTimeMs, 0.001);
            self.fps = self.fps <= 0.01 ? currentFps : (self.fps * 0.8) + (currentFps * 0.2);
        }
    }

    self.lastFrameTimestamp = now;
    self.frameCount += 1;
    self.initializing = NO;
    self.active = YES;
    self.statusText = [NSString stringWithFormat:@"ScreenCaptureKit active | frames %ld", (long)self.frameCount];
}
@end

void *aes_mac_capture_create(void)
{
    if (@available(macOS 12.3, *)) {
        AesMacCaptureController *controller = [AesMacCaptureController new];
        return (__bridge_retained void *)controller;
    }

    return NULL;
}

void aes_mac_capture_destroy(void *capture)
{
    if (capture == NULL) {
        return;
    }

    AesMacCaptureController *controller = (__bridge_transfer AesMacCaptureController *)capture;
    [controller stopCapture];
}

void *aes_mac_capture_get_view(void *capture)
{
    if (capture == NULL) {
        return NULL;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    return (__bridge void *)controller.view;
}

void aes_mac_capture_set_target(void *capture, int processId, const char *windowTitleHint)
{
    if (capture == NULL) {
        return;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    controller.targetWindowTitleHint = windowTitleHint != NULL ? [NSString stringWithUTF8String:windowTitleHint] : @"";
    dispatch_async(dispatch_get_main_queue(), ^{
        [controller startCaptureForPid:(pid_t)processId];
    });
}

void aes_mac_capture_stop(void *capture)
{
    if (capture == NULL) {
        return;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    dispatch_async(dispatch_get_main_queue(), ^{
        [controller stopCapture];
    });
}

void aes_mac_capture_forward_focus(void *capture)
{
    if (capture == NULL) {
        return;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    dispatch_async(dispatch_get_main_queue(), ^{
        [controller forwardFocus];
    });
}

void aes_mac_capture_set_stretch(void *capture, int stretch)
{
    if (capture == NULL) {
        return;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    dispatch_async(dispatch_get_main_queue(), ^{
        controller.stretchMode = stretch;
    });
}

void aes_mac_capture_set_render_options(void *capture, float brightness, float saturation, float tintR, float tintG, float tintB, float tintA)
{
    if (capture == NULL) {
        return;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    dispatch_async(dispatch_get_main_queue(), ^{
        [controller applyRenderOptionsWithBrightness:brightness saturation:saturation tintR:tintR tintG:tintG tintB:tintB tintA:tintA];
    });
}

void aes_mac_capture_set_crop_insets(void *capture, int left, int top, int right, int bottom)
{
    if (capture == NULL) {
        return;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    controller.cropInsets = NSEdgeInsetsMake(top, left, bottom, right);
}

int aes_mac_capture_is_active(void *capture)
{
    if (capture == NULL) {
        return 0;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    return controller.active ? 1 : 0;
}

int aes_mac_capture_is_initializing(void *capture)
{
    if (capture == NULL) {
        return 0;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    return controller.initializing ? 1 : 0;
}

double aes_mac_capture_get_fps(void *capture)
{
    if (capture == NULL) {
        return 0;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    return controller.fps;
}

double aes_mac_capture_get_frame_time_ms(void *capture)
{
    if (capture == NULL) {
        return 0;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    return controller.frameTimeMs;
}

int aes_mac_capture_get_status_text(void *capture, char *buffer, int bufferChars)
{
    if (capture == NULL) {
        return 0;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    return write_utf8_string(controller.statusText, buffer, bufferChars);
}

int aes_mac_capture_get_gpu_renderer(void *capture, char *buffer, int bufferChars)
{
    if (capture == NULL) {
        return 0;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    return write_utf8_string(controller.gpuRenderer, buffer, bufferChars);
}

int aes_mac_capture_get_gpu_vendor(void *capture, char *buffer, int bufferChars)
{
    if (capture == NULL) {
        return 0;
    }

    AesMacCaptureController *controller = (__bridge AesMacCaptureController *)capture;
    return write_utf8_string(controller.gpuVendor, buffer, bufferChars);
}

int aes_mac_pick_emulator_application(char *buffer, int bufferChars)
{
    __block NSInteger result = NSModalResponseCancel;
    __block NSURL *selectedUrl = nil;

    void (^showPanel)(void) = ^{
        NSOpenPanel *panel = [NSOpenPanel openPanel];
        panel.title = @"Select Emulator Application";
        panel.canChooseFiles = YES;
        panel.canChooseDirectories = YES;
        panel.allowsMultipleSelection = NO;
        panel.resolvesAliases = YES;
        panel.treatsFilePackagesAsDirectories = NO;
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
        panel.allowedFileTypes = @[@"app", @"command"];
#pragma clang diagnostic pop

        result = [panel runModal];
        if (result == NSModalResponseOK) {
            selectedUrl = panel.URL;
        }
    };

    if ([NSThread isMainThread]) {
        showPanel();
    } else {
        dispatch_sync(dispatch_get_main_queue(), showPanel);
    }

    if (result != NSModalResponseOK || selectedUrl == nil) {
        return 0;
    }

    return write_utf8_string(selectedUrl.path, buffer, bufferChars);
}

int aes_mac_pick_folder(const char *title, char *buffer, int bufferChars)
{
    __block NSInteger result = NSModalResponseCancel;
    __block NSURL *selectedUrl = nil;

    void (^showPanel)(void) = ^{
        NSOpenPanel *panel = [NSOpenPanel openPanel];
        panel.title = title != NULL ? [NSString stringWithUTF8String:title] : @"Select Folder";
        panel.canChooseFiles = NO;
        panel.canChooseDirectories = YES;
        panel.allowsMultipleSelection = NO;
        panel.resolvesAliases = YES;
        panel.treatsFilePackagesAsDirectories = YES;

        result = [panel runModal];
        if (result == NSModalResponseOK) {
            selectedUrl = panel.URL;
        }
    };

    if ([NSThread isMainThread]) {
        showPanel();
    } else {
        dispatch_sync(dispatch_get_main_queue(), showPanel);
    }

    if (result != NSModalResponseOK || selectedUrl == nil) {
        return 0;
    }

    return write_utf8_string(selectedUrl.path, buffer, bufferChars);
}

void aes_mac_configure_portal_window(void *windowHandle)
{
    if (windowHandle == NULL) {
        return;
    }

    dispatch_async(dispatch_get_main_queue(), ^{
        NSWindow *window = (__bridge NSWindow *)windowHandle;
        [window setLevel:NSNormalWindowLevel];
        [window setOpaque:YES];
        [window setHasShadow:NO];
        [window setHidesOnDeactivate:NO];
        [window setExcludedFromWindowsMenu:YES];
        [window setIgnoresMouseEvents:YES];
        [window orderFront:nil];
    });
}

void aes_mac_order_window_below(void *windowHandle, void *siblingWindowHandle)
{
    if (windowHandle == NULL || siblingWindowHandle == NULL) {
        return;
    }

    dispatch_async(dispatch_get_main_queue(), ^{
        NSWindow *window = (__bridge NSWindow *)windowHandle;
        NSWindow *sibling = (__bridge NSWindow *)siblingWindowHandle;
        [window setLevel:sibling.level];
        [window orderFront:nil];
        [window orderWindow:NSWindowBelow relativeTo:sibling.windowNumber];
    });
}

void aes_mac_attach_portal_window(void *portalWindowHandle, void *parentWindowHandle)
{
    if (portalWindowHandle == NULL || parentWindowHandle == NULL) {
        return;
    }

    dispatch_async(dispatch_get_main_queue(), ^{
        NSWindow *portalWindow = (__bridge NSWindow *)portalWindowHandle;
        NSWindow *parentWindow = (__bridge NSWindow *)parentWindowHandle;

        if (portalWindow.parentWindow == parentWindow) {
            return;
        }

        if (portalWindow.parentWindow != nil) {
            [portalWindow.parentWindow removeChildWindow:portalWindow];
        }

        [parentWindow addChildWindow:portalWindow ordered:NSWindowBelow];
        [portalWindow orderWindow:NSWindowBelow relativeTo:parentWindow.windowNumber];
    });
}

int aes_mac_window_content_to_screen(void *windowHandle, double x, double y, double *screenX, double *screenY)
{
    if (windowHandle == NULL || screenX == NULL || screenY == NULL) {
        return 0;
    }

    __block NSPoint screenPoint = NSZeroPoint;
    void (^convertPoint)(void) = ^{
        NSWindow *window = (__bridge NSWindow *)windowHandle;
        NSView *contentView = window.contentView;
        if (contentView == nil) {
            return;
        }

        // Avalonia coordinates are content-relative with a top-left origin, while AppKit window
        // coordinates use a lower-left origin. Flip inside the content view first, then promote
        // the point into window coordinates before converting to screen space.
        CGFloat contentHeight = NSHeight(contentView.bounds);
        NSPoint contentPoint = NSMakePoint((CGFloat)x, contentHeight - (CGFloat)y);
        NSPoint windowPoint = [contentView convertPoint:contentPoint toView:nil];
        screenPoint = [window convertPointToScreen:windowPoint];

        NSScreen *screen = window.screen ?: NSScreen.mainScreen;
        if (screen != nil) {
            CGFloat topLeftY = NSMaxY(screen.frame) - screenPoint.y;
            screenPoint = NSMakePoint(screenPoint.x, topLeftY);
        }
    };

    if ([NSThread isMainThread]) {
        convertPoint();
    } else {
        dispatch_sync(dispatch_get_main_queue(), convertPoint);
    }

    *screenX = screenPoint.x;
    *screenY = screenPoint.y;
    return 1;
}
