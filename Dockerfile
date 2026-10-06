# Build do servidor (API + painel) e do agente do Windows. Contexto = raiz do repositório.

# --- Servidor ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Atalaia.Shared src/Atalaia.Shared
COPY src/Atalaia.Server src/Atalaia.Server
RUN dotnet publish src/Atalaia.Server -c Release -o /app --no-self-contained

# --- Agente do Windows, embutido no painel ---
# O painel já nasce com o agente pronto: ninguém precisa gerar instalador nem enviar arquivo. O servidor entrega este .exe aos
# computadores (conferindo o hash) e os scripts da tela Instalação o instalam como serviço. A versão do agente acompanha a do servidor.
# (O MSI do installer\ continua existindo só para quem prefere instalar por GPO "software atribuído"; o WiX não roda em Linux.)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS agent
ARG AGENT_VERSION=1.2.0
WORKDIR /src
COPY src/Atalaia.Shared src/Atalaia.Shared
COPY src/Atalaia.Agent src/Atalaia.Agent
RUN dotnet publish src/Atalaia.Agent -c Release -r win-x64 --self-contained \
      -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:EnableWindowsTargeting=true -p:Version=${AGENT_VERSION} \
      -o /agent-out \
 && echo "${AGENT_VERSION}" > /agent-out/version.txt

# --- Imagem final ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=agent /agent-out/Atalaia.Agent.exe /agent-out/version.txt ./agent/

# Banco SQLite fica em volume; o usuário não-root da imagem precisa escrever nele
ENV Server__DbPath=/data/atalaia.db
RUN mkdir /data && chown $APP_UID /data
VOLUME /data
USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "Atalaia.Server.dll"]
