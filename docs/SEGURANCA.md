# DinoTranslator — Segurança

Segurança é requisito, não acabamento: cada módulo só é integrado à `main` com os controles desta lista que lhe cabem.

## 1. O que proteger

- **Conteúdo das conversas**: vídeo, áudio, tela, chat e falas.
- **Credenciais** dos dois usuários.
- **O servidor**: não pode virar porta de entrada nem relay aberto para terceiros.

## 2. Ameaças consideradas

| Ameaça                                   | Exemplo                                                     | Controles |
|------------------------------------------|-------------------------------------------------------------|-----------|
| Acesso de terceiros                      | Alguém descobre o endereço do servidor e tenta entrar       | Sem cadastro; limite de 2 contas; senha forte; bloqueio por tentativas |
| Força bruta e senhas vazadas             | Robô testando milhares de senhas                            | Bloqueio 5 falhas/15 min; limite de requisições por IP; hash lento |
| Roubo de token                           | Programa malicioso lê arquivos do usuário                   | Refresh token cifrado com DPAPI; JWT de 15 min; rotação com detecção de reuso; revogação |
| Interceptação de rede                    | Wi-Fi público, provedor de internet                         | TLS 1.2+ (1.3 preferido), HSTS, WSS, DTLS-SRTP |
| Servidor comprometido alterando a chamada | Troca das impressões digitais DTLS na sinalização (MITM)    | Código de segurança da chamada conferido em voz alta |
| Abuso do TURN                            | Relay aberto; acesso à rede interna do servidor (SSRF)      | Credenciais temporárias HMAC; bloqueio de faixas privadas; cotas |
| Conteúdo malicioso no chat               | Mensagem com HTML ou script                                 | Todo texto renderizado como texto; CSP estrita |
| Navegação maliciosa no WebView2          | Link no chat abre página que tenta usar a câmera            | Navegação externa bloqueada; permissões só para a origem do app |
| Vazamento por logs                       | Transcrições ou tokens gravados em log                      | Logs sem conteúdo das conversas, senhas ou tokens |
| Segredos no repositório                  | Chave de provedor em `appsettings.json`                     | Segredos só em user-secrets/variáveis de ambiente; varredura antes do push |
| Dependência vulnerável                   | Pacote NuGet/npm com CVE                                    | `dotnet list package --vulnerable` e `npm audit` no CI |
| Instalador adulterado                    | Arquivo trocado no caminho                                  | Assinatura de código; SHA-256 publicado; atualização verifica assinatura |
| Uso abusivo do servidor                  | Envio contínuo de áudio para sobrecarregar a tradução       | Só autenticados; limite diário de minutos; tamanho de quadro limitado |

## 3. Controles por módulo

### 3.1 Autenticação (`servidor/autenticacao`)

- Contas criadas **apenas** pelo comando `users add`, executado no próprio servidor. Não existe endpoint de cadastro.
- `MaxUsers = 2` conferido no banco antes de criar qualquer conta.
- Usuário: 3–32 caracteres `[a-z0-9._-]`, comparado em minúsculas.
- Senha: 12 a 128 caracteres; não pode conter o nome de usuário.
- Hash: `PasswordHasher` do ASP.NET Core Identity (PBKDF2-HMAC-SHA512, 100 mil iterações, sal aleatório), com re-hash automático se o formato evoluir.
- Bloqueio: 5 falhas seguidas → 15 min; o contador zera no sucesso.
- Tempo de resposta uniforme: usuário inexistente também passa pelo hash, para não revelar quais usuários existem.
- Limite de requisições: login 10/min por IP; renovação de token 30/min por IP.
- JWT: HS256, chave de no mínimo 256 bits vinda de configuração segura; valida emissor, audiência e expiração (15 min, tolerância de 30 s).
- Refresh token: 256 bits aleatórios, guardado como SHA-256, validade de 30 dias, trocado a cada uso. Reapresentar um token já trocado revoga a sessão inteira.
- Sair revoga a sessão; trocar a senha revoga todas as sessões.

### 3.2 Tempo real (`servidor/sinalizacao`, `chat/*`, `servidor/traducao`)

- SignalR e WebSocket só aceitam usuários autenticados.
- Tamanho máximo de mensagem (SignalR 64 KB; quadro de áudio 4 KB) e validação de todos os campos (chat até 4.000 caracteres).
- Mensagens só são repassadas ao outro usuário — não existem salas arbitrárias.
- Limite de mensagens por segundo por conexão.
- Áudio para tradução: processado em memória, **nunca gravado**; preferência por provedores com opção de não reter dados.

### 3.3 TURN (`infra/deploy`)

- `use-auth-secret` com segredo próprio; credenciais válidas por 12 h emitidas pelo servidor.
- Sem acesso a redes internas: `denied-peer-ip` para `0.0.0.0/8`, `10.0.0.0/8`, `100.64.0.0/10`, `127.0.0.0/8`, `169.254.0.0/16`, `172.16.0.0/12`, `192.168.0.0/16`, `::1`, `fc00::/7`, `fe80::/10`; `no-multicast-peers`.
- `no-cli`, `fingerprint`, TLS com o certificado do domínio, cotas por usuário e banda máxima.

### 3.4 Infraestrutura (`infra/deploy`)

- Firewall negando tudo exceto 443/TCP (HTTPS), 3478 UDP/TCP e 5349/TCP (TURN), faixa de portas de relay do TURN e SSH.
- SSH só por chave, sem login de root.
- Atualizações automáticas de segurança do sistema.
- Contêineres sem root, imagens com versão fixa, sistema de arquivos somente leitura onde possível.
- Segredos em variáveis de ambiente/arquivos de segredo do Docker com permissão restrita.
- Backup do banco cifrado.
- Cabeçalhos: HSTS, `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options`.

### 3.5 Cliente (`cliente/*`)

- O BlazorWebView só carrega conteúdo local empacotado no app.
- Toda navegação fora da origem do app é cancelada; novas janelas são bloqueadas; links externos abrem no navegador padrão após confirmação.
- CSP estrita: sem scripts inline ou remotos; conexões só com o servidor configurado.
- Câmera, microfone e tela são liberados só para a origem do app; qualquer outra permissão é negada.
- DevTools, menu de contexto padrão e atalhos do navegador desligados na versão final.
- Refresh token cifrado com DPAPI (usuário atual + entropia própria); token de acesso só em memória.
- Validação de certificado padrão do Windows, nunca desligada. Opcional: fixação da chave pública do servidor.
- Nenhuma chave de provedor no cliente.
- Texto vindo do outro usuário é sempre renderizado como texto.

### 3.6 Mídia (`cliente/chamada`, `cliente/tela`)

- DTLS-SRTP obrigatório (padrão do WebRTC): com P2P, só os dois computadores têm as chaves; o TURN apenas repassa pacotes cifrados.
- **Código de segurança da chamada**: SHA-256 das duas impressões digitais DTLS (em ordem) → 12 dígitos exibidos para os dois. Se forem iguais, ninguém está no meio.

### 3.7 Repositório e distribuição

- Nada de segredos, bancos de dados, certificados ou builds no Git (`.gitignore`).
- Varredura de segredos (gitleaks) e de dependências vulneráveis no CI.
- Instalador assinado; SHA-256 publicado com cada versão; o app só aplica atualização com assinatura válida.
