using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Atalaia.Server;

public static class Roles
{
    public const string Viewer = "viewer", Operator = "operator", Admin = "admin";
    public static int Rank(string? role) => role switch { Admin => 3, Operator => 2, Viewer => 1, _ => 0 };
    public static bool IsValid(string? role) => Rank(role) > 0;
}

public sealed record Account(string Id, string Username, string DisplayName, string Role, bool Active, bool MustChangePassword,
    DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset? LastLoginAt, DateTimeOffset? LockedUntil);

/// <summary>Quem está logado nesta requisição (resolvido do cookie de sessão).</summary>
public sealed record SessionUser(string AccountId, string Username, string DisplayName, string Role, bool MustChangePassword, string SessionHash);

public sealed record AccessLogEntry(long Id, DateTimeOffset At, string? Actor, string Action, string? Target, string? Detail, string? Ip);

/// <summary>
/// Contas do painel, sessões e o registro de acessos/ações ("quem fez o quê"). Mesmo arquivo SQLite do restante.
/// O registro (access_log) é append-only, como a auditoria dos dispositivos: gatilhos barram UPDATE e DELETE.
/// </summary>
public sealed class AuthStore
{
    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private readonly ILogger<AuthStore> _logger;

    public AuthStore(IConfiguration config, ILogger<AuthStore> logger)
    {
        _logger = logger;
        var path = DbPath.Resolve(config);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();

        using var conn = Open();
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS accounts (
                id                   TEXT PRIMARY KEY,
                username             TEXT NOT NULL UNIQUE COLLATE NOCASE,
                display_name         TEXT NOT NULL,
                role                 TEXT NOT NULL,
                password_hash        TEXT NOT NULL,
                must_change_password INTEGER NOT NULL DEFAULT 0,
                active               INTEGER NOT NULL DEFAULT 1,
                created_at           TEXT NOT NULL,
                created_by           TEXT,
                last_login_at        TEXT,
                failed_attempts      INTEGER NOT NULL DEFAULT 0,
                locked_until         TEXT
            )
            """);
        // Só o hash do token fica no banco: quem lê o arquivo não consegue se passar por uma sessão aberta
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS sessions (
                token_hash TEXT PRIMARY KEY,
                account_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                last_seen  TEXT NOT NULL,
                ip         TEXT,
                user_agent TEXT
            )
            """);
        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_sessions_account ON sessions(account_id)");
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS access_log (
                id     INTEGER PRIMARY KEY AUTOINCREMENT,
                at     TEXT NOT NULL,
                actor  TEXT,
                action TEXT NOT NULL,
                target TEXT,
                detail TEXT,
                ip     TEXT
            )
            """);
        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_access_log_at ON access_log(id DESC)");
        Exec(conn, "CREATE TRIGGER IF NOT EXISTS access_log_no_update BEFORE UPDATE ON access_log BEGIN SELECT RAISE(ABORT, 'access_log e append-only'); END");
        Exec(conn, "CREATE TRIGGER IF NOT EXISTS access_log_no_delete BEFORE DELETE ON access_log BEGIN SELECT RAISE(ABORT, 'access_log e append-only'); END");
    }

    // ---------- Primeira subida e recuperação ----------

    /// <summary>
    /// Sem nenhuma conta, cria o primeiro administrador com Server:AdminUser/AdminPassword. Depois disso o cadastro é a fonte da verdade.
    /// Se todo mundo esquecer a senha: suba uma vez com Server:ResetAdminPassword=true (mais AdminUser/AdminPassword) e remova a variável.
    /// </summary>
    public void Bootstrap(string adminUser, string? adminPassword, bool reset)
    {
        var hasAccounts = AccountCount() > 0;
        if (hasAccounts && !reset) return;

        // Sem conta e sem senha na configuração: quem cria o administrador é o assistente de primeira execução (no navegador)
        if (string.IsNullOrWhiteSpace(adminPassword) && !hasAccounts) return;
        if (string.IsNullOrWhiteSpace(adminPassword))
            throw new InvalidOperationException("Server:ResetAdminPassword exige Server:AdminPassword (env: Server__AdminPassword).");
        if (PasswordHasher.Validate(adminPassword, adminUser) is { } problem)
            throw new InvalidOperationException($"Server:AdminPassword não serve: {problem}");

        var hash = PasswordHasher.Hash(adminPassword);
        lock (_writeLock)
        {
            using var conn = Open();
            var existing = FindByUsernameRaw(conn, adminUser);
            if (existing is null)
            {
                InsertAccount(conn, adminUser, "Administrador", Roles.Admin, hash, mustChange: false, createdBy: null);
                Log(null, hasAccounts ? "recovery_reset" : "bootstrap", adminUser, hasAccounts ? "conta recriada pela variável ResetAdminPassword" : "primeiro administrador criado a partir da configuração");
                _logger.LogWarning("Conta de administrador '{User}' {What}.", adminUser, hasAccounts ? "recriada (ResetAdminPassword)" : "criada");
            }
            else
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE accounts SET password_hash = $h, role = 'admin', active = 1, must_change_password = 0, failed_attempts = 0, locked_until = NULL WHERE id = $id";
                cmd.Parameters.AddWithValue("$h", hash);
                cmd.Parameters.AddWithValue("$id", existing.Value.Account.Id);
                cmd.ExecuteNonQuery();
                RevokeAccountSessionsLocked(conn, existing.Value.Account.Id, null);
                Log(null, "recovery_reset", adminUser, "senha redefinida pela variável ResetAdminPassword");
                _logger.LogWarning("Senha do administrador '{User}' REDEFINIDA por Server:ResetAdminPassword. Remova essa variável e reinicie.", adminUser);
            }
        }
    }

    // ---------- Contas ----------

    public int AccountCount()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM accounts";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountActiveAdmins(string? excludingId = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM accounts WHERE role = 'admin' AND active = 1 AND ($ex IS NULL OR id <> $ex)";
        cmd.Parameters.AddWithValue("$ex", (object?)excludingId ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<Account> List()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = AccountSelect + " ORDER BY display_name COLLATE NOCASE";
        using var rd = cmd.ExecuteReader();
        var list = new List<Account>();
        while (rd.Read()) list.Add(ReadAccount(rd));
        return list;
    }

    public Account? GetById(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = AccountSelect + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? ReadAccount(rd) : null;
    }

    /// <summary>Conta + hash da senha (só para o login).</summary>
    public (Account Account, string PasswordHash)? FindForLogin(string username)
    {
        using var conn = Open();
        return FindByUsernameRaw(conn, username);
    }

    public bool UsernameExists(string username)
    {
        using var conn = Open();
        return FindByUsernameRaw(conn, username) is not null;
    }

    /// <summary>Cria o administrador do assistente de primeira execução. Devolve null se já existe alguma conta (conferido e criado sob o mesmo lock).</summary>
    public Account? CreateFirstAdmin(string username, string displayName, string passwordHash)
    {
        lock (_writeLock)
        {
            if (AccountCount() > 0) return null;
            using var conn = Open();
            var id = InsertAccount(conn, username, displayName, Roles.Admin, passwordHash, mustChange: false, createdBy: null);
            return GetById(id)!;
        }
    }

    public Account Create(string username, string displayName, string role, string passwordHash, bool mustChange, string? createdBy)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            var id = InsertAccount(conn, username, displayName, role, passwordHash, mustChange, createdBy);
            return GetById(id)!;
        }
    }

    public bool Update(string id, string displayName, string role, bool active)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            string? oldRole;
            using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT role FROM accounts WHERE id = $id";
                read.Parameters.AddWithValue("$id", id);
                oldRole = read.ExecuteScalar() as string;
            }
            if (oldRole is null) return false;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE accounts SET display_name = $n, role = $r, active = $a WHERE id = $id";
            cmd.Parameters.AddWithValue("$n", displayName);
            cmd.Parameters.AddWithValue("$r", role);
            cmd.Parameters.AddWithValue("$a", active ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            // Perfil mudou ou conta desativada: as sessões abertas deixam de valer na hora (só mudar o nome não derruba ninguém)
            if (oldRole != role || !active) RevokeAccountSessionsLocked(conn, id, null);
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            RevokeAccountSessionsLocked(conn, id, null);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM accounts WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Troca a senha, destrava a conta e encerra as outras sessões (exceto a atual, se informada).</summary>
    public void SetPassword(string id, string hash, bool mustChange, string? keepSessionHash = null)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE accounts SET password_hash = $h, must_change_password = $m, failed_attempts = 0, locked_until = NULL WHERE id = $id";
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$m", mustChange ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            RevokeAccountSessionsLocked(conn, id, keepSessionHash);
        }
    }

    public void MarkLogin(string id)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE accounts SET last_login_at = $now, failed_attempts = 0, locked_until = NULL WHERE id = $id";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Soma uma tentativa errada; ao atingir o limite, trava a conta por um tempo. Devolve até quando está travada (ou null).</summary>
    public DateTimeOffset? RegisterFailure(string id, int maxAttempts, TimeSpan lockFor)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            int attempts;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE accounts SET failed_attempts = failed_attempts + 1 WHERE id = $id RETURNING failed_attempts";
                cmd.Parameters.AddWithValue("$id", id);
                attempts = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            if (attempts < maxAttempts) return null;

            var until = DateTimeOffset.UtcNow + lockFor;
            using var lockCmd = conn.CreateCommand();
            lockCmd.CommandText = "UPDATE accounts SET failed_attempts = 0, locked_until = $u WHERE id = $id";
            lockCmd.Parameters.AddWithValue("$u", until.ToString("O"));
            lockCmd.Parameters.AddWithValue("$id", id);
            lockCmd.ExecuteNonQuery();
            return until;
        }
    }

    // ---------- Sessões ----------

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Abre uma sessão e devolve o token (vai só para o cookie; no banco fica o hash).</summary>
    public string CreateSession(string accountId, string? ip, string? userAgent, TimeSpan lifetime)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var now = DateTimeOffset.UtcNow;
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions (token_hash, account_id, created_at, expires_at, last_seen, ip, user_agent) VALUES ($h, $a, $now, $exp, $now, $ip, $ua)";
            cmd.Parameters.AddWithValue("$h", Hash(token));
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$now", now.ToString("O"));
            cmd.Parameters.AddWithValue("$exp", (now + lifetime).ToString("O"));
            cmd.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ua", (object?)(userAgent is { Length: > 200 } ? userAgent[..200] : userAgent) ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        return token;
    }

    /// <summary>Quem é o dono do token, se a sessão ainda vale (prazo total, ociosidade e conta ativa).</summary>
    public SessionUser? Resolve(string token, TimeSpan idleTimeout)
    {
        var hash = Hash(token);
        var now = DateTimeOffset.UtcNow;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.expires_at, s.last_seen, a.id, a.username, a.display_name, a.role, a.must_change_password
            FROM sessions s JOIN accounts a ON a.id = s.account_id
            WHERE s.token_hash = $h AND a.active = 1
            """;
        cmd.Parameters.AddWithValue("$h", hash);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;

