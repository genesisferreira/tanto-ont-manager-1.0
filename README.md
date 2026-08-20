# Tanto ONT Manager 1.0

Ferramenta da **Tanto Telecom** para identificação, diagnóstico e **desbloqueio universal** de ONTs ZTE conectadas por cabo de rede.

## Status

| Fase | Status | Descrição |
|------|--------|-----------|
| Fase 1 | Completa | Detecção, login e leitura somente leitura (F6201B V9.3.10P8N1) |
| Fase 2A | Completa | Interceptação e bloqueio de POSTs de escrita |
| Fase 2A.1 | Completa | Diagnóstico passivo de capacidade de escrita |
| **Fase 3** | **Ativa** | **Desbloqueio universal com backup automático e rollback** |

Versão: `0.1.8.1-lab`. Processamento: uma ONT por vez.

## Modelos suportados

| Modelo | Firmware | Estratégia | Status |
|--------|----------|------------|--------|
| ZTE F6201B | V9.3.10P8N1 | Config.bin (ZCU) | Homologado |
| ZTE F670L | V1 / V9 | Config.bin (ZCU) | Homologado |
| ZTE F6600P | V9 | Config.bin → zteOnu fallback | Chave parcial |
| ZTE F6645P | V9 | Config.bin → zteOnu fallback | Chave parcial |
| ZTE H198A | V3 | zteOnu + Telnet | Homologado |
| ZTE H199A | — | zteOnu + Telnet | Homologado |
| ZTE H3601 | V9.1 | Config.bin / zteOnu | Homologado |
| ZTE F6601P | — | Config.bin type 6 | Suporte limitado |

Modelo futuro sem adaptador: Zyxel PM5301-T7.

## Segurança

- Backup automático com SHA-256 **antes** de qualquer modificação; a operação aborta se o backup falhar
- Rollback disponível imediatamente após o desbloqueio
- Sanitização de logs (credenciais, cookies e tokens não são gravados)
- Telnet só LAN (WAN desabilitada por padrão após unlock)
- Allowlist de GETs autenticados preservada no fluxo de leitura; download/upload de `config.bin` usa endpoints de backup homologados
- Sem varredura de rede e sem desabilitar a validação TLS do Windows

## Requisitos

- Windows 10/11
- .NET 8 SDK
- Microsoft Edge WebView2 Runtime (Evergreen)
- Cabo Ethernet até a ONT
- IPv4 na mesma sub-rede do equipamento
- **Python 3.11** em `D:\Tools\Python311\python.exe` (para ZCU)
- **zte-config-utility** em `D:\Tools\zte-config-utility` (pacote `zcu` instalado no Python acima)
- **zteOnu** em `D:\Tools\zteOnu\zteOnu.exe`

## Instalação das ferramentas externas

Caminhos esperados pelo app (já usados pelo instalador `install-tools.ps1`):

```powershell
# Python 3.11 em D:\Tools\Python311 (não depende do python do PATH)
git clone https://github.com/mkst/zte-config-utility.git D:\Tools\zte-config-utility
D:\Tools\Python311\python.exe -m pip install -r D:\Tools\zte-config-utility\requirements.txt
D:\Tools\Python311\python.exe -m pip install -e D:\Tools\zte-config-utility
# zteOnu: o asset Windows é um .tar.gz, não um .exe solto
# https://github.com/Septrum101/zteOnu/releases/download/v0.1.3/zteOnu_0.1.3_windows_amd64.tar.gz
# Extraia zteOnu.exe para D:\Tools\zteOnu\zteOnu.exe
```

## Compilar e executar

```powershell
cd D:\Projetos\tanto-ont-manager-1.0
dotnet restore
dotnet build
dotnet test
dotnet run --project src/TantoOntManager.App/TantoOntManager.App.csproj
```

Logs sanitizados: `%LocalAppData%\TantoTelecom\TantoOntManager\logs\`

Backups: `%LocalAppData%\TantoTelecom\TantoOntManager\backups\`

Diagnósticos públicos: `%LocalAppData%\TantoTelecom\TantoOntManager\diagnostics\`

## O que a leitura (Fase 1 / 2A) faz

- Lista adaptadores Ethernet e o IPv4 atual
- Testa somente `192.168.100.1`, `192.168.1.1` ou um IP informado pelo operador
- Login da F6201B V9.3.10P8N1: um POST no endpoint observado, cookies só em memória
- Leitura autenticada GET por tags evidenciadas e classificadas SafeRead
- Observação passiva dos GETs dinâmicos em WebView2 isolado
- Diagnóstico passivo de capacidade de escrita (Fase 2A.1)

## O que o desbloqueio (Fase 3) faz

- Backup do `config.bin` original antes de qualquer escrita
- Superusuário persistente, desabilitar TR-069, Telnet/SSH LAN e opcionalmente bridge
- Estratégia por perfil (config.bin via ZCU, zteOnu + Telnet, ou híbrido)
- Rollback pelo ticket de backup

## Regras de laboratório

1. Nunca desbloquear sem backup
2. Nunca testar em modelo desconhecido
3. Validar roundtrip primeiro na F6201B V9.3.10P8N1
4. Não adivinhar senhas nem usar credenciais de etiquetas
5. Não varrer a rede

## Arquitetura

Ver `docs/architecture.md`, `docs/security.md`, `docs/device-adapter-contract.md` e `docs/laboratory-procedure.md`.
