@echo off
rem Gera o MSI do agente.
rem
rem   installer\build-msi.cmd <versao> <url-do-servidor> <token>
rem   ex.: installer\build-msi.cmd 1.0.0 https://agent.suaempresa.com.br MEU_TOKEN
rem
rem A URL e o token ficam gravados como padrao dentro do MSI (assim a GPO instala sem parametros).
rem Trate o MSI como segredo: quem le o arquivo le o token. Deixe-o em um share acessivel so a "Domain Computers".
rem
rem Requisitos: .NET SDK e WiX 4.0.6 (versao anterior a taxa de manutencao do WiX 5+/7, sem EULA para aceitar):
rem   dotnet tool install --global wix --version 4.0.6
rem   wix extension add -g WixToolset.Util.wixext/4.0.6
rem NAO atualize para o WiX 5 ou superior sem decidir sobre a taxa (OSMF): o Package.wxs usa o mesmo formato, so a licenca muda.

setlocal
set VERSION=%~1
set SERVER_URL=%~2
set TOKEN=%~3
if "%VERSION%"=="" goto usage
if "%SERVER_URL%"=="" goto usage
if "%TOKEN%"=="" goto usage

set ROOT=%~dp0..
set OUT=%ROOT%\publish\msi
if not exist "%OUT%" mkdir "%OUT%"

echo [1/2] Publicando o agente %VERSION%...
dotnet publish "%ROOT%\src\Atalaia.Agent" -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:Version=%VERSION% ^
  -o "%ROOT%\publish\agent" || exit /b 1

echo [2/2] Gerando o MSI...
wix build "%~dp0Package.wxs" -arch x64 -ext WixToolset.Util.wixext ^
  -d Version=%VERSION% -d ServerUrl=%SERVER_URL% -d Token=%TOKEN% ^
  -d AgentExe="%ROOT%\publish\agent\Atalaia.Agent.exe" ^
  -o "%OUT%\Atalaia-%VERSION%.msi" || exit /b 1

echo.
echo Pronto: %OUT%\Atalaia-%VERSION%.msi
exit /b 0

:usage
echo Uso: installer\build-msi.cmd ^<versao^> ^<url-do-servidor^> ^<token^>
exit /b 1
