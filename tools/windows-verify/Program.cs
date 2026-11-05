using System.Windows.Forms;

namespace WindowsVerify;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var ci = args.Any(arg => string.Equals(arg, "--ci", StringComparison.OrdinalIgnoreCase))
                 || Environment.GetEnvironmentVariable("AMARE_VERIFY_CI") == "1";
        if (ci)
        {
            Environment.SetEnvironmentVariable("AMARE_VERIFY_HEADLESS", "1");
            Environment.SetEnvironmentVariable("AMARE_VERIFY_AUTORUN", "1");
            Environment.SetEnvironmentVariable("AMARE_VERIFY_MANUAL", "0");
            if (!string.Equals(Environment.GetEnvironmentVariable("AMARE_VERIFY_MEMORY"), "1", StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable("AMARE_VERIFY_MEMORY", "0");
            }
            Environment.SetEnvironmentVariable("AMARE_VERIFY_DRM", "0");
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new VerifyForm());
        return Environment.ExitCode;
    }
}
