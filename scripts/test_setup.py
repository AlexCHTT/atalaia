"""Teste do assistente de primeira execução e dos tokens de agentes.
Rode contra um servidor DESCARTÁVEL, recém-criado e sem contas (banco novo, sem Server__AdminPassword), na porta de PORT (padrão 5097):
    python scripts/test_setup.py CODIGO-DO-LOG        (PORT=5095 para outra porta; o código aparece no log do servidor)
Ele cria contas e tokens: nunca aponte para o servidor de verdade."""
import http.client, json, sys, re

import os
BASE = ("127.0.0.1", int(os.environ.get("PORT", "5097")))
CODE = sys.argv[1]
H = {"Content-Type": "application/json", "X-Requested-With": "Atalaia"}
cookie = None
fails = 0

def call(method, path, body=None, headers=None, cookie_val=None):
    c = http.client.HTTPConnection(*BASE)
    h = dict(headers if headers is not None else H)
    if cookie_val: h["Cookie"] = cookie_val
    c.request(method, path, json.dumps(body) if body is not None else None, h)
    r = c.getresponse(); data = r.read().decode()
    try: j = json.loads(data) if data else None
    except Exception: j = data
    return r.status, j, r

def check(name, got, want):
    global fails
    ok = got == want
    if not ok: fails += 1
    print(("OK   " if ok else "FAIL ") + name + ("" if ok else f"  (got {got!r}, want {want!r})"))

# antes da configuraÃ§Ã£o
s, j, r = call("GET", "/", headers={})
check("/ redireciona para o assistente", (s, r.getheader("Location")), (302, "/setup.html"))
s, j, _ = call("GET", "/login.html", headers={}); check("/login.html tambÃ©m", s, 302)
s, j, _ = call("GET", "/setup.html", headers={}); check("/setup.html abre", s, 200)
s, j, _ = call("GET", "/api/setup/status", headers={}); check("status needsSetup", j, {"needsSetup": True})
s, j, _ = call("GET", "/api/machines", headers={}); check("API do painel fechada (401)", s, 401)
s, j, _ = call("POST", "/api/checkin", {"AgentId": "x", "AgentVersion": "t"}, {"Content-Type": "application/json", "X-Enroll-Token": "qualquer"}); check("checkin sem token cadastrado = 401", s, 401)
s, j, _ = call("POST", "/api/setup/verify", {"code": CODE}, {"Content-Type": "application/json"}); check("verify sem cabeÃ§alho CSRF = 403", s, 403)
s, j, _ = call("POST", "/api/setup", {"code": "ZZZZ-ZZZZ", "displayName": "A", "username": "root.adm", "password": "uma-senha-longa-99"}); check("setup com cÃ³digo errado = 403", (s, j["error"]), (403, "wrong_code"))
s, j, _ = call("POST", "/api/setup/verify", {"code": CODE.lower()}); check("verify com cÃ³digo certo (minÃºsculas, aceito) = 204", s, 204)

# validaÃ§Ãµes do administrador
s, j, _ = call("POST", "/api/setup", {"code": CODE, "displayName": "Ana", "username": "ana.souza", "password": "curta"}); check("senha curta = 400", (s, j["error"]), (400, "weak_password"))
s, j, _ = call("POST", "/api/setup", {"code": CODE, "displayName": "Ana", "username": "ana.souza", "password": "ana.souza-123456"}); check("senha com o usuÃ¡rio = 400", (s, j["error"]), (400, "weak_password"))
s, j, _ = call("POST", "/api/setup", {"code": CODE, "displayName": "Ana", "username": "a b", "password": "uma-senha-longa-99"}); check("usuÃ¡rio invÃ¡lido = 400", (s, j["error"]), (400, "invalid_username"))
s, j, _ = call("POST", "/api/setup", {"code": CODE, "displayName": "", "username": "ana.souza", "password": "uma-senha-longa-99"}); check("nome vazio = 400", (s, j["error"]), (400, "invalid_name"))
s, j, _ = call("GET", "/api/setup/status", headers={}); check("ainda precisa configurar apÃ³s erros", j, {"needsSetup": True})

# criaÃ§Ã£o
s, j, r = call("POST", "/api/setup", {"code": CODE, "displayName": "Ana Souza", "username": "ana.souza", "password": "uma-senha-longa-99"})
check("setup ok = 200 admin", (s, j and j.get("role")), (200, "admin"))
sc = r.getheader("Set-Cookie") or ""
check("cookie HttpOnly + SameSite=Strict", ("httponly" in sc.lower(), "samesite=strict" in sc.lower()), (True, True))
cookie = sc.split(";")[0]

