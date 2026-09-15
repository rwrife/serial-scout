[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $OutputDirectory = "artifacts/release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "release-version.ps1")
if (-not (Test-PreviewVersion -Version $Version)) {
    throw "Version must be strict SemVer MAJOR.MINOR.PATCH-preview.ID without a leading v: $Version"
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src/SerialScout.App/SerialScout.App.csproj"
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$artifactBase = "SerialScout-$Version-win-x64"
$archive = Join-Path $outputRoot "$artifactBase.zip"
$provenance = Join-Path $outputRoot "$artifactBase.provenance.txt"
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("serial-scout-package-" + [guid]::NewGuid().ToString("N"))
$publishDirectory = Join-Path $stage "publish"
$numericParts = $Version.Split('-')[0].Split('.')
$numericVersion = "$($numericParts[0]).$($numericParts[1]).$($numericParts[2]).0"

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

try {
    # Restore the complete RuntimeIdentifiers graph recorded in packages.lock.json.
    # Passing one --runtime would narrow that graph and invalidate locked mode.
    dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed." }

    dotnet publish $project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --output $publishDirectory `
        -p:Version=$Version `
        -p:AssemblyVersion=$numericVersion `
        -p:FileVersion=$numericVersion `
        -p:InformationalVersion="$Version+$($env:GITHUB_SHA ?? 'local')"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    if (-not (Test-Path (Join-Path $publishDirectory "SerialScout.App.exe"))) {
        throw "Published executable was not produced."
    }

    if (Test-Path $archive) { Remove-Item -Force $archive }
    Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archive -CompressionLevel Optimal

    $commit = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { (git -C $repositoryRoot rev-parse HEAD).Trim() }
    $tag = if ($env:GITHUB_REF_TYPE -eq "tag") { $env:GITHUB_REF_NAME } else { "none" }
    $runner = if ($env:ImageOS) { "$($env:ImageOS)/$($env:ImageVersion)" } else { "local/$([System.Environment]::OSVersion.VersionString)" }
    $sdk = (dotnet --version).Trim()
    $provenanceLines = @(
        "artifact=$artifactBase.zip"
        "commit=$commit"
        "tag=$tag"
        "sdk=$sdk"
        "runner=$runner"
        "arch=x64"
        "rid=win-x64"
        "self_contained=true"
        "signature=unsigned"
    )
    [System.IO.File]::WriteAllText(
        $provenance,
        (($provenanceLines -join "`n") + "`n"),
        [System.Text.UTF8Encoding]::new($false))

    Write-Output "PACKAGE $archive"
    Write-Output "PROVENANCE $provenance"
}
finally {
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
}
