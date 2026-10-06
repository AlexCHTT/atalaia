"""
Teste de regressão da autenticação, dos perfis e do registro de acessos.

  python scripts/test_auth.py [http://localhost:5096] [caminho-do-banco-de-teste.db]

Suba ANTES um servidor com banco descartável (nunca o banco de verdade), por exemplo:
  Server__DbPath=data/auth-test.db ASPNETCORE_URLS=http://localhost:5096 ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile
O administrador inicial é o de appsettings.Development.json (admin / dev-senha-123456).
Se o caminho do banco for informado, o teste também confirma que o registro de acessos é imutável.
"""
import http.cookiejar, json, sqlite3, sys, urllib.error, urllib.request

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5096"
DB = sys.argv[2] if len(sys.argv) > 2 else None
ADMIN, ADMIN_PW = "admin", "dev-senha-123456"
FAILS = []


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


class Client:
    """Navegador mínimo: guarda cookies e manda o cabeçalho anti-CSRF nas requisições que alteram algo."""
    def __init__(self, csrf=True):
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar), NoRedirect)
        self.csrf = csrf

    def call(self, method, path, body=None, headers=None, raw=False):
        h = {"Content-Type": "application/json", **(headers or {})}
        if self.csrf and method not in ("GET", "HEAD"):
            h.setdefault("X-Requested-With", "Atalaia")
        req = urllib.request.Request(BASE + path, method=method, headers=h, data=json.dumps(body).encode() if body is not None else None)
        try:
            r = self.opener.open(req)
            status, data, hdrs = r.status, r.read(), r.headers
        except urllib.error.HTTPError as e:
            status, data, hdrs = e.code, e.read(), e.headers
        if raw:
            return status, data, hdrs
        try:
            return status, (json.loads(data) if data else None)
        except ValueError:
            return status, data

    def login(self, user, pw):
        return self.call("POST", "/api/auth/login", {"username": user, "password": pw})


def check(name, cond, extra=""):
    print(("  ok   " if cond else "  FALHA"), name, ("-> " + str(extra)) if (extra and not cond) else "")
    if not cond:
        FAILS.append(name)


def section(t):
    print(f"\n== {t}")


# ------------------------------------------------------------------ 1) acesso anônimo
section("1) Sem login")
anon = Client()
s, _, h = anon.call("GET", "/", raw=True)
check("'/' redireciona para o login", s == 302 and h.get("Location") == "/login.html", (s, h.get("Location")))
check("/login.html é público", anon.call("GET", "/login.html", raw=True)[0] == 200)
check("/css/app.css é público (a tela de login usa)", anon.call("GET", "/css/app.css", raw=True)[0] == 200)
check("API sem login -> 401", anon.call("GET", "/api/machines")[0] == 401)
check("/healthz é público", anon.call("GET", "/healthz", raw=True)[0] == 200)
check("check-in de agente sem token -> 401", anon.call("POST", "/api/checkin", {"agentId": "x", "agentVersion": "1"})[0] == 401)
check("check-in com token errado -> 401", anon.call("POST", "/api/checkin", {"agentId": "x", "agentVersion": "1"}, headers={"X-Enroll-Token": "errado"})[0] == 401)
s, _, h = anon.call("GET", "/api/machines", raw=True)
check("cabeçalhos de segurança (CSP, nosniff, no-store)", "default-src 'self'" in h.get("Content-Security-Policy", "") and h.get("X-Content-Type-Options") == "nosniff" and h.get("Cache-Control") == "no-store", dict(h))

# ------------------------------------------------------------------ 2) login
section("2) Login")
c = Client(csrf=False)
check("login sem cabeçalho anti-CSRF -> 403", c.login(ADMIN, ADMIN_PW)[0] == 403)
c = Client()
s1, b1 = c.login(ADMIN, "senha-errada-123")
s2, b2 = Client().login("fulano-que-nao-existe", "qualquer-coisa-123")
check("senha errada -> 401", s1 == 401, (s1, b1))
check("usuário inexistente -> 401 com a MESMA mensagem (não revela quem existe)", s2 == 401 and b1["message"] == b2["message"], (b1, b2))
s, b, h = c.call("POST", "/api/auth/login", {"username": ADMIN, "password": ADMIN_PW}, raw=True)
cookie = h.get("Set-Cookie", "")
check("login correto -> 200", s == 200, (s, b))
check("cookie: HttpOnly + SameSite=Strict", "httponly" in cookie.lower() and "samesite=strict" in cookie.lower(), cookie)
check("cookie sem Secure em HTTP puro (com HTTPS+proxy confiável teria)", "secure" not in cookie.lower())
s, me = c.call("GET", "/api/auth/me")
check("/api/auth/me devolve o administrador", s == 200 and me["role"] == "admin" and me["username"] == ADMIN, me)
s, _, h = c.call("GET", "/login.html", raw=True)
check("logado, /login.html redireciona para o painel", s == 302 and h.get("Location") == "/", (s, h.get("Location")))
check("logado, '/' abre o painel", c.call("GET", "/", raw=True)[0] == 200)

