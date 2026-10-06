using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Atalaia.Server;

/// <summary>Limites de sessão e de bloqueio. Lidos da tela Configurações na hora do uso: mudar vale sem reiniciar.</summary>
public sealed class AuthOptions(SettingsStore settings)
{
    public TimeSpan SessionLifetime => TimeSpan.FromHours(settings.Int("security.session_hours"));
    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(settings.Int("security.idle_minutes"));
    public int MaxFailedAttempts => settings.Int("security.max_failed_logins");
    public TimeSpan LockDuration => TimeSpan.FromMinutes(settings.Int("security.lock_minutes"));
}

public sealed record LoginRequest(string? Username, string? Password);
public sealed record ChangePasswordRequest(string? Current, string? New);
public sealed record CreateAccountRequest(string? Username, string? DisplayName, string? Role, string? Password, bool? MustChangePassword);
public sealed record UpdateAccountRequest(string? DisplayName, string? Role, bool? Active);

/// <summary>Freio por IP no login: muitas tentativas erradas seguidas, de contas diferentes ou não, travam aquele endereço por um tempo.</summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 20;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new();

    public bool IsBlocked(string ip)
    {
        if (!_failures.TryGetValue(ip, out var q)) return false;
        lock (q) { Prune(q); return q.Count >= MaxFailures; }
    }

    public void Fail(string ip)
    {
        var q = _failures.GetOrAdd(ip, _ => new());
        lock (q) { Prune(q); q.Enqueue(DateTimeOffset.UtcNow); }
    }

    private static void Prune(Queue<DateTimeOffset> q)
    {
        while (q.Count > 0 && DateTimeOffset.UtcNow - q.Peek() > Window) q.Dequeue();
    }
}

public static class AuthEndpoints
{
    public const string CookieName = "atalaia_session";
    private static readonly Regex UsernameRule = new(@"^[A-Za-z0-9._@-]{3,40}$", RegexOptions.Compiled);

