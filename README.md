# PsMrbin · TempBin

Partilha efémera de texto simples em **ASP.NET Core 8 + Redis**, preparada para Docker em Linux. Sem login: cole texto, guarde e partilhe um link. O conteúdo expira **1 800 segundos após a criação**. Leitura, download e consulta de metadados nunca renovam o prazo.

## Início rápido com Docker

Requisitos: Docker Engine e Docker Compose v2 num servidor Linux.

```bash
cp .env.example .env
docker compose up -d --build --wait
```

Abra `http://localhost:8080`. O Redis fica apenas na rede interna, sem porta publicada. A aplicação escuta por padrão em `127.0.0.1:8080`, para utilização local ou atrás de um reverse proxy HTTPS. O Compose também ativa o serviço netcat em `127.0.0.1:9999`.

Para publicar num domínio, configure `.env`:

```dotenv
PUBLIC_ORIGIN=https://bin.exemplo.com
HTTP_BIND=127.0.0.1
HTTP_PORT=8080
```

Encaminhe o domínio pelo proxy para `127.0.0.1:8080`. Termine TLS no proxy e limite o corpo a pelo menos 1 600 000 bytes para permitir JSON com caracteres escapados. Se o proxy correr noutro contentor, ligue-o à rede `frontend` e encaminhe para `web:8080`, ou ajuste o bind conforme a sua infraestrutura. `PUBLIC_ORIGIN` deve ser uma origem HTTP(S) absoluta, sem caminho, credenciais, query ou fragmento. Os links nunca são construídos a partir do header `Host`.

```bash
docker compose ps
docker compose logs --tail=50 web
docker compose down
```

O arranque aguarda o health check do Redis; a aplicação tem checks de disponibilidade e prontidão. Os contentores têm limites de memória, reinício `unless-stopped`, capabilities removidas e `no-new-privileges`. A aplicação corre com utilizador não root e filesystem somente de leitura.

## Desenvolvimento

Instale o SDK .NET 8 e inicie um Redis 7 ou posterior:

```bash
docker run --rm --name tempbin-dev -p 127.0.0.1:6379:6379 redis:7.4-alpine redis-server --save '' --appendonly no
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet run --project src/TempBin --urls http://localhost:8080
```

Não existe fallback em memória em produção: indisponibilidade do Redis devolve erro `503`, sem gravar texto em disco.

### Configuração

| Variável | Padrão | Descrição |
| --- | --- | --- |
| `TempBin__PublicOrigin` | `http://localhost:8080` | Origem validada para todos os links |
| `TempBin__Redis` | `localhost:6379,abortConnect=false` | Ligação StackExchange.Redis; use secrets do ambiente para credenciais |
| `TempBin__KeyPrefix` | `tempbin:` | Namespace exclusivo no Redis |
| `TempBin__DeletionKey` | aleatória em memória | Segredo Base64 com pelo menos 32 bytes para HMAC do IP; opcional, nunca versionar |
| `TempBin__KnownProxies__0` | ausente | IP de um proxy HTTP de confiança; adicione `__1`, etc. para outros |
| `Netcat__Enabled` | `false` fora do Compose | Ativa o listener TCP; o Compose ativa por padrão |
| `Netcat__ListenAddress` | `0.0.0.0` | IP local do listener TCP |
| `Netcat__Port` | `9999` | Porta interna do listener TCP |
| `Netcat__PublicHost` | hostname de PublicOrigin | Host apresentado nos exemplos netcat da página inicial |
| `Netcat__PublicPort` | `9999` | Porta externa apresentada nos exemplos |
| `Netcat__IdleTimeoutMilliseconds` | `2000` | Inatividade que termina o envio TCP |
| `Netcat__TotalTimeoutSeconds` | `15` | Duração máxima do envio TCP |
| `PUBLIC_ORIGIN` | `http://localhost:8080` | Variável do Compose que preenche `TempBin__PublicOrigin` |
| `HTTP_BIND` | `127.0.0.1` | Interface de publicação da porta Docker |
| `HTTP_PORT` | `8080` | Porta externa Docker |
| `TCP_BIND` | `127.0.0.1` | Interface de publicação do TCP no Compose |
| `TCP_PORT` | `9999` | Porta TCP externa no Compose |
| `NETCAT_ENABLED` | `true` no Compose | Permite desativar o listener sem alterar código |
| `DELETION_KEY` | ausente | Preenche `TempBin__DeletionKey` no Compose |
| `TEMPBIN_TEST_REDIS` | ausente | Redis real para os testes de integração de armazenamento |

