namespace Atalaia.Server;

/// <summary>Onde fica o banco de dados. Um único lugar decide, para todos os armazenamentos usarem o mesmo arquivo.</summary>
public static class DbPath
{
    public static string Resolve(IConfiguration config)
    {
        var path = config["Server:DbPath"] ?? "data/atalaia.db";

        // O produto já se chamou AgentTools e o banco, agenttools.db. Se esse arquivo ainda existe e o novo não, continua usando o antigo:
        // quem atualiza não perde dispositivos, auditoria nem contas.
        if (!File.Exists(path))
        {
            var legacy = Path.Combine(Path.GetDirectoryName(path) ?? "", "agenttools.db");
            if (File.Exists(legacy)) return legacy;
        }
        return path;
    }
}
