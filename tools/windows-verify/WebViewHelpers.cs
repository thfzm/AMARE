using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Windows.Forms;

namespace WindowsVerify;

internal static class WebViewHelpers
{
    public static Task<CoreWebView2Environment> CreateEnvironmentAsync(string userDataFolder)
    {
        Directory.CreateDirectory(userDataFolder);
        return CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
    }

    public static async Task<(CoreWebView2Environment Env, WebView2 View)> CreateViewAsync(
        Control host,
        string userDataFolder)
    {
        var env = await CreateEnvironmentAsync(userDataFolder);
        var view = await CreateViewWithEnvironmentAsync(host, env);
        return (env, view);
    }

    public static async Task<WebView2> CreateViewWithEnvironmentAsync(
        Control host,
        CoreWebView2Environment env)
    {
        var view = new WebView2
        {
            Width = 800,
            Height = 600,
            Visible = false,
        };
        host.Controls.Add(view);
        await view.EnsureCoreWebView2Async(env);
        return view;
    }

    public static void SetVirtualHost(CoreWebView2 core, string hostName, string folder)
    {
        core.SetVirtualHostNameToFolderMapping(
            hostName,
            folder,
            CoreWebView2HostResourceAccessKind.Allow);
    }

    public static async Task NavigateAsync(WebView2 view, string url, int timeoutSeconds = 30)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            view.NavigationCompleted -= Handler;
            if (args.IsSuccess) completion.TrySetResult(true);
            else completion.TrySetException(new InvalidOperationException($"navigation failed: {args.WebErrorStatus}"));
        }

        view.NavigationCompleted += Handler;
        view.CoreWebView2.Navigate(url);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
    }

    public static async Task<JsonElement> EvalAsync(WebView2 view, string expression)
    {
        var response = await view.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "Runtime.evaluate",
            JsonSerializer.Serialize(new { expression, returnByValue = true, awaitPromise = true }));
        using var document = JsonDocument.Parse(response);
        var result = document.RootElement.GetProperty("result");
        if (result.TryGetProperty("exceptionDetails", out var exception))
            throw new InvalidOperationException("JS exception: " + exception.GetRawText());
        var value = result.GetProperty("result");
        if (!value.TryGetProperty("value", out var valueElement))
            throw new InvalidOperationException("CDP result has no value: " + value.GetRawText());
        return valueElement.Clone();
    }

    public static async Task<string> EvalStringAsync(WebView2 view, string expression)
    {
        var value = await EvalAsync(view, expression);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
    }

    public static async Task<(double X, double Y)> RectAsync(WebView2 view, string selector)
    {
        var expression = $"(() => {{ const r = document.querySelector({JsonSerializer.Serialize(selector)}).getBoundingClientRect(); return {{x:r.left+r.width/2,y:r.top+r.height/2}}; }})()";
        var rect = await EvalAsync(view, expression);
        return (rect.GetProperty("x").GetDouble(), rect.GetProperty("y").GetDouble());
    }

    public static async Task ClickSelectorAsync(WebView2 view, string selector)
    {
        var (x, y) = await RectAsync(view, selector);
        var pressed = JsonSerializer.Serialize(new
        {
            type = "mousePressed",
            x,
            y,
            button = "left",
            clickCount = 1
        });
        var released = JsonSerializer.Serialize(new
        {
            type = "mouseReleased",
            x,
            y,
            button = "left",
            clickCount = 1
        });
        await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", pressed);
        await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", released);
        await Task.Delay(120);
    }

    public static Task InsertTextAsync(WebView2 view, string text) =>
        view.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "Input.insertText",
            JsonSerializer.Serialize(new { text }));

    public static Task ScrollAsync(WebView2 view, double x, double y, int yDistance) =>
        view.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "Input.synthesizeScrollGesture",
            JsonSerializer.Serialize(new { x, y, yDistance, speed = 800 }));

    public static void DisposeView(Control host, WebView2 view)
    {
        host.Controls.Remove(view);
        view.Dispose();
    }
}
