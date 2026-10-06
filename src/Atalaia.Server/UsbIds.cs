namespace Atalaia.Server;

/// <summary>
/// Traduz VID:PID em fabricante e modelo usando o arquivo usb.ids (linux-usb.org).
/// Fica no servidor: o agente só manda os códigos, e atualizar o arquivo melhora também o que já foi coletado.
/// </summary>
public sealed class UsbIds
{
    private readonly Dictionary<string, string> _vendors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _products = new(StringComparer.OrdinalIgnoreCase);

    public int VendorCount => _vendors.Count;

    public UsbIds(ILogger<UsbIds> logger)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "usb.ids");
        if (!File.Exists(path))
        {
            logger.LogWarning("usb.ids não encontrado em {Path}: fabricantes USB aparecerão só como código", path);
            return;
        }

        string? vendor = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == 'C' && line.StartsWith("C ", StringComparison.Ordinal)) break; // depois disso vêm as classes de dispositivo

            if (line[0] != '\t')
            {
                if (line.Length > 6) { vendor = line[..4]; _vendors[vendor] = line[6..].Trim(); }
            }
            else if (line.Length > 7 && line[1] != '\t' && vendor is not null)
            {
                _products[$"{vendor}:{line[1..5]}"] = line[7..].Trim();
            }
        }
    }

    public (string? Vendor, string? Product) Lookup(string? vid, string? pid)
    {
        if (string.IsNullOrEmpty(vid)) return (null, null);
        _vendors.TryGetValue(vid, out var vendor);
        string? product = null;
        if (!string.IsNullOrEmpty(pid)) _products.TryGetValue($"{vid}:{pid}", out product);
        return (vendor, product);
    }

    /// <summary>"Logitech, Inc. Nano Receiver (046D:C534)" ou, sem tradução, o nome do Windows.</summary>
    public string Describe(string? vid, string? pid, string? windowsName)
    {
        var (vendor, product) = Lookup(vid, pid);
        var code = vid is null ? "" : $" ({vid}:{pid})";
        if (vendor is null) return $"{windowsName}{code}".Trim();
        return $"{vendor} {product ?? windowsName}{code}".Trim();
    }
}
