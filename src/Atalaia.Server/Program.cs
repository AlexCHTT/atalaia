using System.Security.Cryptography;
using System.Text;
using Atalaia.Server;
using Atalaia.Shared;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Token dos agentes: opcional na configuração. Se informado, entra como um token normal (revogável no painel);
// se não, o servidor gera um sozinho na primeira configuração e o painel mostra onde usá-lo.
var configEnrollToken = builder.Configuration["Server:EnrollToken"];

// Usuário/senha do primeiro administrador: OPCIONAIS. Sem eles, o primeiro acesso mostra o assistente de configuração no
// navegador. Com eles (instalações automatizadas), a conta é criada na subida, quando ainda não existe nenhuma
// (ou para recuperar o acesso, com Server:ResetAdminPassword=true). Depois disso, as contas são gerenciadas na tela "Acessos".
var adminUser = builder.Configuration["Server:AdminUser"] ?? "admin";
var adminPassword = builder.Configuration["Server:AdminPassword"];
var resetAdmin = builder.Configuration.GetValue("Server:ResetAdminPassword", false);

// Limites de sessão, retenção, tempo de offline etc. vêm da tela Configurações (SettingsStore); as variáveis de ambiente antigas
// (Server:SessionHours, Server:OfflineAfterMinutes, ...) continuam valendo como valor inicial.

// Atrás de proxy reverso (Nginx Proxy Manager, etc.) o IP real vem em X-Forwarded-For, mas o ASP.NET só confia nesse
// cabeçalho se vier de um proxy conhecido (por padrão, só o localhost). Liste o(s) IP(s) do proxy em Server:TrustedProxies
// (separados por vírgula; aceita CIDR, ex.: 192.168.1.0/24). "*" confia em qualquer origem: só use se a porta do servidor
// NÃO estiver exposta direto na rede, senão qualquer máquina pode forjar o IP que aparece no painel.
// O mesmo cabeçalho diz se a conexão original era HTTPS, o que liga a flag "Secure" do cookie de login.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    var trusted = builder.Configuration["Server:TrustedProxies"];
    if (string.IsNullOrWhiteSpace(trusted)) return;

    if (trusted.Trim() == "*") { o.KnownProxies.Clear(); o.KnownIPNetworks.Clear(); return; }
    foreach (var entry in trusted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (entry.Contains('/')) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(entry));
        else o.KnownProxies.Add(System.Net.IPAddress.Parse(entry));
    }
});

builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<AuthOptions>();
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<UsbIds>();
builder.Services.AddSingleton<MachineStore>();
builder.Services.AddSingleton<AuthStore>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<TaskStore>();
builder.Services.AddSingleton<NetworkScanner>();
builder.Services.AddSingleton<DeployStore>();
builder.Services.AddSingleton<PackageStore>();
builder.Services.AddSingleton<SetupService>();
builder.Services.AddHostedService<OfflineWatcher>();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 4 * 1024 * 1024);

var app = builder.Build();
var settings = app.Services.GetRequiredService<SettingsStore>();
var authOptions = app.Services.GetRequiredService<AuthOptions>();

// Estado derivado das configurações que vive em classes estáticas (regra de senha, limites do índice de saúde):
// aplicado na subida e a cada alteração feita na tela Configurações.
void ApplySettings()
{
    PasswordHasher.MinLength = settings.Int("security.min_password_length");
    HealthAssessment.Configure(new HealthAssessment.Thresholds(settings.Int("health.disk_warn_pct"), settings.Int("health.disk_crit_pct"),
        settings.Int("health.signature_max_days"), settings.Int("health.uptime_max_days")));
}
settings.Changed += ApplySettings;
ApplySettings();

var auth = app.Services.GetRequiredService<AuthStore>();
var tokens = app.Services.GetRequiredService<TokenStore>();
var setup = app.Services.GetRequiredService<SetupService>();
var deployStore = app.Services.GetRequiredService<DeployStore>();
auth.Bootstrap(adminUser, adminPassword, resetAdmin);
tokens.ImportConfigToken(configEnrollToken);
if (!setup.NeedsSetup) tokens.EnsureOneActive();   // instalação automatizada (senha por variável) ou banco antigo: sempre há um token

// Sem nenhuma conta: o código de configuração aparece no log assim que o servidor estiver ouvindo
app.Lifetime.ApplicationStarted.Register(() => { if (setup.NeedsSetup) setup.Announce(app.Urls.Count > 0 ? app.Urls : ["http://localhost:8080"]); });

app.UseForwardedHeaders();

// ---- Cabeçalhos de segurança em todas as respostas ----
// A política de conteúdo (CSP) só deixa rodar script que vem do próprio servidor: mesmo que algum texto vindo de uma máquina
// escapasse do tratamento do painel, o navegador se recusaria a executá-lo.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";   // dados da API nunca ficam em cache do navegador
    await next();
});

// ---- Quem está logado (cookie de sessão) ----
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path;
    var needsUser = p.StartsWithSegments("/api") || p == "/" || p.Equals("/index.html") || p.Equals("/login.html") || p.Equals("/setup.html");
    if (needsUser && ctx.Request.Cookies[AuthEndpoints.CookieName] is { Length: > 0 } token)
        ctx.Items["user"] = auth.Resolve(token, authOptions.IdleTimeout);
    await next();
});

