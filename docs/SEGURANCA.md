# DinoTranslator — Segurança

Segurança é requisito, não acabamento: cada módulo só é integrado à `main` com os controles desta lista que lhe cabem.

O app tem dois modos de conexão. O **modo código** (padrão) não usa servidor; o **modo servidor** (opcional, desligado) mantém login e presença. As seções 2 a 4 valem para os dois modos; a seção 5, só para o modo servidor.

## 1. O que proteger

- **Conteúdo das conversas**: vídeo, áudio, tela, chat e falas.
- **Chaves dos serviços de reconhecimento e tradução**, no computador do anfitrião.
- **Os computadores dos dois**: o app não pode virar porta de entrada.

## 2. Ameaças consideradas

| Ameaça                                       | Exemplo                                                        | Controles |
|----------------------------------------------|----------------------------------------------------------------|-----------|
| Alguém entrar na chamada                     | Terceiro obtém o código de convite e responde antes do convidado | Convite de uso único com validade de 30 min; nome de quem respondeu na tela; código de segurança conferido em voz alta |
| Código adulterado no caminho (MITM)          | Troca da impressão digital DTLS dentro do código               | Código de segurança da chamada conferido em voz alta |
| Código malicioso                             | Texto enorme, compressão explosiva, campos inesperados         | Tamanho máximo antes e depois de descomprimir; validação estrita de cada campo; versão conhecida |
| Interceptação de rede                        | Wi-Fi público, provedor de internet                            | DTLS-SRTP na mídia e DTLS no canal de dados |
| Mensagem maliciosa pelo canal de dados       | HTML ou script no chat; mensagem gigante; tipo desconhecido    | Tamanho máximo; tipos conhecidos; todo texto renderizado como texto; CSP estrita |
| Navegação maliciosa no WebView2              | Link no chat abre página que tenta usar a câmera               | Navegação externa bloqueada; permissões só para a origem do app |
| Vazamento das chaves dos motores             | Chave lida do disco ou enviada pela chamada                    | Chaves cifradas com DPAPI; nunca trafegam pela chamada; nunca em log |
| Vazamento do histórico                       | Outra pessoa usa o computador                                  | Histórico cifrado com DPAPI; botão para apagar |
| Exposição do endereço de rede                | O código mostra o IP público a quem o vê                       | Códigos trocados só por mensageiro de confiança; IPs locais ocultos (mDNS do WebRTC) |
| Segredos no repositório                      | Chave de provedor em arquivo versionado                        | Segredos só no computador do usuário; varredura antes do push |
| Dependência vulnerável                       | Pacote NuGet com CVE                                           | Auditoria de pacotes no build (falha com vulnerabilidade) |
| Instalador adulterado                        | Arquivo trocado no caminho                                     | Assinatura de código; SHA-256 publicado |

## 3. Controles do modo código

### 3.1 Código da chamada (`cliente/codigo-chamada`)

- Formato `DT1.` + base64url de JSON comprimido; até 8 KB de texto e 32 KB descomprimido — acima disso é recusado sem processar.
- Campos obrigatórios e validados: versão, tipo (convite ou resposta), id da chamada (128 bits aleatórios), horário de criação, perfil (nome até 40 caracteres, idioma da lista, gênero, tratamento) e dados de conexão (credenciais ICE, impressão digital DTLS SHA-256, até 16 candidatos).
- Convite válido por 30 min e para uma única resposta; a resposta precisa trazer o mesmo id da chamada.
- **Código de segurança**: SHA-256 das duas impressões digitais DTLS (em ordem) → 12 dígitos exibidos para os dois.
- **Encontro automático** (a resposta volta sem o mensageiro e os dois computadores se procuram ao mesmo tempo, o que atravessa os roteadores domésticos): passa por um serviço público de mensagens (ntfy.sh por padrão; trocável ou desligável em `Encontro.Servidor` do appsettings.json, só HTTPS). Tópico e chave AES-256-GCM derivam por HKDF do id da chamada, que só existe nos códigos: o serviço vê um tópico aleatório, bytes cifrados e os endereços IP de quem conecta a ele. Mensagens de até 4 KB, linhas do fluxo de até 16 KB, tipo conhecido e o código dentro é validado como se tivesse sido colado. Sem repasse para notificações de celular.
- Enquanto a chamada abre, nenhum dos lados desiste no primeiro erro: o convidado espera enquanto o convite valer e o anfitrião tenta por 45 s depois de ter a resposta.