        var expires = DateTimeOffset.Parse(rd.GetString(0));
        var lastSeen = DateTimeOffset.Parse(rd.GetString(1));
        if (expires <= now || now - lastSeen > idleTimeout) return null;
        var user = new SessionUser(rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetInt32(6) == 1, hash);
        rd.Close();

        // Atualiza "visto por último" no máximo a cada minuto (evita uma escrita por requisição)
        if (now - lastSeen > TimeSpan.FromMinutes(1))
        {
            lock (_writeLock)
            {
                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE sessions SET last_seen = $now WHERE token_hash = $h";
                upd.Parameters.AddWithValue("$now", now.ToString("O"));
                upd.Parameters.AddWithValue("$h", hash);
                upd.ExecuteNonQuery();
            }
        }
        return user;
    }

    public void RevokeSession(string token)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE token_hash = $h";
            cmd.Parameters.AddWithValue("$h", Hash(token));
            cmd.ExecuteNonQuery();
        }
    }

    public void PurgeSessions(TimeSpan idleTimeout)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE expires_at <= $now OR last_seen <= $idle";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$idle", (DateTimeOffset.UtcNow - idleTimeout).ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    // ---------- Registro de acessos e ações ----------

    public void Log(string? actor, string action, string? target = null, string? detail = null, string? ip = null)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO access_log (at, actor, action, target, detail, ip) VALUES ($at, $actor, $action, $target, $detail, $ip)";
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$actor", (object?)actor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$target", (object?)target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public int CountAccessLogOlderThan(DateTimeOffset limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM access_log WHERE at < $l AND action <> 'retention_purge'";
        cmd.Parameters.AddWithValue("$l", limit.ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Retenção do registro de acessos. O banco barra DELETE nesta tabela; este é o único caminho que o libera, dentro de uma transação
    /// (se algo falhar, a proteção volta). Cada limpeza deixa uma linha "retention_purge", que nunca é apagada.
    /// </summary>
    public int PurgeAccessLog(DateTimeOffset olderThan)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            void Run(string sql, params (string, object)[] ps)
            {
                using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = sql;
                foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v);
                c.ExecuteNonQuery();
            }
            Run("DROP TRIGGER IF EXISTS access_log_no_delete");
            int removed;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM access_log WHERE at < $l AND action <> 'retention_purge'";
                cmd.Parameters.AddWithValue("$l", olderThan.ToString("O"));
                removed = cmd.ExecuteNonQuery();
            }
            Run("CREATE TRIGGER access_log_no_delete BEFORE DELETE ON access_log BEGIN SELECT RAISE(ABORT, 'access_log e append-only'); END");
            if (removed > 0)
                Run("INSERT INTO access_log (at, actor, action, target, detail) VALUES ($at, NULL, 'retention_purge', 'registro de acessos', $d)",
                    ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$d", $"{removed} registro(s) anteriores a {olderThan.ToLocalTime():dd/MM/yyyy} removidos pela política de retenção"));
            tx.Commit();
            return removed;
        }
    }

    public List<AccessLogEntry> QueryLog(string? actor, string? action, int limit, long? beforeId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrEmpty(actor)) { where.Add("actor = $actor COLLATE NOCASE"); cmd.Parameters.AddWithValue("$actor", actor); }
        if (!string.IsNullOrEmpty(action)) { where.Add("action = $action"); cmd.Parameters.AddWithValue("$action", action); }
        if (beforeId is { } b) { where.Add("id < $before"); cmd.Parameters.AddWithValue("$before", b); }
        cmd.CommandText = $"SELECT id, at, actor, action, target, detail, ip FROM access_log {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var rd = cmd.ExecuteReader();
        var list = new List<AccessLogEntry>();
        while (rd.Read())
            list.Add(new AccessLogEntry(rd.GetInt64(0), DateTimeOffset.Parse(rd.GetString(1)), Str(rd, 2), rd.GetString(3), Str(rd, 4), Str(rd, 5), Str(rd, 6)));
        return list;
    }

    // ---------- Internos ----------

    private const string AccountSelect = "SELECT id, username, display_name, role, active, must_change_password, created_at, created_by, last_login_at, locked_until FROM accounts";

    private static Account ReadAccount(SqliteDataReader rd) => new(
        rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetInt32(4) == 1, rd.GetInt32(5) == 1,
        DateTimeOffset.Parse(rd.GetString(6)), Str(rd, 7), rd.IsDBNull(8) ? null : DateTimeOffset.Parse(rd.GetString(8)),
        rd.IsDBNull(9) ? null : DateTimeOffset.Parse(rd.GetString(9)) is var u && u > DateTimeOffset.UtcNow ? u : null);

    private static (Account Account, string PasswordHash)? FindByUsernameRaw(SqliteConnection conn, string username)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = AccountSelect.Replace("FROM accounts", ", password_hash FROM accounts") + " WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (ReadAccount(rd), rd.GetString(10)) : null;
    }

    private static string InsertAccount(SqliteConnection conn, string username, string displayName, string role, string hash, bool mustChange, string? createdBy)
    {
        var id = Guid.NewGuid().ToString("N");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO accounts (id, username, display_name, role, password_hash, must_change_password, active, created_at, created_by) VALUES ($id, $u, $n, $r, $h, $m, 1, $now, $by)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$n", displayName);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$m", mustChange ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$by", (object?)createdBy ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return id;
    }

    private static void RevokeAccountSessionsLocked(SqliteConnection conn, string accountId, string? keepHash)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE account_id = $a AND ($keep IS NULL OR token_hash <> $keep)";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$keep", (object?)keepHash ?? DBNull.Value);
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

    private static string? Str(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
}
