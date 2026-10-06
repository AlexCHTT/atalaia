# Atalaia

**Inventário, saúde e auditoria de computadores Windows**, com agente, painel web e instalação em massa. Gratuito e open source (Apache-2.0): uma alternativa ao GLPI para quem quer um painel sempre atualizado e fácil de instalar.

- **Inventário sempre atual:** hardware, sistema, rede, discos, software, periféricos, usuários e ferramentas de IA de cada PC.
- **Índice de saúde (0 a 100)** por computador: antivírus, firewall, criptografia, disco e portas expostas.
- **Auditoria que ninguém edita:** tudo que muda fica registrado, com quem estava usando o computador.
- **Instalação em vários computadores pela tela:** o painel procura os PCs na rede, você marca quais quer e acompanha cada um.
- **Teste de velocidade** de cada computador, direto do painel.
- **Instala em minutos com Docker**, em HTTP direto na rede interna ou atrás de um proxy com HTTPS.
- **Seus dados ficam com você:** roda no seu servidor e não envia nada para fora.

> Projeto em desenvolvimento ativo. Leia "Limitações e pendências conhecidas" antes de usar em produção.

Um **agente** (serviço do Windows) envia dados a um **servidor** (API + painel web), que guarda o estado atual, o histórico de mudanças e as métricas:

```
[Agente - serviço Windows]  --POST /api/checkin (HTTP ou HTTPS)-->  [Servidor ASP.NET + SQLite]  -->  [Painel web]
   roda como SYSTEM em cada PC                                         Docker/Linux                    login por usuário e perfil
```

## Instalação rápida

Você só precisa de **Docker** (com Compose) no servidor. São três passos.

### 1. Subir o servidor: escolha uma das duas opções

| | **Opção A: IP e porta direto** | **Opção B: atrás de um proxy com HTTPS** |
|---|---|---|
| Para quem | Rede interna, o jeito mais simples e rápido | Quem já tem Nginx Proxy Manager, Caddy ou similar, ou quer HTTPS |
| Endereço do painel | `http://IP-DO-SERVIDOR:8080` | `https://painel.suaempresa.com.br` (o que você configurar no proxy) |
| Criptografia | **Nenhuma**: usuário, senha e token dos agentes trafegam em texto dentro da rede | HTTPS no proxy |
| Comando (Linux/macOS) | `bash install.sh --mode direct` | `bash install.sh --mode proxy --proxy-ip IP-DO-PROXY` |
| Comando (Windows) | `powershell -ExecutionPolicy Bypass -File install.ps1 -Mode direct` | `powershell -ExecutionPolicy Bypass -File install.ps1 -Mode proxy -ProxyIp IP-DO-PROXY` |

Rode o script na pasta do projeto. **Sem argumentos ele pergunta tudo** (modo, porta, IP do proxy): `bash install.sh`. Ele grava o `.env`, constrói e sobe o container, espera o servidor responder e mostra o endereço e o código do próximo passo. Pode rodá-lo de novo para atualizar.

- **Opção B:** no proxy, aponte para `http://IP-DO-SERVIDOR:PORTA` e ligue o SSL. Informar o IP do proxy faz o painel mostrar o IP real de cada máquina. No firewall, deixe a porta acessível só para o proxy. (Esta opção foi testada até a configuração do container; falta validar atrás de um Nginx Proxy Manager real.)
- **Preferiu não usar o script?** `docker compose up -d --build` sobe na porta 8080 (mude com `HTTP_PORT` e `TRUSTED_PROXIES` no `.env`).

### 2. Configurar no navegador

Abra o endereço que o script mostrou. O assistente pede o **código de configuração** (o script o exibe; também aparece em `docker compose logs atalaia`) e cria o administrador.

### 3. Instalar os agentes nos computadores

Você cai na tela **Instalação**, com três abas. **Não há nada para gerar nem enviar**: o painel já traz o agente do Windows embutido.

