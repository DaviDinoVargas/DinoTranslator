# DinoTranslator.Server

API da versão paga do DinoTranslator: contas, planos, grupos, chat e chamadas em grupo.

Feita para rodar em várias instâncias atrás de um proxy (Caddy), sem estado preso a nenhuma
delas: os dados ficam no PostgreSQL e, com Redis, o cache e as mensagens em tempo real (SignalR)
são compartilhados entre as instâncias. Sem Redis, tudo funciona numa instância só.

## Configuração obrigatória

| Chave                  | Variável de ambiente     | Descrição                                          |
|------------------------|--------------------------|----------------------------------------------------|
| `Jwt:SigningKey`       | `Jwt__SigningKey`        | Chave HMAC em base64 com pelo menos 32 bytes        |
| `ConnectionStrings:Database` | `ConnectionStrings__Database` | PostgreSQL em produção: `Host=…;Database=…;Username=…;Password=…` (as migrações rodam ao subir). Sem isso, SQLite em `data/dinotranslator.db` (só para desenvolvimento) |
| `Redis:Configuration`  | `Redis__Configuration`   | Opcional. Ex.: `redis:6379,password=…`. Liga o cache compartilhado e o SignalR entre instâncias |
| `ReverseProxy:KnownNetworks` | `ReverseProxy__KnownNetworks__0` | Redes do proxy (Caddy) cujos `X-Forwarded-*` são aceitos |
| `Ice:StunUrls`, `Ice:TurnUrls` | `Ice__TurnUrls__0` … | Endereços do coturn (`stun:`, `turn:`, `turns:`) |
| `Ice:TurnSecret` | `Ice__TurnSecret` | Igual ao `static-auth-secret` do coturn (32+ caracteres) |

Gerar uma chave:

```bash
openssl rand -base64 32
```

Em desenvolvimento, guarde a chave em user-secrets (fica fora do repositório):

```bash
dotnet user-secrets set "Jwt:SigningKey" "<chave>" --project src/DinoTranslator.Server
```

O servidor não sobe sem uma chave válida. O token JWT só é aceito no cabeçalho `Authorization`, nunca na URL.

## Contas e planos

| Chave | Padrão | Descrição |
|-------|--------|-----------|
| `Auth:OpenRegistration` | `true` | Cadastro pelo app (`POST /api/auth/register`). Desligado, só o comando abaixo cria contas |
| `Auth:RegistrationCode` | — | Se preenchido (12+ caracteres), o cadastro exige este código |
| `Auth:MaxUsers` | `0` | Limite de contas (0 = sem limite) |
| `Auth:RegisterPermitsPerHour` | `5` | Cadastros por IP por hora |
| `Auth:AccessTokenMinutes` | `10` | Validade do token de acesso; plano e suspensão vão nele |

Toda conta nasce no plano **grátis** (entra e cuida do perfil). A versão paga — grupos, chat e
chamadas em grupo — exige o **Plus**, que o dono do servidor ativa enquanto não há cobrança
automática. Comandos, a partir da pasta do projeto (ou `/app` no contêiner):

```bash
dotnet run -- users add dino --nome "Dino" --idioma pt-BR --genero masculino
dotnet run -- users add <usuario> --nome "<nome>" --idioma ru-RU
dotnet run -- users list
dotnet run -- users reset-password <usuario>
dotnet run -- users unlock <usuario>
dotnet run -- users remove <usuario>
dotnet run -- users plan <usuario> plus --dias 30   # sem --dias: sem prazo; renovar soma ao que resta
dotnet run -- users plan <usuario> gratis
dotnet run -- users disable <usuario>               # suspende e encerra as sessões
dotnet run -- users enable <usuario>
```

- A senha é pedida sem eco (ou lida de um pipe em scripts): 12 a 128 caracteres, sem conter o nome de usuário.
- Suspensão e troca de senha derrubam as sessões abertas em até 20 s em todas as instâncias (cache do carimbo da sessão).
- Trocar a senha encerra todas as sessões abertas da conta.

## Endpoints

| Método | Rota                | Autenticação | Limite por IP |
|--------|---------------------|--------------|---------------|
| GET    | `/health`           | —            | —             |
| GET    | `/health/pronto`    | —            | —             |
| POST   | `/api/auth/register`| —            | 5/hora        |
| POST   | `/api/auth/login`   | —            | 10/min        |
| POST   | `/api/auth/refresh` | —            | 30/min        |
| POST   | `/api/auth/logout`  | —            | 30/min        |
| GET    | `/api/me`           | JWT          | —             |
| GET    | `/api/ice-servers`  | JWT          | —             |
| WS     | `/hubs/sala`        | JWT no cabeçalho | 50 mensagens/s por conexão |

## Versão paga (exige Plus)

Tudo abaixo exige token de conta com Plus válido; conta grátis recebe 403.

| Método | Rota | Quem pode |
|--------|------|-----------|
| GET | `/api/eu/grupos` | a própria conta |
| POST | `/api/grupos` `{name}` | qualquer um (até 20 grupos próprios e 200 no total); nasce com `#geral` e o canal de voz |
| GET/PATCH/DELETE | `/api/grupos/{id}` | membros veem; administradores renomeiam; o dono apaga |
| POST | `/api/grupos/{id}/canais` `{name, kind}` | administradores (até 50 canais) |
| DELETE | `/api/canais/{id}` | administradores |
| POST | `/api/grupos/{id}/convites` `{maxUses?, hours?}` | membros (código de 10 caracteres; até 30 dias) |
| GET / POST | `/api/convites/{código}` / `/entrar` | qualquer conta Plus (grupo de até 500 membros) |
| DELETE | `/api/grupos/{id}/membros/{conta}` | a própria pessoa (sair) ou alguém de papel maior (remover) |
| PUT | `/api/grupos/{id}/membros/{conta}/papel` `{role}` | só o dono (Owner passa o grupo) |
| GET | `/api/canais/{id}/mensagens?antes=&limite=` | membros do grupo ou as duas pessoas da conversa |
| POST | `/api/canais/{id}/mensagens` `{content, replyToId?}` | idem; até 4.000 caracteres; 10 por 10 s por conta |
| PATCH / DELETE | `/api/mensagens/{id}` | edita quem escreveu; apaga quem escreveu ou administrador |
| GET / POST | `/api/amigos` `{userName}` | pedir; se a outra pessoa já pediu, vira amizade |
| DELETE | `/api/amigos/{conta}` | desfazer, recusar ou cancelar |
| POST / DELETE | `/api/bloqueios/{conta}` | bloqueado não pede amizade nem manda mensagem direta |
| GET / POST | `/api/conversas` / `/api/conversas/{conta}` | conversa direta só entre amigos ou quem está num mesmo grupo |
| WS | `/hubs/app` | eventos: mensagens, digitando, presença, mudanças de grupo e de amigos |

- A mensagem é guardada como foi escrita, com o idioma de quem escreveu; cada app traduz para o idioma de quem lê, com o próprio motor. O servidor não traduz nem envia texto a serviços de fora.
- O que a pessoa não pode ver responde 404, igual ao que não existe.
- Quem sai ou é removido de um grupo para de receber os eventos dele na hora, em qualquer instância.
- Presença: com Redis, cada instância renova as próprias conexões a cada 30 s; se uma instância cair, quem estava nela fica offline em até 90 s.

Os controles de segurança estão descritos em [docs/SEGURANCA.md](../../docs/SEGURANCA.md).
