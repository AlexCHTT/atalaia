"""Teste da tela Configurações: valida cada parâmetro, e principalmente se mudar um valor muda o comportamento de verdade.

Rode contra um servidor DESCARTÁVEL, subido com um banco novo e:
    Server__AdminUser=ana.souza  Server__AdminPassword=uma-senha-longa-99  Server__EnrollToken=token-de-teste-123  Server__OfflineAfterMinutes=33
    DB=<mesmo banco, em arquivo local> PORT=5088 python scripts/test_settings.py
(Server__OfflineAfterMinutes=33 é usado para provar que a variável antiga continua valendo como valor inicial.)
O teste mexe no relógio das linhas do banco para simular dados antigos: nunca use contra um banco de verdade.
"""
import datetime, http.client, json, os, sqlite3, sys

BASE = ("127.0.0.1", int(os.environ.get("PORT", "5088")))
DB = os.environ["DB"]
H = {"Content-Type": "application/json", "X-Requested-With": "Atalaia"}
AG = {"Content-Type": "application/json", "X-Enroll-Token": "token-de-teste-123"}
fails = 0


def call(method, path, body=None, headers=None, cookie=None):
    c = http.client.HTTPConnection(*BASE, timeout=60)
    h = dict(headers if headers is not None else H)
    if cookie: h["Cookie"] = cookie
    c.request(method, path, json.dumps(body) if body is not None else None, h)
    r = c.getresponse(); d = r.read()
    try: j = json.loads(d) if d else None
    except Exception: j = d
    return r.status, j, r


def check(name, got, want):
    global fails
    ok = got == want
    if not ok: fails += 1
    print(("OK   " if ok else "FAIL ") + name + ("" if ok else f"  (got {got!r}, want {want!r})"))


def login(u, p):
    s, j, r = call("POST", "/api/auth/login", {"username": u, "password": p})
    return (r.getheader("Set-Cookie") or "").split(";")[0] if s == 200 else None


adm = login("ana.souza", "uma-senha-longa-99")
put = lambda changes, confirm=False, ck=None: call("PUT", "/api/settings", {"changes": changes, "confirm": confirm}, cookie=ck or adm)
items = lambda: {i["key"]: i for i in call("GET", "/api/settings", cookie=adm)[1]["items"]}
ago = lambda days: (datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(days=days)).isoformat()
db = sqlite3.connect(DB)

# ---------------- Acesso e catálogo ----------------
s, cat, _ = call("GET", "/api/settings", cookie=adm)
check("catálogo traz grupos, parâmetros e dados do sistema", (s, len(cat["groups"]) >= 6, len(cat["items"]) >= 20, "serverVersion" in cat["system"]), (200, True, True, True))
s, j, _ = call("POST", "/api/accounts", {"username": "op.um", "displayName": "Op", "role": "operator", "password": "outra-senha-longa-77", "mustChangePassword": False}, cookie=adm)
op = login("op.um", "outra-senha-longa-77")
for path, m in (("/api/settings", "GET"), ("/api/system/storage", "GET")):
    check(f"operador não acessa {path} (403)", call(m, path, cookie=op)[0], 403)
check("operador não grava configurações (403)", call("PUT", "/api/settings", {"changes": {"brand.company_name": "X"}}, cookie=op)[0], 403)
check("operador não aplica retenção (403)", call("POST", "/api/system/purge-now", cookie=op)[0], 403)
check("configurações sem login = 401", call("GET", "/api/settings", headers={})[0], 401)
check("gravar sem cabeçalho CSRF = 403", call("PUT", "/api/settings", {"changes": {"brand.company_name": "X"}}, headers={"Content-Type": "application/json"}, cookie=adm)[0], 403)
s, pub, _ = call("GET", "/api/public/info", headers={})
check("identidade pública abre sem login", (s, sorted(pub)), (200, ["company", "minPasswordLength"]))

# ---------------- Origem dos valores: padrão, variável antiga, definido aqui ----------------
it = items()
check("variável de ambiente antiga vale como valor inicial (33 min)", (it["agents.offline_after_minutes"]["value"], it["agents.offline_after_minutes"]["source"]), ("33", "config"))
check("sem nada definido, vale o padrão", (it["retention.metrics_days"]["value"], it["retention.metrics_days"]["source"]), ("30", "default"))
s, j, _ = put({"agents.offline_after_minutes": "10"})
check("gravar muda o valor e a origem para 'db'", (s, j["applied"], items()["agents.offline_after_minutes"]["source"], items()["agents.offline_after_minutes"]["value"]), (200, 1, "db", "10"))
s, j, _ = put({"agents.offline_after_minutes": ""})
check("restaurar padrão volta ao valor da variável antiga (33), não ao de fábrica", (items()["agents.offline_after_minutes"]["value"], items()["agents.offline_after_minutes"]["source"]), ("33", "config"))

