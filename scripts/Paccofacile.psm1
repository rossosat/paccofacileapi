# Modulo PowerShell per Paccofacile CLI.
# Import:  Import-Module "C:\Users\fred.SERVER-FRED\source\repos\Paccofacileapi\scripts\Paccofacile.psm1"
# Per averlo sempre: aggiungi quella riga al tuo $PROFILE.

$script:PfExe = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\paccofacile.exe'

function Invoke-Pf {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$CliArgs)
    $exe = if (Test-Path $script:PfExe) { $script:PfExe } elseif (Get-Command paccofacile -ErrorAction SilentlyContinue) { 'paccofacile' } else { throw "paccofacile.exe non trovato: esegui dotnet publish (vedi README)" }
    & $exe @CliArgs
}

function Test-PfAccount { Invoke-Pf check }
function Get-PfCarriers { param([switch]$Json) if ($Json) { Invoke-Pf carriers --json } else { Invoke-Pf carriers } }

function Get-PfQuote {
    <# .SYNOPSIS Preventivo verso una destinazione. Es: Get-PfQuote -Cap 20121 -Citta Milano -Prov MI -Collo 30x20x15:1 #>
    param(
        [Parameter(Mandatory)][string]$Cap,
        [Parameter(Mandatory)][string]$Citta,
        [Parameter(Mandatory)][string]$Prov,
        [string]$Paese = 'IT',
        [string]$Collo,
        [int]$Servizio
    )
    $a = @('quote', '--zip', $Cap, '--city', $Citta, '--prov', $Prov, '--country', $Paese)
    if ($Collo) { $a += @('--parcel', $Collo) }
    if ($Servizio) { $a += @('--service', $Servizio) }
    Invoke-Pf @a
}

function New-PfShipment {
    <#
    .SYNOPSIS Crea una spedizione (non pagata: si paga sul sito).
    .EXAMPLE New-PfShipment -Nome "Mario Rossi" -Indirizzo "Via Verdi 12" -Cap 20121 -Citta Milano -Prov MI -Telefono 3331234567 -Collo 30x20x15:1.2 -Ordine 403-1234567-1234567
    #>
    param(
        [Parameter(Mandatory)][string]$Nome,
        [Parameter(Mandatory)][string]$Indirizzo,
        [string]$Civico,
        [Parameter(Mandatory)][string]$Cap,
        [Parameter(Mandatory)][string]$Citta,
        [Parameter(Mandatory)][string]$Prov,
        [string]$Paese = 'IT',
        [string]$Telefono,
        [string]$Email,
        [string]$Note,
        [string]$Collo,
        [string]$Ordine,
        [int]$Servizio,
        [switch]$Paga,
        [switch]$SoloPreventivo,
        [switch]$Verbose_
    )
    $a = @('ship', '--name', $Nome, '--address', $Indirizzo, '--zip', $Cap, '--city', $Citta, '--prov', $Prov, '--country', $Paese)
    if ($Civico)   { $a += @('--number', $Civico) }
    if ($Telefono) { $a += @('--phone', $Telefono) }
    if ($Email)    { $a += @('--email', $Email) }
    if ($Note)     { $a += @('--note', $Note) }
    if ($Collo)    { $a += @('--parcel', $Collo) }
    if ($Ordine)   { $a += @('--order', $Ordine) }
    if ($Servizio) { $a += @('--service', $Servizio) }
    if ($Paga)     { $a += '--buy' }
    if ($SoloPreventivo) { $a += '--dry-run' }
    if ($Verbose_) { $a += '--verbose' }
    Invoke-Pf @a
}

function Import-PfAmazonOrders {
    <# .SYNOPSIS Crea spedizioni dal report "Ordini non spediti" di Seller Central e scrive il file di conferma. #>
    param(
        [Parameter(Mandatory)][string]$File,
        [switch]$SoloPreventivo,
        [switch]$Paga,
        [string]$Solo,
        [int]$Servizio,
        [string]$Collo,
        [string]$Out
    )
    $a = @('amazon', '--file', $File)
    if ($SoloPreventivo) { $a += '--dry-run' }
    if ($Paga) { $a += '--buy' }
    if ($Solo) { $a += @('--only', $Solo) }
    if ($Servizio) { $a += @('--service', $Servizio) }
    if ($Collo) { $a += @('--parcel', $Collo) }
    if ($Out) { $a += @('--out', $Out) }
    Invoke-Pf @a
}

function Import-PfCsvOrders {
    param([Parameter(Mandatory)][string]$File, [switch]$SoloPreventivo, [switch]$Paga, [int]$Servizio)
    $a = @('csv', '--file', $File)
    if ($SoloPreventivo) { $a += '--dry-run' }
    if ($Paga) { $a += '--buy' }
    if ($Servizio) { $a += @('--service', $Servizio) }
    Invoke-Pf @a
}

function Get-PfTracking { param([string]$Ordine, [switch]$Full) $a = @('tracking'); if ($Ordine) { $a += @('--order', $Ordine) }; if ($Full) { $a += '--full' }; Invoke-Pf @a }
function Complete-PfAmazonConfirmation { param([Parameter(Mandatory)][string]$File, [string]$Out) $a = @('confirm', '--file', $File); if ($Out) { $a += @('--out', $Out) }; Invoke-Pf @a }
function Register-PfWebhook { param([Parameter(Mandatory)][string]$Url) Invoke-Pf webhook-register --url $Url }

Export-ModuleMember -Function Invoke-Pf, Test-PfAccount, Get-PfCarriers, Get-PfQuote, New-PfShipment, Import-PfAmazonOrders, Import-PfCsvOrders, Get-PfTracking, Complete-PfAmazonConfirmation, Register-PfWebhook
