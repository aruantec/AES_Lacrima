using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AES_Controls.GL
{
    public unsafe class GlBackgroundControl : OpenGlControlBase
    {
        #region Properties
        public static readonly StyledProperty<Bitmap?> BackgroundImageProperty =
            AvaloniaProperty.Register<GlBackgroundControl, Bitmap?>(nameof(BackgroundImage));

        public Bitmap? BackgroundImage
        {
            get => GetValue(BackgroundImageProperty);
            set => SetValue(BackgroundImageProperty, value);
        }

        public static readonly StyledProperty<Stretch> StretchProperty =
            AvaloniaProperty.Register<GlBackgroundControl, Stretch>(nameof(Stretch), Stretch.Uniform);

        public Stretch Stretch
        {
            get => GetValue(StretchProperty);
            set => SetValue(StretchProperty, value);
        }

        public static readonly StyledProperty<AvaloniaList<double>?> SpectrumProperty =
            AvaloniaProperty.Register<GlBackgroundControl, AvaloniaList<double>?>(nameof(Spectrum));

        public AvaloniaList<double>? Spectrum
        {
            get => GetValue(SpectrumProperty);
            set => SetValue(SpectrumProperty, value);
        }

        public static readonly StyledProperty<double> DeltaTimeProperty =
            AvaloniaProperty.Register<GlBackgroundControl, double>(nameof(DeltaTime), 0.0);

        public double DeltaTime
        {
            get => GetValue(DeltaTimeProperty);
            set => SetValue(DeltaTimeProperty, value);
        }

        public static readonly StyledProperty<double> PulseIntervalProperty =
            AvaloniaProperty.Register<GlBackgroundControl, double>(nameof(PulseInterval), 10.0);

        public double PulseInterval
        {
            get => GetValue(PulseIntervalProperty);
            set => SetValue(PulseIntervalProperty, value);
        }

        public static readonly StyledProperty<bool> AutoPulseProperty =
            AvaloniaProperty.Register<GlBackgroundControl, bool>(nameof(AutoPulse), true);

        public bool AutoPulse
        {
            get => GetValue(AutoPulseProperty);
            set => SetValue(AutoPulseProperty, value);
        }

        public static readonly StyledProperty<double> MaxZoomProperty =
            AvaloniaProperty.Register<GlBackgroundControl, double>(nameof(MaxZoom), 0.15);

        public double MaxZoom
        {
            get => GetValue(MaxZoomProperty);
            set => SetValue(MaxZoomProperty, value);
        }

        public static readonly StyledProperty<bool> DisableVSyncProperty =
            AvaloniaProperty.Register<GlBackgroundControl, bool>(nameof(DisableVSync), true);

        public bool DisableVSync
        {
            get => GetValue(DisableVSyncProperty);
            set => SetValue(DisableVSyncProperty, value);
        }
        #endregion

        private int _shaderProgram, _vbo, _textureId;
        private bool _texInitialized, _isEs;
        private Stopwatch _st = new Stopwatch();
        private double _lastFrameTime;
        private float _smoothAmplitude = 0f;
        private bool _vsyncDisabled = false;
        private DispatcherTimer? _uiHeartbeat;

        private delegate void GLUniform2f(int location, float x, float y);
        private delegate void GLUniform1f(int location, float x);
        private delegate void GLUniform1i(int location, int x);
        private delegate void GLBlendFunc(int sfactor, int dfactor);

        private GLUniform2f? _glUniform2f;
        private GLUniform1f? _glUniform1f;
        private GLUniform1i? _glUniform1i;
        private GLBlendFunc? _glBlendFunc;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int eglSwapIntervalDel(IntPtr dpy, int interval);
        private eglSwapIntervalDel? _eglSwapInterval;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool wglSwapIntervalEXTDel(int interval);
        private wglSwapIntervalEXTDel? _wglSwapIntervalEXT;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr eglGetCurrentDisplayDel();
        private eglGetCurrentDisplayDel? _eglGetCurrentDisplay;

        private readonly List<IDisposable> _propertySubscriptions = new();

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == BackgroundImageProperty) _texInitialized = false;
            // If the control just became visible, start the render loop again
            if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            {
                RequestNextFrameRendering();
            }
        }

        protected override void OnOpenGlInit(GlInterface gl)
        {
            _st.Start();
            _isEs = gl.GetString(0x1F02)?.Contains("OpenGL ES") == true;

            try
            {
                IntPtr pWglSwap = gl.GetProcAddress("wglSwapIntervalEXT");
                if (pWglSwap != IntPtr.Zero) _wglSwapIntervalEXT = Marshal.GetDelegateForFunctionPointer<wglSwapIntervalEXTDel>(pWglSwap);
                IntPtr pEglSwap = gl.GetProcAddress("eglSwapInterval");
                if (pEglSwap != IntPtr.Zero) _eglSwapInterval = Marshal.GetDelegateForFunctionPointer<eglSwapIntervalDel>(pEglSwap);
                IntPtr pGetDisplay = gl.GetProcAddress("eglGetCurrentDisplay");
                if (pGetDisplay != IntPtr.Zero) _eglGetCurrentDisplay = Marshal.GetDelegateForFunctionPointer<eglGetCurrentDisplayDel>(pGetDisplay);
            }
            catch { }

            _glUniform2f = Marshal.GetDelegateForFunctionPointer<GLUniform2f>(gl.GetProcAddress("glUniform2f"));
            _glUniform1f = Marshal.GetDelegateForFunctionPointer<GLUniform1f>(gl.GetProcAddress("glUniform1f"));
            _glUniform1i = Marshal.GetDelegateForFunctionPointer<GLUniform1i>(gl.GetProcAddress("glUniform1i"));
            _glBlendFunc = Marshal.GetDelegateForFunctionPointer<GLBlendFunc>(gl.GetProcAddress("glBlendFunc"));

            _shaderProgram = CreateProgram(gl, VertexShader, FragmentShader);
            _vbo = gl.GenBuffer();

            _uiHeartbeat = new DispatcherTimer(TimeSpan.FromMilliseconds(4), DispatcherPriority.Render, (_, _) =>
            {
                if (IsVisible) RequestNextFrameRendering();
            });
            _uiHeartbeat.Start();

            // If there are observable subscriptions to set up in future, add them to _propertySubscriptions
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            _uiHeartbeat?.Stop();
            try
            {
                if (_shaderProgram != 0) { gl.DeleteProgram(_shaderProgram); _shaderProgram = 0; }
                if (_vbo != 0) { gl.DeleteBuffer(_vbo); _vbo = 0; }
                if (_textureId != 0) { gl.DeleteTexture(_textureId); _textureId = 0; }
                _texInitialized = false;
            }
            catch { /* ignore GL cleanup failures */ }

            // Dispose any property subscriptions created by this control
            try
            {
                foreach (var d in _propertySubscriptions) d.Dispose();
            }
            catch { }
            _propertySubscriptions.Clear();
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (!IsVisible) return;

            if (DisableVSync && !_vsyncDisabled)
            {
                try
                {
                    _wglSwapIntervalEXT?.Invoke(0);
                    var dpy = _eglGetCurrentDisplay?.Invoke() ?? IntPtr.Zero;
                    _eglSwapInterval?.Invoke(dpy, 0);
                    _vsyncDisabled = true;
                }
                catch { }
            }

            double currentTime = _st.Elapsed.TotalSeconds;
            float dt = (float)(currentTime - _lastFrameTime);
            if (dt <= 0) dt = 1f / 120f;
            _lastFrameTime = currentTime;
            SetCurrentValue(DeltaTimeProperty, (double)dt);

            gl.Enable(0x0BE2); // GL_BLEND
            _glBlendFunc?.Invoke(0x0302, 0x0303);
            gl.ClearColor(0, 0, 0, 0);
            gl.Clear(0x4000);

            double scaling = VisualRoot?.RenderScaling ?? 1.0;
            int w = (int)(Bounds.Width * scaling);
            int h = (int)(Bounds.Height * scaling);
            gl.Viewport(0, 0, w, h);

            if (!_texInitialized && BackgroundImage != null) UpdateTexture(gl);
            if (_textureId == 0) return;

            gl.UseProgram(_shaderProgram);

            float imgW = (float)BackgroundImage!.PixelSize.Width;
            float imgH = (float)BackgroundImage!.PixelSize.Height;
            float viewRatio = (float)w / (float)h;
            float imgRatio = imgW / imgH;

            float scaleX = 1.0f, scaleY = 1.0f;
            if (Stretch == Stretch.Uniform)
            {
                if (imgRatio > viewRatio) scaleY = viewRatio / imgRatio;
                else scaleX = imgRatio / viewRatio;
            }
            else if (Stretch == Stretch.UniformToFill)
            {
                if (imgRatio > viewRatio) scaleX = imgRatio / viewRatio;
                else scaleY = viewRatio / imgRatio;
            }

            // Thread-safe Spectrum calculation
            float currentPulse = 0f;
            var spec = Spectrum;
            if (spec != null && spec.Count > 0)
            {
                double sum = 0;
                int count = 0;
                try
                {
                    count = spec.Count;
                    for (int i = 0; i < count; i++)
                    {
                        sum += spec[i];
                    }
                }
                catch { }

                if (count > 0)
                {
                    float avg = (float)(sum / count) / 45.0f;
                    _smoothAmplitude = _smoothAmplitude + (avg - _smoothAmplitude) * (dt * 12.0f);
                    currentPulse = Math.Clamp(_smoothAmplitude, 0.0f, 1.0f);
                }
            }
            else if (AutoPulse)
            {
                float progress = (float)(currentTime % PulseInterval / PulseInterval);
                currentPulse = (float)Math.Pow(Math.Sin(progress * Math.PI), 2.0);
            }

            float[] vertices = { -1f, -1f, 0f, 1f, 1f, -1f, 1f, 1f, -1f, 1f, 0f, 0f, 1f, 1f, 1f, 0f };
            gl.BindBuffer(0x8892, _vbo);
            fixed (float* p = vertices) gl.BufferData(0x8892, vertices.Length * 4, (IntPtr)p, 0x88E4);

            _glUniform1f?.Invoke(GetLocation(gl, "u_pulse"), currentPulse);
            _glUniform1f?.Invoke(GetLocation(gl, "u_maxZoom"), (float)MaxZoom);
            _glUniform2f?.Invoke(GetLocation(gl, "u_stretchScale"), scaleX, scaleY);

            gl.ActiveTexture(0x84C0);
            gl.BindTexture(0x0DE1, _textureId);
            _glUniform1i?.Invoke(GetLocation(gl, "u_texture"), 0);

            int posLoc = GetAttrib(gl, "position");
            gl.EnableVertexAttribArray(posLoc);
            gl.VertexAttribPointer(posLoc, 2, 0x1406, 0, 16, IntPtr.Zero);
            int uvLoc = GetAttrib(gl, "uv");
            gl.EnableVertexAttribArray(uvLoc);
            gl.VertexAttribPointer(uvLoc, 2, 0x1406, 0, 16, (IntPtr)8);

            gl.DrawArrays(0x0005, 0, 4);
            RequestNextFrameRendering();
        }

        private void UpdateTexture(GlInterface gl)
        {
            if (BackgroundImage == null) return;
            if (_textureId == 0) _textureId = gl.GenTexture();
            gl.BindTexture(0x0DE1, _textureId);
            var size = BackgroundImage.PixelSize;
            int totalSize = size.Width * size.Height * 4;
            byte[] pixels = new byte[totalSize];
            fixed (byte* p = pixels)
            {
                BackgroundImage.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (IntPtr)p, totalSize, size.Width * 4);
                if (_isEs)
                {
                    for (int i = 0; i < totalSize; i += 4)
                    {
                        byte b = pixels[i + 0]; byte r = pixels[i + 2];
                        pixels[i + 0] = r; pixels[i + 2] = b;
                    }
                    gl.TexImage2D(0x0DE1, 0, 0x1908, size.Width, size.Height, 0, 0x1908, 0x1401, (IntPtr)p);
                }
                else
                {
                    gl.TexImage2D(0x0DE1, 0, 0x1908, size.Width, size.Height, 0, 0x80E1, 0x1401, (IntPtr)p);
                }
            }
            gl.TexParameteri(0x0DE1, 0x2801, 0x2601);
            gl.TexParameteri(0x0DE1, 0x2800, 0x2601);
            _texInitialized = true;
        }

        private int GetLocation(GlInterface gl, string name)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
            fixed (byte* p = bytes) return gl.GetUniformLocation(_shaderProgram, (IntPtr)p);
        }

        private int GetAttrib(GlInterface gl, string name)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
            fixed (byte* p = bytes) return gl.GetAttribLocation(_shaderProgram, (IntPtr)p);
        }

        private int CreateProgram(GlInterface gl, string vs, string fs)
        {
            int v = gl.CreateShader(0x8B31); ShaderSource(gl, v, vs); gl.CompileShader(v);
            int f = gl.CreateShader(0x8B30); ShaderSource(gl, f, fs); gl.CompileShader(f);
            int p = gl.CreateProgram(); gl.AttachShader(p, v); gl.AttachShader(p, f); gl.LinkProgram(p);
            return p;
        }

        private void ShaderSource(GlInterface gl, int shader, string source)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(source);
            int length = bytes.Length;
            fixed (byte* p = bytes) { IntPtr pAddr = (IntPtr)p; gl.ShaderSource(shader, 1, new IntPtr(&pAddr), new IntPtr(&length)); }
        }

        #region Shaders
        private const string VertexShader = @"
            attribute vec2 position;
            attribute vec2 uv;
            varying vec2 v_uv;
            void main() {
                v_uv = uv;
                gl_Position = vec4(position, 0.0, 1.0);
            }";

        private const string FragmentShader = @"
            precision highp float;
            varying vec2 v_uv;
            uniform float u_pulse;
            uniform float u_maxZoom;
            uniform vec2 u_stretchScale;
            uniform sampler2D u_texture;

            void main() {
                float zoomIntensity = u_pulse * u_maxZoom;
                vec2 stretchedUv = (v_uv - 0.5) / u_stretchScale + 0.5;

                vec4 finalCol = vec4(0.0);
                const int samples = 14; // Increased samples for deeper zooms
                float totalWeight = 0.0;

                for(int i = 0; i < samples; i++) {
                    float s = 1.0 - zoomIntensity * (float(i) / float(samples - 1));
                    vec2 sampleUv = (stretchedUv - 0.5) * s + 0.5;
                    
                    if(sampleUv.x >= 0.0 && sampleUv.x <= 1.0 && sampleUv.y >= 0.0 && sampleUv.y <= 1.0) {
                        finalCol += texture2D(u_texture, sampleUv);
                        totalWeight += 1.0;
                    }
                }
                
                if (totalWeight > 0.0) {
                    gl_FragColor = finalCol / totalWeight;
                } else {
                    gl_FragColor = vec4(0.0);
                }
            }";
        #endregion
    }
}