using Microsoft.Data.Sqlite;

namespace Atalaia.Server;

public sealed record AgentTaskRow(string Id, string AgentId, string Type, string? Target, string Status, string? RequestedBy,
    DateTimeOffset RequestedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? ResultJson, string? Error);

/// <summary>
/// Fila de tarefas por máquina (hoje: teste de velocidade). O agente é quem busca (poll), então nada é "empurrado" para o PC.
/// Estados: pending (esperando o agente buscar) → running (agente pegou) → done | failed | expired.
/// Uma máquina tem no máximo uma tarefa ativa: pedir de novo enquanto roda devolve a que já existe.
/// </summary>
public sealed class TaskStore
{
    public static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(3);    // agente offline: o pedido vale por este tempo (o agente busca a cada 30 s)
    public static readonly TimeSpan RunningTtl = TimeSpan.FromMinutes(3);    // um teste leva ~20 s; passou disso o agente caiu no meio

    private readonly string _connectionString;
    private readonly object _lock = new();
    private readonly SettingsStore _settings;

    public TaskStore(IConfiguration config, SettingsStore settings)
    {
        _settings = settings;
        var path = DbPath.Resolve(config);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();

        using var conn = Open();
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS agent_tasks (
                id           TEXT PRIMARY KEY,
                agent_id     TEXT NOT NULL,
                type         TEXT NOT NULL,
                target       TEXT,
                status       TEXT NOT NULL,
                requested_by TEXT,
                requested_at TEXT NOT NULL,
                started_at   TEXT,
                completed_at TEXT,
                result       TEXT,
                error        TEXT
            )
            """);
        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_agent_tasks_agent ON agent_tasks (agent_id, requested_at DESC)");
    }

    /// <summary>Cria a tarefa, ou devolve a que já está ativa para a máquina (Created=false).</summary>
    public (AgentTaskRow Task, bool Created) Create(string agentId, string type, string? target, string? requestedBy)
    {
        lock (_lock)
        {
            using var conn = Open();
            ExpireStale(conn);
            if (Select(conn, "agent_id = $a AND status IN ('pending','running')", 1, ("$a", agentId)).FirstOrDefault() is { } active)
                return (active, false);

            var id = Guid.NewGuid().ToString("N");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO agent_tasks (id, agent_id, type, target, status, requested_by, requested_at) VALUES ($id, $a, $t, $tg, 'pending', $by, $now)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$a", agentId);
            cmd.Parameters.AddWithValue("$t", type);
            cmd.Parameters.AddWithValue("$tg", (object?)target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$by", (object?)requestedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.ExecuteNonQuery();
            return (Select(conn, "id = $id", 1, ("$id", id)).First(), true);
        }
    }

    /// <summary>O agente busca o que há para fazer; o que sai daqui passa a "running".</summary>
    public List<AgentTaskRow> Poll(string agentId)
    {
        lock (_lock)
        {
            using var conn = Open();
            ExpireStale(conn);
            var pending = Select(conn, "agent_id = $a AND status = 'pending'", 5, ("$a", agentId), asc: true);
            foreach (var t in pending)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE agent_tasks SET status = 'running', started_at = $now WHERE id = $id AND status = 'pending'";
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.Parameters.AddWithValue("$id", t.Id);
                cmd.ExecuteNonQuery();
            }
            return pending;
        }
    }

    /// <summary>Grava o resultado. Só vale se a tarefa é desta máquina e ainda está em execução.</summary>
    public bool Complete(string id, string agentId, bool ok, string? error, string? resultJson)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agent_tasks SET status = $s, completed_at = $now, result = $r, error = $e WHERE id = $id AND agent_id = $a AND status = 'running'";
            cmd.Parameters.AddWithValue("$s", ok ? "done" : "failed");
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$r", (object?)resultJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$e", (object?)Truncate(error, 300) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$a", agentId);
            var changed = cmd.ExecuteNonQuery() > 0;
            if (changed) Prune(conn, agentId);
            return changed;
        }
    }

    /// <summary>
    /// Cancela a tarefa ativa da máquina (pendente ou em execução) e libera um novo pedido na hora. Devolve null se não há nenhuma.
    /// Se o agente já estava medindo, ele termina o teste, mas o resultado é descartado (a tarefa deixou de estar "em execução").
    /// </summary>
    public AgentTaskRow? Cancel(string agentId)
    {
        lock (_lock)
        {
            using var conn = Open();
            ExpireStale(conn);
            if (Select(conn, "agent_id = $a AND status IN ('pending','running')", 1, ("$a", agentId)).FirstOrDefault() is not { } active) return null;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agent_tasks SET status = 'cancelled', completed_at = $now WHERE id = $id AND status IN ('pending','running')";
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$id", active.Id);
            return cmd.ExecuteNonQuery() > 0 ? Select(conn, "id = $id", 1, ("$id", active.Id)).First() : null;
        }
    }

    public List<AgentTaskRow> List(string agentId, int limit)
    {
        lock (_lock)
        {
            using var conn = Open();
            ExpireStale(conn);
            return Select(conn, "agent_id = $a", Math.Clamp(limit, 1, 50), ("$a", agentId));
        }
    }

    /// <summary>Quantos testes sairiam do histórico se cada máquina guardasse só <paramref name="keep"/>.</summary>
    public int CountBeyond(int keep)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(MAX(n - $k, 0)), 0) FROM (SELECT COUNT(*) AS n FROM agent_tasks WHERE status NOT IN ('pending','running') GROUP BY agent_id)";
            cmd.Parameters.AddWithValue("$k", keep);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public int PruneAll()
    {
        lock (_lock)
        {
            using var conn = Open();
            var keep = _settings.Int("retention.speedtests_keep");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM agent_tasks WHERE status NOT IN ('pending','running') AND id NOT IN (SELECT id FROM (SELECT id, ROW_NUMBER() OVER (PARTITION BY agent_id ORDER BY requested_at DESC) AS rn FROM agent_tasks) WHERE rn <= {keep})";
            return cmd.ExecuteNonQuery();
        }
    }

    // ---------- Internos ----------

    private static void ExpireStale(SqliteConnection conn)
    {
        var now = DateTimeOffset.UtcNow;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_tasks SET status = 'expired', completed_at = $now WHERE status = 'pending' AND requested_at < $pendingCut;
            UPDATE agent_tasks SET status = 'failed', completed_at = $now, error = 'O agente parou de responder durante o teste.' WHERE status = 'running' AND started_at < $runningCut;
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$pendingCut", (now - PendingTtl).ToString("O"));
        cmd.Parameters.AddWithValue("$runningCut", (now - RunningTtl).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private void Prune(SqliteConnection conn, string agentId)
    {
        var keep = _settings.Int("retention.speedtests_keep");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM agent_tasks WHERE agent_id = $a AND id NOT IN (SELECT id FROM agent_tasks WHERE agent_id = $a ORDER BY requested_at DESC LIMIT {keep})";
        cmd.Parameters.AddWithValue("$a", agentId);
        cmd.ExecuteNonQuery();
    }

    private static List<AgentTaskRow> Select(SqliteConnection conn, string where, int limit, (string Name, object Value) p, bool asc = false)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, agent_id, type, target, status, requested_by, requested_at, started_at, completed_at, result, error FROM agent_tasks WHERE {where} ORDER BY requested_at {(asc ? "ASC" : "DESC")} LIMIT {limit}";
        cmd.Parameters.AddWithValue(p.Name, p.Value);
        using var rd = cmd.ExecuteReader();
        var list = new List<AgentTaskRow>();
        while (rd.Read())
            list.Add(new AgentTaskRow(rd.GetString(0), rd.GetString(1), rd.GetString(2), Str(rd, 3), rd.GetString(4), Str(rd, 5),
                DateTimeOffset.Parse(rd.GetString(6)), Date(rd, 7), Date(rd, 8), Str(rd, 9), Str(rd, 10)));
        return list;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
    private static void Exec(SqliteConnection conn, string sql) { using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private static string? Str(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
    private static DateTimeOffset? Date(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : DateTimeOffset.Parse(rd.GetString(i));
}
