using System.Net.Http.Json;
using Atalaia.Shared;
using Microsoft.Extensions.Options;

namespace Atalaia.Agent;

/// <summary>Quem sou eu para o servidor: o Worker preenche depois do primeiro check-in e o TaskRunner usa para buscar tarefas.</summary>
public sealed class AgentState
{
    public volatile string? AgentId;

    /// <summary>Ajustes que o administrador definiu no painel (tela Configurações). Nulo, ou campo nulo = vale o que está configurado neste computador.</summary>
    public volatile AgentConfig? Config;
}

/// <summary>
/// Pergunta ao servidor, de tempos em tempos, se há alguma tarefa para esta máquina (o servidor nunca se conecta ao PC).
/// Só executa tipos que este código conhece: hoje, apenas o teste de velocidade. Qualquer outro é recusado.
/// </summary>
public sealed class TaskRunner(
    ILogger<TaskRunner> logger,
    IHttpClientFactory httpFactory,
    IOptionsMonitor<AgentOptions> options,
    AgentState state,
    SpeedTester speed) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue;
            try
            {
                if (state.AgentId is { } agentId && !string.IsNullOrWhiteSpace(opt.ServerUrl) && !string.IsNullOrWhiteSpace(opt.EnrollToken))
                    foreach (var task in await PollAsync(opt, agentId, stoppingToken))
                        await RunAsync(opt, agentId, task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Silencioso de propósito: servidor antigo (sem esta rota), rede fora... o check-in já avisa dos problemas reais.
                logger.LogDebug("Busca de tarefas falhou: {Message}", ex.Message);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, opt.PollSeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<List<AgentTaskDto>> PollAsync(AgentOptions opt, string agentId, CancellationToken ct)
    {
        using var http = httpFactory.CreateClient("server");
        using var req = Post(opt, "api/agent/poll", new PollRequest(agentId));
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var reply = await res.Content.ReadFromJsonAsync<PollResponse>(AgentJson.Options, ct);
        state.Config = reply?.Config;   // sempre troca: se o administrador voltou ao padrão, o agente volta ao que tem localmente
        return reply?.Tasks ?? [];
    }

    private async Task RunAsync(AgentOptions opt, string agentId, AgentTaskDto task, CancellationToken ct)
    {
        TaskResultRequest result;
        if (task.Type != TaskTypes.SpeedTest || !SpeedTestTargets.IsValid(task.Target))
        {
            logger.LogWarning("Tarefa recusada (tipo não suportado): {Type}/{Target}", task.Type, task.Target);
            result = new TaskResultRequest(agentId, false, "Tarefa não suportada por este agente (atualize o agente).", null);
        }
        else
        {
            try
            {
                logger.LogInformation("Teste de velocidade ({Target}) iniciado", task.Target);
                var r = await speed.RunAsync(task.Target!, opt, ct);
                logger.LogInformation("Teste de velocidade: ↓ {Down} Mbps, ↑ {Up} Mbps, {Lat} ms", r.DownMbps, r.UpMbps, r.LatencyMs);
                result = new TaskResultRequest(agentId, true, null, r);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning("Teste de velocidade falhou: {Message}", ex.Message);
                result = new TaskResultRequest(agentId, false, ex.Message, null);
            }
        }

        using var http = httpFactory.CreateClient("server");
        using var req = Post(opt, $"api/agent/tasks/{task.Id}/result", result);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage Post<T>(AgentOptions opt, string path, T body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(opt.ServerUrl.TrimEnd('/') + "/"), path)) { Content = JsonContent.Create(body, options: AgentJson.Options) };
        req.Headers.Add("X-Enroll-Token", opt.EnrollToken);
        return req;
    }
}
