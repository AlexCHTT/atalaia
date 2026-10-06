using System.Diagnostics;
using System.Security.Cryptography;

namespace Atalaia.Server;

public sealed record PackageInfo(long Size, string Sha256, string Version, string Source);

/// <summary>
/// O agente do Windows (Atalaia.Agent.exe, arquivo único) que os computadores baixam deste servidor. Na imagem Docker ele já vem
/// embutido (construído junto com o servidor, em /app/agent): ninguém precisa gerar nem enviar instalador.
/// Em desenvolvimento, usa o que estiver em publish/agent (resultado do dotnet publish do agente).
/// O hash é calculado aqui e vai dentro dos scripts: o computador de destino confere antes de instalar.
/// </summary>
public sealed class PackageStore
{
    private readonly object _lock = new();
    private readonly string[] _candidates;
    private (string Path, long Length, DateTime Stamp, PackageInfo Info)? _cache;

    public PackageStore(IConfiguration config, IHostEnvironment env)
    {
        var list = new List<string>();
        if (config["Server:AgentPackagePath"] is { Length: > 0 } configured) list.Add(configured);
        list.Add(Path.Combine(AppContext.BaseDirectory, "agent", "Atalaia.Agent.exe"));   // imagem Docker
        foreach (var start in new[] { env.ContentRootPath, AppContext.BaseDirectory })        // desenvolvimento: sobe as pastas procurando publish/agent
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
                list.Add(Path.Combine(dir.FullName, "publish", "agent", "Atalaia.Agent.exe"));
        }
        _candidates = list.ToArray();
    }

    private string? FindFile() => _candidates.FirstOrDefault(File.Exists);

    public PackageInfo? Info()
    {
        lock (_lock)
        {
            var path = FindFile();
            if (path is null) return null;
            var fi = new FileInfo(path);
            if (_cache is { } c && c.Path == path && c.Length == fi.Length && c.Stamp == fi.LastWriteTimeUtc) return c.Info;   // não recalcula o hash a cada chamada

            string hash;
            using (var fs = File.OpenRead(path)) hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            var source = path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) ? "embutido" : "desenvolvimento";
            var info = new PackageInfo(fi.Length, hash, VersionOf(path), source);
            _cache = (path, fi.Length, fi.LastWriteTimeUtc, info);
            return info;
        }
    }

    public FileStream? OpenRead() => FindFile() is { } p ? new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true) : null;

    private static string VersionOf(string path)
    {
        try
        {
            var side = Path.Combine(Path.GetDirectoryName(path)!, "version.txt");
            if (File.Exists(side) && File.ReadAllText(side).Trim() is { Length: > 0 } v) return v;
            return FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0] ?? "desconhecida";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "desconhecida"; }
    }
}