### 3.2 Mídia e canal de dados (`cliente/chamada`, `cliente/tela`)

- DTLS-SRTP obrigatório (padrão do WebRTC): só os dois computadores têm as chaves.
- STUN público usado só para descobrir o endereço de cada lado; nenhum dado da chamada passa por ele.
- Retransmissão (TURN) opcional, configurada pela pessoa para redes que não deixam a ligação direta passar: só repassa pacotes já cifrados (DTLS-SRTP). Endereço validado (`turn:`/`turns:`), usuário e senha guardados com DPAPI em `conexao.dat`, nunca enviados pela chamada.
- Canal de dados: JSON de até 16 KB por mensagem, tipos conhecidos, limite de mensagens por segundo; o que não for reconhecido é descartado.
- O app não abre portas de entrada nem fica escutando conexões fora de uma chamada.
- Renegociação de mídia só é aceita pelo canal de sinal e com o mesmo certificado DTLS do início da chamada.

### 3.3 Motores de tradução (`cliente/traducao`)

- Região e chave configuradas só no computador que traduz, cifradas com DPAPI (usuário atual + entropia própria) em `motores.dat`; a chave não volta a ser exibida na tela.
- Nunca enviadas pela chamada, nunca em log; erros do serviço viram frases sem detalhes internos.
- Áudio enviado ao motor só durante a chamada, a partir da captura do próprio app (blocos de até 64 KB).
- Legendas recebidas são aceitas só de quem foi escolhido para traduzir e passam pela mesma validação das mensagens do canal.
- Chat escrito e correções: mensagens de tipo próprio (texto com id `e-…`, correção de um trecho), com texto exibível de até 2.000 caracteres e ao menos uma letra ou número. Quem traduz aceita no máximo 40 por minuto da outra pessoa (cada uma custa uma tradução) e só corrige trechos que são dela — ninguém muda a fala dos outros.
- O motor simulado e a variável que o liga existem só no build Debug.
- Motor local: modelos baixados por HTTPS de versões fixas dos repositórios, conferidos por tamanho e SHA-256 ao baixar e de novo ao carregar (a pasta pode estar em outro disco); arquivo diferente é recusado. A pasta só pode ficar num disco fixo deste computador, e apagar os modelos remove só os arquivos do catálogo.
- Motores extras: cada pacote tem endereço HTTPS, tamanho e SHA-256 fixos no código (instalador e app); o que chega diferente é apagado antes de ser aberto, e a extração recusa qualquer entrada que tente sair da pasta do componente. Ficam na pasta `extras` do próprio app, com a mesma confiança dos arquivos do app.
- Motor auxiliar (Python): processo filho que conversa só pela entrada e saída padrão, em modo isolado (`-I`: sem variáveis PYTHON* nem pacotes do usuário), com o acesso à internet do Hugging Face desligado; não abre portas, recebe só caminhos e textos do app, limita o áudio a 30 s e o texto a 2.000 caracteres, e termina quando o app fecha a entrada.

### 3.4 Dados locais

- Perfil e preferências em `%LOCALAPPDATA%\DinoTranslator`.
- Histórico de chat e falas cifrado com DPAPI; opção de não guardar e botão para apagar.

## 4. Controles comuns aos dois modos

### 4.1 App (`cliente/*`)

