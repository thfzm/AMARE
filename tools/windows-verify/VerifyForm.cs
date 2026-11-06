using System.Text.Json;
using System.Windows.Forms;

namespace WindowsVerify;

internal sealed class VerifyForm : Form
{
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9),
    };

    private readonly Button _run = new()
    {
        Text = "Run Phase 0.5 probes",
        Dock = DockStyle.Top,
        Height = 36,
    };

    private readonly Label _status = new()
    {
        Text = "Ready.",
        Dock = DockStyle.Top,
        Height = 26,
        Padding = new Padding(8, 4, 8, 0),
    };

    private readonly List<ProbeResult> _results = [];
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "amare-phase05",
        DateTime.Now.ToString("yyyyMMdd-HHmmss"));

    public VerifyForm()
    {
        Text = "AMARE Phase 0.5 — Windows probes";
        Width = 1100;
        Height = 780;

        if (Environment.GetEnvironmentVariable("AMARE_VERIFY_HEADLESS") == "1")
        {
            ShowInTaskbar = false;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;
            // Keep a real client area so WebView2 can process CDP mouse input
            // in headless CI; a 1x1 hidden form clips the hit-test area.
            Size = new Size(1100, 780);
        }

        _run.Click += async (_, _) => await RunAllAsync();
        Controls.Add(_log);
        Controls.Add(_status);
        Controls.Add(_run);

        if (Environment.GetEnvironmentVariable("AMARE_VERIFY_AUTORUN") == "1")
        {
            Shown += async (_, _) => await RunAllAsync();
        }
    }

    private bool _running;

    private async Task RunAllAsync()
    {
        if (_running) return;
        _running = true;
        _run.Enabled = false;
        _results.Clear();
        _log.Clear();
        var startedAt = DateTimeOffset.UtcNow;
        EnvironmentInfo? environment = null;

        try
        {
            Directory.CreateDirectory(_root);
            environment = EnvironmentProbe.Capture(_root);
            Log($"environment: {environment.OsVersion} / {environment.Architecture}");
            Log($"cpu: {environment.Cpu}");
            Log($"ram: {environment.RamBytes / 1024 / 1024 / 1024} GiB");
            Log($"gpu: {environment.Gpu}");
            Log($"webview2: {environment.WebView2Version}");
            Log($"dotnet: {environment.DotNet}");
            Log($"test_root: {_root}");
            Log("");

            await Add("webview2_local_navigation", () => Phase21Probes.LocalNavigationAsync(this, _root));
            await Add("webview2_web_compat", () => Phase21Probes.WebCompatibilityAsync(this, _root));
            await Add("webview2_https_navigation", () => Phase21Probes.HttpsNavigationAsync(this, _root));
            await Add("webview2_certificate_error", () => Phase21Probes.KestrelCertificateErrorAsync(this, _root));
            await Add("webview2_certificate_expired", () => Phase21Probes.KestrelCertificateExpiredAsync(this, _root));
            await Add("webview2_certificate_hostname_mismatch", () => Phase21Probes.KestrelCertificateHostnameMismatchAsync(this, _root));
            await Add("webview2_local_trusted_ca", () => Phase21Probes.LocalTrustedCaAsync(this, _root));
            await Add("webview2_tab_lifecycle_10", () => Phase21Probes.TabLifecycleAsync(this, _root));
            await Add("profile_isolation", () => Probes.ProfileIsolationAsync(this, _root));
            await Add("cdp_dom_click_input_scroll", () => Probes.CdpInteractionAsync(this, _root));

            if (Environment.GetEnvironmentVariable("AMARE_VERIFY_MANUAL") == "0")
            {
                AddResult(new ProbeResult(
                    "user_intervention_detection",
                    ProbeStatus.Unverified,
                    false,
                    "Skipped by AMARE_VERIFY_MANUAL=0. Run interactively for this probe.",
                    DateTimeOffset.UtcNow));
            }
            else
            {
                await Add("user_intervention_detection", () => Probes.UserInterventionAsync(this, _root, SetStatus));
            }

            if (Environment.GetEnvironmentVariable("AMARE_VERIFY_MEMORY") == "0")
            {
                AddResult(new ProbeResult(
                    "windows_browser_performance",
                    ProbeStatus.Unverified,
                    false,
                    "Skipped by AMARE_VERIFY_MEMORY=0. Run on Windows 11 target hardware.",
                    DateTimeOffset.UtcNow));
                AddResult(new ProbeResult(
                    "memory_30_tabs",
                    ProbeStatus.Unverified,
                    false,
                    "Skipped by AMARE_VERIFY_MEMORY=0. Run on target hardware for the 30-tab measurement.",
                    DateTimeOffset.UtcNow));
            }
            else
            {
                await Add("windows_browser_performance", () => Phase21Probes.BrowserPerformanceAsync(this, _root));
                await Add("memory_30_tabs", () => Probes.Memory30TabsAsync(this, _root, SetStatus));
            }

            if (Environment.GetEnvironmentVariable("AMARE_VERIFY_DRM") == "0")
            {
                AddResult(new ProbeResult(
                    "drm_capability_matrix",
                    ProbeStatus.Unverified,
                    false,
                    "Skipped by AMARE_VERIFY_DRM=0 (CI runner DRM/VMP is not representative).",
                    DateTimeOffset.UtcNow));
            }
            else
            {
                await Add("drm_capability_matrix", () => Probes.DrmMatrixAsync(this, _root));
            }

            AddResult(Probes.UpdateInstaller());
            AddResult(Probes.WindowsKeychain());
            AddResult(Probes.PaidDrmServices());
            AddResult(Probes.DrmPlaybackManual());
            AddResult(new ProbeResult(
                "local_data_encryption_logic",
                ProbeStatus.Unverified,
                false,
                "Crypto logic is tested by cargo test -p amare-security; Windows DPAPI/Keystore integration is not tested here.",
                DateTimeOffset.UtcNow));
            AddResult(new ProbeResult(
                "update_signing_logic",
                ProbeStatus.Unverified,
                false,
                "Signature/rollback logic is tested by cargo test -p amare-update; Windows installer is not implemented.",
                DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            Log("FATAL: " + exception);
            AddResult(new ProbeResult(
                "run",
                ProbeStatus.Fail,
                true,
                exception.ToString(),
                DateTimeOffset.UtcNow));
        }
        finally
        {
            var finishedAt = DateTimeOffset.UtcNow;
            if (environment is null)
            {
                environment = EnvironmentProbe.Capture(_root);
            }
            WriteReport(new RunReport(
                "1",
                startedAt,
                finishedAt,
                environment!,
                _results,
                BuildSummary()));
            Log("");
            Log($"Done. {BuildSummary()}");
            Log("Results: windows-verify-results.json");
            SetStatus("Done. Review windows-verify-results.json.");
            Environment.ExitCode = _results.Any(r => r.Status == ProbeStatus.Fail) ? 1 : 0;
            _run.Enabled = true;
            _running = false;
            if (Environment.GetEnvironmentVariable("AMARE_VERIFY_HEADLESS") == "1")
            {
                Close();
            }
        }
    }

    private async Task Add(string name, Func<Task<ProbeResult>> probe)
    {
        Log($"RUN {name}");
        try
        {
            var result = await probe();
            AddResult(result);
        }
        catch (Exception exception)
        {
            AddResult(new ProbeResult(
                name,
                ProbeStatus.Fail,
                true,
                exception.ToString(),
                DateTimeOffset.UtcNow));
        }
    }

    private void AddResult(ProbeResult result)
    {
        _results.Add(result);
        Log($"{result.Status} {result.Name} (executed={result.Executed}): {result.Detail}");
    }

    private string BuildSummary()
    {
        var pass = _results.Count(r => r.Status == ProbeStatus.Pass);
        var fail = _results.Count(r => r.Status == ProbeStatus.Fail);
        var unverified = _results.Count(r => r.Status == ProbeStatus.Unverified);
        var skipped = _results.Count(r => r.Status == ProbeStatus.Skipped);
        return $"PASS={pass} FAIL={fail} UNVERIFIED={unverified} SKIPPED={skipped}";
    }

    private void WriteReport(RunReport report)
    {
        var output = Environment.GetEnvironmentVariable("AMARE_VERIFY_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            output = Environment.CurrentDirectory;
        }
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(_root);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "windows-verify-results.json"), json);
        File.WriteAllText(Path.Combine(_root, "windows-verify-results.json"), json);
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.Refresh();
        Console.WriteLine($"STATUS {text}");
    }

    private void Log(string text)
    {
        _log.AppendText(text + Environment.NewLine);
        Console.WriteLine(text);
    }
}
