using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Atalaia.Server;

public enum SettingKind { Int, Bool, Text }

/// <param name="ConfigKey">Variável/arquivo de configuração que já existia antes desta tela: continua valendo como valor inicial.</param>
/// <param name="Retention">Reduzir o valor pode apagar dados: exige confirmação e mostra o impacto.</param>
public sealed record SettingDef(string Key, string Group, string Label, string Help, SettingKind Kind, string Default, long Min, long Max,
    string? Unit = null, string? ConfigKey = null, bool Retention = false);

public sealed record SettingItem(SettingDef Def, string Value, string Source);   // Source: default | config | db
public sealed record SettingChange(string Key, string Label, string? Old, string? New);

/// <summary>Todos os parâmetros ajustáveis na tela Configurações. Nenhum exige reiniciar o servidor.</summary>
public static class SettingsCatalog
{
    public static readonly (string Id, string Label, string Help)[] Groups =
    [
        ("data", "Dados e retenção", "Por quanto tempo guardar cada tipo de informação e quando avisar sobre o tamanho do banco."),
        ("agents", "Agentes", "Quando um computador é considerado offline e a frequência com que os agentes enviam dados."),
        ("health", "Índice de saúde", "Limites usados no cálculo do índice de 0 a 100 de cada dispositivo."),
        ("security", "Acesso e segurança", "Sessões, bloqueio por tentativas e regra de senha dos usuários do painel."),
        ("network", "Rede e testes", "Teste de velocidade e varredura de rede."),
        ("brand", "Aparência", "Identificação da sua empresa no painel."),
    ];

    public static readonly SettingDef[] All =
    [
        // ---- Dados e retenção ----
        new("retention.events_days", "data", "Auditoria dos dispositivos", "Eventos mais antigos que isto são apagados. 0 guarda para sempre. O prazo é decisão da empresa (política interna/jurídico), não do software.",
            SettingKind.Int, "0", 0, 36500, "dias", "Server:EventsRetentionDays", Retention: true),
        new("retention.access_log_days", "data", "Registro de acessos", "Quem entrou, saiu e mexeu no painel. 0 guarda para sempre.",
            SettingKind.Int, "0", 0, 36500, "dias", null, Retention: true),
        new("retention.metrics_days", "data", "Métricas de desempenho", "Amostras de CPU, memória e disco usadas nos gráficos (uma por check-in).",
            SettingKind.Int, "30", 1, 3650, "dias", "Server:MetricsRetentionDays", Retention: true),
        new("retention.speedtests_keep", "data", "Testes de velocidade por computador", "Quantos testes ficam no histórico de cada máquina; os mais antigos saem.",
            SettingKind.Int, "20", 1, 200, "testes", null, Retention: true),
        new("storage.alert_mb", "data", "Avisar quando o banco passar de", "Mostra um alerta nesta tela quando o banco ultrapassar o tamanho. 0 desliga o aviso.",
            SettingKind.Int, "0", 0, 10_000_000, "MB"),

        // ---- Agentes ----
        new("agents.offline_after_minutes", "agents", "Considerar offline após", "Tempo sem check-in para o computador aparecer como offline (e gerar o alerta).",
            SettingKind.Int, "20", 2, 1440, "minutos", "Server:OfflineAfterMinutes"),
        new("agents.checkin_minutes", "agents", "Intervalo de check-in", "Com que frequência cada agente envia o estado completo. Só é enviado aos agentes quando você altera aqui; mudanças chegam em até 30 segundos.",
            SettingKind.Int, "5", 1, 60, "minutos"),
        new("agents.software_every_hours", "agents", "Atualizar lista de software a cada", "A lista de programas instalados é grande e muda pouco. Também só vale para os agentes quando alterada aqui.",
            SettingKind.Int, "6", 1, 168, "horas"),

        // ---- Índice de saúde ----
        new("health.disk_warn_pct", "health", "Disco do sistema: atenção a partir de", "Ocupação do disco que gera o problema de atenção.", SettingKind.Int, "80", 50, 99, "%"),
        new("health.disk_crit_pct", "health", "Disco do sistema: crítico a partir de", "Ocupação do disco que gera o problema crítico. Precisa ser maior que o de atenção.", SettingKind.Int, "90", 51, 100, "%"),
        new("health.signature_max_days", "health", "Assinaturas do antivírus: tolerar até", "Mais velho que isso vira problema de atenção.", SettingKind.Int, "7", 1, 60, "dias"),
        new("health.uptime_max_days", "health", "Sem reiniciar: tolerar até", "Mais tempo ligado sem reiniciar vira aviso informativo.", SettingKind.Int, "30", 7, 365, "dias"),

        // ---- Acesso e segurança ----
        new("security.session_hours", "security", "Duração máxima da sessão", "Depois disso a pessoa precisa entrar de novo. Vale para novos logins.", SettingKind.Int, "12", 1, 168, "horas", "Server:SessionHours"),
        new("security.idle_minutes", "security", "Encerrar sessão ociosa após", "Sem usar o painel por este tempo, a sessão cai.", SettingKind.Int, "240", 5, 1440, "minutos", "Server:SessionIdleMinutes"),
        new("security.max_failed_logins", "security", "Tentativas de login antes de travar a conta", "Senhas erradas seguidas até a conta ser travada temporariamente.", SettingKind.Int, "5", 3, 20, "tentativas", "Server:MaxFailedLogins"),
        new("security.lock_minutes", "security", "Tempo de bloqueio da conta", "Quanto tempo a conta fica travada depois de errar a senha demais.", SettingKind.Int, "10", 1, 1440, "minutos", "Server:LockMinutes"),
        new("security.min_password_length", "security", "Tamanho mínimo da senha", "Vale para senhas novas e trocas. Senhas já existentes não são afetadas.", SettingKind.Int, "10", 8, 64, "caracteres"),

        // ---- Rede e testes ----
        new("speedtest.internet_enabled", "network", "Permitir teste de velocidade até a internet", "Usa os servidores públicos da Cloudflare. Desligado, só o teste até o servidor fica disponível.", SettingKind.Bool, "true", 0, 0, null, "Server:SpeedTest:InternetEnabled"),
        new("speedtest.max_mb", "network", "Limite de dados por sentido no teste de velocidade", "Cada teste para neste volume (ou em ~6 s, o que vier primeiro). Mais dados medem melhor, mas gastam mais banda. Mudanças chegam aos agentes em até 30 segundos.", SettingKind.Int, "150", 10, 1000, "MB"),
        new("deploy.max_scan_hosts", "network", "Tamanho máximo da varredura de rede", "Máximo de endereços de IP por varredura na tela Instalação.", SettingKind.Int, "1024", 16, 4096, "endereços"),

        // ---- Aparência ----
        new("brand.company_name", "brand", "Nome da empresa", "Aparece na tela de login e no topo do painel. Vazio usa só o nome do produto.", SettingKind.Text, "", 0, 60),
    ];

