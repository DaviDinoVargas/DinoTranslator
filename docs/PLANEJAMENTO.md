# DinoTranslator — Planejamento

> Chamada de vídeo com tradução simultânea em texto entre duas pessoas. No modo padrão não há servidor: a conexão é feita por um código de chamada e vai direto de um computador ao outro.
> Versão 2 · 07/11/2026

---

## 1. Requisitos

| #   | Requisito                                          | Como o plano atende                                                                 |
|-----|----------------------------------------------------|-------------------------------------------------------------------------------------|
| R1  | Tradução ao vivo, em texto, enquanto a pessoa fala | Legenda provisória palavra a palavra e legenda final na tela, sem voz sintetizada (seção 4) |
| R2  | Tradução assertiva                                 | Tradução com contexto da conversa, glossário, gênero e tratamento (4.3)             |
| R3  | Ping baixo                                         | Mídia P2P direta (WebRTC), buffer de jitter mínimo (5.1)                            |
| R4  | Câmera dos dois na tela                            | Dois quadros sempre visíveis, inclusive durante o compartilhamento de tela (6)      |
| R5  | Áudio da mais alta qualidade                       | Opus 48 kHz até 128 kb/s, FEC, modo alta fidelidade com fones (5.2)                  |
| R6  | Compartilhamento de tela até 2K                    | 2560×1440 a até 30 fps, AV1/VP9/H.264, prioridade à nitidez (5.4)                    |
| R7  | Chat de texto com tradutor                         | Mensagens traduzidas automaticamente, com o original disponível (6.3)               |
| R8  | Segundo chat com as falas em forma de mensagem     | Painel "Falas": cada trecho falado vira uma mensagem com original e tradução (6.3)  |
| R9  | Layout próprio, não genérico                       | Identidade visual própria, definida em [DESIGN.md](DESIGN.md)                        |
| R10 | Só quem recebe o código entra na chamada           | Código de uso único, com validade, que carrega a chave da chamada (3.2)             |
| R11 | Executável com instalador e desinstalador próprios | WiX (Burn) com interface própria em WPF, no visual do app (7)                       |
| R12 | Desenvolvido em C#                                 | App e instalador em C#/.NET                                                         |
| R13 | App seguro                                         | Mídia cifrada ponta a ponta, nenhum servidor, app travado (8 e [SEGURANCA.md](SEGURANCA.md)) |
| R14 | Sem servidor e sem login (servidor opcional)       | Conexão por código de convite e de resposta (3.2); servidor com login mantido como modo opcional (3.4) |
| R15 | Idiomas: português (Brasil), inglês, ucraniano, russo e espanhol | Cada pessoa escolhe o próprio idioma; qualquer par é traduzido (4)    |

## 2. Premissas

- Os dois usam **Windows 10 (versão 2004 ou mais nova) ou 11**.
- Uso **pessoal**, entre duas pessoas por chamada.
- Cada pessoa fala **um idioma por chamada**, escolhido no perfil: `pt-BR`, `en-US`, `uk-UA`, `ru-RU` ou `es-ES`.
- Os códigos da chamada são trocados por um mensageiro que vocês já usam (Telegram, WhatsApp etc.).
- A conexão direta entre as redes dos dois funciona. Se não funcionar em algum caso, o ajuste fica para depois (ver riscos).

---

## 3. Arquitetura

```
┌───────────────────────────┐                      ┌───────────────────────────┐
│ App Windows · anfitrião   │   mídia P2P direta   │ App Windows · convidado   │
│ WPF + Blazor Hybrid (C#)  │◄════════════════════►│ WPF + Blazor Hybrid (C#)  │
│ WebRTC do WebView2        │ câmera · áudio · 2K  │ WebRTC do WebView2        │
│ tradução dos dois lados   │ legendas · chat      │                           │
└─────────────┬─────────────┘                      └───────────────────────────┘
              │ HTTPS, só a partir do anfitrião
              ▼
   serviços de reconhecimento de fala e tradução
```

No modo padrão não há servidor do DinoTranslator. Tudo o que a chamada transporta — câmera, áudio, tela, legendas e chat — vai direto de um computador ao outro, cifrado de ponta a ponta. Cada lado só consulta um servidor STUN público para descobrir o próprio endereço na internet; nenhum dado da chamada passa por ele.

### 3.1 App desktop (Windows)

