"""
Gera uma frota FICTÍCIA para demonstrar/testar o painel: ~40 PCs com perfis de segurança variados, usuários, histórico de
14 dias, métricas de 7 dias, periféricos e ferramentas de IA.

  python scripts/seed_demo.py --url http://localhost:5098 --token dev-enroll-token --db src/Atalaia.Server/data/demo.db

ATENÇÃO: use SEMPRE contra um servidor e um banco SEPARADOS (ex.: Server__DbPath=data/demo.db). A auditoria é imutável:
dados fictícios jogados no banco de verdade ficam lá. Com --db, o script retroage datas e cria histórico escrevendo direto no
SQLite (solta e recria o gatilho de imutabilidade, algo que só se faz num banco de demonstração).
Os IDs começam com "demo-"; os dados são sorteados com semente fixa (mesma frota a cada execução).
"""
import argparse, datetime as dt, json, math, random, sqlite3, uuid

from _client import Panel

ap = argparse.ArgumentParser()
ap.add_argument("--url", default="http://localhost:5098")
ap.add_argument("--token", default="dev-enroll-token")
ap.add_argument("--admin", default="admin:dev-senha-123456", help="usuário:senha de um administrador do servidor de demonstração")
ap.add_argument("--db", help="caminho do SQLite do servidor de demonstração (habilita histórico, métricas e datas retroativas)")
ap.add_argument("--count", type=int, default=40)
args = ap.parse_args()

rnd = random.Random(7)
NOW = dt.datetime.now(dt.timezone.utc)
iso = lambda t: t.strftime("%Y-%m-%dT%H:%M:%S.0000000+00:00")
GB = 1024 ** 3

# ---------------------------------------------------------------- HTTP
_user, _, _password = args.admin.partition(":")
_panel = Panel(args.url, _user, _password)


def call(method, path, body=None, token=None):
    return _panel.call(method, path, body, token)[0]   # só o status interessa aqui

# ---------------------------------------------------------------- catálogo
FIRST = ["Ana", "Bruno", "Carla", "Diego", "Elisa", "Felipe", "Gabriela", "Henrique", "Isabela", "João", "Karen", "Lucas", "Marina", "Nicolas",
         "Olívia", "Pedro", "Quésia", "Rafael", "Sofia", "Thiago", "Úrsula", "Vinícius", "Wesley", "Yasmin", "Zeca"]
LAST = ["Souza", "Lima", "Oliveira", "Santos", "Pereira", "Costa", "Ribeiro", "Almeida", "Carvalho", "Gomes", "Martins", "Rocha", "Barbosa", "Araújo"]
strip = lambda s: s.lower().replace("á", "a").replace("ã", "a").replace("é", "e").replace("ê", "e").replace("í", "i").replace("ó", "o").replace("ú", "u").replace("ç", "c")
users = []
while len(users) < args.count + 8:
    u = f"EMPRESA\\{strip(rnd.choice(FIRST))}.{strip(rnd.choice(LAST))}"
    if u not in users:
        users.append(u)

LAPTOPS = [("Dell", "Latitude 5440"), ("Dell", "Latitude 3540"), ("Lenovo", "ThinkPad E14 Gen 5"), ("Lenovo", "ThinkPad L14"), ("HP", "ProBook 450 G10"),
           ("HP", "EliteBook 840 G9"), ("Acer", "Aspire 5 A515"), ("ASUS", "Vivobook 15")]
DESKTOPS = [("Dell", "OptiPlex 3000"), ("Dell", "OptiPlex 7010"), ("Lenovo", "ThinkCentre M70s"), ("HP", "EliteDesk 800 G6"), ("Positivo", "Master D3400")]
CPUS = ["13th Gen Intel(R) Core(TM) i5-1335U", "12th Gen Intel(R) Core(TM) i5-12400", "Intel(R) Core(TM) i7-1355U", "11th Gen Intel(R) Core(TM) i5-1135G7",
        "AMD Ryzen 5 5600U", "Intel(R) Core(TM) i3-10105", "AMD Ryzen 7 5700U", "10th Gen Intel(R) Core(TM) i5-10210U"]
