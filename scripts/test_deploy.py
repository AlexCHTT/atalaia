"""Teste da instalação em massa (varredura, agente embutido, scripts, trabalhos e acompanhamento).

Rode contra um servidor DESCARTÁVEL, subido com:
    Server__AdminUser=ana.souza  Server__AdminPassword=uma-senha-longa-99  Server__EnrollToken=token-de-teste-123  Server__DbPath=<banco novo>
    python scripts/test_deploy.py      (PORT=5090 muda a porta; DB=<mesmo banco, em arquivo local> liga os testes de expiração por horário)
O servidor precisa ter o agente disponível: imagem Docker (embutido) ou publish/agent (desenvolvimento).
A varredura só toca em 127.0.0.x (este computador). O teste NÃO instala nada em lugar nenhum.
"""
import hashlib, http.client, json, os, re, sqlite3, subprocess, sys, datetime, tempfile

BASE = ("127.0.0.1", int(os.environ.get("PORT", "5090")))
H = {"Content-Type": "application/json", "X-Requested-With": "Atalaia"}
AG = {"Content-Type": "application/json", "X-Enroll-Token": "token-de-teste-123"}
fails = 0


def call(method, path, body=None, headers=None, cookie=None, raw=None, timeout=120):
    c = http.client.HTTPConnection(*BASE, timeout=timeout)
    h = dict(headers if headers is not None else H)
    if cookie: h["Cookie"] = cookie
    data = raw if raw is not None else (json.dumps(body) if body is not None else None)
    c.request(method, path, data, h)
    r = c.getresponse(); d = r.read()
    ctype = r.getheader("Content-Type", "")
    j = d
    if ctype.startswith("application/json") and d:
        try: j = json.loads(d)
        except Exception: pass
    elif ctype.startswith("text/"): j = d.decode("utf-8", "replace")
    return r.status, j, r


def check(name, got, want):
    global fails
    ok = got == want
    if not ok: fails += 1
    print(("OK   " if ok else "FAIL ") + name + ("" if ok else f"  (got {got!r}, want {want!r})"))


def login(u, p):
    s, j, r = call("POST", "/api/auth/login", {"username": u, "password": p}); assert s == 200, (s, j)
    return r.getheader("Set-Cookie").split(";")[0]


def wait_scan(ck, timeout=90):
    import time; t0 = time.time()
    while time.time() - t0 < timeout:
        s, j, _ = call("GET", "/api/discovery/scans/latest", cookie=ck)
        if j.get("state") in ("done", "cancelled", "failed"): return j
        time.sleep(0.5)
    return None


adm = login("ana.souza", "uma-senha-longa-99")
for u, role in (("op.um", "operator"),):
    call("POST", "/api/accounts", {"username": u, "displayName": u, "role": role, "password": "outra-senha-longa-77", "mustChangePassword": False}, cookie=adm)
op = login("op.um", "outra-senha-longa-77")

# ---------------- Varredura: validação da faixa ----------------
for bad, why in (("8.8.8.8", "IP público"), ("0.0.0.0/0", "toda a internet"), ("10.0.0.0/8", "grande demais"), ("192.168.0.0/21", "/21 acima do limite"),
                 ("abc", "texto"), ("1.2", "IP incompleto"), ("172.15.255.250-172.16.0.5", "cruza para faixa pública"), ("10.0.0.10-5", "fim antes do começo"),
                 ("192.168.1.0/24; calc", "lixo no fim"), ("", "vazio"), ("10.0.0.1-10.0.5.1", "intervalo grande demais")):
    s, j, _ = call("POST", "/api/discovery/scans", {"range": bad}, cookie=adm)
    check(f"faixa recusada ({why}): {bad!r}", (s, j.get("error") if isinstance(j, dict) else None), (400, "invalid_range"))
s, j, _ = call("POST", "/api/discovery/scans", {"range": "127.0.0.1"}, cookie=op); check("operador não varre a rede (403)", s, 403)
s, j, _ = call("POST", "/api/discovery/scans", {"range": "127.0.0.1"}, headers={"Content-Type": "application/json"}, cookie=adm); check("varredura sem cabeçalho CSRF = 403", s, 403)
s, j, _ = call("POST", "/api/discovery/scans", {"range": "127.0.0.1"}); check("varredura sem login = 401", s, 401)