Os padrões estão em `src/TempBin/appsettings.json`. `.env` é ignorado pelo Git. Não coloque credenciais no repositório.

## API e curl

| Método | Rota | Resultado |
| --- | --- | --- |
| `POST` | `/api/v1/pastes` | Cria texto; `201 Created` e header `Location` |
| `GET` | `/{slug}` | Página HTML com links, pré-visualização e contador |
| `GET` | `/raw/{slug}` | Texto puro UTF-8 |
| `GET` | `/api/v1/pastes/{slug}` | Conteúdo, metadados e links em JSON |
| `GET` | `/api/v1/pastes/{slug}/meta` | Metadados sem conteúdo |
| `GET` | `/download/{slug}` | Download `.txt` sem alterar os bytes UTF-8 |
| `DELETE` | `/api/v1/pastes/{slug}` | Elimina nos primeiros 10 minutos, pelo mesmo IP |
| `POST` | `/delete?slug={slug}` | Forma compatível com a remoção do termbin |
| `GET` | `/health/live` | Processo ativo |
| `GET` | `/health/ready` | Ligação Redis operacional |
| `GET` | `/openapi` | Redireciona para a especificação OpenAPI JSON |

Criar texto simples:

```bash
curl -X POST http://localhost:8080/api/v1/pastes \
  -H 'Content-Type: text/plain; charset=utf-8' \
  --data-binary 'Olá, mundo!'
```

Publicar ficheiro, escolhendo um slug de 12 caracteres:

```bash
curl -X POST 'http://localhost:8080/api/v1/pastes?slug_length=12' \
  -H 'Content-Type: text/plain; charset=utf-8' \
  --data-binary @exemplo.txt
```

Publicar JSON:

```bash
curl -X POST http://localhost:8080/api/v1/pastes \
  -H 'Content-Type: application/json' \
  --data-binary '{"content":"Olá, mundo!","slug_length":8}'
```

Exemplo de resposta (o slug e as datas reais são gerados no servidor):

```json
{
  "slug": "m7k2p9x4",
  "url": "http://localhost:8080/m7k2p9x4",
  "raw_url": "http://localhost:8080/raw/m7k2p9x4",
  "api_url": "http://localhost:8080/api/v1/pastes/m7k2p9x4",
  "download_url": "http://localhost:8080/download/m7k2p9x4",
  "created_at": "2026-11-08T20:00:00+00:00",
  "expires_at": "2026-11-08T20:30:00+00:00",
  "ttl_seconds": 1800
}
```

Substitua `m7k2p9x4` pelo slug recebido:

```bash
curl http://localhost:8080/raw/m7k2p9x4
curl http://localhost:8080/api/v1/pastes/m7k2p9x4
curl http://localhost:8080/api/v1/pastes/m7k2p9x4/meta
curl http://localhost:8080/download/m7k2p9x4 -o texto.txt
```

O link principal também devolve texto puro para curl/wget ou `Accept: text/plain`; os navegadores continuam a receber a página HTML. Use `Accept: text/html` para obter a página pelo curl. `/raw/{slug}` é sempre texto puro, independentemente dos headers.

Erros: `400` para JSON/UTF-8/valores inválidos ou texto vazio, `413` para excesso de tamanho, `415` para tipo de conteúdo não suportado, `404` para slug desconhecido, `410` para texto expirado ou eliminado com marcador ainda presente, `403` para remoção não autorizada ou fora do prazo, `429` para limite de pedidos e `503` para armazenamento indisponível. As respostas de erro têm a propriedade `error`.

## Terminal: netcat, ficheiros e pipes

