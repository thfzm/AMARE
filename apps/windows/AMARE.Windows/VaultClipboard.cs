using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Amare.Windows;

/// <summary>
/// Explicit clipboard copy with a best-effort 30-second cleanup. Cleanup only
/// happens when the clipboard still contains the exact value copied by AMARE.
/// Windows Clipboard History / other applications remain outside our control.
/// </summary>
internal sealed class VaultClipboard
{
    private static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _clearCancellation;
    private string? _ownedText;

    public VaultClipboard(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public void Copy(string password)
    {
        _clearCancellation?.Cancel();
        _ownedText = password;
        var package = new DataPackage();
        package.SetText(password);
        Clipboard.SetContent(package);
        var cts = new CancellationTokenSource();
        _clearCancellation = cts;
        _ = ClearLaterAsync(cts.Token);
    }

    public async Task ClearIfOwnedAsync()
    {
        var owned = _ownedText;
        _ownedText = null;
        if (owned is null)
        {
            return;
        }
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }
            var current = await content.GetTextAsync();
            if (string.Equals(current, owned, StringComparison.Ordinal))
            {
                Clipboard.Clear();
            }
        }
        catch
        {
            // Never delete unrelated clipboard data on failure.
        }
    }

    public void Forget()
    {
        _clearCancellation?.Cancel();
        _clearCancellation = null;
        _ownedText = null;
    }

    private async Task ClearLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ClearAfter, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await EnqueueAsync(ClearIfOwnedAsync);
    }

    private Task EnqueueAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetResult();
        }
        return completion.Task;
    }
}