# ---------------- Varredura: execução e cruzamento com o inventário ----------------
call("POST", "/api/checkin", {"AgentId": "pc-local", "AgentVersion": "1.2.0", "identity": {"hostname": "PC-LOCAL"},
                              "network": [{"name": "loopback", "ipv4": ["127.0.0.1"]}]}, AG)
s, j, _ = call("POST", "/api/discovery/scans", {"range": "127.0.0.1"}, cookie=adm); check("varredura de 1 IP aceita (202)", (s, j["total"]), (202, 1))
r = wait_scan(adm)
check("varredura terminou", r and r["state"], "done")
hosts = r["hosts"] if r else []
check("achou o próprio computador (porta aberta ou recusada)", [h["ip"] for h in hosts], ["127.0.0.1"])
if hosts:
    print(f"     portas abertas em 127.0.0.1: {hosts[0]['openPorts']}")
    check("cruzou com o inventário (já tem agente)", (hosts[0]["agent"] or {}).get("hostname"), "PC-LOCAL")

s, j, _ = call("POST", "/api/discovery/scans", {"range": "127.0.0.0/24"}, cookie=adm); check("varredura de /24 aceita", s, 202)
s2, j2, _ = call("POST", "/api/discovery/scans", {"range": "127.0.0.1"}, cookie=adm); check("segunda varredura ao mesmo tempo = 409", (s2, j2.get("error")), (409, "busy"))
s3, _, _ = call("POST", f"/api/discovery/scans/{j['id']}/cancel", cookie=adm); check("cancelar varredura = 204", s3, 204)
r = wait_scan(adm); check("varredura cancelada", r and r["state"], "cancelled")

# ---------------- Agente embutido ----------------
s, pk, _ = call("GET", "/api/install/package", cookie=adm)
check("agente disponível no servidor", (s, pk.get("present")), (200, True))
print(f"     agente: versão {pk.get('version')}, {pk.get('size', 0) / 1e6:.1f} MB, origem {pk.get('source')}")
sha = pk["sha256"]
s, j, _ = call("GET", "/api/install/package", cookie=op); check("operador não vê a tela do agente (403)", s, 403)
s, body, _ = call("GET", "/api/deploy/package", headers={"X-Enroll-Token": "token-de-teste-123"})
check("download pelo token devolve exatamente o arquivo informado (hash)", (s, hashlib.sha256(body).hexdigest() if isinstance(body, bytes) else None), (200, sha))
check("o arquivo é um executável do Windows (cabeçalho MZ)", body[:2] if isinstance(body, bytes) else None, b"MZ")
s, _, _ = call("GET", "/api/deploy/package", headers={"X-Enroll-Token": "errado"}); check("download com token errado = 401", s, 401)
s, _, _ = call("GET", "/api/deploy/package", headers={}); check("download sem token = 401", s, 401)
s, _, _ = call("PUT", "/api/install/package", headers={"Content-Type": "application/octet-stream", "X-Requested-With": "Atalaia"}, cookie=adm, raw=b"x" * 5000)
check("não existe mais envio de instalador (404/405)", s in (404, 405), True)

