# Procedimento de laboratório — Fase 1

## Preparação

1. Conecte **uma** ONT por cabo Ethernet ao computador.
2. Não conecte várias ONTs ao mesmo tempo: o IP padrão se repete.
3. Abra o Tanto ONT Manager.
4. Confirme o indicador `Laboratório — somente leitura`.

## Rede

Para `192.168.100.1` (F6201B de laboratório):

```text
IP sugerido: 192.168.100.10
Máscara: 255.255.255.0
Gateway: 192.168.100.1
```

Para `192.168.1.1`:

```text
IP sugerido: 192.168.1.10
Máscara: 255.255.255.0
Gateway: 192.168.1.1
```

O aplicativo **não** aplica essa configuração. Ajuste a placa manualmente, se necessário.

## Detecção

1. Selecione a interface Ethernet.
2. Confirme o IPv4 atual e o estado do cabo.
3. Selecione o IP conhecido ou informe um IP personalizado.
4. Mantenha o aviso de certificado local visível.
5. Clique em `Testar conexão` e depois em `Detectar ONT`.
6. Confira confiança, evidências, status HTTPS e hash curto.
7. Clique em `Exportar diagnóstico público` se precisar arquivar a página pública.
8. Clique em `Exportar diagnóstico público` se precisar arquivar a página pública.
9. Se a F6201B foi identificada com confiança suficiente, informe a credencial da etiqueta e clique **uma vez** em `Login`.
10. Após o login, o aplicativo lê automaticamente Device, PON, WAN Status e WAN Config por GET homologado. Confira as abas Dispositivo, PON e WAN. Use `Exportar diagnóstico autenticado` e confira a inspeção do ZIP.
11. Clique em `Encerrar sessão` ao terminar. O aplicativo envia no máximo um POST de logout oficial e descarta os cookies.

## Observação passiva de GETs (fallback 0.1.8-lab)

1. A leitura cotidiana **não** depende do WebView2. O observador permanece só como ferramenta de laboratório e fallback.
2. Com a F6201B autenticada em modo laboratório, clique em `Observar navegação GET` e confirme.
3. No WebView2 isolado, feche o baseline do shell e use os botões Device / PON / WAN Status / WAN Config.
4. Durante os 20 s de cada captura, navegue **manualmente** na tela correspondente da ONT.
5. Confira os contadores: POST de configuração deve permanecer 0.
6. `Exportar observação sanitizada` e inspecione IncludesCookies/Credentials/Tokens/RawAuthenticatedBody = false.
7. `Promover contrato de leitura` gera só um JSON local em `diagnostics/proposals`; o adaptador F6201B não muda.
8. Feche o observador: a pasta WebView2 e os cookies temporários são destruídos.

## Mapeamento bloqueado WAN/PPPoE (Fase 2A)

Esta fase **não configura** a ONT. `ConfigurationRequestsSent` permanece 0.

1. Detecte a F6201B, autentique pelo fluxo homologado e confirme a firmware `V9.3.10P8N1`.
2. Abra `Observar navegação GET` e confirme o WebView2 isolado.
3. Navegue **manualmente** até Internet → WAN, escolha o perfil e, se necessário, preencha dados fictícios de laboratório.
4. Digite exatamente `MAPEAR F6201B` (sem variação de maiúsculas ou espaços) e clique em `Iniciar captura bloqueada`.
5. Firmware Unconfirmed recusa a captura e mantém somente leitura. Firmware incompatível recusa a captura e encerra a sessão autenticada.
6. Clique **manualmente** em Apply/Save na interface oficial. O aplicativo intercepta no `WebResourceRequested` **antes da rede**, registra só a estrutura sanitizada e bloqueia o envio.
7. A captura termina no primeiro candidato. Para outra tentativa, feche o observador e abra uma nova sessão.
8. `Exportar proposta sanitizada` grava em `%LocalAppData%\TantoTelecom\TantoOntManager\diagnostics\proposals\write-contract\`.
9. `Promover contrato de gravação` salva só um JSON local `CandidateOnly`. Não altera o adaptador F6201B nem a allowlist de escrita (continua vazia).
10. A Fase 2B só pode começar depois de captura real bloqueada, revisão humana, backup, rollback e autorização separada.

## Diagnóstico passivo de capacidade de escrita (Fase 2A.1)

Esta fase **não configura** a ONT e **não contorna** permissões. `ConfigurationRequestsSent` permanece 0. A allowlist de escrita permanece vazia.

1. Detecte a F6201B, autentique pelo fluxo homologado e confirme a firmware `V9.3.10P8N1`.
2. Abra `Observar navegação GET` e inicie `Mapear gravação WAN/PPPoE — bloqueado` com a confirmação `MAPEAR F6201B`.
3. Navegue **manualmente** até Internet → WAN. Abra os perfis visíveis e percorra a página até o rodapé.
4. Clique em `Inspecionar estrutura do DOM`. O WebView2 executa só o script fixo de estrutura (nomes/ids/tipos, opções públicas, disabled/readonly/hidden). Nenhum clique automático, nenhum preenchimento e nenhum valor de senha.
5. Confira a aba `Capacidade de escrita`: conta observada, firmware, perfis WAN, tipos de conexão, PPPoE/criar perfil/Apply-Save (Sim/Não/Não confirmado), controles bloqueados, evidências e próximo passo.
6. Se PPPoE e criação não estiverem disponíveis, a conclusão tipada será uma de `PppoeOptionUnavailable`, `ReadOnlyAccount`, `PresetLocked` ou `InsufficientEvidence`. A ausência isolada de um botão **não** basta para concluir conta somente leitura ou preset.
7. `Exportar diagnóstico de capacidade` grava em `%LocalAppData%\TantoTelecom\TantoOntManager\diagnostics\proposals\write-capability\` os arquivos `write-capability-report.json`, `write-capability-summary.txt` e `manifest.json`.
8. `Promover contrato de gravação` permanece desabilitado quando candidatos = 0, PPPoE indisponível, Apply/Save ausente, firmware não confirmada ou a conta aparentar leitura/preset.

Resultado homologado nesta firmware/conta `admin`: IP Type só DHCP/Static; sem PPPoE, Create New Item, Add, Apply ou Save; candidatos interceptados = 0; conclusão `PppoeOptionUnavailable`. Nenhuma tentativa de contornar permissões.

## Resultado esperado nesta fase

- Resposta HTTPS do endereço testado (HTTP pode estar indisponível)
- Identificação pública `ZTE ZXHN F6201B` com evidências suficientes
- Login homologado somente para firmware `V9.3.10P8N1`: um POST, cookies em memória
- Hardware, firmware, boot, PON, temperatura, potência e dois perfis WAN lidos automaticamente por GET homologado após o login, quando presentes nas respostas
- Serial e MAC mascarados na UI e na exportação

## Proibido no laboratório desta fase

- Factory reset pelo aplicativo
- Alterar WAN/VLAN/PPPoE/TR-069
- Ativar Telnet/SSH
- Enviar requisições a caminhos não observados na interface pública
