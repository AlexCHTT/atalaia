using System.Text;
using System.Text.RegularExpressions;

namespace Atalaia.Server;

/// <summary>
/// O trecho de PowerShell que efetivamente instala o agente num computador: baixa o .exe deste servidor, confere o hash, grava a
/// configuração em HKLM\SOFTWARE\Atalaia e cria/inicia o serviço "Atalaia". É a mesma sequência do scripts\dev-service.ps1.
/// Serve para os dois usos: o script avulso (um computador / GPO) e o comando que a implantação em massa manda cada PC executar.
/// Todos os valores embutidos passam por validação rígida: nada digitado por alguém vira código no script.
/// </summary>
public static class AgentInstallScript
{
    private static readonly Regex SafeToken = new(@"^[A-Za-z0-9._~+/=:@-]{8,200}$", RegexOptions.Compiled);
    private static readonly Regex SafeServer = new(@"^https?://[A-Za-z0-9.\-]+(:\d{1,5})?$", RegexOptions.Compiled);
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9._-]{1,63}$", RegexOptions.Compiled);

    public static bool IsSafeToken(string token) => SafeToken.IsMatch(token);
    public static bool IsSafeServer(string url) => SafeServer.IsMatch(url);
    public static string? CleanName(string? name) => name is not null && SafeName.IsMatch(name.Trim()) ? name.Trim() : null;

    // ASCII puro de propósito: o PowerShell 5.1 lê arquivo sem BOM como ANSI e estragaria acentos.
    private const string Body = """
$ErrorActionPreference = 'Stop'
$server = '@@SERVER@@'
$token = '@@TOKEN@@'
$sha256 = '@@SHA@@'
# Migracao: o produto ja se chamou AgentTools. Remove o servico, a pasta e a configuracao antigos antes de instalar o novo.
$old = Get-Service -Name AgentTools -ErrorAction SilentlyContinue
if ($old) {
  Stop-Service -Name AgentTools -Force -ErrorAction SilentlyContinue
  for ($i = 0; $i -lt 30 -and (Get-Service -Name AgentTools -ErrorAction SilentlyContinue).Status -ne 'Stopped'; $i++) { Start-Sleep -Milliseconds 500 }
  sc.exe delete AgentTools | Out-Null
  Remove-Item (Join-Path $env:ProgramFiles 'AgentTools') -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item 'HKLM:\SOFTWARE\AgentTools' -Recurse -Force -ErrorAction SilentlyContinue
}

$svc = Get-Service -Name AtalaiaAgent -ErrorAction SilentlyContinue
if ($svc -and -not $Force) { Write-Output 'Atalaia ja esta instalado neste computador (use -Force para reinstalar).'; return }

$dir = Join-Path $env:ProgramFiles 'Atalaia'
$exe = Join-Path $dir 'Atalaia.Agent.exe'
$tmp = Join-Path $env:TEMP 'Atalaia.Agent.download.exe'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Output 'Baixando o agente do painel...'
Invoke-WebRequest -UseBasicParsing -Uri "$server/api/deploy/package" -Headers @{ 'X-Enroll-Token' = $token } -OutFile $tmp
if ((Get-FileHash $tmp -Algorithm SHA256).Hash -ne $sha256) { Remove-Item $tmp -Force; throw 'o hash do arquivo baixado nao confere com o do painel' }

if ($svc) {
  Stop-Service -Name AtalaiaAgent -Force -ErrorAction SilentlyContinue
  for ($i = 0; $i -lt 30 -and (Get-Service -Name AtalaiaAgent).Status -ne 'Stopped'; $i++) { Start-Sleep -Milliseconds 500 }
}
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item $tmp $exe -Force
Remove-Item $tmp -Force -ErrorAction SilentlyContinue

$key = 'HKLM:\SOFTWARE\Atalaia'
New-Item -Path $key -Force | Out-Null
Set-ItemProperty -Path $key -Name ServerUrl -Value $server
Set-ItemProperty -Path $key -Name EnrollToken -Value $token
Set-ItemProperty -Path $key -Name IntervalMinutes -Value '5'

if (-not $svc) {
  New-Service -Name AtalaiaAgent -BinaryPathName ('"' + $exe + '"') -DisplayName 'Atalaia Agent' `
    -Description 'Envia o inventario desta maquina ao painel de TI.' -StartupType Automatic | Out-Null
}
sc.exe failure AtalaiaAgent reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
Start-Service -Name AtalaiaAgent
Write-Output 'Atalaia instalado e iniciado. Ele aparece no painel em alguns minutos.'
""";

    private static string Fill(string text, string server, string token, string sha) =>
        text.Replace("@@SERVER@@", server).Replace("@@TOKEN@@", token).Replace("@@SHA@@", sha);

    private static void Validate(string server, string token)
    {
        if (!SafeServer.IsMatch(server)) throw new InvalidOperationException("endereço do servidor inválido");
        if (!SafeToken.IsMatch(token)) throw new InvalidOperationException("token com caracteres não suportados");
    }

    /// <summary>Comando para a implantação em massa: roda no PC de destino como SYSTEM, sem janela, e deixa um log para diagnóstico.</summary>
    public static string Remote(string server, string token, string sha)
    {
        Validate(server, token);
        return Fill("""
$Force = $true
Start-Transcript -Path (Join-Path $env:windir 'Temp\atalaia-install.log') -Force | Out-Null
""" + "\n" + Body, server, token, sha);
    }

    /// <summary>Script avulso para um computador (ou GPO de inicialização): o administrador baixa e executa.</summary>
    public static string Standalone(string server, string token, string sha, string requestedBy, string version)
    {
        Validate(server, token);
        var who = new string(requestedBy.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '@').Take(60).ToArray());
        var header = $$"""
<#
  Atalaia - instalacao do agente neste computador
  Gerado por {{who}} em {{DateTimeOffset.Now:yyyy-MM-dd HH:mm}} para o painel {{server}} (agente {{version}}).

  COMO USAR
    Um computador:  abra o PowerShell COMO ADMINISTRADOR e rode
                      powershell -ExecutionPolicy Bypass -File .\instalar-agente.ps1
    Varios computadores pela GPO:  Configuracao do Computador > Politicas > Configuracoes do Windows > Scripts >
                      Inicializacao > aba "Scripts do PowerShell" > adicione este arquivo (em uma pasta que as contas de
                      computador do dominio consigam ler). Se o agente ja estiver instalado, o script nao faz nada.
    Reinstalar/atualizar mesmo com o agente instalado:  -Force

  Este arquivo contem o token de registro dos agentes: trate-o como senha.
#>
param([switch]$Force)

""";
        return (header + Fill(Body, server, token, sha)).Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}
