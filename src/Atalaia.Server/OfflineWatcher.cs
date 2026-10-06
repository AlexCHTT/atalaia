namespace Atalaia.Server;

/// <summary>
/// Rotina de fundo do servidor. Máquina que some não manda nada para o servidor notar, então este vigia confere de minuto em minuto
/// e registra no log de auditoria, uma vez só, quando uma máquina passa do limite sem check-in. (O evento "online" é gravado no
/// próprio check-in, quando ela volta.) A cada 6 horas aplica a política de retenção e confere o tamanho do banco.
/// Todos os limites vêm da tela Configurações e são relidos a cada ciclo: mudar vale sem reiniciar.
/// </summary>
public sealed class OfflineWatcher(MachineStore store, AuthStore auth, AuthOptions authOptions, SettingsStore settings, RetentionService retention,
    StorageService storage, ILogger<OfflineWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        var lastPurge = DateTimeOffset.MinValue;

        do
        {
            try
            {
                auth.PurgeSessions(authOptions.IdleTimeout);   // sessões vencidas somem do banco
                var n = store.MarkOffline(TimeSpan.FromMinutes(settings.Int("agents.offline_after_minutes")), DateTimeOffset.UtcNow);
                if (n > 0) logger.LogInformation("{N} máquina(s) marcada(s) como offline", n);

                if (DateTimeOffset.UtcNow - lastPurge > TimeSpan.FromHours(6))
                {
                    lastPurge = DateTimeOffset.UtcNow;
                    retention.PurgeNow();   // métricas expiram sempre; auditoria e registro de acessos só se houver prazo definido (0 = para sempre)

                    var report = storage.Report();
                    if (report.OverLimit)
                        logger.LogWarning("O banco ({Mb:N0} MB) passou do limite de aviso configurado ({Limit:N0} MB). Veja Configurações > Dados e retenção.",
                            (report.DbBytes + report.WalBytes) / 1024 / 1024, report.AlertBytes / 1024 / 1024);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha na rotina de fundo");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
