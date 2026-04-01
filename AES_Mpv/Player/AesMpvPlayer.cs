using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AES_Mpv.Interop;
using AES_Mpv.Native;

namespace AES_Mpv.Player;

public unsafe class AesMpvPlayer : IDisposable
{
    private readonly Lock _gate = new();
    private readonly AesMpvPlayerOptions _options;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _pendingAsyncWork = new();
    private readonly MpvWakeupCallback _wakeupCallback;
    private MpvHandle* _handle;
    private MpvRenderContext* _renderContext;
    private MpvOpenGlAddressResolver? _openGlResolver;
    private MpvRenderUpdateCallback? _renderUpdateCallback;
    private bool _disposed;
    private long _requestSequence;
    private int _eventPumpQueued;
    private int _eventPumpPending;

    public AesMpvPlayer()
        : this(new AesMpvPlayerOptions())
    {
    }

    public AesMpvPlayer(Action<AesMpvPlayer> beforeInitialize)
        : this(new AesMpvPlayerOptions { BeforeInitialize = beforeInitialize })
    {
    }

    public AesMpvPlayer(AesMpvPlayerOptions options)
    {
        _options = options ?? new AesMpvPlayerOptions();
        _wakeupCallback = OnWakeup;

        Debug.WriteLine("AES_Mpv client API version: {0}", MpvNativeApi.ClientApiVersion());
        _handle = CreateHandle(_options);
        InitializeCore(_options.SharedPlayer == null);
    }

    public IntPtr Handle => (IntPtr)_handle;
    public AesMpvPlayerOptions Options => _options;

    static AesMpvPlayer()
    {
        MpvNativeLibrary.RegisterResolver();
    }

    public event EventHandler<MpvEvent>? EventReceived;
    public event EventHandler? RenderInvalidated;

    public void ObserveProperty(string name, MpvFormat format)
    {
        lock (_gate)
        {
            EnsureActiveHandle();
            ThrowIfError(MpvNativeApi.ObserveProperty(_handle, 0, name, format), nameof(MpvNativeApi.ObserveProperty), name, format.ToString());
        }
    }

    public void SetProperty(string name, long value)
    {
        lock (_gate)
        {
            EnsureActiveHandle();
            long local = value;
            ThrowIfError(MpvNativeApi.SetProperty(_handle, name, MpvFormat.Int64, &local), nameof(MpvNativeApi.SetProperty), name, value.ToString());
        }
    }

    public void SetProperty(string name, string? value)
    {
        lock (_gate)
        {
            EnsureActiveHandle();
            ThrowIfError(MpvNativeApi.SetPropertyString(_handle, name, value), nameof(MpvNativeApi.SetPropertyString), name, value ?? "<null>");
        }
    }

    public bool SetProperty(string name, double value)
    {
        lock (_gate)
        {
            EnsureActiveHandle();
            double local = value;
            return MpvNativeApi.SetProperty(_handle, name, MpvFormat.Double, &local) >= 0;
        }
    }

    public void SetProperty(string name, bool value)
    {
        lock (_gate)
        {
            EnsureActiveHandle();
            int local = value ? 1 : 0;
            ThrowIfError(MpvNativeApi.SetProperty(_handle, name, MpvFormat.Flag, &local), nameof(MpvNativeApi.SetProperty), name, value.ToString());
        }
    }

    public double GetDoubleProperty(string name)
    {
        lock (_gate)
        {
            EnsureActiveHandle();
            double value = 0;
            ThrowIfError(MpvNativeApi.GetProperty(_handle, name, MpvFormat.Double, &value), nameof(MpvNativeApi.GetProperty), name);
            return value;
        }
    }

    public void RunCommand(params string[] args)
    {
        lock (_gate)
        {
            EnsureActiveHandle();
            using var nativeArgs = CommandArgumentBlock.Create(args);
            ThrowIfError(MpvNativeApi.Command(_handle, nativeArgs.Pointer), nameof(MpvNativeApi.Command), args);
        }
    }

    public Task RunCommandAsync(string[] args, CancellationToken cancellation = default)
    {
        EnsureActiveHandle();
        var requestId = unchecked((ulong)Interlocked.Increment(ref _requestSequence));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingAsyncWork.TryAdd(requestId, completion))
            throw new InvalidOperationException("Failed to register async mpv command.");

        CancellationTokenRegistration cancellationRegistration = default;
        if (cancellation.CanBeCanceled)
        {
            cancellationRegistration = cancellation.Register(() =>
            {
                if (_pendingAsyncWork.TryRemove(requestId, out var pending))
                {
                    lock (_gate)
                    {
                        if (!_disposed && _handle != null)
                            MpvNativeApi.AbortAsyncCommand(_handle, requestId);
                    }

                    pending.TrySetCanceled(cancellation);
                }
            });
        }

