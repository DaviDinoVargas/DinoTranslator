# DinoTranslator — Planejamento

> Chamada de vídeo com tradução simultânea português ⇄ russo para dois usuários: um no Brasil e outro na Rússia.
> Versão 1 · 03/11/2026

---

## 1. Requisitos

| #   | Requisito                                         | Como o plano atende                                                                 |
|-----|---------------------------------------------------|-------------------------------------------------------------------------------------|
| R1  | Tradução ao vivo, em texto, enquanto a pessoa fala | Legenda provisória palavra a palavra e legenda final na tela, sem voz sintetizada (seção 4) |
| R2  | Tradução assertiva                                | Tradução com contexto da conversa, glossário, gênero e tratamento; motores escolhidos por teste com a fala real (4.3 e 4.4) |
| R3  | Ping baixo                                        | Mídia P2P direta (WebRTC), TURN só como reserva, buffer de jitter mínimo (5.1)      |
| R4  | Câmera dos dois na tela                           | Dois quadros sempre visíveis, inclusive durante o compartilhamento de tela (6)      |
| R5  | Áudio da mais alta qualidade                      | Opus 48 kHz até 128 kb/s, FEC, modo alta fidelidade com fones (5.2)                  |
| R6  | Compartilhamento de tela até 2K                   | 2560×1440 a até 30 fps, AV1/VP9/H.264, prioridade à nitidez (5.4)                    |
| R7  | Chat de texto com tradutor                        | Mensagens traduzidas automaticamente, com o original disponível (6.3)               |
| R8  | Segundo chat com as falas em forma de mensagem    | Painel "Falas": cada trecho falado vira uma mensagem com original e tradução (6.3)  |
| R9  | Layout próprio, não genérico                      | Identidade visual própria, definida em [DESIGN.md](DESIGN.md)                        |
| R10 | Login e senha, só esses dois usuários             | Sem cadastro público; as duas contas são criadas no servidor; limite rígido de 2 (3.3) |
| R11 | Executável com instalador e desinstalador próprios | WiX (Burn) com interface própria em WPF, no visual do app (7)                       |
| R12 | Desenvolvido em C#                                | Cliente, servidor e instalador em C#/.NET                                           |
| R13 | App seguro                                        | Mídia cifrada ponta a ponta, login reforçado, cliente e servidor endurecidos (8 e [SEGURANCA.md](SEGURANCA.md)) |

## 2. Premissas (confirmar — ver seção 13)

- Os dois usam **Windows 10 ou 11** — o executável é para Windows.
- Uso **pessoal**, só entre os dois, sem fins comerciais.
- Cada usuário tem **idioma fixo**: Brasil = `pt-BR`, Rússia = `ru-RU`. Sem detecção automática de idioma, o reconhecimento fica mais preciso.
- Tradução **direta** PT ⇄ RU, sem passar pelo inglês.
- Haverá um **servidor** próprio (VPS), necessário para login, chat, tradução e como reserva de conexão.

---

## 3. Arquitetura

```
┌───────────────────────────┐                      ┌───────────────────────────┐
│ App Windows · Brasil      │   mídia P2P direta   │ App Windows · Rússia      │
│ WPF + Blazor Hybrid (C#)  │◄════════════════════►│ WPF + Blazor Hybrid (C#)  │
│ WebRTC do WebView2        │  câmera · áudio · 2K │ WebRTC do WebView2        │
└─────────────┬─────────────┘                      └─────────────┬─────────────┘
              │ HTTPS · SignalR · WebSocket de áudio             │
              ▼                                                  ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│ Servidor DinoTranslator · ASP.NET Core (C#) · VPS na Europa                  │
│ login e JWT · sinalização WebRTC · chat · tradução (STT → texto traduzido)   │
│ SQLite · coturn (TURN de reserva em TLS/443) · Caddy (HTTPS)                 │
└──────────────────────────────────────────────────────────────────────────────┘
```

