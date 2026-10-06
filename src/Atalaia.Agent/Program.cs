using System.Text.Json;
using Atalaia.Agent;
using Atalaia.Agent.Collectors;
using Atalaia.Shared;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;

// Modo de diagnóstico: coleta uma vez e imprime o JSON (útil para testar sem API).
//   Atalaia.Agent.exe --dump [--software]
if (args.Contains("--dump"))
{
    var report = new InventoryCollector().Collect(includeSoftware: args.Contains("--software"));
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine(JsonSerializer.Serialize(report, AgentJson.Indented));
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// Config da instalação: o MSI grava %ProgramData%\Atalaia\agent.json (ServerUrl/EnrollToken), sobrepondo o appsettings.json.
var machineConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Atalaia", "agent.json");
builder.Configuration.AddJsonFile(machineConfig, optional: true, reloadOnChange: true);

// Config gravada pelo MSI em HKLM\SOFTWARE\Atalaia. O MSI deixa a chave herdando "Usuários: leitura";
// o próprio serviço (SYSTEM) restringe a ACL a SYSTEM/Administradores para o token não ficar legível por usuário comum.
if (WindowsServiceHelpers.IsWindowsService()) LockDownConfigKey();

// Tem precedência sobre o arquivo.
builder.Configuration.AddInMemoryCollection(ReadRegistryConfig());

builder.Services.AddWindowsService(o => o.ServiceName = "AtalaiaAgent");
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddHttpClient("server", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<InventoryCollector>();
builder.Services.AddSingleton<AgentState>();
builder.Services.AddSingleton<SpeedTester>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<TaskRunner>();

builder.Build().Run();

static List<KeyValuePair<string, string?>> ReadRegistryConfig()
{
    try
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Atalaia");
        return key is null ? [] : key.GetValueNames()
            .Select(n => KeyValuePair.Create<string, string?>($"Agent:{n}", key.GetValue(n)?.ToString()))
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToList();
    }
    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
    {
        // Chave protegida (só SYSTEM/Administradores) e este processo não é um deles, ex.: teste em console sem admin.
        // Segue com appsettings/agent.json em vez de cair.
        Console.Error.WriteLine("Sem permissão para ler HKLM\\SOFTWARE\\Atalaia; usando as outras fontes de configuração.");
        return [];
    }
}

static void LockDownConfigKey()
{
    try
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Atalaia",
            RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadKey);
        if (key is null) return;

        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // corta a herança de "Usuários"
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sid, null), RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        key.SetAccessControl(security);
    }
    catch (Exception ex)
    {
        // Falhar em proteger não deve impedir o agente de funcionar; só registra.
        Console.Error.WriteLine($"Não consegui restringir a ACL da chave de configuração: {ex.Message}");
    }
}
