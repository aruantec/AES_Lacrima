using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using AES_Core.Logging;
using log4net;

namespace AES_Controls.Helpers.Windows;

/// <summary>
/// Minimal client for the Virtual Display Driver named-pipe protocol.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VddPipeClient
{
    private static readonly ILog Log = LogHelper.For(typeof(VddPipeClient));

    public const string PipeName = "MTTVirtualDisplayPipe";

    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _readTimeout;

    public VddPipeClient(TimeSpan? connectTimeout = null, TimeSpan? readTimeout = null)
    {
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("PING", cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(response) &&
               response.Contains("PONG", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> SetDisplayCountAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        var response = await SendCommandAsync($"SETDISPLAYCOUNT {count}", cancellationToken).ConfigureAwait(false);
        return response != null;
    }

    public async Task<string?> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command is required.", nameof(command));

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(_connectTimeout);

            try
            {
                await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (TimeoutException)
            {
                return null;
            }

            var payload = Encoding.Unicode.GetBytes(command);
            await pipe.WriteAsync(payload, connectCts.Token).ConfigureAwait(false);
            await pipe.FlushAsync(connectCts.Token).ConfigureAwait(false);

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(_readTimeout);

            using var responseStream = new MemoryStream();
            var buffer = new byte[4096];
            while (pipe.CanRead)
            {
                int read;
                try
                {
                    read = await pipe.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (read <= 0)
                    break;

                responseStream.Write(buffer, 0, read);
            }

            if (responseStream.Length == 0)
                return null;

            return Encoding.UTF8.GetString(responseStream.ToArray());
        }
        catch (Exception ex)
        {
            Log.Debug($"VDD pipe command failed: {command}", ex);
            return null;
        }
    }
}
