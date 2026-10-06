"""Teste do teste de velocidade (fila de tarefas, permissões, limites e execução pelo agente).

Rode contra um servidor DESCARTÁVEL, subido com:
    Server__AdminUser=ana.souza  Server__AdminPassword=uma-senha-longa-99  Server__EnrollToken=token-de-teste-123
e depois, em duas etapas:
    python scripts/test_speedtest.py prepare      # cria máquina e contas, confere permissões e limites, deixa um teste pendente
    (suba um agente apontando para o servidor, com AgentId "test-agent-1")
    python scripts/test_speedtest.py run          # espera o agente executar e confere o resultado (server e internet)
PORT=5094 muda a porta. NÃO rode em um agente instalado de verdade: ele usaria o agent.json da máquina e falaria com o servidor real.
"""
import http.client, json, os, sys, time

BASE = ("127.0.0.1", int(os.environ.get("PORT", "5094")))
TOKEN = "token-de-teste-123"
AGENT = "test-agent-1"
H = {"Content-Type": "application/json", "X-Requested-With": "Atalaia"}
fails = 0


def call(method, path, body=None, headers=None, cookie=None, raw=None):
    c = http.client.HTTPConnection(*BASE, timeout=60)
    h = dict(headers if headers is not None else H)
    if cookie: h["Cookie"] = cookie
    data = raw if raw is not None else (json.dumps(body) if body is not None else None)
    c.request(method, path, data, h)
    r = c.getresponse(); d = r.read()
    try: j = json.loads(d) if d and r.getheader("Content-Type", "").startswith("application/json") else d
    except Exception: j = d
    return r.status, j, r


def check(name, got, want):
    global fails
    ok = got == want
    if not ok: fails += 1
    print(("OK   " if ok else "FAIL ") + name + ("" if ok else f"  (got {got!r}, want {want!r})"))


def login(user, pw):
    s, j, r = call("POST", "/api/auth/login", {"username": user, "password": pw})
    assert s == 200, (s, j)
    return r.getheader("Set-Cookie").split(";")[0]


AG = {"Content-Type": "application/json", "X-Enroll-Token": TOKEN}


def prepare():
    s, _, _ = call("POST", "/api/checkin", {"AgentId": AGENT, "AgentVersion": "t", "identity": {"hostname": "PC-TESTE"}}, AG)
    check("máquina de teste registrada (check-in)", s, 204)
    admin = login("ana.souza", "uma-senha-longa-99")
    for u, role in (("op.um", "operator"), ("leitor.um", "viewer")):
        s, j, _ = call("POST", "/api/accounts", {"username": u, "displayName": u, "role": role, "password": "outra-senha-longa-77", "mustChangePassword": False}, cookie=admin)
        check(f"conta {u} criada", s, 201)
    op, viewer = login("op.um", "outra-senha-longa-77"), login("leitor.um", "outra-senha-longa-77")

    # --- limites e autenticação dos endpoints do agente ---
    s, _, _ = call("POST", "/api/agent/poll", {"AgentId": AGENT}, {"Content-Type": "application/json"}); check("poll sem token = 401", s, 401)
    s, _, _ = call("POST", "/api/agent/poll", {"AgentId": AGENT}, {"Content-Type": "application/json", "X-Enroll-Token": "errado"}); check("poll com token errado = 401", s, 401)
    s, _, _ = call("GET", "/api/agent/speedtest/down?bytes=1000", headers={}); check("download sem token = 401", s, 401)
    s, _, _ = call("GET", "/api/agent/speedtest/ping", headers={"X-Enroll-Token": TOKEN}); check("ping com token = 200", s, 200)
    s, body, _ = call("GET", "/api/agent/speedtest/down?bytes=1000000", headers={"X-Enroll-Token": TOKEN}); check("download 1 MB devolve 1 MB", (s, len(body)), (200, 1_000_000))
    s, body, _ = call("GET", "/api/agent/speedtest/down?bytes=999999999999", headers={"X-Enroll-Token": TOKEN}); check("download é limitado a 64 MB", (s, len(body)), (200, 64 * 1024 * 1024))
    s, j, _ = call("POST", "/api/agent/speedtest/up", headers={"Content-Type": "application/octet-stream", "X-Enroll-Token": TOKEN}, raw=b"x" * 524288); check("upload de 512 KB confirmado", (s, j and j.get("bytes")), (200, 524288))
    try:
        s, j, _ = call("POST", "/api/agent/speedtest/up", headers={"Content-Type": "application/octet-stream", "X-Enroll-Token": TOKEN}, raw=b"x" * (9 * 1024 * 1024)); got = s
    except Exception:
        got = "conexão encerrada"
    check("upload de 9 MB (acima de 8 MB) é recusado", got in (413, "conexão encerrada"), True)
    s, _, _ = call("POST", f"/api/agent/tasks/inexistente/result", {"AgentId": AGENT, "Ok": False, "Error": "x"}, AG); check("resultado de tarefa inexistente = 404", s, 404)

    # --- painel: permissões e validações ---
    s, _, _ = call("POST", f"/api/machines/{AGENT}/speedtest", {"target": "server"}, headers={"Content-Type": "application/json"}); check("pedir teste sem login = 401", s, 401)
    s, j, _ = call("POST", f"/api/machines/{AGENT}/speedtest", {"target": "server"}, cookie=viewer); check("leitor não pede teste = 403", s, 403)
    s, j, _ = call("GET", f"/api/machines/{AGENT}/speedtests", cookie=viewer); check("leitor pode ver o histórico = 200", s, 200)
    s, j, _ = call("POST", f"/api/machines/{AGENT}/speedtest", {"target": "http://evil.example/"}, cookie=op); check("destino arbitrário = 400", (s, j["error"]), (400, "invalid_target"))
    s, j, _ = call("POST", "/api/machines/nao-existe/speedtest", {"target": "server"}, cookie=op); check("máquina inexistente = 404", s, 404)
    s, j, _ = call("POST", f"/api/machines/{AGENT}/speedtest", {"target": "server"}, headers={"Content-Type": "application/json"}, cookie=op); check("sem cabeçalho CSRF = 403", s, 403)

    s, j, _ = call("POST", f"/api/machines/{AGENT}/speedtest", {"target": "server"}, cookie=op); check("operador pede teste = 202 pendente", (s, j["status"], j["requestedBy"]), (202, "pending", "op.um"))
    s, j, _ = call("POST", f"/api/machines/{AGENT}/speedtest", {"target": "internet"}, cookie=op); check("segundo pedido enquanto há um ativo = 409", (s, j["error"]), (409, "busy"))
    s, j, _ = call("GET", "/api/features", cookie=op); check("features informa internet", j, {"speedtestInternet": True})
    s, j, _ = call("GET", "/api/access-log?limit=20", cookie=admin); check("pedido entrou no registro de acessos", any(e["action"] == "speedtest_requested" and e["actor"] == "op.um" for e in j), True)
    print("\nPreparado: há 1 teste pendente (server). Suba o agente e rode:  python scripts/test_speedtest.py run")


