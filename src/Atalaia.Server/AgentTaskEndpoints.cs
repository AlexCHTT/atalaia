using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atalaia.Shared;
using Microsoft.AspNetCore.Http.Features;

namespace Atalaia.Server;

public sealed record SpeedTestRequest(string? Target);

/// <summary>Tarefas remotas (teste de velocidade): endpoints que o agente usa e endpoints do painel.</summary>
public static class AgentTaskEndpoints
{
    private const long MaxDownloadBytes = 64L * 1024 * 1024;   // por requisição
    private const long MaxUploadBytes = 8L * 1024 * 1024;      // por requisição (o agente envia pedaços de 512 KB)
    private static readonly SemaphoreSlim Streams = new(16);    // medições simultâneas no servidor, para ninguém usá-lo como torneira de banda
    private static readonly byte[] Pattern = RandomNumberGenerator.GetBytes(64 * 1024);

    public static void MapAgentTasks(this WebApplication app, TokenStore tokens, TaskStore tasks, MachineStore machines, AuthStore auth, SettingsStore settings)
    {

        // O token é conferido sem gravar uso: estas rotas são chamadas o tempo todo (a cada 30 s por máquina)
        bool AgentAuthorized(HttpContext http) => tokens.Validate(http.Request.Headers["X-Enroll-Token"], recordUse: false);

        // ---------------- Agente ----------------
        app.MapPost("/api/agent/poll", (HttpContext http, PollRequest body) =>
        {
            if (!AgentAuthorized(http)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.AgentId) || body.AgentId.Length > 100) return Results.BadRequest("AgentId inválido");
            var due = tasks.Poll(body.AgentId).Select(t => new AgentTaskDto(t.Id, t.Type, t.Target)).ToList();
            // Ajustes que o administrador definiu na tela Configurações (só os definidos lá; o resto o agente mantém como está)
            var cfg = new AgentConfig(settings.Explicit("agents.checkin_minutes"), settings.Explicit("agents.software_every_hours"), settings.Explicit("speedtest.max_mb"));
            var hasConfig = cfg.CheckinMinutes is not null || cfg.SoftwareEveryHours is not null || cfg.SpeedtestMaxMb is not null;
            return Results.Json(new PollResponse(due, hasConfig ? cfg : null), AgentJson.Options);
        });