Inspirado no [termbin.com](https://termbin.com/), o serviço TCP aceita texto UTF-8 e devolve **apenas uma URL de texto puro seguida de newline**. Não precisa de headers HTTP nem bibliotecas cliente. Usa os mesmos slugs criptográficos, Redis, limite de 256 KiB e expiração de 30 minutos da API.

```bash
echo 'Olá, mundo!' | nc localhost 9999
cat exemplo.txt | nc localhost 9999
ls -la | nc localhost 9999
journalctl -n 50 --no-pager | nc localhost 9999
```

Leia o link devolvido diretamente com `curl`. Para aceder à página HTML, remova `/raw` da URL ou consulte o slug em `/{slug}`. Nessa página pode ativar números de linha sem alterar o texto original; a numeração é limitada a 10 000 linhas para conter o custo de renderização.

O envio termina quando o cliente fecha a escrita (EOF / half-close, por exemplo `nc -N`) ou após **2 segundos sem novos bytes**. O limite total é **15 segundos**. Dados pausados por mais de 2 segundos podem ser tratados como o fim do texto; para uploads lentos ou grandes, use HTTP. Se o prazo total terminar durante o envio, o conteúdo parcial é rejeitado. As respostas de erro começam por `ERROR <código>:` e não são URLs. Cada ligação cria no máximo uma publicação.

Por IP são permitidas 20 ligações TCP por minuto; há um teto global separado de 600 ligações/minuto e 32 envios concorrentes. Os limites TCP são independentes dos limites HTTP e locais a cada instância. A fila de escuta é limitada. `nc`, `ncat` ou `netcat` são clientes possíveis; flags de EOF/timeout variam entre versões. O modo sem flags funciona pelo timeout de inatividade do servidor.

### Atalhos úteis

Adicione à configuração da sua shell (`~/.bashrc`, `~/.zshrc` ou `~/.bash_profile`, conforme o sistema):

```bash
alias tb='nc localhost 9999'
ls -la | tb
```

Para copiar a URL para o clipboard:

```bash
# Linux com Wayland
ls -la | tb | wl-copy
# Linux com X11
ls -la | tb | xclip -selection clipboard
# macOS
ls -la | tb | pbcopy
```

Sem netcat, envie stdin por HTTPS com curl:

```bash
ls -la | curl -H 'Content-Type: text/plain; charset=utf-8' --data-binary @- https://bin.exemplo.com/api/v1/pastes
```

No PowerShell 7 com Ncat instalado: `Get-Content -Raw exemplo.txt | ncat localhost 9999`. Confirme que a shell envia UTF-8.

### Publicar a porta TCP

No Compose, mantenha o bind local para testes. Para clientes externos, configure `TCP_BIND=0.0.0.0`, `TCP_PORT=9999`, `PUBLIC_ORIGIN=https://bin.exemplo.com` e liberte apenas essa porta TCP na firewall. Não exponha Redis. Um reverse proxy HTTP não encaminha este protocolo: use ligação TCP direta ou um encaminhamento de nível 4 que preserve o IP de origem. O listener não interpreta PROXY protocol nem headers HTTP.

**TCP netcat não cifra o envio.** HTTPS protege apenas a leitura do link devolvido; para cifrar o envio, use o endpoint HTTPS com curl. Fora do Compose, ative explicitamente `Netcat__Enabled=true`.

## Remoção antecipada

Durante os primeiros **10 minutos desde a criação**, o IP que publicou pode eliminar o texto pela interface ou por HTTP, incluindo publicações criadas via TCP:

```bash
curl -X POST 'http://localhost:8080/delete?slug=m7k2p9x4'
curl -X DELETE http://localhost:8080/api/v1/pastes/m7k2p9x4
```

O sucesso devolve `204 No Content`. O conteúdo e o identificador do autor são removidos atomicamente, mantendo apenas o marcador sem conteúdo até completar as 24 horas originais. O link passa a `410`. Leituras não estendem nem a janela de remoção nem a validade da publicação. Aos 10 minutos exatos, a remoção já é recusada. Textos publicados antes desta funcionalidade não têm identificador de autor e continuam a expirar normalmente.

Guarda-se somente um **HMAC do IP**, na chave temporária do conteúdo. O IP em claro não é guardado nem registado. A chave HMAC é gerada em RAM por processo: reiniciar a aplicação sem `DeletionKey` torna impossível validar a autoria de textos anteriores, que expiram normalmente. Para suportar reinícios ou réplicas, forneça o mesmo segredo aleatório Base64 de pelo menos 32 bytes a todas as instâncias via `DELETION_KEY`/secret manager. Não o coloque no Git. A impressão HMAC continua a ser um identificador pseudónimo e deve ser tratada como tal.

**IP não equivale a identidade pessoal:** utilizadores numa rede NAT partilham a possibilidade de remoção de um link conhecido; mudar de IP pode impedir a remoção. Se um proxy ocultar todos os IPs, todos os clientes ficam no mesmo grupo. Em produção atrás de um proxy HTTP, configure `TempBin__KnownProxies__0` com o IP exato do proxy; o middleware aceita apenas um salto de `X-Forwarded-For` proveniente desses endereços. Headers enviados por pares não autorizados são ignorados. O proxy deve substituir os headers recebidos dos clientes. TCP através de um proxy que oculta o IP pode impedir a correspondência com o IP visto em HTTP. Pedidos de remoção vindos de páginas de outra origem são rejeitados; curl sem `Origin` continua suportado.

## Slugs e expiração

- Comprimento entre **8 e 16**, padrão **8**. A instrução da mensagem principal prevalece sobre o intervalo 4–16 do anexo: 4 a 7 são rejeitados tanto na API como na interface.
- Alfabeto: `abcdefghjkmnpqrstuvwxyz23456789`, sem símbolos nem caracteres visualmente ambíguos.
- Aleatoriedade criptográfica com `RandomNumberGenerator`, sem IDs sequenciais ou timestamps.
- Um script Lua no Redis verifica colisões e cria o conteúdo e o marcador numa operação atómica. Nunca substitui conteúdo existente; o serviço tenta novamente em caso de colisão.
- A hora autoritativa é `TIME` do Redis. `created_at` e `expires_at` têm precisão de milissegundos e diferem exatamente 30 minutos. Todas as leituras verificam a expiração no mesmo relógio. Mantenha o relógio do servidor sincronizado.
- A chave de conteúdo tem TTL fixo de 1 800 segundos. O Redis invalida logicamente a chave ao atingir o prazo e liberta a memória pela sua estratégia de expiração; não se promete sobrescrita física imediata da RAM.
- Um marcador sem texto e com TTL de **24 horas desde a criação** permite devolver `410` e impede a reutilização imediata. Após esse prazo o resultado passa a `404` e o slug pode voltar a ser sorteado.
- Não há edição, extensão do prazo, lista pública, pesquisa ou recuperação. A remoção antecipada encurta a disponibilidade, sem alterar o TTL original do marcador.

## Segurança e limites operacionais

Máximo de **256 KiB (262 144 bytes UTF-8)** por publicação. O corpo JSON tem teto de 1 600 000 bytes para acomodar escaping; o texto descodificado continua limitado a 256 KiB. Corpos chunked também são limitados. Texto vazio ou só com espaços é rejeitado.

O HTML é codificado antes de aparecer na pré-visualização. O frontend usa `textContent`; conteúdo publicado não é interpretado como HTML. CSP restritiva, `nosniff`, `no-store`, `Referrer-Policy: no-referrer`, `X-Robots-Tag` e `robots.txt` reduzem execução, cache e indexação. Não há dependências de fontes externas, analytics, autenticação ou cookies de sessão.

Limites por IP e minuto: **20 criações**, **120 leituras**, incluindo slugs inexistentes. Limite global por instância: **3 000 pedidos/minuto** e **64 pedidos concorrentes** que chegam ao middleware, além de 256 ligações Kestrel. `429` inclui `Retry-After`. Recursos estáticos são servidos antes do limitador. Estes limites reduzem enumeração e abuso, sem garantir resistência absoluta a ataques distribuídos.

Os limites são locais a cada instância. Atrás de um proxy, a configuração padrão usa o IP do proxy, partilhando o limite entre visitantes. Não se confia automaticamente em `X-Forwarded-For`. Configure `TempBin__KnownProxies__0`, etc. para IPs de confiança; a aplicação instala `UseForwardedHeaders` antes do rate limiter e da identificação para remoção. Em múltiplas réplicas, aplique limites agregados no proxy/gateway.

No Compose, Redis usa `--save '' --appendonly no`, RAM para `/data`, memória limitada a 128 MiB e `noeviction`. Ao atingir a capacidade, novas criações podem receber `503`; a expulsão de chaves nunca substitui o TTL. Os textos são perdidos em reinícios. Use uma instância dedicada se o Redis existente precisar de persistência.

**Quem tem o link pode ler. Não publique segredos.** Não há criptografia ponta a ponta. Uma pessoa pode guardar uma cópia antes da expiração; nenhuma expiração revoga cópias, screenshots ou downloads já feitos. O contador do navegador usa a data devolvida pelo servidor, depende do relógio local e não altera a validade real. O servidor decide sempre a disponibilidade.

Não se registam corpos das publicações nem payloads de exceções Redis. Não habilite logs de corpo HTTP, APM que capture payloads ou logs de debug em produção. Snapshots do host/hipervisor, backups de RAM, swap, core dumps, réplicas e logs externos podem reter dados fora do controlo da aplicação: desative-os ou estabeleça políticas próprias. A ausência de persistência no Redis não promete eliminação física segura nem remove cópias externas.

## Estrutura e integração noutra aplicação .NET 8

```text
src/TempBin/
  Models/       # Publicação, pedidos e estados
  Services/     # Validação, geração de slug e coordenação
                # Listener netcat e impressão HMAC do IP
  Storage/      # Contrato e armazenamento Redis com Lua
  Endpoints/    # Minimal APIs, HTML, raw e download
  Web/          # HTML com codificação segura
  wwwroot/      # CSS e JavaScript locais
  Program.cs    # DI, segurança, limites e rotas
tests/TempBin.Tests/
  ServiceTests.cs  # Slugs, colisões, concorrência e relógio simulado
  ApiTests.cs      # Integração ASP.NET Core via WebApplicationFactory
  RedisTests.cs    # Scripts reais, TTL, atomicidade e marcadores
  TerminalTests.cs # Sockets TCP reais: EOF, inatividade, UTF-8 e limites
  TermbinApiTests.cs # curl, remoção, janela de 10 minutos e proxies
```

Para integrar, copie o módulo e os recursos estáticos; adicione `StackExchange.Redis`, registe `TempBinOptions`, `NetcatOptions`, `ClientIdentity`, `IConnectionMultiplexer`, `IPasteStore`, `ISlugGenerator` e `PasteService` como no `Program.cs`, configure os middlewares e invoque `app.MapTempBin()`. Registe `NetcatServer` como hosted service se quiser suporte TCP. Mantenha ficheiros estáticos antes de `UseRouting`, seguido de `UseRateLimiter`. Verifique conflitos com `/`, `/{slug}`, `/raw`, `/download`, `/delete`, `/api/v1/pastes`, `/health`, `/openapi` e a porta TCP 9999. Caso existam, adapte o prefixo dos endpoints, dos links e dos recursos, preservando as rotas da aplicação hospedeira. Reutilize a ligação Redis apenas se a política de persistência e o namespace forem compatíveis.

## Testes

```bash
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

Os testes de API usam armazenamento isolado em memória, somente no projeto de testes, e relógio controlável. Para executar também os testes dos scripts contra Redis real:

```bash
TEMPBIN_TEST_REDIS=localhost:6379 dotnet test -c Release
```

Em PowerShell, defina `$env:TEMPBIN_TEST_REDIS='localhost:6379'` antes do comando. Cada teste usa um prefixo único e remove apenas as suas chaves, sem `FLUSHDB`. O teste de expiração antecipa o timestamp ou reduz o TTL de uma chave de teste; não altera o prazo de produção.

O workflow `.github/workflows/ci.yml` executa build, testes com Redis real e smoke tests HTTP, netcat e remoção da stack Docker em Ubuntu. Consulte o resultado do workflow para confirmar a validação em Linux; adicionar o workflow não significa que já tenha sido executado.

## Referências de produto

Inspirado no fluxo de colar/guardar do [Pastebin](https://pastebin.com/) e na utilização por HTTP/terminal pedida com referência ao [CurlBin](https://curlbin.online/). Implementação e identidade visual próprias.

As funções de netcat, pipes, aliases e remoção nos primeiros 10 minutos seguem o [termbin.com](https://termbin.com/) e a documentação do [Fiche](https://github.com/solusipse/fiche). O TempBin mantém as regras próprias: 30 minutos, slugs de 8–16 caracteres, Redis sem persistência e 256 KiB. Não copia a retenção de uma semana, o armazenamento em disco ou o registo de IPs do serviço de referência. Não há estatísticas persistentes nem catálogo público de textos.