# ---------------- Script avulso (um computador / GPO) ----------------
s, sc, r = call("GET", "/api/install/agent-script", cookie=adm)
check("script avulso baixado como anexo", (s, "instalar-agente.ps1" in (r.getheader("Content-Disposition") or "")), (200, True))
CRLF = chr(13) + chr(10)
check("script avulso: ASCII, CRLF e sem marcadores", (sc.isascii(), CRLF in sc and chr(10) not in sc.replace(CRLF, ""), "@@" in sc), (True, True, False))
check("script avulso traz o hash, o servidor e o parâmetro -Force", (sha in sc, "http://127.0.0.1:%d" % BASE[1] in sc, "param([switch]$Force)" in sc), (True, True, True))
sf0 = os.path.join(tempfile.gettempdir(), "instalar-agente-test.ps1"); open(sf0, "w", encoding="ascii", newline="").write(sc)
pr = subprocess.run(["powershell", "-NoProfile", "-Command", f"$e=$null;$t=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('{sf0}',[ref]$t,[ref]$e); if($e.Count){{$e|%{{$_.Message}};exit 1}}else{{'parse-ok'}}"], capture_output=True, text=True)
check("PowerShell aceita a sintaxe do script avulso", (pr.returncode, pr.stdout.strip()), (0, "parse-ok"))
try: os.remove(sf0)
except OSError: pass
s, _, _ = call("GET", "/api/install/agent-script", cookie=op); check("operador não baixa o script avulso (403)", s, 403)
s, _, _ = call("GET", "/api/install/agent-script"); check("script avulso sem login = 401", s, 401)
s, _, _ = call("GET", "/api/install/agent-script?tokenId=nao-existe", cookie=adm); check("token inexistente no script avulso = 400", s, 400)

# ---------------- Trabalhos ----------------
s, j, _ = call("POST", "/api/deploy/jobs", {"targets": [{"ip": "8.8.8.8"}]}, cookie=adm); check("alvo público recusado", (s, j["error"]), (400, "bad_target"))
s, j, _ = call("POST", "/api/deploy/jobs", {"targets": [{"ip": "10.0.0.1; calc"}]}, cookie=adm); check("alvo com lixo recusado", (s, j["error"]), (400, "bad_target"))
s, j, _ = call("POST", "/api/deploy/jobs", {"targets": []}, cookie=adm); check("sem alvos = 400", (s, j["error"]), (400, "no_targets"))
s, j, _ = call("POST", "/api/deploy/jobs", {"targets": [{"ip": f"10.0.{i // 250}.{i % 250 + 1}"} for i in range(501)]}, cookie=adm); check("mais de 500 alvos = 400", (s, j["error"]), (400, "too_many"))
s, j, _ = call("POST", "/api/deploy/jobs", {"targets": [{"ip": "127.0.0.1"}], "tokenId": "nao-existe"}, cookie=adm); check("token inexistente = 400", (s, j["error"]), (400, "no_token"))
s, j, _ = call("POST", "/api/deploy/jobs", {"targets": [{"ip": "127.0.0.1"}]}, cookie=op); check("operador não cria trabalho (403)", s, 403)

targets = [{"ip": "127.0.0.1", "name": "PC-LOCAL"}, {"ip": "127.0.0.2", "name": "evil';calc;'"}, {"ip": "127.0.0.3"}, {"ip": "127.0.0.1"}]
s, job, _ = call("POST", "/api/deploy/jobs", {"targets": targets}, cookie=adm)
check("trabalho criado (201), duplicado ignorado, todos pendentes", (s, len(job["targets"]), {t["status"] for t in job["targets"]}), (201, 3, {"pending"}))
check("nome malicioso foi descartado", [t["name"] for t in job["targets"] if t["ip"] == "127.0.0.2"], [None])
check("a resposta não vaza o segredo do trabalho", "secret" in json.dumps(job).lower(), False)
jid = job["id"]

# ---------------- Script gerado ----------------
s, script, r = call("GET", f"/api/deploy/jobs/{jid}/script", cookie=adm)
check("script baixado como anexo", (s, "attachment" in (r.getheader("Content-Disposition") or "")), (200, True))
check("script é ASCII puro e CRLF", (script.isascii(), "\r\n" in script and "\n" not in script.replace("\r\n", "")), (True, True))
check("nenhum marcador @@ ficou sem substituir", "@@" in script, False)
check("script traz servidor, hash e alvos", ("http://127.0.0.1:%d" % BASE[1] in script, sha in script, "Ip = '127.0.0.1'; Name = 'PC-LOCAL'" in script, "calc" in script), (True, True, True, False))
check("o comando remoto força a reinstalação e deixa log", ("$Force = $true" in script, "Start-Transcript" in script, "New-Service" in script), (True, True, True))
sf = os.path.join(tempfile.gettempdir(), "instalar-atalaia-test.ps1")
open(sf, "w", encoding="ascii", newline="").write(script)
parse = subprocess.run(["powershell", "-NoProfile", "-Command",
    f"$e=$null;$t=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('{sf}',[ref]$t,[ref]$e); if($e.Count){{$e|%{{$_.Message}};exit 1}}else{{'parse-ok'}}"],
    capture_output=True, text=True)
