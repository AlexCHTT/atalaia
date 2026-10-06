using System.Text.Json;

namespace Atalaia.Shared;

/// <summary>Opções de JSON únicas, usadas pelo agente (envio) e pelo servidor (leitura/armazenamento).</summary>
public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
