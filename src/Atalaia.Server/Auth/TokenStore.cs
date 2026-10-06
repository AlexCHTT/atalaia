using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Atalaia.Server;

public sealed record EnrollToken(string Id, string Label, string Token, DateTimeOffset CreatedAt, string? CreatedBy,
    DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt, long UseCount);

/// <summary>
/// Tokens que os agentes usam para se registrar (cabeçalho X-Enroll-Token), mais algumas configurações do painel.
/// Pode haver vários tokens ativos ao mesmo tempo: dá para criar um novo, instalar com ele e só então revogar o antigo,
/// sem derrubar nenhum agente no meio. Um token revogado deixa de valer na hora.
/// </summary>
public sealed class TokenStore
{
    private readonly string _connectionString;
    private readonly object _lock = new();
    private (DateTimeOffset At, List<(byte[] Hash, string Id)> Active)? _cache;

    public TokenStore(IConfiguration config)
    {
        var path = DbPath.Resolve(config);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();

        using var conn = Open();
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS enroll_tokens (
                id           TEXT PRIMARY KEY,
                label        TEXT NOT NULL,
                token        TEXT NOT NULL UNIQUE,
                created_at   TEXT NOT NULL,
                created_by   TEXT,
                revoked_at   TEXT,
                last_used_at TEXT,
                use_count    INTEGER NOT NULL DEFAULT 0
            )
            """);
        Exec(conn, "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL)");
    }

    // ---------- Tokens ----------

    public static string Generate() => "atl_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Token vindo da configuração (Server:EnrollToken) entra como um token normal, uma vez. Se já existir (até revogado), não é recriado.</summary>
    public void ImportConfigToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        lock (_lock)
        {
            using var conn = Open();
            using var exists = conn.CreateCommand();
            exists.CommandText = "SELECT 1 FROM enroll_tokens WHERE token = $t";
            exists.Parameters.AddWithValue("$t", token);
            if (exists.ExecuteScalar() is not null) return;
            Insert(conn, "Definido na configuração do servidor", token, null);
            _cache = null;
        }
    }

    /// <summary>Garante que exista ao menos um token ativo (a primeira instalação não precisa configurar nenhum).</summary>
    public void EnsureOneActive()
    {
        if (List().Any(t => t.RevokedAt is null)) return;
        Create("Token padrão", null);
    }

    public EnrollToken Create(string label, string? createdBy)
    {
        lock (_lock)
        {
            using var conn = Open();
            var id = Insert(conn, label, Generate(), createdBy);
            _cache = null;
            return List().First(t => t.Id == id);
        }
    }

    public bool Revoke(string id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE enroll_tokens SET revoked_at = $now WHERE id = $id AND revoked_at IS NULL";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            var changed = cmd.ExecuteNonQuery() > 0;
            _cache = null;   // o token deixa de valer já, sem esperar o cache expirar
            return changed;
        }
    }

    public List<EnrollToken> List()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, label, token, created_at, created_by, revoked_at, last_used_at, use_count FROM enroll_tokens ORDER BY created_at DESC";
        using var rd = cmd.ExecuteReader();
        var list = new List<EnrollToken>();
        while (rd.Read())
            list.Add(new EnrollToken(rd.GetString(0), rd.GetString(1), rd.GetString(2), DateTimeOffset.Parse(rd.GetString(3)), Str(rd, 4),
                rd.IsDBNull(5) ? null : DateTimeOffset.Parse(rd.GetString(5)), rd.IsDBNull(6) ? null : DateTimeOffset.Parse(rd.GetString(6)), rd.GetInt64(7)));
        return list;
    }

    /// <summary>O token é de um agente autorizado? Compara em tempo constante e registra o uso (quando e quantas vezes).</summary>
    public bool Validate(string? token, bool recordUse = true)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return false;
        var active = ActiveTokens();
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        string? matchedId = null;
        foreach (var (hash, id) in active)
            if (CryptographicOperations.FixedTimeEquals(hash, given)) matchedId = id;   // não para no primeiro: tempo não depende de qual casou
        if (matchedId is null) return false;
        if (!recordUse) return true;   // consultas frequentes do agente (busca de tarefas, teste de velocidade) não gravam no banco a cada chamada

        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE enroll_tokens SET last_used_at = $now, use_count = use_count + 1 WHERE id = $id";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", matchedId);
            cmd.ExecuteNonQuery();
        }
        return true;
    }

    private List<(byte[] Hash, string Id)> ActiveTokens()
    {
        if (_cache is { } c && DateTimeOffset.UtcNow - c.At < TimeSpan.FromSeconds(15)) return c.Active;
        lock (_lock)
        {
            var fresh = List().Where(t => t.RevokedAt is null).Select(t => (SHA256.HashData(Encoding.UTF8.GetBytes(t.Token)), t.Id)).ToList();
            _cache = (DateTimeOffset.UtcNow, fresh);
            return fresh;
        }
    }

    // ---------- Configurações ----------

    public string? GetSetting(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO settings (key, value, updated_at) VALUES ($k, $v, $now) ON CONFLICT(key) DO UPDATE SET value = $v, updated_at = $now";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    // ---------- Internos ----------

    private static string Insert(SqliteConnection conn, string label, string token, string? createdBy)
    {
        var id = Guid.NewGuid().ToString("N");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO enroll_tokens (id, label, token, created_at, created_by) VALUES ($id, $l, $t, $now, $by)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$l", label);
        cmd.Parameters.AddWithValue("$t", token);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$by", (object?)createdBy ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return id;
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
    private static void Exec(SqliteConnection conn, string sql) { using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private static string? Str(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
}
