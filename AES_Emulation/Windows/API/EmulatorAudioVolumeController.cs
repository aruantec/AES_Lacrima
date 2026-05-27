using log4net;
using AES_Core.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AES_Emulation.Windows.API
{
    [SupportedOSPlatform("windows")]
    public sealed class EmulatorAudioVolumeController : IDisposable
    {
        private static readonly ILog Log = LogHelper.For<EmulatorAudioVolumeController>();

        private const string AudioBridgeDll = "AudioBridge.dll";

        [DllImport(AudioBridgeDll, PreserveSig = true)]
        private static extern int AudioBridge_FindSessionAndGetVolume(uint pid, out float volume);

        [DllImport(AudioBridgeDll, PreserveSig = true)]
        private static extern int AudioBridge_FindSessionAndSetVolume(uint pid, float volume);

        private static readonly bool _nativeAvailable;

        static EmulatorAudioVolumeController()
        {
            try
            {
                IntPtr lib = NativeLibrary.Load(AudioBridgeDll);
                _nativeAvailable = lib != IntPtr.Zero;
                NativeLibrary.Free(lib);
                Log.Warn($"EmulatorVolume: AudioBridge native DLL available.");
            }
            catch
            {
                _nativeAvailable = false;
                Log.Warn($"EmulatorVolume: AudioBridge native DLL not available, using COM interop fallback.");
            }
        }

        private ISimpleAudioVolume? _simpleAudioVolume;
        private int _processId;
        private bool _disposed;
        private DateTime _nextRetryTime = DateTime.MinValue;
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

        public void Attach(int processId)
        {
            Detach();
            _processId = processId;
            Log.Warn($"EmulatorVolume: Attaching for PID {processId}.");
            _simpleAudioVolume = FindAudioSessionForProcess(processId);
            if (_simpleAudioVolume == null)
                Log.Warn($"EmulatorVolume: No audio session for PID {processId} during attach. Will retry.");
            else
                Log.Warn($"EmulatorVolume: Session found for PID {processId} during attach.");
        }

        public void Detach()
        {
            _processId = 0;
            _simpleAudioVolume = null;
        }

        public float Volume
        {
            get
            {
                // Try native bridge first (bypasses C# COM interop issues with FxSound)
                if (_nativeAvailable && _processId > 0)
                {
                    float nativeVol = 1.0f;
                    int hr = AudioBridge_FindSessionAndGetVolume((uint)_processId, out nativeVol);
                    if (hr >= 0)
                    {
                        Log.Warn($"Volume getter: native bridge returned volume={nativeVol} for PID {_processId}.");
                        return nativeVol;
                    }
                    Log.Warn($"Volume getter: native bridge failed (0x{hr:X8}) for PID {_processId}, falling back.");
                }

                if (_simpleAudioVolume == null && DateTime.UtcNow >= _nextRetryTime)
                {
                    Log.Warn($"Volume getter: no session cached, retrying for PID {_processId}.");
                    _simpleAudioVolume = FindAudioSessionForProcess(_processId);
                    if (_simpleAudioVolume == null)
                        _nextRetryTime = DateTime.UtcNow + RetryInterval;
                }

                if (_simpleAudioVolume == null)
                {
                    Log.Warn($"Volume getter: no audio session found for PID {_processId}, returning 1.0.");
                    return 1.0f;
                }

                try
                {
                    _simpleAudioVolume.GetMasterVolume(out float level);
                    Log.Warn($"Volume getter: level={level} for PID {_processId}.");
                    return level;
                }
                catch (COMException ex)
                {
                    Log.Warn($"Volume getter: COM error reading volume for PID {_processId}.", ex);
                    _simpleAudioVolume = null;
                    _nextRetryTime = DateTime.UtcNow + RetryInterval;
                    return 1.0f;
                }
            }
            set
            {
                float clamped = Math.Clamp(value, 0.0f, 1.0f);

                // Try native bridge first (bypasses C# COM interop issues with FxSound)
                if (_nativeAvailable && _processId > 0)
                {
                    int hr = AudioBridge_FindSessionAndSetVolume((uint)_processId, clamped);
                    if (hr >= 0)
                    {
                        Log.Warn($"Volume setter: native bridge set volume to {clamped} for PID {_processId}.");
                        return;
                    }
                    Log.Warn($"Volume setter: native bridge failed (0x{hr:X8}) for PID {_processId}, falling back.");
                }

                if (_simpleAudioVolume == null && DateTime.UtcNow >= _nextRetryTime)
                {
                    Log.Warn($"Volume setter: no session cached, retrying for PID {_processId}.");
                    _simpleAudioVolume = FindAudioSessionForProcess(_processId);
                    if (_simpleAudioVolume == null)
                        _nextRetryTime = DateTime.UtcNow + RetryInterval;
                }

                if (_simpleAudioVolume == null)
                {
                    Log.Warn($"Volume setter: no audio session found for PID {_processId}, cannot set volume.");
                    return;
                }

                try
                {
                    var guid = Guid.Empty;
                    Log.Warn($"Volume setter: setting audio session volume to {clamped} for PID {_processId}.");
                    _simpleAudioVolume.SetMasterVolume(clamped, ref guid);
                }
                catch (COMException ex)
                {
                    Log.Warn($"Volume setter: COM error setting volume {value} for PID {_processId}.", ex);
                    _simpleAudioVolume = null;
                    _nextRetryTime = DateTime.UtcNow + RetryInterval;
                }
            }
        }

        private static IAudioSessionManager2? TryActivateSessionManager(IMMDevice device)
        {
            var iid2 = typeof(IAudioSessionManager2).GUID;

            // Try IAudioSessionManager2 directly first
            int hr2 = device.Activate(ref iid2, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out IntPtr pMgr2);
            if (hr2 >= 0 && pMgr2 != IntPtr.Zero)
            {
                Log.Warn($"TryActivateSessionManager: IAudioSessionManager2 activated directly.");
                var mgr = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(pMgr2);
                Marshal.Release(pMgr2);
                return mgr;
            }
            Log.Warn($"TryActivateSessionManager: IAudioSessionManager2 failed (0x{hr2:X8}), trying IAudioSessionManager...");

            // Fall back to IAudioSessionManager, then QI for IAudioSessionManager2
            var iid1 = typeof(IAudioSessionManager).GUID;
            int hr1 = device.Activate(ref iid1, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out IntPtr pMgr1);
            if (hr1 >= 0 && pMgr1 != IntPtr.Zero)
            {
                Log.Warn($"TryActivateSessionManager: IAudioSessionManager activated, QI for IAudioSessionManager2.");
#pragma warning disable CS9191 // Marshal.QueryInterface requires ref, not in
                var qi = Marshal.QueryInterface(pMgr1, ref iid2, out IntPtr pMgr2FromQi);
#pragma warning restore CS9191
                Marshal.Release(pMgr1);
                if (qi >= 0 && pMgr2FromQi != IntPtr.Zero)
                {
                    Log.Warn($"TryActivateSessionManager: QI to IAudioSessionManager2 succeeded.");
                    var mgr = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(pMgr2FromQi);
                    Marshal.Release(pMgr2FromQi);
                    return mgr;
                }
                Log.Warn($"TryActivateSessionManager: QI to IAudioSessionManager2 failed (0x{qi:X8}).");
            }
            else
            {
                Log.Warn($"TryActivateSessionManager: IAudioSessionManager also failed (0x{hr1:X8}).");
            }

            // Last resort: activate IAudioClient and use GetService
            Log.Warn($"TryActivateSessionManager: trying IAudioClient.GetService path...");
            var iidAudioClient = typeof(IAudioClient).GUID;
            int hrClient = device.Activate(ref iidAudioClient, 1, IntPtr.Zero, out IntPtr pAudioClient);
            if (hrClient >= 0 && pAudioClient != IntPtr.Zero)
            {
                try
                {
                    var audioClient = (IAudioClient)Marshal.GetObjectForIUnknown(pAudioClient);
                    audioClient.GetMixFormat(out IntPtr formatPtr);
                    try
                    {
                        audioClient.Initialize(0 /* AUDCLNT_SHAREMODE_SHARED */, 0, 200000, 0, formatPtr, IntPtr.Zero);
                        int hrService = audioClient.GetService(ref iid2, out IntPtr pSessionMgr);
                        if (hrService >= 0 && pSessionMgr != IntPtr.Zero)
                        {
                            Log.Warn($"TryActivateSessionManager: IAudioClient.GetService succeeded.");
                            var mgr = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(pSessionMgr);
                            Marshal.Release(pSessionMgr);
                            return mgr;
                        }
                        Log.Warn($"TryActivateSessionManager: IAudioClient.GetService failed (0x{hrService:X8}).");
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(formatPtr);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"TryActivateSessionManager: IAudioClient path failed.", ex);
                }
                finally
                {
                    Marshal.Release(pAudioClient);
                }
            }
            else
            {
                Log.Warn($"TryActivateSessionManager: IAudioClient activation failed (0x{hrClient:X8}).");
            }

            return null;
        }

        private static ISimpleAudioVolume? FindAudioSessionForProcess(int processId)
        {
            if (processId <= 0)
            {
                Log.Warn($"FindAudioSessionForProcess: invalid PID {processId}.");
                return null;
            }

            try
            {
                Log.Warn($"FindAudioSessionForProcess: creating MMDeviceEnumerator for PID {processId}.");
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

                // Try default endpoints under each role (FxSound may only intercept eConsole)
                List<IMMDevice> devicesToTry = new List<IMMDevice>();
                int[] roles = { 0 /* eConsole */, 1 /* eMultimedia */, 2 /* eCommunications */ };
                foreach (int role in roles)
                {
                    try
                    {
                        Log.Warn($"FindAudioSessionForProcess: getting default audio endpoint (render, role={role}).");
                        IMMDevice device;
                        enumerator.GetDefaultAudioEndpoint(0 /* eRender */, role, out device);
                        devicesToTry.Add(device);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"FindAudioSessionForProcess: render role {role} unavailable.", ex);
                    }
                }

                foreach (IMMDevice device in devicesToTry)
                {
                    Log.Warn($"FindAudioSessionForProcess: trying device for session manager...");
                    IAudioSessionManager2? sessionManager = TryActivateSessionManager(device);
                    if (sessionManager == null)
                    {
                        Log.Warn($"FindAudioSessionForProcess: device does not support session manager, skipping.");
                        continue;
                    }
                    Log.Warn($"FindAudioSessionForProcess: session manager activated on device.");

                    Log.Warn($"FindAudioSessionForProcess: getting session enumerator.");
                    sessionManager.GetAudioSessionEnumerator(out IAudioSessionEnumerator sessionEnum);

                    sessionEnum.GetCount(out int count);
                    Log.Warn($"FindAudioSessionForProcess: found {count} audio sessions. Scanning for PID {processId}.");

                    for (int i = 0; i < count; i++)
                    {
                        sessionEnum.GetSession(i, out IAudioSessionControl sessionControl);
                        var sessionControl2 = sessionControl as IAudioSessionControl2;
                        if (sessionControl2 == null)
                        {
                            Log.Warn($"FindAudioSessionForProcess: session {i}: not an IAudioSessionControl2.");
                            continue;
                        }

                        try
                        {
                            sessionControl2.GetProcessId(out uint sessionPid);
                            Log.Warn($"FindAudioSessionForProcess: session {i}: PID={sessionPid}.");

                            if (sessionPid == processId)
                            {
                                Log.Warn($"FindAudioSessionForProcess: found matching session at index {i} for PID {processId}.");
                                var volume = sessionControl as ISimpleAudioVolume;
                                if (volume != null)
                                {
                                    Log.Warn($"FindAudioSessionForProcess: successfully obtained ISimpleAudioVolume for PID {processId}.");
                                    return volume;
                                }
                                else
                                {
                                    Log.Warn($"FindAudioSessionForProcess: session matched but ISimpleAudioVolume is null!");
                                }
                            }
                        }
                        catch (COMException ex)
                        {
                            Log.Warn($"FindAudioSessionForProcess: session {i}: COM error getting PID.", ex);
                            continue;
                        }
                    }

                    Log.Warn($"FindAudioSessionForProcess: no session matched PID {processId} among {count} sessions on this device.");
                }

                Log.Warn($"FindAudioSessionForProcess: no audio session found for PID {processId} on any device.");
            }
            catch (Exception ex)
            {
                Log.Warn($"FindAudioSessionForProcess: error for PID {processId}.", ex);
            }

            return null;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Detach();
            }
        }
    }
}
