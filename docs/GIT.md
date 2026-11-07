# DinoTranslator — Fluxo Git

Repositório: <https://github.com/DaviDinoVargas/DinoTranslator>

## Branches

- `main` — sempre compila e passa nos testes. Não recebe commits diretos (exceto o commit inicial).
- **Uma branch por módulo**, no formato `<área>/<módulo>`, sempre criada a partir da `main` atualizada.

| Área         | Exemplos                                                        |
|--------------|-----------------------------------------------------------------|
| `docs`       | `docs/planejamento`                                             |
| `base`       | `base/estrutura`                                                |
| `servidor`   | `servidor/autenticacao`, `servidor/sinalizacao` (modo servidor, opcional) |
| `cliente`    | `cliente/shell`, `cliente/perfil`, `cliente/codigo-chamada`, `cliente/chamada`, `cliente/tela`, `cliente/traducao`, `cliente/legendas`, `cliente/design` |
| `shared`     | `shared/idiomas`                                                |
| `chat`       | `chat/texto`, `chat/falas`                                      |
| `instalador` | `instalador/wix`                                                |
| `infra`      | `infra/deploy`                                                  |

## Commits

- **Vários commits por branch, um por tema** — cada commit compila e passa nos testes.
- Formato [Conventional Commits](https://www.conventionalcommits.org/pt-br/), em português e no imperativo:

  ```
  tipo(escopo): descrição curta

  Corpo opcional explicando o porquê.
  ```

- Tipos: `feat`, `fix`, `docs`, `test`, `build`, `chore`, `refactor`, `style`, `perf`, `ci`.
- Exemplos:
  - `feat(auth): adiciona login com JWT e refresh rotativo`
  - `test(auth): cobre bloqueio após tentativas erradas`
  - `build(desktop): compila o módulo de mídia com esbuild`
- Sem trailers de coautoria.

## Integração

- O módulo entra na `main` com `git merge --no-ff`, preservando os commits por tema e marcando onde o módulo entrou. Pode ser via pull request no GitHub quando houver revisão.
- Versões marcadas com tags `vMAJOR.MINOR.PATCH` na `main`.

## Nunca versionar

Segredos (`.env`, `appsettings.*.local.json`, user-secrets), bancos de dados (`*.db`), certificados (`*.pfx`), builds (`bin/`, `obj/`, `artifacts/`) e `node_modules/`.
