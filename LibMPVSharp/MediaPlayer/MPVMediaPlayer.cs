using System.Diagnostics;
using System.Text;
using LibMPVSharp.Wraps;

namespace LibMPVSharp
{
    public unsafe partial class MPVMediaPlayer : IDisposable
    {
        private readonly MPVMediaPlayerOptions _options;
        private MpvSetWakeupCallback_cbCallback? _wakeupCallback;
        private MpvHandle* _clientHandle;
        private bool _disposed;

        public IntPtr MPVHandle => (IntPtr)_clientHandle;
        public MPVMediaPlayerOptions Options => _options;

        static MPVMediaPlayer()
        {
            LibraryName.DllImportResolver();
        }

        public MPVMediaPlayer() : this(new MPVMediaPlayerOptions())
        {
            
        }

        public MPVMediaPlayer(Action<MPVMediaPlayer> beforeInitialize) : this(new MPVMediaPlayerOptions{ BeforeInitialize = beforeInitialize })
        {
            
        }

        public MPVMediaPlayer(MPVMediaPlayerOptions options) 
        {
            _options = options;
            Debug.WriteLine("Api version:{0}", Client.MpvClientApiVersion());
            if (_options.SharedPlayer == null)
            {
                _clientHandle = Client.MpvCreate();
                Initialize();
                return;
            }
            else if (_options.IsWeakReference)
            {
                _clientHandle = Client.MpvCreateWeakClient(_options.SharedPlayer._clientHandle, _options.SharePlayerName);
            }
            else
            {
                _clientHandle = Client.MpvCreateClient(_options.SharedPlayer._clientHandle, _options.SharePlayerName);
            }
            Initialize(false);
        }

        private void Initialize(bool initialize = true)
        {
            CheckClientHandle();

            if (initialize)
            {
                _options.BeforeInitialize?.Invoke(this);
                var error = Client.MpvInitialize(_clientHandle);
                CheckError(error, nameof(Client.MpvInitialize));
            }
            
            SetProperty(VideoOpts.Vo , "libmpv");
            _wakeupCallback = MPVWeakup;
            Client.MpvSetWakeupCallback(_clientHandle, _wakeupCallback, _clientHandle);
        }

        public void ObservableProperty(string name, MpvFormat format)
        {
            CheckClientHandle();
            var err = Client.MpvObserveProperty(_clientHandle, 0, name, format);
            CheckError(err, nameof(Client.MpvObserveProperty), name, format.ToString());
        }

        public void SetProperty(string name, long value)
        {
            CheckClientHandle();
            var array = new long[] { value };
            fixed(long* val = array)
            {
                var error = Client.MpvSetProperty(_clientHandle, name, MpvFormat.MPV_FORMAT_INT64, val);
                CheckError(error, nameof(Client.MpvSetProperty), name, value.ToString());
            }
        }

        public void SetProperty(string name, string? value)
        {
            CheckClientHandle();
            var error = Client.MpvSetPropertyString(_clientHandle, name, value);
            CheckError(error, nameof(Client.MpvSetPropertyString), name, value ?? "<null>");
        }

        public bool SetProperty(string name, double value)
        {
            CheckClientHandle();
            var array = new double[] { value };
            fixed(double* val = array)
            {
                var error = Client.MpvSetProperty(_clientHandle, name, MpvFormat.MPV_FORMAT_DOUBLE, val);
                value = array[0];
                return error >= 0;
            }
        }

        public double GetPropertyDouble(string name)
        {
            CheckClientHandle();
            var array = new double[] { 0 };
            fixed(double* arrayPtr = array)
            {
                var error = Client.MpvGetProperty(_clientHandle, name, MpvFormat.MPV_FORMAT_DOUBLE, arrayPtr);
                CheckError(error, nameof(Client.MpvGetProperty), name);
                return array[0];
            }
        }

        public void SetProperty(string name, bool value)
        {
            CheckClientHandle();
            bool[] array = value ? [true] : [false];
            fixed(bool* val = array)
            {
                var error = Client.MpvSetProperty(_clientHandle, name, MpvFormat.MPV_FORMAT_FLAG, val);
                CheckError(error, nameof(Client.MpvSetProperty), name, value.ToString());
            }
        }

        public void ExecuteCommand(params string[] args)
        {
            CheckClientHandle();

            var rootPtr = GetStringArrayPointer(args, out var disposable);

            try
            {
                var err = Client.MpvCommand(_clientHandle, (char**)rootPtr);
                CheckError(err, nameof(Client.MpvCommand), args);
            }
            finally
            {
                disposable?.Dispose();
            }
        }

        public Task ExecuteCommandAsync(string[] args, CancellationToken cancellation = default)
        {
            CheckClientHandle();
            var rootPtr = GetStringArrayPointer(args, out var disposable);
            TaskCompletionSource tcs = new TaskCompletionSource();
            var handle = GCHandle.Alloc(tcs);
            var userData = (ulong)GCHandle.ToIntPtr(handle);
            cancellation.Register(() =>
            {
                Client.MpvAbortAsyncCommand(_clientHandle, userData);
                tcs.TrySetCanceled();
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            });
            try
            {
                var err = Client.MpvCommandAsync(_clientHandle, userData, (char**)rootPtr);
                CheckError(err, nameof(Client.MpvCommand), args);
            }
            catch (Exception ex)
            {
                handle.Free();
                tcs.TrySetException(ex);
            }
            finally
            {
                disposable?.Dispose();
            }
            return tcs.Task;
        }

        public void Dispose() => Dispose(false);

        public void Dispose(bool terminate)
        {
            _disposed = true;
            ReleaseRenderContext();

            if (_clientHandle == null) return;
            if (terminate)
            {
                Client.MpvTerminateDestroy(_clientHandle);
                _clientHandle = null;
            }
            else
            {
                Client.MpvDestroy(_clientHandle);
                _clientHandle = null;
            }
        }

        private IntPtr GetStringArrayPointer(string[] args, out IDisposable disposable)
        {
            var count = args.Length + 1;
            var arrPtrs = new IntPtr[count];
            var rootPtr = Marshal.AllocHGlobal(IntPtr.Size * count);

            for (int i = 0; i < args.Length; i++)
            {
                var buffer = Encoding.UTF8.GetBytes(args[i] + '\0');
                var ptr = Marshal.AllocHGlobal(buffer.Length);
                Marshal.Copy(buffer, 0, ptr, buffer.Length);
                arrPtrs[i] = ptr;
            }

            Marshal.Copy(arrPtrs, 0, rootPtr, count);

            disposable = new DisposableObject(() =>
            {
                foreach (var item in arrPtrs)
                {
                    Marshal.FreeHGlobal(item);
                }

                Marshal.FreeHGlobal(rootPtr);
            });
            return rootPtr;
        }

        private void CheckError(int errorCode, string function, params string[] args)
        {
            if (errorCode < 0)
            {
                var error = (MpvError)errorCode;
                var msg = $"{function}({string.Join(",", args)}) error:{Client.MpvErrorString(errorCode)}";
                Debug.WriteLine(msg);
                throw new LibMPVException(error, msg);
            }
        }

        private void CheckClientHandle()
        {
            if (_clientHandle == null || _disposed) throw new LibMPVException(MpvError.MPV_ERROR_UNINITIALIZED, "Client handle is null or disposed.");
        }
    }
}