OSES = [("Microsoft Windows 11 Pro", "10.0.26100", "26100", 0.52), ("Microsoft Windows 11 Pro", "10.0.22631", "22631", 0.2),
        ("Microsoft Windows 10 Pro", "10.0.19045", "19045", 0.22), ("Microsoft Windows 11 Home", "10.0.26100", "26100", 0.06)]
SOFT = [("Google Chrome", "129.0.6668.90", "Google LLC"), ("Mozilla Firefox", "131.0", "Mozilla"), ("Microsoft 365 Apps", "16.0.18025", "Microsoft Corporation"),
        ("Adobe Acrobat Reader", "24.003.20112", "Adobe"), ("7-Zip", "24.08", "Igor Pavlov"), ("Zoom Workplace", "6.2.5", "Zoom"),
        ("Microsoft Teams", "24243.1309", "Microsoft Corporation"), ("VLC media player", "3.0.21", "VideoLAN"), ("Notepad++", "8.7.1", "Notepad++ Team"),
        ("WinRAR", "7.01", "win.rar GmbH"), ("Java 8 Update 421", "8.0.4210", "Oracle Corporation")]
EXTRA = [("AnyDesk", "9.0.4", "philandro Software"), ("TeamViewer", "15.58", "TeamViewer"), ("Visual Studio Code", "1.94.0", "Microsoft Corporation"),
         ("Spotify", "1.2.48", "Spotify AB"), ("WhatsApp", "2.24.20", "WhatsApp")]
AI_TOOLS = [("Claude (Anthropic)", 0.30), ("ChatGPT (OpenAI)", 0.22), ("Microsoft Copilot", 0.14), ("Cursor", 0.06), ("Ollama", 0.03), ("Perplexity", 0.04)]
USB_VENDORS = [("046D", "C534", "Mouse", "Dispositivo de entrada USB"), ("046D", "C52B", "Mouse", "Receptor Unifying"), ("413C", "2113", "Teclado", "Teclado USB"),
               ("04F2", "B6BF", "Câmera/Scanner", "USB Camera"), ("0BDA", "8153", "Adaptador de rede", "USB 10/100/1000 LAN")]


def pick_os():
    r, acc = rnd.random(), 0
    for name, ver, build, p in OSES:
        acc += p
        if r <= acc:
            return name, ver, build
    return OSES[0][:3]


