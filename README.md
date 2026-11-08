# PsMrbin · TempBin

Partilha efémera de texto simples em **ASP.NET Core 8 + Redis**, preparada para Docker em Linux. Sem login: cole texto, guarde e partilhe um link. O conteúdo expira **1 800 segundos após a criação**. Leitura, download e consulta de metadados nunca renovam o prazo.

## Início rápido com Docker

Requisitos: Docker Engine e Docker Compose v2 num servidor Linux.

```bash
cp .env.example .env
docker compose up -d --build --wait
```

Abra `http://localhost:8080`. O Redis fica apenas na rede interna, sem porta publicada. A aplicação escuta por padrão em `127.0.0.1:8080`, para utilização local ou atrás de um reverse proxy HTTPS.

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
| `PUBLIC_ORIGIN` | `http://localhost:8080` | Variável do Compose que preenche `TempBin__PublicOrigin` |
| `HTTP_BIND` | `127.0.0.1` | Interface de publicação da porta Docker |
| `HTTP_PORT` | `8080` | Porta externa Docker |
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

Erros: `400` para JSON/UTF-8/valores inválidos ou texto vazio, `413` para excesso de tamanho, `415` para tipo de conteúdo não suportado, `404` para slug desconhecido, `410` para texto expirado com marcador ainda presente, `429` para limite de pedidos e `503` para armazenamento indisponível. As respostas de erro têm a propriedade `error`.

## Slugs e expiração

- Comprimento entre **8 e 16**, padrão **8**. A instrução da mensagem principal prevalece sobre o intervalo 4–16 do anexo: 4 a 7 são rejeitados tanto na API como na interface.
- Alfabeto: `abcdefghjkmnpqrstuvwxyz23456789`, sem símbolos nem caracteres visualmente ambíguos.
- Aleatoriedade criptográfica com `RandomNumberGenerator`, sem IDs sequenciais ou timestamps.
- Um script Lua no Redis verifica colisões e cria o conteúdo e o marcador numa operação atómica. Nunca substitui conteúdo existente; o serviço tenta novamente em caso de colisão.
- A hora autoritativa é `TIME` do Redis. `created_at` e `expires_at` têm precisão de milissegundos e diferem exatamente 30 minutos. Todas as leituras verificam a expiração no mesmo relógio. Mantenha o relógio do servidor sincronizado.
- A chave de conteúdo tem TTL fixo de 1 800 segundos. O Redis invalida logicamente a chave ao atingir o prazo e liberta a memória pela sua estratégia de expiração; não se promete sobrescrita física imediata da RAM.
- Um marcador sem texto e com TTL de **24 horas desde a criação** permite devolver `410` e impede a reutilização imediata. Após esse prazo o resultado passa a `404` e o slug pode voltar a ser sorteado.
- Não há edição, extensão do prazo, lista pública, pesquisa ou recuperação.

## Segurança e limites operacionais

Máximo de **256 KiB (262 144 bytes UTF-8)** por publicação. O corpo JSON tem teto de 1 600 000 bytes para acomodar escaping; o texto descodificado continua limitado a 256 KiB. Corpos chunked também são limitados. Texto vazio ou só com espaços é rejeitado.

O HTML é codificado antes de aparecer na pré-visualização. O frontend usa `textContent`; conteúdo publicado não é interpretado como HTML. CSP restritiva, `nosniff`, `no-store`, `Referrer-Policy: no-referrer`, `X-Robots-Tag` e `robots.txt` reduzem execução, cache e indexação. Não há dependências de fontes externas, analytics, autenticação ou cookies de sessão.

Limites por IP e minuto: **20 criações**, **120 leituras**, incluindo slugs inexistentes. Limite global por instância: **3 000 pedidos/minuto** e **64 pedidos concorrentes** que chegam ao middleware, além de 256 ligações Kestrel. `429` inclui `Retry-After`. Recursos estáticos são servidos antes do limitador. Estes limites reduzem enumeração e abuso, sem garantir resistência absoluta a ataques distribuídos.

Os limites são locais a cada instância. Atrás de um proxy, a configuração padrão usa o IP do proxy, partilhando o limite entre visitantes. Não se confia automaticamente em `X-Forwarded-For`. Para obter o IP do cliente, configure `ForwardedHeadersOptions` com `KnownProxies`/`KnownNetworks` explícitos e coloque `UseForwardedHeaders` antes do rate limiter. Em múltiplas réplicas, aplique limites agregados no proxy/gateway.

No Compose, Redis usa `--save '' --appendonly no`, RAM para `/data`, memória limitada a 128 MiB e `noeviction`. Ao atingir a capacidade, novas criações podem receber `503`; a expulsão de chaves nunca substitui o TTL. Os textos são perdidos em reinícios. Use uma instância dedicada se o Redis existente precisar de persistência.

**Quem tem o link pode ler. Não publique segredos.** Não há criptografia ponta a ponta. Uma pessoa pode guardar uma cópia antes da expiração; nenhuma expiração revoga cópias, screenshots ou downloads já feitos. O contador do navegador usa a data devolvida pelo servidor, depende do relógio local e não altera a validade real. O servidor decide sempre a disponibilidade.

Não se registam corpos das publicações nem payloads de exceções Redis. Não habilite logs de corpo HTTP, APM que capture payloads ou logs de debug em produção. Snapshots do host/hipervisor, backups de RAM, swap, core dumps, réplicas e logs externos podem reter dados fora do controlo da aplicação: desative-os ou estabeleça políticas próprias. A ausência de persistência no Redis não promete eliminação física segura nem remove cópias externas.

## Estrutura e integração noutra aplicação .NET 8

```text
src/TempBin/
  Models/       # Publicação, pedidos e estados
  Services/     # Validação, geração de slug e coordenação
  Storage/      # Contrato e armazenamento Redis com Lua
  Endpoints/    # Minimal APIs, HTML, raw e download
  Web/          # HTML com codificação segura
  wwwroot/      # CSS e JavaScript locais
  Program.cs    # DI, segurança, limites e rotas
tests/TempBin.Tests/
  ServiceTests.cs  # Slugs, colisões, concorrência e relógio simulado
  ApiTests.cs      # Integração ASP.NET Core via WebApplicationFactory
  RedisTests.cs    # Scripts reais, TTL, atomicidade e marcadores
```

Para integrar, copie o módulo e os recursos estáticos; adicione `StackExchange.Redis`, registe `TempBinOptions`, `IConnectionMultiplexer`, `IPasteStore`, `ISlugGenerator` e `PasteService` como no `Program.cs`, configure os middlewares e invoque `app.MapTempBin()`. Mantenha ficheiros estáticos antes de `UseRouting`, seguido de `UseRateLimiter`. Verifique conflitos com `/`, `/{slug}`, `/raw`, `/download`, `/api/v1/pastes`, `/health` e `/openapi`. Caso existam, adapte o prefixo dos endpoints, dos links e dos recursos, preservando as rotas da aplicação hospedeira. Reutilize a ligação Redis apenas se a política de persistência e o namespace forem compatíveis.

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

O workflow `.github/workflows/ci.yml` executa build, testes com Redis real e smoke tests da stack Docker em Ubuntu. Consulte o resultado do workflow para confirmar a validação em Linux; adicionar o workflow não significa que já tenha sido executado.

## Referências de produto

Inspirado no fluxo de colar/guardar do [Pastebin](https://pastebin.com/) e na utilização por HTTP/terminal pedida com referência ao [CurlBin](https://curlbin.online/). Implementação e identidade visual próprias.
