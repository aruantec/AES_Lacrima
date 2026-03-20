using AES_Controls.Helpers;

namespace AES_Controls.Tests;

public sealed class SimpleObserverTests
{
    [Fact]
    public void Constructor_ThrowsWhenCallbackIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SimpleObserver<int>(null!));
    }

    [Fact]
    public void OnNext_ForwardsValueToCallback()
    {
        var observed = 0;
        var observer = new SimpleObserver<int>(value => observed = value);

        observer.OnNext(42);

        Assert.Equal(42, observed);
    }
}
