using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Atalaia.Shared;

namespace Atalaia.Agent.Collectors;

// Periféricos, rede por processo e detecção de ferramentas de interesse.
public sealed partial class InventoryCollector
{
    // ---------- Periféricos ----------

    // USB\VID_046D&PID_C534...  ou  HID\VID_046D&PID_C534...
    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidRegex();

    // HID sobre Bluetooth/USB-HID: ..._VID&0002046D_PID&B01A  (os 4 primeiros dígitos são a origem: 0001 = USB, 0002 = Bluetooth)
    [GeneratedRegex(@"VID&([0-9A-F]{4})([0-9A-F]{4})_PID&([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex HidVidPidRegex();

    // USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA...
    [GeneratedRegex(@"VEN_([^&]+)&PROD_([^&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UsbStorRegex();

    // Impressoras virtuais não interessam
    [GeneratedRegex(@"pdf|xps|onenote|fax|print to|snagit|anydesk", RegexOptions.IgnoreCase)]
    private static partial Regex VirtualPrinter();

    private static List<PeripheralInfo> CollectPeripherals()
    {
        var found = new Dictionary<string, PeripheralInfo>();

        foreach (var d in Wmi.All("""
            SELECT Name, Manufacturer, PNPClass, PNPDeviceID FROM Win32_PnPEntity
            WHERE PNPClass = 'Keyboard' OR PNPClass = 'Mouse' OR PNPClass = 'Camera' OR PNPClass = 'Image'
               OR PNPClass = 'MEDIA' OR PNPClass = 'Bluetooth' OR PNPClass = 'DiskDrive'
            """))
        {
            var id = d.Str("PNPDeviceID") ?? "";
            string? vid = null, pid = null, connection = null, vendorText = null;

            if (VidPidRegex().Match(id) is { Success: true } m)
            {
                (vid, pid) = (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant());
                connection = "USB";
            }
            else if (HidVidPidRegex().Match(id) is { Success: true } h)
            {
                (vid, pid) = (h.Groups[2].Value.ToUpperInvariant(), h.Groups[3].Value.ToUpperInvariant());
                connection = h.Groups[1].Value == "0002" ? "Bluetooth" : "USB";
            }
            else if (id.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase) && UsbStorRegex().Match(id) is { Success: true } u)
            {
                vendorText = u.Groups[1].Value.Replace('_', ' ').Trim();
                connection = "USB";
            }
            else if (id.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase)) connection = "Bluetooth";
            else if (id.StartsWith("ACPI", StringComparison.OrdinalIgnoreCase) || id.StartsWith("ROOT", StringComparison.OrdinalIgnoreCase)) connection = "Interno";
            else connection = "Outro";

            // O que descartar: serviços de pilha Bluetooth, áudio interno, discos que não são USB...
            var cls = d.Str("PNPClass");
            var kind = cls switch
            {
                "Keyboard" => "Teclado",
                "Mouse" => "Mouse",
                "Camera" or "Image" => "Câmera/Scanner",
                "MEDIA" when connection == "USB" => "Áudio USB",
                "Bluetooth" when connection == "Bluetooth" => "Bluetooth pareado",
                "Bluetooth" when connection == "USB" => "Adaptador Bluetooth",
                "DiskDrive" when id.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase) => "Armazenamento USB",
                _ => null,
            };
            if (kind is null) continue;

            // Um mouse/teclado costuma aparecer em várias interfaces HID: junta pelo VID:PID
            var key = vid is null ? $"{kind}|{d.Str("Name")}|{id}" : $"{kind}|{vid}|{pid}";
            found.TryAdd(key, new PeripheralInfo(kind, d.Str("Name"), vendorText ?? d.Str("Manufacturer"), vid, pid, connection));
        }

        foreach (var p in Wmi.All("SELECT Name, DriverName, PortName, Network FROM Win32_Printer"))
        {
            var name = p.Str("Name");
            if (name is null || VirtualPrinter().IsMatch(name)) continue;
            var port = p.Str("PortName") ?? "";
            var connection = p.Bool("Network") == true ? "Rede" : port.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ? "USB" : "Local";
            found.TryAdd($"Impressora|{name}", new PeripheralInfo("Impressora", name, p.Str("DriverName"), null, null, connection));
        }

        return found.Values.OrderBy(x => x.Kind).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- Rede por processo ----------

    private static NetworkActivityInfo CollectNetworkActivity()
    {
        var names = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p) names[p.Id] = p.ProcessName;
        }
        string Name(int pid) => names.TryGetValue(pid, out var n) ? n : pid is 0 or 4 ? "System" : $"pid {pid}";

        var rows = TcpTable.Read();
        var activity = new NetworkActivityInfo();

        // Escutando e acessível pela rede (loopback é só a própria máquina: fora)
        foreach (var g in rows.Where(r => r.State == TcpTable.Listen && !IPAddress.IsLoopback(r.Local))
                     .GroupBy(r => (r.LocalPort, Process: Name(r.Pid))).OrderBy(g => g.Key.LocalPort).Take(200))
            activity.Listening.Add(new ListeningPort(g.Key.LocalPort, g.Key.Process));

        // Conexões estabelecidas por processo, só com as portas de destino (sem IPs: não vira histórico de navegação)
        foreach (var g in rows.Where(r => r.State == TcpTable.Established && !IPAddress.IsLoopback(r.Remote))
                     .GroupBy(r => Name(r.Pid)).OrderByDescending(g => g.Count()).Take(25))
        {
            var ports = g.GroupBy(r => r.RemotePort).OrderByDescending(p => p.Count()).Take(10).Select(p => p.Key).ToList();
            activity.Connections.Add(new ProcessConnections(g.Key, g.Count(), ports));
        }
        return activity;
    }

