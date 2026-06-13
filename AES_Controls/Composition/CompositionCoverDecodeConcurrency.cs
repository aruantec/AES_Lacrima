using System.Threading;

namespace AES_Controls.Composition;

/// <summary>
/// Limits concurrent cover decode work so scrolling and composition stay responsive.
/// </summary>
internal static class CompositionCoverDecodeConcurrency
{
    public static readonly SemaphoreSlim Gate = new(2, 2);
}
