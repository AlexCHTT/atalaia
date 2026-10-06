using System.Reflection;

namespace Atalaia.Server;

public sealed record SettingsUpdate(Dictionary<string, string?>? Changes, bool Confirm);

/// <summary>Tela Configurações: parâmetros, uso de disco e ações de manutenção. Tudo é só do administrador, exceto a identidade pública (login).</summary>
public static class SettingsEndpoints
{
    private static string[] HealthKeys => SettingsCatalog.All.Where(d => d.Group == "health").Select(d => d.Key).ToArray();

    public static void MapSettings(this WebApplication app, SettingsStore settings, RetentionService retention, StorageService storage, MachineStore machines,
        PackageStore packages, AuthStore auth)
    {
        static IResult Error(int status, string code, string message, object? extra = null) =>
            Results.Json(new { error = code, message, extra }, statusCode: status);

        object Dto(SettingItem i) => new
        {
            i.Def.Key, i.Def.Group, i.Def.Label, i.Def.Help, Kind = i.Def.Kind.ToString().ToLowerInvariant(), i.Def.Min, i.Def.Max, i.Def.Unit, i.Def.Retention,
            i.Value, i.Source, Default = i.Def.Default,
            // valor que vale se o administrador "restaurar o padrão": o da configuração do servidor, se houver, senão o padrão de fábrica
            Fallback = i.Def.ConfigKey is not null && app.Configuration[i.Def.ConfigKey] is { Length: > 0 } c ? c : i.Def.Default,
        };

        object System() => new
        {
            ServerVersion = typeof(SettingsEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?",
            AgentVersion = packages.Info()?.Version,
            DbPath = DbPath.Resolve(app.Configuration),
            TrustedProxies = app.Configuration["Server:TrustedProxies"],
            Machines = machines.List().Count,
        };

        // Identidade e regra de senha: sem login, para a tela de entrada e o assistente de primeira execução
        app.MapGet("/api/public/info", () => new { company = settings.Text("brand.company_name"), minPasswordLength = settings.Int("security.min_password_length") });

        app.MapGet("/api/settings", () => new
        {
            groups = SettingsCatalog.Groups.Select(g => new { id = g.Id, label = g.Label, help = g.Help }),
            items = settings.Items().Select(Dto),
            system = System(),
        }).RequireRole(Roles.Admin);

        app.MapPut("/api/settings", (HttpContext http, SettingsUpdate body) =>
        {
            var user = http.Current()!;
            var changes = body.Changes ?? [];
            if (changes.Count == 0) return Error(400, "empty", "Nenhuma alteração.");

            var errors = settings.Validate(changes);
            if (errors.Count > 0) return Results.Json(new { error = "invalid", message = "Há valores inválidos.", errors }, statusCode: 400);

            // Apagar dados é irreversível: o servidor mostra o impacto e só segue com confirmação explícita (não depende da tela)
            var impact = retention.Impact(changes);
            if (impact.Count > 0 && !body.Confirm)
                return Results.Json(new { error = "confirmation_required", message = "Esta alteração apagaria dados.", impact }, statusCode: 409);

            var applied = settings.Apply(changes);
            foreach (var c in applied)
                auth.Log(user.Username, "setting_changed", c.Label, $"{c.Old ?? "—"} → {c.New ?? "—"}", http.ClientIp());

            PurgeResult? purged = null;
            if (impact.Count > 0 && applied.Any(a => SettingsCatalog.ByKey[a.Key].Retention)) purged = retention.PurgeNow();

            var recalculated = applied.Any(a => HealthKeys.Contains(a.Key)) ? machines.RecalculateAllHealth() : 0;
            return Results.Json(new { applied = applied.Count, purged, recalculated, items = settings.Items().Select(Dto) });
        }).RequireRole(Roles.Admin);

        app.MapGet("/api/system/storage", () => storage.Report()).RequireRole(Roles.Admin);

        // Aplica agora a política de retenção já definida (a rotina automática roda a cada 6 horas)
        app.MapPost("/api/system/purge-now", (HttpContext http) =>
        {
            var user = http.Current()!;
            var r = retention.PurgeNow();
            auth.Log(user.Username, "purge_now", "retenção", $"{r.Events} evento(s), {r.AccessLog} acesso(s), {r.Metrics} métrica(s), {r.SpeedTests} teste(s) removidos", http.ClientIp());
            return Results.Json(r);
        }).RequireRole(Roles.Admin);

        app.MapPost("/api/system/recalculate-health", (HttpContext http) =>
        {
            var n = machines.RecalculateAllHealth();
            auth.Log(http.Current()!.Username, "health_recalculated", $"{n} dispositivo(s)", null, http.ClientIp());
            return Results.Json(new { recalculated = n });
        }).RequireRole(Roles.Admin);
    }
}