# ---------------- Validação (tudo ou nada) ----------------
bad = {"retention.metrics_days": "0", "agents.checkin_minutes": "abc", "health.disk_warn_pct": "100", "security.min_password_length": "7",
       "brand.company_name": "x" * 61, "speedtest.internet_enabled": "talvez", "parametro.inexistente": "1", "storage.alert_mb": "-5"}
s, j, _ = put(bad)
check("valores inválidos são recusados (400) e cada erro aponta o campo", (s, sorted(j["errors"])), (400, sorted(bad)))
check("nada foi gravado quando há erro (tudo ou nada)", items()["retention.metrics_days"]["source"], "default")
s, j, _ = put({"brand.company_name": "Bad\x01Name"}); check("caractere de controle no texto é recusado", s, 400)
s, j, _ = put({"health.disk_warn_pct": "92", "health.disk_crit_pct": "90"}); check("atenção >= crítico é recusado (regra entre campos)", (s, "health.disk_crit_pct" in j["errors"]), (400, True))
check("corpo vazio = 400", put({})[0], 400)

# ---------------- Efeito real: senha mínima ----------------
s, _, _ = put({"security.min_password_length": "14"})
check("identidade pública reflete a senha mínima", call("GET", "/api/public/info", headers={})[1]["minPasswordLength"], 14)
s, j, _ = call("POST", "/api/accounts", {"username": "curta.um", "displayName": "C", "role": "viewer", "password": "doze-chars-1", "mustChangePassword": False}, cookie=adm)
check("senha de 12 caracteres é recusada com mínimo 14", s, 400)
s, j, _ = call("POST", "/api/accounts", {"username": "longa.um", "displayName": "L", "role": "viewer", "password": "quatorze-chars-1", "mustChangePassword": False}, cookie=adm)
check("senha de 16 caracteres é aceita", s, 201)
s, j, _ = call("POST", "/api/accounts", {"username": "temp.um", "displayName": "T", "role": "viewer"}, cookie=adm)
check("senha temporária gerada cumpre o mínimo vigente", (s, len(j.get("temporaryPassword", "")) >= 14), (201, True))
put({"security.min_password_length": ""})
check("restaurar: senha de 12 caracteres volta a ser aceita", call("POST", "/api/accounts", {"username": "doze.um", "displayName": "D", "role": "viewer", "password": "doze-chars-ok1", "mustChangePassword": False}, cookie=adm)[0], 201)

# ---------------- Efeito real: bloqueio por tentativas ----------------
put({"security.max_failed_logins": "3"})
codes = [call("POST", "/api/auth/login", {"username": "longa.um", "password": "errada-errada-1"})[0] for _ in range(3)]
check("com limite 3, a 3ª senha errada trava a conta (423)", codes, [401, 401, 423])
put({"security.max_failed_logins": ""})
check("conta travada continua travada com a senha certa (423)", call("POST", "/api/auth/login", {"username": "longa.um", "password": "quatorze-chars-1"})[0], 423)

# ---------------- Efeito real: tempo de offline ----------------
call("POST", "/api/checkin", {"AgentId": "pc-off", "AgentVersion": "1.2.0", "identity": {"hostname": "PC-OFF"}, "network": []}, AG)
db.execute("UPDATE machines SET last_seen=? WHERE agent_id='pc-off'", ((datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(minutes=15)).isoformat(),)); db.commit()
online = lambda: next(m["online"] for m in call("GET", "/api/machines", cookie=adm)[1] if m["agentId"] == "pc-off")
check("sem check-in há 15 min com limite de 33 min: online", online(), True)
put({"agents.offline_after_minutes": "10"})
check("limite 10 min: a mesma máquina passa a offline, sem reiniciar", online(), False)
put({"agents.offline_after_minutes": ""})

# ---------------- Efeito real: limites do índice de saúde ----------------
rep = {"AgentId": "pc-disco", "AgentVersion": "1.2.0", "identity": {"hostname": "PC-DISCO"}, "volumes": [{"letter": "C:", "sizeBytes": 1000, "freeBytes": 150}], "network": []}
call("POST", "/api/checkin", rep, AG)
issues = lambda: [i["code"] for i in next(m["issues"] for m in call("GET", "/api/machines", cookie=adm)[1] if m["agentId"] == "pc-disco")]
check("disco 85% ocupado com atenção a partir de 80%: problema 'disk_high'", "disk_high" in issues(), True)
s, j, _ = put({"health.disk_warn_pct": "88", "health.disk_crit_pct": "95"})
check("mudar os limites recalcula os dispositivos na hora", (s, j["recalculated"] >= 2), (200, True))
check("com atenção a partir de 88%, o problema some", "disk_high" in issues(), False)
put({"health.disk_warn_pct": "", "health.disk_crit_pct": ""})
check("restaurar: o problema volta", "disk_high" in issues(), True)

