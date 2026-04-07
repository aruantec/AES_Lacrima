using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace AES_Lacrima.Android;

[Application]
public class Application : AvaloniaAndroidApplication<App>
{
    protected Application(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 256000000 })
            .LogToTrace();
    }
}
