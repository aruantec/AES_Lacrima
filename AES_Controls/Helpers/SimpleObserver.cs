namespace AES_Controls.Helpers;

/// <summary>
/// Lightweight <see cref="IObserver{T}"/> implementation that delegates
/// <see cref="OnNext(T)"/> calls to the provided callback. Useful for
/// subscribing to simple observable sequences without creating a full
/// observer implementation.
/// </summary>
/// <typeparam name="T">Type of the values observed.</typeparam>
public class SimpleObserver<T> : IObserver<T>
{
    private readonly Action<T> _onNext;

    /// <summary>
    /// Create a new <see cref="SimpleObserver{T}"/> that invokes <paramref name="onNext"/>
    /// for each value produced by the observable sequence.
    /// </summary>
    /// <param name="onNext">Callback invoked for each observed value. Must not be <c>null</c>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="onNext"/> is <c>null</c>.</exception>
    public SimpleObserver(Action<T> onNext) { _onNext = onNext ?? throw new ArgumentNullException(nameof(onNext)); }

    /// <summary>
    /// Invoked when the observable sequence produces a new value. Delegates to the
    /// callback supplied to the constructor.
    /// </summary>
    /// <param name="value">The value produced by the sequence.</param>
    public void OnNext(T value) => _onNext(value);

    /// <summary>
    /// Invoked when the observable sequence signals an error. The default
    /// implementation ignores the error; override or replace if logging or
    /// error handling is required.
    /// </summary>
    /// <param name="error">The error that occurred.</param>
    public void OnError(Exception error) { /* optional logging */ }

    /// <summary>
    /// Invoked when the observable sequence completes. Default implementation
    /// does nothing.
    /// </summary>
    public void OnCompleted() { }
}
