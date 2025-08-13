using System.Diagnostics;
using System.IO;

namespace AzerothReforged.Launcher
{
    public static class LaunchHelper
    {
        public static void LaunchConfig(string configExePath)
        {
            if (File.Exists(configExePath))
                Process.Start(new ProcessStartInfo { FileName = configExePath, UseShellExecute = true });
        }

        public static void LaunchGame(string wowPath, string args = "")
        {
            if (!File.Exists(wowPath)) return;
            var psi = new ProcessStartInfo
            {
                FileName = wowPath,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(wowPath)!,
                UseShellExecute = false
            };
            Process.Start(psi);
        }
    }
}
