namespace Atalaia.Server;

public sealed record ScanRequest(string? Range);
public sealed record DeployTargetRequest(string? Ip, string? Name);
public sealed record DeployJobRequest(List<DeployTargetRequest>? Targets, string? TokenId);
public sealed record ProgressRequest(string? Ip, string? Step, string? Message);

/// <summary>Instalação do agente: varredura da rede, agente embutido, scripts e trabalhos em massa (com acompanhamento). Tudo é só do administrador.</summary>
public static class DeployEndpoints
{
    private const int MaxTargets = 500;

    public static void MapDeploy(this WebApplication app, NetworkScanner scanner, DeployStore deploy, PackageStore packages, TokenStore tokens, MachineStore machines, AuthStore auth)
    {
        static IResult Error(int status, string code, string message) => Results.Json(new { error = code, message }, statusCode: status);

        // ---------------- Varredura ----------------
        object ScanDto(ScanState s)
        {
            var known = machines.IpIndex();
            return new
            {
                s.Id, s.Range, s.State, s.Total, s.Done, s.StartedAt, s.FinishedAt, s.RequestedBy,
                Hosts = s.Hosts.Select(h => new
                {
                    h.Ip, h.Name, h.OpenPorts,
                    Agent = known.TryGetValue(h.Ip, out var k) ? new { k.AgentId, k.Hostname, k.AgentVersion, k.LastSeen } : null,
                }),
            };
        }

        app.MapPost("/api/discovery/scans", (HttpContext http, ScanRequest body) =>
        {
            var user = http.Current()!;
            var (state, error, busy) = scanner.Start(body.Range, user.Username);
            if (busy) return Results.Json(new { error = "busy", message = error, scan = ScanDto(state!) }, statusCode: 409);
            if (state is null) return Error(400, "invalid_range", error ?? "Faixa inválida.");
            auth.Log(user.Username, "deploy_scan", state.Range, $"varredura de {state.Total} endereço(s)", http.ClientIp());
            return Results.Json(ScanDto(state), statusCode: 202);
        }).RequireRole(Roles.Admin);

        app.MapGet("/api/discovery/scans/latest", () => scanner.Latest() is { } s ? Results.Json(ScanDto(s)) : Results.Json(new { scan = (object?)null }))
            .RequireRole(Roles.Admin);

        app.MapPost("/api/discovery/scans/{id}/cancel", (string id) => scanner.Cancel(id) ? Results.NoContent() : Results.NotFound()).RequireRole(Roles.Admin);

        // ---------------- Agente embutido ----------------
        object PackageDto(PackageInfo? p) => p is null ? new { present = false } : new { present = true, p.Size, p.Sha256, p.Version, p.Source };

        app.MapGet("/api/install/package", () => PackageDto(packages.Info())).RequireRole(Roles.Admin);

        // O computador de destino baixa o agente aqui, provando que conhece um token de agentes ativo
        app.MapGet("/api/deploy/package", (HttpContext http) =>
        {
            if (!tokens.Validate(http.Request.Headers["X-Enroll-Token"], recordUse: false)) return Results.Unauthorized();
            return packages.OpenRead() is { } stream ? Results.File(stream, "application/octet-stream", "Atalaia.Agent.exe") : Results.NotFound();
        });

        // Script avulso para instalar em UM computador (ou por GPO de inicialização): o administrador baixa e executa
        app.MapGet("/api/install/agent-script", (HttpContext http, string? tokenId) =>
        {
            var user = http.Current()!;
            if (packages.Info() is not { } pkg) return Error(409, "no_package", "O agente não está disponível neste servidor (veja o README: imagem Docker ou publish do agente).");
            var token = tokenId is { Length: > 0 } ? tokens.List().FirstOrDefault(t => t.Id == tokenId) : tokens.List().FirstOrDefault(t => t.RevokedAt is null);
            if (token is null || token.RevokedAt is not null) return Error(400, "no_token", "Escolha um token de agentes ativo.");
            if (!AgentInstallScript.IsSafeToken(token.Token)) return Error(400, "unsafe_token", "Este token tem caracteres que não podem ir num script. Crie outro token.");
            var serverUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            if (!AgentInstallScript.IsSafeServer(serverUrl)) return Error(400, "bad_server", "O endereço do painel tem um formato não suportado.");

            auth.Log(user.Username, "install_script_downloaded", token.Label, "script de instalação de um computador", http.ClientIp());
            http.Response.Headers.ContentDisposition = "attachment; filename=\"instalar-agente.ps1\"";
            return Results.Text(AgentInstallScript.Standalone(serverUrl, token.Token, pkg.Sha256, user.Username, pkg.Version), "text/plain; charset=utf-8");
        }).RequireRole(Roles.Admin);

        // ---------------- Trabalhos ----------------
        object JobDto(DeployJob j) => new
        {
            j.Id, j.CreatedAt, j.CreatedBy, j.TokenLabel, j.ServerUrl,
            Counts = j.Targets.GroupBy(t => t.Status).ToDictionary(g => g.Key, g => g.Count()),
            Targets = j.Targets.Select(t => new { t.Ip, t.Name, t.Status, t.Message, t.UpdatedAt, t.FoundHostname }),
        };

        app.MapGet("/api/deploy/jobs", () => deploy.Recent(10).Select(JobDto)).RequireRole(Roles.Admin);

        app.MapGet("/api/deploy/jobs/{id}", (string id) => deploy.Get(id) is { } j ? Results.Json(JobDto(j)) : Results.NotFound()).RequireRole(Roles.Admin);

        app.MapPost("/api/deploy/jobs", (HttpContext http, DeployJobRequest body) =>
        {
            var user = http.Current()!;
            if (packages.Info() is not { } pkg) return Error(400, "no_package", "O agente não está disponível neste servidor (veja o README: imagem Docker ou publish do agente).");

            var token = (body.TokenId is { Length: > 0 } tid ? tokens.List().FirstOrDefault(t => t.Id == tid) : tokens.List().FirstOrDefault(t => t.RevokedAt is null));
            if (token is null || token.RevokedAt is not null) return Error(400, "no_token", "Escolha um token de agentes ativo.");
            if (!DeployScript.IsSafeToken(token.Token)) return Error(400, "unsafe_token", "Este token tem caracteres que não podem ir num script. Crie outro token na tela Instalação.");

            var serverUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            if (!DeployScript.IsSafeServer(serverUrl)) return Error(400, "bad_server", "O endereço do painel tem um formato não suportado.");

            var targets = new Dictionary<string, string?>();
            foreach (var t in body.Targets ?? [])
            {
                if (!IpRange.TryParseIp(t.Ip, out var ip) || !IpRange.IsAllowed(ip)) return Error(400, "bad_target", $"Endereço inválido ou fora de rede privada: {t.Ip}");
                targets.TryAdd(IpRange.ToText(ip), DeployScript.CleanName(t.Name));
            }
            if (targets.Count == 0) return Error(400, "no_targets", "Marque ao menos um computador.");
            if (targets.Count > MaxTargets) return Error(400, "too_many", $"Máximo de {MaxTargets} computadores por vez.");

            var (job, _) = deploy.Create(user.Username, token.Id, token.Label, pkg.Sha256, serverUrl, targets.Select(kv => (kv.Key, kv.Value)));
            auth.Log(user.Username, "deploy_job_created", $"{targets.Count} computador(es)", $"token “{token.Label}”", http.ClientIp());
            return Results.Json(JobDto(job), statusCode: 201);
        }).RequireRole(Roles.Admin);

        app.MapGet("/api/deploy/jobs/{id}/script", (HttpContext http, string id) =>
        {
            var user = http.Current()!;
            if (deploy.Get(id) is not { } job || deploy.SecretOf(id) is not { } secret) return Results.NotFound();
            if (DateTimeOffset.UtcNow - job.CreatedAt > DeployStore.JobLifetime) return Error(410, "expired", "Este trabalho expirou (24 h). Gere um novo.");
            var token = tokens.List().FirstOrDefault(t => t.Id == job.TokenId);
            if (token is null || token.RevokedAt is not null) return Error(409, "token_revoked", "O token deste trabalho foi revogado. Gere um novo trabalho com um token ativo.");
            if (packages.Info() is not { } pkg || pkg.Sha256 != job.PackageSha) return Error(409, "package_changed", "O agente do painel foi atualizado depois deste trabalho. Gere um novo.");

            auth.Log(user.Username, "deploy_script_downloaded", $"{job.Targets.Count} computador(es)", null, http.ClientIp());
            var text = DeployScript.Build(job, secret, token.Token, user.Username);
            http.Response.Headers.ContentDisposition = "attachment; filename=\"instalar-atalaia.ps1\"";
            return Results.Text(text, "text/plain; charset=utf-8");
        }).RequireRole(Roles.Admin);

        // O script avisa o que fez com cada computador. Autenticação: segredo do trabalho (não o token dos agentes, nem a sessão).
        app.MapPost("/api/deploy/jobs/{id}/progress", (HttpContext http, string id, ProgressRequest body) =>
        {
            if (body.Step is not ("sent" or "failed") || !IpRange.TryParseIp(body.Ip, out _)) return Error(400, "invalid", "Dados inválidos.");
            return deploy.Report(id, http.Request.Headers["X-Job-Secret"], body.Ip!.Trim(), body.Step, body.Message) switch
            {
                DeployStore.ProgressResult.Ok => Results.NoContent(),
                DeployStore.ProgressResult.Unauthorized => Results.Unauthorized(),
                DeployStore.ProgressResult.Expired => Error(410, "expired", "Trabalho expirado."),
                _ => Results.NotFound(),
            };
        });
    }
}