    public static readonly Dictionary<string, SettingDef> ByKey = All.ToDictionary(d => d.Key);
}

/// <summary>
/// Valor efetivo de cada parâmetro: o que o administrador definiu na tela (banco) → o valor antigo vindo da configuração do servidor
/// (variável de ambiente / appsettings, para instalações existentes continuarem iguais) → o padrão. Lido na hora do uso: mudar vale sem reiniciar.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _connectionString;
    private readonly IConfiguration _config;
    private readonly ConcurrentDictionary<string, string> _db = new();
    private readonly object _lock = new();

    /// <summary>Disparado depois de qualquer alteração (e uma vez na subida), para quem guarda estado derivado.</summary>
    public event Action? Changed;

    public SettingsStore(IConfiguration config)
    {
        _config = config;
        var path = DbPath.Resolve(config);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL)";
            cmd.ExecuteNonQuery();
        }
        Reload(conn);
    }

    private void Reload(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings";
        using var rd = cmd.ExecuteReader();
        var seen = new HashSet<string>();
        while (rd.Read())
        {
            var key = rd.GetString(0);
            if (!SettingsCatalog.ByKey.ContainsKey(key)) continue;   // chaves de outros usos da tabela (ex.: configurações internas)
            _db[key] = rd.GetString(1); seen.Add(key);
        }
        foreach (var k in _db.Keys.Where(k => !seen.Contains(k)).ToList()) _db.TryRemove(k, out _);
    }

    // ---------- Leitura ----------

    public (string Value, string Source) Resolve(string key)
    {
        var def = SettingsCatalog.ByKey[key];
        if (_db.TryGetValue(key, out var stored) && TryNormalize(def, stored, out var v, out _)) return (v, "db");
        if (def.ConfigKey is not null && _config[def.ConfigKey] is { Length: > 0 } cfg && TryNormalize(def, cfg, out var cv, out _)) return (cv, "config");
        return (def.Default, "default");
    }

    public int Int(string key) => int.Parse(Resolve(key).Value, CultureInfo.InvariantCulture);
    public bool Bool(string key) => Resolve(key).Value == "true";
    public string Text(string key) => Resolve(key).Value;

    /// <summary>Valor só se o administrador o definiu na tela (usado para empurrar configuração aos agentes sem mudar o que já está local).</summary>
    public int? Explicit(string key) => Resolve(key) is { Source: "db" } r ? int.Parse(r.Value, CultureInfo.InvariantCulture) : null;

    public List<SettingItem> Items() => SettingsCatalog.All.Select(d => { var (v, s) = Resolve(d.Key); return new SettingItem(d, v, s); }).ToList();

    // ---------- Validação e gravação ----------

    private static bool TryNormalize(SettingDef def, string raw, out string value, out string? error)
    {
        value = ""; error = null;
        raw = raw.Trim();
        switch (def.Kind)
        {
            case SettingKind.Int:
                if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) { error = "Informe um número inteiro."; return false; }
                if (n < def.Min || n > def.Max) { error = $"Use um valor entre {def.Min} e {def.Max}."; return false; }
                value = n.ToString(CultureInfo.InvariantCulture); return true;
            case SettingKind.Bool:
                if (raw is "true" or "false") { value = raw; return true; }
                error = "Valor inválido."; return false;
            default:
                if (raw.Length > def.Max) { error = $"No máximo {def.Max} caracteres."; return false; }
                if (raw.Any(char.IsControl)) { error = "Caracteres inválidos."; return false; }
                value = raw; return true;
        }
    }

    /// <summary>Como os valores ficariam depois de aplicar as alterações (ignora as inválidas). Serve para mostrar o impacto antes de gravar.</summary>
    public Dictionary<string, string> Effective(IReadOnlyDictionary<string, string?> changes)
    {
        var effective = SettingsCatalog.All.ToDictionary(d => d.Key, d => Resolve(d.Key).Value);
        foreach (var (key, raw) in changes)
        {
            if (!SettingsCatalog.ByKey.TryGetValue(key, out var def)) continue;
            if (string.IsNullOrWhiteSpace(raw) && def.Kind != SettingKind.Text) effective[key] = DefaultFor(def);
            else if (TryNormalize(def, raw ?? "", out var v, out _)) effective[key] = v;
        }
        return effective;
    }

    /// <summary>Valida tudo antes de gravar qualquer coisa. Valor nulo ou vazio = voltar ao padrão.</summary>
    public Dictionary<string, string> Validate(IReadOnlyDictionary<string, string?> changes)
    {
        var errors = new Dictionary<string, string>();
        var effective = SettingsCatalog.All.ToDictionary(d => d.Key, d => Resolve(d.Key).Value);

        foreach (var (key, raw) in changes)
        {
            if (!SettingsCatalog.ByKey.TryGetValue(key, out var def)) { errors[key] = "Parâmetro desconhecido."; continue; }
            if (string.IsNullOrWhiteSpace(raw) && def.Kind != SettingKind.Text) { effective[key] = DefaultFor(def); continue; }   // restaurar padrão
            if (!TryNormalize(def, raw ?? "", out var v, out var err)) { errors[key] = err!; continue; }
            effective[key] = v;
        }

        if (errors.Count == 0 && int.Parse(effective["health.disk_warn_pct"]) >= int.Parse(effective["health.disk_crit_pct"]))
            errors["health.disk_crit_pct"] = "Precisa ser maior que o limite de atenção.";
        return errors;
    }

    private string DefaultFor(SettingDef def) =>
        def.ConfigKey is not null && _config[def.ConfigKey] is { Length: > 0 } cfg && TryNormalize(def, cfg, out var v, out _) ? v : def.Default;

    /// <summary>Grava (já validado). Devolve só o que de fato mudou.</summary>
    public List<SettingChange> Apply(IReadOnlyDictionary<string, string?> changes)
    {
        var done = new List<SettingChange>();
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var (key, raw) in changes)
            {
                var def = SettingsCatalog.ByKey[key];
                var before = Resolve(key).Value;
                var reset = string.IsNullOrWhiteSpace(raw) && def.Kind != SettingKind.Text;
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                if (reset)
                {
                    cmd.CommandText = "DELETE FROM settings WHERE key = $k";
                    cmd.Parameters.AddWithValue("$k", key);
                    cmd.ExecuteNonQuery();
                    _db.TryRemove(key, out _);
                }
                else
                {
                    TryNormalize(def, raw ?? "", out var value, out _);
                    cmd.CommandText = "INSERT INTO settings (key, value, updated_at) VALUES ($k, $v, $n) ON CONFLICT(key) DO UPDATE SET value = $v, updated_at = $n";
                    cmd.Parameters.AddWithValue("$k", key);
                    cmd.Parameters.AddWithValue("$v", value);
                    cmd.Parameters.AddWithValue("$n", DateTimeOffset.UtcNow.ToString("O"));
                    cmd.ExecuteNonQuery();
                    _db[key] = value;
                }
                var after = Resolve(key).Value;
                if (before != after) done.Add(new SettingChange(key, def.Label, before, after));
            }
            tx.Commit();
        }
        if (done.Count > 0) Changed?.Invoke();
        return done;
    }

    public void NotifyStartup() => Changed?.Invoke();

    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
}
