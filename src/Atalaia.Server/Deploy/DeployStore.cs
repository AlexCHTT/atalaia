using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Atalaia.Server;

public sealed record DeployTarget(string Ip, string? Name, string Status, string? Message, DateTimeOffset UpdatedAt, string? FoundHostname);
public sealed record DeployJob(string Id, DateTimeOffset CreatedAt, string? CreatedBy, string TokenId, string TokenLabel, string PackageSha, string ServerUrl, List<DeployTarget> Targets);

/// <summary>
/// Trabalhos de instalação em massa. Cada alvo passa por: pending (script ainda não tentou) → sent (script mandou instalar)
/// → installed (o agente se registrou no servidor: é a prova de que funcionou) | failed (o script não conseguiu) | timeout (mandou, mas o agente nunca apareceu).
/// </summary>
public sealed class DeployStore
{
    public static readonly TimeSpan SentTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan JobLifetime = TimeSpan.FromHours(24);   // o script gerado só consegue reportar neste prazo

    private readonly string _connectionString;
    private readonly object _lock = new();

    public DeployStore(IConfiguration config)
    {
        var path = DbPath.Resolve(config);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var conn = Open();
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS deploy_jobs (
                id TEXT PRIMARY KEY, created_at TEXT NOT NULL, created_by TEXT, token_id TEXT NOT NULL DEFAULT '', token_label TEXT NOT NULL,
                package_sha TEXT NOT NULL, server_url TEXT NOT NULL, secret TEXT NOT NULL
            )
            """);
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS deploy_targets (
                job_id TEXT NOT NULL, ip TEXT NOT NULL, name TEXT, status TEXT NOT NULL, message TEXT,
                updated_at TEXT NOT NULL, sent_at TEXT, found_hostname TEXT,
                PRIMARY KEY (job_id, ip)
            )
            """);
        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_deploy_targets_status ON deploy_targets (status)");
    }

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Cria o trabalho. Devolve o segredo (que vai dentro do script) junto, pois ele não é exibido depois.</summary>
    public (DeployJob Job, string Secret) Create(string? by, string tokenId, string tokenLabel, string packageSha, string serverUrl, IEnumerable<(string Ip, string? Name)> targets)
    {
        var id = Guid.NewGuid().ToString("N");
        var secret = NewSecret();
        var now = Now();
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            Run(conn, tx, "INSERT INTO deploy_jobs (id, created_at, created_by, token_id, token_label, package_sha, server_url, secret) VALUES ($i, $n, $by, $tk, $tl, $sha, $url, $s)",
                ("$i", id), ("$n", now), ("$by", by), ("$tk", tokenId), ("$tl", tokenLabel), ("$sha", packageSha), ("$url", serverUrl), ("$s", secret));
            foreach (var (ip, name) in targets)
                Run(conn, tx, "INSERT OR IGNORE INTO deploy_targets (job_id, ip, name, status, updated_at) VALUES ($j, $ip, $nm, 'pending', $n)",
                    ("$j", id), ("$ip", ip), ("$nm", name), ("$n", now));
            tx.Commit();
        }
        return (Get(id)!, secret);
    }

    public DeployJob? Get(string id)
    {
        lock (_lock)
        {
            using var conn = Open();
            ExpireStale(conn);
            return Read(conn, "id = $i", ("$i", id)).FirstOrDefault();
        }
    }

    public List<DeployJob> Recent(int limit)
    {
        lock (_lock)
        {
            using var conn = Open();
            ExpireStale(conn);
            return Read(conn, "1 = 1", null, limit);
        }
    }

    /// <summary>Segredo do trabalho (para montar o script). Null se o trabalho não existe.</summary>
    public string? SecretOf(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT secret FROM deploy_jobs WHERE id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        return cmd.ExecuteScalar() as string;
    }

    public enum ProgressResult { Ok, Unauthorized, NotFound, Expired }

    /// <summary>O script avisa o que fez com cada PC. Só vale com o segredo do trabalho, dentro do prazo, e nunca "desfaz" um alvo já instalado.</summary>
    public ProgressResult Report(string jobId, string? secret, string ip, string step, string? message)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT secret, created_at FROM deploy_jobs WHERE id = $i";
            cmd.Parameters.AddWithValue("$i", jobId);
            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return ProgressResult.NotFound;
            var stored = rd.GetString(0); var created = DateTimeOffset.Parse(rd.GetString(1));
            rd.Close();

            if (string.IsNullOrEmpty(secret) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), SHA256.HashData(Encoding.UTF8.GetBytes(stored))))
                return ProgressResult.Unauthorized;
            if (DateTimeOffset.UtcNow - created > JobLifetime) return ProgressResult.Expired;

            var status = step == "sent" ? "sent" : "failed";
            var now = Now();
            using var up = conn.CreateCommand();
            up.CommandText = """
                UPDATE deploy_targets SET status = $s, message = $m, updated_at = $n, sent_at = CASE WHEN $s = 'sent' THEN $n ELSE sent_at END
                WHERE job_id = $j AND ip = $ip AND status <> 'installed'
                """;
            up.Parameters.AddWithValue("$s", status);
            up.Parameters.AddWithValue("$m", (object?)Truncate(message, 300) ?? DBNull.Value);
            up.Parameters.AddWithValue("$n", now);
            up.Parameters.AddWithValue("$j", jobId);
            up.Parameters.AddWithValue("$ip", ip);
            return up.ExecuteNonQuery() > 0 ? ProgressResult.Ok : ProgressResult.NotFound;
        }
    }

    /// <summary>Um agente acabou de se registrar: se algum alvo "enviado" tem um dos IPs dele, a instalação funcionou.</summary>
    public int OnCheckin(IReadOnlyCollection<string> ips, string? hostname)
    {
        if (ips.Count == 0) return 0;
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var names = ips.Select((_, i) => $"$p{i}").ToList();
            cmd.CommandText = $"UPDATE deploy_targets SET status = 'installed', message = NULL, found_hostname = $h, updated_at = $n WHERE status = 'sent' AND ip IN ({string.Join(",", names)})";
            cmd.Parameters.AddWithValue("$h", (object?)hostname ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", Now());
            var i = 0;
            foreach (var ip in ips) cmd.Parameters.AddWithValue(names[i++], ip);
            return cmd.ExecuteNonQuery();
        }
    }

    // ---------- Internos ----------

    private static void ExpireStale(SqliteConnection conn)
    {
        var now = DateTimeOffset.UtcNow;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE deploy_targets SET status = 'timeout', updated_at = $now,
                   message = 'A instalação foi enviada, mas o agente não se registrou em 15 minutos. Veja o computador (firewall, antivírus, instalador).'
            WHERE status = 'sent' AND sent_at < $sentCut;
            UPDATE deploy_targets SET status = 'timeout', updated_at = $now, message = 'O script não chegou a tentar este computador.'
            WHERE status = 'pending' AND job_id IN (SELECT id FROM deploy_jobs WHERE created_at < $jobCut);
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$sentCut", (now - SentTimeout).ToString("O"));
        cmd.Parameters.AddWithValue("$jobCut", (now - JobLifetime).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static List<DeployJob> Read(SqliteConnection conn, string where, (string, object)? p, int limit = 1)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, created_at, created_by, token_id, token_label, package_sha, server_url FROM deploy_jobs WHERE {where} ORDER BY created_at DESC LIMIT {limit}";
        if (p is { } pp) cmd.Parameters.AddWithValue(pp.Item1, pp.Item2);
        var jobs = new List<(string, DateTimeOffset, string?, string, string, string, string)>();
        using (var rd = cmd.ExecuteReader())
            while (rd.Read()) jobs.Add((rd.GetString(0), DateTimeOffset.Parse(rd.GetString(1)), rd.IsDBNull(2) ? null : rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6)));

        var result = new List<DeployJob>();
        foreach (var (id, created, by, tokenId, label, sha, url) in jobs)
        {
            using var t = conn.CreateCommand();
            t.CommandText = "SELECT ip, name, status, message, updated_at, found_hostname FROM deploy_targets WHERE job_id = $j";
            t.Parameters.AddWithValue("$j", id);
            var targets = new List<DeployTarget>();
            using var rd = t.ExecuteReader();
            while (rd.Read())
                targets.Add(new DeployTarget(rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3), DateTimeOffset.Parse(rd.GetString(4)), rd.IsDBNull(5) ? null : rd.GetString(5)));
            targets.Sort((a, b) => IpRange.TryParseIp(a.Ip, out var x) && IpRange.TryParseIp(b.Ip, out var y) ? x.CompareTo(y) : 0);
            result.Add(new DeployJob(id, created, by, tokenId, label, sha, url, targets));
        }
        return result;
    }

    private static void Run(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
    private static void Exec(SqliteConnection conn, string sql) { using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
}
