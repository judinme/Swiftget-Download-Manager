#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Post-build MSI dialog styling fix for SwiftGet WiX Installer.
.DESCRIPTION
    Fixes a visual defect in WiX WixUI_InstallDir ExitDialog where the
    OptionalCheckBox ("Launch SwiftGet Download Manager") control's 220x40
    Win32 button surface renders with COLOR_BTNFACE (grey #F0F0F0) across
    the pure white (#FFFFFF) dialog.bmp canvas.

    Because Windows Installer's Win32 BUTTON control class does not support
    transparency (ignoring msidbControlAttributesTransparent = 0x10000), this
    script applies the targeted fix:
      1. Resizes OptionalCheckBox to 10x10 DLUs, tightly bounding the checkbox glyph.
      2. Inserts a companion transparent Text control (OptionalCheckBoxText) at
         X=148, Y=190 with Attributes=196611 (Transparent).
      3. Adds the matching Show ControlCondition.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $MsiPath)) {
    throw "MSI file not found at: $MsiPath"
}

Write-Host "Applying ExitDialog visual fix to: $MsiPath" -ForegroundColor Cyan

$wi = New-Object -ComObject WindowsInstaller.Installer
$db = $wi.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $wi, @($MsiPath, 1))

try {
    # 1. Update OptionalCheckBox size and keep full accessible text
    $v1 = $db.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $db, @("UPDATE Control SET Width = 10, Height = 10, Text = 'Launch SwiftGet Download Manager' WHERE Dialog_ = 'ExitDialog' AND Control = 'OptionalCheckBox'"))
    $v1.GetType().InvokeMember("Execute", "InvokeMethod", $null, $v1, $null)
    $v1.GetType().InvokeMember("Close", "InvokeMethod", $null, $v1, $null)
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($v1) | Out-Null

    # 2. Check if OptionalCheckBoxText already exists (for idempotency)
    $vCheck = $db.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $db, @("SELECT Control FROM Control WHERE Dialog_ = 'ExitDialog' AND Control = 'OptionalCheckBoxText'"))
    $vCheck.GetType().InvokeMember("Execute", "InvokeMethod", $null, $vCheck, $null)
    $record = $vCheck.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $vCheck, $null)
    $exists = ($record -ne $null)
    if ($record -ne $null) { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($record) | Out-Null }
    $vCheck.GetType().InvokeMember("Close", "InvokeMethod", $null, $vCheck, $null)
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($vCheck) | Out-Null

    if (-not $exists) {
        # 3. Insert transparent Text control
        $v2 = $db.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $db, @("INSERT INTO Control (Dialog_, Control, Type, X, Y, Width, Height, Attributes, Property, Text, Control_Next, Help) VALUES ('ExitDialog', 'OptionalCheckBoxText', 'Text', 148, 190, 207, 14, 196611, null, '[WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT]', null, null)"))
        $v2.GetType().InvokeMember("Execute", "InvokeMethod", $null, $v2, $null)
        $v2.GetType().InvokeMember("Close", "InvokeMethod", $null, $v2, $null)
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($v2) | Out-Null

        # 4. Insert ControlCondition for OptionalCheckBoxText
        $v3 = $db.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $db, @("INSERT INTO ControlCondition (Dialog_, Control_, Action, Condition) VALUES ('ExitDialog', 'OptionalCheckBoxText', 'Show', 'WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT AND NOT Installed')"))
        $v3.GetType().InvokeMember("Execute", "InvokeMethod", $null, $v3, $null)
        $v3.GetType().InvokeMember("Close", "InvokeMethod", $null, $v3, $null)
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($v3) | Out-Null
    }

    # Commit modifications
    $db.GetType().InvokeMember("Commit", "InvokeMethod", $null, $db, $null)
    Write-Host "Successfully applied ExitDialog visual fix." -ForegroundColor Green
}
finally {
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($db) | Out-Null
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($wi) | Out-Null
    [System.GC]::Collect()
}
