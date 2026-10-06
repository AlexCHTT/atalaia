namespace Atalaia.Server;

public sealed record PurgeResult(int Events, int AccessLog, int Metrics, int SpeedTests);
public sealed record RetentionImpact(string Key, string Label, int Rows, string Detail);

/// <summary>
/// Aplica a política de retenção definida em Configurações. A auditoria e o registro de acessos são "somente acréscimo": o banco
/// recusa DELETE, e apagar só é possível por estes caminhos controlados, que deixam um registro de que a limpeza aconteceu.
/// </summary>
public sealed class RetentionService(MachineStore machines, AuthStore auth, TaskStore tasks, SettingsStore settings, ILogger<RetentionService> logger)
{
    public PurgeResult PurgeNow()
    {
        var now = DateTimeOffset.UtcNow;
        var metrics = machines.PurgeMetrics(now - TimeSpan.FromDays(settings.Int("retention.metrics_days")));
        var eventsDays = settings.Int("retention.events_days");
        var events = eventsDays > 0 ? machines.PurgeEvents(now - TimeSpan.FromDays(eventsDays), now) : 0;
        var accessDays = settings.Int("retention.access_log_days");
        var access = accessDays > 0 ? auth.PurgeAccessLog(now - TimeSpan.FromDays(accessDays)) : 0;
        var speed = tasks.PruneAll();
        if (events + access + metrics + speed > 0)
            logger.LogInformation("Retenção: {E} evento(s), {A} registro(s) de acesso, {M} amostra(s) de métricas, {S} teste(s) de velocidade removidos", events, access, metrics, speed);
        return new PurgeResult(events, access, metrics, speed);
    }

    private static string N(int n) => n.ToString("N0", new System.Globalization.CultureInfo("pt-BR"));

    /// <summary>O que seria apagado se estas alterações fossem gravadas agora (só o que realmente existe no banco e ficaria fora do novo prazo).</summary>
    public List<RetentionImpact> Impact(IReadOnlyDictionary<string, string?> changes)
    {
        var eff = settings.Effective(changes);
        var now = DateTimeOffset.UtcNow;
        var list = new List<RetentionImpact>();

        void AddDays(string key, Func<DateTimeOffset, int> count, string what)
        {
            if (!changes.ContainsKey(key)) return;
            var days = int.Parse(eff[key]);
            if (days <= 0) return;   // 0 = guardar para sempre: nunca apaga
            var rows = count(now - TimeSpan.FromDays(days));
            if (rows > 0) list.Add(new RetentionImpact(key, SettingsCatalog.ByKey[key].Label, rows, $"{N(rows)} {what} com mais de {days} dias seriam apagados"));
        }
        AddDays("retention.events_days", machines.CountEventsOlderThan, "evento(s) de auditoria");
        AddDays("retention.access_log_days", auth.CountAccessLogOlderThan, "registro(s) de acesso");
        AddDays("retention.metrics_days", machines.CountMetricsOlderThan, "amostra(s) de métricas");

        if (changes.ContainsKey("retention.speedtests_keep"))
        {
            var keep = int.Parse(eff["retention.speedtests_keep"]);
            var beyond = tasks.CountBeyond(keep);
            if (beyond > 0)
                list.Add(new RetentionImpact("retention.speedtests_keep", SettingsCatalog.ByKey["retention.speedtests_keep"].Label, beyond,
                    $"{N(beyond)} teste(s) de velocidade antigos seriam apagados (ficam só os {keep} mais recentes por computador)"));
        }
        return list;
    }
}