# ---------------- Efeito real: marca e teste de velocidade ----------------
put({"brand.company_name": "Prefeitura de Teste"})
check("nome da empresa aparece na identidade pública", call("GET", "/api/public/info", headers={})[1]["company"], "Prefeitura de Teste")
put({"brand.company_name": ""})
check("nome da empresa pode ser esvaziado", call("GET", "/api/public/info", headers={})[1]["company"], "")
put({"speedtest.internet_enabled": "false"})
check("internet desligada: /api/features reflete", call("GET", "/api/features", cookie=adm)[1], {"speedtestInternet": False})
s, j, _ = call("POST", "/api/machines/pc-off/speedtest", {"target": "internet"}, cookie=adm); check("internet desligada: pedir teste = 400 'disabled'", (s, j["error"]), (400, "disabled"))
put({"speedtest.internet_enabled": ""})
check("restaurar: internet volta a ser permitida", call("GET", "/api/features", cookie=adm)[1], {"speedtestInternet": True})

# ---------------- Efeito real: tamanho da varredura ----------------
put({"deploy.max_scan_hosts": "16"})
s, j, _ = call("POST", "/api/discovery/scans", {"range": "127.0.0.0/24"}, cookie=adm); check("varredura de 254 endereços recusada com limite 16", (s, j["error"]), (400, "invalid_range"))
s, j, _ = call("POST", "/api/discovery/scans", {"range": "127.0.0.1-127.0.0.8"}, cookie=adm); check("varredura de 8 endereços aceita com limite 16", s, 202)
put({"deploy.max_scan_hosts": ""})

# ---------------- Configuração empurrada aos agentes ----------------
poll = lambda: call("POST", "/api/agent/poll", {"AgentId": "pc-off"}, AG)[1]
check("sem nada definido aqui, o agente não recebe configuração (mantém a dele)", poll().get("config"), None)
put({"agents.checkin_minutes": "2", "speedtest.max_mb": "50"})
check("o que foi definido aqui chega ao agente (e só isso)", poll().get("config"), {"checkinMinutes": 2, "softwareEveryHours": None, "speedtestMaxMb": 50})
put({"agents.checkin_minutes": "", "speedtest.max_mb": ""})
check("restaurar padrão: o agente volta a não receber configuração", poll().get("config"), None)

# ---------------- Retenção: métricas ----------------
for k in range(12): db.execute("INSERT INTO metrics (agent_id, at, cpu, ram_pct, disk_pct) VALUES ('pc-off', ?, 1, 1, 1)", (ago(40 + k),))
db.commit()
s, j, _ = put({"retention.metrics_days": "20"})
check("reduzir a retenção que apagaria dados exige confirmação (409) e mostra o impacto", (s, j["error"], j["impact"][0]["rows"] >= 12), (409, "confirmation_required", True))
check("sem confirmar, nada foi gravado nem apagado", (items()["retention.metrics_days"]["source"], db.execute("SELECT COUNT(*) FROM metrics WHERE at < ?", (ago(30),)).fetchone()[0] >= 12), ("default", True))
s, j, _ = put({"retention.metrics_days": "20"}, confirm=True)
check("com confirmação grava e apaga agora", (s, j["purged"]["metrics"] >= 12, db.execute("SELECT COUNT(*) FROM metrics WHERE at < ?", (ago(20),)).fetchone()[0]), (200, True, 0))
put({"retention.metrics_days": ""})

# ---------------- Retenção: auditoria dos dispositivos ----------------
db.execute("DROP TRIGGER IF EXISTS events_no_update")
db.execute("UPDATE events SET at=? WHERE agent_id='pc-disco'", (ago(100),)); db.commit()
n_old = db.execute("SELECT COUNT(*) FROM events WHERE at < ?", (ago(60),)).fetchone()[0]
db.execute("CREATE TRIGGER IF NOT EXISTS events_no_update BEFORE UPDATE ON events BEGIN SELECT RAISE(ABORT, 'events e append-only'); END"); db.commit()
check("há eventos antigos para o teste", n_old > 0, True)
s, j, _ = put({"retention.events_days": "60"}); check("prazo de auditoria que apagaria eventos pede confirmação", (s, j["error"]), (409, "confirmation_required"))
check("0 = guardar para sempre nunca apaga nem pede confirmação", put({"retention.events_days": "0"})[0], 200)
s, j, _ = put({"retention.events_days": "60"}, confirm=True)
check("confirmado: eventos antigos apagados", (s, j["purged"]["events"] >= n_old), (200, True))
check("a limpeza deixou um evento 'retention_purge' que não foi apagado", db.execute("SELECT COUNT(*) FROM events WHERE type='retention_purge'").fetchone()[0] >= 1, True)
check("a proteção contra DELETE voltou (append-only)", "RAISE" in (db.execute("SELECT sql FROM sqlite_master WHERE name='events_no_delete'").fetchone() or [""])[0], True)
put({"retention.events_days": ""})

