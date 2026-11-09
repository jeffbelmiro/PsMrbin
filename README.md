# Timebin

**Share text for 30 minutes. From your browser or your terminal.**

A log your teammate needs to see. A JSON document for the next API call. A note that only matters right now. Timebin turns plain text into a shareable link, without asking anyone to create an account.

Paste, create a link, and get back to your work. The text expires 30 minutes after it is saved.

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

New slugs contain **12–16 readable characters**, generated cryptographically. Timebin chooses the length from recent publishing activity and creation volume, using counters shared through Redis. Collision checks are atomic, existing content is never replaced, and reserved slugs are not immediately reused. Older 8–16 character links remain readable until they expire.

Content expires after **1,800 seconds**. A marker without paste content remains for up to 24 hours from creation to identify expired/deleted links and delay reuse. Downloads, screenshots and copies made by recipients remain outside Timebin's control. Expired text cannot be recovered through the service.

## Explore the project

| Resource | What you will find |
| --- | --- |
| [Operator guide](docs/operations.md) | Setup, environment variables, endpoints, limits, Redis and proxy configuration |
| [Slug policy](docs/slug-policy.md) | Activity estimates, length thresholds, occupancy budget and limitations |
| [Design direction](DESIGN.md) | Interface and copy decisions informed by the anti-slop reference |
| [Interface review](docs/interface-review.md) | Recorded localization, accessibility and browser checks |
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

Have a bug or an improvement in mind? [Open a project issue](https://github.com/jeffbelmiro/PsMrbin/issues). Include the behavior you expected and steps to reproduce it. Do not include sensitive paste content.
