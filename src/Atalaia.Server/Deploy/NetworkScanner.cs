using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Atalaia.Server;

public sealed record ScanHost(string Ip, string? Name, int[] OpenPorts);
public sealed record ScanState(string Id, string Range, string State, int Total, int Done, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string? RequestedBy, List<ScanHost> Hosts);

/// <summary>
/// Faixas de IP aceitas pela varredura: CIDR (192.168.1.0/24), um IP (192.168.1.10) ou intervalo (192.168.1.10-50 ou 192.168.1.10-192.168.1.50).
/// Só redes privadas (e loopback, para teste) e no máximo o limite das Configurações (padrão 1024) de endereços: o painel não serve para varrer a internet.
/// </summary>
public static class IpRange
{
    public const int AbsoluteMaxHosts = 4096;   // teto do que a tela Configurações permite escolher

    public static bool IsAllowed(uint ip) =>
        (ip >> 24) == 10 || (ip >> 20) == 0xAC1 || (ip >> 16) == 0xC0A8 || (ip >> 24) == 127;   // 10/8, 172.16/12, 192.168/16, 127/8

    public static string ToText(uint ip) => $"{ip >> 24}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}";

    public static bool TryParseIp(string? s, out uint ip)
    {
        ip = 0;
        if (string.IsNullOrWhiteSpace(s) || !IPAddress.TryParse(s.Trim(), out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        // IPAddress.TryParse aceita formas como "1.2" ou "0x7f.1": exigimos quatro números decimais
        if (s.Trim().Split('.') is not { Length: 4 } parts || parts.Any(p => p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit))) return false;
        var b = a.GetAddressBytes();
        ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        return true;
    }

    public static (List<uint>? Ips, string? Error) Parse(string? input, int maxHosts = 1024)
    {
        var text = (input ?? "").Trim();
        if (text.Length is 0 or > 64) return (null, "Informe uma faixa de IP, por exemplo 192.168.1.0/24.");
        uint first, last;

        if (text.Contains('/'))
        {
            var parts = text.Split('/');
            if (parts.Length != 2 || !TryParseIp(parts[0], out var baseIp) || !int.TryParse(parts[1], out var bits) || parts[1].Length > 2)
                return (null, "Faixa inválida. Use o formato 192.168.1.0/24.");
            if (bits < 12 || bits > 32 || (long)1 << (32 - bits) > maxHosts) return (null, $"A faixa é grande demais: o limite atual é de {maxHosts} endereços por varredura.");
            var mask = bits == 32 ? uint.MaxValue : ~(uint.MaxValue >> bits);
            first = baseIp & mask; last = first | ~mask;
            if (bits <= 30) { first++; last--; }   // sem o endereço da rede e o de broadcast
        }
        else if (text.Contains('-'))
        {
            var parts = text.Split('-');
            if (parts.Length != 2 || !TryParseIp(parts[0], out first)) return (null, "Intervalo inválido. Use 192.168.1.10-50 ou 192.168.1.10-192.168.1.50.");
            if (!TryParseIp(parts[1], out last))
            {
                if (!int.TryParse(parts[1], out var tail) || tail is < 0 or > 255 || parts[1].Length > 3) return (null, "Intervalo inválido. Use 192.168.1.10-50 ou 192.168.1.10-192.168.1.50.");
                last = (first & 0xFFFFFF00) | (uint)tail;
            }
        }
        else if (!TryParseIp(text, out first)) return (null, "Endereço inválido. Exemplos: 192.168.1.0/24, 192.168.1.10, 192.168.1.10-50.");
        else last = first;

        if (last < first) return (null, "O fim do intervalo vem antes do começo.");
        if ((long)last - first + 1 > maxHosts) return (null, $"Faixa grande demais: o limite atual é de {maxHosts} endereços por varredura.");

        var ips = new List<uint>((int)(last - first + 1));
        for (var ip = first; ip <= last; ip++)
        {
            if (!IsAllowed(ip)) return (null, "Só é permitido varrer redes privadas (10.x.x.x, 172.16-31.x.x, 192.168.x.x).");
            ips.Add(ip);
            if (ip == uint.MaxValue) break;
        }
        return (ips, null);
    }
}

/// <summary>
/// Procura computadores numa faixa de IP por TCP (445, 135, 5985, 3389): porta aberta OU recusada (RST) significa que há alguém lá.
/// Não usa ping (exigiria privilégio no container). Um PC com firewall que descarta tudo não aparece: a lista nunca é garantida como completa.
/// Só roda uma varredura por vez.
/// </summary>
public sealed class NetworkScanner(SettingsStore settings, ILogger<NetworkScanner> log)
{
    public static readonly int[] Ports = [445, 135, 5985, 3389];
    private const int ConnectTimeoutMs = 800;
    private const int ParallelHosts = 64;

