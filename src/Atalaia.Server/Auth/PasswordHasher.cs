using System.Security.Cryptography;
using System.Text;

namespace Atalaia.Server;

/// <summary>
/// Senhas guardadas com PBKDF2-SHA256 (600 mil iterações, recomendação OWASP) e sal único por senha.
/// Formato: pbkdf2-sha256$iterações$sal$hash (base64). Guardar as iterações junto permite aumentá-las no futuro sem quebrar senhas antigas.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000, SaltBytes = 16, HashBytes = 32;
    public const int MaxLength = 128;
    /// <summary>Definido na tela Configurações (padrão 10). Só vale para senhas novas.</summary>
    public static int MinLength { get; set; } = 10;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Confere a senha em tempo constante. <paramref name="needsRehash"/> indica hash gravado com menos iterações que o padrão atual.</summary>
    public static bool Verify(string password, string stored, out bool needsRehash)
    {
        needsRehash = false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            needsRehash = iterations < Iterations;
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    // Usuário inexistente também gasta o mesmo tempo de um hash: quem tenta adivinhar nomes não distingue "não existe" de "senha errada" pelo tempo de resposta
    private static readonly string Decoy = Hash("decoy-" + Guid.NewGuid());
    public static void BurnTime() => Verify("x", Decoy, out _);

    /// <summary>Mensagem de erro se a senha não serve; null se serve. Segue a lógica de comprimento acima de complexidade (NIST 800-63B).</summary>
    public static string? Validate(string? password, string? username)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return $"A senha precisa ter pelo menos {MinLength} caracteres.";
        if (password.Length > MaxLength) return $"A senha pode ter no máximo {MaxLength} caracteres.";
        if (!string.IsNullOrEmpty(username) && password.Contains(username, StringComparison.OrdinalIgnoreCase)) return "A senha não pode conter o nome de usuário.";
        if (password.Distinct().Count() < 5) return "A senha é repetitiva demais.";
        return null;
    }

    // Sem caracteres ambíguos (0/O, 1/l/I): a senha temporária costuma ser ditada ou copiada à mão
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
    public static string GenerateTemporary(int length = 0)
    {
        length = Math.Max(Math.Max(length, 14), MinLength);   // a senha temporária sempre cumpre a regra vigente
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}