    public static SessionUser? Current(this HttpContext ctx) => ctx.Items["user"] as SessionUser;
    public static string? ClientIp(this HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    private static IResult Err(int status, string code, string message) => Results.Json(new { error = code, message }, statusCode: status);

    // Contra CSRF: além do cookie SameSite=Strict, toda requisição que altera algo precisa deste cabeçalho, que um site de fora
    // não consegue enviar (exigiria uma checagem CORS que o servidor nunca aprova).
    private static bool HasCsrfHeader(HttpContext http) => http.Request.Headers["X-Requested-With"] == "Atalaia";
    private static bool IsReadOnly(HttpRequest r) => HttpMethods.IsGet(r.Method) || HttpMethods.IsHead(r.Method);

    /// <summary>Exige sessão válida e perfil mínimo. Enquanto a pessoa precisa trocar a senha, só as rotas de /api/auth passam.</summary>
    public static TBuilder RequireRole<TBuilder>(this TBuilder builder, string minRole, bool allowDuringPasswordChange = false) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var user = http.Current();
            if (user is null) return Err(401, "unauthenticated", "Faça login para continuar.");
            if (!IsReadOnly(http.Request) && !HasCsrfHeader(http)) return Err(403, "csrf", "Requisição recusada.");
            if (user.MustChangePassword && !allowDuringPasswordChange) return Err(403, "password_change_required", "Defina uma nova senha para continuar.");
            if (Roles.Rank(user.Role) < Roles.Rank(minRole)) return Err(403, "forbidden", "Seu perfil não permite esta ação.");
            return await next(ctx);
        });

    private static object Me(SessionUser u) => new { id = u.AccountId, username = u.Username, displayName = u.DisplayName, role = u.Role, mustChangePassword = u.MustChangePassword };

    private static object Dto(Account a) => new
    {
        a.Id, a.Username, a.DisplayName, a.Role, a.Active, a.MustChangePassword, a.CreatedAt, a.CreatedBy, a.LastLoginAt,
        Locked = a.LockedUntil is not null, a.LockedUntil,
    };

    public static void MapAuth(this WebApplication app, AuthOptions opt)
    {
        // ---------------- Login / sessão ----------------
        app.MapPost("/api/auth/login", (HttpContext http, LoginRequest body, AuthStore auth, LoginThrottle throttle) =>
        {
            if (!HasCsrfHeader(http)) return Err(403, "csrf", "Requisição recusada.");
            var ip = http.ClientIp() ?? "?";
            if (throttle.IsBlocked(ip)) return Err(429, "too_many", "Muitas tentativas deste endereço. Aguarde alguns minutos.");

            var username = body.Username?.Trim() ?? "";
            var password = body.Password ?? "";
            if (username.Length == 0 || password.Length == 0 || username.Length > 80 || password.Length > PasswordHasher.MaxLength)
                return Err(400, "invalid", "Informe usuário e senha.");

            const string invalid = "Usuário ou senha inválidos.";
            if (auth.FindForLogin(username) is not { } found)
            {
                // Nome digitado não vai para o registro: quem erra de campo pode ter colado a senha ali
                PasswordHasher.BurnTime();
                throttle.Fail(ip);
                auth.Log(null, "login_failed", "(usuário inexistente)", null, ip);
                return Err(401, "invalid_credentials", invalid);
            }

            var (account, hash) = found;
            if (account.LockedUntil is { } lockedUntil)
            {
                PasswordHasher.BurnTime();
                auth.Log(account.Username, "login_blocked", null, "conta temporariamente travada", ip);
                return Err(423, "locked", $"Conta travada por excesso de tentativas. Tente de novo às {lockedUntil.ToLocalTime():HH:mm} ou peça a um administrador para redefinir a senha.");
            }

            if (!PasswordHasher.Verify(password, hash, out _))
            {
                throttle.Fail(ip);
                var until = auth.RegisterFailure(account.Id, opt.MaxFailedAttempts, opt.LockDuration);
                auth.Log(account.Username, "login_failed", null, "senha incorreta", ip);
                if (until is { } u)
                {
                    auth.Log(account.Username, "account_locked", null, $"travada por {opt.LockDuration.TotalMinutes:0} min após {opt.MaxFailedAttempts} tentativas", ip);
                    return Err(423, "locked", $"Conta travada por excesso de tentativas. Tente de novo às {u.ToLocalTime():HH:mm}.");
                }
                return Err(401, "invalid_credentials", invalid);
            }

            // Só depois de a senha estar certa dizemos que a conta está desativada (senão daria para descobrir quais nomes existem)
            if (!account.Active)
            {
                auth.Log(account.Username, "login_denied", null, "conta desativada", ip);
                return Err(403, "disabled", "Esta conta está desativada. Fale com um administrador.");
            }

            auth.MarkLogin(account.Id);
            var token = auth.CreateSession(account.Id, ip, http.Request.Headers.UserAgent.ToString(), opt.SessionLifetime);
            http.Response.Cookies.Append(CookieName, token, new CookieOptions
            {
                HttpOnly = true,                         // JavaScript não lê o cookie (um XSS não rouba a sessão)
                SameSite = SameSiteMode.Strict,          // outros sites não conseguem enviá-lo
                Secure = http.Request.IsHttps,           // atrás de proxy com HTTPS, só trafega criptografado
                Path = "/",
                IsEssential = true,
            });
            auth.Log(account.Username, "login_ok", null, null, ip);
            return Results.Json(new { id = account.Id, username = account.Username, displayName = account.DisplayName, role = account.Role, mustChangePassword = account.MustChangePassword });
        });

        app.MapPost("/api/auth/logout", (HttpContext http, AuthStore auth) =>
        {
            if (http.Request.Cookies[CookieName] is { } token) auth.RevokeSession(token);
            auth.Log(http.Current()?.Username, "logout", null, null, http.ClientIp());
            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
            return Results.NoContent();
        }).RequireRole(Roles.Viewer, allowDuringPasswordChange: true);

        app.MapGet("/api/auth/me", (HttpContext http) =>
            http.Current() is { } u ? Results.Json(Me(u)) : Err(401, "unauthenticated", "Faça login para continuar."));

        app.MapPost("/api/auth/change-password", (HttpContext http, ChangePasswordRequest body, AuthStore auth) =>
        {
            var user = http.Current()!;
            if (auth.FindForLogin(user.Username) is not { } found || !PasswordHasher.Verify(body.Current ?? "", found.PasswordHash, out _))
            {
                auth.Log(user.Username, "password_change_failed", null, "senha atual incorreta", http.ClientIp());
                return Err(400, "wrong_current", "A senha atual está incorreta.");
            }
            if (PasswordHasher.Validate(body.New, user.Username) is { } problem) return Err(400, "weak_password", problem);
            if (body.New == body.Current) return Err(400, "same_password", "A nova senha precisa ser diferente da atual.");

            // As outras sessões desta conta caem; a atual continua
            auth.SetPassword(user.AccountId, PasswordHasher.Hash(body.New!), mustChange: false, keepSessionHash: user.SessionHash);
            auth.Log(user.Username, "password_changed", null, null, http.ClientIp());
            return Results.NoContent();
        }).RequireRole(Roles.Viewer, allowDuringPasswordChange: true);

        // ---------------- Gestão de contas (só administrador) ----------------
        app.MapGet("/api/accounts", (AuthStore auth) => auth.List().Select(Dto)).RequireRole(Roles.Admin);

        app.MapPost("/api/accounts", (HttpContext http, CreateAccountRequest body, AuthStore auth) =>
        {
            var admin = http.Current()!;
            var username = body.Username?.Trim() ?? "";
            var display = body.DisplayName?.Trim() ?? "";
            if (!UsernameRule.IsMatch(username)) return Err(400, "invalid_username", "Usuário: 3 a 40 caracteres entre letras, números, ponto, hífen, sublinhado e @.");
            if (display.Length is < 1 or > 80) return Err(400, "invalid_name", "Informe o nome (até 80 caracteres).");
            if (!Roles.IsValid(body.Role)) return Err(400, "invalid_role", "Perfil inválido.");
            if (auth.UsernameExists(username)) return Err(409, "duplicate", "Já existe um usuário com esse nome de acesso.");

            // Sem senha informada: gera uma temporária e obriga a troca no primeiro acesso
            var generated = string.IsNullOrEmpty(body.Password);
            var password = generated ? PasswordHasher.GenerateTemporary() : body.Password!;
            if (!generated && PasswordHasher.Validate(password, username) is { } problem) return Err(400, "weak_password", problem);

            var account = auth.Create(username, display, body.Role!, PasswordHasher.Hash(password), mustChange: generated || (body.MustChangePassword ?? true), createdBy: admin.Username);
            auth.Log(admin.Username, "account_created", username, $"perfil {body.Role}", http.ClientIp());
            return Results.Json(new { account = Dto(account), temporaryPassword = generated ? password : null }, statusCode: 201);
        }).RequireRole(Roles.Admin);

        app.MapPut("/api/accounts/{id}", (HttpContext http, string id, UpdateAccountRequest body, AuthStore auth) =>
        {
            var admin = http.Current()!;
            if (auth.GetById(id) is not { } target) return Err(404, "not_found", "Usuário não encontrado.");
            var display = body.DisplayName?.Trim() ?? "";
            if (display.Length is < 1 or > 80) return Err(400, "invalid_name", "Informe o nome (até 80 caracteres).");
            if (!Roles.IsValid(body.Role)) return Err(400, "invalid_role", "Perfil inválido.");
            var active = body.Active ?? target.Active;

            // Não dá para tirar o próprio acesso por aqui (evita ficar trancado do lado de fora)...
            if (target.Id == admin.AccountId && (body.Role != target.Role || !active))
                return Err(409, "self", "Você não pode alterar o seu próprio perfil nem desativar a própria conta.");
            // ...nem deixar o sistema sem nenhum administrador ativo
            if (target is { Role: Roles.Admin, Active: true } && (body.Role != Roles.Admin || !active) && auth.CountActiveAdmins(excludingId: target.Id) == 0)
                return Err(409, "last_admin", "É preciso manter ao menos um administrador ativo.");

            auth.Update(id, display, body.Role!, active);
            var changes = new List<string>();
            if (display != target.DisplayName) changes.Add($"nome → {display}");
            if (body.Role != target.Role) changes.Add($"perfil {target.Role} → {body.Role}");
            if (active != target.Active) changes.Add(active ? "reativada" : "desativada");
            auth.Log(admin.Username, "account_updated", target.Username, changes.Count > 0 ? string.Join("; ", changes) : "sem mudanças", http.ClientIp());
            return Results.Json(Dto(auth.GetById(id)!));
        }).RequireRole(Roles.Admin);

        app.MapPost("/api/accounts/{id}/reset-password", (HttpContext http, string id, AuthStore auth) =>
        {
            var admin = http.Current()!;
            if (auth.GetById(id) is not { } target) return Err(404, "not_found", "Usuário não encontrado.");
            if (target.Id == admin.AccountId) return Err(409, "self", "Para trocar a sua própria senha use \"Alterar minha senha\" no menu do avatar.");

            var temporary = PasswordHasher.GenerateTemporary();
            auth.SetPassword(id, PasswordHasher.Hash(temporary), mustChange: true);   // também destrava a conta e encerra as sessões dela
            auth.Log(admin.Username, "password_reset", target.Username, "senha temporária gerada", http.ClientIp());
            return Results.Json(new { temporaryPassword = temporary });
        }).RequireRole(Roles.Admin);

        app.MapDelete("/api/accounts/{id}", (HttpContext http, string id, AuthStore auth) =>
        {
            var admin = http.Current()!;
            if (auth.GetById(id) is not { } target) return Err(404, "not_found", "Usuário não encontrado.");
            if (target.Id == admin.AccountId) return Err(409, "self", "Você não pode excluir a própria conta.");
            if (target is { Role: Roles.Admin, Active: true } && auth.CountActiveAdmins(excludingId: target.Id) == 0)
                return Err(409, "last_admin", "É preciso manter ao menos um administrador ativo.");

            auth.Delete(id);
            auth.Log(admin.Username, "account_deleted", target.Username, $"perfil {target.Role}", http.ClientIp());
            return Results.NoContent();
        }).RequireRole(Roles.Admin);

        app.MapGet("/api/access-log", (AuthStore auth, string? actor, string? action, int? limit, long? before) =>
            auth.QueryLog(actor, action, limit ?? 20, before)).RequireRole(Roles.Admin);
    }
}