    // ---------- Ferramentas de interesse ----------

    // (nome exibido, processos, trechos do nome no "Programas e Recursos"). Lista curta e conservadora; ajuste à vontade.
    private static readonly (string Tool, string[] Processes, string[] Software)[] Watchlist =
    [
        ("Claude (Anthropic)", ["claude"], ["Claude"]),
        ("ChatGPT (OpenAI)", ["chatgpt"], ["ChatGPT"]),
        ("Microsoft Copilot", ["copilot"], ["Microsoft 365 Copilot", "Microsoft Copilot"]),
        ("Cursor", ["cursor"], ["Cursor"]),
        ("Windsurf", ["windsurf"], ["Windsurf"]),
        ("Ollama", ["ollama", "ollama app"], ["Ollama"]),
        ("LM Studio", ["lm studio"], ["LM Studio"]),
        ("Perplexity", ["perplexity"], ["Perplexity"]),
    ];

    private static List<DetectedTool> CollectTools(IReadOnlyList<SoftwareInfo>? software)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p) running.Add(p.ProcessName);
        }

        var tools = new List<DetectedTool>();
        foreach (var (tool, processes, patterns) in Watchlist)
        {
            foreach (var proc in processes.Where(running.Contains))
                tools.Add(new DetectedTool(tool, "em execução", proc));

            var installed = software?.FirstOrDefault(s => patterns.Any(p => s.Name.Contains(p, StringComparison.OrdinalIgnoreCase)));
            if (installed is not null) tools.Add(new DetectedTool(tool, "instalado", $"{installed.Name} {installed.Version}".Trim()));
        }
        return tools;
    }
}

/// <summary>Tabela TCP do Windows com o PID dono de cada conexão (o equivalente ao netstat -ano).</summary>
internal static class TcpTable
{
    public const int Listen = 2, Established = 5;
    public readonly record struct Row(int State, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, int Pid);

    private const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidAll = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    public static List<Row> Read()
    {
        var rows = new List<Row>();
        Read(AfInet, rows, rowSize: 24, Parse4);
        Read(AfInet6, rows, rowSize: 56, Parse6);
        return rows;
    }

    private static void Read(int family, List<Row> rows, int rowSize, Func<IntPtr, Row> parse)
    {
        // A tabela pode crescer entre a medição e a leitura: repete até caber
        var size = 0;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            uint result;
            do
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = size > 0 ? Marshal.AllocHGlobal(size) : IntPtr.Zero;
                result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0);
            }
            while (result == 122 /* ERROR_INSUFFICIENT_BUFFER */);

            if (result != 0 || buffer == IntPtr.Zero) return;
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++) rows.Add(parse(buffer + 4 + i * rowSize));
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    // A porta vem em ordem de bytes de rede, nos 16 bits baixos
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    // MIB_TCPROW_OWNER_PID: estado, IP local, porta local, IP remoto, porta remota, PID
    private static Row Parse4(IntPtr p) => new(
        Marshal.ReadInt32(p, 0),
        new IPAddress((uint)Marshal.ReadInt32(p, 4)), Port(Marshal.ReadInt32(p, 8)),
        new IPAddress((uint)Marshal.ReadInt32(p, 12)), Port(Marshal.ReadInt32(p, 16)),
        Marshal.ReadInt32(p, 20));

    // MIB_TCP6ROW_OWNER_PID: IP local (16), escopo, porta, IP remoto (16), escopo, porta, estado, PID
    private static Row Parse6(IntPtr p)
    {
        var local = new byte[16];
        var remote = new byte[16];
        Marshal.Copy(p, local, 0, 16);
        Marshal.Copy(p + 24, remote, 0, 16);
        return new Row(Marshal.ReadInt32(p, 48),
            new IPAddress(local), Port(Marshal.ReadInt32(p, 20)),
            new IPAddress(remote), Port(Marshal.ReadInt32(p, 44)),
            Marshal.ReadInt32(p, 52));
    }
}
