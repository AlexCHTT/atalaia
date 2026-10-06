# Instalador do servidor Atalaia em Docker (Windows com Docker Desktop).
#
#   powershell -ExecutionPolicy Bypass -File install.ps1                          pergunta o que precisa
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Mode direct -Port 8080   sem perguntas: HTTP direto no IP:porta
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Mode proxy -ProxyIp 10.0.0.5
#
# direct: o painel responde em http://IP:PORTA (rede interna, sem HTTPS).
# proxy : um proxy reverso (Nginx Proxy Manager, Caddy...) fica na frente e cuida do HTTPS.
param(
  [ValidateSet('', 'direct', 'proxy')][string]$Mode = '',
  [int]$Port = 0,
  [string]$ProxyIp = '',
  [switch]$Yes
)
# 'Continue' de proposito: no PowerShell 5.1, com 'Stop' qualquer texto no stderr de um comando nativo (docker) vira excecao
# e atropelaria as mensagens de erro abaixo, que checam $LASTEXITCODE.
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

function Ask([string]$q, [string]$default) {
  if ($Yes -or -not [Environment]::UserInteractive) { return $default }
  $r = Read-Host "$q [$default]"
  if ([string]::IsNullOrWhiteSpace($r)) { return $default } else { return $r.Trim() }
}
function Die([string]$m) { Write-Host "ERRO: $m" -ForegroundColor Red; exit 1 }

# ---- Pre-requisitos ----
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Die 'Docker nao encontrado. Instale o Docker Desktop (https://www.docker.com/products/docker-desktop/) e rode de novo.' }
docker info *> $null
if ($LASTEXITCODE -ne 0) { Die 'O Docker esta instalado mas nao responde. Abra o Docker Desktop, espere ele iniciar e rode de novo.' }
docker compose version *> $null
if ($LASTEXITCODE -ne 0) { Die 'Docker Compose nao encontrado (docker compose). Atualize o Docker Desktop.' }

# ---- Perguntas ----
if ($Mode -eq '') {
  Write-Host ''
  Write-Host 'Como o painel vai ser acessado?'
  Write-Host '  1) Direto, por IP e porta em HTTP (mais simples; rede interna)'
  Write-Host '  2) Atras de um proxy reverso com HTTPS (Nginx Proxy Manager, Caddy...)'
  switch (Ask 'Escolha' '1') { '1' { $Mode = 'direct' } 'direct' { $Mode = 'direct' } '2' { $Mode = 'proxy' } 'proxy' { $Mode = 'proxy' } default { Die 'Opcao invalida.' } }
}
if ($Port -eq 0) { $p = Ask 'Porta a abrir na rede' '8080'; if (-not ($p -match '^\d+$')) { Die "Porta invalida: $p" }; $Port = [int]$p }
if ($Port -lt 1 -or $Port -gt 65535) { Die "Porta fora de 1-65535: $Port" }

if ($Mode -eq 'proxy' -and $ProxyIp -eq '') {
  Write-Host ''
  Write-Host 'Para o painel mostrar o IP real de cada maquina, informe o IP do proxy (ou uma faixa, ex.: 10.0.0.0/24).'
  $ProxyIp = Ask 'IP do proxy (deixe vazio para pular)' ''
}
if ($Mode -eq 'direct') { $ProxyIp = '' }

# Se a porta ja e do nosso proprio container (reinstalar/atualizar), nao ha conflito
$ownPort = (docker compose port atalaia 8080 2>$null) -replace '^.*:', ''
if ((Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) -and "$ownPort" -ne "$Port") {
  Write-Host "AVISO: a porta $Port ja esta em uso neste computador." -ForegroundColor Yellow
  if ((Ask 'Continuar mesmo assim? (s/n)' 'n') -notmatch '^[sSyY]') { Die 'Cancelado. Rode de novo com outra porta (-Port).' }
}

# ---- .env (preserva o que ja existe) ----
if (-not (Test-Path .env)) { Copy-Item .env.example .env }
function Set-EnvValue([string]$key, [string]$value) {
  $lines = @(Get-Content .env -Encoding UTF8 | Where-Object { $_ -notmatch "^$key=" })
  $lines += "$key=$value"
  # UTF-8 sem BOM (o Docker Compose le o .env como esta)
  [IO.File]::WriteAllLines((Join-Path $PWD '.env'), $lines, (New-Object Text.UTF8Encoding($false)))
}
Set-EnvValue 'HTTP_PORT' "$Port"
Set-EnvValue 'BIND_ADDRESS' '0.0.0.0'
Set-EnvValue 'TRUSTED_PROXIES' $ProxyIp

# ---- Subir ----
Write-Host ''
Write-Host 'Construindo e iniciando o servidor (a primeira vez demora alguns minutos)...'
docker compose up -d --build
if ($LASTEXITCODE -ne 0) { Die 'Falha ao subir o container. Veja a mensagem acima.' }

Write-Host 'Aguardando o servidor responder...'
$ok = $false
for ($i = 0; $i -lt 60 -and -not $ok; $i++) {
  try { $ok = (Invoke-WebRequest "http://127.0.0.1:$Port/healthz" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { Start-Sleep 2 }
}
if (-not $ok) { docker compose logs --tail 30 atalaia; Die 'O servidor nao respondeu em 2 minutos. Veja os logs acima.' }

# ---- Resultado ----
# O adaptador com gateway padrao e o da rede de verdade (os virtuais do WSL/Hyper-V/Docker nao tem gateway)
$ip = (Get-NetIPConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.IPv4DefaultGateway -and $_.IPv4Address } |
  Select-Object -First 1).IPv4Address.IPAddress
if (-not $ip) { $ip = 'IP-DO-SERVIDOR' }
$logs = (docker compose logs atalaia 2>&1) -join "`n"
$code = ([regex]::Matches($logs, '[A-Z2-9]{4}-[A-Z2-9]{4}') | Select-Object -Last 1).Value

Write-Host ''
Write-Host '============================================================'
Write-Host ' Atalaia no ar'
if ($Mode -eq 'direct') {
  Write-Host " Abra no navegador:  http://${ip}:${Port}/"
  Write-Host ' (sem HTTPS: use apenas em rede interna confiavel)'
} else {
  Write-Host " O proxy deve encaminhar para:  http://${ip}:${Port}"
  Write-Host ' Depois abra o endereco HTTPS que voce configurou no proxy.'
}
if ($code) { Write-Host " Codigo do assistente de configuracao:  $code" } else { Write-Host ' Painel ja configurado (nao ha codigo de primeira execucao).' }
Write-Host '============================================================'
Write-Host 'Comandos uteis:  docker compose logs -f atalaia   |   docker compose down   |   docker compose up -d --build (atualizar)'
