# Política de segurança

O Atalaia roda um agente como **SYSTEM** nos computadores e guarda dados sensíveis (inventário, auditoria, contas). Levamos vulnerabilidades a sério.

*English: please report security issues privately through GitHub ("Security" tab → "Report a vulnerability"), not in public issues.*

## Como reportar uma vulnerabilidade

**Não abra uma issue pública.** Use o relato privado do GitHub:

1. Abra a aba **Security** deste repositório.
2. Clique em **Report a vulnerability**.
3. Descreva o problema.

Ajuda muito incluir: a versão (ou o commit), o que você fez, o que esperava e o que aconteceu, e o impacto (quem consegue explorar e o que consegue fazer).

Este é um projeto mantido por uma pessoa, em tempo livre. Não há prazo garantido de resposta, mas todo relato é lido e tratado com prioridade. Pedimos que você aguarde a correção antes de divulgar publicamente.

## Versões com correção

Só a versão mais recente (branch `main`) recebe correções. Quem usa uma versão antiga deve atualizar.

## O que está no escopo

O painel e a API do servidor, o agente do Windows, os scripts de instalação gerados pelo painel e a instalação em massa. Exemplos que queremos saber: ler ou alterar dados sem estar logado, subir de perfil (Leitor → Operador → Administrador), executar código no servidor ou nos computadores, vazar o token dos agentes ou senhas, burlar a confirmação de apagar dados ou a proteção "somente acréscimo" da auditoria.

## Decisões de projeto que já são conhecidas

Não são falhas escondidas, estão documentadas no README. Relatar mesmo assim é bem-vindo se você tiver uma ideia de melhoria.

- **Token de registro compartilhado:** todos os agentes de um mesmo token o dividem (existe revogação no painel; token por máquina está planejado). Os tokens ficam em texto no banco, porque o painel precisa mostrá-los ao administrador.
- **Modo de IP e porta direto usa HTTP:** usuário, senha e token trafegam sem criptografia dentro da rede. Em ambiente que não seja uma rede interna confiável, coloque um proxy com HTTPS na frente.
- **O agente não é assinado digitalmente** e roda como SYSTEM: antivírus podem desconfiar. O arquivo baixado do painel é conferido por SHA-256 antes de instalar.
- **Credenciais de desenvolvimento** (`admin / dev-senha-123456`, `dev-enroll-token`) existem só no modo `Development` e são públicas. O servidor avisa no log quando elas estão em uso. Nunca use esse modo exposto na rede.
- **A instalação remota usa a conta do administrador que roda o script**, no computador dele. O painel nunca vê nem guarda essa senha.
- **O agente só executa tarefas de uma lista fixa** (hoje, o teste de velocidade). Ele nunca roda comando, script ou endereço vindo do servidor.

## Recomendações para quem instala

- Coloque **HTTPS** na frente do painel (Nginx Proxy Manager, Caddy...) e informe o IP do proxy em `TRUSTED_PROXIES`.
- Restrinja no firewall quem alcança a porta do painel.
- Use senhas longas, crie contas com o menor perfil necessário e revogue tokens que não usa mais.
- Proteja o volume de dados e os backups: eles contêm tokens e a auditoria.
- Apague os scripts de instalação depois de usar: eles contêm o token dos agentes.
