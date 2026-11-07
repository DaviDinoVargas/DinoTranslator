# DinoTranslator — Identidade visual

## 1. Conceito: Ipê e Gzhel

Dois símbolos culturais, sem bandeiras: o **ipê-amarelo**, árvore brasileira, e a **porcelana Gzhel**, o azul-cobalto da tradição russa. Na tela de cada um, **você é ipê e a outra pessoa é Gzhel**, seja qual for o idioma de cada um; tudo o que a pessoa diz — legenda, mensagem, quadro de câmera — leva essa cor. Quem olha a tela sabe de quem é cada frase sem ler o nome.

O fundo é escuro, cor de tinta; a tipografia é editorial; as divisões são linhas finas e os cantos são retos. A sensação desejada é a de uma cabine de interpretação, não a de um template.

## 2. Regras para não ficar genérico

- Sem gradientes coloridos, sem vidro fosco, sem cartões flutuando com sombra grande.
- Sem emojis como ícones: ícones próprios em traço de 1,5 px, com cantos retos.
- Sem fonte padrão do sistema: tipografia própria embutida no app.
- Raio de canto de 2 px (no máximo 4 px). Separação por linhas, não por sombras.
- Layout assimétrico: palco grande e trilho lateral; hierarquia pela tipografia, não por caixas.

## 3. Cores — tema escuro (padrão)

| Token            | Hex       | Uso                                              |
|------------------|-----------|--------------------------------------------------|
| `--tinta`        | `#12110F` | fundo                                            |
| `--superficie`   | `#1B1916` | painéis                                          |
| `--superficie-2` | `#24211D` | campos, hover                                    |
| `--linha`        | `#34302A` | divisórias                                       |
| `--papel`        | `#F1ECE2` | texto principal                                  |
| `--papel-2`      | `#A8A091` | texto secundário                                 |
| `--ipe`          | `#F2B705` | você: bordas, marcadores, texto de destaque      |
| `--ipe-fundo`    | `#3A2E0B` | fundos com a sua cor                             |
| `--gzhel`        | `#3D6FE0` | a outra pessoa: bordas e marcadores              |
| `--gzhel-texto`  | `#86A6F2` | a outra pessoa: texto de destaque (contraste ≥ 4,5:1) |
| `--gzhel-fundo`  | `#16264D` | fundos com a cor da outra pessoa                 |
| `--urucum`       | `#E0662F` | alertas, conexão instável                        |
| `--ok`           | `#4FA36C` | conectado                                        |
| `--erro`         | `#D9534F` | erro                                             |

Um tema claro ("porcelana": fundo `#F4F1EA`, texto `#1A1814`) entra na Fase 4.

## 4. Tipografia

Todas com cirílico e acentos do português, licença OFL, embutidas no app (nada carregado da internet):

| Fonte          | Uso                                                  |
|----------------|------------------------------------------------------|
| Unbounded      | marca e títulos                                      |
| Golos Text     | interface, legendas e chats                          |
| JetBrains Mono | números: ping, relógios, horários das mensagens      |

Escala: 12 · 14 · 16 · 20 · 28 · 40 px. Legenda traduzida em 26 px; original em 16 px.

## 5. Componentes

- **Barra de título**: marca "DT" em Unbounded; relógios duplos em mono — `SÃO PAULO 15:40 · МОСКВА 21:40`; ping com 4 barras (ok / urucum / erro).
- **Quadro de câmera**: borda superior de 2 px na cor da pessoa; etiqueta `RU · ru-RU` em mono no canto; a borda pulsa com o nível de voz quando a pessoa fala.
- **Legenda**: faixa sob o palco; original pequeno em `--papel-2`, tradução grande em `--papel`; trecho provisório com 60% de opacidade.
- **Painel Falas**: lista com régua vertical na cor de quem falou (não balões); hora em mono; tradução precedida de `›`.
- **Chat**: régua de cor por autor; o campo de texto mostra o idioma de destino (`→ RU`, `→ UK`, `→ EN`, `→ ES`, `→ PT`).
- **Dock de controles**: botões quadrados de 44 px com rótulo abaixo; ativo = preenchido na cor do usuário.
- **Código de segurança**: 12 dígitos em mono, em grupos de 4, num selo discreto na barra de título.
- **Instalador**: mesma paleta e tipografia; ilustração em linha de um galho de ipê com um padrão Gzhel.

## 6. Movimento

120–180 ms, curva `cubic-bezier(.2, 0, 0, 1)`, sem efeito elástico. Respeita a opção "mostrar animações" do Windows.

## 7. Acessibilidade

Contraste mínimo de 4,5:1 em texto; navegação completa por teclado; foco visível (contorno de 2 px na cor do usuário); tamanho da legenda ajustável.

## 8. Validação

Mockups de login, sala, sala com tela compartilhada, configurações e instalador serão aprovados pelos dois antes do acabamento final (Fase 4).
