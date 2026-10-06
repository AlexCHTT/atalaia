using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using Atalaia.Shared;
using Microsoft.Win32;

namespace Atalaia.Agent.Collectors;

/// <summary>
/// Coleta o inventário da máquina via WMI, registro e APIs de rede do .NET.
/// Cada seção é isolada: se uma falhar, as demais continuam e o erro vai em <see cref="InventoryReport.Errors"/>.
/// </summary>
public sealed partial class InventoryCollector
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public InventoryReport Collect(bool includeSoftware)
    {
        var errors = new List<string>();

        T Section<T>(string name, Func<T> collect, T fallback)
        {
            try { return collect(); }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); return fallback; }
        }

        var identity = Section("identity", CollectIdentity, new IdentityInfo());
        var os = Section("os", CollectOs, new OsInfo());
        var hardware = Section("hardware", CollectHardware, new HardwareInfo());
        var network = Section("network", CollectNetwork, []);
        var disks = Section("disks", CollectDisks, []);
        var volumes = Section("volumes", CollectVolumes, []);
        var monitors = Section("monitors", CollectMonitors, []);
        var security = Section("security", CollectSecurity, new SecurityInfo());
        var health = Section("health", CollectHealth, new HealthInfo());
        var peripherals = Section("peripherals", CollectPeripherals, []);
        var networkActivity = Section("networkActivity", CollectNetworkActivity, new NetworkActivityInfo());

        // A lista de software é lida sempre (barato, só registro) porque alimenta a detecção de ferramentas;
        // mas só é enviada quando pedida, pois é grande e muda pouco.
        var software = Section<List<SoftwareInfo>?>("software", CollectSoftware, null);
        var tools = Section("tools", () => CollectTools(software), []);

        return new InventoryReport
        {
            AgentId = Section("agentId", GetStableId, Environment.MachineName),
            AgentVersion = Version,
            Identity = identity,
            Os = os,
            Hardware = hardware,
            Network = network,
            Disks = disks,
            Volumes = volumes,
            Monitors = monitors,
            Security = security,
            Health = health,
            Peripherals = peripherals,
            NetworkActivity = networkActivity,
            Tools = tools,
            Software = includeSoftware ? software : null,
            Errors = errors,
        };
    }

    // ---------- Identidade / SO ----------

    // UUIDs que fabricantes de placa-mãe barata deixam como padrão: iguais em várias máquinas, não identificam nada
    private static readonly HashSet<string> BogusUuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "03000200-0400-0500-0006-000700080009",
        "00000000-0000-0000-0000-000000000000",
        "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
    };

    /// <summary>
    /// ID da máquina: UUID da BIOS, que sobrevive a reinstalação do Windows (o MachineGuid é regerado e faria a
    /// máquina formatada aparecer como outra no painel). Se a BIOS não der um UUID utilizável, cai no MachineGuid.
    /// </summary>
    private static string GetStableId()
    {
        var uuid = Wmi.First("SELECT UUID FROM Win32_ComputerSystemProduct")?.Str("UUID")?.Trim();
        return uuid is { Length: > 0 } && !BogusUuids.Contains(uuid) ? uuid.ToLowerInvariant() : GetMachineGuid();
    }

    private static string GetMachineGuid()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string ?? throw new InvalidOperationException("MachineGuid não encontrado");
    }

    private static IdentityInfo CollectIdentity()
    {
        var cs = Wmi.First("SELECT Name, Domain, PartOfDomain, UserName FROM Win32_ComputerSystem");
        return new IdentityInfo
        {
            Hostname = Environment.MachineName,
            Domain = cs?.Str("Domain"),
            PartOfDomain = cs?.Bool("PartOfDomain"),
            // Em serviço (LocalSystem) Environment.UserName seria SYSTEM; o WMI devolve o usuário da sessão interativa.
            LoggedUser = cs?.Str("UserName"),
        };
    }

    private static OsInfo CollectOs()
    {
        var os = Wmi.First("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate FROM Win32_OperatingSystem");
        // LicenseStatus 1 = Licensed
        var lic = Wmi.First("SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL AND Name LIKE 'Windows%'");
        var ubr = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null);
        var build = os?.Str("BuildNumber");
        return new OsInfo
        {
            Name = os?.Str("Caption")?.Trim(),
            Version = os?.Str("Version"),
            Build = ubr is null ? build : $"{build}.{ubr}",
            Architecture = os?.Str("OSArchitecture"),
            InstallDate = os?.Date("InstallDate"),
            Activated = lic is null ? null : lic.Int("LicenseStatus") == 1,
        };
    }

    // ---------- Hardware ----------

    private static HardwareInfo CollectHardware()
    {
        var cs = Wmi.First("SELECT Manufacturer, Model, TotalPhysicalMemory, PCSystemType FROM Win32_ComputerSystem");
        var bios = Wmi.First("SELECT SerialNumber, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
        var prod = Wmi.First("SELECT UUID FROM Win32_ComputerSystemProduct");
        var cpu = Wmi.First("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        var battery = Wmi.First("SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");

        var hw = new HardwareInfo
        {
            Manufacturer = cs?.Str("Manufacturer"),
            Model = cs?.Str("Model"),
            SerialNumber = bios?.Str("SerialNumber"),
            Uuid = prod?.Str("UUID"),
            BiosVersion = bios?.Str("SMBIOSBIOSVersion"),
            BiosDate = bios?.Date("ReleaseDate"),
            Cpu = cpu?.Str("Name")?.Trim(),
            CpuCores = cpu?.Int("NumberOfCores"),
            CpuThreads = cpu?.Int("NumberOfLogicalProcessors"),
            CpuMaxMhz = cpu?.Int("MaxClockSpeed"),
            RamTotalBytes = cs?.Long("TotalPhysicalMemory"),
            // PCSystemType 2 = Mobile; ter bateria também indica notebook
            IsLaptop = cs?.Int("PCSystemType") == 2 || battery is not null,
            Battery = battery is null ? null : new BatteryInfo(
                battery.Int("EstimatedChargeRemaining"),
                battery.Int("BatteryStatus") is 2 or 6 or 7 or 8 ? true : false),
        };

        foreach (var m in Wmi.All("SELECT Manufacturer, PartNumber, Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory"))
            hw.RamModules.Add(new RamModuleInfo(m.Str("Manufacturer"), m.Str("PartNumber")?.Trim(), m.Long("Capacity"), m.Int("ConfiguredClockSpeed") ?? m.Int("Speed")));

        foreach (var g in Wmi.All("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController"))
            hw.Gpus.Add(new GpuInfo(g.Str("Name"), g.Str("DriverVersion"), g.Long("AdapterRAM")));

        return hw;
    }

    private static List<DiskInfo> CollectDisks()
    {
        var disks = new List<DiskInfo>();

        // MSFT_PhysicalDisk traz tipo (SSD/HDD), barramento e saúde; precisa de privilégio de admin (o serviço roda como SYSTEM).
        var physical = Wmi.All("SELECT FriendlyName, SerialNumber, Size, MediaType, BusType, HealthStatus FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");
        foreach (var d in physical)
        {
            disks.Add(new DiskInfo(
                d.Str("FriendlyName"),
                d.Str("SerialNumber")?.Trim(' ', '.'),
                d.Long("Size"),
                d.Int("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" },
                d.Int("BusType") switch { 3 => "ATA", 7 => "USB", 8 => "RAID", 11 => "SATA", 17 => "NVMe", _ => null },
                d.Int("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => null }));
        }
        if (disks.Count > 0) return disks;

        // Fallback quando o namespace Storage não está disponível
        foreach (var d in Wmi.All("SELECT Model, SerialNumber, Size, InterfaceType, Status FROM Win32_DiskDrive"))
            disks.Add(new DiskInfo(d.Str("Model"), d.Str("SerialNumber")?.Trim(), d.Long("Size"), null, d.Str("InterfaceType"), d.Str("Status")));
        return disks;
    }

    private static List<VolumeInfo> CollectVolumes() =>
        Wmi.All("SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3")
            .Select(v => new VolumeInfo(v.Str("DeviceID") ?? "?", v.Str("VolumeName"), v.Str("FileSystem"), v.Long("Size"), v.Long("FreeSpace")))
            .ToList();

    private static List<MonitorInfo> CollectMonitors() =>
        Wmi.All("SELECT ManufacturerName, UserFriendlyName, SerialNumberID, YearOfManufacture FROM WmiMonitorID", @"root\wmi")
            .Select(m => new MonitorInfo(
                m.UShortString("ManufacturerName"),
                m.UShortString("UserFriendlyName"),
                m.UShortString("SerialNumberID"),
                m.Int("YearOfManufacture")))
            .ToList();

    // ---------- Rede ----------

    private static List<NetworkAdapterInfo> CollectNetwork()
    {
        var ssids = GetWifiSsids();
        var list = new List<NetworkAdapterInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var props = nic.GetIPProperties();
            var info = new NetworkAdapterInfo
            {
                Name = nic.Name,
                Description = nic.Description,
                Type = nic.NetworkInterfaceType.ToString(),
                Mac = FormatMac(nic.GetPhysicalAddress()),
                SpeedBps = nic.Speed > 0 ? nic.Speed : null,
                WifiSsid = ssids.GetValueOrDefault(nic.Name),
                IsVirtual = VirtualAdapter().IsMatch(nic.Description) || VirtualAdapter().IsMatch(nic.Name),
            };
            try { info.DhcpEnabled = props.GetIPv4Properties().IsDhcpEnabled; } catch { /* sem Ipv4 */ }

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork) info.Ipv4.Add(ua.Address.ToString());
                else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6 && !ua.Address.IsIPv6LinkLocal) info.Ipv6.Add(ua.Address.ToString());
            }
            foreach (var gw in props.GatewayAddresses) info.Gateways.Add(gw.Address.ToString());
            foreach (var dns in props.DnsAddresses) info.Dns.Add(dns.ToString());

            // Interface sem IP útil (ex.: adaptador virtual desconectado) não interessa
            if (info.Ipv4.Count == 0 && info.Ipv6.Count == 0) continue;
            list.Add(info);
        }

        // Principal = primeiro adaptador físico com gateway Ipv4
        var primary = list.FirstOrDefault(a => !a.IsVirtual && a.Gateways.Any(g => !g.Contains(':')));
        if (primary is not null) primary.IsPrimary = true;
        return list;
    }

    [GeneratedRegex(@"virtual|vmware|virtualbox|hyper-v|vethernet|wi-fi direct|tap-|wintun|wireguard|vpn|npcap|loopback|bluetooth", RegexOptions.IgnoreCase)]
    private static partial Regex VirtualAdapter();

    private static string? FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? null : string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>Mapeia nome da interface Wi-Fi → SSID usando netsh.</summary>
    private static Dictionary<string, string> GetWifiSsids()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var output = RunProcess("netsh", "wlan show interfaces");
            string? name = null;
            foreach (var line in output.Split('\n'))
            {
                var m = NetshLine().Match(line);
                if (!m.Success) continue;
                var key = m.Groups[1].Value.Trim();
                var value = m.Groups[2].Value.Trim();
                // Chaves variam por idioma ("Name"/"Nome", "SSID"); BSSID também casa com "SSID", por isso o teste exato
                if (key is "Name" or "Nome") name = value;
                else if (key == "SSID" && name is not null && value.Length > 0) result[name] = value;
            }
        }
        catch { /* sem Wi-Fi ou netsh indisponível */ }
        return result;
    }

    [GeneratedRegex(@"^\s*([^:]+?)\s*:\s*(.*?)\s*$")]
    private static partial Regex NetshLine();

    // ---------- Segurança ----------

    private static SecurityInfo CollectSecurity()
    {
        var sec = new SecurityInfo();

        var defender = Wmi.First("SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureLastUpdated FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender");
        if (defender is not null)
        {
            sec.AntivirusEnabled = defender.Bool("AntivirusEnabled");
            sec.RealTimeProtection = defender.Bool("RealTimeProtectionEnabled");
            sec.AntivirusSignatureDate = defender.Date("AntivirusSignatureLastUpdated");
        }

        // SecurityCenter2 só existe em edições cliente do Windows
        foreach (var av in Wmi.All("SELECT displayName FROM AntiVirusProduct", @"root\SecurityCenter2"))
            if (av.Str("displayName") is { } n) sec.AntivirusProducts.Add(n);

        sec.FirewallDomain = FirewallEnabled("DomainProfile");
        sec.FirewallPrivate = FirewallEnabled("StandardProfile");
        sec.FirewallPublic = FirewallEnabled("PublicProfile");

        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var bl = Wmi.First($"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{systemDrive}'", @"root\CIMV2\Security\MicrosoftVolumeEncryption");
        if (bl is not null) sec.BitLockerOnSystemDrive = bl.Int("ProtectionStatus") == 1;

        var tpm = Wmi.First("SELECT IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm", @"root\CIMV2\Security\MicrosoftTpm");
        // Sem permissão (não-admin) a consulta volta vazia; reportamos desconhecido (null) em vez de "não tem TPM"
        sec.TpmPresent = tpm is null ? null : true;
        sec.TpmVersion = tpm?.Str("SpecVersion")?.Split(',')[0].Trim();

        var sb = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null);
        sec.SecureBoot = sb is int i ? i == 1 : null;

        sec.PendingReboot = IsRebootPending();
        return sec;
    }

    private static bool? FirewallEnabled(string profile) =>
        Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profile}", "EnableFirewall", null) is int v ? v == 1 : null;

    private static bool IsRebootPending()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        string[] keys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
        ];
        if (keys.Any(k => hklm.OpenSubKey(k) is { } sub && Dispose(sub))) return true;
        using var sm = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        return sm?.GetValue("PendingFileRenameOperations") is not null;

        static bool Dispose(RegistryKey k) { k.Dispose(); return true; }
    }

    // ---------- Saúde / uso ----------

    private static HealthInfo CollectHealth()
    {
        var os = Wmi.First("SELECT LastBootUpTime, FreePhysicalMemory FROM Win32_OperatingSystem");
        var cpuLoad = Wmi.All("SELECT LoadPercentage FROM Win32_Processor").Select(p => p.Int("LoadPercentage")).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
        var lastBoot = os?.Date("LastBootUpTime");
        return new HealthInfo
        {
            LastBoot = lastBoot,
            UptimeSeconds = lastBoot is null ? null : (long)(DateTimeOffset.UtcNow - lastBoot.Value).TotalSeconds,
            CpuLoadPercent = cpuLoad.Count > 0 ? Math.Round(cpuLoad.Average(), 1) : null,
            RamFreeBytes = os?.Long("FreePhysicalMemory") * 1024, // WMI informa em KB
        };
    }

    // ---------- Software instalado ----------

    private static List<SoftwareInfo> CollectSoftware()
    {
        var found = new Dictionary<string, SoftwareInfo>(StringComparer.OrdinalIgnoreCase);
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        foreach (var path in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        })
        {
            using var root = hklm.OpenSubKey(path);
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                if (k.GetValue("SystemComponent") is 1) continue;
                if (k.GetValue("ParentKeyName") is not null) continue; // atualizações/patches
                var version = k.GetValue("DisplayVersion") as string;
                found[$"{name}|{version}"] = new SoftwareInfo(name.Trim(), version, k.GetValue("Publisher") as string, k.GetValue("InstallDate") as string);
            }
        }
        return found.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- Util ----------

    private static string RunProcess(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(5000)) { p.Kill(); throw new TimeoutException($"{file} demorou demais"); }
        return output;
    }
}

