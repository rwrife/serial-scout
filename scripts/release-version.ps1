function Test-PreviewVersion {
    param([Parameter(Mandatory = $true)][string] $Version)

    $number = '(?:0|[1-9][0-9]*)'
    $identifier = '(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)'
    return $Version -cmatch "^$number\.$number\.$number-preview\.$identifier(?:\.$identifier)*$"
}
