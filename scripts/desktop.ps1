#Requires -Version 5.1
<#
.SYNOPSIS
Installs or manages Athena Desktop for the current Windows user.
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\desktop.ps1 -Action Install -McpSettingsPath "$env:LOCALAPPDATA\AthenaDesktop\mcp.json"
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\desktop.ps1 -Action Stop
#>
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Start', 'Stop', 'Status', 'Uninstall')]
    [string] $Action = 'Install',
    [string] $InstallDirectory = (Join-Path $env:LOCALAPPDATA 'AthenaDesktop\app'),
    [string] $McpSettingsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryDirectory = Split-Path -Parent $PSScriptRoot
$installPath = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$executablePath = Join-Path $installPath 'Athena.Desktop.exe'
$manifestPath = Join-Path $installPath 'athena-desktop.install.json'
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$shortcutPath = Join-Path $startupDirectory 'Athena Desktop.lnk'
$hasMcpSettings = $PSBoundParameters.ContainsKey('McpSettingsPath')

if ($hasMcpSettings -and $Action -ne 'Install') {
    throw '-McpSettingsPath configures the installation; use it with -Action Install.'
}

function Assert-InstallPath {
    $protectedPaths = @(
        [IO.Path]::GetPathRoot($installPath).TrimEnd([IO.Path]::DirectorySeparatorChar),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        $env:LOCALAPPDATA,
        $startupDirectory,
        $repositoryDirectory
    )
    foreach ($protectedPath in $protectedPaths) {
        if ($installPath.Equals($protectedPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Use a dedicated application directory for -InstallDirectory.'
        }
    }
    $ancestorPath = $installPath
    while ($ancestorPath) {
        if (Test-Path -LiteralPath $ancestorPath) {
            $item = Get-Item -LiteralPath $ancestorPath -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Installation path must contain only regular directories: $ancestorPath"
            }
        }
        $ancestorPath = Split-Path -Parent $ancestorPath
    }
}

function Get-OwnedFilePath([string] $RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw 'Invalid file in the installation manifest.'
    }
    $filePath = [IO.Path]::GetFullPath((Join-Path $installPath $RelativePath))
    if (-not $filePath.StartsWith($installPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installation manifest contains a file outside its application directory.'
    }
    # Refuse symlinks and junctions before copying or deleting any owned file.
    $itemPath = $filePath
    while ($itemPath -and -not $itemPath.Equals($installPath, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $itemPath) {
            $item = Get-Item -LiteralPath $itemPath -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Installation contains a symlink or junction: $itemPath"
            }
        }
        $itemPath = Split-Path -Parent $itemPath
    }
    return $filePath
}

function Read-Manifest {
    $null = Get-OwnedFilePath 'athena-desktop.install.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return $null
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Application -ne 'Athena.Desktop' -or $manifest.Version -ne 1 -or
        -not $installPath.Equals([string] $manifest.InstallDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'This directory does not contain a valid Athena Desktop installation manifest.'
    }
    foreach ($relativePath in $manifest.Files) {
        if ($relativePath -eq 'athena-desktop.install.json') { throw 'The manifest cannot own itself.' }
        $null = Get-OwnedFilePath $relativePath
    }
    return $manifest
}

function Write-Manifest([string[]] $Files) {
    $null = Get-OwnedFilePath 'athena-desktop.install.json'
    [ordered]@{
        Application = 'Athena.Desktop'
        Version = 1
        InstallDirectory = $installPath
        Files = @($Files)
    } | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}

function Invoke-Control([string] $Argument) {
    $process = Start-Process -FilePath $executablePath -ArgumentList $Argument -WorkingDirectory $installPath -WindowStyle Hidden -PassThru -Wait
    return $process.ExitCode
}

function Stop-Desktop {
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { return }
    $currentProcess = [Diagnostics.Process]::GetCurrentProcess()
    try { $currentSessionId = $currentProcess.SessionId }
    finally { $currentProcess.Dispose() }
    $installedProcesses = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
    try {
        foreach ($process in (Get-Process -Name 'Athena.Desktop' -ErrorAction SilentlyContinue)) {
            $matched = $false
            try {
                if ($process.SessionId -eq $currentSessionId -and
                    $executablePath.Equals($process.Path, [StringComparison]::OrdinalIgnoreCase)) {
                    $installedProcesses.Add($process)
                    $matched = $true
                }
            } catch {
                # An exited or inaccessible process cannot be identified as this installation.
                Write-Verbose "Could not inspect process $($process.Id): $($_.Exception.Message)"
            } finally {
                if (-not $matched) { $process.Dispose() }
            }
        }
        if ((Invoke-Control '--stop') -ne 0) {
            throw 'Athena Desktop did not stop. Check %LOCALAPPDATA%\AthenaDesktop\logs\athena.log before retrying.'
        }
        # Mutex release precedes the final process exit; wait until installed files are unlocked.
        foreach ($process in $installedProcesses) {
            if (-not $process.WaitForExit(10000)) {
                throw "Athena Desktop process $($process.Id) has not exited. Installation files were not changed."
            }
        }
    } finally {
        foreach ($process in $installedProcesses) { $process.Dispose() }
    }
}