# ---------------------------------------------------------------- frota
machines = []
for i in range(args.count):
    laptop = rnd.random() < 0.62
    maker, model = rnd.choice(LAPTOPS if laptop else DESKTOPS)
    os_name, os_ver, os_build = pick_os()
    ram = rnd.choice([8, 16, 16, 16, 32] if laptop else [8, 16, 16, 32])
    disk_gb = rnd.choice([256, 512, 512, 1024])
    used = min(97, max(22, rnd.gauss(55, 17)))
    old = os_ver.endswith("19045") or rnd.random() < 0.15
    host = f"{'NB' if laptop else 'DT'}-{rnd.randint(100, 899):03d}"
    while any(m["host"] == host for m in machines):
        host = f"{'NB' if laptop else 'DT'}-{rnd.randint(100, 899):03d}"
    av = rnd.random()
    machines.append(dict(
        id="demo-" + str(uuid.UUID(int=rnd.getrandbits(128))), host=host, laptop=laptop, maker=maker, model=model, user=users[i], os=(os_name, os_ver, os_build),
        ram=ram, disk_gb=disk_gb, used=used, hdd=(not laptop and rnd.random() < 0.25), cpu=rnd.choice(CPUS), serial="".join(rnd.choices("ABCDEFGHJKLMNPRSTUVWXYZ0123456789", k=8)),
        ip=f"192.168.{rnd.choice([70, 70, 70, 71, 72])}.{rnd.randint(10, 240)}", mac=":".join(f"{b:02X}" for b in [0x60, 0xC7, 0x27] + [rnd.randint(0, 255) for _ in range(3)]),
        agent="1.1.0" if rnd.random() < 0.8 else "1.0.1",
        av_state="off" if av < 0.04 else "third" if av < 0.10 else "ok", realtime_off=rnd.random() < 0.03,
        sig_days=rnd.choice([10, 14, 21]) if rnd.random() < 0.06 else rnd.choice([0, 0, 1, 1, 2]),
        fw_off=rnd.random() < 0.10, bitlocker=(rnd.random() < (0.72 if laptop else 0.35)) if rnd.random() > 0.08 else None,
        secureboot=rnd.random() < 0.9 and not old, tpm=rnd.random() < 0.92 and not old, reboot=rnd.random() < 0.18, activated=rnd.random() < 0.96,
        disk_warn=rnd.random() < 0.025, uptime_days=rnd.choice([0, 1, 2, 3, 5, 8, 12, 21, 35, 48]), rdp=rnd.random() < 0.06, vnc=rnd.random() < 0.09,
        ftp=rnd.random() < 0.01, tools=[t for t, p in AI_TOOLS if rnd.random() < p], extra_soft=rnd.sample(EXTRA, rnd.randint(0, 2)),
        usb=rnd.sample(USB_VENDORS, rnd.randint(0, 2)),
        # fixos por máquina: se fossem sorteados a cada check-in, o servidor veria "mudanças" falsas na auditoria
        printer="\\\\srv-print\\IMP-" + rnd.choice(["ADM", "FIN", "RH", "TI"]), build_suffix=rnd.randint(1000, 5000), install_days=rnd.randint(60, 700),
        battery=(rnd.randint(30, 100), rnd.random() < 0.6),
    ))
for m in machines:
    m["tool_src"] = {t: rnd.choice(["instalado", "em execução"]) for t in m["tools"]}


