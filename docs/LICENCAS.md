# Licenças: o que pode ser vendido

A versão **Plus** é um serviço pago, então só usa componentes cuja licença permite uso comercial.
A versão grátis (chamada por código) segue com tudo, inclusive o que é só para uso não comercial.

## No serviço pago (versão Plus) — tudo permite uso comercial

| Componente | Para quê | Licença | Obrigação |
|------------|----------|---------|-----------|
| .NET, ASP.NET Core, SignalR, EF Core | app e servidor | MIT | aviso de licença |
| Npgsql (EF Core) | acesso ao PostgreSQL | PostgreSQL License | aviso |
| StackExchange.Redis | cache e tempo real | MIT | aviso |
| PostgreSQL 18 | banco | PostgreSQL License | — |
| Valkey 8 | cache, presença e tempo real entre instâncias | BSD-3-Clause | aviso |
| Caddy 2 | HTTPS e balanceamento | Apache-2.0 | aviso |
| LiveKit server 1.13 / livekit-client 2.23 | chamadas em grupo (SFU) | Apache-2.0 | aviso |
| Whisper (modelos), whisper.cpp, Whisper.net | reconhecimento de fala | MIT | aviso |
| faster-whisper, CTranslate2 | reconhecimento e tradução no motor auxiliar | MIT | aviso |
| **MarianMT / OPUS-MT** (Helsinki-NLP) | **tradução local na versão Plus** | **CC BY 4.0** | **atribuição** (tela Licenças do app) |
| Silero VAD | detector de fala | MIT | aviso |
| DeepFilterNet3 (via @workadventure/noise-suppression) | supressor de ruído | MIT / Apache-2.0 | aviso |
| RNNoise, GTCRN (via @sapphi-red/web-noise-suppressor) | supressores de ruído | BSD-3-Clause, MIT | aviso |
| ONNX Runtime | motor local | MIT | aviso |
| Python 3.11 (incorporável) | motor auxiliar | PSF | aviso |
| Bibliotecas CUDA da NVIDIA (cuBLAS, cuDNN, NVRTC) | placa de vídeo | NVIDIA CUDA EULA (redistribuição permitida junto do app) | seguir a EULA |
| Fonte Inter | interface | SIL OFL 1.1 | aviso |
| Azure Speech (se a pessoa configurar) | reconhecimento e tradução na nuvem | serviço pago da Microsoft, com a chave da própria pessoa | termos da Microsoft |

## Fora do serviço pago

| Componente | Licença | Por que fica de fora | Onde aparece |
|------------|---------|----------------------|--------------|
| **NLLB-200** (Meta), 600M e 1.3B | **CC BY-NC 4.0** (só uso não comercial) | proíbe uso comercial | só na versão grátis. Na Plus, o app usa o MarianMT e nunca cai no NLLB, nem como reserva (`EngineChoice.ForPlus`, testes em `CommercialEngineTests`) |
| Redis 8 | AGPLv3 / RSAL / SSPL | obrigações de rede e restrições de serviço | trocado pelo Valkey no deploy |
| ntfy.sh (serviço público do encontro automático) | serviço gratuito com uso justo | não é para tráfego de um produto | só na versão grátis; para escala, hospede o próprio ntfy (Apache-2.0) e aponte o `Encontro:Servidor` para ele |

### Ponto para conferir com um advogado

Mesmo na versão grátis, o NLLB vai junto no instalador de um produto que tem uma parte paga.
A licença proíbe uso "principalmente voltado a vantagem comercial"; a versão grátis em si não é
vendida, mas o caminho mais seguro é tornar o MarianMT o tradutor padrão em todo o app e deixar o
NLLB como opção que a própria pessoa liga. Essa decisão é do dono do produto.

## Atribuição

O app mostra os componentes e as licenças em **Licenças** (link no rodapé da tela inicial),
inclusive o crédito do OPUS-MT exigido pela CC BY 4.0: *OPUS-MT, Jörg Tiedemann e Santhosh
Thottingal, Universidade de Helsinque (Helsinki-NLP), CC BY 4.0*.
