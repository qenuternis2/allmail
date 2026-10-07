using System.Diagnostics;
using System.Text;
using System.Windows;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.App.Services;

internal static class UninstallData
{
    public static async Task<int> RemoveAsync()
    {
        try
        {
            // A host crash can leave WebView2 alive after its file lease was released.
            // Refuse deletion if such a process exists, or if Windows cannot inspect it.
            const string script = """
                $ErrorActionPreference='Stop'
                try {
                  $root=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ProtonProfiles'
                  foreach($p in Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'") {
                    if(!$p.CommandLine) { exit 2 }
                    if($p.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0) { exit 1 }
                  }
                  exit 0
                } catch { exit 2 }
                """;
            var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось проверить процессы браузера.");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (process.ExitCode != 0) throw new InvalidOperationException("Не подтверждено завершение процессов WebView2. Данные сохранены; закройте браузерные процессы профилей и повторите удаление.");
            var credentials = new WindowsCredentialStore();
            var result = ManagedDataRemoval.Remove(ManagedPaths.ForCurrentUser(), credentials, credentials.ListManagedProfileIds());
            if (!result.IsComplete) throw new InvalidOperationException(result.Reason);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Удаление данных не завершено. Оставшиеся данные сохранены.\n\n" + ex.Message,
                "SecureBrowser — удаление данных", MessageBoxButton.OK, MessageBoxImage.Error);
            return 5;
        }
    }
}