function Start-Desktop {
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw 'Athena Desktop is not installed here. Run -Action Install first.'
    }
    $process = Start-Process -FilePath $executablePath -WorkingDirectory $installPath -WindowStyle Hidden -PassThru
    if ($process.WaitForExit(1500) -and $process.ExitCode -ne 0) {
        throw 'Athena Desktop could not start. Check %LOCALAPPDATA%\AthenaDesktop\logs\athena.log.'
    }
    Write-Host 'Athena Desktop started. Use the notification area icon to view logs or exit.'
}

function Assert-ShortcutOwnership {
    if (Test-Path -LiteralPath $shortcutPath) {
        if ((Get-Item -LiteralPath $shortcutPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Refusing to overwrite or remove a Startup shortcut that is a symlink.'
        }
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        if (-not $executablePath.Equals($shortcut.TargetPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "A Startup shortcut already uses '$shortcutPath' for another location. Uninstall that installation first."
        }
    }
}

function Remove-OwnedFiles([string[]] $Files) {
    # Delete only files recorded by this installer. Keep unrelated files and directories.
    $directories = @{}
    foreach ($relativePath in $Files) {
        $filePath = Get-OwnedFilePath $relativePath
        if (Test-Path -LiteralPath $filePath -PathType Leaf) {
            Remove-Item -LiteralPath $filePath -Force
        }
        $directoryPath = Split-Path -Parent $filePath
        while ($directoryPath -and -not $directoryPath.Equals($installPath, [StringComparison]::OrdinalIgnoreCase)) {
            $directories[$directoryPath] = $true
            $directoryPath = Split-Path -Parent $directoryPath
        }
    }
    foreach ($directoryPath in ($directories.Keys | Sort-Object Length -Descending)) {
        if ((Test-Path -LiteralPath $directoryPath -PathType Container) -and
            -not (Get-ChildItem -LiteralPath $directoryPath -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $directoryPath
        }
    }
}

Assert-InstallPath
switch ($Action) {
    'Start' { Start-Desktop }
    'Stop' { Stop-Desktop; Write-Host 'Athena Desktop stopped.' }
    'Status' {
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            Write-Host 'Athena Desktop is not installed here.'
        } else {
            $status = Invoke-Control '--status'
            if ($status -eq 0) { Write-Host 'Athena Desktop is running in this user session.' }
            elseif ($status -eq 1) { Write-Host 'Athena Desktop is stopped in this user session.' }
            else { throw "Could not read Athena Desktop status (exit code $status)." }
        }
    }
    'Install' {
        Assert-ShortcutOwnership
        $previousManifest = Read-Manifest
        if ((Test-Path -LiteralPath $installPath) -and -not $previousManifest -and
            (Get-ChildItem -LiteralPath $installPath -Force | Select-Object -First 1)) {
            throw 'The installation directory is not empty and has no Athena Desktop manifest. Choose an empty directory.'
        }
        $previousFiles = @()
        if ($previousManifest) { $previousFiles = @($previousManifest.Files) }
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
        $runtimes = & $dotnet --list-runtimes
        if ($LASTEXITCODE -ne 0 -or -not ($runtimes -match '^Microsoft\.WindowsDesktop\.App 10\.0\.')) {
            throw 'Install the .NET 10 Windows Desktop Runtime before installing Athena Desktop.'
        }
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $stagingName = 'athena-desktop-publish-' + [Guid]::NewGuid().ToString('N')
        $stagingPath = [IO.Path]::GetFullPath((Join-Path $temporaryRoot $stagingName))
        $null = New-Item -ItemType Directory -Path $stagingPath
        try {
            & $dotnet publish (Join-Path $repositoryDirectory 'src\Athena.Desktop.csproj') -c Release --self-contained false -p:UseAppHost=true --output $stagingPath
            if ($LASTEXITCODE -ne 0) { throw 'Publishing failed; the previous installation has not been stopped.' }
            if (-not (Test-Path -LiteralPath (Join-Path $stagingPath 'Athena.Desktop.exe') -PathType Leaf)) {
                throw 'Publishing did not produce Athena.Desktop.exe.'
            }
            if ([string]::IsNullOrWhiteSpace($McpSettingsPath)) {
                $McpSettingsPath = [Environment]::GetEnvironmentVariable('ATHENA_MCP_SETTINGS', 'User')
                if ([string]::IsNullOrWhiteSpace($McpSettingsPath)) {
                    $McpSettingsPath = Join-Path $env:LOCALAPPDATA 'AthenaDesktop\mcp.json'
                }
            }
            $McpSettingsPath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($McpSettingsPath))
            # Validate the staged program before stopping the existing installation.
            $previousSettings = [Environment]::GetEnvironmentVariable('ATHENA_MCP_SETTINGS', 'Process')
            try {
                [Environment]::SetEnvironmentVariable('ATHENA_MCP_SETTINGS', $McpSettingsPath, 'Process')
                $checkError = Join-Path $stagingPath 'config-check.error.txt'
                $checkOutput = Join-Path $stagingPath 'config-check.output.txt'
                $check = Start-Process -FilePath (Join-Path $stagingPath 'Athena.Desktop.exe') -ArgumentList '--check-config' -WorkingDirectory $stagingPath -WindowStyle Hidden -PassThru -Wait -RedirectStandardError $checkError -RedirectStandardOutput $checkOutput
                try {
                    if ($check.ExitCode -ne 0) { throw "Configuration check failed; the previous installation has not been stopped. $(Get-Content -LiteralPath $checkError -Raw)" }
                } finally { $check.Dispose() }
                Remove-Item -LiteralPath $checkError, $checkOutput
            } finally {
                [Environment]::SetEnvironmentVariable('ATHENA_MCP_SETTINGS', $previousSettings, 'Process')
            }
            $publishedFiles = @(Get-ChildItem -LiteralPath $stagingPath -Recurse -File | ForEach-Object {
                $_.FullName.Substring($stagingPath.Length + 1)
            })
            foreach ($relativePath in $publishedFiles) {
                $destinationPath = Get-OwnedFilePath $relativePath
                if (Test-Path -LiteralPath $destinationPath -PathType Container) {
                    throw "Publishing would overwrite a directory: $destinationPath"
                }
                if ((Test-Path -LiteralPath $destinationPath) -and $relativePath -notin $previousFiles) {
                    throw "Publishing would overwrite an unowned file: $destinationPath"
                }
            }
            Stop-Desktop
            $null = New-Item -ItemType Directory -Path $installPath -Force
            # Retain ownership if an interrupted update needs to be retried or uninstalled.
            Write-Manifest @($previousFiles + $publishedFiles | Select-Object -Unique)
            foreach ($relativePath in $publishedFiles) {
                $destinationPath = Get-OwnedFilePath $relativePath
                $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force
                Copy-Item -LiteralPath (Join-Path $stagingPath $relativePath) -Destination $destinationPath -Force
            }
            Remove-OwnedFiles @($previousFiles | Where-Object { $_ -notin $publishedFiles })
            Write-Manifest $publishedFiles
            [Environment]::SetEnvironmentVariable('ATHENA_MCP_SETTINGS', $McpSettingsPath, 'User')
            [Environment]::SetEnvironmentVariable('ATHENA_MCP_SETTINGS', $McpSettingsPath, 'Process')
            $null = New-Item -ItemType Directory -Path $startupDirectory -Force
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = $executablePath
            $shortcut.WorkingDirectory = $installPath
            $shortcut.Description = 'Athena Desktop Windows integration'
            $shortcut.Save()
            Write-Host "Installed Athena Desktop at $installPath. It will start when you sign in."
            Start-Desktop
        } finally {
            # Check the exact absolute staging target before recursive cleanup.
            if (-not $stagingPath.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                (Split-Path -Leaf $stagingPath) -ne $stagingName) {
                throw 'Refusing to remove an unexpected publishing directory.'
            }
            Remove-Item -LiteralPath $stagingPath -Recurse -Force
        }
    }
    'Uninstall' {
        Assert-ShortcutOwnership
        $manifest = Read-Manifest
        if (-not $manifest) {
            throw 'No Athena Desktop installation manifest found. No files or Startup shortcut were removed.'
        }
        Stop-Desktop
        if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath }
        Remove-OwnedFiles @($manifest.Files)
        Remove-Item -LiteralPath $manifestPath
        if (-not (Get-ChildItem -LiteralPath $installPath -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $installPath
        }
        Write-Host 'Athena Desktop uninstalled. Logs and user environment settings were preserved.'
    }
}
