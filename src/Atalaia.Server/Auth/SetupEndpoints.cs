using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Atalaia.Server;

public sealed record SetupVerifyRequest(string? Code);
public sealed record SetupRequest(string? Code, string? DisplayName, string? Username, string? Password);

/// <summary>
/// Assistente de primeira execução. Enquanto não existe nenhuma conta, o painel abre a tela de configuração, que só aceita
/// quem tiver o CÓDIGO impresso no log do servidor: sem ele, quem chegasse primeiro pela rede viraria o administrador.
/// O código vive só na memória: reiniciar o servidor gera outro.
/// </summary>
public sealed class SetupService(AuthStore auth, ILogger<SetupService> logger)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // sem 0/O/1/I
    private readonly object _lock = new();
    private string _code = NewCode();
    private int _wrongAttempts;

    public bool NeedsSetup => auth.AccountCount() == 0;

    private static string NewCode()
    {
        var c = new char[8];
        for (var i = 0; i < c.Length; i++) c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(c, 0, 4) + "-" + new string(c, 4, 4);
    }

    public void Announce(IEnumerable<string> urls)
    {
        string where;
        lock (_lock) where = _code;
        // "http://[::]:8080" e "http://0.0.0.0:8080" são endereços de escuta (comuns em container), não de navegador
        var shown = urls.Select(u => Regex.Replace(u.TrimEnd('/'), @"//(\[::\]|0\.0\.0\.0|\*|\+)", "//localhost") + "/").Distinct();
        logger.LogWarning("""

            ============================================================
             PRIMEIRA EXECUÇÃO: configure o administrador
             1. Abra no navegador: {Urls}
                (de outro computador, use o endereço deste servidor na rede; no Docker, a porta que você publicou)
             2. Informe este código de configuração:  {Code}
            ============================================================
            """, string.Join("  ou  ", shown), where);
    }

    /// <summary>Confere o código (tempo constante). Depois de 5 erros ele é trocado por outro, para impedir adivinhação.</summary>
    public bool TryCode(string? provided, IEnumerable<string> urls)
    {
        var normalized = new string((provided ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        lock (_lock)
        {
            var expected = _code.Replace("-", "");
            var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(normalized.PadRight(16)), Encoding.UTF8.GetBytes(expected.PadRight(16))) && normalized.Length == expected.Length;
            if (ok) return true;
            if (++_wrongAttempts >= 5)
            {
                _code = NewCode(); _wrongAttempts = 0;
                logger.LogWarning("Muitas tentativas erradas do código de configuração: o código foi trocado.");
                Announce(urls);
            }
            return false;
        }
    }
}

public static class SetupEndpoints
{
    private static readonly Regex UsernameRule = new(@"^[A-Za-z0-9._@-]{3,40}$", RegexOptions.Compiled);
    private static object Err(string code, string message) => new { error = code, message };

    public static void MapSetup(this WebApplication app, AuthOptions opt)
    {
        IEnumerable<string> Urls() => app.Urls.Count > 0 ? app.Urls : ["http://localhost:8080"];

        app.MapGet("/api/setup/status", (SetupService setup) => new { needsSetup = setup.NeedsSetup });

        // Passo 1 da tela: o código está certo? (a resposta só diz sim ou não)
        app.MapPost("/api/setup/verify", (HttpContext http, SetupVerifyRequest body, SetupService setup, LoginThrottle throttle) =>
        {
            if (http.Request.Headers["X-Requested-With"] != "Atalaia") return Results.Json(Err("csrf", "Requisição recusada."), statusCode: 403);
            if (!setup.NeedsSetup) return Results.Json(Err("done", "O painel já foi configurado."), statusCode: 409);
            var ip = http.ClientIp() ?? "?";
            if (throttle.IsBlocked(ip)) return Results.Json(Err("too_many", "Muitas tentativas. Aguarde alguns minutos."), statusCode: 429);
            if (setup.TryCode(body.Code, Urls())) return Results.NoContent();
            throttle.Fail(ip);
            return Results.Json(Err("wrong_code", "Código incorreto. Ele aparece no terminal (ou nos logs do container) onde o servidor foi iniciado."), statusCode: 403);
        });

        // Passo final: cria o administrador, entra com ele e gera o primeiro token de agentes
        app.MapPost("/api/setup", (HttpContext http, SetupRequest body, SetupService setup, AuthStore auth, TokenStore tokens, LoginThrottle throttle) =>
        {
            if (http.Request.Headers["X-Requested-With"] != "Atalaia") return Results.Json(Err("csrf", "Requisição recusada."), statusCode: 403);
            if (!setup.NeedsSetup) return Results.Json(Err("done", "O painel já foi configurado."), statusCode: 409);
            var ip = http.ClientIp() ?? "?";
            if (throttle.IsBlocked(ip)) return Results.Json(Err("too_many", "Muitas tentativas. Aguarde alguns minutos."), statusCode: 429);
            if (!setup.TryCode(body.Code, Urls())) { throttle.Fail(ip); return Results.Json(Err("wrong_code", "Código incorreto."), statusCode: 403); }

            var username = body.Username?.Trim() ?? "";
            var display = body.DisplayName?.Trim() ?? "";
            if (display.Length is < 1 or > 80) return Results.Json(Err("invalid_name", "Informe o seu nome (até 80 caracteres)."), statusCode: 400);
            if (!UsernameRule.IsMatch(username)) return Results.Json(Err("invalid_username", "Usuário: 3 a 40 caracteres entre letras, números, ponto, hífen, sublinhado e @."), statusCode: 400);
            if (PasswordHasher.Validate(body.Password, username) is { } problem) return Results.Json(Err("weak_password", problem), statusCode: 400);

            var account = auth.CreateFirstAdmin(username, display, PasswordHasher.Hash(body.Password!));
            if (account is null) return Results.Json(Err("done", "O painel já foi configurado."), statusCode: 409);   // alguém terminou na frente

            tokens.EnsureOneActive();
            auth.Log(account.Username, "setup_completed", account.Username, "painel configurado pelo assistente de primeira execução", ip);

            // já entra logado
            auth.MarkLogin(account.Id);
            var token = auth.CreateSession(account.Id, ip, http.Request.Headers.UserAgent.ToString(), opt.SessionLifetime);
            http.Response.Cookies.Append(AuthEndpoints.CookieName, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps, Path = "/", IsEssential = true });
            auth.Log(account.Username, "login_ok", null, null, ip);
            return Results.Json(new { id = account.Id, username = account.Username, displayName = account.DisplayName, role = account.Role, mustChangePassword = false });
        });
    }
}