def build_report(m, with_software=True, extra_peripherals=None, firewall_off=None):
    nd = lambda d: iso(NOW - dt.timedelta(days=d))
    cpu = max(2, min(95, rnd.gauss(18, 12)))
    total = m["ram"] * GB
    free = total * (1 - min(0.93, max(0.3, rnd.gauss(0.6, 0.15))))
    vol = m["disk_gb"] * GB * 0.93
    fw_off = m["fw_off"] if firewall_off is None else firewall_off
    third = m["av_state"] == "third"
    ports = [135, 445]
    if m["rdp"]: ports.append(3389)
    if m["vnc"]: ports += [5800, 5900]
    if m["ftp"]: ports.append(21)
    peripherals = [
        {"kind": "Teclado", "name": "Teclado Padrão PS/2" if m["laptop"] else "Teclado USB", "manufacturer": None, "vendorId": None if m["laptop"] else "413C", "productId": None if m["laptop"] else "2113", "connection": "Interno" if m["laptop"] else "USB"},
        {"kind": "Mouse", "name": "Mouse compatível com HID", "manufacturer": "Microsoft", "vendorId": None, "productId": None, "connection": "Outro"},
        {"kind": "Impressora", "name": m["printer"], "manufacturer": "HP Universal Printing PCL 6", "vendorId": None, "productId": None, "connection": "Rede"},
    ]
    for vid, pid, kind, name in m["usb"]:
        peripherals.append({"kind": kind, "name": name, "manufacturer": None, "vendorId": vid, "productId": pid, "connection": "USB"})
    if extra_peripherals:
        peripherals += extra_peripherals
    software = [{"name": n, "version": v, "publisher": p, "installDate": None} for n, v, p in SOFT + m["extra_soft"]] if with_software else None
    return {
        "agentId": m["id"], "agentVersion": m["agent"], "collectedAt": iso(NOW),
        "identity": {"hostname": m["host"], "domain": "empresa.local", "partOfDomain": True, "loggedUser": m["user"]},
        "os": {"name": m["os"][0], "version": m["os"][1], "build": m["os"][2] + "." + str(m["build_suffix"]), "architecture": "64 bits", "installDate": nd(m["install_days"]), "activated": m["activated"]},
        "hardware": {"manufacturer": m["maker"], "model": m["model"], "serialNumber": m["serial"], "uuid": m["id"][5:].upper(), "biosVersion": "1.12.0", "biosDate": nd(400),
                     "cpu": m["cpu"], "cpuCores": 8, "cpuThreads": 12, "cpuMaxMhz": 2100, "ramTotalBytes": total,
                     "ramModules": [{"manufacturer": "Kingston", "partNumber": "KF3200C16", "capacityBytes": total // 2, "speedMhz": 3200}] * 2,
                     "gpus": [{"name": "Intel(R) UHD Graphics", "driverVersion": "31.0.101.4502", "memoryBytes": 2 * GB}], "isLaptop": m["laptop"],
                     "battery": {"chargePercent": m["battery"][0], "charging": m["battery"][1]} if m["laptop"] else None},
        "network": [{"name": "Ethernet" if not m["laptop"] or rnd.random() < 0.5 else "Wi-Fi", "description": "Realtek PCIe GbE Family Controller", "type": "Ethernet", "mac": m["mac"],
                     "speedBps": 1_000_000_000, "dhcpEnabled": True, "ipv4": [m["ip"]], "ipv6": [], "gateways": [m["ip"].rsplit(".", 1)[0] + ".254"], "dns": ["192.168.10.1"],
                     "wifiSsid": None, "isVirtual": False, "isPrimary": True}],
        "disks": [{"model": ("ST1000DM010" if m["hdd"] else "Micron MTFDKCD512QFM"), "serialNumber": m["serial"] + "D", "sizeBytes": m["disk_gb"] * GB,
                   "mediaType": "HDD" if m["hdd"] else "SSD", "busType": "SATA" if m["hdd"] else "NVMe", "healthStatus": "Warning" if m["disk_warn"] else "Healthy"}],
        "volumes": [{"letter": "C:", "label": "Windows", "fileSystem": "NTFS", "sizeBytes": int(vol), "freeBytes": int(vol * (1 - m["used"] / 100))}],
        "monitors": [{"manufacturer": "DEL", "name": "DELL P2422H", "serialNumber": m["serial"][:6], "manufactureYear": 2022}] if not m["laptop"] else [],
        "security": {
            "antivirusEnabled": m["av_state"] == "ok", "realTimeProtection": False if m["realtime_off"] else m["av_state"] == "ok",
            "antivirusSignatureDate": iso(NOW - dt.timedelta(days=m["sig_days"])),
            "antivirusProducts": ["Windows Defender", "ESET Endpoint Security"] if third else ["Windows Defender"] if m["av_state"] == "ok" else [],
            "firewallDomain": True, "firewallPrivate": True, "firewallPublic": not fw_off,
            "bitLockerOnSystemDrive": m["bitlocker"], "tpmPresent": m["tpm"], "tpmVersion": "2.0" if m["tpm"] else None,
            "secureBoot": m["secureboot"], "pendingReboot": m["reboot"],
        },
        "health": {"lastBoot": nd(m["uptime_days"]), "uptimeSeconds": m["uptime_days"] * 86400 + 3600, "cpuLoadPercent": round(cpu, 1), "ramFreeBytes": int(free)},
        "peripherals": peripherals,
        "networkActivity": {"listening": [{"port": p, "process": {135: "svchost", 445: "System", 3389: "svchost", 5800: "winvnc", 5900: "winvnc", 21: "ftpsvc"}[p]} for p in ports],
                            "connections": [{"process": "chrome", "established": rnd.randint(3, 25), "remotePorts": [443]}, {"process": "OUTLOOK", "established": rnd.randint(1, 6), "remotePorts": [443]},
                                            {"process": "Teams", "established": rnd.randint(1, 8), "remotePorts": [443, 3478]}]},
        "tools": [{"tool": t, "source": m["tool_src"][t], "detail": t.split()[0].lower()} for t in m["tools"]],
        "software": software, "errors": [],
    }


# ---------------------------------------------------------------- 1) registra via API
print(f"1/4 registrando {len(machines)} máquinas em {args.url} ...")
for m in machines:
    st = call("POST", "/api/checkin", build_report(m), token=args.token)
    assert st == 204, (m["host"], st)
# algumas aposentadas / em estoque
for m, status in zip(machines[-5:], ["retired", "retired", "retired", "stock", "stock"]):
    call("PUT", f"/api/machines/{m['id']}/status", {"status": status})

if not args.db:
    print("sem --db: pulando histórico/métricas/offline (só check-ins reais). Pronto.")
    raise SystemExit(0)

# ---------------------------------------------------------------- 2) retroage datas e cria histórico (SÓ em banco de demonstração)
print("2/4 retroagindo datas e criando histórico (banco de demonstração) ...")
db = sqlite3.connect(args.db, timeout=30, isolation_level=None)
trigger_sql = db.execute("SELECT sql FROM sqlite_master WHERE type='trigger' AND name='events_no_delete'").fetchone()[0]
update_trigger_sql = db.execute("SELECT sql FROM sqlite_master WHERE type='trigger' AND name='events_no_update'").fetchone()[0]
db.execute("BEGIN IMMEDIATE")
db.execute("DROP TRIGGER events_no_delete"); db.execute("DROP TRIGGER events_no_update")

ids = [m["id"] for m in machines]
reg_times = sorted(NOW - dt.timedelta(days=rnd.uniform(15, 40)) for _ in machines)
for m, t in zip(machines, reg_times):
    m["registered"] = t
    db.execute("UPDATE machines SET first_seen = ? WHERE agent_id = ?", (iso(t), m["id"]))
    db.execute("UPDATE events SET at = ? WHERE agent_id = ? AND type = 'registered'", (iso(t), m["id"]))
    db.execute("UPDATE assignments SET first_seen = ? WHERE agent_id = ?", (iso(t), m["id"]))
# eventos de status_changed (aposentadas) ficam "hoje"; ok para a demonstração
db.execute("DELETE FROM events WHERE type = 'status_changed'")

# máquinas offline: 5 há algumas horas, 2 há 3 dias, 2 há 12 dias (as últimas aposentadas/estoque ficam de fora)
active = [m for m in machines if m not in machines[-5:]]
offline = {}
for m in rnd.sample(active, 9):
    offline[m["id"]] = NOW - (dt.timedelta(hours=rnd.uniform(1, 5)) if len(offline) < 5 else dt.timedelta(days=3, hours=rnd.randint(0, 8)) if len(offline) < 7 else dt.timedelta(days=12, hours=rnd.randint(0, 8)))
for m in machines[-5:]:
    offline[m["id"]] = NOW - dt.timedelta(days=rnd.randint(8, 30))

hist = []   # (at, agent, host, type, severity, field, old, new, user)
SOFT_UPD = [("Google Chrome", "128.0.6613.120", "129.0.6668.90"), ("Mozilla Firefox", "130.0", "131.0"), ("Zoom Workplace", "6.1.10", "6.2.5"), ("7-Zip", "23.01", "24.08"),
            ("Adobe Acrobat Reader", "24.002.21005", "24.003.20112"), ("Microsoft Teams", "24215.1309", "24243.1309")]
for m in machines:
    start = max(m["registered"], NOW - dt.timedelta(days=14))
    for _ in range(rnd.randint(0, 5)):
        t = start + (NOW - start) * rnd.random()
        kind = rnd.random()
        if kind < 0.5:
            n, a, b = rnd.choice(SOFT_UPD)
            hist.append((t, m, "software_updated", "info", n, a, b))
        elif kind < 0.62:
            n, v, _p = rnd.choice(EXTRA)
            hist.append((t, m, "software_installed", "info", n, None, v))
        elif kind < 0.74:
            hist.append((t, m, "changed", "info", "os.build", f"{m['os'][2]}.{rnd.randint(1000, 2400)}", f"{m['os'][2]}.{rnd.randint(2500, 5000)}"))
        elif kind < 0.84:
            hist.append((t, m, "changed", "info", "network.primaryIp", f"192.168.10.{rnd.randint(10, 240)}", m["ip"]))
        elif kind < 0.92:
            vid, pid, k, name = rnd.choice(USB_VENDORS)
            hist.append((t, m, "peripheral_connected", "info", k, None, {"046D": "Logitech, Inc. Nano Receiver (046D:C534)", "413C": "Dell Computer Corp. Keyboard (413C:2113)"}.get(vid, f"{name} ({vid}:{pid})")))
        else:
            hist.append((t, m, "peripheral_connected", "warn", "Armazenamento USB", None, rnd.choice(["SanDisk Ultra USB Device", "Kingston DataTraveler 3.0", "Generic Flash Disk"])))
# alertas de segurança e quedas de energia/rede
for m in rnd.sample(active, 6):
    t = NOW - dt.timedelta(days=rnd.uniform(0.2, 9))
    hist.append((t, m, "changed", "warn", "security.firewallPublic", "true", "false"))
for m in rnd.sample(active, 3):
    t = NOW - dt.timedelta(days=rnd.uniform(0.5, 8))
    hist.append((t, m, "changed", "warn", "security.antivirusProducts", "Windows Defender", ""))
for m in rnd.sample(active, 8):
    t = NOW - dt.timedelta(days=rnd.uniform(0.3, 12))
    back = t + dt.timedelta(hours=rnd.uniform(1, 14))
    hist.append((t, m, "offline", "warn", None, None, f"último sinal {t.astimezone().strftime('%d/%m/%Y %H:%M')}"))
    hist.append((back, m, "online", "info", None, None, f"sem sinal desde {t.astimezone().strftime('%d/%m/%Y %H:%M')}"))
# trocas de usuário (usuário anterior -> atual) e PCs antigos por usuário
for m in rnd.sample(active, 7):
    t = max(m["registered"], NOW - dt.timedelta(days=rnd.uniform(1, 13)))
    prev = rnd.choice(users[args.count:])
    hist.append((t, m, "user_changed", "info", "identity.loggedUser", prev, m["user"]))
    db.execute("INSERT INTO assignments (agent_id, hostname, user_name, first_seen, last_seen) VALUES (?,?,?,?,?)",
               (m["id"], m["host"], prev, iso(m["registered"]), iso(t - dt.timedelta(minutes=1))))
    # o vínculo do usuário atual começa na troca (as linhas são lidas pela mais recente, então a antiga fica com id menor: reordeno abaixo)
    db.execute("UPDATE assignments SET first_seen = ? WHERE agent_id = ? AND user_name = ?", (iso(t), m["id"], m["user"]))
# usuários que ficaram só com PC antigo (trocaram de máquina): vínculo antigo em PC aposentado
for u in users[args.count:args.count + 3]:
    old = rnd.choice(machines[-5:])
    db.execute("INSERT INTO assignments (agent_id, hostname, user_name, first_seen, last_seen) VALUES (?,?,?,?,?)",
               (old["id"], old["host"], u, iso(old["registered"]), iso(NOW - dt.timedelta(days=rnd.randint(9, 25)))))
# garante que o vínculo mais recente de cada PC (maior id) seja o usuário atual
for m in machines:
    row = db.execute("SELECT id FROM assignments WHERE agent_id = ? AND user_name = ?", (m["id"], m["user"])).fetchone()
    if row:
        cur = db.execute("SELECT id, hostname, user_name, first_seen, last_seen FROM assignments WHERE id = ?", (row[0],)).fetchone()
        db.execute("DELETE FROM assignments WHERE id = ?", (cur[0],))
        db.execute("INSERT INTO assignments (agent_id, hostname, user_name, first_seen, last_seen) VALUES (?,?,?,?,?)", (m["id"], cur[1], cur[2], cur[3], iso(NOW) if m["id"] not in offline else iso(offline[m["id"]])))

for t, m, typ, sev, field, old, new in sorted(hist, key=lambda x: x[0]):
    user = m["user"]
    db.execute("INSERT INTO events (at, agent_id, hostname, type, severity, field, old_value, new_value, user_name) VALUES (?,?,?,?,?,?,?,?,?)",
               (iso(t), m["id"], m["host"], typ, sev, field, old, new, user))
for m in machines:
    db.execute("UPDATE events SET user_name = ? WHERE agent_id = ? AND type = 'registered'", (m["user"], m["id"]))

# offline: ajusta last_seen e a marca (o vigia não duplica o evento) e registra o evento de queda na hora certa
for aid, t in offline.items():
    m = next(x for x in machines if x["id"] == aid)
    db.execute("UPDATE machines SET last_seen = ?, offline_flagged = 1 WHERE agent_id = ?", (iso(t), aid))
    if aid not in {x["id"] for x in machines[-5:]}:
        db.execute("INSERT INTO events (at, agent_id, hostname, type, severity, field, old_value, new_value, user_name) VALUES (?,?,?,?,?,?,?,?,?)",
                   (iso(t + dt.timedelta(minutes=20)), aid, m["host"], "offline", "warn", None, None, f"último sinal {t.astimezone().strftime('%d/%m/%Y %H:%M')}", m["user"]))

db.execute(trigger_sql); db.execute(update_trigger_sql)
db.execute("COMMIT")

# ---------------------------------------------------------------- 3) métricas (7 dias: 5 min nas últimas 24 h, 30 min antes)
print("3/4 gerando métricas ...")
db.execute("BEGIN")
db.execute("DELETE FROM metrics")
for m in machines:
    end = offline.get(m["id"], NOW)
    base_cpu, base_ram = rnd.uniform(6, 26), rnd.uniform(38, 74)
    t = max(m["registered"], NOW - dt.timedelta(days=7))
    rows = []
    while t < end:
        local_h = (t.hour - 3) % 24 + t.minute / 60
        work = 1.0 if 8.5 <= local_h <= 18 and t.weekday() < 5 else 0.25
        cpu = max(1, min(98, base_cpu * (0.5 + work) + rnd.gauss(0, 5) + (rnd.random() < 0.02) * rnd.uniform(30, 55)))
        ram = max(20, min(96, base_ram + 8 * work + rnd.gauss(0, 2.5)))
        disk = m["used"] - (NOW - t).total_seconds() / 86400 * 0.15
        rows.append((m["id"], iso(t), round(cpu, 1), round(ram, 1), round(disk, 1)))
        t += dt.timedelta(minutes=5) if (NOW - t) < dt.timedelta(hours=24) else dt.timedelta(minutes=30)
    db.executemany("INSERT INTO metrics (agent_id, at, cpu, ram_pct, disk_pct) VALUES (?,?,?,?,?)", rows)
db.execute("COMMIT")
db.close()

# ---------------------------------------------------------------- 4) atividade "de hoje" pelo fluxo real (gera eventos com a data atual)
print("4/4 atividade recente (fluxo real) ...")
live = [m for m in active if m["id"] not in offline]
for m in rnd.sample(live, 3):
    call("POST", "/api/checkin", build_report(m, with_software=False, extra_peripherals=[{"kind": "Armazenamento USB", "name": "SanDisk Ultra USB Device", "manufacturer": "SanDisk", "vendorId": None, "productId": None, "connection": "USB"}]), token=args.token)
m = rnd.choice(live)
call("POST", "/api/checkin", build_report(m, with_software=False, firewall_off=True), token=args.token)

print("pronto. máquinas:", len(machines), "| offline:", len(offline), "| eventos históricos:", len(hist))
