# Segurança

## Modo laboratório

Toda a Fase 1 opera em **somente leitura**. Funções de escrita estão desativadas por padrão e não possuem implementação.

## Credenciais

- Não há usuário ou senha no código.
- Não há persistência em arquivo nesta fase.
- A opção “Não persistir a credencial” é o comportamento real: a credencial é descartada da memória após a tentativa.
- DPAPI está preparado para uma fase posterior; não é usado para gravar senhas agora.
- O botão Login envia a credencial **uma vez** somente para a F6201B com contrato homologado.
- Cookies ficam em `CookieContainer` na memória e são descartados ao encerrar a sessão, trocar o IP ou fechar o aplicativo.
- O clique em Encerrar sessão envia o POST de logout observado na interface; se a ONT não confirmar, a sessão local mesmo assim é destruída.

## TLS

- A validação de certificado do Windows **não** é desabilitada globalmente.
- Self-signed é aceito somente quando:
  1. o operador marca a opção de confiança local;
  2. o IP remoto é exatamente o selecionado;
  3. o aviso permanece visível na interface.
- Não há `ServerCertificateValidationCallback` estático.

## Logs

Os logs em `%LocalAppData%\TantoTelecom\TantoOntManager\logs\` mascaram:

- serial (início e fim)
- MAC (primeiro e último octeto)
- senhas, tokens, cookies e Authorization
- usuário PPPoE, quando existir em texto

Não registrar corpo autenticado, cookies ou headers de autorização.

## Rede

- Sem varredura ampla.
- Alvos permitidos: `192.168.100.1`, `192.168.1.1` ou IP informado pelo operador.
- A exportação pública grava apenas HTML/certificado/resumo sanitizados em `%LocalAppData%\TantoTelecom\TantoOntManager\diagnostics\`.
- Cookies, Authorization e valores digitados de usuário/senha bloqueiam a exportação.
