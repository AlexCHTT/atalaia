using Microsoft.Data.Sqlite;

namespace Atalaia.Server;

public sealed record TableUsage(string Name, string Label, long Rows, long? Bytes, DateTimeOffset? Oldest, DateTimeOffset? Newest);
public sealed record GrowthEstimate(string Table, string Label, double RowsPerDay, long? BytesPerMonth, int? RetentionDays, long? SteadyStateBytes);
public sealed record StorageReport(string DbPath, long DbBytes, long WalBytes, long? DiskTotal, long? DiskFree, bool PerTableSizes, bool SizesAreEstimates,
    List<TableUsage> Tables, List<GrowthEstimate> Growth, long? AlertBytes, bool OverLimit);

/// <summary>Quanto o banco ocupa, onde, e para onde ele cresce (estimativa a partir da última semana de dados).</summary>
public sealed class StorageService(IConfiguration config, SettingsStore settings)
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["machines"] = "Dispositivos (inventário)", ["events"] = "Auditoria dos dispositivos", ["metrics"] = "Métricas de desempenho", ["assignments"] = "Usuários por computador",
        ["access_log"] = "Registro de acessos", ["accounts"] = "Contas do painel", ["sessions"] = "Sessões", ["agent_tasks"] = "Testes de velocidade",
        ["deploy_jobs"] = "Instalações em massa", ["deploy_targets"] = "Computadores das instalações", ["enroll_tokens"] = "Tokens de agentes", ["settings"] = "Configurações",
    };
    private static readonly string[] TimeSeries = ["events", "metrics", "access_log"];

    /// <summary>Soma o tamanho do conteúdo de todas as colunas (texto/blob pelo comprimento, números como 8 bytes).</summary>
    private static long? EstimateContentBytes(SqliteConnection conn, string table)
    {
        try
        {
            var parts = new List<string>();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var rd = c.ExecuteReader();
                while (rd.Read())
                {
                    var col = rd.GetString(1);
                    parts.Add($"CASE typeof(\"{col}\") WHEN 'text' THEN length(CAST(\"{col}\" AS BLOB)) WHEN 'blob' THEN length(\"{col}\") WHEN 'null' THEN 1 ELSE 8 END");
                }
            }
            if (parts.Count == 0) return 0;
            using var q = conn.CreateCommand();
            q.CommandText = $"SELECT COALESCE(SUM({string.Join(" + ", parts)}), 0) FROM \"{table}\"";
            return Convert.ToInt64(q.ExecuteScalar());
        }
        catch (SqliteException) { return null; }
    }

    public StorageReport Report()
    {
        var path = Path.GetFullPath(DbPath.Resolve(config));
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();

        long Scalar(string sql, params (string, object)[] ps)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v);
            return Convert.ToInt64(c.ExecuteScalar() ?? 0);
        }

        // Tamanho por tabela (tabela + índices) via dbstat. A build do SQLite embutida costuma não ter essa função; nesse caso o tamanho
        // é ESTIMADO somando o conteúdo das colunas de cada tabela (não inclui índices nem espaço livre, então fica um pouco abaixo do real).
        var bytes = new Dictionary<string, long>();
        var perTable = false;
        try
        {
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT COALESCE(m.tbl_name, d.name), SUM(d.pgsize) FROM dbstat d LEFT JOIN sqlite_master m ON m.name = d.name GROUP BY 1";
            using var rd = c.ExecuteReader();
            while (rd.Read()) bytes[rd.GetString(0)] = rd.GetInt64(1);
            perTable = true;
        }
        catch (SqliteException) { /* sem dbstat: estima abaixo */ }
        var estimated = !perTable;

        var names = new List<string>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var rd = c.ExecuteReader();
            while (rd.Read()) names.Add(rd.GetString(0));
        }

        DateTimeOffset? Date(string sql)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            return c.ExecuteScalar() is string s ? DateTimeOffset.Parse(s) : null;
        }

        var tables = new List<TableUsage>();
        var growth = new List<GrowthEstimate>();
        var now = DateTimeOffset.UtcNow;
        foreach (var name in names)
        {
            var rows = Scalar($"SELECT COUNT(*) FROM \"{name}\"");
            long? size = bytes.TryGetValue(name, out var b) ? b : estimated ? EstimateContentBytes(conn, name) : null;
            DateTimeOffset? oldest = null, newest = null;
            if (TimeSeries.Contains(name)) { oldest = Date($"SELECT MIN(at) FROM {name}"); newest = Date($"SELECT MAX(at) FROM {name}"); }
            tables.Add(new TableUsage(name, Labels.GetValueOrDefault(name, name), rows, size, oldest, newest));

            if (TimeSeries.Contains(name) && rows > 0 && oldest is { } first)
            {
                var spanDays = Math.Clamp((now - first).TotalDays, 1, 7);   // dias de dados considerados: no máximo a última semana
                var recent = Scalar($"SELECT COUNT(*) FROM {name} WHERE at >= $s", ("$s", now.AddDays(-spanDays).ToString("O")));
                var perDay = recent / spanDays;
                long? perRow = size is { } sz ? sz / rows : null;
                var retention = name switch { "metrics" => settings.Int("retention.metrics_days"), "events" => settings.Int("retention.events_days"), _ => settings.Int("retention.access_log_days") };
                growth.Add(new GrowthEstimate(name, Labels[name], Math.Round(perDay, 1),
                    perRow is { } pr ? (long)(perDay * 30 * pr) : null,
                    retention > 0 ? retention : null,
                    perRow is { } pr2 && retention > 0 ? (long)(perDay * retention * pr2) : null));
            }
        }

        var dbBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        var wal = File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0;
        long? total = null, free = null;
        try { var d = new DriveInfo(Path.GetPathRoot(path)!); total = d.TotalSize; free = d.AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { /* sem informação do disco */ }

        var alertMb = settings.Int("storage.alert_mb");
        long? alert = alertMb > 0 ? alertMb * 1024L * 1024 : null;
        return new StorageReport(path, dbBytes, wal, total, free, perTable || estimated, estimated, tables.OrderByDescending(t => t.Bytes ?? t.Rows).ToList(), growth, alert,
            alert is { } a && dbBytes + wal > a);
    }
}