// ---- O painel exige login; quem já está logado não vê a tela de login ----
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path;
    var loggedIn = ctx.Current() is not null;
    var isPage = p == "/" || p.Equals("/index.html") || p.Equals("/login.html");
    // Painel ainda sem conta: tudo leva ao assistente; depois de configurado, o assistente deixa de existir
    if (isPage && setup.NeedsSetup) { ctx.Response.Redirect("/setup.html"); return; }
    if (p.Equals("/setup.html") && !setup.NeedsSetup) { ctx.Response.Redirect(loggedIn ? "/" : "/login.html"); return; }
    if ((p == "/" || p.Equals("/index.html")) && !loggedIn) { ctx.Response.Redirect("/login.html"); return; }
    if (p.Equals("/login.html") && loggedIn) { ctx.Response.Redirect("/"); return; }
    await next();
});

app.UseDefaultFiles();
// O painel é um conjunto de arquivos estáticos que mudam a cada versão: sempre revalidar (ETag, barato) evita o navegador
// ficar com um JavaScript antigo depois de uma atualização.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapGet("/healthz", () => "ok");
app.MapAuth(authOptions);
app.MapSetup(authOptions);

// ---- Agentes (autenticam por token, não por sessão) ----
app.MapPost("/api/checkin", (HttpContext ctx, InventoryReport report, MachineStore store) =>
{
    if (!tokens.Validate(ctx.Request.Headers["X-Enroll-Token"])) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(report.AgentId) || report.AgentId.Length > 100) return Results.BadRequest("AgentId inválido");

    store.Upsert(report, ctx.Connection.RemoteIpAddress?.ToString());
    // Se este computador estava na lista de uma instalação em massa, o primeiro check-in prova que a instalação funcionou
    deployStore.OnCheckin(report.Network.SelectMany(n => n.Ipv4).Append(ctx.Connection.RemoteIpAddress?.ToString() ?? "").Where(ip => ip.Length > 0).Distinct().ToList(), report.Identity.Hostname);
    return Results.NoContent();
});

// ---- Painel: leitura (qualquer perfil) ----
object Project(MachineSummary m) => new
{
    m.AgentId, m.Hostname, m.PrimaryIp, m.Mac, m.Os, m.LoggedUser, m.Manufacturer, m.Model, m.Serial, m.AgentVersion,
    m.FirstSeen, m.LastSeen, m.RemoteIp, m.Status, m.HealthScore, m.Issues,
    Online = DateTimeOffset.UtcNow - m.LastSeen < TimeSpan.FromMinutes(settings.Int("agents.offline_after_minutes")),
};

app.MapGet("/api/machines", (MachineStore store) => store.List().Select(Project)).RequireRole(Roles.Viewer);

app.MapGet("/api/machines/{id}", (string id, MachineStore store) =>
    store.Get(id) is { } m ? Results.Json(m, AgentJson.Options) : Results.NotFound()).RequireRole(Roles.Viewer);

app.MapGet("/api/machines/{id}/events", (string id, MachineStore store, int? limit, long? before) =>
    store.Events(agentId: id, limit: limit ?? 200, beforeId: before)).RequireRole(Roles.Viewer);

app.MapGet("/api/machines/{id}/users", (string id, MachineStore store) => store.MachineAssignments(id)).RequireRole(Roles.Viewer);

app.MapGet("/api/machines/{id}/metrics", (string id, MachineStore store, int? hours) =>
    store.Metrics(id, TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, 24 * 90)))).RequireRole(Roles.Viewer);

app.MapGet("/api/events", (MachineStore store, string? agentId, string? type, string? severity, string? q, int? limit, long? before,
        string? user, DateTimeOffset? from, DateTimeOffset? to, long? after) =>
    store.Events(agentId, type, severity, q, limit ?? 200, before, user, from, to, after)).RequireRole(Roles.Viewer);

// Quantos eventos (ex.: alertas das últimas 24 h) para o sininho do painel
app.MapGet("/api/events/count", (MachineStore store, string? severity, DateTimeOffset? from, long? after) =>
    new { count = store.EventsCount(severity, from, after) }).RequireRole(Roles.Viewer);

// Opções dos filtros: PCs (inclusive os já removidos do painel) e usuários que aparecem no log
app.MapGet("/api/events/filters", (MachineStore store) => store.EventFilters()).RequireRole(Roles.Viewer);

// Ferramentas da lista de interesse (ex.: apps de IA) por máquina
app.MapGet("/api/tools", (MachineStore store) => store.ToolsOverview()).RequireRole(Roles.Viewer);

app.MapGet("/api/users", (MachineStore store) => store.Users()).RequireRole(Roles.Viewer);

// O nome do usuário traz barra (DOMINIO\usuario), por isso vai em query string e não na rota
app.MapGet("/api/user-history", (string name, MachineStore store) => store.UserHistory(name)).RequireRole(Roles.Viewer);

