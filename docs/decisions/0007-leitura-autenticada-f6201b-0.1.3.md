# ADR 0007 — Leitura autenticada completa F6201B 0.1.3

## Contexto

A homologação 0.1.2 autenticou a F6201B com um POST, mas as tags GET vieram do shell SPA (`*_homepage_lua`, `accessdev_data`) e não das telas Device Information, PON e WAN. O logout oficial não foi enviado.

## Decisão

- Descobrir tags somente por evidência da interface autenticada (`menuTreeJSON`, `MenuPage`, `openLink`, `_type+_tag`).
- Classificar cada tag (`SafeRead`, `BlockedPotentialAction`, `UnknownNotAccessed`, `Duplicate`, `Invalid`) e requisitar somente `SafeRead`.
- Permitir GET `menuView`, `menuData` e `hiddenData` no mesmo IP, sem parâmetros extras.
- Parsers específicos F6201B V9.3.10P8N1 com evidência por campo e resultado parcial.
- POST de logout oficial `/?_type=loginData&_tag=logout_entry` somente no clique em Encerrar sessão.
- Export autenticado com `safe-read-inventory.json` e inspeção do ZIP antes de concluir.

## Consequências

Escrita WAN/PPPoE/VLAN/TR-069, firmware, reset e adivinhação de endpoints continuam em fase de descoberta.