s, j, _ = call("GET", "/api/setup/status", headers={}); check("needsSetup=false", j, {"needsSetup": False})
s, j, _ = call("POST", "/api/setup", {"code": CODE, "displayName": "Outro", "username": "outro.adm", "password": "uma-senha-longa-99"}); check("segundo setup = 409", s, 409)
s, j, _ = call("POST", "/api/setup/verify", {"code": CODE}); check("verify depois de pronto = 409", s, 409)
s, j, r = call("GET", "/setup.html", headers={}, cookie_val=cookie); check("/setup.html logado redireciona p/ /", (s, r.getheader("Location")), (302, "/"))
s, j, r = call("GET", "/setup.html", headers={}); check("/setup.html deslogado vai p/ login", (s, r.getheader("Location")), (302, "/login.html"))
s, j, _ = call("GET", "/api/auth/me", headers={}, cookie_val=cookie); check("sessÃ£o do setup funciona (me)", (s, j and j.get("username")), (200, "ana.souza"))

# tokens
s, j, _ = call("GET", "/api/install/info", headers={}); check("install/info sem login = 401", s, 401)
s, j, _ = call("GET", "/api/install/info", headers={}, cookie_val=cookie)
check("install/info: 1 token ativo gerado", (s, len(j["tokens"]), j["tokens"][0]["active"], j["tokens"][0]["token"].startswith("atl_")), (200, 1, True, True))
check("serverUrl correto", j["serverUrl"], f"http://127.0.0.1:{BASE[1]}")
tok1 = j["tokens"][0]["token"]

s, j, _ = call("POST", "/api/checkin", {"AgentId": "", "AgentVersion": "t"}, {"Content-Type": "application/json", "X-Enroll-Token": tok1}); check("token vÃ¡lido passa (400 AgentId vazio)", s, 400)
s, j, _ = call("POST", "/api/checkin", {"AgentId": "", "AgentVersion": "t"}, {"Content-Type": "application/json", "X-Enroll-Token": tok1 + "x"}); check("token errado = 401", s, 401)

s, j, _ = call("POST", "/api/install/tokens", {"label": ""}, cookie_val=cookie); check("token sem nome = 400", s, 400)
s, j, _ = call("POST", "/api/install/tokens", {"label": "Filial Centro"}, headers={"Content-Type": "application/json"}, cookie_val=cookie); check("criar token sem CSRF = 403", s, 403)
s, j, _ = call("POST", "/api/install/tokens", {"label": "Filial Centro"}, cookie_val=cookie); check("criar token = 201", s, 201)
tok2, id2 = j["token"], j["id"]
check("tokens distintos", tok1 != tok2, True)
s, j, _ = call("POST", "/api/checkin", {"AgentId": "", "AgentVersion": "t"}, {"Content-Type": "application/json", "X-Enroll-Token": tok2}); check("token novo vale jÃ¡ (400)", s, 400)
s, j, _ = call("DELETE", f"/api/install/tokens/{id2}", cookie_val=cookie); check("revogar = 204", s, 204)
s, j, _ = call("POST", "/api/checkin", {"AgentId": "", "AgentVersion": "t"}, {"Content-Type": "application/json", "X-Enroll-Token": tok2}); check("token revogado = 401 na hora", s, 401)
s, j, _ = call("POST", "/api/checkin", {"AgentId": "", "AgentVersion": "t"}, {"Content-Type": "application/json", "X-Enroll-Token": tok1}); check("o outro token segue valendo", s, 400)
s, j, _ = call("DELETE", f"/api/install/tokens/{id2}", cookie_val=cookie); check("revogar de novo = 404", s, 404)

# uso registrado
s, j, _ = call("GET", "/api/install/info", headers={}, cookie_val=cookie)
t1 = [t for t in j["tokens"] if t["token"] == tok1][0]
check("use_count e Ãºltimo uso do token 1", (t1["useCount"] >= 2, t1["lastUsedAt"] is not None), (True, True))

# perfis: leitor nÃ£o vÃª tokens
s, j, _ = call("POST", "/api/accounts", {"username": "leitor.um", "displayName": "Leitor", "role": "viewer", "password": "outra-senha-longa-77", "mustChangePassword": False}, cookie_val=cookie); check("criar leitor", s, 201)
s, j, r = call("POST", "/api/auth/login", {"username": "leitor.um", "password": "outra-senha-longa-77"}); lc = (r.getheader("Set-Cookie") or "").split(";")[0]
s, j, _ = call("GET", "/api/install/info", headers={}, cookie_val=lc); check("leitor nÃ£o acessa install/info (403)", s, 403)
s, j, _ = call("DELETE", f"/api/install/tokens/{id2}", cookie_val=lc); check("leitor nÃ£o revoga (403)", s, 403)

# registro de acessos
s, j, _ = call("GET", "/api/access-log?limit=50", headers={}, cookie_val=cookie)
acts = [e["action"] for e in j]
for a in ("setup_completed", "token_created", "token_revoked"):
    check(f"access_log tem {a}", a in acts, True)

print("\nFALHAS:", fails)
sys.exit(1 if fails else 0)


