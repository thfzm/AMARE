using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase27Tests
{
    public static void Register()
    {
        Program.Run("vault_idle_policy_expires_once_and_resets", IdlePolicy);
    }

    private static string IdlePolicy()
    {
        var start = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
        var policy = new VaultIdlePolicy(TimeSpan.FromMinutes(5), start);
        Assert(!policy.TryConsumeExpiration(start.AddMinutes(4)), "early");
        var five = start.AddMinutes(5);
        Assert(policy.TryConsumeExpiration(five), "expire");
        Assert(!policy.TryConsumeExpiration(five.AddMinutes(1)), "once");
        Assert(policy.IsExpired(five.AddMinutes(1)), "still expired");
        policy.Touch(five.AddMinutes(2));
        Assert(!policy.TryConsumeExpiration(five.AddMinutes(6)), "reset");
        Assert(policy.TryConsumeExpiration(five.AddMinutes(7)), "second expire");
        return "5 minute idle policy verified";
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