- **Vários computadores:** o assistente procura os PCs na rede, você marca quais quer e acompanha cada um (veja [Instalação em vários computadores](#instalação-em-vários-computadores)).
- **Um computador ou GPO:** baixa um único script `instalar-agente.ps1`, já com o endereço do painel e o token. Rode no PC como administrador, ou ponha como script de inicialização da GPO.
- **Tokens:** crie, copie e revogue os tokens de registro dos agentes.

## Frequências (tudo configurável)

Os ajustes abaixo (e outros) ficam na tela **Configurações** do painel e valem na hora, sem reiniciar. A coluna "Onde muda" mostra o nome antigo da variável de ambiente, que continua valendo como **valor inicial**.

| O quê | Quando | Onde muda |
|---|---|---|
| **Check-in do agente** (estado completo da máquina) | a cada **5 min** | registro `IntervalMinutes` (MSI: `INTERVAL_MINUTES`) |
| **Busca de tarefas** (ex.: teste de velocidade pedido pelo painel) | a cada **30 s**, uma consulta pequena; o servidor nunca se conecta ao PC | `Agent:PollSeconds` (mínimo 10) |
| Se o servidor estiver fora do ar | repete em **30 s, 1 min, 2 min…** até o intervalo normal; não acumula fila, a próxima coleta já manda o estado atual | fixo |
| **Lista de software instalado** | ao iniciar o serviço e depois a cada **6 h** (a lista é grande e muda pouco) | `Agent:SoftwareEveryHours` |
| Serviço cai | o Windows o reinicia após **5 s** | configurado na instalação (script ou MSI) |
| **Máquina vira "offline"** | sem check-in por mais de **20 min** | `Server:OfflineAfterMinutes` |
| Vigia de offline (servidor) | confere a cada **1 min** e registra o evento uma vez | fixo |
| Painel atualiza sozinho | a cada **30 s** | `index.html` |
| **Métricas** (CPU, RAM, disco) | 1 amostra por check-in, guardada por **30 dias**; limpeza a cada 6 h | `Server:MetricsRetentionDays` |
| **Auditoria** (eventos) | guardada **para sempre** por padrão; o banco recusa UPDATE/DELETE. Limpeza automática só se você definir um prazo | `Server:EventsRetentionDays` (0 = nunca) |

## O que o agente coleta

- **Identidade**: hostname, domínio, usuário no console, UUID da BIOS (é o ID da máquina; sobrevive a reinstalar o Windows).
- **Rede**: IPs, MACs, gateway, DNS, DHCP, velocidade, SSID; adaptadores virtuais marcados, o físico com gateway é o "principal".
- **Hardware**: fabricante, modelo, serial, BIOS, CPU, RAM (módulos), GPU, discos (tipo, saúde), monitores, bateria.
- **Sistema e segurança**: versão/build do Windows, ativação, Defender, firewall, BitLocker, TPM, Secure Boot, reinício pendente.
- **Periféricos**: teclado, mouse, câmera, impressoras, pendrives, Bluetooth. O VID:PID é traduzido para fabricante/modelo no servidor (`usb.ids`).
- **Rede por processo**: portas TCP abertas para a rede (destaca RDP, VNC, SMB, FTP, Telnet) e conexões ativas por processo.
- **Ferramentas de interesse** (Claude, ChatGPT, Copilot, Cursor, Windsurf, Ollama, LM Studio, Perplexity): instaladas ou em execução. A lista está em `Watchlist` no agente.
- **Software instalado**.

**Decisões de privacidade (de propósito):** o agente **não** envia IPs de destino das conexões, só as portas; não lê histórico de navegador nem DNS; quem usa uma ferramenta de IA só pelo navegador não é detectado. Monitorar o uso de pessoas exige política comunicada (LGPD): alinhe com RH/jurídico antes de ampliar.

## O que o servidor registra

- **Estado atual** de cada PC (aba *Dispositivos*), com status ativa / aposentada / em estoque.
- **Auditoria** (aba *Auditoria*, com filtro por PC, usuário, tipo, período, busca e exportação CSV): PC registrado, hostname/IP/RAM/disco/GPU alterados, software instalado/atualizado/removido, periférico conectado/removido (pendrive vira alerta), troca de usuário, PC offline/online, mudança de status, remoção, e **segurança que piorou** (firewall ou antivírus desligado) como alerta.
- **Vínculo usuário ↔ PC** com datas (aba *Usuários*): quem usou cada PC e por quais PCs cada pessoa passou. Remover um PC do painel **mantém** o histórico dele.
- **Ferramentas** (aba *Ferramentas*): onde cada ferramenta da lista aparece.

> "Usuário" é quem está no console no momento do check-in. Acesso remoto e sessões que o agente não enxerga não aparecem. Eventos antigos, de antes da coluna de usuário existir, ficam sem usuário (o log não permite alteração).

## Acessos e permissões

O painel tem **tela de login** (usuário e senha, sessão por cookie) e uma tela **Acessos**, só para administradores, para criar, editar, desativar, excluir usuários e redefinir senhas. Cada pessoa entra com a própria conta, e o registro de acessos diz **quem fez o quê**.

| Perfil | O que pode |
|---|---|
| **Administrador** | Tudo: gerencia usuários, remove dispositivos, muda status, exporta CSV, lê o registro de acessos. |
| **Operador** | Consulta tudo, muda o status dos dispositivos e exporta CSV. Não remove dispositivos nem gerencia usuários. |
| **Leitor** | Só consulta. Não altera nada nem exporta. |

**Primeiro acesso (assistente de configuração).** Na primeira execução não há nenhuma conta, então o painel abre um assistente no navegador. Para que ninguém da rede configure o painel antes de você, o assistente pede um **código de configuração** que o servidor imprime no log (procure por `PRIMEIRA EXECUÇÃO`; no Docker: `docker compose logs atalaia`). O código vale só enquanto o servidor está no ar (reiniciar gera outro) e é trocado após 5 erros. Em seguida você cria o administrador e já entra, caindo na tela **Instalação**. A senha segue a regra de todas: **no mínimo 10 caracteres e sem o nome de usuário dentro**.

**Instalação automatizada (sem assistente).** Se `Server__AdminUser` e `Server__AdminPassword` (no `.env`: `ADMIN_USER` e `ADMIN_PASSWORD`) estiverem definidos, a conta é criada na subida e o assistente não aparece. Se a senha não serve, o servidor não sobe. Depois que existe uma conta, essas variáveis não têm mais efeito: as contas são gerenciadas na tela Acessos.

**Tokens dos agentes.** O token que os agentes usam para se registrar é gerado pelo servidor e fica na tela **Instalação** (só administrador), junto com os comandos de instalação já preenchidos. Pode haver vários tokens ativos: crie um novo, instale com ele e só então revogue o antigo. Revogar vale na hora para novos check-ins. O token continua podendo ser definido por `Server__EnrollToken`; nesse caso ele entra na lista como um token normal e pode ser revogado no painel. Os tokens ficam gravados em texto no banco (o painel precisa mostrá-los), por isso proteja o volume `/data` e os backups.

**Esqueceram a senha de todos os administradores?** Suba o servidor uma vez com `Server__ResetAdminPassword=true` (mais `Server__AdminUser` e `Server__AdminPassword` com a senha nova). Ele redefine ou recria essa conta de administrador, destrava, encerra as sessões dela e registra o fato no registro de acessos. **Remova a variável e reinicie** em seguida.

**Senha temporária.** Ao criar um usuário sem informar senha, ou ao redefinir a de alguém, o sistema gera uma senha temporária, mostrada **uma única vez** ao administrador. A pessoa é obrigada a trocá-la no primeiro acesso, e até lá nada além da troca funciona. Não há envio de e-mail: a redefinição é sempre feita por um administrador.

### Como a segurança funciona
- **Senhas** guardadas só como hash PBKDF2-SHA256 (600 mil iterações, sal próprio). Nunca em texto.
- **Sessão** em cookie `HttpOnly` (JavaScript não lê), `SameSite=Strict` e `Secure` quando a conexão é HTTPS. No banco fica só o hash do token. Vale 12 h no total e cai após 4 h parada (`Server__SessionHours`, `Server__SessionIdleMinutes`). Desativar a conta, mudar o perfil ou redefinir a senha **encerra as sessões abertas na hora**.
- **Tentativas erradas:** 5 falhas seguidas travam a conta por 10 minutos (ajustável em Configurações; antes por `Server__MaxFailedLogins` e `Server__LockMinutes`); um administrador destrava redefinindo a senha. Além disso, mais de 20 falhas do mesmo endereço em 10 minutos bloqueiam aquele endereço. A mensagem de erro é a mesma para "usuário não existe" e "senha errada".
- **CSRF:** todo pedido que altera algo exige o cabeçalho `X-Requested-With: Atalaia`, que um site de fora não consegue enviar, além do cookie `SameSite=Strict`.
- **Política de conteúdo (CSP)** e outros cabeçalhos: só roda script que vem do próprio servidor, a página não pode ser embutida em outra e a API nunca fica em cache.
- **Regras de segurança do cadastro:** o sistema sempre mantém ao menos um administrador ativo, e ninguém altera o próprio perfil, se desativa ou se exclui.
- **Registro de acessos** (login, falhas, travamentos, criação e alteração de usuários, redefinições de senha, remoção de dispositivos, mudanças de status, exportações) é **imutável**, como a auditoria dos dispositivos.

> Atrás de um proxy reverso, configure `TRUSTED_PROXIES`. Sem isso o servidor vê todos os acessos como vindos do proxy: o freio por endereço passa a valer para todo mundo junto, e o cookie perde a flag `Secure`.

O teste de regressão de tudo isso está em `scripts/test_auth.py` (instruções no cabeçalho do arquivo).

## O painel

Console web com barra lateral: **Dashboard**, **Dispositivos**, **Usuários**, **Ferramentas** e **Auditoria**. Tem tema claro/escuro, busca global (tecla `/`), atualização automática a cada 30 s e o botão ☰ que recolhe/expande a barra lateral (a escolha fica salva no navegador). O sino conta só os alertas **ainda não vistos**: clicar nele, ou abrir a Auditoria, zera o contador (o "visto" fica guardado neste navegador; o card "Alertas em 24 h" do dashboard continua mostrando o total). Cada dispositivo e cada usuário abre numa gaveta lateral com endereço próprio (`#/devices/<id>`, `#/users/<usuario>`), que dá para compartilhar. O dashboard e o resto funcionam sem internet: nada é carregado de CDN, e os gráficos são SVG feitos à mão.

### Configurações

A tela **Configurações** (só administrador) concentra o que antes exigia variável de ambiente e reinício. Cada mudança vale **na hora**, fica registrada no registro de acessos (com o valor antes e depois) e tem um botão "Restaurar padrão".

| Grupo | O que dá para ajustar |
|---|---|
| **Dados e retenção** | Prazo da auditoria dos dispositivos, do registro de acessos e das métricas (em dias; 0 guarda para sempre onde se aplica); quantos testes de velocidade guardar por computador; limite de tamanho do banco para avisar |
| **Agentes** | Minutos sem check-in para considerar offline; intervalo de check-in; frequência da lista de software |
| **Índice de saúde** | Ocupação do disco para atenção e crítico; tolerância de assinaturas do antivírus; dias sem reiniciar |
| **Acesso e segurança** | Duração e ociosidade da sessão; tentativas antes de travar a conta e tempo de bloqueio; tamanho mínimo da senha |
| **Rede e testes** | Teste de velocidade até a internet (liga/desliga); limite de dados por teste; tamanho máximo da varredura de rede |
| **Aparência** | Nome da empresa (tela de login, lateral e título da aba) |

- **Uso de disco:** no topo da tela, quanto o banco ocupa, o espaço livre, o tamanho e o período de cada tabela e uma **estimativa de crescimento** (no ritmo da última semana, e para onde o banco estabiliza com a retenção atual). Os tamanhos por tabela são estimativas (soma do conteúdo, sem índices). Botões para **aplicar a retenção agora** (ela também roda sozinha a cada 6 horas) e **recalcular os índices de saúde**.
- **Apagar dados exige confirmação, e quem exige é o servidor:** se reduzir um prazo apagaria dados, a tela mostra quantos registros serão removidos e só segue com "Apagar e salvar". A auditoria e o registro de acessos continuam "somente acréscimo" para qualquer outro caminho, e cada limpeza deixa um registro de que aconteceu.
- **Valores vêm de três lugares, nesta ordem:** o que foi definido na tela → a variável de ambiente antiga (valor inicial; a tela marca "Do servidor") → o padrão. "Restaurar padrão" volta ao que valia antes de definir na tela.
- **Agentes:** intervalo de check-in, frequência da lista de software e limite do teste de velocidade só são enviados aos agentes quando você os define na tela (senão cada agente mantém o seu valor local) e chegam em até 30 segundos. Agentes antigos ignoram.
- **Só no servidor (não mudam pela tela):** porta, endereço do banco, `TRUSTED_PROXIES`, token e administrador iniciais.

### Instalação em vários computadores

Na tela **Instalação**, a aba **Vários computadores** (só administrador) é um assistente em três passos:

1. **Procurar computadores na rede.** Informe uma faixa de IP (`192.168.1.0/24`, um endereço, ou um intervalo `192.168.1.10-50`). O servidor testa cada endereço por TCP (portas 445, 135, 5985 e 3389; uma porta que recusa a conexão também prova que há um PC ali). Só redes privadas, até **1024 endereços** por varredura, uma varredura por vez.
2. **Escolher onde instalar.** A lista mostra IP, nome, se é provavelmente Windows, quais portas de administração remota respondem e, cruzando com o inventário, se o PC **já tem agente**. Marque um, vários ou todos (há um atalho "Marcar os que faltam").
3. **Instalar.** O agente já vem embutido no painel (versão, tamanho e hash aparecem na tela). Escolha o token e o painel gera um **script PowerShell** com a lista marcada. Você o roda **no seu PC, como administrador**, e a tela acompanha cada computador: *Aguardando o script → Enviado → **Instalado*** (ou *Falhou*, com o motivo, ou *Sem resposta*).

Como funciona por dentro, e por que é assim:

- **Nenhuma senha passa pelo painel.** O script usa a sua conta do Windows (ou a que você digitar com `-Credential (Get-Credential)`) para falar com cada PC por WinRM ou WMI/DCOM. O painel nunca vê nem guarda credencial de administrador, então invadir o painel não entrega o controle dos computadores.
- Em cada PC o script manda baixar o agente **deste painel**, **confere o SHA-256**, grava a configuração, cria o serviço `Atalaia` e o inicia (a mesma sequência do `scripts\dev-service.ps1`). Em caso de falha, o PC deixa um log em `C:\Windows\Temp\atalaia-install.log`.
- **"Instalado" é provado, não presumido:** o computador só muda para esse estado quando o agente de fato se registra no servidor (o servidor casa pelo IP dos adaptadores do relatório, o que funciona mesmo atrás de NAT).
- O script avisa o painel com um segredo próprio do trabalho (não é o token dos agentes), válido por **24 h**. Um "enviado" que não vira agente em 15 minutos passa para "sem resposta".
- Opções do script: `-DryRun` (só simula, sem instalar nem avisar o painel), `-Only 192.168.1.10` (um computador só), `-Credential`.
- Tratar o script como senha: ele contém o token dos agentes. Apague depois de usar. O token aparece na linha de comando do processo de instalação no PC de destino durante alguns segundos.

Requisitos nos PCs de destino: a porta **5985** (WinRM) ou **135** (RPC/WMI) liberada do seu PC para eles, e acesso ao painel pela rede. A varredura só enxerga o que o **servidor** alcança (uma VM em outra sub-rede pode não ver os PCs), e PCs com firewall que descarta tudo não aparecem: a lista nunca é garantida como completa.

### Teste de velocidade

Na ficha de cada dispositivo, a aba **Velocidade** mede a conexão daquele PC: download, upload e latência (com a variação). Quem pode pedir: **Operador** e **Administrador**; o Leitor só vê os resultados. Os dois destinos são:

- **Até o servidor**: velocidade entre o PC e o painel, que é o que importa dentro da rede interna. Se houver proxy no meio, ele entra na medição.
- **Até a internet**: usa os servidores públicos da Cloudflare (`speed.cloudflare.com`). O tráfego sai do PC para fora da empresa. Para desligar esta opção: `Server__SpeedTest__InternetEnabled=false` (no Docker, `SPEEDTEST_INTERNET=false` no `.env`).

Como funciona: o painel registra o pedido e o **agente o busca** em até 30 s (o servidor nunca abre conexão com o PC); o teste leva cerca de 15 s e o resultado aparece na tela sozinho. Cada teste usa 4 conexões e para em ~6 s **ou ~150 MB por sentido**, o que vier primeiro (numa rede de 1 Gbps ele acaba por volta de 1 s). Só um teste por máquina por vez. Se o agente não buscar o pedido em **3 minutos** (máquina desligada), ele aparece como "Expirado" e libera um novo; um teste que o agente começou e não terminou falha após 3 minutos. Dá para **cancelar** um teste em andamento pelo botão na própria tela (Operador e Administrador), o que libera a máquina na hora. Cada pedido e cada cancelamento ficam no registro de acessos, com quem fez.

Segurança: o agente só executa tarefas de uma **lista fixa** no código (hoje, só `speedtest`) e os endereços de medição são fixos nele (o servidor só escolhe entre "servidor" e "internet"). Nunca roda comando ou script vindo do servidor. O servidor limita o tamanho de cada medição e a 16 transferências simultâneas.

**Requer agente atualizado.** Agentes antigos não buscam tarefas: o pedido expira. Reinstale com o agente do painel: na tela Instalação, rode o script com `-Force` (o assistente de rede sempre reinstala).

### Índice de saúde (0 a 100)

Cada dispositivo ganha um índice calculado a cada check-in: 100 menos os pontos dos problemas encontrados. É um indicador de **higiene da configuração**, não um detector de vírus (100 não significa "sem malware"). Faixas: **90+ Protegido**, **70–89 Atenção**, **abaixo de 70 Em risco**.

| Problema | Gravidade | Pontos |
|---|---|---|
| Antivírus desativado ou ausente (a menos que haja outro antivírus ativo) | crítico | −30 |
| Proteção em tempo real desligada | crítico | −25 |
| Disco com problema de saúde | crítico | −20 |
| Disco do sistema com 90% ou mais ocupado / 80% ou mais | crítico / atenção | −15 / −8 |
| Telnet ou FTP expostos na rede | crítico | −15 |
| Firewall desligado em algum perfil | atenção | −15 |
| Sem criptografia do disco (BitLocker) | atenção | −10 |
| Assinaturas do antivírus com mais de 7 dias | atenção | −10 |
| RDP ou VNC expostos na rede | atenção | −8 cada |
| Windows não ativado | atenção | −5 |
| Secure Boot desativado / sem TPM / reinício pendente / mais de 30 dias sem reiniciar / falhas de coleta | informativo | −4 / −3 / −3 / −2 / −1 |

Quando o agente não consegue ler um controle (ex.: sem privilégio), o valor fica "desconhecido" e **não** conta como problema. As regras estão em `src/Atalaia.Server/HealthAssessment.cs`.

### Segurança do painel

Tudo que vem das máquinas (nome do computador, usuário, nome de software, periférico) é tratado como **não confiável**: o painel escapa todo texto ao montar a tela, então um agente comprometido não consegue injetar HTML ou script. Isso foi testado registrando um dispositivo com `<img onerror=...>` em todos os campos e percorrendo todas as telas.

## Tamanho e retenção (estimativa para ~140 máquinas)

Medido no banco de teste: um evento ocupa ~100 bytes de dados, uma amostra de métrica ~84 bytes, o relatório de uma máquina ~7 KB (+ ~9 KB de software). Com índices e sobrecarga do SQLite, conte ~300 B por evento e ~200 B por amostra (**estimativa**, não medida em produção).

| Dado | Conta | Tamanho |
|---|---|---|
| Estado atual das máquinas | 140 × ~16 KB | ~2 MB, fixo |
| **Métricas** (a maior parte) | 140 × 288 amostras/dia = ~40 mil linhas/dia | **30 dias ≈ 240 MB** (90 dias ≈ 730 MB) |
| **Auditoria** | depende de quantas mudanças por PC/dia: 2 → ~30 MB/ano · 10 → ~150 MB/ano · 20 → ~300 MB/ano | cresce devagar |
| Tráfego de rede | 140 × 288 × 7 KB | ~300 MB/dia na LAN (~0,3 Mbit/s médio) |

As métricas definem o tamanho do banco; por isso o padrão é 30 dias. A auditoria custa pouco em disco, então o prazo dela é uma decisão de **política** (jurídico/RH), não de espaço.

- **Métricas** são apagadas automaticamente além do prazo definido em Configurações (padrão 30 dias).
- **Auditoria**: com o prazo da auditoria (Configurações) > 0, eventos mais antigos que isso são removidos (a checagem roda ao iniciar e a cada 6 h). O banco continua barrando UPDATE/DELETE para tudo, exceto esta rotina, que o faz dentro de uma transação. Cada limpeza grava um evento **"Limpeza por retenção"** (com a quantidade removida), e esse registro nunca é apagado. Exporte o CSV antes se precisar arquivar.
- O arquivo do SQLite não encolhe sozinho depois de apagar (o espaço é reaproveitado). Para devolver espaço ao disco: `VACUUM` com o servidor parado.

## Estrutura

```
src/Atalaia.Agent    serviço Windows (.NET, net10.0-windows): coleta e envia
src/Atalaia.Server   API (ASP.NET + SQLite), HealthAssessment.cs (índice de saúde) e Resources/usb.ids
  wwwroot/              o painel: index.html, css/app.css, js/ (módulos ES: core, ui, charts, store, pages/...), img/ (logo)
src/Atalaia.Shared   contrato JSON usado pelos dois
installer/              Package.wxs (WiX) + build-msi.cmd (MSI opcional, para GPO "software atribuído")
scripts/                run_suite.py (roda os testes), test_*.py (suítes), dev-service.ps1 (serviço de teste), seed_demo.py (frota fictícia)
.github/workflows/      ci.yml (compila e roda os testes a cada envio)
Dockerfile, docker-compose.yml, .env.example, install.sh, install.ps1
LICENSE, NOTICE, SECURITY.md, CONTRIBUTING.md
```

## Rodar em desenvolvimento

```powershell
# servidor (usa appsettings.Development.json: token e senha de teste, banco em data/dev.db)
$env:ASPNETCORE_ENVIRONMENT="Development"; $env:ASPNETCORE_URLS="http://localhost:5099"
dotnet run --project src/Atalaia.Server --no-launch-profile
# painel: http://localhost:5099  (entre com admin / dev-senha-123456, a conta criada a partir do appsettings.Development.json)

# agente em console (sem instalar nada): coleta e envia; --dump só imprime o JSON
$env:DOTNET_ENVIRONMENT="Development"
dotnet run --project src/Atalaia.Agent --no-launch-profile
dotnet run --project src/Atalaia.Agent --no-launch-profile -- --dump
```

**Agente para o painel entregar:** ao rodar o servidor do código (e não pela imagem Docker, que já traz o agente), publique o agente uma vez para a tela Instalação ter o que entregar aos computadores:

```powershell
dotnet publish src/Atalaia.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:Version=1.2.0 -o publish/agent
```

Sem administrador, o agente não lê TPM e BitLocker (aparecem como "?" = desconhecido). Como serviço (SYSTEM) lê.

Os arquivos do painel (`wwwroot/`) são servidos direto do disco: editar HTML/CSS/JS vale com F5, sem recompilar. Já mudanças em C# exigem parar o servidor e subir de novo. Depois de atualizar o servidor, reinicie-o antes de abrir o painel: o painel novo depende de campos que só o servidor novo envia.

### Testes

```powershell
dotnet build src/Atalaia.Server -c Release
python scripts/run_suite.py            # todas as suítes que o seu sistema suporta
python scripts/run_suite.py auth setup # ou só algumas: auth, setup, settings, deploy
```

Cada suíte sobe um **servidor descartável com banco novo** (numa pasta temporária), roda e o desliga. Nunca usa o seu banco de verdade. A suíte `deploy` exige o agente publicado (acima) e o Windows PowerShell. O GitHub roda as mesmas suítes a cada envio (`.github/workflows/ci.yml`). Veja também o [CONTRIBUTING](CONTRIBUTING.md).

### Modo demonstração (frota fictícia)

Para ver o painel com dezenas de máquinas, usuários, histórico e alertas, gere uma frota fictícia **num servidor e banco separados** (a auditoria é imutável: dados fictícios jogados no banco de verdade ficam lá):

```powershell
$env:ASPNETCORE_ENVIRONMENT="Development"; $env:ASPNETCORE_URLS="http://localhost:5098"; $env:Server__DbPath="data/demo.db"
dotnet run --project src/Atalaia.Server --no-launch-profile      # terminal 1
python scripts/seed_demo.py --url http://localhost:5098 --db src/Atalaia.Server/data/demo.db   # terminal 2
```

Gera ~40 PCs com perfis variados (protegidos, em risco, offline), usuários, 14 dias de eventos e 7 dias de métricas, sempre com os mesmos dados. Para limpar, pare o servidor e apague `data/demo.db*`.

## MSI (opcional)

O caminho normal é o script da tela Instalação, que não precisa de MSI nem de ferramenta nenhuma. O MSI serve para quem quer instalar pelo "software atribuído" da GPO. Ele **não** é gerado pelo Docker (o WiX só roda em Windows) e precisa ser construído numa máquina Windows:

Requisitos: .NET SDK, **WiX 4.0.6** (`dotnet tool install --global wix --version 4.0.6` e `wix extension add -g WixToolset.Util.wixext/4.0.6`).
Não atualize para o WiX 5 ou superior sem decidir sobre a *Open Source Maintenance Fee* deles.

```powershell
installer\build-msi.cmd 1.1.0 https://painel.suaempresa.com.br SEU_TOKEN
msiexec /i publish\msi\Atalaia-1.1.0.msi /qn          # instala ou atualiza por cima
msiexec /x publish\msi\Atalaia-1.1.0.msi /qn          # remove
```

- A **URL e o token ficam gravados dentro do MSI** (para a GPO instalar sem parâmetros). Trate o MSI como segredo: guarde num share que só as contas de computador do domínio leiam.
- A config vai para `HKLM\SOFTWARE\Atalaia`; o próprio serviço restringe a chave a SYSTEM e Administradores ao iniciar.
- **GPO**: Configuração do Computador → Instalação de Software → *Atribuído* → apontar para o MSI no share. (Ainda não testado em domínio real.)
- Teste local sem MSI: `powershell -ExecutionPolicy Bypass -File scripts\dev-service.ps1 install` (como administrador). O nome do serviço é o mesmo do MSI: desinstale um antes de instalar o outro.

## Servidor em produção (Docker)

A imagem **já traz o agente do Windows embutido** (compilado junto, em `/app/agent`): o painel o entrega aos computadores sem você gerar nem enviar nada. A instalação passo a passo está em [Instalação rápida](#instalação-rápida) (`install.sh` / `install.ps1`). Manualmente: `docker compose up -d --build` e `docker compose logs atalaia` para ver o código do assistente. O `.env` é opcional e serve para ajustar porta, `TRUSTED_PROXIES`, retenção etc. (veja `.env.example`).

Em produção, coloque HTTPS na frente (Nginx Proxy Manager, Caddy...): o token do agente e a senha do painel viajam em cabeçalhos HTTP. Atrás de proxy, o servidor só lê o IP real de `X-Forwarded-For` se você informar o IP do proxy em `TRUSTED_PROXIES` (`.env`); sem isso o painel mostra o IP do proxy em "IP visto pelo servidor". O banco fica no volume `/data`.
A imagem foi construída e testada localmente (processo sem privilégio, banco persistente no volume `/data`, `usb.ids` presente, autenticação e check-in). Falta testar atrás de um Nginx Proxy Manager real, com HTTPS.

## API (painel, exige login por sessão)

`GET /api/machines` (cada item traz `healthScore` e `issues`) · `GET /api/machines/{id}` · `GET /api/machines/{id}/events|users|metrics?hours=` · `PUT /api/machines/{id}/status` · `DELETE /api/machines/{id}` · `GET /api/events` (filtros: `agentId, user, type, severity, q, from, to, before, limit`) · `GET /api/events/filters` · `GET /api/events/count?severity=&from=` · `GET /api/events/export` (CSV) · `GET /api/users` · `GET /api/user-history?name=` · `GET /api/tools`.
Agente (sem sessão, cabeçalho `X-Enroll-Token`): `POST /api/checkin` · `POST /api/agent/poll` · `POST /api/agent/tasks/{id}/result` · `GET /api/agent/speedtest/ping|down` · `POST /api/agent/speedtest/up`.
Configurações (Administrador): `GET|PUT /api/settings` · `GET /api/system/storage` · `POST /api/system/purge-now` · `POST /api/system/recalculate-health`. Sem login: `GET /api/public/info`.
Instalação em vários computadores (Administrador): `POST /api/discovery/scans` · `GET /api/discovery/scans/latest` · `POST /api/discovery/scans/{id}/cancel` · `GET /api/install/package` · `GET /api/install/agent-script` · `POST /api/deploy/jobs` · `GET /api/deploy/jobs[/{id}[/script]]`. Script e PC de destino: `POST /api/deploy/jobs/{id}/progress` (cabeçalho `X-Job-Secret`) · `GET /api/deploy/package` (token dos agentes).
Teste de velocidade (painel): `GET /api/machines/{id}/speedtests` (Leitor) · `POST /api/machines/{id}/speedtest` e `.../speedtest/cancel` (Operador) · `GET /api/features`.
Primeira execução (sem login, só enquanto não existe conta): `GET /api/setup/status` · `POST /api/setup/verify` · `POST /api/setup`. Administrador, tokens dos agentes: `GET /api/install/info` · `POST /api/install/tokens` · `DELETE /api/install/tokens/{id}`.
Acesso: `POST /api/auth/login` · `POST /api/auth/logout` · `GET /api/auth/me` · `POST /api/auth/change-password`. Administrador: `GET/POST /api/accounts` · `PUT/DELETE /api/accounts/{id}` · `POST /api/accounts/{id}/reset-password` · `GET /api/access-log`.
Todas as rotas do painel exigem sessão e o perfil mínimo (leitura: Leitor; exportar CSV e mudar status: Operador; remover dispositivo e gerir contas: Administrador). Pedidos que alteram dados precisam do cabeçalho `X-Requested-With: Atalaia`.

## Limitações e pendências conhecidas

- **Instalação em vários computadores:** foram verificados varredura, validações, agente embutido (compilado no Docker e executado no Windows), geração dos scripts, `-DryRun`, progresso e a prova pelo check-in. **Não** foi verificada, por falta de um PC de destino real, a instalação remota em si (WinRM/WMI e o `New-Service` rodando no destino). **Teste primeiro em um computador** (`-Only`). Antivírus corporativo pode bloquear a execução remota.
- Os agentes se registram com um **token compartilhado** (pode haver vários, revogáveis no painel, mas todas as máquinas de um mesmo token o dividem). Próximo passo: token por máquina.
- **SQLite**: serve para algumas centenas de máquinas; a migração para Postgres está planejada junto do deploy.
- **Sem auto-update** do agente: atualize reinstalando (script com `-Force`, assistente de instalação ou MSI com a mesma `UpgradeCode`).
- Sem HTTPS próprio no servidor (use proxy reverso).
- **Login só com usuários locais.** A integração com Active Directory/LDAP é uma etapa à parte.
- Sem autenticação em dois fatores e sem e-mail (a redefinição de senha é feita por um administrador).
- `usb.ids` traduz fabricante e, quando existe na base, o modelo; produtos fora da base aparecem só com o nome do Windows.
- A base `usb.ids` é de linux-usb.org (GPL-2.0+ ou BSD-3). Para atualizar, substitua `src/Atalaia.Server/Resources/usb.ids`.

## Atualizando de uma versão anterior (quando o projeto se chamava AgentTools)

O projeto foi renomeado para **Atalaia**. A atualização não perde nada:

- **Banco de dados:** se existir um `agenttools.db` e não houver `atalaia.db`, o servidor continua usando o antigo (dispositivos, auditoria e contas ficam). Para migrar de vez, basta renomear o arquivo para `atalaia.db` com o servidor parado.
- **Agentes já instalados** continuam enviando dados normalmente (o protocolo não mudou). Para trocá-los pelo agente novo, rode o script da tela **Instalação** (ou o assistente de rede): ele **remove sozinho** o serviço antigo `AgentTools`, a pasta e a chave de registro antigas e instala o `AtalaiaAgent`. Em desenvolvimento, `scripts\dev-service.ps1 install` faz o mesmo.
- **Docker:** o serviço do compose agora é `atalaia` e o volume `atalaia-data`. Quem já tinha dados no volume antigo (`agenttools-data`) deve copiá-los ou apontar o volume antigo no `docker-compose.yml`.
- **Nomes que mudaram:** serviço do Windows `AtalaiaAgent`, executável `Atalaia.Agent.exe`, pasta `Program Files\Atalaia`, chave `HKLM\SOFTWARE\Atalaia`, cookie `atalaia_session`.

## Antivírus e assinatura do agente

O agente é um executável que roda como **SYSTEM** e coleta dados do computador. Por isso, **antivírus e EDR podem desconfiar dele**, ainda mais porque o arquivo **não é assinado digitalmente** (assinatura exige um certificado). O que fazer:

- **Cadastre uma exceção** no antivírus para `C:\Program Files\Atalaia\Atalaia.Agent.exe` (ou para o hash SHA-256 que a tela Instalação mostra).
- Empresas que **bloqueiam executáveis sem assinatura** (AppLocker/WDAC) precisam assinar o agente com um certificado da própria empresa e distribuí-lo como "editor confiável" pela GPO.
- Qualquer pessoa pode **conferir o que está instalando**: o código é aberto, o agente do painel é compilado a partir dele (imagem Docker) e a instalação confere o SHA-256 antes de executar.

Assinar as versões oficiais está nos planos; até lá, o caminho acima vale.

## Segurança e contribuições

- Encontrou uma vulnerabilidade? **Não abra issue pública**: veja o [SECURITY.md](SECURITY.md).
- Quer contribuir? Veja o [CONTRIBUTING.md](CONTRIBUTING.md).

## Licença

Atalaia é software livre sob a [Apache License 2.0](LICENSE). Você pode usar, modificar e distribuir, inclusive em ambiente comercial, mantendo o aviso de licença e o arquivo [NOTICE](NOTICE) e indicando o que modificou. A licença inclui uma concessão de patentes dos contribuidores. Os componentes de terceiros e suas licenças estão listados no `NOTICE`.