/// <summary>Helpers finos sobre WMI: consultas que nunca lançam exceção e leitura tipada de propriedades.</summary>
internal static class Wmi
{
    public static List<ManagementBaseObject> All(string wql, string scope = @"root\cimv2")
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            using var results = searcher.Get();
            foreach (var o in results) list.Add(o);
        }
        catch (ManagementException) { /* namespace/classe inexistente nesta edição do Windows */ }
        catch (UnauthorizedAccessException) { }
        return list;
    }

    public static ManagementBaseObject? First(string wql, string scope = @"root\cimv2") => All(wql, scope).FirstOrDefault();

    private static object? Prop(this ManagementBaseObject o, string name)
    {
        try { return o[name]; } catch (ManagementException) { return null; }
    }

    public static string? Str(this ManagementBaseObject o, string name) => o.Prop(name)?.ToString() is { Length: > 0 } s ? s : null;
    public static int? Int(this ManagementBaseObject o, string name) => o.Prop(name) is { } v ? Convert.ToInt32(v) : null;
    public static long? Long(this ManagementBaseObject o, string name) => o.Prop(name) is { } v ? Convert.ToInt64(v) : null;
    public static bool? Bool(this ManagementBaseObject o, string name) => o.Prop(name) is { } v ? Convert.ToBoolean(v) : null;

    public static DateTimeOffset? Date(this ManagementBaseObject o, string name)
    {
        if (o.Prop(name) is not string s || s.Length == 0) return null;
        try { return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(s)); } catch { return null; }
    }

    /// <summary>Strings no namespace root\wmi vêm como array de ushort (códigos de caractere).</summary>
    public static string? UShortString(this ManagementBaseObject o, string name) =>
        o.Prop(name) is ushort[] chars
            ? new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() is { Length: > 0 } s ? s : null
            : null;
}
