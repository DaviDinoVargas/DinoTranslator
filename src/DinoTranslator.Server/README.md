# DinoTranslator.Server

API do DinoTranslator: login, sinalização das chamadas, chats e tradução.

## Configuração obrigatória

| Chave                  | Variável de ambiente     | Descrição                                          |
|------------------------|--------------------------|----------------------------------------------------|
| `Jwt:SigningKey`       | `Jwt__SigningKey`        | Chave HMAC em base64 com pelo menos 32 bytes        |
| `ConnectionStrings:Database` | `ConnectionStrings__Database` | Opcional. Padrão: `data/dinotranslator.db` |
| `ReverseProxy:KnownNetworks` | `ReverseProxy__KnownNetworks__0` | Redes do proxy (Caddy) cujos `X-Forwarded-*` são aceitos |

Gerar uma chave:

```bash
openssl rand -base64 32
```

Em desenvolvimento, guarde a chave em user-secrets (fica fora do repositório):

```bash
dotnet user-secrets set "Jwt:SigningKey" "<chave>" --project src/DinoTranslator.Server
```

O servidor não sobe sem uma chave válida.

## Contas

Não existe cadastro pela rede: as duas contas são criadas no próprio servidor, a partir da pasta do projeto (ou `/app` no contêiner):

```bash
dotnet run -- users add dino --nome "Dino" --idioma pt-BR --genero masculino
dotnet run -- users add <usuario> --nome "<nome>" --idioma ru-RU
dotnet run -- users list
dotnet run -- users reset-password <usuario>
dotnet run -- users unlock <usuario>
dotnet run -- users remove <usuario>
```

- A senha é pedida sem eco (ou lida de um pipe em scripts): 12 a 128 caracteres, sem conter o nome de usuário.
- Uma terceira conta é recusada.
- Trocar a senha encerra todas as sessões abertas da conta.

## Endpoints

| Método | Rota                | Autenticação | Limite por IP |
|--------|---------------------|--------------|---------------|
| GET    | `/health`           | —            | —             |
| POST   | `/api/auth/login`   | —            | 10/min        |
| POST   | `/api/auth/refresh` | —            | 30/min        |
| POST   | `/api/auth/logout`  | —            | 30/min        |
| GET    | `/api/me`           | JWT          | —             |

Os controles de segurança estão descritos em [docs/SEGURANCA.md](../../docs/SEGURANCA.md).
