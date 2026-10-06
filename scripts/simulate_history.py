"""
Simula uma máquina fictícia mudando ao longo do tempo para testar o log de auditoria e o vínculo usuário↔PC.
Uso:  python scripts/simulate_history.py  [http://localhost:5099]
Só mexe na máquina fictícia "sim-pc-0001"; remova pelo painel depois (o histórico dela permanece, por ser auditoria).
"""
import copy, json, sqlite3, sys, time

from _client import Panel

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5099"
ID = "sim-pc-0001"
call = Panel(BASE).call


def checkin(report):
    status, _ = call("POST", "/api/checkin", report, token="dev-enroll-token")
    assert status == 204, f"checkin falhou: {status}"


base = {
    "agentId": ID, "agentVersion": "1.0.1",
    "identity": {"hostname": "SIM-PC", "domain": "empresa.local", "partOfDomain": True, "loggedUser": "EMPRESA\\ana"},
    "os": {"name": "Windows 11 Pro", "build": "26200.100", "activated": True},
    "hardware": {"manufacturer": "ACME", "model": "X1", "serialNumber": "SN123", "cpu": "CPU Z", "ramTotalBytes": 17179869184, "gpus": [], "ramModules": []},
    "network": [{"name": "Ethernet", "mac": "AA:BB:CC:00:00:01", "ipv4": ["10.0.0.10"], "ipv6": [], "gateways": ["10.0.0.1"], "dns": [], "isPrimary": True, "isVirtual": False}],
    "disks": [{"model": "SSD A", "serialNumber": "D1", "sizeBytes": 512000000000}],
    "security": {"antivirusEnabled": True, "firewallPublic": True, "bitLockerOnSystemDrive": None},
    "software": [{"name": "App A", "version": "1.0"}, {"name": "App B", "version": "2.0"}],
}

print("== limpando execucoes anteriores (so a maquina fake)")
call("DELETE", f"/api/machines/{ID}")

print("1) primeiro check-in")
checkin(base)

print("2) check-in identico (nao deve gerar evento nenhum)")
checkin(base)

print("3) mudancas: RAM 16->32GB, hostname, firewall publico desligado, bitlocker desconhecido->true (ignorar), software A atualizado/B removido/C instalado")
r3 = copy.deepcopy(base)
r3["hardware"]["ramTotalBytes"] = 34359738368
r3["identity"]["hostname"] = "SIM-PC-NOVO"
r3["security"]["firewallPublic"] = False
r3["security"]["bitLockerOnSystemDrive"] = True
r3["software"] = [{"name": "App A", "version": "1.1"}, {"name": "App C", "version": "3.0"}]
checkin(r3)

print("4) outro usuario no console")
r4 = copy.deepcopy(r3)
r4["identity"]["loggedUser"] = "EMPRESA\\bruno"
r4["software"] = None  # ciclo sem lista de software: nao pode gerar 'removido'
checkin(r4)

print("5) ninguem no console (tela de login): nao deve abrir vinculo novo")
r5 = copy.deepcopy(r4)
r5["identity"]["loggedUser"] = None
checkin(r5)

_, events = call("GET", f"/api/machines/{ID}/events")
print(f"\n== EVENTOS ({len(events)}), do mais antigo ao mais novo")
for e in reversed(events):
    print(f"  [{e['severity']:4}] {e['type']:18} {str(e['field'] or ''):28} {str(e['oldValue'] or ''):18} -> {e['newValue'] or ''}")

_, assigns = call("GET", f"/api/machines/{ID}/users")
print(f"\n== VINCULOS DO PC ({len(assigns)})")
for a in assigns:
    print(f"  {a['user']:16} atual={a['current']}  host={a['hostname']}")

_, hist = call("GET", "/api/user-history?name=" + urllib.request.quote("EMPRESA\\ana"))
print(f"\n== HISTORICO DO USUARIO ana: {[(h['hostname'], h['current']) for h in hist]}")

print("\n== status: aposentar o PC")
print("  PUT status=retired ->", call("PUT", f"/api/machines/{ID}/status", {"status": "retired"})[0])
print("  PUT status=invalido ->", call("PUT", f"/api/machines/{ID}/status", {"status": "xpto"})[0])

print("\n== offline: envelhecendo last_seen no banco para o vigia (roda a cada 1 min) detectar")
call("PUT", f"/api/machines/{ID}/status", {"status": "active"})
db = sqlite3.connect("src/Atalaia.Server/data/dev.db", timeout=15)
db.execute("UPDATE machines SET last_seen = '2020-01-01T00:00:00.0000000+00:00' WHERE agent_id = ?", (ID,))
db.commit()
for i in range(75):
    time.sleep(1)
    _, ev = call("GET", f"/api/machines/{ID}/events?limit=1")
    if ev and ev[0]["type"] == "offline":
        print(f"  evento offline apareceu apos ~{i + 1}s: {ev[0]['newValue']}")
        break
else:
    print("  FALHOU: vigia nao registrou offline em 75s")

print("  voltando a enviar check-in...")
checkin(r5)
_, ev = call("GET", f"/api/machines/{ID}/events?limit=1")
print(f"  ultimo evento: {ev[0]['type']} / {ev[0]['newValue']}")

print("\n== auditoria e imutavel? tentando UPDATE e DELETE direto no banco")
for sql in ("UPDATE events SET type = 'x' WHERE id = (SELECT MAX(id) FROM events)", "DELETE FROM events WHERE id = (SELECT MAX(id) FROM events)"):
    try:
        db.execute(sql)
        db.commit()
        print("  FALHOU: o banco permitiu:", sql[:30])
    except sqlite3.DatabaseError as e:
        db.rollback()  # sem isso a transação fica aberta e trava as escritas do servidor ("database is locked")
        print(f"  barrado ({sql.split()[0]}): {e}")
db.close()

print("\n== remover o PC do painel: o historico deve permanecer")
print("  DELETE ->", call("DELETE", f"/api/machines/{ID}")[0])
_, ev = call("GET", f"/api/machines/{ID}/events?limit=500")
print(f"  eventos ainda existem: {len(ev)} (ultimo: {ev[0]['type']})")
_, assigns = call("GET", f"/api/machines/{ID}/users")
print(f"  vinculos ainda existem: {len(assigns)}")