        app.MapPost("/api/agent/tasks/{id}/result", (HttpContext http, string id, TaskResultRequest body) =>
        {
            if (!AgentAuthorized(http)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.AgentId) || body.AgentId.Length > 100) return Results.BadRequest("AgentId inválido");
            string? json = null;
            if (body.Ok)
            {
                var r = body.SpeedTest;
                if (r is null || !SpeedTestTargets.IsValid(r.Target) || !Sane(r)) return Results.BadRequest("Resultado inválido");
                json = JsonSerializer.Serialize(r, AgentJson.Options);
            }
            return tasks.Complete(id, body.AgentId, body.Ok, body.Error, json) ? Results.NoContent() : Results.NotFound();
        });

        // Medição contra o servidor: ping, download e upload. O agente cronometra do lado dele.
        app.MapGet("/api/agent/speedtest/ping", (HttpContext http) => AgentAuthorized(http) ? Results.Text("pong") : Results.Unauthorized());

        app.MapGet("/api/agent/speedtest/down", async (HttpContext http, long? bytes) =>
        {
            if (!AgentAuthorized(http)) { http.Response.StatusCode = 401; return; }
            var total = Math.Clamp(bytes ?? 10_000_000, 0, MaxDownloadBytes);
            if (!Streams.Wait(0)) { http.Response.StatusCode = 429; return; }
            try
            {
                http.Response.ContentType = "application/octet-stream";
                http.Response.ContentLength = total;
                for (long sent = 0; sent < total;)
                {
                    var n = (int)Math.Min(Pattern.Length, total - sent);
                    await http.Response.Body.WriteAsync(Pattern.AsMemory(0, n), http.RequestAborted);
                    sent += n;
                }
            }
            catch (OperationCanceledException) { /* o agente encerrou ao fim do tempo do teste: normal */ }
            finally { Streams.Release(); }
        });

        app.MapPost("/api/agent/speedtest/up", async (HttpContext http) =>
        {
            if (!AgentAuthorized(http)) return Results.Unauthorized();
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxUploadBytes;
            if (!Streams.Wait(0)) return Results.StatusCode(429);
            try
            {
                var buffer = new byte[64 * 1024];
                long read = 0; int n;
                while ((n = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0) read += n;
                return Results.Json(new { bytes = read });
            }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            finally { Streams.Release(); }
        });

        // ---------------- Painel ----------------
        object Dto(AgentTaskRow t) => new
        {
            t.Id, t.Type, t.Target, t.Status, t.RequestedBy, t.RequestedAt, t.StartedAt, t.CompletedAt, t.Error,
            Result = t.ResultJson is null ? null : JsonNode.Parse(t.ResultJson),
        };

        app.MapGet("/api/features", () => new { speedtestInternet = settings.Bool("speedtest.internet_enabled") }).RequireRole(Roles.Viewer);

        app.MapGet("/api/machines/{id}/speedtests", (string id, int? limit) =>
            tasks.List(id, limit ?? 10).Where(t => t.Type == TaskTypes.SpeedTest).Select(Dto)).RequireRole(Roles.Viewer);

        app.MapPost("/api/machines/{id}/speedtest", (HttpContext http, string id, SpeedTestRequest body) =>
        {
            var target = body.Target ?? SpeedTestTargets.Server;
            if (!SpeedTestTargets.IsValid(target)) return Results.Json(new { error = "invalid_target", message = "Destino inválido." }, statusCode: 400);
            if (target == SpeedTestTargets.Internet && !settings.Bool("speedtest.internet_enabled"))
                return Results.Json(new { error = "disabled", message = "O teste até a internet está desativado neste servidor (Configurações > Rede e testes)." }, statusCode: 400);
            if (machines.Get(id) is not { } machine) return Results.NotFound();

            var user = http.Current()!;
            var (task, created) = tasks.Create(id, TaskTypes.SpeedTest, target, user.Username);
            if (!created) return Results.Json(new { error = "busy", message = "Já existe um teste em andamento nesta máquina.", task = Dto(task) }, statusCode: 409);

            var host = machine["identity"]?["hostname"]?.GetValue<string>() ?? id;
            auth.Log(user.Username, "speedtest_requested", host, target == SpeedTestTargets.Internet ? "teste de velocidade até a internet" : "teste de velocidade até o servidor", http.ClientIp());
            return Results.Json(Dto(task), statusCode: 202);
        }).RequireRole(Roles.Operator);

        // Cancela o teste em andamento (ex.: a máquina está desligada e você não quer esperar o prazo do pedido)
        app.MapPost("/api/machines/{id}/speedtest/cancel", (HttpContext http, string id) =>
        {
            if (tasks.Cancel(id) is not { } cancelled) return Results.Json(new { error = "none", message = "Não há teste em andamento nesta máquina." }, statusCode: 404);
            var user = http.Current()!;
            var host = machines.Get(id)?["identity"]?["hostname"]?.GetValue<string>() ?? id;
            auth.Log(user.Username, "speedtest_cancelled", host, $"teste cancelado (pedido por {cancelled.RequestedBy ?? "?"})", http.ClientIp());
            return Results.Json(Dto(cancelled));
        }).RequireRole(Roles.Operator);
    }

    // Recusa números absurdos (um agente com defeito não deve poluir o histórico)
    private static bool Sane(SpeedTestResult r) =>
        Ok(r.DownMbps, 1_000_000) && Ok(r.UpMbps, 1_000_000) && Ok(r.LatencyMs, 600_000) && Ok(r.JitterMs, 600_000) && Ok(r.Seconds, 3_600) && r.BytesDown >= 0 && r.BytesUp >= 0;
    private static bool Ok(double v, double max) => double.IsFinite(v) && v >= 0 && v <= max;
}
