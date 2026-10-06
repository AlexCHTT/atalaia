using System.Text.Json;
using System.Text.Json.Nodes;
using Atalaia.Shared;
using Microsoft.Data.Sqlite;

namespace Atalaia.Server;

public sealed record MachineSummary(
    string AgentId, string? Hostname, string? PrimaryIp, string? Mac, string? Os, string? LoggedUser,
    string? Manufacturer, string? Model, string? Serial, string? AgentVersion,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string? RemoteIp, string Status, int? HealthScore, List<Issue> Issues);

/// <summary>Período em que um usuário apareceu no console de um PC.</summary>
public sealed record Assignment(long Id, string AgentId, string? Hostname, string User, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, bool Current);

public sealed record UserSummary(string User, int Machines, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string? LastHostname);

public static class MachineStatus
{
    public const string Active = "active", Retired = "retired", Stock = "stock";
    public static bool IsValid(string? s) => s is Active or Retired or Stock;
}

/// <summary>
/// Último estado de cada máquina + histórico:
///  - machines: snapshot atual (colunas desnormalizadas para a listagem, relatório completo em JSON);
///  - events: log de auditoria append-only (gatilhos do banco barram UPDATE/DELETE);
///  - assignments: quem usou cada PC e quando (a linha mais recente de um PC é o usuário atual).
/// </summary>
public sealed class MachineStore
{
    private readonly string _connectionString;
    private static string CreateNoDeleteTrigger(string ifNotExists = "") =>
        $"CREATE TRIGGER {ifNotExists}events_no_delete BEFORE DELETE ON events BEGIN SELECT RAISE(ABORT, 'events e append-only'); END";

    private readonly UsbIds _usb;
    private readonly object _writeLock = new(); // um servidor só: serializa as escritas e evita ler-modificar-escrever concorrente

