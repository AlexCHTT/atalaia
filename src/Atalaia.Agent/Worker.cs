using System.Net.Http.Json;
using Atalaia.Agent.Collectors;
using Atalaia.Shared;
using Microsoft.Extensions.Options;

namespace Atalaia.Agent;

public sealed class AgentOptions
{
    public string ServerUrl { get; set; } = "";
    public string EnrollToken { get; set; } = "";

    /// <summary>Intervalo entre check-ins.</summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>De quanto em quanto tempo a lista de software é reenviada (é grande e muda pouco).</summary>
    public int SoftwareEveryHours { get; set; } = 6;

    /// <summary>De quanto em quanto tempo o agente pergunta ao servidor se há tarefa (ex.: teste de velocidade). Mínimo 10 s.</summary>
    public int PollSeconds { get; set; } = 30;
}

public sealed class Worker(
    ILogger<Worker> logger,
    InventoryCollector collector,
    IHttpClientFactory httpFactory,
    IOptionsMonitor<AgentOptions> options,
    AgentState state) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastSoftware = null;
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue; // relê a cada ciclo: mudar o arquivo de config vale sem reiniciar
            var wait = TimeSpan.FromMinutes(Math.Clamp(state.Config?.CheckinMinutes ?? opt.IntervalMinutes, 1, 60));   // o painel pode ajustar (Configurações)

            try
            {
                if (string.IsNullOrWhiteSpace(opt.ServerUrl) || string.IsNullOrWhiteSpace(opt.EnrollToken))
                    throw new InvalidOperationException("Agent:ServerUrl e Agent:EnrollToken não configurados");

                var includeSoftware = lastSoftware is null || DateTimeOffset.UtcNow - lastSoftware >= TimeSpan.FromHours(Math.Clamp(state.Config?.SoftwareEveryHours ?? opt.SoftwareEveryHours, 1, 168));

                // Coleta usa WMI (bloqueante); fora da thread do host para não travar o shutdown do serviço
                var report = await Task.Run(() => collector.Collect(includeSoftware), stoppingToken);
                await SendAsync(opt, report, stoppingToken);
                state.AgentId = report.AgentId;   // a partir daqui o TaskRunner já sabe quem perguntar

                if (includeSoftware) lastSoftware = DateTimeOffset.UtcNow;
                failures = 0;
                logger.LogInformation("Check-in OK ({Host}, {Errors} falhas de coleta)", report.Identity.Hostname, report.Errors.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Sem fila: ao voltar a rede, a próxima coleta já manda o estado atual.
                failures++;
                wait = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds, 30 * Math.Pow(2, failures - 1))); // 30s, 1min, 2min… até o intervalo normal
                logger.LogWarning("Falha no check-in ({N}ª seguida): {Message}. Nova tentativa em {Wait}", failures, ex.Message, wait);
            }

            // Espera em fatias de até 15 s, reavaliando o intervalo a cada uma: se o administrador mudar o intervalo no painel
            // (Configurações), o agente passa a valer o novo prazo sem terminar antes a espera antiga.
            var waitStarted = DateTimeOffset.UtcNow;
            try
            {
                while (true)
                {
                    var target = failures > 0 ? wait : TimeSpan.FromMinutes(Math.Clamp(state.Config?.CheckinMinutes ?? options.CurrentValue.IntervalMinutes, 1, 60));
                    var left = target - (DateTimeOffset.UtcNow - waitStarted);
                    if (left <= TimeSpan.Zero) break;
                    await Task.Delay(left < TimeSpan.FromSeconds(15) ? left : TimeSpan.FromSeconds(15), stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SendAsync(AgentOptions opt, InventoryReport report, CancellationToken ct)
    {
        using var http = httpFactory.CreateClient("server");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(opt.ServerUrl.TrimEnd('/') + "/"), "api/checkin"))
        {
            Content = JsonContent.Create(report, options: AgentJson.Options),
        };
        request.Headers.Add("X-Enroll-Token", opt.EnrollToken);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