// ---- Painel: ações que alteram ou extraem dados. Cada uma fica registrada com o nome de quem fez ----

// Exportação CSV com os mesmos filtros (separador ";" e BOM para o Excel em português abrir certo). Operador ou administrador.
app.MapGet("/api/events/export", (HttpContext http, MachineStore store, string? agentId, string? type, string? severity, string? q,
        string? user, DateTimeOffset? from, DateTimeOffset? to) =>
{
    static string Cell(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        // Nome de software ou de dispositivo vem da máquina: sem isso, "=cmd|..." viraria fórmula ao abrir no Excel
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.Contains(';') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    var sb = new StringBuilder("﻿Quando (UTC);Severidade;PC;Usuario;Evento;Campo;Antes;Depois\r\n");
    var rows = store.Events(agentId, type, severity, q, 50000, null, user, from, to);
    foreach (var e in rows)
        sb.Append(string.Join(';', e.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"), e.Severity, Cell(e.Hostname), Cell(e.User), e.Type, Cell(e.Field), Cell(e.OldValue), Cell(e.NewValue))).Append("\r\n");

    auth.Log(http.Current()!.Username, "events_export", null, $"{rows.Count} evento(s) exportados", http.ClientIp());
    return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", $"auditoria-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
}).RequireRole(Roles.Operator);

string HostOf(MachineStore store, string id) => store.Get(id)?["identity"]?["hostname"]?.GetValue<string>() ?? id;

app.MapPut("/api/machines/{id}/status", (HttpContext http, string id, StatusRequest body, MachineStore store) =>
{
    if (!MachineStatus.IsValid(body.Status)) return Results.BadRequest("status deve ser active, retired ou stock");
    var host = HostOf(store, id);
    if (!store.SetStatus(id, body.Status)) return Results.NotFound();
    auth.Log(http.Current()!.Username, "device_status", host, $"status → {body.Status}", http.ClientIp());
    return Results.NoContent();
}).RequireRole(Roles.Operator);

// Remover é irreversível pelo painel: só administrador
app.MapDelete("/api/machines/{id}", (HttpContext http, string id, MachineStore store) =>
{
    var host = HostOf(store, id);
    if (!store.Delete(id)) return Results.NotFound();
    auth.Log(http.Current()!.Username, "device_removed", host, null, http.ClientIp());
    return Results.NoContent();
}).RequireRole(Roles.Admin);

// ---- Instalação em massa (varredura, instalador, trabalhos) ----
app.MapDeploy(app.Services.GetRequiredService<NetworkScanner>(), deployStore, app.Services.GetRequiredService<PackageStore>(), tokens, app.Services.GetRequiredService<MachineStore>(), auth);

// ---- Tarefas remotas (teste de velocidade) ----
app.MapAgentTasks(tokens, app.Services.GetRequiredService<TaskStore>(), app.Services.GetRequiredService<MachineStore>(), auth, settings);

// ---- Configurações, uso de disco e manutenção ----
app.MapSettings(settings, app.Services.GetRequiredService<RetentionService>(), app.Services.GetRequiredService<StorageService>(),
    app.Services.GetRequiredService<MachineStore>(), app.Services.GetRequiredService<PackageStore>(), auth);

// ---- Instalação dos agentes: tokens e dados para o comando de instalação (só administrador) ----
// O token aparece inteiro porque o administrador precisa copiá-lo para o comando de instalação; por isso a tela é só dele.
object TokenDto(EnrollToken t) => new { t.Id, t.Label, t.Token, t.CreatedAt, t.CreatedBy, t.RevokedAt, t.LastUsedAt, t.UseCount, Active = t.RevokedAt is null };

app.MapGet("/api/install/info", (HttpContext http) => new
{
    // Endereço que os agentes devem usar: o mesmo por onde o administrador abriu o painel (já considera o proxy/HTTPS)
    serverUrl = $"{http.Request.Scheme}://{http.Request.Host}",
    tokens = tokens.List().Select(TokenDto),
}).RequireRole(Roles.Admin);

app.MapPost("/api/install/tokens", (HttpContext http, NewTokenRequest body) =>
{
    var label = body.Label?.Trim() ?? "";
    if (label.Length is < 1 or > 80) return Results.Json(new { error = "invalid_label", message = "Dê um nome ao token (até 80 caracteres), por exemplo o setor ou a data." }, statusCode: 400);
    var admin = http.Current()!;
    var created = tokens.Create(label, admin.Username);
    auth.Log(admin.Username, "token_created", label, "token de agentes criado", http.ClientIp());
    return Results.Json(TokenDto(created), statusCode: 201);
}).RequireRole(Roles.Admin);

app.MapDelete("/api/install/tokens/{id}", (HttpContext http, string id) =>
{
    var label = tokens.List().FirstOrDefault(t => t.Id == id)?.Label;
    if (label is null || !tokens.Revoke(id)) return Results.NotFound();
    auth.Log(http.Current()!.Username, "token_revoked", label, "token de agentes revogado", http.ClientIp());
    return Results.NoContent();
}).RequireRole(Roles.Admin);

app.Run();

public sealed record NewTokenRequest(string? Label);

record StatusRequest(string Status);