    private sealed class Run
    {
        public required string Id, Range;
        public required List<uint> Ips;
        public string? RequestedBy;
        public DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt;
        public string State = "running";   // running | done | cancelled | failed
        public int Done;
        public ConcurrentBag<ScanHost> Hosts = [];
        public CancellationTokenSource Cts = new();
    }

    private readonly object _lock = new();
    private readonly ILogger<NetworkScanner> _log = log;
    private Run? _current;

    public (ScanState? State, string? Error, bool Busy) Start(string? range, string? requestedBy)
    {
        var (ips, error) = IpRange.Parse(range, settings.Int("deploy.max_scan_hosts"));
        if (ips is null) return (null, error, false);
        lock (_lock)
        {
            if (_current is { State: "running" }) return (Snapshot(_current), "Já existe uma varredura em andamento.", true);
            var run = new Run { Id = Guid.NewGuid().ToString("N"), Range = range!.Trim(), Ips = ips, RequestedBy = requestedBy };
            _current = run;
            _ = Task.Run(() => Execute(run));
            return (Snapshot(run), null, false);
        }
    }

    public ScanState? Latest() { lock (_lock) return _current is null ? null : Snapshot(_current); }

    public bool Cancel(string id)
    {
        lock (_lock)
        {
            if (_current is not { State: "running" } run || run.Id != id) return false;
            run.Cts.Cancel();
            return true;
        }
    }

    private static ScanState Snapshot(Run r) => new(r.Id, r.Range, r.State, r.Ips.Count, Volatile.Read(ref r.Done), r.StartedAt, r.FinishedAt, r.RequestedBy,
        r.Hosts.OrderBy(h => IpRange.TryParseIp(h.Ip, out var n) ? n : 0).ToList());

    private async Task Execute(Run run)
    {
        try
        {
            using var gate = new SemaphoreSlim(ParallelHosts);
            await Task.WhenAll(run.Ips.Select(async ip =>
            {
                await gate.WaitAsync(run.Cts.Token);
                try { if (await ProbeHost(new IPAddress(ToBytes(ip)), run.Cts.Token) is { } host) run.Hosts.Add(host); }
                finally { gate.Release(); Interlocked.Increment(ref run.Done); }
            }));
            run.State = "done";
        }
        catch (OperationCanceledException) { run.State = "cancelled"; }
        catch (Exception ex) { _log.LogWarning(ex, "Varredura falhou"); run.State = "failed"; }
        finally { run.FinishedAt = DateTimeOffset.UtcNow; }
    }

    private static byte[] ToBytes(uint ip) => [(byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip];

    private static async Task<ScanHost?> ProbeHost(IPAddress ip, CancellationToken ct)
    {
        var results = await Task.WhenAll(Ports.Select(p => Probe(ip, p, ct)));
        if (!results.Any(r => r.Alive)) return null;
        var open = Ports.Where((_, i) => results[i].Open).ToArray();
        return new ScanHost(ip.ToString(), await ReverseDns(ip, ct), open);
    }

    private static async Task<(bool Open, bool Alive)> Probe(IPAddress ip, int port, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeoutMs);
        try { await socket.ConnectAsync(new IPEndPoint(ip, port), cts.Token); return (true, true); }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused) { return (false, true); }   // recusou: o PC existe, só não escuta ali
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (false, false); }   // estourou o tempo
        catch (SocketException) { return (false, false); }
    }

    private static async Task<string?> ReverseDns(IPAddress ip, CancellationToken ct)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);   // sem CancellationToken nesta sobrecarga: o limite de 1,5 s abaixo cobre
            if (await Task.WhenAny(lookup, Task.Delay(1500, ct)) != lookup) return null;
            var name = (await lookup).HostName;
            return string.IsNullOrWhiteSpace(name) || name == ip.ToString() ? null : name.Split('.')[0];   // só o nome curto da máquina
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
