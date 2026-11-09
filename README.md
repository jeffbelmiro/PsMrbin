# Timebin

**Share text. Set the time. From your browser or your terminal.**

A log your teammate needs to see. A JSON document for the next API call. A note that only matters right now. Timebin turns plain text into a shareable link, without asking anyone to create an account.

Paste, create a link, and get back to your work. The text expires after 30 minutes by default. Choose 8, 12 or 24 hours before saving when you need more time. Reads never extend the deadline.

## One paste, ready for your workflow

- **Share in the browser.** Open the editor, paste up to 256 KiB of UTF-8 text, and copy the resulting link.
- **Work from the terminal.** Send a file or command output with curl or netcat. Open the returned raw URL with curl.
- **Use the text elsewhere.** The result page includes ready-to-copy examples for saving a local file, posting a request body, validating a JSON payload, and wrapping text in a JSON field.
- **Choose your language.** English comes first, with Spanish, Brazilian Portuguese, French and Simplified Chinese available from the header.
- **Remove a mistake early.** The IP address that created a paste can delete it within the first 10 minutes.
- **Run it on your infrastructure.** ASP.NET Core 8, a Redis instance and Docker Compose for Linux. Redis stores paste content in memory with persistence disabled in the supplied stack.

Each paste has an HTML page, raw text URL, JSON API response and `.txt` download. Read access never extends its lifetime. Version and UTC build date appear in the footer so you can identify the build you are running.

## Start in minutes

You need Docker Engine and Docker Compose v2.

```bash
git clone https://github.com/jeffbelmiro/PsMrbin.git
cd PsMrbin
cp .env.example .env
docker compose up -d --build --wait
```

Open **http://localhost:8080**. The supplied configuration publishes HTTP and netcat on loopback only. Set `PUBLIC_ORIGIN` and configure your HTTPS proxy before serving a public domain. Redis has no published port.

### Browser, curl or netcat

Create a paste:

```bash
curl --fail http://localhost:8080/api/v1/pastes \
  -H 'Content-Type: text/plain; charset=utf-8' \
  --data-binary 'A note for the next 30 minutes.'
```

Share a file:

```bash
curl --fail http://localhost:8080/api/v1/pastes \
  -H 'Content-Type: text/plain; charset=utf-8' \
  --data-binary @example.txt
```

Pipe command output to netcat:

```bash
ls -la | nc localhost 9999
```

Netcat returns a single raw-text URL. TCP uploads are unencrypted; use the HTTPS API when you need transport encryption.

### Take the text into an API request

Use the actual `raw_url` returned when you create a paste. To send arbitrary text as a JSON string without breaking quotes or line endings:

```bash
curl --fail --silent --show-error 'YOUR_RAW_URL' --output paste.txt &&
jq -n --rawfile content paste.txt '{content: $content}' > payload.json &&
curl --fail -X POST 'https://api.example.com/endpoint' \
  -H 'Content-Type: application/json' \
  --data-binary '@payload.json'
```

This example requires jq. Replace the sample API endpoint and add its required authentication headers. Existing local files with the sample names are overwritten. Timebin's result page generates these examples with your paste's real URL.

## Short-lived by design

**Anyone with a link can read its text.** Keep passwords, credentials and personal information out of pastes. Random links make discovery harder; they do not provide authentication or end-to-end encryption.

New slugs contain **12 to 16 readable characters**, generated cryptographically. Timebin chooses the length from recent publishing activity and creation volume, using counters shared through Redis. Collision checks are atomic, existing content is never replaced, and reserved slugs are not immediately reused. Older 8 to 16 character links remain readable until they expire.

Content expires after **30 minutes by default**, or **8, 12 or 24 hours** when selected at creation. A marker without paste content remains until the later of 24 hours from creation or one hour after the selected expiry, to identify expired/deleted links and delay reuse. Downloads, screenshots and copies made by recipients remain outside Timebin's control. Expired text cannot be recovered through the service.

## Explore the project

| Resource | What you will find |
| --- | --- |
| [Security](SECURITY.md) | Private vulnerability reporting, credentials, secret checks and safe AI-assisted development |
| [Deployment configuration](compose.yaml) | HTTP/TCP ports, memory limits, health checks and Redis isolation |
| [Secret-file overlay](compose.secrets.example.yaml) | Optional read-only runtime secrets from outside the checkout |
| `/openapi` on your instance | Machine-readable API specification |
| `/disclaimer` on your instance | Content responsibility, acceptable use and service limitations |

