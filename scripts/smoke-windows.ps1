[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Package
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$archive = [System.IO.Path]::GetFullPath($Package)
if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or [System.IO.Path]::GetExtension($archive) -ne ".zip") {
    throw "A packaged Windows ZIP is required: $archive"
}

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("serial-scout-smoke-package-" + [guid]::NewGuid().ToString("N"))
$process = $null
try {
    Expand-Archive -LiteralPath $archive -DestinationPath $stage
    $executable = Join-Path $stage "SerialScout.App.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "ZIP does not contain SerialScout.App.exe at its root."
    }

    $stdoutPath = Join-Path $stage "smoke.stdout.log"
    $stderrPath = Join-Path $stage "smoke.stderr.log"
    $process = Start-Process -FilePath $executable `
        -ArgumentList "--packaged-smoke" `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru
    if (-not $process.WaitForExit(60000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "Packaged smoke exceeded the 60-second timeout."
    }
    $stdout = Get-Content -LiteralPath $stdoutPath -Raw
    $stderr = Get-Content -LiteralPath $stderrPath -Raw
    if ($stdout) { [Console]::Out.Write($stdout) }
    if ($stderr) { [Console]::Error.Write($stderr) }
    if ($process.ExitCode -ne 0) { throw "Packaged smoke exited with code $($process.ExitCode)." }
    if ($stdout -notmatch '(?m)^PASS packaged smoke: SQLite create/write/close/reopen/read succeeded; serial discovery was not initialized\r?$') {
        throw "Packaged smoke did not emit the SQLite PASS marker."
    }
    if ($stdout -notmatch '(?m)^PASS packaged smoke cleanup: temporary storage removed\r?$') {
        throw "Packaged smoke did not emit the cleanup PASS marker."
    }
    Write-Output "PASS Windows package smoke: $([System.IO.Path]::GetFileName($archive))"
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $process.Dispose()
    }
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
}