# ---------------- Retenção: registro de acessos ----------------
db.execute("DROP TRIGGER IF EXISTS access_log_no_update")
db.execute("UPDATE access_log SET at=? WHERE id IN (SELECT id FROM access_log ORDER BY id LIMIT 3)", (ago(200),)); db.commit()
db.execute("CREATE TRIGGER IF NOT EXISTS access_log_no_update BEFORE UPDATE ON access_log BEGIN SELECT RAISE(ABORT, 'access_log e append-only'); END"); db.commit()
s, j, _ = put({"retention.access_log_days": "90"}); check("prazo do registro de acessos pede confirmação", (s, j["error"]), (409, "confirmation_required"))
s, j, _ = put({"retention.access_log_days": "90"}, confirm=True); check("confirmado: registros antigos apagados", (s, j["purged"]["accessLog"] >= 3), (200, True))
check("a limpeza deixou 'retention_purge' no registro de acessos", db.execute("SELECT COUNT(*) FROM access_log WHERE action='retention_purge'").fetchone()[0] >= 1, True)
check("a proteção do registro de acessos voltou", "RAISE" in (db.execute("SELECT sql FROM sqlite_master WHERE name='access_log_no_delete'").fetchone() or [""])[0], True)
put({"retention.access_log_days": ""})

# ---------------- Retenção: histórico de testes de velocidade ----------------
for _ in range(4):
    s, t, _ = call("POST", "/api/machines/pc-off/speedtest", {"target": "server"}, cookie=adm)
    call("POST", "/api/machines/pc-off/speedtest/cancel", cookie=adm)
check("há 4 testes no histórico", len(call("GET", "/api/machines/pc-off/speedtests?limit=50", cookie=adm)[1]) >= 4, True)
s, j, _ = put({"retention.speedtests_keep": "1"}); check("guardar menos testes pede confirmação e mostra o impacto", (s, j["impact"][0]["rows"] >= 3), (409, True))
s, j, _ = put({"retention.speedtests_keep": "1"}, confirm=True)
check("confirmado: fica só 1 teste por máquina", (s, len(call("GET", "/api/machines/pc-off/speedtests?limit=50", cookie=adm)[1])), (200, 1))
put({"retention.speedtests_keep": ""})

# ---------------- Uso de disco ----------------
s, st = call("GET", "/api/system/storage", cookie=adm)[:2]
tbl = {t["name"]: t for t in st["tables"]}
check("relatório de disco: tamanho do banco, tabelas conhecidas e crescimento", (s, st["dbBytes"] > 0, {"machines", "events", "metrics", "access_log"} <= set(tbl), isinstance(st["growth"], list)), (200, True, True, True))
check("contagem de registros bate com o banco", (tbl["machines"]["rows"], tbl["events"]["rows"]), (db.execute("SELECT COUNT(*) FROM machines").fetchone()[0], db.execute("SELECT COUNT(*) FROM events").fetchone()[0]))
put({"storage.alert_mb": "1"})
st = call("GET", "/api/system/storage", cookie=adm)[1]
check("limite de aviso configurado (1 MB) e coerente com o tamanho", (st["alertBytes"], st["overLimit"]), (1048576, st["dbBytes"] + st["walBytes"] > 1048576))
put({"storage.alert_mb": ""})
check("sem limite, o aviso fica desligado", call("GET", "/api/system/storage", cookie=adm)[1]["alertBytes"], None)

# ---------------- Manutenção manual e registro ----------------
s, j, _ = call("POST", "/api/system/purge-now", cookie=adm); check("'aplicar retenção agora' responde com as contagens", (s, sorted(j)), (200, ["accessLog", "events", "metrics", "speedTests"]))
s, j, _ = call("POST", "/api/system/recalculate-health", cookie=adm); check("'recalcular saúde' responde (200)", (s, j["recalculated"] >= 2), (200, True))
log = call("GET", "/api/access-log?limit=500", cookie=adm)[1]
chg = [e for e in log if e["action"] == "setting_changed"]
check("cada mudança ficou no registro de acessos, com antes e depois", any(e["target"] == "Considerar offline após" and e["detail"] == "33 → 10" for e in chg), True)
check("restaurar também fica registrado", any(e["target"] == "Considerar offline após" and e["detail"] == "10 → 33" for e in chg), True)
check("limpezas manuais ficam no registro", {"purge_now", "health_recalculated"} <= {e["action"] for e in log}, True)

print("\nFALHAS:", fails)
sys.exit(1 if fails else 0)