## Built to be checked

The repository includes xUnit integration tests for the web API, Redis scripts, netcat uploads, expiry, rate limits, localization and adaptive slugs. GitHub Actions builds the project and exercises the Docker stack on Linux. See the [current workflow results](https://github.com/jeffbelmiro/PsMrbin/actions).

```bash
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

Set `TEMPBIN_TEST_REDIS` to a Redis connection string to include storage integration tests. The CI workflow supplies Redis automatically.

## Fork and configure

The public tree includes application source, tests, build/deployment configuration, CI and essential usage/security information. Internal planning, design, AI sessions and audit documents are local-only. Ignoring a file does not remove it from past Git commits or keep it out of an AI tool's accessible context.

| Setting | Default / purpose |
| --- | --- |
| `PUBLIC_ORIGIN` | `http://localhost:8080`; set your public HTTPS origin for shared links |
| `HTTP_BIND`, `HTTP_PORT` | `127.0.0.1`, `8080`; HTTP listener publication |
| `TCP_BIND`, `TCP_PORT` | `127.0.0.1`, `9999`; netcat listener publication |
| `NETCAT_ENABLED` | `true` in Compose; disable if you only need HTTP |
| `TempBin__Redis` | Redis connection string; provided internally by the base Compose stack |
| `DELETION_KEY` | Optional Base64 key of at least 32 random bytes; use an external secret store or the secret-file overlay for deployments |
| `TempBin__KnownProxies__0` | Trusted proxy IP; add `__1`, etc. if needed. Untrusted forwarding headers are ignored |

Keep secrets outside the checkout. Files mounted under `/run/secrets` with names such as `TempBin__DeletionKey` and `TempBin__Redis` override default settings. See [SECURITY.md](SECURITY.md) before configuring production credentials.

After installing Gitleaks from its official releases, enable the local commit guard with `git config --local core.hooksPath .githooks`. CI also checks the public tree and scans history with redacted output. Source, tests and runtime localization resources are retained so a fork can build and run without internal documents.

The app uses an English first-visit default and supports `?lang=en`, `es`, `pt-BR`, `fr` or `zh-CN`. API errors also accept `Accept-Language`. Text and API property names are never translated. New link lengths start at 12 and increase at 100, 1,000, 10,000 and 100,000 recent publishing identities, with a second sizing budget based on recent creation volume; older valid links continue to work.

Have a bug or an improvement in mind? [Open a project issue](https://github.com/jeffbelmiro/PsMrbin/issues). Include the behavior you expected and steps to reproduce it. Do not include sensitive paste content.

### Choose your sharing window

The browser defaults to **30 minutes** on every new paste. Select **8h**, **12h** or **24h** before saving. The deadline is fixed at creation and applies to every output.

JSON clients can send `{"content":"Hello!","ttlSeconds":28800}` (allowed seconds: `1800`, `28800`, `43200`, `86400`). Plain-text clients can POST to `/api/v1/pastes?ttl_seconds=28800`. Omission keeps 30 minutes; unsupported values return 400. The TCP/netcat interface keeps the 30-minute default.

### Repository layout

- `src/TempBin/`: ASP.NET application; Api (routes), Components and wwwroot (UI), Guards (security), Models, Utils (services) and Storage.
- `tests/TempBin.Tests/`: unit, API and security tests; Redis integration tests run in CI.
- `.github/workflows/`: security scanning, tests and container validation.
- `.githooks/` and `ai-security-gate.json`: local public-tree and secret checks.
- `.ai/` (ignored): local rules, design references, skills, agent instructions and workflows. Never store credentials here or include these files in builds.

Real secrets belong outside the checkout and outside AI context, mounted at runtime via the example secrets overlay. Ignoring a directory prevents Git tracking; it does not restrict an AI tool's filesystem access. See SECURITY.md.

`config/security-policies/policies.json` maps the enforced public security policies to their implementation. It contains no credentials. The DevSecOps workflow runs secret scanning, the AI policy lint, .NET static analyzers, security tests and Docker HTTP smoke checks. Local agent files are reference instructions only; they do not start autonomous processes.

For local commits, install Python 3 and Gitleaks, enable `git config core.hooksPath .githooks`, and set `git config timebin.pythonPath /absolute/path/to/python3` if Python is not on PATH. The pre-commit hook validates the public-tree rules, `ai-security-gate.json` and staged secrets. CI repeats the checks independently.
