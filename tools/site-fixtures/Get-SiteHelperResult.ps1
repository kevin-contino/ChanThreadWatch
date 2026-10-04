# Runs a site helper from the built ChanThreadWatch.Core.dll on a saved page and prints what it finds
# as JSON: whether it is a thread page, the images, the thumbnails and the cross links.
# Used by sanitize_site_fixture.py to check that a fixture gives the same results as its capture.
# Requires Windows PowerShell 5.1 (the app targets .NET Framework 4.8).
param(
    [Parameter(Mandatory = $true)][string]$CoreDll,
    [Parameter(Mandatory = $true)][string]$Helper,
    [Parameter(Mandatory = $true)][string]$Url,
    [Parameter(Mandatory = $true)][string]$Html
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$assembly = [Reflection.Assembly]::LoadFrom($CoreDll)
$helperType = $assembly.GetType("JDP.$Helper", $true)
$parserType = $assembly.GetType('JDP.HTMLParser', $true)
$thumbnailType = $assembly.GetType('JDP.ThumbnailInfo', $true)

$siteHelper = [Activator]::CreateInstance($helperType)
$siteHelper.SetURL($Url)
$siteHelper.SetHTMLParser([Activator]::CreateInstance($parserType, @([IO.File]::ReadAllText($Html, [Text.Encoding]::UTF8))))

$thumbnails = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($thumbnailType))
$images = $siteHelper.GetImages($null, $thumbnails, $false)

$result = [ordered]@{
    isThread = $siteHelper.IsThreadPage()
    images = @($images | ForEach-Object {
        [ordered]@{
            url = $_.URL
            originalFileName = $_.OriginalFileName
            poster = $_.Poster
            hash = $(if ($_.Hash) { [Convert]::ToBase64String($_.Hash) } else { $null })
            hashType = $_.HashType.ToString()
        }
    })
    thumbnails = @($thumbnails | ForEach-Object { $_.URL })
    crossLinks = @($siteHelper.GetCrossLinks($null, $true))
}
$result | ConvertTo-Json -Depth 4 -Compress
