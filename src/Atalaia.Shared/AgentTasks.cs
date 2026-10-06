namespace Atalaia.Shared;

/// <summary>
/// Tarefas que o servidor pode pedir ao agente. A lista é FIXA e conhecida pelo agente: ele nunca executa comando, script
/// ou endereço escolhido pelo servidor. Tipo desconhecido = recusado.
/// </summary>
public static class TaskTypes
{
    public const string SpeedTest = "speedtest";
}

public static class SpeedTestTargets
{
    /// <summary>Mede a velocidade entre o PC e o servidor do painel (o que importa numa rede interna).</summary>
    public const string Server = "server";
    /// <summary>Mede a velocidade até a internet (endpoints públicos da Cloudflare, fixos no agente).</summary>
    public const string Internet = "internet";

    public static bool IsValid(string? t) => t is Server or Internet;
}

public sealed record PollRequest(string AgentId);
public sealed record AgentTaskDto(string Id, string Type, string? Target);
/// <summary>Ajustes que o administrador definiu na tela Configurações para os agentes. Campo nulo = o agente mantém o que tem localmente.</summary>
public sealed record AgentConfig(int? CheckinMinutes, int? SoftwareEveryHours, int? SpeedtestMaxMb);
public sealed record PollResponse(List<AgentTaskDto> Tasks, AgentConfig? Config = null);

public sealed record SpeedTestResult(string Target, double DownMbps, double UpMbps, double LatencyMs, double JitterMs, long BytesDown, long BytesUp, double Seconds);

/// <summary>Resultado de uma tarefa: Ok com o resultado, ou !Ok com a mensagem de erro.</summary>
public sealed record TaskResultRequest(string AgentId, bool Ok, string? Error, SpeedTestResult? SpeedTest);
