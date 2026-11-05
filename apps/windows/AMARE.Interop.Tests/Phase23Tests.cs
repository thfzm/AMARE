using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase23Tests
{
    public static void Register()
    {
        Program.Run("windows_0_17_semver", () =>
        {
            if (SemanticVersion.Compare("0.17.0", "0.16.9") <= 0) throw new InvalidOperationException("minor ordering");
            if (SemanticVersion.Compare("0.17.0-beta.1", "0.17.0") >= 0) throw new InvalidOperationException("prerelease ordering");
            if (SemanticVersion.Compare("v1.0.0", "1.0.0") != 0) throw new InvalidOperationException("v prefix");
            if (!SemanticVersion.TryParse("v0.17.0-beta.2", out _)) throw new InvalidOperationException("parse");
            return "semver ordering";
        });

        Program.Run("windows_0_17_update_selection", () =>
        {
            var releases = new[]
            {
                Release("v0.18.0-beta.1", true, "AMARE-0.18.0-win-x64.zip"),
                Release("v0.17.0", false, "AMARE-0.17.0-win-x64.zip"),
                Release("v0.16.0", false, "AMARE-0.16.0-win-x64.zip")
            };
            var stable = GitHubReleaseClient.SelectUpdate("0.16.0", UpdateChannel.Stable, releases);
            if (!stable.Succeeded || stable.Update?.Version != "0.17.0" || stable.Update.Checksum == null)
                throw new InvalidOperationException("stable selection");
            var pre = GitHubReleaseClient.SelectUpdate("0.16.0", UpdateChannel.Prerelease, releases);
            if (pre.Update?.Version != "0.18.0-beta.1")
                throw new InvalidOperationException("prerelease selection");
            var none = GitHubReleaseClient.SelectUpdate("0.17.0", UpdateChannel.Stable, releases);
            if (none.UpdateAvailable) throw new InvalidOperationException("unexpected update");
            return $"stable={stable.Update.Version} pre={pre.Update.Version}";
        });

        Program.Run("windows_0_17_update_requires_checksum", () =>
        {
            var release = Release("v0.17.1", false, "AMARE-0.17.1-win-x64.zip", includeChecksum: false);
            var result = GitHubReleaseClient.SelectUpdate("0.17.0", UpdateChannel.Stable, [release]);
            if (result.Update?.Checksum != null) throw new InvalidOperationException("checksum should be absent");
            return "checksum absent detected";
        });

        Program.Run("windows_0_17_ai_request_and_parse", () =>
        {
            var request = AiApiClient.BuildRequest("gpt-test", "Summarize this", "url=https://example.com");
            if (!request.Contains("\"model\":\"gpt-test\"") || !request.Contains("\"stream\":false"))
                throw new InvalidOperationException("request shape");
            if (!request.Contains("untrusted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("untrusted context instruction");
            var content = AiApiClient.ParseContent("""{"choices":[{"message":{"content":"hello"}}]}""");
            if (content != "hello") throw new InvalidOperationException("content parse");
            var delta = AiApiClient.ParseDelta("""{"choices":[{"delta":{"content":"hel"}}]}""");
            if (delta != "hel") throw new InvalidOperationException("delta parse");
            if (AiApiClient.ParseDelta("[DONE]") != null) throw new InvalidOperationException("done parse");
            return "request/response parsing";
        });
    }

    private static ReleaseInfo Release(string tag, bool prerelease, string assetName, bool includeChecksum = true)
    {
        var assets = new List<ReleaseAsset>
        {
            new(assetName, $"https://example.invalid/{assetName}", 1234)
        };
        if (includeChecksum)
        {
            assets.Add(new ReleaseAsset(assetName + ".sha256", $"https://example.invalid/{assetName}.sha256", 65));
        }
        return new ReleaseInfo(tag, tag, "notes", prerelease, false, "https://example.invalid/release", assets);
    }
}
