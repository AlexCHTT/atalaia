<#
  Instala/remove o agente como serviço do Windows nesta máquina, para teste local.
  Precisa de PowerShell COMO ADMINISTRADOR.

    .\scripts\dev-service.ps1 install     # copia o exe, grava a config, cria e inicia o serviço
    .\scripts\dev-service.ps1 uninstall   # para e remove o serviço (e a pasta de instalação)
    .\scripts\dev-service.ps1 status
#>
param(
    [Parameter(Mandatory)][ValidateSet('install', 'uninstall', 'status')][string]$Action,
    [string]$ServerUrl = 'http://localhost:5099',
    [string]$Token = 'dev-enroll-token'
)

$ErrorActionPreference = 'Stop'
$serviceName = 'AtalaiaAgent'
$installDir = Join-Path $env:ProgramFiles 'Atalaia'
$configDir = Join-Path $env:ProgramData 'Atalaia'
$source = Join-Path $PSScriptRoot '..\publish\agent\Atalaia.Agent.exe'

if ($Action -eq 'status') {
    Get-Service $serviceName -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType -AutoSize
    if (Test-Path $configDir) { Get-ChildItem $configDir | Format-Table Name, Length, LastWriteTime -AutoSize }
    return
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Abra o PowerShell como Administrador.' }

function Remove-AgentService {
    # Remove o servico atual E o antigo, de quando o produto se chamava AgentTools
    foreach ($name in @($serviceName, 'AgentTools')) {
        if (Get-Service $name -ErrorAction SilentlyContinue) {
            Stop-Service $name -Force -ErrorAction SilentlyContinue
            sc.exe delete $name | Out-Null
            # o SCM so libera o nome depois que o servico para de vez
            for ($i = 0; $i -lt 20 -and (Get-Service $name -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
        }
    }
    $oldDir = Join-Path $env:ProgramFiles 'AgentTools'
    if (Test-Path $oldDir) { Remove-Item $oldDir -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($Action -eq 'uninstall') {
    Remove-AgentService
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
    Write-Host "Serviço removido. A config em $configDir foi mantida (apague à mão se quiser)."
    return
}

# ---- install ----
if (-not (Test-Path $source)) { throw "Não achei $source. Rode 'dotnet publish' do agente antes." }

Remove-AgentService   # reinstalar por cima funciona
New-Item -ItemType Directory -Force $installDir, $configDir | Out-Null
Copy-Item $source (Join-Path $installDir 'Atalaia.Agent.exe') -Force

# Config da instalação (é exatamente o que o MSI vai gravar depois). Sem BOM, para o parser JSON não reclamar.
$json = @{ Agent = @{ ServerUrl = $ServerUrl; EnrollToken = $Token; IntervalMinutes = 1 } } | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $configDir 'agent.json'), $json, (New-Object Text.UTF8Encoding $false))

$exe = Join-Path $installDir 'Atalaia.Agent.exe'
New-Service -Name $serviceName -BinaryPathName "`"$exe`"" -DisplayName 'Atalaia Agent' `
    -Description 'Envia inventário desta máquina ao painel Atalaia.' -StartupType Automatic | Out-Null

# Se o serviço cair, o Windows reinicia (5s, 5s, 30s)
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null

Start-Service $serviceName
Start-Sleep 2
Get-Service $serviceName | Format-Table Name, Status, StartType -AutoSize
Write-Host "Instalado em $installDir. Config: $configDir\agent.json"
Write-Host "Logs: Visualizador de Eventos > Aplicativo, origem 'Atalaia'."
