using Atalaia.Shared;

namespace Atalaia.Server;

/// <summary>Um problema de higiene de segurança encontrado numa máquina.</summary>
public sealed record Issue(string Code, string Severity, string Title, int Penalty);

/// <summary>
/// Índice de saúde (0 a 100) de uma máquina, calculado só com o que o agente já coleta: antivírus, firewall, criptografia,
/// espaço e saúde de disco, atualizações pendentes e portas de acesso remoto expostas.
/// É um indicador de HIGIENE da configuração, não um detector de malware: 100 não significa "sem vírus".
/// </summary>
public static class HealthAssessment
{
    public const string Critical = "critical", Warning = "warning", Info = "info";

    public sealed record Thresholds(int DiskWarnPct, int DiskCritPct, int SignatureMaxDays, int UptimeMaxDays);
    private static volatile Thresholds _t = new(80, 90, 7, 30);
    /// <summary>Limites vindos da tela Configurações (chamado na subida e a cada alteração).</summary>
    public static void Configure(Thresholds t) => _t = t;

    // Faixas do índice: usadas no painel para o selo "Protegido / Atenção / Em risco"
    public static string Level(int score) => score >= 90 ? "good" : score >= 70 ? "warning" : "critical";

    public static (int Score, List<Issue> Issues) Assess(InventoryReport r, DateTimeOffset now)
    {
        var issues = new List<Issue>();
        void Add(string code, string severity, string title, int penalty) => issues.Add(new Issue(code, severity, title, penalty));

        var s = r.Security;

        // Outro antivírus ativo (ex.: ESET) deixa o Defender passivo: não é problema ele estar "desligado".
        var hasThirdPartyAv = s.AntivirusProducts.Any(p => !p.Contains("Defender", StringComparison.OrdinalIgnoreCase));

        if (s.AntivirusEnabled == false && !hasThirdPartyAv)
            Add("av_off", Critical, "Antivírus desativado ou ausente", 30);
        else if (s.RealTimeProtection == false && !hasThirdPartyAv)
            Add("av_realtime_off", Critical, "Proteção em tempo real desligada", 25);

        if (s.AntivirusEnabled == true && s.AntivirusSignatureDate is { } sig && (now - sig).TotalDays > _t.SignatureMaxDays)
            Add("av_outdated", Warning, $"Assinaturas do antivírus com {(int)(now - sig).TotalDays} dias", 10);

        if (s.FirewallDomain == false || s.FirewallPrivate == false || s.FirewallPublic == false)
            Add("firewall_off", Warning, "Firewall desligado em algum perfil de rede", 15);

        if (s.BitLockerOnSystemDrive == false)
            Add("bitlocker_off", Warning, "Disco do sistema sem criptografia (BitLocker)", 10);

        if (s.SecureBoot == false)
            Add("secureboot_off", Info, "Secure Boot desativado", 4);

        if (s.TpmPresent == false)
            Add("tpm_missing", Info, "Sem TPM", 3);

        if (r.Os.Activated == false)
            Add("not_activated", Warning, "Windows não ativado", 5);

        if (s.PendingReboot == true)
            Add("pending_reboot", Info, "Reinício pendente", 3);

        // Disco do sistema (C: ou, na falta, o primeiro volume)
        var volume = r.Volumes.FirstOrDefault(v => v.Letter.Equals("C:", StringComparison.OrdinalIgnoreCase)) ?? r.Volumes.FirstOrDefault();
        if (volume is { SizeBytes: > 0, FreeBytes: { } free })
        {
            var used = 100.0 * (volume.SizeBytes!.Value - free) / volume.SizeBytes.Value;
            if (used >= _t.DiskCritPct) Add("disk_full", Critical, $"Disco do sistema com {used:0}% ocupado", 15);
            else if (used >= _t.DiskWarnPct) Add("disk_high", Warning, $"Disco do sistema com {used:0}% ocupado", 8);
        }

        if (r.Disks.Any(d => d.HealthStatus is not null and not "Healthy" and not "OK"))
            Add("disk_health", Critical, "Disco com problema de saúde", 20);

        if (r.Health.UptimeSeconds is { } up && up > _t.UptimeMaxDays * 86400L)
            Add("uptime_long", Info, $"Sem reiniciar há {r.Health.UptimeSeconds / 86400} dias", 2);

        // Acesso remoto/serviços legados escutando para a rede (SMB não entra: é normal em PC de domínio)
        var ports = r.NetworkActivity.Listening.Select(l => l.Port).ToHashSet();
        if (ports.Contains(3389)) Add("rdp_exposed", Warning, "Área de trabalho remota (RDP) exposta, porta 3389", 8);
        if (ports.Overlaps([5800, 5900, 5901])) Add("vnc_exposed", Warning, "VNC exposto na rede, portas 5800/5900", 8);
        if (ports.Contains(23)) Add("telnet_exposed", Critical, "Telnet exposto, porta 23", 15);
        if (ports.Contains(21)) Add("ftp_exposed", Critical, "FTP exposto, porta 21", 15);

        if (r.Errors.Count > 0)
            Add("collect_errors", Info, $"{r.Errors.Count} falha(s) de coleta no agente", 1);

        var score = Math.Max(0, 100 - issues.Sum(i => i.Penalty));
        return (score, issues);
    }
}