# ------------------------------------------------------------------ 3) CSRF
section("3) CSRF")
nocsrf = Client(csrf=False)
nocsrf.jar = c.jar
nocsrf.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(c.jar), NoRedirect)
check("POST autenticado sem o cabeçalho -> 403", nocsrf.call("POST", "/api/accounts", {"username": "x"})[0] == 403)
check("GET autenticado sem cabeçalho funciona", nocsrf.call("GET", "/api/machines")[0] == 200)

# ------------------------------------------------------------------ 4) contas e validações
section("4) Gestão de contas (administrador)")
s, b = c.call("POST", "/api/accounts", {"username": "ana", "displayName": "Ana Operadora", "role": "operator"})
check("cria conta sem senha -> 201 e gera senha temporária", s == 201 and b.get("temporaryPassword") and b["account"]["mustChangePassword"] is True, (s, b))
ana_tmp = b["temporaryPassword"]
lista = json.dumps(c.call("GET", "/api/accounts")[1])
check("a senha temporária e o hash não aparecem na lista de contas", ana_tmp not in lista and "pbkdf2" not in lista and "passwordHash" not in lista)
check("usuário duplicado (ignorando maiúsculas) -> 409", c.call("POST", "/api/accounts", {"username": "ANA", "displayName": "x", "role": "viewer"})[0] == 409)
check("senha curta -> 400", c.call("POST", "/api/accounts", {"username": "bia", "displayName": "Bia", "role": "viewer", "password": "curta"})[0] == 400)
check("nome de usuário inválido -> 400", c.call("POST", "/api/accounts", {"username": "a b", "displayName": "x", "role": "viewer"})[0] == 400)
check("perfil inválido -> 400", c.call("POST", "/api/accounts", {"username": "carlos", "displayName": "Carlos", "role": "root"})[0] == 400)
s, b = c.call("POST", "/api/accounts", {"username": "leo", "displayName": "Leo Leitor", "role": "viewer", "password": "SenhaForte-123", "mustChangePassword": False})
check("cria leitor com senha própria", s == 201 and b["temporaryPassword"] is None, (s, b))
leo_id = b["account"]["id"]
ana_id = next(a["id"] for a in c.call("GET", "/api/accounts")[1] if a["username"] == "ana")
admin_id = me["id"]

# ------------------------------------------------------------------ 5) troca obrigatória de senha
section("5) Primeiro acesso com senha temporária")
ana = Client()
s, b = ana.login("ana", ana_tmp)
check("login com a temporária -> mustChangePassword", s == 200 and b["mustChangePassword"] is True, (s, b))
s, b = ana.call("GET", "/api/machines")
check("com troca pendente, a API de dados fica bloqueada (403)", s == 403 and b["error"] == "password_change_required", (s, b))
check("...mas /api/auth/me funciona", ana.call("GET", "/api/auth/me")[0] == 200)
check("troca com senha atual errada -> 400", ana.call("POST", "/api/auth/change-password", {"current": "errada", "new": "NovaSenha-2026!"})[0] == 400)
check("troca para senha fraca -> 400", ana.call("POST", "/api/auth/change-password", {"current": ana_tmp, "new": "123"})[0] == 400)
check("troca para senha com o nome de usuário -> 400", ana.call("POST", "/api/auth/change-password", {"current": ana_tmp, "new": "xxana-xxana-1"})[0] == 400)
check("troca válida -> 204", ana.call("POST", "/api/auth/change-password", {"current": ana_tmp, "new": "NovaSenha-2026!"})[0] == 204)
check("depois da troca a API libera", ana.call("GET", "/api/machines")[0] == 200)

