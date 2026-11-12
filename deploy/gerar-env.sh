#!/bin/sh
# Cria o deploy/.env com segredos aleatórios (rode uma vez, no servidor). Nunca versione o .env.
#   sh gerar-env.sh api.seudominio.com midia.seudominio.com
set -eu
cd "$(dirname "$0")"
if [ -f .env ]; then
  echo ".env já existe; apague-o se quiser gerar outro (as senhas do banco mudariam)." >&2
  exit 1
fi
API="${1:?informe o domínio da API, ex.: api.seudominio.com}"
MIDIA="${2:?informe o domínio de mídia, ex.: midia.seudominio.com}"
secret() { openssl rand -base64 "$1" | tr -d '\n=+/' | cut -c1-"$2"; }
umask 077
cat > .env <<EOF
DOMINIO_API=$API
DOMINIO_MIDIA=$MIDIA
POSTGRES_PASSWORD=$(secret 48 40)
REDIS_PASSWORD=$(secret 48 40)
JWT_SIGNING_KEY=$(openssl rand -base64 32 | tr -d '\n')
LIVEKIT_API_KEY=dino$(secret 12 10)
LIVEKIT_API_SECRET=$(secret 64 48)
TURN_SECRET=$(secret 64 48)
TURN_IP_EXTERNO=
CADASTRO_ABERTO=true
CODIGO_CADASTRO=
API_REPLICAS=2
EOF
echo "deploy/.env criado (só o dono lê). Agora: docker compose up -d --build"
