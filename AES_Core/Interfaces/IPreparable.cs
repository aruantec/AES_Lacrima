using System.Threading;
using System.Threading.Tasks;

namespace AES_Core.Interfaces;

/// <summary>
/// Represents an object that requires asynchronous preparation before use.
/// Implementations should perform any expensive or asynchronous initialization
/// work in <see cref="PrepareAsync"/> and expose readiness via
/// <see cref="IsPrepared"/>.
/// </summary>
public interface IPreparable
{
    /// <summary>
    /// Perform asynchronous preparation work. This method should be safe to
    /// call multiple times; implementations may return immediately when the
    /// instance is already prepared. Cancellation may be observed via the
    /// provided <paramref name="ct"/>.
    /// </summary>
    /// <param name="ct">Cancellation token that can cancel the preparation.</param>
    /// <returns>A task that completes when preparation is finished.</returns>
    Task PrepareAsync(CancellationToken ct = default);

    /// <summary>
    /// Indicates whether the instance has completed preparation and is ready
    /// for use. Implementations should update this flag when
    /// <see cref="PrepareAsync"/> has successfully finished.
    /// </summary>
    bool IsPrepared { get; }
}