# ------------------------------------------------------------------ 6) matriz de perfis
section("6) Perfis: o que cada um pode")
# um dispositivo para testar status/remoção/exportação
anon.call("POST", "/api/checkin", {"agentId": "auth-test-pc", "agentVersion": "1.1.0", "identity": {"hostname": "PC-TESTE-AUTH", "loggedUser": "T\\\\u"}, "os": {"name": "Windows 11 Pro"}, "hardware": {"ramTotalBytes": 8589934592}},
          headers={"X-Enroll-Token": "dev-enroll-token"})
leo = Client(); leo.login("leo", "SenhaForte-123")
# colunas: ler, exportar CSV, mudar status, remover dispositivo, gerir contas
WANT = {"viewer (leo)": (200, 403, 403, 403, 403), "operator (ana)": (200, 200, 204, 403, 403), "admin": (200, 200, 204, 204, 200)}
for name, cl in (("viewer (leo)", leo), ("operator (ana)", ana), ("admin", c)):
    got = (
        cl.call("GET", "/api/machines")[0],
        cl.call("GET", "/api/events/export", raw=True)[0],
        cl.call("PUT", "/api/machines/auth-test-pc/status", {"status": "stock"})[0],
        None if name == "admin" else cl.call("DELETE", "/api/machines/auth-test-pc")[0],   # a remoção do admin vem logo abaixo, por último
        cl.call("GET", "/api/accounts")[0],
    )
    want = WANT[name]
    ok = all(g == w for g, w in zip(got, want) if g is not None)
    check(f"{name:15} ler={got[0]} exportar={got[1]} status={got[2]} remover={got[3] if got[3] else '(abaixo)'} contas={got[4]}", ok, f"esperado {want}")
check("admin remove o dispositivo -> 204", c.call("DELETE", "/api/machines/auth-test-pc")[0] == 204)

# ------------------------------------------------------------------ 7) proteções
section("7) Proteções contra ficar sem acesso")
check("admin não troca o próprio perfil -> 409", c.call("PUT", f"/api/accounts/{admin_id}", {"displayName": "Admin", "role": "viewer", "active": True})[0] == 409)
check("admin não desativa a própria conta -> 409", c.call("PUT", f"/api/accounts/{admin_id}", {"displayName": "Admin", "role": "admin", "active": False})[0] == 409)
check("admin não exclui a própria conta -> 409", c.call("DELETE", f"/api/accounts/{admin_id}")[0] == 409)
check("admin não redefine a própria senha por ali -> 409", c.call("POST", f"/api/accounts/{admin_id}/reset-password")[0] == 409)
s, b = c.call("POST", "/api/accounts", {"username": "adm2", "displayName": "Segundo Admin", "role": "admin", "password": "OutraSenha-1234", "mustChangePassword": False})
adm2_id = b["account"]["id"]
adm2 = Client(); adm2.login("adm2", "OutraSenha-1234")
check("o 2º admin NÃO consegue rebaixar o 1º se isso zerasse os admins ativos? (há 2: pode)", adm2.call("PUT", f"/api/accounts/{admin_id}", {"displayName": "Administrador", "role": "operator", "active": True})[0] == 200)
check("...mas agora que adm2 é o único admin, não pode ser rebaixado nem por si -> 409", adm2.call("PUT", f"/api/accounts/{adm2_id}", {"displayName": "x", "role": "viewer", "active": True})[0] == 409)
# devolve o perfil ao primeiro admin (usa uma sessão nova dele, pois o perfil mudou e a sessão antiga caiu)
check("sessão antiga do admin caiu ao ter o perfil alterado", c.call("GET", "/api/auth/me")[0] == 401)
check("2º admin restaura o 1º admin", adm2.call("PUT", f"/api/accounts/{admin_id}", {"displayName": "Administrador", "role": "admin", "active": True})[0] == 200)
c = Client(); c.login(ADMIN, ADMIN_PW)