    public MachineStore(IConfiguration config, UsbIds usb)
    {
        _usb = usb;
        var path = DbPath.Resolve(config);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();

        using var conn = Open();
        Exec(conn, "PRAGMA journal_mode=WAL");
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS machines (
                agent_id      TEXT PRIMARY KEY,
                hostname      TEXT,
                primary_ip    TEXT,
                mac           TEXT,
                os            TEXT,
                logged_user   TEXT,
                manufacturer  TEXT,
                model         TEXT,
                serial        TEXT,
                agent_version TEXT,
                remote_ip     TEXT,
                first_seen    TEXT NOT NULL,
                last_seen     TEXT NOT NULL,
                report_json   TEXT NOT NULL,
                software_json TEXT
            )
            """);
        AddColumnIfMissing(conn, "machines", "status", "TEXT NOT NULL DEFAULT 'active'");
        AddColumnIfMissing(conn, "machines", "offline_flagged", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "machines", "health_score", "INTEGER");
        AddColumnIfMissing(conn, "machines", "issues_json", "TEXT");
        BackfillHealth(conn);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS events (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                at        TEXT NOT NULL,
                agent_id  TEXT NOT NULL,
                hostname  TEXT,
                type      TEXT NOT NULL,
                severity  TEXT NOT NULL DEFAULT 'info',
                field     TEXT,
                old_value TEXT,
                new_value TEXT
            )
            """);
        AddColumnIfMissing(conn, "events", "user_name", "TEXT");
        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_events_agent ON events(agent_id, id DESC)");
        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_events_user ON events(user_name)");
        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_events_at ON events(at)");
        // Auditoria de verdade: nem este código consegue alterar ou apagar o que já foi registrado.
        Exec(conn, "CREATE TRIGGER IF NOT EXISTS events_no_update BEFORE UPDATE ON events BEGIN SELECT RAISE(ABORT, 'events e append-only'); END");
        Exec(conn, CreateNoDeleteTrigger("IF NOT EXISTS "));

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS assignments (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id   TEXT NOT NULL,
                hostname   TEXT,
                user_name  TEXT NOT NULL,
                first_seen TEXT NOT NULL,
                last_seen  TEXT NOT NULL
            )
            """);
        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_assign_agent ON assignments(agent_id, id DESC)");
        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_assign_user ON assignments(user_name)");

        // Série temporal para os gráficos (uma amostra por check-in). Tem retenção; a auditoria (events) não tem.
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS metrics (
                agent_id TEXT NOT NULL,
                at       TEXT NOT NULL,
                cpu      REAL,
                ram_pct  REAL,
                disk_pct REAL
            )
            """);
        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_metrics ON metrics(agent_id, at)");
    }

    // ---------- Escrita ----------

    public void Upsert(InventoryReport r, string? remoteIp) => Upsert(r, remoteIp, DateTimeOffset.UtcNow);

    public void Upsert(InventoryReport r, string? remoteIp, DateTimeOffset now)
    {
        var primary = r.Network.FirstOrDefault(n => n.IsPrimary)
                      ?? r.Network.FirstOrDefault(n => !n.IsVirtual && n.Ipv4.Count > 0)
                      ?? r.Network.FirstOrDefault();

        // Software sai do relatório principal e vai para a própria coluna
        var node = JsonSerializer.SerializeToNode(r, AgentJson.Options)!.AsObject();
        node.Remove("software");
        var softwareJson = r.Software is null ? null : JsonSerializer.Serialize(r.Software, AgentJson.Options);
        var nowText = now.ToString("O");

        lock (_writeLock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            var previous = LoadPrevious(conn, tx, r.AgentId);
            var events = new List<(string Type, string Severity, string? Field, string? Old, string? New)>();

            if (previous is null)
            {
                events.Add(("registered", "info", null, null, r.Identity.Hostname));
            }
            else
            {
                foreach (var d in ChangeDetector.Diff(ChangeDetector.Facts(previous.Report), ChangeDetector.Facts(r)))
                    events.Add((d.Type, d.Severity, d.Field, d.Old, d.New));

                foreach (var d in ChangeDetector.DiffSoftware(previous.Software, r.Software))
                    events.Add((d.Type, "info", d.Field, d.Old, d.New));

                foreach (var d in ChangeDetector.DiffPeripherals(previous.Report.Peripherals, r.Peripherals,
                             x => _usb.Describe(x.VendorId, x.ProductId, x.Name)))
                    events.Add((d.Type, d.Severity, d.Field, d.Old, d.New));

                if (previous.OfflineFlagged)
                    events.Add(("online", "info", null, null, $"sem sinal desde {previous.LastSeen.ToLocalTime():g}"));
            }

            // Usuário no console: a linha mais recente do PC é o "dono atual"; trocou, abre outra linha.
            if (!string.IsNullOrWhiteSpace(r.Identity.LoggedUser))
            {
                var user = r.Identity.LoggedUser.Trim();
                var current = LatestAssignment(conn, tx, r.AgentId);
                if (current is { } c && string.Equals(c.User, user, StringComparison.OrdinalIgnoreCase))
                {
                    Run(conn, tx, "UPDATE assignments SET last_seen = $now, hostname = $host WHERE id = $id",
                        ("$now", nowText), ("$host", r.Identity.Hostname), ("$id", c.Id));
                }
                else
                {
                    Run(conn, tx, "INSERT INTO assignments (agent_id, hostname, user_name, first_seen, last_seen) VALUES ($a, $host, $u, $now, $now)",
                        ("$a", r.AgentId), ("$host", r.Identity.Hostname), ("$u", user), ("$now", nowText));
                    // Só é "troca" se havia outro usuário antes; o primeiro vínculo conhecido de um PC não é evento.
                    if (current is { } was) events.Add(("user_changed", "info", "identity.loggedUser", was.User, user));
                }
            }

            foreach (var e in events)
                InsertEvent(conn, tx, nowText, r.AgentId, r.Identity.Hostname, e.Type, e.Severity, e.Field, e.Old, e.New);

            var (healthScore, healthIssues) = HealthAssessment.Assess(r, now);

            Run(conn, tx, """
                INSERT INTO machines (agent_id, hostname, primary_ip, mac, os, logged_user, manufacturer, model, serial,
                                      agent_version, remote_ip, first_seen, last_seen, report_json, software_json, health_score, issues_json)
                VALUES ($id, $host, $ip, $mac, $os, $user, $manu, $model, $serial, $ver, $remote, $now, $now, $json, $sw, $score, $issues)
                ON CONFLICT(agent_id) DO UPDATE SET
                    hostname = $host, primary_ip = $ip, mac = $mac, os = $os, logged_user = $user,
                    manufacturer = $manu, model = $model, serial = $serial, agent_version = $ver,
                    remote_ip = $remote, last_seen = $now, report_json = $json,
                    software_json = COALESCE($sw, software_json), offline_flagged = 0,
                    health_score = $score, issues_json = $issues
                """,
                ("$id", r.AgentId), ("$host", r.Identity.Hostname), ("$ip", primary?.Ipv4.FirstOrDefault()), ("$mac", primary?.Mac),
                ("$os", r.Os.Name), ("$user", r.Identity.LoggedUser), ("$manu", r.Hardware.Manufacturer), ("$model", r.Hardware.Model),
                ("$serial", r.Hardware.SerialNumber), ("$ver", r.AgentVersion), ("$remote", remoteIp), ("$now", nowText),
                ("$json", node.ToJsonString(AgentJson.Options)), ("$sw", softwareJson),
                ("$score", healthScore), ("$issues", JsonSerializer.Serialize(healthIssues, AgentJson.Options)));

            var sample = MetricSample(r);
            if (sample is { } m)
                Run(conn, tx, "INSERT INTO metrics (agent_id, at, cpu, ram_pct, disk_pct) VALUES ($a, $at, $cpu, $ram, $disk)",
                    ("$a", r.AgentId), ("$at", nowText), ("$cpu", m.Cpu), ("$ram", m.Ram), ("$disk", m.Disk));

            tx.Commit();
        }
    }

    /// <summary>Marca como offline (uma vez) as máquinas ativas sem check-in há mais que <paramref name="threshold"/>.</summary>
    public int MarkOffline(TimeSpan threshold, DateTimeOffset now)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            var limit = (now - threshold).ToString("O");
            var stale = new List<(string Id, string? Host, string Last)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT agent_id, hostname, last_seen FROM machines WHERE offline_flagged = 0 AND status = 'active' AND last_seen < $limit";
                cmd.Parameters.AddWithValue("$limit", limit);
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) stale.Add((rd.GetString(0), Str(rd, 1), rd.GetString(2)));
            }

            foreach (var (id, host, last) in stale)
            {
                InsertEvent(conn, tx, now.ToString("O"), id, host, "offline", "warn", null, null, $"último sinal {DateTimeOffset.Parse(last).ToLocalTime():g}");
                Run(conn, tx, "UPDATE machines SET offline_flagged = 1 WHERE agent_id = $id", ("$id", id));
            }
            tx.Commit();
            return stale.Count;
        }
    }

    public bool SetStatus(string agentId, string status)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT hostname, status FROM machines WHERE agent_id = $id";
            cmd.Parameters.AddWithValue("$id", agentId);
            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return false;
            var (host, old) = (Str(rd, 0), rd.GetString(1));
            rd.Close();
            if (old == status) return true;

            Run(conn, tx, "UPDATE machines SET status = $s WHERE agent_id = $id", ("$s", status), ("$id", agentId));
            InsertEvent(conn, tx, DateTimeOffset.UtcNow.ToString("O"), agentId, host, "status_changed", "info", "status", old, status);
            tx.Commit();
            return true;
        }
    }

    /// <summary>Remove o PC do painel. O histórico (eventos e vínculos) permanece, e a remoção também fica registrada.</summary>
    public bool Delete(string agentId)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            string? host;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT hostname FROM machines WHERE agent_id = $id";
                cmd.Parameters.AddWithValue("$id", agentId);
                using var rd = cmd.ExecuteReader();
                if (!rd.Read()) return false;
                host = Str(rd, 0);
            }
            Run(conn, tx, "DELETE FROM machines WHERE agent_id = $id", ("$id", agentId));
            InsertEvent(conn, tx, DateTimeOffset.UtcNow.ToString("O"), agentId, host, "removed", "warn", null, null, "removida do painel");
            tx.Commit();
            return true;
        }
    }

    // ---------- Leitura ----------

    public List<MachineSummary> List()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT agent_id, hostname, primary_ip, mac, os, logged_user, manufacturer, model, serial,
                   agent_version, first_seen, last_seen, remote_ip, status, health_score, issues_json
            FROM machines ORDER BY hostname COLLATE NOCASE
            """;
        using var rd = cmd.ExecuteReader();
        var list = new List<MachineSummary>();
        while (rd.Read())
        {
            list.Add(new MachineSummary(
                rd.GetString(0), Str(rd, 1), Str(rd, 2), Str(rd, 3), Str(rd, 4), Str(rd, 5), Str(rd, 6), Str(rd, 7), Str(rd, 8), Str(rd, 9),
                DateTimeOffset.Parse(rd.GetString(10)), DateTimeOffset.Parse(rd.GetString(11)), Str(rd, 12), rd.GetString(13),
                rd.IsDBNull(14) ? null : rd.GetInt32(14), ParseIssues(Str(rd, 15))));
        }
        return list;
    }

    public sealed record KnownHost(string AgentId, string? Hostname, DateTimeOffset LastSeen, string? AgentVersion);

    /// <summary>Todos os IPv4 conhecidos de cada máquina (adaptadores do relatório, IP principal e o IP visto pelo servidor), para cruzar com a varredura de rede.</summary>
    public Dictionary<string, KnownHost> IpIndex()
    {
        var index = new Dictionary<string, KnownHost>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT agent_id, hostname, last_seen, agent_version, primary_ip, remote_ip, report_json FROM machines WHERE status <> 'retired'";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var host = new KnownHost(rd.GetString(0), Str(rd, 1), DateTimeOffset.Parse(rd.GetString(2)), Str(rd, 3));
            var ips = new HashSet<string>();
            if (Str(rd, 4) is { } primary) ips.Add(primary);
            if (Str(rd, 5) is { } remote) ips.Add(remote);
            try
            {
                if (JsonNode.Parse(rd.GetString(6))?["network"] is JsonArray adapters)
                    foreach (var a in adapters)
                        if (a?["ipv4"] is JsonArray v4) foreach (var ip in v4) if (ip?.GetValue<string>() is { } s) ips.Add(s);
            }
            catch (JsonException) { /* relatório ilegível: segue só com o IP principal */ }
            foreach (var ip in ips) index.TryAdd(ip, host);
        }
        return index;
    }

    /// <summary>Relatório completo + metadados, com o software de volta no lugar. Null se a máquina não existe.</summary>
    public JsonObject? Get(string agentId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT report_json, software_json, first_seen, last_seen, remote_ip, status, health_score, issues_json FROM machines WHERE agent_id = $id";
        cmd.Parameters.AddWithValue("$id", agentId);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;

        var obj = JsonNode.Parse(rd.GetString(0))!.AsObject();
        obj["software"] = Str(rd, 1) is { } sw ? JsonNode.Parse(sw) : null;
        obj["firstSeen"] = rd.GetString(2);
        obj["lastSeen"] = rd.GetString(3);
        obj["remoteIp"] = Str(rd, 4);
        obj["status"] = rd.GetString(5);
        obj["healthScore"] = rd.IsDBNull(6) ? null : rd.GetInt32(6);
        obj["issues"] = JsonSerializer.SerializeToNode(ParseIssues(Str(rd, 7)), AgentJson.Options);

        // Fabricante/modelo legíveis vêm do usb.ids na hora de servir, não do que foi gravado: atualizar o arquivo melhora o histórico.
        if (obj["peripherals"] is JsonArray peripherals)
        {
            foreach (var item in peripherals.OfType<JsonObject>())
            {
                var (vendor, product) = _usb.Lookup(item["vendorId"]?.GetValue<string>(), item["productId"]?.GetValue<string>());
                item["vendorName"] = vendor;
                item["productName"] = product;
            }
        }
        return obj;
    }

    public List<AuditEvent> Events(string? agentId = null, string? type = null, string? severity = null, string? search = null, int limit = 200,
        long? beforeId = null, string? user = null, DateTimeOffset? from = null, DateTimeOffset? to = null, long? afterId = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        void Filter(string clause, string name, object? value)
        {
            if (value is null or "") return;
            where.Add(clause);
            cmd.Parameters.AddWithValue(name, value);
        }
        Filter("agent_id = $agent", "$agent", agentId);
        Filter("type = $type", "$type", type);
        Filter("severity = $sev", "$sev", severity);
        Filter("id < $before", "$before", beforeId);
        Filter("id > $after", "$after", afterId);
        Filter("user_name = $user COLLATE NOCASE", "$user", user);
        Filter("at >= $from", "$from", from?.ToString("O"));
        Filter("at < $to", "$to", to?.ToString("O"));
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(hostname LIKE $q OR user_name LIKE $q OR field LIKE $q OR old_value LIKE $q OR new_value LIKE $q)");
            cmd.Parameters.AddWithValue("$q", $"%{search.Trim()}%");
        }

        cmd.CommandText = $"""
            SELECT id, at, agent_id, hostname, type, severity, field, old_value, new_value, user_name FROM events
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        using var rd = cmd.ExecuteReader();
        var list = new List<AuditEvent>();
        while (rd.Read())
            list.Add(new AuditEvent(rd.GetInt64(0), DateTimeOffset.Parse(rd.GetString(1)), rd.GetString(2), Str(rd, 3), rd.GetString(4), rd.GetString(5), Str(rd, 6), Str(rd, 7), Str(rd, 8), Str(rd, 9)));
        return list;
    }

    public sealed record PcOption(string AgentId, string? Hostname);
    public sealed record EventFilterOptions(List<PcOption> Pcs, List<string> Users);

    public EventFilterOptions EventFilters()
    {
        using var conn = Open();
        var pcs = new List<PcOption>();
        var users = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            // O hostname mais recente de cada PC (eles mudam de nome; o agent_id não)
            cmd.CommandText = "SELECT agent_id, (SELECT hostname FROM events e2 WHERE e2.agent_id = e.agent_id ORDER BY id DESC LIMIT 1) FROM events e WHERE agent_id <> 'system' GROUP BY agent_id";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) pcs.Add(new PcOption(rd.GetString(0), Str(rd, 1)));
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT user_name FROM events WHERE user_name IS NOT NULL ORDER BY user_name COLLATE NOCASE";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) users.Add(rd.GetString(0));
        }
        pcs.Sort((a, b) => string.Compare(a.Hostname, b.Hostname, StringComparison.OrdinalIgnoreCase));
        return new EventFilterOptions(pcs, users);
    }

    public List<Assignment> MachineAssignments(string agentId) => QueryAssignments("a.agent_id = $v", agentId);

    public List<Assignment> UserHistory(string user) => QueryAssignments("a.user_name = $v COLLATE NOCASE", user);

    public List<UserSummary> Users()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.user_name, COUNT(DISTINCT a.agent_id), MIN(a.first_seen), MAX(a.last_seen),
                   (SELECT hostname FROM assignments b WHERE b.user_name = a.user_name COLLATE NOCASE ORDER BY b.last_seen DESC LIMIT 1)
            FROM assignments a GROUP BY a.user_name COLLATE NOCASE ORDER BY MAX(a.last_seen) DESC
            """;
        using var rd = cmd.ExecuteReader();
        var list = new List<UserSummary>();
        while (rd.Read())
            list.Add(new UserSummary(rd.GetString(0), rd.GetInt32(1), DateTimeOffset.Parse(rd.GetString(2)), DateTimeOffset.Parse(rd.GetString(3)), Str(rd, 4)));
        return list;
    }

    public sealed record MetricPoint(DateTimeOffset At, double? Cpu, double? RamPct, double? DiskPct);

    /// <summary>Série para os gráficos. Se houver pontos demais, agrupa fazendo a média para o gráfico não pesar.</summary>
    public List<MetricPoint> Metrics(string agentId, TimeSpan range, int maxPoints = 400)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT at, cpu, ram_pct, disk_pct FROM metrics WHERE agent_id = $a AND at >= $since ORDER BY at";
        cmd.Parameters.AddWithValue("$a", agentId);
        cmd.Parameters.AddWithValue("$since", (DateTimeOffset.UtcNow - range).ToString("O"));
        using var rd = cmd.ExecuteReader();

        var raw = new List<MetricPoint>();
        while (rd.Read())
            raw.Add(new MetricPoint(DateTimeOffset.Parse(rd.GetString(0)), rd.IsDBNull(1) ? null : rd.GetDouble(1), rd.IsDBNull(2) ? null : rd.GetDouble(2), rd.IsDBNull(3) ? null : rd.GetDouble(3)));
        if (raw.Count <= maxPoints) return raw;

        var size = (int)Math.Ceiling(raw.Count / (double)maxPoints);
        return raw.Chunk(size).Select(c => new MetricPoint(
            c[^1].At,
            c.Average(p => p.Cpu), c.Average(p => p.RamPct), c.Average(p => p.DiskPct))).ToList();
    }

    public int CountMetricsOlderThan(DateTimeOffset limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM metrics WHERE at < $l";
        cmd.Parameters.AddWithValue("$l", limit.ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountEventsOlderThan(DateTimeOffset limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events WHERE at < $l AND agent_id <> 'system'";
        cmd.Parameters.AddWithValue("$l", limit.ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Recalcula o índice de saúde de todas as máquinas a partir do último relatório (usado quando os limites mudam).</summary>
    public int RecalculateAllHealth()
    {
        lock (_writeLock)
        {
            using var conn = Open();
            return Backfill(conn, onlyMissing: false);
        }
    }

    public int PurgeMetrics(DateTimeOffset olderThan)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM metrics WHERE at < $limit";
            cmd.Parameters.AddWithValue("$limit", olderThan.ToString("O"));
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Retenção do log de auditoria. O banco barra DELETE em events; este é o único caminho que o libera, e dentro de uma
    /// transação (se algo falhar, o gatilho volta). Cada limpeza deixa um evento "retention_purge", que nunca é apagado.
    /// </summary>
    public int PurgeEvents(DateTimeOffset olderThan, DateTimeOffset now)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            Run(conn, tx, "DROP TRIGGER IF EXISTS events_no_delete");
            int removed;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM events WHERE at < $limit AND agent_id <> 'system'";
                cmd.Parameters.AddWithValue("$limit", olderThan.ToString("O"));
                removed = cmd.ExecuteNonQuery();
            }
            Run(conn, tx, CreateNoDeleteTrigger());

            if (removed > 0)
                InsertEvent(conn, tx, now.ToString("O"), "system", "(sistema)", "retention_purge", "info", null, null,
                    $"{removed} evento(s) anteriores a {olderThan.ToLocalTime():dd/MM/yyyy} removidos pela política de retenção");
            tx.Commit();
            return removed;
        }
    }

    public sealed record ToolHit(string AgentId, string? Hostname, string? User, string Source, string? Detail);
    public sealed record ToolSummary(string Tool, int Machines, List<ToolHit> Hits);

    /// <summary>Quais ferramentas da lista de interesse aparecem em quais máquinas ativas (instaladas ou em execução).</summary>
    public List<ToolSummary> ToolsOverview()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT agent_id, hostname, logged_user, report_json FROM machines WHERE status = 'active'";
        using var rd = cmd.ExecuteReader();

        var byTool = new Dictionary<string, List<ToolHit>>();
        while (rd.Read())
        {
            using var doc = JsonDocument.Parse(rd.GetString(3));
            if (!doc.RootElement.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array) continue;
            foreach (var t in tools.EnumerateArray())
            {
                var name = t.GetProperty("tool").GetString()!;
                if (!byTool.TryGetValue(name, out var hits)) byTool[name] = hits = [];
                hits.Add(new ToolHit(rd.GetString(0), Str(rd, 1), Str(rd, 2),
                    t.GetProperty("source").GetString() ?? "", t.TryGetProperty("detail", out var d) ? d.GetString() : null));
            }
        }
        return byTool.Select(kv => new ToolSummary(kv.Key, kv.Value.Select(h => h.AgentId).Distinct().Count(), kv.Value))
                     .OrderByDescending(t => t.Machines).ThenBy(t => t.Tool).ToList();
    }

    /// <summary>CPU, RAM e disco do sistema em %, a partir do relatório. Null se não veio nada utilizável.</summary>
    private static (double? Cpu, double? Ram, double? Disk)? MetricSample(InventoryReport r)
    {
        var total = r.Hardware.RamTotalBytes;
        double? ram = total is > 0 && r.Health.RamFreeBytes is { } free ? Math.Round(100.0 * (total.Value - free) / total.Value, 1) : null;

        var vol = r.Volumes.FirstOrDefault(v => v.Letter.Equals("C:", StringComparison.OrdinalIgnoreCase)) ?? r.Volumes.FirstOrDefault();
        double? disk = vol is { SizeBytes: > 0, FreeBytes: { } f } ? Math.Round(100.0 * (vol.SizeBytes!.Value - f) / vol.SizeBytes.Value, 1) : null;

        var cpu = r.Health.CpuLoadPercent;
        return cpu is null && ram is null && disk is null ? null : (cpu, ram, disk);
    }

    /// <summary>Quantos eventos batem com o filtro (usado no sininho de alertas do painel).</summary>
    public int EventsCount(string? severity = null, DateTimeOffset? from = null, long? afterId = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrEmpty(severity)) { where.Add("severity = $sev"); cmd.Parameters.AddWithValue("$sev", severity); }
        if (from is { } f) { where.Add("at >= $from"); cmd.Parameters.AddWithValue("$from", f.ToString("O")); }
        if (afterId is { } a) { where.Add("id > $after"); cmd.Parameters.AddWithValue("$after", a); }
        cmd.CommandText = "SELECT COUNT(*) FROM events" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<Issue> ParseIssues(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<Issue>>(json, AgentJson.Options) ?? [];

    /// <summary>Máquinas gravadas antes do índice de saúde existir ganham o cálculo na primeira subida do servidor.</summary>
    private static void BackfillHealth(SqliteConnection conn) => Backfill(conn, onlyMissing: true);

    private static int Backfill(SqliteConnection conn, bool onlyMissing)
    {
        var pending = new List<(string Id, string Json)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = onlyMissing ? "SELECT agent_id, report_json FROM machines WHERE health_score IS NULL" : "SELECT agent_id, report_json FROM machines";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) pending.Add((rd.GetString(0), rd.GetString(1)));
        }

        foreach (var (id, json) in pending)
        {
            var report = JsonSerializer.Deserialize<InventoryReport>(json, AgentJson.Options)!;
            var (score, issues) = HealthAssessment.Assess(report, DateTimeOffset.UtcNow);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE machines SET health_score = $s, issues_json = $i WHERE agent_id = $id";
            cmd.Parameters.AddWithValue("$s", score);
            cmd.Parameters.AddWithValue("$i", JsonSerializer.Serialize(issues, AgentJson.Options));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        return pending.Count;
    }

    // ---------- Internos ----------

    private sealed record Previous(InventoryReport Report, List<SoftwareInfo>? Software, bool OfflineFlagged, DateTimeOffset LastSeen);

    private static Previous? LoadPrevious(SqliteConnection conn, SqliteTransaction tx, string agentId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT report_json, software_json, offline_flagged, last_seen FROM machines WHERE agent_id = $id";
        cmd.Parameters.AddWithValue("$id", agentId);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;

        var report = JsonSerializer.Deserialize<InventoryReport>(rd.GetString(0), AgentJson.Options)!;
        var software = Str(rd, 1) is { } sw ? JsonSerializer.Deserialize<List<SoftwareInfo>>(sw, AgentJson.Options) : null;
        return new Previous(report, software, rd.GetInt32(2) == 1, DateTimeOffset.Parse(rd.GetString(3)));
    }

    private static (long Id, string User)? LatestAssignment(SqliteConnection conn, SqliteTransaction tx, string agentId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, user_name FROM assignments WHERE agent_id = $id ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$id", agentId);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (rd.GetInt64(0), rd.GetString(1)) : null;
    }

    private List<Assignment> QueryAssignments(string whereClause, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT a.id, a.agent_id, a.hostname, a.user_name, a.first_seen, a.last_seen,
                   a.id = (SELECT MAX(id) FROM assignments x WHERE x.agent_id = a.agent_id)
            FROM assignments a WHERE {whereClause} ORDER BY a.id DESC
            """;
        cmd.Parameters.AddWithValue("$v", value);
        using var rd = cmd.ExecuteReader();
        var list = new List<Assignment>();
        while (rd.Read())
            list.Add(new Assignment(rd.GetInt64(0), rd.GetString(1), Str(rd, 2), rd.GetString(3), DateTimeOffset.Parse(rd.GetString(4)), DateTimeOffset.Parse(rd.GetString(5)), rd.GetInt32(6) == 1));
        return list;
    }

    private static void InsertEvent(SqliteConnection conn, SqliteTransaction tx, string at, string agentId, string? host,
        string type, string severity, string? field, string? oldValue, string? newValue) =>
        Run(conn, tx, """
            INSERT INTO events (at, agent_id, hostname, type, severity, field, old_value, new_value, user_name)
            VALUES ($at, $a, $h, $t, $s, $f, $o, $n, $u)
            """,
            ("$at", at), ("$a", agentId), ("$h", host), ("$t", type), ("$s", severity), ("$f", field), ("$o", oldValue), ("$n", newValue),
            // Quem estava no PC quando aconteceu. Eventos antigos (de antes desta coluna) ficam sem usuário: o log não permite UPDATE.
            ("$u", LatestAssignment(conn, tx, agentId)?.User));

    private static void Run(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Migração simples para bancos criados por versões anteriores.</summary>
    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (cmd.ExecuteScalar() is null) Exec(conn, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    private static string? Str(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
}