        try
        {
            lock (_gate)
            {
                EnsureActiveHandle();
                using var nativeArgs = CommandArgumentBlock.Create(args);
                ThrowIfError(MpvNativeApi.CommandAsync(_handle, requestId, nativeArgs.Pointer), nameof(MpvNativeApi.CommandAsync), args);
            }
        }
        catch
        {
            cancellationRegistration.Dispose();
            _pendingAsyncWork.TryRemove(requestId, out _);
            throw;
        }

        return completion.Task.ContinueWith(
            _ =>
            {
                cancellationRegistration.Dispose();
                _pendingAsyncWork.TryRemove(requestId, out var ignored);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void EnsureRenderContext()
    {
        EnsureActiveHandle();
        if (_renderContext != null)
            return;

        var sharedPlayer = _options.SharedPlayer;
        if (sharedPlayer != null && sharedPlayer._renderContext != null)
        {
            _renderContext = sharedPlayer._renderContext;
            return;
        }

        var apiTypeBytes = Encoding.ASCII.GetBytes("opengl\0");
        var apiTypePtr = Marshal.AllocHGlobal(apiTypeBytes.Length);
        Marshal.Copy(apiTypeBytes, 0, apiTypePtr, apiTypeBytes.Length);

        var initParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<OpenGlAddressResolverContext>());
        try
        {
            _openGlResolver = ResolveOpenGlAddress;
            _renderUpdateCallback = HandleRenderUpdate;

            var initParams = new OpenGlAddressResolverContext
            {
                Resolve = _openGlResolver,
                ResolveContext = null,
                ExtraExtensions = null,
            };

            Marshal.StructureToPtr(initParams, initParamsPtr, false);

            var parameters = new[]
            {
                new MpvRenderParameter { Type = MpvRenderParameterType.ApiType, Data = (void*)apiTypePtr },
                new MpvRenderParameter { Type = MpvRenderParameterType.OpenGlInitParams, Data = (void*)initParamsPtr },
                new MpvRenderParameter { Type = MpvRenderParameterType.Invalid, Data = null },
            };

            fixed (MpvRenderParameter* parameterPtr = parameters)
            {
                lock (_gate)
                {
                    EnsureActiveHandle();
                    MpvRenderContext* createdContext = null;
                    ThrowIfError(MpvRenderApi.CreateContext(&createdContext, _handle, parameterPtr), nameof(MpvRenderApi.CreateContext));
                    _renderContext = createdContext;
                    MpvRenderApi.SetUpdateCallback(_renderContext, _renderUpdateCallback, null);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(initParamsPtr);
            Marshal.FreeHGlobal(apiTypePtr);
        }
    }

    public void RenderToOpenGl(int width, int height, int framebuffer, int internalFormat = 0, int flipY = 0)
    {
        if (_renderContext == null || _disposed)
            return;

        int blockForTargetTime = 0;

        var target = new MpvOpenGlFramebuffer
        {
            Framebuffer = framebuffer,
            Width = width,
            Height = height,
            InternalFormat = internalFormat,
        };
        var flip = flipY;
        Span<MpvRenderParameter> parameters =
        [
            new MpvRenderParameter { Type = MpvRenderParameterType.OpenGlFbo, Data = &target },
            new MpvRenderParameter { Type = MpvRenderParameterType.FlipY, Data = &flip },
            new MpvRenderParameter { Type = MpvRenderParameterType.BlockForTargetTime, Data = &blockForTargetTime },
            new MpvRenderParameter { Type = MpvRenderParameterType.Invalid, Data = null },
        ];

        fixed (MpvRenderParameter* parameterPtr = parameters)
        {
            lock (_gate)
            {
                if (_renderContext == null || _disposed)
                    return;

                ThrowIfError(MpvRenderApi.Render(_renderContext, parameterPtr), nameof(MpvRenderApi.Render));
                MpvRenderApi.ReportSwap(_renderContext);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseRenderContext();

        lock (_gate)
        {
            if (_handle != null)
            {
                MpvNativeApi.Destroy(_handle);
                _handle = null;
            }
        }

        foreach (var pending in _pendingAsyncWork.Values)
            pending.TrySetCanceled();
        _pendingAsyncWork.Clear();
    }

    public void DisposeAndShutdown()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseRenderContext();

        lock (_gate)
        {
            if (_handle != null)
            {
                MpvNativeApi.TerminateAndDestroy(_handle);
                _handle = null;
            }
        }
    }

    private static unsafe MpvHandle* CreateHandle(AesMpvPlayerOptions options)
    {
        if (options.SharedPlayer == null)
            return MpvNativeApi.Create();

        return options.UseWeakReference
            ? MpvNativeApi.CreateWeakClient(options.SharedPlayer._handle, options.SharedClientName)
            : MpvNativeApi.CreateClient(options.SharedPlayer._handle, options.SharedClientName);
    }

    private void InitializeCore(bool initialize)
    {
        EnsureActiveHandle();

        if (initialize)
        {
            _options.BeforeInitialize?.Invoke(this);
            ThrowIfError(MpvNativeApi.Initialize(_handle), nameof(MpvNativeApi.Initialize));
        }

        SetProperty(MpvPropertyNames.Video.OutputDriver, "libmpv");
        MpvNativeApi.SetWakeupCallback(_handle, _wakeupCallback, null);
    }

    private void ReleaseRenderContext()
    {
        if (_renderContext == null || _options.SharedPlayer != null)
            return;

        lock (_gate)
        {
            if (_renderContext != null)
            {
                MpvRenderApi.Free(_renderContext);
                _renderContext = null;
            }
        }
    }

    private nint ResolveOpenGlAddress(nint context, string name)
        => _options.ResolveOpenGlAddress?.Invoke(context, name) ?? 0;

    private void HandleRenderUpdate(void* context)
    {
        _options.OnRenderInvalidated?.Invoke(context);

        try
        {
            RenderInvalidated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RenderInvalidated event handler threw an exception: {ex}");
        }
    }

    private void OnWakeup(void* context)
    {
        if (_disposed)
            return;

        Interlocked.Exchange(ref _eventPumpPending, 1);
        if (Interlocked.CompareExchange(ref _eventPumpQueued, 1, 0) != 0)
            return;

        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            var player = (AesMpvPlayer)state!;
            while (!player._disposed)
            {
                Interlocked.Exchange(ref player._eventPumpPending, 0);
                player.DrainEventQueue();

                Interlocked.Exchange(ref player._eventPumpQueued, 0);
                if (player._disposed || Volatile.Read(ref player._eventPumpPending) == 0)
                    return;

                if (Interlocked.CompareExchange(ref player._eventPumpQueued, 1, 0) != 0)
                    return;
            }
        }, this);
    }

    private void DrainEventQueue()
    {
        while (!_disposed)
        {
            MpvEvent? nextEvent = null;

            lock (_gate)
            {
                if (_handle == null)
                    return;

                var nativeEvent = MpvNativeApi.WaitEvent(_handle, 0);
                if (nativeEvent == null || nativeEvent->EventId == MpvEventId.None)
                    return;

                nextEvent = *nativeEvent;
            }

            HandleEvent(nextEvent.Value);
        }
    }

    private void HandleEvent(MpvEvent mpvEvent)
    {
        switch (mpvEvent.EventId)
        {
            case MpvEventId.CommandReply:
            case MpvEventId.GetPropertyReply:
            case MpvEventId.SetPropertyReply:
                CompleteAsyncRequest(mpvEvent);
                break;
            case MpvEventId.Shutdown:
                Dispose();
                break;
        }

        try
        {
            EventReceived?.Invoke(this, mpvEvent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"EventReceived handler threw an exception: {ex}");
        }
    }

    private void CompleteAsyncRequest(MpvEvent mpvEvent)
    {
        if (!_pendingAsyncWork.TryRemove(mpvEvent.ReplyUserData, out var completion))
            return;

        if ((int)mpvEvent.Error < 0)
        {
            completion.TrySetException(new AesMpvException(mpvEvent.Error, MpvNativeApi.DescribeError((int)mpvEvent.Error)));
            return;
        }

        completion.TrySetResult();
    }

    private void ThrowIfError(int errorCode, string function, params string[] args)
    {
        if (errorCode >= 0)
            return;

        var message = $"{function}({string.Join(",", args)}) error: {MpvNativeApi.DescribeError(errorCode)}";
        Debug.WriteLine(message);
        throw new AesMpvException((MpvError)errorCode, message);
    }

    private void EnsureActiveHandle()
    {
        if (_handle == null || _disposed)
            throw new AesMpvException(MpvError.Uninitialized, "The AES mpv player handle is null or disposed.");
    }

    private sealed unsafe class CommandArgumentBlock : IDisposable
    {
        private readonly nint[] _buffers;
        private readonly nint _root;

        private CommandArgumentBlock(nint[] buffers, nint root)
        {
            _buffers = buffers;
            _root = root;
        }

        public byte** Pointer => (byte**)_root;

        public static CommandArgumentBlock Create(string[] args)
        {
            var buffers = new nint[args.Length];
            var root = Marshal.AllocHGlobal((args.Length + 1) * IntPtr.Size);
            var pointers = new nint[args.Length + 1];

            for (int i = 0; i < args.Length; i++)
            {
                var utf8 = Encoding.UTF8.GetBytes(args[i] + '\0');
                var buffer = Marshal.AllocHGlobal(utf8.Length);
                Marshal.Copy(utf8, 0, buffer, utf8.Length);
                buffers[i] = buffer;
                pointers[i] = buffer;
            }

            pointers[args.Length] = 0;
            Marshal.Copy(pointers, 0, root, args.Length + 1);
            return new CommandArgumentBlock(buffers, root);
        }

        public void Dispose()
        {
            foreach (var buffer in _buffers)
            {
                if (buffer != 0)
                    Marshal.FreeHGlobal(buffer);
            }

            if (_root != 0)
                Marshal.FreeHGlobal(_root);
        }
    }
}