# ------------------------------------------------------------------ 8) bloqueio por tentativas
section("8) Bloqueio por tentativas erradas")
victim = Client()
codes = [victim.login("leo", f"errada-{i}-xxxxxxx")[0] for i in range(5)]
check("4 erros -> 401 e o 5º trava a conta -> 423", codes == [401, 401, 401, 401, 423], codes)
s, b = Client().login("leo", "SenhaForte-123")
check("conta travada: nem a senha certa entra (423)", s == 423, (s, b))
leo_row = next(a for a in c.call("GET", "/api/accounts")[1] if a["username"] == "leo")
check("a lista de contas mostra 'travada'", leo_row["locked"] is True, leo_row)
s, b = c.call("POST", f"/api/accounts/{leo_id}/reset-password")
leo_tmp = b["temporaryPassword"]
check("admin redefine a senha -> 200 com senha temporária", s == 200 and leo_tmp, (s, b))
s, b = Client().login("leo", leo_tmp)
check("redefinir também destrava a conta", s == 200 and b["mustChangePassword"] is True, (s, b))
check("a sessão antiga do leo caiu com a redefinição", leo.call("GET", "/api/machines")[0] == 401)

# ------------------------------------------------------------------ 9) sessões
section("9) Desativar / excluir / sair")
leo2 = Client(); leo2.login("leo", leo_tmp)
leo2.call("POST", "/api/auth/change-password", {"current": leo_tmp, "new": "TerceiraSenha-777"})
check("leo logado lê dados", leo2.call("GET", "/api/machines")[0] == 200)
check("admin desativa o leo -> 200", c.call("PUT", f"/api/accounts/{leo_id}", {"displayName": "Leo Leitor", "role": "viewer", "active": False})[0] == 200)
check("a sessão do leo caiu na hora", leo2.call("GET", "/api/machines")[0] == 401)
s, b = Client().login("leo", "TerceiraSenha-777")
check("conta desativada: com a senha certa avisa 'desativada' (403)", s == 403 and b["error"] == "disabled", (s, b))
check("mas com senha errada continua dizendo só 'inválidos' (não revela)", Client().login("leo", "errada-aaaaaaaa")[0] == 401)
check("admin exclui o leo -> 204", c.call("DELETE", f"/api/accounts/{leo_id}")[0] == 204)
check("logout -> 204 e a sessão some", (c2 := Client(), c2.login(ADMIN, ADMIN_PW), c2.call("POST", "/api/auth/logout")[0])[2] == 204 and c2.call("GET", "/api/auth/me")[0] == 401)

# ------------------------------------------------------------------ 11) registro de acessos
section("10) Registro de acessos")
c = Client()
s, b = c.login(ADMIN, ADMIN_PW)
if s == 200:
    s, log = c.call("GET", "/api/access-log?limit=500")
    actions = {e["action"] for e in log}
    for need in ("bootstrap", "login_ok", "login_failed", "account_locked", "account_created", "account_updated", "password_reset", "account_deleted", "password_changed", "device_status", "device_removed", "events_export", "logout"):
        check(f"registrou '{need}'", need in actions or (need == "bootstrap" and any(a in actions for a in ("bootstrap",))), sorted(actions))
    check("o log nunca guarda o texto do usuário inexistente", all("nao-existe" not in (e.get("target") or "") for e in log))
    check("só administrador lê o log", Client().call("GET", "/api/access-log")[0] == 401)
else:
    check("login do admin para ler o registro", False, s)



if DB:
    section("11) Imutabilidade do registro e armazenamento (lê o banco de teste direto)")
    con = sqlite3.connect(DB, timeout=15)
    for sql in ("UPDATE access_log SET action = 'x'", "DELETE FROM access_log"):
        try:
            con.execute(sql); con.commit(); check(f"banco barra '{sql.split()[0]}'", False)
        except sqlite3.DatabaseError as e:
            con.rollback(); check(f"banco barra '{sql.split()[0]}'", "append-only" in str(e), e)
    hashes = [r[0] for r in con.execute("SELECT password_hash FROM accounts")]
    check("senhas gravadas só como hash PBKDF2", all(h.startswith("pbkdf2-sha256$600000$") for h in hashes), hashes[:1])
    toks = [r[0] for r in con.execute("SELECT token_hash FROM sessions")]
    check("sessões guardam só o hash do token (64 hex)", all(len(t) == 64 for t in toks), toks[:1])
    con.close()

# ------------------------------------------------------------------ 12) freio por IP (por último: bloqueia o endereço do teste por 10 min)
section("12) Freio por IP")
ip = Client()
last = [ip.login(f"nao-existe-{i}", "senha-qualquer-123")[0] for i in range(24)]
check("depois de ~20 falhas seguidas o endereço leva 429", 429 in last, last)

print("\n" + ("TUDO CERTO" if not FAILS else f"{len(FAILS)} FALHA(S): " + "; ".join(FAILS)))
sys.exit(1 if FAILS else 0)
