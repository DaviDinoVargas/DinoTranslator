# DinoTranslator Plus — subir no seu servidor

O que roda no servidor: a API (várias réplicas), PostgreSQL, Redis, o servidor de mídia das
chamadas em grupo (LiveKit), a retransmissão da conexão garantida (coturn) e o Caddy, que cuida do
HTTPS. Tudo em Docker Compose, na pasta
`deploy/`. A versão grátis (chamada por código) não precisa de nada disto.

## 1. O que você precisa

- Um VPS Linux (Ubuntu 24.04 ou parecido). Para começar: 2 vCPU e 4 GB de RAM atendem algumas
  centenas de pessoas conectadas; chamadas em grupo pesam mais em banda do que em CPU.
- Dois nomes no seu domínio apontando (registro A/AAAA) para o IP do VPS:
  `api.seudominio.com` (API) e `midia.seudominio.com` (chamadas em grupo).
- Portas abertas no firewall: **80/tcp e 443/tcp+udp** (HTTPS), **7881/tcp e 7882/udp** (mídia
  do LiveKit), **3478/udp+tcp e 49160–49999/udp** (TURN da conexão garantida). Nenhuma outra:
  banco e Redis ficam só na rede interna do Docker.
- O nome da API sem proxy de CDN (no Cloudflare, "DNS only"): o TURN precisa chegar direto no IP.

## 2. Subir

```bash
# no VPS
sudo apt update && sudo apt install -y docker.io docker-compose-v2 git openssl
git clone https://github.com/DaviDinoVargas/DinoTranslator.git
cd DinoTranslator/deploy
sh gerar-env.sh api.seudominio.com midia.seudominio.com   # cria .env com segredos aleatórios
sudo docker compose up -d --build
sudo docker compose ps        # tudo "running"/"healthy" em 1–2 minutos
curl https://api.seudominio.com/health/pronto              # Healthy
```

O banco é criado e migrado sozinho na primeira subida (uma réplica migra, as outras esperam).

## 3. Contas e planos

No app: tela inicial → cartão **Plus** → servidor `api.seudominio.com` → **Criar conta**.
Toda conta nasce no plano grátis. Para liberar a versão paga a alguém:

```bash
sudo docker compose exec api dotnet DinoTranslator.Server.dll users plan <usuario> plus --dias 30
sudo docker compose exec api dotnet DinoTranslator.Server.dll users list
```

Outros comandos (`users add`, `reset-password`, `disable`, `enable`, `remove`) estão em
`src/DinoTranslator.Server/README.md`. Para um servidor só de convidados: `CADASTRO_ABERTO=false`
(só você cria contas) ou `CODIGO_CADASTRO=<código de 12+ caracteres>` (o app pede o código), no
`.env`, e `docker compose up -d` de novo.

## 4. Atualizar

```bash
cd DinoTranslator && git pull && cd deploy && sudo docker compose up -d --build
```

Se o `.env` foi criado antes da conexão garantida, acrescente o segredo do TURN uma vez:
`echo "TURN_SECRET=$(openssl rand -hex 32)" >> .env`.

As réplicas são trocadas e o Caddy só manda tráfego às que respondem em `/health/pronto`.

## 5. Backup

```bash
# diário, por exemplo num cron do root
sudo docker compose exec -T postgres pg_dump -U dinotranslator -Fc dinotranslator > backup-$(date +%F).dump
# restaurar
sudo docker compose exec -T postgres pg_restore -U dinotranslator -d dinotranslator --clean < backup-AAAA-MM-DD.dump
```

Guarde também o `deploy/.env` (sem ele, as sessões e as chamadas param de valer).

## 6. Crescer sem ficar lento

- **Mais conexões e mensagens**: `API_REPLICAS=4` no `.env` e `docker compose up -d`. A API não
  guarda estado: sessões, presença e eventos em tempo real passam pelo Redis, então qualquer
  réplica atende qualquer pessoa.
- **Banco**: os pontos quentes (mensagens por canal, membros por conta, sessões) têm índices; o
  acesso e as sessões ficam em cache curto, sem uma consulta por requisição. Quando crescer,
  aumente `shared_buffers` e a máquina do PostgreSQL, ou mova-o para um serviço gerenciado
  (troque só `ConnectionStrings__Database`).
- **Chamadas em grupo**: o LiveKit já usa o Redis; para mais salas simultâneas, suba outro nó do
  LiveKit em outra máquina apontando para o mesmo Redis.
- **Conexão garantida**: só as chamadas 1:1 que não fecham direto passam pelo coturn (cada uma
  usa a banda do vídeo nos dois sentidos). A cota total é de 1.200 alocações e 10 Mbit/s por
  sessão (`total-quota`, `max-bps` no `docker-compose.yml`); para mais, suba outro coturn com o
  mesmo `TURN_SECRET` em outra máquina e acrescente o endereço dele em `Ice__TurnUrls`.
- **Vários servidores**: a API pode rodar em mais de uma máquina atrás de um balanceador, todas
  com o mesmo `.env` (mesmo banco, mesmo Redis, mesma chave JWT).

Medido com `tests/carga/carga.ps1` (duas instâncias da API, PostgreSQL e Redis, tudo num mesmo
PC — i7-10700KF —, junto com o gerador de carga):

| Contas conectadas | Mensagens/s aceitas | Entregas/s (9 leitores cada) | Entregues | Envio p50 / p95 | Entrega p50 / p95 / p99 |
|-------------------|---------------------|------------------------------|-----------|-----------------|-------------------------|
| 300               | 132                 | 1.193                        | 100%      | 3,6 / 27 ms     | 3,6 / 26 / 94 ms        |
| 1.000             | 397                 | 3.574                        | 100%      | 6,7 / 26 ms     | 6,6 / 26 / 238 ms       |

## 7. Segurança do servidor

- Só o Caddy fica exposto (HTTPS com certificado automático); API, banco e Redis estão na rede
  interna. A imagem da API é "chiseled": sem shell, usuário sem privilégios.
- Segredos só no `.env` (fora do Git, gerados aleatoriamente, lidos só pelo dono do arquivo).
- Limites por IP (login, cadastro) e por conta (mensagens, ações); detalhes em
  [SEGURANCA.md](SEGURANCA.md).
- Mantenha o VPS atualizado (`unattended-upgrades`) e o acesso SSH só por chave.
