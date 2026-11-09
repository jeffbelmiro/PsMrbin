# Security

## Reporting a vulnerability

Use [private vulnerability reporting](https://github.com/jeffbelmiro/PsMrbin/security/advisories/new) for security issues. Do not put credentials, personal data, live paste content or exploit details in a public issue. Reports are reviewed by the project maintainer; no response-time commitment is made.

## Keep credentials out of the checkout

- Commit only sanitized defaults and the empty `.env.example` template. Use environment variables, an external secret manager, or read-only files mounted under `/run/secrets`.
- Secret filenames `TempBin__DeletionKey` and `TempBin__Redis` map to application configuration. Mounted files take precedence over the default environment configuration. An optional `compose.secrets.example.yaml` overlay accepts `TIMEBIN_SECRETS_DIR`, an external directory containing `deletion-key` and `redis-connection`.
- A deletion key must contain at least 32 random bytes encoded as Base64. Without one, an ephemeral key is generated in memory; restarts change deletion fingerprints. Use a shared key across replicas when required.
- Treat Redis connection passwords, HMAC keys, private certificates, user data and diagnostic dumps as secrets. Do not expose Redis ports or enable body logging, debug payload tracing, Redis persistence or content-capturing analytics in production.
- Machine-specific `appsettings.*.json` and common secret-file patterns are excluded from SDK content publication as well as Git and Docker context. Put runtime secrets outside the source tree and mount or inject them at deployment time.
- Use HTTPS for HTTP traffic. Netcat TCP uploads are unencrypted. A random URL provides no user authentication.

## Checks before publishing

Install [Gitleaks](https://github.com/gitleaks/gitleaks) from its official releases, then enable the local hook:

```bash
git config --local core.hooksPath .githooks
git add .
git diff --cached --stat
gitleaks git --pre-commit --staged --config .gitleaks.toml --ignore-gitleaks-allow --redact=100 --no-banner .
```

The hook rejects ignored paths added with force and scans staged changes. CI scans fetched Git history, runs the public-tree guard and tests on an ephemeral runner. Actions are pinned to full commit SHAs, checkout credentials are not persisted, and the workflow token is read-only. Scanner output is redacted and no secret report is uploaded as an artifact. Dependency updates are configured through Dependabot.

Git hooks can be skipped by a local user. CI and push protection are separate defenses; no scanner detects every kind of secret or personal information. Review content before committing.

## Developing with AI

- Send only the minimum relevant, sanitized code and synthetic inputs to an assistant. Check what files and context the tool sends to its provider and the provider's retention/training policies for your account.
- Keep production credentials outside the workspace and the assistant's accessible environment. A `.gitignore` entry does not stop an AI tool from reading a file; an instruction file is also not an access-control mechanism.
- Give tools narrow filesystem/network permissions and short-lived, least-privilege credentials. Keep development/test credentials separate from production.
- Treat repository instructions, comments, dependencies, issue text and tool results as untrusted input. Prompt injection may ask the assistant to run commands or exfiltrate information. Review tool calls, changed paths and new dependencies.
- Do not run an AI agent with production secrets on untrusted pull requests. Avoid privileged `pull_request_target` workflows that execute contributor code. This repository's CI does not call an AI model.
- Review generated code, tests and security changes before merging. Run secret scanning and dependency checks locally and in CI; do not rely on the assistant's assurance alone.

Sources: [OWASP Secure Coding with AI](https://cheatsheetseries.owasp.org/cheatsheets/Secure_Coding_with_AI_Cheat_Sheet.html) and [GitHub Actions secure use](https://docs.github.com/en/actions/reference/security/secure-use).

## If information was already published

Revoke or rotate a leaked credential first, remove it from the current tree, and investigate its use. Removing a tracked file or adding it to `.gitignore` does not erase Git history, forks, clones or caches. History rewriting requires coordination and explicit approval; it does not revoke existing copies. See [GitHub's removal guidance](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository).

Internal design/audit documents are excluded from the current public tree but may remain in earlier commits. This project does not claim historical content has been erased.