check("PowerShell aceita a sintaxe do script (0 erros)", (parse.returncode, parse.stdout.strip()), (0, "parse-ok"))
dry = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", sf, "-DryRun", "-Only", "127.0.0.1"], capture_output=True, text=True, timeout=120)
print("     -DryRun:", " | ".join(l.strip() for l in dry.stdout.splitlines() if l.strip())[:300])
check("-DryRun roda sem erro e não instala nada", (dry.returncode, "Concluido" in dry.stdout or "pronto para instalar" in dry.stdout or "sem resposta" in dry.stdout), (0, True))
s, j, _ = call("GET", f"/api/deploy/jobs/{jid}", cookie=adm); check("-DryRun não avisou o painel (continua pendente)", {t["status"] for t in j["targets"]}, {"pending"})
secret = re.search(r"\$JobSecret = '([^']+)'", script).group(1)

# ---------------- Progresso e prova de instalação ----------------
P = f"/api/deploy/jobs/{jid}/progress"
HS = lambda sec: {"Content-Type": "application/json", "X-Job-Secret": sec}
s, _, _ = call("POST", P, {"ip": "127.0.0.1", "step": "sent"}, headers={"Content-Type": "application/json"}); check("progresso sem segredo = 401", s, 401)
s, _, _ = call("POST", P, {"ip": "127.0.0.1", "step": "sent"}, headers=HS("segredo-errado")); check("progresso com segredo errado = 401", s, 401)
s, _, _ = call("POST", P, {"ip": "127.0.0.1", "step": "sent"}, headers=AG | {"X-Job-Secret": "x"}); check("o token dos agentes NÃO serve como segredo (401)", s, 401)
s, _, _ = call("POST", P, {"ip": "127.0.0.1", "step": "format c:"}, headers=HS(secret)); check("etapa inválida = 400", s, 400)
s, _, _ = call("POST", P, {"ip": "10.9.9.9", "step": "sent"}, headers=HS(secret)); check("IP que não está no trabalho = 404", s, 404)
s, _, _ = call("POST", "/api/deploy/jobs/inexistente/progress", {"ip": "127.0.0.1", "step": "sent"}, headers=HS(secret)); check("trabalho inexistente = 404", s, 404)
s, _, _ = call("POST", P, {"ip": "127.0.0.3", "step": "failed", "message": "Acesso negado " + "x" * 500}, headers=HS(secret)); check("falha reportada = 204", s, 204)
s, _, _ = call("POST", P, {"ip": "127.0.0.1", "step": "sent", "message": "ok"}, headers=HS(secret)); check("'enviado' reportado = 204", s, 204)
s, _, _ = call("POST", P, {"ip": "127.0.0.2", "step": "sent"}, headers=HS(secret))
s, j, _ = call("GET", f"/api/deploy/jobs/{jid}", cookie=adm)
st = {t["ip"]: t for t in j["targets"]}
check("estados após o script", (st["127.0.0.1"]["status"], st["127.0.0.2"]["status"], st["127.0.0.3"]["status"]), ("sent", "sent", "failed"))
check("mensagem de erro é truncada", len(st["127.0.0.3"]["message"]) <= 300, True)

