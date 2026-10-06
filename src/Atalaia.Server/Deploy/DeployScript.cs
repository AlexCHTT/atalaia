using System.Text;

namespace Atalaia.Server;

/// <summary>
/// Gera o script PowerShell que o administrador roda no PC dele. O script usa a conta do próprio administrador (ou a que ele digitar)
/// para mandar cada computador baixar o instalador deste servidor e instalá-lo. A senha nunca vai para o servidor.
/// Todos os valores embutidos passam por validação rígida de caracteres: nada digitado por alguém vira código no script.
/// </summary>
public static class DeployScript
{
    public static bool IsSafeToken(string token) => AgentInstallScript.IsSafeToken(token);
    public static bool IsSafeServer(string url) => AgentInstallScript.IsSafeServer(url);
    public static string? CleanName(string? name) => AgentInstallScript.CleanName(name);

    public static string Build(DeployJob job, string secret, string enrollToken, string requestedBy)
    {
        var remote = AgentInstallScript.Remote(job.ServerUrl, enrollToken, job.PackageSha);   // valida servidor e token
        var who = new string(requestedBy.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '@').Take(60).ToArray());

        var targets = string.Join(",\r\n", job.Targets.Select(t =>
            $"  @{{ Ip = '{t.Ip}'; Name = '{(CleanName(t.Name) ?? "")}' }}"));

        var sb = new StringBuilder(Template);
        sb.Replace("@@REMOTE@@", remote).Replace("@@WHO@@", who).Replace("@@DATE@@", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm")).Replace("@@SERVER@@", job.ServerUrl)
          .Replace("@@JOBID@@", job.Id).Replace("@@SECRET@@", secret).Replace("@@TARGETS@@", targets);
        return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");   // CRLF: abre bem no Bloco de Notas e no PowerShell 5.1
    }

    // ASCII puro de propósito: o PowerShell 5.1 lê arquivo sem BOM como ANSI e estragaria acentos.
    private const string Template = """
<#
  Atalaia - instalacao do agente em varios computadores
  Gerado por @@WHO@@ em @@DATE@@ para o servidor @@SERVER@@ (trabalho @@JOBID@@).

  COMO USAR
    1. Abra o PowerShell como Administrador no SEU computador (ele precisa alcancar os computadores de destino).
    2. Execute:  powershell -ExecutionPolicy Bypass -File .\instalar-atalaia.ps1
       - Usa a sua conta atual. Para usar outra conta de administrador:  -Credential (Get-Credential)
       - Para testar em um computador so:                                 -Only 192.168.0.10
       - Para ver o que seria feito, sem instalar nada:                   -DryRun
    3. Acompanhe o resultado de cada computador no painel (tela Instalacao, aba Varios computadores).

  A senha NUNCA e enviada ao servidor: ela fica so nesta sessao do PowerShell.
  Este arquivo contem o token de registro dos agentes: trate-o como senha e apague depois de usar.
  Requisitos nos computadores de destino: porta 5985 (WinRM) ou 135 (RPC/WMI) liberada para o seu PC,
  e acesso ao servidor do painel (@@SERVER@@).
#>
[CmdletBinding()]
param(
  [System.Management.Automation.PSCredential]$Credential,
  [string[]]$Only,
  [switch]$DryRun
)
$ErrorActionPreference = 'Continue'
$Server = '@@SERVER@@'
$JobId = '@@JOBID@@'
$JobSecret = '@@SECRET@@'
$Targets = @(
@@TARGETS@@
)

# Instalador que cada computador de destino executa (como SYSTEM): baixa o agente do painel, confere o hash e instala.
$RemoteScript = @'
@@REMOTE@@
'@

function Send-Progress($ip, $step, $message) {
  if ($DryRun) { return }
  try {
    $body = @{ ip = $ip; step = $step; message = $message } | ConvertTo-Json -Compress
    Invoke-RestMethod -Method Post -Uri "$Server/api/deploy/jobs/$JobId/progress" -Headers @{ 'X-Job-Secret' = $JobSecret } `
      -ContentType 'application/json' -Body $body -TimeoutSec 15 | Out-Null
  } catch { Write-Warning ("Nao consegui avisar o painel: " + $_.Exception.Message) }
}

function Test-Port($ip, $port, $ms = 1500) {
  $c = New-Object Net.Sockets.TcpClient
  try {
    $a = $c.BeginConnect($ip, $port, $null, $null)
    if ($a.AsyncWaitHandle.WaitOne($ms) -and $c.Connected) { $c.EndConnect($a); return $true }
    return $false
  } catch { return $false } finally { $c.Close() }
}

function New-RemoteSession($computer, $useWsman) {
  $protocol = 'Dcom'
  if ($useWsman) { $protocol = 'Wsman' }
  $params = @{ ComputerName = $computer; SessionOption = (New-CimSessionOption -Protocol $protocol); ErrorAction = 'Stop' }
  if ($Credential) { $params.Credential = $Credential }
  New-CimSession @params
}

$list = $Targets
if ($Only) { $list = @($Targets | Where-Object { ($Only -contains $_.Ip) -or ($Only -contains $_.Name) }) }
if ($list.Count -eq 0) { Write-Warning 'Nenhum computador na lista.'; return }

$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($RemoteScript))
$commandLine = 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encoded

Write-Host ("Atalaia: instalando em {0} computador(es)..." -f $list.Count) -ForegroundColor Cyan
if ($DryRun) { Write-Host '(modo de teste: nada sera instalado e o painel nao sera avisado)' -ForegroundColor Yellow }
$ok = 0; $failed = 0

foreach ($t in $list) {
  $ip = $t.Ip
  $computer = $ip
  if ($t.Name) { $computer = $t.Name }
  Write-Host ("- {0} ({1}) ... " -f $ip, $computer) -NoNewline

  $wsman = Test-Port $ip 5985
  $dcom = Test-Port $ip 135
  if (-not $wsman -and -not $dcom) {
    Write-Host 'sem resposta nas portas 5985/135' -ForegroundColor Red
    Send-Progress $ip 'failed' 'Nenhuma porta de administracao remota respondeu (5985 WinRM, 135 RPC). Computador desligado ou firewall bloqueando.'
    $failed++; continue
  }
  if ($DryRun) { Write-Host ('pronto para instalar via ' + $(if ($wsman) { 'WinRM' } else { 'WMI/DCOM' })) -ForegroundColor Green; continue }

  $session = $null
  try {
    try { $session = New-RemoteSession $computer $wsman }
    catch { if ($computer -ne $ip) { $session = New-RemoteSession $ip $wsman } else { throw } }   # o nome nao resolveu: tenta pelo IP
    $r = Invoke-CimMethod -CimSession $session -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $commandLine } -ErrorAction Stop
    if ($r.ReturnValue -ne 0) { throw ("o Windows recusou iniciar o processo (codigo {0})" -f $r.ReturnValue) }
    Write-Host 'enviado' -ForegroundColor Green
    Send-Progress $ip 'sent' 'Instalacao iniciada no computador. Aguardando o agente se registrar no servidor.'
    $ok++
  } catch {
    $msg = ($_.Exception.Message -replace '\s+', ' ')
    Write-Host ('falhou: ' + $msg) -ForegroundColor Red
    Send-Progress $ip 'failed' $msg
    $failed++
  } finally { if ($session) { Remove-CimSession $session -ErrorAction SilentlyContinue } }
}

Write-Host ''
Write-Host ("Concluido: {0} enviado(s), {1} falha(s). O painel marca 'Instalado' quando cada agente se registrar." -f $ok, $failed) -ForegroundColor Cyan
""";
}
