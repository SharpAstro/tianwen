Import-Module PowerHTML -Cmdlet ConvertFrom-Html
Add-Type -AssemblyName System.Web

# Constants
Set-Variable S_IAUName 'IAU Name' -Option Constant
Set-Variable S_IAUNameId 'IAUName' -Option Constant
Set-Variable S_ConstId 'ID' -Option Constant
Set-Variable S_ConstName 'Const.' -Option Constant
Set-Variable S_ConstNameId 'Constellation' -Option Constant
Set-Variable S_StarComp '#' -Option Constant
Set-Variable S_StarCompId 'WDSComponentId' -Option Constant
Set-Variable S_Designation 'Designation' -Option Constant
Set-Variable S_RaJ2000 'RA(J2000)' -Option Constant
Set-Variable S_RaJ2000Id 'RA_J2000' -Option Constant
Set-Variable S_DecJ2000 'Dec(J2000)' -Option Constant
Set-Variable S_DecJ2000Id 'Dec_J2000' -Option Constant
Set-Variable S_Vmag 'Vmag' -Option Constant
Set-Variable S_ApprovalDate 'Approval Date' -Option Constant
Set-Variable S_ApprovalDateId 'ApprovalDate' -Option Constant

function ConvertTo-CompatibleName
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, ValueFromPipeline = $true)] [string] $name
    )

    switch ($name) {
        $S_IAUName { $S_IAUNameId }
        $S_ConstName { $S_ConstNameId }
        $S_StarComp { $S_StarCompId }
        $S_RaJ2000 { $S_RaJ2000Id }
        $S_DecJ2000 { $S_DecJ2000Id }
        $S_ApprovalDate { $S_ApprovalDateId }
        default { $name }
    }
}

function Get-NamedStars
{
    [CmdletBinding()]
    param()

    begin {
        $html = ConvertFrom-Html -Uri 'https://www.iau.org/public/themes/naming_stars/' -Raw
        $table = $html.GetElementById('dtHorizontalExample')
        $rows = $table.ChildNodes | Where-Object Name -eq 'tr'
        $headers = $rows[0].ChildNodes | Where-Object Name -eq 'th' | Select-Object -ExpandProperty InnerText
        $dataRows = $rows | Select-Object -Skip 1
    }

    process {
        $dataRows | ForEach-Object {
            $cols = $PSItem.ChildNodes | Where-Object Name -eq 'td'
            if ($cols.Count -eq $headers.Count) {
                $props = [ordered]@{}
                0..($cols.Count - 1) | ForEach-Object {
                    $header = $headers[$PSItem] | ConvertTo-CompatibleName
                    $innerText = [System.Web.HttpUtility]::HtmlDecode($cols[$PSItem].InnerText)
                    if ($innerText -ne '-' -and $innerText -ne '_') {
                        $props[$header] = $innerText
                    }
                }
                $designation = $props[$S_Designation]
                if ($null -ne $designation -and -not $designation.StartsWith('PSR'))
                {
                    $S_Vmag | ForEach-Object {
                        $propId = $PSItem | ConvertTo-CompatibleName
                        $props[$propId] = [double]::Parse($props[$propId])
                    }
                }

                $S_RaJ2000, $S_DecJ2000 | ForEach-Object {
                    $propId = $PSItem | ConvertTo-CompatibleName
                    $props[$propId] = [double]::Parse($props[$propId])
                }
                [PSCustomObject]$props
            }
        }
    }

    end {

    }
}

$stars = Get-NamedStars | Sort-Object $S_IAUNameId

$outFile = "$PSScriptRoot/iau-named-stars.json"
$stars | ConvertTo-Json | Out-File -Encoding UTF8NoBOM $outFile
$null = 7z -mx9 -scsUTF-8 a "$($outFile).gz" $outFile