| Peça                      | Tecnologia                                           | Papel                                                                 |
|---------------------------|------------------------------------------------------|-----------------------------------------------------------------------|
| Janela e serviços nativos | .NET 10 + WPF                                        | Janela com barra de título própria, dados locais cifrados com DPAPI, integração com o instalador |
| Interface                 | Blazor Hybrid (BlazorWebView) + CSS próprio          | Todas as telas em componentes Razor (C#); liberdade total de visual   |
| Mídia                     | WebRTC do Chromium via WebView2 + módulo JavaScript  | Câmera, áudio, tela, conexão P2P, canal de dados, estatísticas de ping |
| Código da chamada         | C#                                                   | Monta, valida e lê os códigos de convite e de resposta                |

**Por que o WebRTC do WebView2 e não uma biblioteca 100% C#:** o WebView2 traz o mesmo motor de chamadas do Chrome/Edge — codificação por hardware, AV1/VP9/H.264, cancelamento de eco, captura de tela. As bibliotecas WebRTC em .NET não têm cancelamento de eco nem codificação por hardware. A lógica do app continua em C#; o JavaScript fica restrito à camada de mídia.

### 3.2 Código da chamada

Sem servidor, os dois apps precisam trocar os dados de conexão por vocês:

1. **Anfitrião** clica em "Nova chamada"; o app gera um **código de convite**.
2. O anfitrião envia o código pelo mensageiro.
3. **Convidado** cola o convite no app, que gera um **código de resposta**.
4. O convidado envia a resposta pelo mensageiro.
5. O anfitrião cola a resposta e a chamada conecta direto entre os dois.

O código:

- Tem o formato `DT1.` seguido de texto em base64url — algumas centenas de caracteres, para copiar e colar.
- Leva: versão, tipo (convite ou resposta), identificador da chamada, horário de criação, perfil (nome, idioma, gênero, tratamento) e os dados de conexão WebRTC (credenciais ICE, impressão digital DTLS e candidatos de rede).
- É comprimido e tem tamanho máximo; qualquer campo fora do esperado faz o código ser recusado.
- **Vale uma vez e por 30 minutos.** Um convite aceita uma única resposta.
- Carrega a impressão digital da criptografia da chamada: se chegar intacto, ninguém consegue se pôr no meio. Para conferir, os dois veem um **código de segurança** de 12 dígitos e comparam em voz alta.

### 3.3 Quem traduz

Um único computador processa as duas falas: a da própria pessoa pelo microfone e a da outra pelo áudio que já chega pela chamada. Ele envia as legendas à outra pessoa pelo canal de dados.

- **Regra**: o anfitrião traduz se tiver motor configurado; senão, o convidado. Ao conectar, cada lado se apresenta dizendo se tem motor, e os dois chegam à mesma escolha.
- Só esse computador fala com os serviços de reconhecimento e tradução; as chaves ficam só nele, cifradas com DPAPI, e nunca trafegam pela chamada.
- Isso também evita os provedores que não atendem a Rússia: basta quem está no Brasil configurar o motor.
- Legendas só são aceitas de quem foi escolhido para traduzir.

### 3.4 Modo servidor (opcional, desligado)

O servidor ASP.NET Core com login, presença e credenciais de TURN continua no repositório (`DinoTranslator.Server`), com seus testes, para ser reativado se precisar — por exemplo, um relay TURN se a conexão direta falhar em alguma rede.

- O app escolhe o modo em `appsettings.json`: `"Conexao": { "Modo": "codigo" }` (padrão) ou `"servidor"`.
- No modo servidor, valem as telas de login e de sala com presença e os controles da seção "Modo servidor" de [SEGURANCA.md](SEGURANCA.md).

---

## 4. Tradução simultânea

### 4.1 Idiomas

| Código  | Idioma               |
|---------|----------------------|
| `pt-BR` | Português (Brasil)   |
| `en-US` | English              |
| `uk-UA` | Українська           |
| `ru-RU` | Русский              |
| `es-ES` | Español              |

A fala de cada pessoa é traduzida para o idioma da outra — qualquer combinação entre os cinco. Se os dois escolherem o mesmo idioma, a legenda mostra só a transcrição.

### 4.2 O que esperar

Latência zero não existe: os idiomas têm ordem de palavras diferente, e o sistema precisa ouvir parte da frase antes de traduzir com segurança. Intérpretes humanos costumam ficar de 2 a 5 segundos atrás. Metas:

| O que o ouvinte recebe                              | Atraso alvo                 |
|-----------------------------------------------------|-----------------------------|
| Texto original, palavra a palavra                   | < 1 s                       |
| Legenda traduzida **provisória** (vai se ajustando) | ~1 s                        |
| Legenda traduzida **final**                         | ≤ 1,5 s depois da pausa     |

A tradução é **só em texto na tela**: cada um ouve a voz original do outro, com toda a qualidade, e lê a tradução.

### 4.3 Fluxo, para cada pessoa que fala

```
áudio ─► 16 kHz ─► VAD ─► STT streaming ─┬─ parcial ─► tradução rápida ─► legenda provisória
                                         │
                                         └─ final ───► tradução com contexto ─┬─► legenda final
                                                                              └─► painel Falas
```

1. **Provisória** — o STT envia hipóteses parciais; o trecho ainda instável é retraduzido a cada ~300–500 ms e aparece esmaecido.
2. **Final** — quando o STT fecha o trecho (pausa ou fim de frase), a tradução definitiva substitui a provisória e o trecho entra no painel Falas.

### 4.4 Assertividade

- Idioma fixo por pessoa durante a chamada (sem detecção automática).
- Glossário da conversa (nomes, apelidos, lugares, termos) enviado ao STT como palavras-chave e ao tradutor.
- Tradução final com contexto: as últimas ~10 falas dos dois, o glossário, o **gênero de quem fala** (russo e ucraniano: verbo no passado concorda — "я пошёл" / "я пошла"; português e espanhol: adjetivos — "cansado" / "cansada") e o **tratamento** (informal ou formal: ты/вы, ти/ви, tú/usted, você/o senhor).
- Números, moedas e datas mantidos sem conversão.
- Cada um vê a própria transcrição e percebe na hora se foi mal reconhecido.

### 4.5 Motores

O app tem adaptadores por provedor. **Implementado: Azure Speech**, que reconhece e traduz na mesma conexão de streaming (provisórias estáveis, trecho fechado após 600 ms de pausa, glossário). No build de desenvolvimento há também um motor simulado, que gera legendas a partir do volume do áudio, para testar sem serviço externo.

**Implementado também: motor neste computador**, sem conta nem chave, e o áudio não sai da máquina:

- Reconhecimento com Whisper (turbo, preciso, ou small, rápido) na placa de vídeo pelo Vulkan; a fala é separada em trechos pelo volume (provisória a cada segundo de fala, final após 600 ms de pausa).
- Tradução com NLLB-200 (600M, quantizado) no processador, cerca de 45 ms por palavra: a tabela de palavras é descomprimida uma vez ao carregar (antes, a cada palavra, o que levava 160 ms). Reconhecimento (placa de vídeo) e tradução (processador) trabalham ao mesmo tempo. Durante a fala, reconhecimento com janela de 15 s e tradução rápida (a palavra mais provável de cada vez); no fim da frase, reconhecimento com a janela inteira e busca em feixe (5 caminhos) e tradução cuidadosa (busca em feixe com 4 caminhos), que não perde orações como a rápida às vezes perde.
- Modelos baixados pelo app (1,5 a 1,7 GB) de versões fixas, conferidos por SHA-256 ao baixar e ao carregar; a pasta pode ficar em outro disco.
- Medido numa GTX 1650 com i7-10700KF: 6,5 s de fala reconhecidos em 0,6 s (provisória) ou 1,1 s (final) com o turbo; frase traduzida em 0,4–1,4 s (rápida) ou 0,6–1,8 s (cuidadosa). A tradução completa aparece cerca de 1,5 s depois da pausa e a versão final, cuidadosa, cerca de 3,5 s depois.
- Tradução na placa de vídeo (DirectML) foi medida e descartada por ora: o decodificador combinado gera texto errado no DirectML e, numa placa de 4 GB, divide a memória com o Whisper e deixa o reconhecimento 10 vezes mais lento.

**Motores extras, escolhidos durante a chamada** (botão **Motores**, no computador que traduz). Nada do motor original foi trocado: ele é a combinação padrão, e as outras peças se somam a ele.

- **Detector de fala:** volume (original) ou Silero VAD (rede neural, no próprio app).
- **Fim da frase:** 0,5, 0,8 (original) ou 1,2 s de pausa.
- **Reconhecimento:** Whisper turbo, small ou large-v3 (whisper.cpp, Vulkan); faster-whisper turbo (CTranslate2) na placa da NVIDIA (CUDA) ou no processador.
- **Tradução:** NLLB-200 600M em ONNX (original); NLLB 600M e 1.3B e MarianMT (OPUS-MT, um modelo por grupo de idiomas, com etiqueta para o português do Brasil) no CTranslate2, na placa (CUDA) ou no processador. Texto com várias frases é traduzido frase a frase.
- **Versão final:** cuidadosa (busca em feixe) ou rápida.
- O painel mostra as médias das últimas frases: reconhecimento final, tradução final e da hora em que a pessoa parou de falar até a legenda final. Cada combinação aquece os modelos ao carregar.
- faster-whisper e CTranslate2 rodam num processo auxiliar em Python (`Motor/motor.py`), porque as bibliotecas CUDA não convivem com o Vulkan no mesmo processo; o app e ele conversam pela entrada e saída padrão.

Comparativo medido numa chamada de verdade (dois apps, GTX 1650 + i7-10700KF, fala em português, `tests/e2e/motores.ps1`), média até a legenda final depois da fala:

| Combinação                                         | Reconhecimento | Tradução | Legenda final |
|----------------------------------------------------|----------------|----------|---------------|
| Original (volume, 0,8 s, Whisper turbo, NLLB 600M) | 1,3 s          | 1,4 s    | 3,5 s         |
| Silero, 0,5 s, NLLB 600M                           | 1,3 s          | 1,7 s    | 3,5 s         |
| Silero, 0,5 s, MarianMT na placa                   | 1,6 s          | 0,1 s    | 2,2 s         |
| Silero, 0,5 s, NLLB 1.3B na placa                  | 1,3 s          | 0,6 s    | 2,5 s         |
| Silero, 0,5 s, faster-whisper e MarianMT na placa  | 3,5 s          | 1,9 s    | 6,4 s         |

Qualidade nas mesmas frases: o NLLB 1.3B acertou todas, inclusive gíria ("Tô morrendo de saudade" → "Я так скучаю"); o MarianMT é o mais rápido e o único em português do Brasil ("não pude te ligar"), mas trocou "português" por "russo" numa frase; o NLLB 600M erra gíria. Nesta placa o faster-whisper é mais lento que o Whisper do app (2 s contra 1,1 s por frase), e a pausa de 0,5 s corta frases faladas com vírgulas longas.

| Etapa         | Serviços em nuvem (candidatos)                                      | No próprio computador                         |
|---------------|---------------------------------------------------------------------|-----------------------------------------------|
| VAD           | —                                                                   | Silero VAD (ONNX, CPU)                        |
| STT streaming | Azure Speech (SDK C#), Deepgram, Soniox, Speechmatics               | Whisper large-v3-turbo via Whisper.net (GPU)  |
| Tradução      | DeepL (biblioteca .NET, glossário e formalidade), Azure Translator, Yandex Translate | NLLB-200, MADLAD-400       |

Todos os candidatos de tradução cobrem os cinco idiomas.

### 4.6 Legibilidade

- Legenda de no máximo 2 linhas por pessoa; o histórico completo fica no painel Falas.
- Tamanho e posição da legenda ajustáveis; contraste alto sobre o vídeo.
- Se a pessoa fala muito sem pausa, o trecho é fechado a cada ~12 s.

---

## 5. Chamada: ping, áudio, câmera e tela

### 5.1 Ping baixo

- Conexão **P2P direta** entre os dois computadores, sem servidor no meio.
- Limite físico: entre Brasil e Rússia (~12 mil km), a rede fica em **~200–260 ms de ida e volta**. O objetivo é chegar perto disso, com atraso boca-ouvido de ~150–250 ms.
- Buffer de jitter mínimo no receptor, prioridade alta para o áudio, FEC do Opus para perdas.
- Indicador de ping, perda e taxa na barra de título (`getStats` a cada 1 s).
- STUN públicos (Google e Cloudflare) só para descobrir o endereço de cada lado.

### 5.2 Áudio

- Opus 48 kHz, quadros de 20 ms, FEC embutido, DTX desligado, até 128 kb/s.
- **Modo normal**: cancelamento de eco, supressão de ruído e ganho automático.
- **Modo alta fidelidade** (com fones): sem processamento — voz natural.
- **Supressão de ruído** (botão **Ruído** na chamada e no teste de dispositivos, vale na hora, sem reconectar). O microfone passa por uma cadeia no próprio app antes de ir para a chamada e para a tradução: supressor → sensibilidade (porta de ruído com medidor, automática ou com limite em dB, como no Discord) → envio.
  - Modos: desligada; padrão (a do WebRTC, a de antes); RNNoise; GTCRN; **DeepFilterNet3** (padrão), com intensidade ajustável. Cancelamento de eco e ganho automático também têm chave.
  - Os modelos rodam em AudioWorklets (WebAssembly) e vêm junto com o app, copiados sem mudança de pacotes npm com licença MIT; `js/vendor/ORIGEM.json` registra a origem e o SHA-256 de cada arquivo, e `tools/atualizar-supressores.ps1` confere tudo contra os pacotes publicados.
  - Se o DeepFilterNet3 não der conta em tempo real, a cadeia desce sozinha para o GTCRN e o painel avisa.
  - Medido no app (`tests/e2e/ruido.ps1`: fala com ventilador e teclado 12 dB abaixo; o medidor vai até -80 dB):

    | Modo                | Ruído removido | Voz perdida |
    |---------------------|----------------|-------------|
    | Padrão (WebRTC)     | 17 dB          | 1,6 dB      |
    | RNNoise             | 46 dB          | 6,4 dB      |
    | GTCRN               | ≥ 51 dB        | 7,2 dB      |
    | DeepFilterNet3 20%  | 34 dB          | 1,6 dB      |
    | DeepFilterNet3 60%  | 46 dB          | 1,6 dB      |
    | DeepFilterNet3 100% | ≥ 51 dB        | 0,8 dB      |
- Escolha de microfone e saída de áudio nas configurações.

### 5.3 Câmera

- Até 1080p a 30 fps, caindo para 720p automaticamente se a banda apertar.
- As duas câmeras ficam sempre visíveis: lado a lado na conversa; numa coluna lateral durante o compartilhamento de tela.

### 5.4 Tela até 2K

- 2560×1440 a até 30 fps.
- Modo **texto** (código, documentos): nitidez máxima, mantém a resolução.
- Modo **movimento** (vídeo, jogos): mantém fps e reduz resolução se preciso.
- Codec preferido: AV1 → VP9 → H.264, conforme o suporte dos dois lados.

### 5.5 Banda necessária

| Fluxo           | Upload por pessoa |
|-----------------|-------------------|
| Áudio           | 0,13 Mb/s         |
| Câmera 1080p    | 2–3 Mb/s          |
| Tela 2K         | 4–8 Mb/s          |
| **Tudo ligado** | **~6–11 Mb/s de upload e o mesmo de download** |

### 5.6 Canal de dados

Um canal de dados WebRTC confiável e ordenado, dentro da mesma conexão cifrada, leva: apresentação dos perfis, mensagens do chat, legendas, falas e o aviso de encerramento. Cada mensagem é JSON com tipo e tamanho máximo; o que não for reconhecido é descartado.

---

## 6. Interface

### 6.1 Telas

1. **Início** — perfil (nome, idioma, gênero, tratamento), botões "Nova chamada" e "Entrar com código".
2. **Convite** — anfitrião: mostra o código de convite para copiar e o campo para colar a resposta.
3. **Resposta** — convidado: campo para colar o convite; mostra o código de resposta para copiar.
4. **Chamada** — tela principal.
5. **Configurações** — dispositivos, modo de áudio, glossário, tamanho da legenda, motores de tradução (anfitrião).

### 6.2 Chamada

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ DinoTranslator      SÃO PAULO 15:40 · МОСКВА 21:40      ping 212 ms    _ □ × │
├────────────────────────────────────────────────────┬─────────────────────────┤
│ ┌──────────────────────┐  ┌──────────────────────┐ │ [Chat]  [Falas]         │
│ │                      │  │                      │ │ ─────────────────────── │
│ │   câmera do outro    │  │     sua câmera       │ │ ANNA · 15:39            │
│ │                      │  │                      │ │ Привет! Как прошёл      │
│ │ Anna · ru-RU         │  │ Você · pt-BR         │ │ твой день?              │
│ └──────────────────────┘  └──────────────────────┘ │ › Oi! Como foi o seu    │
│                                                    │   dia?                  │
│  Правда? А какая у вас пого…                       │                         │
│  Sério? E como está o tem…                         │ VOCÊ · 15:39            │
│                                                    │ Foi ótimo! Hoje o dia   │
│                                                    │ está lindo.             │
│                                                    │ › Отлично! Сегодня      │
│                                                    │   прекрасный день.      │
├────────────────────────────────────────────────────┴─────────────────────────┤
│ [Mic] [Câmera] [Tela 2K]   legenda: tamanho 26 · 2 linhas   [Config] [Sair]  │
└──────────────────────────────────────────────────────────────────────────────┘
```

- Legenda abaixo das câmeras: linha de cima no idioma original (menor), linha de baixo traduzida (maior). Trecho terminado em "…" é provisório e aparece esmaecido.
- Ao compartilhar a tela, ela ocupa o palco e as duas câmeras vão para uma coluna à direita.
- Os relógios mostram o horário de cada pessoa, conforme o fuso informado no perfil.

### 6.3 Os dois chats

- **Chat** — cada um escreve no seu idioma; o outro recebe traduzido, com "ver original".
- **Falas** — cada trecho falado vira uma mensagem: quem falou, hora, texto original e tradução. Busca e exportação (.txt / .md).
- Os painéis abrem e fecham de forma independente; os dois podem ficar abertos.
- O histórico fica só no computador de cada um, cifrado com DPAPI, e pode ser apagado.

---

## 7. Instalador e desinstalador

- **WiX Toolset (Burn)** com interface própria em WPF (C#), no mesmo visual do app.
  - **Instalar**: boas-vindas → pasta → atalhos → progresso → concluído ("abrir agora").
  - **Desinstalar** (pelo `Desinstalar.exe` da pasta do app ou por "Aplicativos instalados" do Windows): confirmação → manter ou apagar dados locais → progresso → concluído. Instalações antigas ou sem registro ganham o `Desinstalar.exe` e o registro ao abrir o app.
  - **Motores e modelos de tradução** (opção marcada por padrão): o instalador baixa e instala todos, cerca de 10 GB, na pasta `extras` ao lado do app. O GitHub limita cada arquivo a 2 GB, então eles não cabem dentro do instalador: vêm da release `componentes-1` deste projeto (pacotes gerados por `tools/criar-extras.ps1`) e das versões fixas dos repositórios originais. Se o download falhar, o app fica instalado e os motores podem ser baixados depois em Ajustes › Tradução. Atualizar o app mantém a pasta `extras`; com os modelos lá, a tradução já começa ligada no motor local.
  - Verifica e instala o WebView2 Runtime se faltar.
- App publicado **self-contained**: não exige .NET instalado.
- **Assinatura de código** recomendada; sem ela, o SmartScreen mostra "editor desconhecido".

---

## 8. Segurança

Resumo — o modelo de ameaças e a lista completa de controles estão em [SEGURANCA.md](SEGURANCA.md).

- **Sem servidor no modo padrão**: não existe conta, senha ou serviço exposto para ser atacado. O modo servidor, opcional, mantém login reforçado e limites de requisição.
- **Mídia e dados cifrados ponta a ponta** (DTLS-SRTP e DTLS/SCTP): só os dois computadores têm as chaves.
- **Código de chamada** de uso único, com validade de 30 min, validado com limites rígidos; código de segurança para conferir em voz alta.
- **Chaves dos serviços de tradução** só no computador do anfitrião, cifradas com DPAPI.
- **App travado**: WebView2 sem navegação externa, sem scripts remotos, CSP estrita; câmera, microfone e tela só para a própria interface.
- **Distribuição**: instalador assinado e verificado por hash.

---

## 9. Estrutura do repositório

```
DinoTranslator/
├── src/
│   ├── DinoTranslator.Shared/       idiomas, perfil, código da chamada, mensagens do canal de dados
│   ├── DinoTranslator.Desktop/      WPF + Blazor Hybrid (interface) + mídia WebRTC (JavaScript)
│   ├── DinoTranslator.Server/       modo servidor opcional: login, presença, credenciais TURN
│   └── Installer/                   WiX: MSI, bundle e interface própria do instalador
├── tests/
│   ├── DinoTranslator.Desktop.Tests/
│   └── DinoTranslator.Server.Tests/
└── docs/                            planejamento, design, segurança, fluxo git
```

---

## 10. Fases e módulos

Cada módulo é uma branch (fluxo em [GIT.md](GIT.md)).

### Fase 1 — Base

| Branch                 | Conteúdo                                                   | Situação |
|------------------------|------------------------------------------------------------|----------|
| `base/estrutura`       | solução, projetos, configurações compartilhadas            | feito    |
| `cliente/shell`        | janela WPF, Blazor Hybrid, WebView2 travado                | feito    |
| `base/dotnet10`        | migração para o .NET 10                                    | feito    |
| `servidor/autenticacao`, `servidor/sinalizacao` | login, presença, TURN (modo opcional) | feito |
| `base/modo-codigo`     | modo código como padrão; servidor e login opcionais        | feito    |
| `shared/idiomas`       | os cinco idiomas                                           | feito    |
| `cliente/perfil`       | perfil local e tela inicial                                | feito    |
| `cliente/design`       | tokens visuais, fontes, componentes base                   |          |

### Fase 2 — Chamada

| Branch                   | Conteúdo                                                        |
|--------------------------|-----------------------------------------------------------------|
| `cliente/codigo-chamada` | código de convite e de resposta, código de segurança — feito    |
| `cliente/chamada`        | WebRTC P2P, câmera, áudio alta qualidade, canal de dados, ping — feito; teste de ponta a ponta em `tests/e2e/chamada.ps1` |
| `cliente/tela`           | compartilhamento de tela até 2K, modos texto/movimento — feito  |

**Pronto quando**: chamada de 30 min Brasil ⇄ Rússia com câmera e tela 2K, sem quedas, com o ping exibido.

### Fase 3 — Tradução e chats

| Branch              | Conteúdo                                                        |
|---------------------|-----------------------------------------------------------------|
| `cliente/traducao`  | pipeline STT → tradução, adaptadores dos motores — feito (Azure Speech); falta a tradução final com contexto (gênero, tratamento) |
| `cliente/legendas`  | legendas provisória/final — feito; falta tamanho e posição ajustáveis |
| `chat/texto`        | chat com tradução automática                                    |
| `chat/falas`        | painel de falas, busca e exportação                             |

**Pronto quando**: conversa de 15 min com legenda final ≤ 1,5 s após a pausa e tradução aprovada pelos dois.

### Fase 4 — Visual final e instalador

| Branch            | Conteúdo                                                   |
|-------------------|------------------------------------------------------------|
| `cliente/design`  | acabamento visual de todas as telas                        |
| `instalador/wix`  | MSI, bundle, interface própria de instalar e desinstalar   |

**Pronto quando**: o convidado instala sozinho pelo instalador, usa e desinstala sem resíduos.

### Fase 5 — Robustez (contínuo)

Reconexão transparente quando a rede oscila, métricas de latência por etapa, atualização, glossário editável na interface.

---

## 11. Riscos

| Risco                                                                                        | Impacto | Mitigação |
|----------------------------------------------------------------------------------------------|---------|-----------|
| Conexão direta não fechar entre as redes dos dois (internet móvel, IP compartilhado pelo provedor) | Alto — a chamada não conecta | Preferir Wi-Fi ou cabo; se acontecer, adicionar um relay TURN depois |
| Bloqueios de rede na Rússia (restrições a chamadas em apps estrangeiros já ocorreram)        | Alto    | App próprio fora das listas de bloqueio; relay como plano B |
| Desligamentos de internet móvel em regiões da Rússia                                         | Médio   | Usar Wi-Fi ou cabo do lado russo |
| Queda da conexão no meio da chamada                                                          | Médio   | Reconexão pelo canal de dados enquanto ele existir; senão, novos códigos |
| Termos dos provedores de fala e tradução                                                     | Médio   | As requisições saem só do computador do anfitrião; adaptadores plugáveis |
| Latência de tradução acima do aceitável                                                      | Médio   | Legenda provisória como base; corte por orações; métricas por etapa |
| Erros de reconhecimento (sotaque, ruído, nomes)                                              | Médio   | Fone com microfone decente; glossário; transcrição própria visível |
| Banda insuficiente para câmera + tela 2K                                                     | Médio   | Adaptação automática de resolução |
| SmartScreen bloquear o instalador                                                            | Baixo   | Assinatura de código ou instrução de "executar assim mesmo" |

---

## 12. Decisões em aberto

1. **Tradução final com contexto**: somar um segundo motor (ex.: DeepL, com formalidade e glossário) para refinar o trecho final com gênero e tratamento?
2. **Histórico**: guardar chat e falas no computador de cada um? Por quanto tempo?
