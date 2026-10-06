namespace Atalaia.Shared;

/// <summary>Payload enviado pelo agente a cada check-in.</summary>
public sealed class InventoryReport
{
    /// <summary>ID estável da máquina (MachineGuid do Windows).</summary>
    public required string AgentId { get; init; }
    public required string AgentVersion { get; init; }
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;

    public IdentityInfo Identity { get; init; } = new();
    public OsInfo Os { get; init; } = new();
    public HardwareInfo Hardware { get; init; } = new();
    public List<NetworkAdapterInfo> Network { get; init; } = [];
    public List<DiskInfo> Disks { get; init; } = [];
    public List<VolumeInfo> Volumes { get; init; } = [];
    public List<MonitorInfo> Monitors { get; init; } = [];
    public SecurityInfo Security { get; init; } = new();
    public HealthInfo Health { get; init; } = new();
    public List<PeripheralInfo> Peripherals { get; init; } = [];
    public NetworkActivityInfo NetworkActivity { get; init; } = new();

    /// <summary>Ferramentas da lista de interesse (ex.: apps de IA) instaladas ou em execução.</summary>
    public List<DetectedTool> Tools { get; init; } = [];

    /// <summary>Só vem preenchido em alguns ciclos (lista é grande e muda pouco).</summary>
    public List<SoftwareInfo>? Software { get; init; }

    /// <summary>Seções que falharam na coleta (nome da seção + mensagem).</summary>
    public List<string> Errors { get; init; } = [];
}

public sealed class IdentityInfo
{
    public string? Hostname { get; set; }
    public string? Domain { get; set; }
    public bool? PartOfDomain { get; set; }
    public string? LoggedUser { get; set; }
}

public sealed class OsInfo
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Build { get; set; }
    public string? Architecture { get; set; }
    public DateTimeOffset? InstallDate { get; set; }
    public bool? Activated { get; set; }
}

public sealed class HardwareInfo
{
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? Uuid { get; set; }
    public string? BiosVersion { get; set; }
    public DateTimeOffset? BiosDate { get; set; }
    public string? Cpu { get; set; }
    public int? CpuCores { get; set; }
    public int? CpuThreads { get; set; }
    public int? CpuMaxMhz { get; set; }
    public long? RamTotalBytes { get; set; }
    public List<RamModuleInfo> RamModules { get; init; } = [];
    public List<GpuInfo> Gpus { get; init; } = [];
    public bool? IsLaptop { get; set; }
    public BatteryInfo? Battery { get; set; }
}

public sealed record RamModuleInfo(string? Manufacturer, string? PartNumber, long? CapacityBytes, int? SpeedMhz);
public sealed record GpuInfo(string? Name, string? DriverVersion, long? MemoryBytes);
public sealed record BatteryInfo(int? ChargePercent, bool? Charging);

public sealed class NetworkAdapterInfo
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Type { get; set; }
    public string? Mac { get; set; }
    public long? SpeedBps { get; set; }
    public bool? DhcpEnabled { get; set; }
    public List<string> Ipv4 { get; init; } = [];
    public List<string> Ipv6 { get; init; } = [];
    public List<string> Gateways { get; init; } = [];
    public List<string> Dns { get; init; } = [];
    public string? WifiSsid { get; set; }

    /// <summary>VirtualBox, Hyper-V, VMware, VPN, Wi-Fi Direct etc.</summary>
    public bool IsVirtual { get; set; }

    /// <summary>Adaptador físico com gateway: o "IP principal" da máquina.</summary>
    public bool IsPrimary { get; set; }
}

public sealed record DiskInfo(string? Model, string? SerialNumber, long? SizeBytes, string? MediaType, string? BusType, string? HealthStatus);
public sealed record VolumeInfo(string Letter, string? Label, string? FileSystem, long? SizeBytes, long? FreeBytes);
public sealed record MonitorInfo(string? Manufacturer, string? Name, string? SerialNumber, int? ManufactureYear);

public sealed class SecurityInfo
{
    public bool? AntivirusEnabled { get; set; }
    public bool? RealTimeProtection { get; set; }
    public DateTimeOffset? AntivirusSignatureDate { get; set; }
    public List<string> AntivirusProducts { get; init; } = [];
    public bool? FirewallDomain { get; set; }
    public bool? FirewallPrivate { get; set; }
    public bool? FirewallPublic { get; set; }
    public bool? BitLockerOnSystemDrive { get; set; }
    public bool? TpmPresent { get; set; }
    public string? TpmVersion { get; set; }
    public bool? SecureBoot { get; set; }
    public bool? PendingReboot { get; set; }
}

public sealed class HealthInfo
{
    public DateTimeOffset? LastBoot { get; set; }
    public long? UptimeSeconds { get; set; }
    public double? CpuLoadPercent { get; set; }
    public long? RamFreeBytes { get; set; }
}

public sealed record SoftwareInfo(string Name, string? Version, string? Publisher, string? InstallDate);

/// <summary>Dispositivo conectado (teclado, mouse, câmera, impressora, pendrive...). O fabricante/modelo legível é resolvido no servidor a partir do VID:PID.</summary>
public sealed record PeripheralInfo(string Kind, string? Name, string? Manufacturer, string? VendorId, string? ProductId, string? Connection);

/// <summary>
/// Visão de rede por processo. De propósito NÃO inclui os IPs de destino das conexões (só portas), para não virar
/// histórico de navegação de cada pessoa.
/// </summary>
public sealed class NetworkActivityInfo
{
    /// <summary>Portas TCP escutando e acessíveis pela rede (loopback fica de fora).</summary>
    public List<ListeningPort> Listening { get; init; } = [];

    /// <summary>Conexões estabelecidas agrupadas por processo.</summary>
    public List<ProcessConnections> Connections { get; init; } = [];
}

public sealed record ListeningPort(int Port, string? Process);
public sealed record ProcessConnections(string Process, int Established, List<int> RemotePorts);

/// <summary>Source: "instalado" ou "em execução".</summary>
public sealed record DetectedTool(string Tool, string Source, string? Detail);