def wait_done(cookie, timeout=90):
    t0 = time.time()
    while time.time() - t0 < timeout:
        s, j, _ = call("GET", f"/api/machines/{AGENT}/speedtests?limit=1", cookie=cookie)
        if j and j[0]["status"] in ("done", "failed", "expired"): return j[0]
        time.sleep(2)
    return None


def run():
    op = login("op.um", "outra-senha-longa-77")
    t = wait_done(op)
    check("teste até o servidor terminou", t and t["status"], "done")
    if t and t["status"] == "done":
        r = t["result"]; print(f"     servidor: ↓ {r['downMbps']} Mbps  ↑ {r['upMbps']} Mbps  latência {r['latencyMs']} ms  jitter {r['jitterMs']} ms  ({r['bytesDown']/1e6:.0f} MB / {r['bytesUp']/1e6:.0f} MB, {r['seconds']} s)")
        check("resultado coerente (>0 e alvo certo)", (r["downMbps"] > 0, r["upMbps"] > 0, r["target"]), (True, True, "server"))
        check("tarefa mostra quando começou e terminou", (t["startedAt"] is not None, t["completedAt"] is not None), (True, True))
    elif t: print("     ", t.get("error"))

    s, j, _ = call("POST", f"/api/machines/{AGENT}/speedtest", {"target": "internet"}, cookie=op); check("pede teste até a internet = 202", s, 202)
    t = wait_done(op, 120)
    check("teste até a internet terminou", t and t["status"], "done")
    if t and t["status"] == "done":
        r = t["result"]; print(f"     internet: ↓ {r['downMbps']} Mbps  ↑ {r['upMbps']} Mbps  latência {r['latencyMs']} ms  jitter {r['jitterMs']} ms  ({r['bytesDown']/1e6:.0f} MB / {r['bytesUp']/1e6:.0f} MB, {r['seconds']} s)")
    elif t: print("      status:", t["status"], "erro:", t.get("error"))

    s, j, _ = call("GET", f"/api/machines/{AGENT}/speedtests?limit=10", cookie=op)
    check("histórico lista os 2 testes, mais novo primeiro", (len(j), j[0]["target"]), (2, "internet"))
    print("\nFALHAS:", fails)
    sys.exit(1 if fails else 0)


{"prepare": prepare, "run": run}[sys.argv[1]]()
if sys.argv[1] == "prepare":
    print("FALHAS:", fails); sys.exit(1 if fails else 0)