# o agente do alvo se registra -> instalado (127.0.0.1 é o IP de origem do check-in deste teste)
call("POST", "/api/checkin", {"AgentId": "pc-novo", "AgentVersion": "1.2.0", "identity": {"hostname": "PC-NOVO"}, "network": [{"name": "Ethernet", "ipv4": ["127.0.0.1"]}]}, AG)   # o servidor casa pelo IP do adaptador, não pelo IP de origem (que muda atrás de NAT/Docker)
s, j, _ = call("GET", f"/api/deploy/jobs/{jid}", cookie=adm)
st = {t["ip"]: t for t in j["targets"]}
check("check-in do agente marca como instalado (com o nome real)", (st["127.0.0.1"]["status"], st["127.0.0.1"]["foundHostname"]), ("installed", "PC-NOVO"))
check("o outro alvo 'enviado' (127.0.0.2) continua aguardando", st["127.0.0.2"]["status"], "sent")
s, _, _ = call("POST", P, {"ip": "127.0.0.1", "step": "failed", "message": "tarde demais"}, headers=HS(secret))
s, j, _ = call("GET", f"/api/deploy/jobs/{jid}", cookie=adm)
check("falha tardia não desfaz um 'instalado'", {t["ip"]: t["status"] for t in j["targets"]}["127.0.0.1"], "installed")

# ---------------- Prazos ----------------
if os.environ.get("DB"):   # só dá para mexer nos horários se o teste enxerga o arquivo do banco (não use isso contra um volume de container)
    db = sqlite3.connect(os.environ["DB"]); ago = lambda h: (datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(hours=h)).isoformat()
    db.execute("UPDATE deploy_targets SET sent_at=? WHERE job_id=? AND ip='127.0.0.2'", (ago(0.3), jid)); db.commit()
    s, j, _ = call("GET", f"/api/deploy/jobs/{jid}", cookie=adm)
    check("'enviado' há 18 min sem agente vira timeout", {t["ip"]: t["status"] for t in j["targets"]}["127.0.0.2"], "timeout")

    s, job2, _ = call("POST", "/api/deploy/jobs", {"targets": [{"ip": "127.0.0.5"}]}, cookie=adm)
    s, sc2, _ = call("GET", f"/api/deploy/jobs/{job2['id']}/script", cookie=adm); sec2 = re.search(r"\$JobSecret = '([^']+)'", sc2).group(1)
    db.execute("UPDATE deploy_jobs SET created_at=? WHERE id=?", (ago(25), job2["id"])); db.commit()
    s, _, _ = call("POST", f"/api/deploy/jobs/{job2['id']}/progress", {"ip": "127.0.0.5", "step": "sent"}, headers=HS(sec2)); check("progresso depois de 24 h = 410", s, 410)
    s, _, _ = call("GET", f"/api/deploy/jobs/{job2['id']}/script", cookie=adm); check("script depois de 24 h = 410", s, 410)
    s, j, _ = call("GET", f"/api/deploy/jobs/{job2['id']}", cookie=adm); check("alvo que o script nunca tentou vira timeout", j["targets"][0]["status"], "timeout")
else:
    print("     (sem DB=...: testes de prazo por horário foram pulados)")

# token revogado -> não gera script
s, tk, _ = call("POST", "/api/install/tokens", {"label": "Temporario"}, cookie=adm)
s, job3, _ = call("POST", "/api/deploy/jobs", {"targets": [{"ip": "127.0.0.6"}], "tokenId": tk["id"]}, cookie=adm); check("trabalho com token escolhido", s, 201)
call("DELETE", f"/api/install/tokens/{tk['id']}", cookie=adm)
s, j, _ = call("GET", f"/api/deploy/jobs/{job3['id']}/script", cookie=adm); check("token revogado depois = script recusado (409)", (s, j["error"]), (409, "token_revoked"))

# ---------------- Registro de acessos ----------------
s, log, _ = call("GET", "/api/access-log?limit=100", cookie=adm)
acts = {e["action"] for e in log}
for a in ("deploy_scan", "install_script_downloaded", "deploy_job_created", "deploy_script_downloaded"):
    check(f"registro de acessos tem {a}", a in acts, True)
s, jobs, _ = call("GET", "/api/deploy/jobs", cookie=adm); check("lista de trabalhos recentes", len(jobs) >= (3 if os.environ.get("DB") else 2), True)

print("\nFALHAS:", fails)
sys.exit(1 if fails else 0)
