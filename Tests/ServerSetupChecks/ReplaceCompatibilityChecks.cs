using System.Diagnostics;

internal static class ReplaceCompatibilityChecks
{
    internal static int Run(string fixtureRoot)
    {
        var helper = File.ReadAllText(Path.Combine("Scripts", "ServerSetup", "Manage-LinkSafeServer.ps1"));
        foreach (var destination in new[] { "$Path", "$target" })
            if (!helper.Contains($"[IO.File]::Replace($temp,{destination},[System.Management.Automation.Language.NullString]::Value)", StringComparison.Ordinal))
                throw new Exception("Installer no longer uses the verified PowerShell-compatible replacement call.");
        if (!helper.Contains("$clients = @(($json | ConvertFrom-Json))", StringComparison.Ordinal)
            || helper.Contains("$originalClients = @(Get-Content", StringComparison.Ordinal))
            throw new Exception("Installer no longer uses the verified PS 5.1 client array expression.");

        // Test .NET argument binding using ordinary PS commands, without executing the
        // installation helper or changing execution policy, services, or live data.
        const string command = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $root = $env:LINKSAFE_REPLACE_FIXTURE
            [IO.Directory]::CreateDirectory($root) | Out-Null
            $temp = Join-Path $root 'new content.tmp'
            $target = Join-Path $root 'existing config.json'
            [IO.File]::WriteAllText($temp, 'new')
            [IO.File]::WriteAllText($target, 'original')
            $rejected = $false
            try { [IO.File]::Replace($temp,$target,$null) } catch { $rejected = $true }
            if (-not $rejected) { throw 'The original PS 5.1 regression did not reproduce.' }
            if ([IO.File]::ReadAllText($target) -ne 'original') { throw 'Failed replacement changed the original.' }
            [IO.File]::Replace($temp,$target,[System.Management.Automation.Language.NullString]::Value)
            if ([IO.File]::ReadAllText($target) -ne 'new') { throw 'Corrected replacement failed.' }
            if ([IO.File]::Exists($temp)) { throw 'Successful replacement did not consume the source.' }
            $config = @{ BackupRoot='D:\UsersBackup'; AdminUsername='fixture-admin'; ServerUrl='http://127.0.0.1:5055' }
            foreach ($url in @('http://0.0.0.0:5055','http://127.0.0.1:5055')) {
                $config.ServerUrl = $url
                $config | ConvertTo-Json | Set-Content -LiteralPath $temp -Encoding UTF8
                [IO.File]::Replace($temp,$target,[System.Management.Automation.Language.NullString]::Value)
                $saved = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
                if ($saved.BackupRoot -ne 'D:\UsersBackup' -or $saved.AdminUsername -ne 'fixture-admin' -or $saved.ServerUrl -ne $url) { throw 'Config update/rollback lost a setting.' }
            }
            [IO.File]::WriteAllBytes($temp,[byte[]](1,2,3,0,255))
            [IO.File]::Replace($temp,$target,[System.Management.Automation.Language.NullString]::Value)
            if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($target)) -ne 'AQIDAP8=') { throw 'Binary replacement corrupted content.' }
            [IO.File]::WriteAllText($temp, 'must not replace locked target')
            $lock = [IO.File]::Open($target,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
            $rejected = $false
            try {
                try { [IO.File]::Replace($temp,$target,[System.Management.Automation.Language.NullString]::Value) } catch { $rejected = $true }
            } finally { $lock.Dispose() }
            if (-not $rejected -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($target)) -ne 'AQIDAP8=') { throw 'Locked target was not preserved.' }
            'PASS: 7 replacement regression checks on Windows PowerShell ' + $PSVersionTable.PSVersion
            $oldRoot = '\\fixture-server\UsersBackup$'
            $localRoot = 'D:\UsersBackup'
            $records = @(
                [pscustomobject]@{ BackupDestinationPath=($oldRoot + '\alice\PC01'); IsDisabled=$false },
                [pscustomobject]@{ BackupDestinationPath=($oldRoot + '\bob\PC02'); IsDisabled=$true }
            )
            $json = ConvertTo-Json -InputObject $records
            $old = @($json | ConvertFrom-Json)
            if ($old.Count -ne 1 -or $old[0] -isnot [array]) { throw 'The original PS 5.1 array regression did not reproduce.' }
            $destination = [string]$old[0].BackupDestinationPath
            $broken = $localRoot + '\' + $destination.Substring(($oldRoot + '\').Length)
            if ($broken -notmatch '[ .](\\|$)') { throw 'Combined destinations did not reproduce the reported path validation error.' }
            foreach ($count in @(0,1,2,10)) {
                $records = @(for ($i=0; $i -lt $count; $i++) {
                    [pscustomobject]@{ BackupDestinationPath=($oldRoot + '\user' + $i + '\PC' + $i); IsDisabled=($i -eq 1) }
                })
                $json = ConvertTo-Json -InputObject $records
                $clients = @(($json | ConvertFrom-Json))
                if ($clients.Count -ne $count) { throw 'Wrong client count with corrected expression.' }
                $mapped = @(foreach ($client in $clients) {
                    $copy = $client | ConvertTo-Json | ConvertFrom-Json
                    $prefix = $oldRoot.TrimEnd('\') + '\'
                    $destination = [string]$copy.BackupDestinationPath
                    $copy.BackupDestinationPath = $localRoot.TrimEnd('\') + '\' + $destination.Substring($prefix.Length)
                    $copy
                })
                $serialized = ConvertTo-Json -InputObject $mapped
                $roundtrip = @(($serialized | ConvertFrom-Json))
                if (-not $serialized.TrimStart().StartsWith('[') -or $roundtrip.Count -ne $count) { throw 'Migrated registry lost its array shape.' }
                for ($i=0; $i -lt $count; $i++) {
                    if ($roundtrip[$i].BackupDestinationPath -ne ($localRoot + '\user' + $i + '\PC' + $i) -or $roundtrip[$i].IsDisabled -ne ($i -eq 1)) { throw 'Incorrect migrated record.' }
                    if ($clients[$i].BackupDestinationPath -ne ($oldRoot + '\user' + $i + '\PC' + $i)) { throw 'Original rollback snapshot was changed.' }
                }
                $restored = @((ConvertTo-Json -InputObject $clients | ConvertFrom-Json))
                if ($restored.Count -ne $count) { throw 'Rollback lost records.' }
            }
            'PASS: 14 JSON array compatibility checks on Windows PowerShell ' + $PSVersionTable.PSVersion
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["LINKSAFE_REPLACE_FIXTURE"] = Path.Combine(fixtureRoot, "replace compatibility");
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Could not start Windows PowerShell compatibility checks.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(); throw new Exception("Replacement compatibility checks timed out."); }
        var stdout = output.GetAwaiter().GetResult();
        var stderr = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new Exception(stdout + stderr);
        Console.Write(stdout);
        return 21;
    }
}
