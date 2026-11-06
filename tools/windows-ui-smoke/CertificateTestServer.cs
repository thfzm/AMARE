using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

internal sealed class CertificateTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string Url { get; }

    private CertificateTestServer(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public static Task<CertificateTestServer> StartAsync() => StartAsync("self-signed");

    public static async Task<CertificateTestServer> StartAsync(string kind)
    {
        using var key = RSA.Create(2048);
        var dnsName = kind == "hostname-mismatch" ? "wrong.example" : "localhost";
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        if (kind != "hostname-mismatch") san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        var expired = kind == "expired";
        using var ephemeral = request.CreateSelfSigned(
            expired ? DateTimeOffset.UtcNow.AddDays(-30) : DateTimeOffset.UtcNow.AddDays(-1),
            expired ? DateTimeOffset.UtcNow.AddDays(-2) : DateTimeOffset.UtcNow.AddDays(30));
        var pfx = ephemeral.Export(X509ContentType.Pfx);
        var certificate = new X509Certificate2(
            pfx, (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(CertificateTestServer).Assembly.GetName().Name,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.MapGet("/", () => Results.Content(
            "<!doctype html><title>Certificate test</title><h1>certificate test</h1>", "text/html"));
        await app.StartAsync();
        var url = app.Urls.First();
        return new CertificateTestServer(app, url);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
