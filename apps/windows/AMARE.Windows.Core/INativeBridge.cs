using System.Text.Json;

namespace Amare.Windows.Core;

public interface INativeBridge : IDisposable
{
    Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
}