A **mídia** (câmera, áudio, tela) vai direto de um computador ao outro — é o caminho de menor ping. O **servidor** cuida do que precisa de um ponto central: login, troca de mensagens de conexão, chats e tradução.

### 3.1 Cliente desktop (Windows)

| Peça                      | Tecnologia                               | Papel                                                                 |
|---------------------------|------------------------------------------|-----------------------------------------------------------------------|
| Janela e serviços nativos | .NET + WPF                               | Janela com barra de título própria, login guardado com DPAPI, integração com o instalador |
| Interface                 | Blazor Hybrid (BlazorWebView) + CSS próprio | Todas as telas em componentes Razor (C#); liberdade total de visual   |
| Mídia                     | WebRTC do Chromium via WebView2 + módulo TypeScript | Câmera, áudio, tela, conexão P2P, estatísticas de ping           |

**Por que o WebRTC do WebView2 e não uma biblioteca 100% C#:** o WebView2 traz o mesmo motor de chamadas do Chrome/Edge — codificação por hardware, AV1/VP9/H.264, cancelamento de eco, captura de tela. As bibliotecas WebRTC em .NET (ex.: SIPSorcery) não têm cancelamento de eco nem codificação por hardware, e não sustentariam tela 2K com boa qualidade. A lógica do app continua em C#; o TypeScript fica restrito à camada de mídia.

### 3.2 Servidor

Um único serviço ASP.NET Core:

- **Autenticação**: usuário e senha → JWT de acesso (15 min) + refresh token rotativo (guardado como hash).
- **SignalR** `/hubs/sala`: presença, sinalização WebRTC (offer/answer/ICE), chat, legendas e falas.
- **WebSocket** `/ws/audio`: recebe o áudio do microfone (16 kHz) só para reconhecimento de fala.
- **Pipeline de tradução** com interfaces plugáveis (`ISpeechRecognizer`, `ITranslator`).
- **EF Core + SQLite**: usuários, tokens, mensagens do chat, falas.
- **coturn**: STUN/TURN com credenciais temporárias (HMAC) emitidas pelo servidor.
- **Caddy**: HTTPS automático. Implantação com Docker Compose.

### 3.3 Login — somente dois usuários

- **Não existe tela de cadastro.** As contas são criadas no servidor por comando (`users add`), com limite rígido de 2 (configurável). Uma terceira conta é recusada.
- Senha com hash PBKDF2 (`PasswordHasher` do ASP.NET Core Identity), mínimo de 10 caracteres.
- Bloqueio de 15 min após 5 tentativas erradas; mensagem de erro sempre genérica.
- JWT curto + refresh token rotativo; sair revoga o refresh token; o app guarda o refresh token cifrado com DPAPI (só o usuário do Windows consegue ler).
- Tudo via HTTPS/WSS. Segundo fator (TOTP) opcional na fase de robustez.

---

## 4. Tradução simultânea

### 4.1 O que esperar

Latência zero não existe: português e russo têm ordem de palavras diferente, e o sistema precisa ouvir parte da frase antes de traduzir com segurança. Intérpretes humanos costumam ficar de 2 a 5 segundos atrás. Metas:

| O que o ouvinte recebe                         | Atraso alvo                 |
|------------------------------------------------|-----------------------------|
| Texto original, palavra a palavra              | < 1 s                       |
| Legenda traduzida **provisória** (vai se ajustando) | ~1 s                   |
| Legenda traduzida **final**                    | ≤ 1,5 s depois da pausa     |

A tradução é **só em texto na tela**: cada um ouve a voz original do outro, com toda a qualidade, e lê a tradução.

### 4.2 Fluxo, para cada pessoa que fala

```
microfone ─► 16 kHz ─► VAD ─► STT streaming ─┬─ parcial ─► tradução rápida ─► legenda provisória
                                             │
                                             └─ final ───► tradução com contexto ─┬─► legenda final
                                                                                  └─► painel Falas
```

1. **Provisória** — o STT envia hipóteses parciais; o trecho ainda instável é retraduzido a cada ~300–500 ms e aparece esmaecido.
2. **Final** — quando o STT fecha o trecho (pausa ou fim de frase), a tradução definitiva substitui a provisória e o trecho entra no painel Falas. Um modo opcional fecha trechos em orações/vírgulas para frases longas aparecerem antes.

### 4.3 Assertividade

- Idioma fixo por usuário (sem detecção automática).
- Glossário da conversa (nomes, apelidos, lugares, termos) enviado ao STT como palavras-chave e ao tradutor.
- Tradução final feita por um motor de tradução contextual, recebendo: as últimas ~10 falas dos dois, o glossário, o **gênero de quem fala** (no russo o verbo no passado concorda: "я пошёл" / "я пошла"; no português os adjetivos: "cansado" / "cansada") e o **tratamento** (ты informal / вы formal).
- Duas passadas: tradução rápida para o provisório e tradução contextual para o final.
- Números, moedas (R$, ₽) e datas mantidos sem conversão.
- Cada um vê a própria transcrição e percebe na hora se foi mal reconhecido; um botão "corrigir" reenvia o texto corrigido para tradução.

### 4.4 Motores de reconhecimento, tradução e voz

O servidor terá adaptadores por provedor; a escolha final sai da **Fase 0**, com gravações reais da fala dos dois.

| Etapa           | Serviços comerciais (candidatos)                                           | Auto-hospedado no servidor                             |
|-----------------|----------------------------------------------------------------------------|--------------------------------------------------------|
| VAD             | —                                                                          | Silero VAD (ONNX, CPU)                                 |
| STT streaming   | Azure Speech (SDK C# nativo), Deepgram, Soniox, Speechmatics, Yandex SpeechKit | Whisper large-v3-turbo via Whisper.net (C#, CUDA); sherpa-onnx (C#) |
| Tradução        | DeepL (biblioteca .NET, glossário e formalidade), Azure Translator, Yandex Translate | NLLB-200 \*, MADLAD-400                     |

\* licença não comercial — aceitável para uso pessoal, não para produto.

> **Atenção — Rússia.** Vários provedores de nuvem dos EUA e da Europa não atendem a Rússia ou restringem o uso por quem está lá, mesmo com as chamadas saindo do seu servidor. Os termos de cada candidato serão lidos na Fase 0. Se for um impedimento, os caminhos são os modelos auto-hospedados no próprio servidor (exige GPU) ou provedores que atendem a Rússia (ex.: Yandex).

### 4.5 Legibilidade

- Legenda de no máximo 2 linhas por pessoa, com o trecho mais antigo saindo por cima; o histórico completo fica no painel Falas.
- Tamanho e posição da legenda ajustáveis; contraste alto sobre o vídeo.
- Se a pessoa fala muito sem pausa, o trecho é fechado à força a cada ~12 s para a legenda não "correr" demais.

---

## 5. Chamada: ping, áudio, câmera e tela

### 5.1 Ping baixo

- Conexão **P2P direta** entre os dois computadores; TURN só se a direta falhar.
- Limite físico: São Paulo ⇄ Moscou são ~12 mil km; na prática a rede fica em **~200–260 ms de ida e volta**. O objetivo é chegar perto disso, com atraso boca-ouvido de ~150–250 ms.
- Buffer de jitter mínimo no receptor (`jitterBufferTarget`), prioridade alta para o áudio, FEC do Opus para perdas.
- Indicador de ping, perda e taxa na barra de título (`getStats` a cada 1 s).

### 5.2 Áudio

- Opus 48 kHz, quadros de 20 ms, FEC embutido, DTX desligado, até 128 kb/s (estéreo opcional).
- **Modo normal**: cancelamento de eco, supressão de ruído e ganho automático.
- **Modo alta fidelidade** (com fones): sem processamento — voz natural, como em estúdio.
- Escolha de microfone e saída de áudio nas configurações.

### 5.3 Câmera

- Até 1080p a 30 fps, caindo para 720p automaticamente se a banda apertar; H.264 por hardware ou VP9.
- As duas câmeras ficam sempre visíveis: lado a lado na conversa normal; numa coluna lateral durante o compartilhamento de tela.

### 5.4 Tela até 2K

- 2560×1440 a até 30 fps.
- Modo **texto** (código, documentos): nitidez máxima, mantém a resolução e reduz fps se preciso.
- Modo **movimento** (vídeo, jogos): mantém fps e reduz resolução se preciso.
- Codec preferido: AV1 → VP9 → H.264, conforme o suporte dos dois lados.

### 5.5 Banda necessária

| Fluxo                | Upload por pessoa |
|----------------------|-------------------|
| Áudio                | 0,13 Mb/s         |
| Câmera 1080p         | 2–3 Mb/s          |
| Tela 2K              | 4–8 Mb/s          |
| **Tudo ligado**      | **~6–11 Mb/s de upload e o mesmo de download** |

---

## 6. Interface

### 6.1 Telas

1. **Login** — usuário e senha; "manter conectado".
2. **Sala** — tela principal (abaixo).
3. **Configurações** — dispositivos, modo de áudio, glossário, perfil (nome, gênero, tratamento), tamanho da legenda.

### 6.2 Sala

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ DinoTranslator      SÃO PAULO 15:40 · МОСКВА 21:40      ping 212 ms    _ □ × │
├────────────────────────────────────────────────────┬─────────────────────────┤
│ ┌──────────────────────┐  ┌──────────────────────┐ │ [Chat]  [Falas]         │
│ │                      │  │                      │ │ ─────────────────────── │
│ │     câmera RU        │  │     sua câmera       │ │ RU · 15:39              │
│ │                      │  │                      │ │ Привет! Как прошёл      │
│ │ RU · ru-RU           │  │ Você · pt-BR         │ │ твой день?              │
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
- Ao compartilhar a tela, ela ocupa o palco e as duas câmeras vão para uma coluna à direita do palco.
- Barra de título própria com os dois relógios (6 h de diferença entre Brasília e Moscou) e o ping.

### 6.3 Os dois chats

- **Chat** — cada um escreve no seu idioma; o outro recebe traduzido, com "ver original". Histórico salvo no servidor.
- **Falas** — cada trecho falado vira uma mensagem: quem falou, hora, texto original e tradução. O trecho em andamento aparece como "falando…". Busca e exportação (.txt / .md).
- Os painéis abrem e fecham de forma independente; os dois podem ficar abertos, um acima do outro.

O visual (cores, tipografia, componentes) está em [DESIGN.md](DESIGN.md).

---

## 7. Instalador e desinstalador

- **WiX Toolset (Burn)** com interface própria em WPF (C#), no mesmo visual do app.
  - **Instalar**: boas-vindas → pasta → atalhos → progresso → concluído ("abrir agora").
  - **Desinstalar** (pela mesma interface, a partir de "Aplicativos instalados" do Windows): confirmação → manter ou apagar dados locais → progresso → concluído.
  - Verifica e instala o WebView2 Runtime se faltar.
- App publicado **self-contained**: não exige .NET instalado.
- **Assinatura de código** recomendada; sem ela, o SmartScreen mostra "editor desconhecido".
- **Atualização**: o app consulta a versão no servidor e oferece baixar o novo instalador (Fase 5).
- Licença: o WiX v6+ cobra a taxa OSMF de uso comercial; para uso pessoal não se aplica (confirmar ao chegar nessa fase).

---

## 8. Segurança

Resumo — o modelo de ameaças e a lista completa de controles estão em [SEGURANCA.md](SEGURANCA.md).

- **Mídia cifrada ponta a ponta**: na conexão P2P, câmera, áudio e tela são cifrados (DTLS-SRTP) com chaves negociadas entre os dois computadores; nem o servidor nem o TURN conseguem decifrar. Um **código de segurança da chamada** (derivado das impressões digitais DTLS dos dois lados) aparece na tela para os dois conferirem em voz alta.
- **Áudio enviado para tradução**: trafega só por WSS, é processado em memória e **nunca é gravado**.
- **Login**: só duas contas, senha com hash forte, bloqueio por tentativas, limite de requisições, tokens curtos e rotativos com detecção de reuso.
- **Servidor**: só as portas necessárias, contêineres sem root, segredos fora do repositório, TURN sem acesso à rede interna, logs sem senhas, tokens ou conteúdo das conversas.
- **Cliente**: WebView2 travado (sem navegação externa, sem scripts remotos, CSP estrita, DevTools desligado na versão final), câmera/microfone/tela liberados só para o próprio app, login guardado com DPAPI.
- **Distribuição**: instalador e atualizações assinados e verificados por hash.

---

## 9. Estrutura do repositório

```
DinoTranslator/
├── src/
│   ├── DinoTranslator.Shared/       contratos usados por cliente e servidor (DTOs, mensagens)
│   ├── DinoTranslator.Server/       ASP.NET Core: login, SignalR, tradução, EF Core
│   ├── DinoTranslator.Desktop/      WPF + Blazor Hybrid (interface) + mídia WebRTC (TypeScript)
│   └── Installer/                   WiX: MSI, bundle e interface própria do instalador
├── tests/
│   ├── DinoTranslator.Server.Tests/
│   └── DinoTranslator.Desktop.Tests/
├── tools/bench/                     testes da Fase 0 (STT, tradução, rede)
├── deploy/                          Docker Compose, coturn, Caddy
└── docs/                            planejamento, design, segurança, fluxo git
```

---

## 10. Fases e módulos

Cada módulo é uma branch (fluxo em [GIT.md](GIT.md)). Estimativas para 1 desenvolvedor dedicado.

### Fase 0 — Validação técnica (~1 semana)

- [ ] Testar a partir da Rússia, com o usuário real: acesso ao servidor/domínio, P2P direto, TURN em UDP e TLS/443.
- [ ] Medir ping e banda reais entre os dois.
- [ ] Gravar ~10 min de fala de cada um e comparar 2–3 motores de STT (taxa de erro e latência).
- [ ] Comparar tradução PT ⇄ RU em ~50 frases reais, avaliadas pelos dois.
- [ ] Ler os termos de uso dos provedores quanto a usuários na Rússia.
- **Entrega**: escolha dos motores e números medidos.

### Fase 1 — Base (~1 semana)

| Branch                  | Conteúdo                                                        |
|-------------------------|-----------------------------------------------------------------|
| `docs/planejamento`     | este plano, design, fluxo git                                   |
| `base/estrutura`        | solução, projetos, configurações compartilhadas                 |
| `servidor/autenticacao` | usuários, senha, JWT, refresh, bloqueio, comando de usuários    |
| `servidor/sinalizacao`  | SignalR: presença, offer/answer/ICE, credenciais TURN           |
| `cliente/shell`         | janela WPF, Blazor Hybrid, tela de login, login guardado        |
| `cliente/design`        | tokens visuais, fontes, componentes base                        |
| `infra/deploy`          | Docker Compose, coturn, Caddy                                   |

**Pronto quando**: os dois fazem login no app e se veem "online" um para o outro.

### Fase 2 — Chamada (~2 semanas)

| Branch             | Conteúdo                                                             |
|--------------------|----------------------------------------------------------------------|
| `cliente/chamada`  | WebRTC P2P, câmera, áudio alta qualidade, reconexão, indicador de ping |
| `cliente/tela`     | compartilhamento de tela até 2K, modos texto/movimento               |

**Pronto quando**: chamada de 30 min Brasil ⇄ Rússia com câmera e tela 2K, sem quedas, com o ping exibido.

### Fase 3 — Tradução e chats (~2–3 semanas)

| Branch               | Conteúdo                                                        |
|----------------------|-----------------------------------------------------------------|
| `servidor/traducao`  | pipeline STT → tradução, adaptadores dos motores escolhidos     |
| `cliente/legendas`   | legendas provisória/final, tamanho e posição ajustáveis         |
| `chat/texto`         | chat com tradução automática                                    |
| `chat/falas`         | painel de falas, busca e exportação                             |

**Pronto quando**: conversa de 15 min com legenda final ≤ 1,5 s após a pausa e tradução aprovada pelos dois.

### Fase 4 — Visual final e instalador (~1–2 semanas)

| Branch            | Conteúdo                                                   |
|-------------------|------------------------------------------------------------|
| `cliente/design`  | acabamento visual de todas as telas, aprovado pelos dois   |
| `instalador/wix`  | MSI, bundle, interface própria de instalar e desinstalar   |

**Pronto quando**: o usuário russo instala sozinho pelo instalador, usa e desinstala sem resíduos.

### Fase 5 — Robustez (contínuo)

Reconexão transparente, métricas de latência por etapa, atualização automática, TOTP, backups, glossário editável na interface.

---

## 11. Custos (ordem de grandeza — confirmar preços na Fase 0)

| Item                         | Com APIs comerciais                     | Com modelos próprios                             |
|------------------------------|-----------------------------------------|--------------------------------------------------|
| Servidor (VPS + coturn)      | US$ 10–40/mês                           | US$ 10–40/mês                                    |
| GPU                          | —                                       | US$ 0,30–1,00/h ligada só nas conversas, ou US$ 150–400/mês dedicada |
| Por hora de conversa         | ~US$ 0,50–2 (STT dos dois + tradução)   | ~0                                               |
| Domínio                      | ~US$ 10–15/ano                          | ~US$ 10–15/ano                                   |
| Assinatura de código (opcional) | US$ 10–30/mês ou ~US$ 200–400/ano    | idem                                             |

---

## 12. Riscos

| Risco                                                                                       | Impacto | Mitigação |
|---------------------------------------------------------------------------------------------|---------|-----------|
| Bloqueios de rede na Rússia (serviços estrangeiros, VPNs, faixas de IP de hospedagem e restrições a chamadas em apps estrangeiros já ocorreram) | Alto — a chamada não conecta | Testar na Fase 0 com o usuário real; TURN em TLS/443; plano B de hospedagem em outro país |
| Desligamentos de internet móvel em regiões da Rússia                                        | Médio   | Usar Wi-Fi ou cabo do lado russo |
| Termos dos provedores de fala e tradução excluírem usuários na Rússia                       | Alto    | Adaptadores plugáveis; caminho auto-hospedado pronto como alternativa |
| Invasão do servidor ou roubo de credenciais                                                 | Alto    | Controles de [SEGURANCA.md](SEGURANCA.md); mídia P2P não passa pelo servidor |
| Latência de tradução acima do aceitável                                                     | Médio   | Legenda provisória como base; corte por orações; métricas por etapa |
| Erros de reconhecimento (sotaque, ruído, nomes)                                              | Médio   | Fone com microfone decente; glossário; transcrição própria visível; botão "corrigir" |
| Banda insuficiente para câmera + tela 2K                                                     | Médio   | Adaptação automática de resolução; medir na Fase 0 |
| SmartScreen bloquear o instalador                                                            | Baixo   | Assinatura de código ou instrução de "executar assim mesmo" |

---

## 13. Decisões em aberto

1. **Sistema dos dois**: ambos usam Windows? Se o usuário russo usar Mac ou Linux, o cliente muda de WPF para Avalonia.
2. **Motores**: serviços pagos por uso ou modelos próprios em GPU alugada? Quem cria as contas e chaves?
3. **Servidor**: já existe VPS e domínio? Onde hospedar (sugestão: Europa — Alemanha, Holanda ou Finlândia)?
4. **Histórico**: guardar chat e falas no servidor? Por quanto tempo?
5. **.NET 10**: instalar o SDK do .NET 10 (LTS). Hoje a máquina tem só o .NET 9, cujo suporte termina em 10/11/2026; o projeto centraliza a versão em `Directory.Build.props` para a troca ser de uma linha.