- O BlazorWebView só carrega conteúdo local empacotado no app.
- Toda navegação fora da origem do app é cancelada; novas janelas e downloads são bloqueados; links externos abrem no navegador padrão após confirmação.
- CSP estrita: sem scripts, estilos ou fontes de fora do app. Conexões só para o próprio app e, por `wss:`/`https:`, para o servidor de mídia das chamadas em grupo (LiveKit); as chamadas à API da versão Plus saem do C#, não da página. A única exceção é `'wasm-unsafe-eval'`, que deixa compilar WebAssembly dos arquivos do próprio app (os supressores de ruído); não libera `eval` de JavaScript.
- Supressores de ruído de terceiros (`wwwroot/js/vendor`): copiados sem mudança de pacotes npm fixos; o SHA-256 de cada arquivo fica em `ORIGEM.json`, um teste recusa arquivo alterado ou fora da lista, e `tools/atualizar-supressores.ps1` refaz a cópia conferindo o pacote publicado. Eles não acessam a rede: recebem os binários do app e rodam só sobre o áudio do microfone.
- Câmera, microfone e tela são liberados só para a origem do app; qualquer outra permissão é negada.
- DevTools, menu de contexto padrão, atalhos do navegador e preenchimento automático desligados na versão final.
- Na versão final, o app apaga ao iniciar as variáveis de ambiente do WebView2 (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` e afins), que permitiriam a um atalho ou lançador malicioso ligar a depuração remota, trocar o navegador embutido ou a pasta de dados.
- Variáveis de desenvolvimento (`DINOTRANSLATOR_DATA`, `DINOTRANSLATOR_MODE`, `DINOTRANSLATOR_SERVER`, `DINOTRANSLATOR_RENDEZVOUS`) só existem no build Debug.
- Texto vindo do outro usuário é sempre renderizado como texto.

### 4.2 Repositório e distribuição

- Nada de segredos, bancos de dados, certificados ou builds no Git (`.gitignore`).
- Auditoria de dependências vulneráveis em todo build.
- Instalador assinado; SHA-256 publicado com cada versão.
- Instalação por usuário, sem administrador. O registro em "Aplicativos instalados" é gravado antes dos atalhos e aponta para o `Desinstalar.exe` da pasta do app. O app só conserta o registro numa pasta instalada pelo instalador (registrada, ou apontada pelo atalho do menu Iniciar), e desinstalar uma cópia só remove o registro e os atalhos dela. A pasta do app só é apagada depois que o desinstalador fecha, por um processo que recebe o caminho por variável de ambiente, não entra em junções e recusa a raiz de um disco ou uma pasta sem o app. Os testes usam registro e atalhos próprios.

## 5. Versão Plus: o servidor (`DinoTranslator.Server`)

Controles do servidor da versão paga (contas, grupos, chat e chamadas em grupo).

### 5.1 Autenticação e contas

- Cadastro pelo app (`/api/auth/register`) pode ser desligado ou exigir um código de 12+ caracteres (comparado em tempo constante); 5 cadastros por IP por hora. O comando `users` no servidor cria contas, ativa o Plus e suspende.
- Limite de contas configurável (`Auth:MaxUsers`, 0 = sem limite).
- Plano no token de acesso; a política Plus confere o prazo de novo a cada requisição. Conta grátis recebe 403 em tudo da versão paga, inclusive no tempo real.
- Suspensão: muda o carimbo da sessão, revoga os refresh tokens e recusa login e renovação.
- Usuário: 3–32 caracteres `[a-z0-9._-]`, comparado em minúsculas.
- Senha: 12 a 128 caracteres; não pode conter o nome de usuário.
- Hash: `PasswordHasher` do ASP.NET Core Identity (PBKDF2-HMAC-SHA512, 210 mil iterações, como recomenda a OWASP; sal aleatório), refeito no próximo login quando o formato ou as iterações mudam.
- Bloqueio: 5 falhas seguidas → 15 min; o contador zera no sucesso.
- Tempo de resposta uniforme: usuário inexistente também passa pelo hash, para não revelar quais usuários existem.
- Limite de requisições: login 10/min por IP; renovação de token 30/min por IP.
- JWT: HS256, chave de no mínimo 256 bits vinda de configuração segura; valida emissor, audiência e expiração (10 min, tolerância de 30 s); aceito só no cabeçalho `Authorization`.
- O carimbo da sessão é conferido em toda requisição, com cache de 20 s (compartilhado pelo Redis): troca de senha e suspensão valem em todas as instâncias em até 20 s, sem uma consulta ao banco por requisição.
- Refresh token: 256 bits aleatórios, guardado como SHA-256, validade de 30 dias, trocado a cada uso. Reapresentar um token já trocado revoga a sessão inteira.
- Sair revoga a sessão; trocar a senha revoga todas as sessões.
- No app: refresh token cifrado com DPAPI; token de acesso só em memória; validação de certificado padrão do Windows, nunca desligada.

### 5.2 Grupos, chat e tempo real

- Toda rota confere se a pessoa pode ver e mexer no grupo, canal ou mensagem; o que ela não pode ver responde 404, igual ao que não existe. Papéis: só administradores criam e apagam canais e removem membros de papel menor; só o dono apaga o grupo e muda papéis.
- Convites de 10 caracteres aleatórios (cerca de 50 bits), com validade (até 30 dias) e limite de usos contados no próprio banco.
- Bloqueio: quem bloqueou não aparece para quem foi bloqueado (pedido de amizade responde "não existe") e a conversa direta é recusada. Conversa direta só entre amigos ou quem divide um grupo.
- Mensagens até 4.000 caracteres, guardadas como foram escritas e mostradas sempre como texto. O servidor não traduz nem manda texto a serviços de fora.
- Limites por conta: 10 mensagens a cada 10 s e 60 ações por minuto; no hub, limite por conexão e mensagens de até 64 KB.
- Quem sai ou é removido de um grupo é tirado na hora dos grupos do SignalR em todas as instâncias: não recebe mais nada dele.
- A antiga sala de duas pessoas foi removida: num servidor com muitas contas ela mandaria presença e sinais à conta errada.

### 5.3 Chamada 1:1 pela conta

- O servidor só repassa o convite e a resposta da chamada (os mesmos códigos do modo código) entre as duas pessoas da conversa direta: quem não é da conversa, ou quando uma bloqueou a outra, recebe erro e nada é entregue.
- Sinais conferidos no hub: tipo da lista, id da chamada com 128 bits em hexadecimal, código só no convite e na resposta, até 16 KB; toques limitados por conexão (5 seguidos, depois 1 a cada 10 s).
- A mídia continua direta e cifrada de ponta a ponta (DTLS-SRTP); o servidor não a recebe. O código de segurança na tela da chamada vem das impressões digitais DTLS dos dois lados: se o servidor trocasse alguma no caminho, os códigos ficariam diferentes.
- No app, o convite recebido é validado como no modo código (versão, prazo, campos) e precisa ter o mesmo id da chamada do sinal; o nome mostrado é o da conta, vindo do servidor. Pela conta, o serviço público de encontro não é usado.
- Atendida ou recusada num aparelho, os outros aparelhos da mesma conta param de tocar; quem já está numa chamada responde "ocupado" sozinho.

### 5.4 Chamadas em grupo (LiveKit)

- A API emite a permissão (JWT HS256 com o segredo do LiveKit, 32+ caracteres) só para a sala do canal de voz que a pessoa pode usar, com validade de 6 h.
- Os webhooks do LiveKit são aceitos só com assinatura válida (JWT com o SHA-256 do corpo); corpo alterado ou segredo errado é recusado.
- As legendas que chegam pelo canal de dados são conferidas (tamanho e campos) antes de aparecer.

### 5.5 TURN (conexão garantida)

- Credencial só para conta Plus ou Pro (`/api/ice-servers` com a política do plano pago), válida por 12 h: `use-auth-secret` com segredo próprio de 32+ caracteres; o coturn confere a assinatura e a validade sozinho.
- O usuário do TURN não traz o id da conta: é um apelido de 128 bits (HMAC-SHA256 da conta com o segredo), porque o TURN manda o usuário sem cifrar.
- O app só aceita endereços `turn:`/`turns:` da resposta (no máximo 4), guarda a credencial só em memória e a renova meia hora antes de vencer; sair da conta ou trocar de servidor descarta a guardada.
- Sem acesso a redes internas nem ao próprio servidor: `denied-peer-ip` para todas as faixas privadas e reservadas de IPv4 e IPv6 (inclui a rede do Docker, onde ficam banco e Redis), `no-multicast-peers`; laço local negado por padrão.
- Só retransmissão: `no-stun` (sem reflexão de STUN), `no-tcp-relay`, `no-software-attribute`, `fingerprint`, CLI desligada; cotas por credencial (12 alocações), no total (1.200) e banda por sessão (10 Mbit/s).
- A mídia continua cifrada de ponta a ponta (DTLS-SRTP): o coturn só repassa pacotes.

### 5.6 Infraestrutura

- Firewall negando tudo exceto 80 e 443 (HTTPS), 7881/TCP e 7882/UDP (LiveKit), 3478 UDP/TCP (TURN), faixa de relay do TURN (49160–49999/UDP) e SSH.
- SSH só por chave, sem login de root.
- Atualizações automáticas de segurança do sistema.
- Contêineres sem root, imagens com versão fixa, sistema de arquivos somente leitura onde possível.
- Segredos em variáveis de ambiente/arquivos de segredo do Docker com permissão restrita.
- Cabeçalhos: HSTS, `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options`.
