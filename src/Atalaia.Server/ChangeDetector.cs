using Atalaia.Shared;

namespace Atalaia.Server;

/// <summary>Uma linha do log de auditoria. O log só cresce: nada é editado nem apagado.</summary>
public sealed record AuditEvent(
    long Id, DateTimeOffset At, string AgentId, string? Hostname,
    string Type, string Severity, string? Field, string? OldValue, string? NewValue, string? User);

/// <summary>
/// Compara o relatório anterior com o atual e devolve só o que mudou de forma relevante para auditoria.
/// Valores que oscilam o tempo todo (CPU, RAM livre, uptime) ficam de fora de propósito: viram métricas, não eventos.
/// </summary>
public static class ChangeDetector
{
    public static Dictionary<string, string?> Facts(InventoryReport r)
    {
        var real = r.Network.Where(n => !n.IsVirtual).ToList();
        var primary = r.Network.FirstOrDefault(n => n.IsPrimary);

        return new Dictionary<string, string?>
        {
            ["identity.hostname"] = r.Identity.Hostname,
            ["identity.domain"] = r.Identity.Domain,
            ["os.name"] = r.Os.Name,
            ["os.build"] = r.Os.Build,
            ["os.activated"] = Bool(r.Os.Activated),
            ["network.primaryIp"] = primary?.Ipv4.FirstOrDefault(),
            ["network.macs"] = Join(real.Select(n => n.Mac)),
            ["hardware.manufacturer"] = r.Hardware.Manufacturer,
            ["hardware.model"] = r.Hardware.Model,
            ["hardware.serial"] = r.Hardware.SerialNumber,
            ["hardware.cpu"] = r.Hardware.Cpu,
            ["hardware.ram"] = r.Hardware.RamTotalBytes is { } b ? $"{Math.Round(b / 1073741824.0)} GB" : null,
            ["hardware.disks"] = Join(r.Disks.Select(d => $"{d.Model} {d.SerialNumber}".Trim())),
            ["hardware.gpus"] = Join(r.Hardware.Gpus.Select(g => g.Name)),
            ["security.antivirusEnabled"] = Bool(r.Security.AntivirusEnabled),
            ["security.realTimeProtection"] = Bool(r.Security.RealTimeProtection),
            ["security.antivirusProducts"] = Join(r.Security.AntivirusProducts),
            ["security.firewallDomain"] = Bool(r.Security.FirewallDomain),
            ["security.firewallPrivate"] = Bool(r.Security.FirewallPrivate),
            ["security.firewallPublic"] = Bool(r.Security.FirewallPublic),
            ["security.bitLocker"] = Bool(r.Security.BitLockerOnSystemDrive),
            ["security.tpm"] = Bool(r.Security.TpmPresent),
            ["security.secureBoot"] = Bool(r.Security.SecureBoot),
            ["agent.version"] = r.AgentVersion,
        };
    }

    /// <summary>Mudanças gerais (hardware, rede, segurança...). Sem relatório anterior não há o que comparar.</summary>
    public static IEnumerable<(string Type, string Severity, string Field, string? Old, string? New)> Diff(
        Dictionary<string, string?> before, Dictionary<string, string?> after)
    {
        foreach (var (key, now) in after)
        {
            before.TryGetValue(key, out var was);
            if (was == now) continue;

            var isSecurity = key.StartsWith("security.", StringComparison.Ordinal);

            // Em segurança, null significa "não consegui ler" (ex.: agente sem admin), não "mudou".
            if (isSecurity && (was is null || now is null)) continue;

            // Ficar pior em segurança (true → false, ou perder um antivírus) é o que a auditoria quer ver em destaque.
            var worse = isSecurity && (was == "true" && now == "false" || key == "security.antivirusProducts" && (now?.Length ?? 0) < (was?.Length ?? 0));
            yield return ("changed", worse ? "warn" : "info", key, was, now);
        }
    }

    /// <summary>Software instalado/removido/atualizado. Só compara quando os dois lados têm a lista.</summary>
    public static IEnumerable<(string Type, string Field, string? Old, string? New)> DiffSoftware(
        IReadOnlyList<SoftwareInfo>? before, IReadOnlyList<SoftwareInfo>? after)
    {
        if (before is null || after is null) yield break;

        var was = ByName(before);
        var now = ByName(after);

        foreach (var (name, version) in now)
        {
            if (!was.TryGetValue(name, out var old)) yield return ("software_installed", name, null, version);
            else if (old != version) yield return ("software_updated", name, old, version);
        }
        foreach (var (name, version) in was)
            if (!now.ContainsKey(name)) yield return ("software_removed", name, version, null);
    }

    /// <summary>Periférico conectado/removido. Pendrive conectado sobe como alerta: é o que a auditoria quer ver.</summary>
    public static IEnumerable<(string Type, string Severity, string Field, string? Old, string? New)> DiffPeripherals(
        IReadOnlyList<PeripheralInfo> before, IReadOnlyList<PeripheralInfo> after, Func<PeripheralInfo, string> describe)
    {
        // Lista vazia nunca é real (todo PC tem ao menos um teclado): significa "não coletado". Anterior vazio = veio de um agente
        // sem este recurso (linha de base, não "tudo foi conectado"); atual vazio = agente antigo ou coleta que falhou
        // (não "tudo foi removido"). Sem isso, trocar de versão do agente geraria uma enxurrada de eventos falsos.
        if (before.Count == 0 || after.Count == 0) yield break;

        static string Key(PeripheralInfo p) => $"{p.Kind}|{p.VendorId}|{p.ProductId}|{(p.VendorId is null ? p.Name : "")}";
        var was = before.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var now = after.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());

        foreach (var (key, p) in now)
            if (!was.ContainsKey(key))
                yield return ("peripheral_connected", p.Kind == "Armazenamento USB" ? "warn" : "info", p.Kind, null, describe(p));

        foreach (var (key, p) in was)
            if (!now.ContainsKey(key))
                yield return ("peripheral_removed", "info", p.Kind, describe(p), null);
    }

    private static Dictionary<string, string?> ByName(IEnumerable<SoftwareInfo> list)
    {
        // Mesmo nome com versões diferentes (ex.: runtimes lado a lado) vira uma entrada só, com as versões juntas
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in list.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            map[g.Key] = string.Join(", ", g.Select(s => s.Version).Where(v => !string.IsNullOrEmpty(v)).Distinct().Order());
        return map;
    }

    private static string? Bool(bool? v) => v is null ? null : v.Value ? "true" : "false";

    private static string? Join(IEnumerable<string?> items)
    {
        var s = string.Join(", ", items.Where(i => !string.IsNullOrWhiteSpace(i)).Order());
        return s.Length == 0 ? null : s;
    }
}
