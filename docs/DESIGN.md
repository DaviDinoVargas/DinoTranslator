# DinoTranslator — Identidade visual

## 1. Conceito: vidro sobre a noite

Um app de chamada que parece nativo de um telefone moderno: **limpo, centralizado e em vidro**. O fundo é um papel de parede azul-noite com o padrão da porcelana **Gzhel** (azul russo) bem sutil nas bordas, um brilho dourado de **ipê** (amarelo brasileiro) no canto de cima e um azul no de baixo. Por cima dele, tudo é feito de **vidro fosco**: painéis translúcidos que desfocam o fundo, com borda fina clara e um reflexo no alto.

A marca são dois balões de conversa que se encontram: o **dourado com o "A"** atrás e o **azul com o "Я"** na frente — o português e o russo, as duas pessoas da chamada.

## 2. Princípios

- **Centralizado e alinhado**: colunas de 540–580 px no meio da tela; títulos e textos curtos centralizados; formulários alinhados à esquerda dentro dos cartões.
- **Vidro, não caixa**: superfícies translúcidas com desfoque (`backdrop-filter: blur(28px) saturate(170%)`), borda `rgba(255,255,255,.13)`, brilho `inset 0 1px 0 rgba(255,255,255,.12)` e reflexo diagonal.
- **Redondo**: pílulas em botões, campos com 14 px, cartões com 22 px, folhas com 30 px; controles da chamada em círculos.
- **Pouco texto, ícones claros**: ícones próprios em traço de 1,8 px com pontas arredondadas, embutidos no app.
- **Nada da internet**: fontes, ícones e imagens vão dentro do app (a política de segurança da página só aceita o que é do próprio app).

## 3. Cores (modo escuro)

| Token            | Valor                       | Uso                                         |
|------------------|-----------------------------|---------------------------------------------|
| fundo            | `#0B1124` → `#060912`       | papel de parede, com brilhos e padrão Gzhel |
| `--vidro`        | `rgba(255,255,255,.075)`    | cartões, folhas, listas                     |
| `--vidro-2`/`-3` | `.11` / `.17`               | botões de vidro, hover                      |
| `--preenchimento`| `rgba(120,128,160,.2)`      | campos de texto, trilhos                    |
| `--texto`        | `#F5F7FF`                   | texto principal                             |
| `--texto-2`/`-3` | 64% / 40% de opacidade      | secundário / terciário                      |
| `--azul`         | `#0A84FF`                   | ação principal, seus balões na conversa     |
| `--verde`        | `#30D158`                   | conectado, código de segurança              |
| `--laranja`      | `#FF9F0A`                   | avisos, conexão instável                    |
| `--vermelho`     | `#FF453A`                   | encerrar, erros                             |
| `--amarelo`      | `#FFD60A`                   | destaque de aviso                           |
| ipê / Gzhel      | `#FFE15A→#FF9F0A` / `#5AB0FF→#0A6FE0` | marca                             |

## 4. Tipografia

**Inter** (OFL), embutida em `wwwroot/fonts`, com latim e cirílico completos (português, inglês, espanhol, russo e ucraniano). Títulos grandes em 700 com espaçamento −0,02 em (26–34 px); interface em 15 px; números alinhados (`tabular-nums`). Códigos em Cascadia Mono/Consolas. Nas janelas nativas (barra de título, instalador), Segoe UI Variable.

## 5. Componentes

- **Tela inicial**: marca, título grande, idiomas em pílulas, cartão do perfil (toque para editar), aviso quando a tradução está desligada, ações em pílula larga e os **Ajustes em lista agrupada** com ícones em quadradinhos coloridos (tradução azul, câmera e áudio verde, conexão índigo).
- **Convite e entrada**: folha de vidro centralizada; andamento em três barras (Convite · Resposta · Chamada); ao conectar, o avatar com ondas.
- **Chamada (como no FaceTime)**: o vídeo é um cartão arredondado flutuando sobre o papel de parede; por cima dele, uma **pílula de vidro** no topo (pessoa, código de segurança, sinal e qualidade) e outra embaixo com **controles redondos** (desligado = círculo branco; encerrar = vermelho). Legendas em cartões de vidro acima dos controles. A barra de cima encolhe por etapas conforme a largura do vídeo.
- **Palco**: uma fonte em destaque (câmera da outra pessoa, tela dela ou a sua tela) e as outras em **miniaturas arredondadas** na coluna da direita; passar o mouse mostra o botão de ampliar e um clique troca o destaque. Sua câmera é sempre a última miniatura, espelhada. A tela que acabou de começar a ser mostrada vai para o destaque sozinha.
- **Conversa**: painel de vidro ao lado do vídeo (nunca por cima dele, exceto em janela estreita), com balões no estilo das mensagens do iPhone — os seus em azul à direita, os da outra pessoa em vidro à esquerda; tradução e original no mesmo balão. No pé, o **campo de escrever** em pílula com o botão azul de enviar; nos seus balões, um **lápis** para corrigir o texto (a edição acontece dentro do próprio balão) e as marcas "escrita" e "editada" junto do horário.
- **Plus**: quatro colunas de vidro escuro — trilho de grupos (círculos com as iniciais que viram quadrados arredondados no hover e no grupo aberto, ponto vermelho de não lido), canais (# para texto, alto-falante para voz com quem está dentro), o canal aberto e os membros (online e offline, papel e idioma). Mensagens com autor, hora, a tradução em destaque e o original pequeno em itálico; conta no rodapé da segunda coluna. Selo **Plus** amarelo nos cartões e telas da versão paga.
- **Chamada em grupo**: quadros de vídeo e tela num grid com borda verde em quem fala; quem está sem câmera aparece como avatar grande com o anel verde; legendas em faixas escuras no idioma de quem vê; o mesmo dock da chamada por código (microfone, som, ruído, câmera, tela, sair).
- **Ruído**: a mesma folha de vidro dos motores, com o ícone em quadradinho verde; os modos numa lista de opções com descrição curta, intensidade num controle deslizante verde ("voz mais natural" ↔ "mais silêncio"), sensibilidade com o medidor (cinza abaixo do limite, verde acima) e a marca amarela do limite, e o que está ativo de fato numa linha embaixo. No teste de dispositivos, o mesmo bloco com "Ouvir meu microfone".
- **Motores**: folha de vidro sobre o palco, acima dos controles, com o ícone em quadradinho índigo; cada escolha numa lista agrupada (detector, fim da frase, reconhecimento, tradução, versão final), opções não instaladas desabilitadas, "Carregando os modelos…" com um anel girando e, embaixo, as médias de tempo, com a legenda final em verde.
- **Janela flutuante**: o vídeo com a legenda num cartão de vidro arredondado desenhado junto.
- **Barra de título**: ícone do app, relógios numa pílula translúcida no centro, sem divisória.
- **Ícone do app**: quadrado arredondado azul (do azul de sistema ao índigo) com o balão dourado e o branco — gerado em vetor por `tools/gerar-icone.ps1`.
- **Instalador e desinstalador**: o mesmo papel de parede, ícone em destaque, cartão de vidro e botões em pílula.

## 6. Movimento

160 ms com `cubic-bezier(.25,.8,.25,1)`; botões encolhem levemente ao clicar; o palco desliza quando a conversa abre. Tudo some com "reduzir animações" do Windows.

## 7. Acessibilidade

Contraste mínimo de 4,5:1 no texto; foco visível em azul; navegação por teclado; rótulos de texto em todos os controles (nos botões só com ícone, como dica e para leitores de tela).
