using System.Globalization;
using System.Net;
using TempBin.Localization;
using TempBin.Models;
using TempBin.Services;

namespace TempBin.Web;

public static class Html
{
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string T(string key) => E(Text.Get(key));
    private static string Shell(string body, string? title = null) => $$"""
        <!doctype html><html lang="{{Text.Locale}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="robots" content="noindex,nofollow"><meta name="description" content="{{T("description")}}">
        <title>Timebin · {{E(title ?? Text.Get("pageTitle"))}}</title><link rel="stylesheet" href="/app.css"><script src="/app.js" defer></script></head>
        <body><a class="skip-link" href="#main">{{T("skip")}}</a><div id="i18n" data-messages="{{E(Text.ClientMessages)}}" hidden></div>
        <header class="topbar"><a class="brand" href="/" aria-label="{{T("home")}}"><span class="logo" aria-hidden="true">t<span>↗</span></span>Timebin</a>
        <form id="language-form" class="language-form" method="get"><label for="language">{{T("language")}}</label><select id="language" name="lang">{{LanguageOptions()}}</select><button class="copy" type="submit">{{T("applyLanguage")}}</button></form></header>
        <main id="main">{{body}}</main><footer><span>{{T("footer")}}</span><nav aria-label="{{T("footerLinks")}}"><a href="/disclaimer">{{T("disclaimer.title")}}</a><a href="/openapi">{{T("apiDocs")}}</a></nav>
        <small class="build-info">App v{{E(BuildInfo.Version)}} · {{T("buildLabel")}} <time datetime="{{BuildInfo.BuiltAt.ToString("O", CultureInfo.InvariantCulture)}}">{{E(BuildInfo.BuiltAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))}}</time></small></footer>
        <dialog id="confirm-dialog" aria-labelledby="confirm-message"><form method="dialog"><p id="confirm-message"></p><div class="dialog-actions"><button class="copy" value="cancel" autofocus>{{T("js.cancel")}}</button><button class="primary" id="confirm-action" value="confirm">{{T("js.continue")}}</button></div></form></dialog></body></html>
        """;

    private static string LanguageOptions() => string.Join("", Text.Cultures.Select((culture, index) =>
        $"<option value=\"{culture}\"{(culture == Text.Locale ? " selected" : "")}>{E(Text.LanguageNames[index])}</option>"));

    public static string Home(string origin, NetcatOptions? netcat = null) => Shell($$"""
        <section class="intro"><h1>{{T("headline")}}</h1><p class="lead">{{T("intro")}}</p></section>
        <div class="workspace"><section class="editor card"><form id="paste-form">
        <div class="panel-heading"><label for="content">{{T("editor")}}</label><span class="muted">{{T("plainUtf8")}}</span></div>
        <textarea id="content" name="content" placeholder="{{T("placeholder")}}" spellcheck="false" required aria-describedby="editor-help byte-count"></textarea>
        <div class="editor-meta"><span id="editor-help">{{T("automaticLinks")}}</span><span id="byte-count" aria-live="polite">0 / {{PasteService.MaxBytes.ToString("N0", CultureInfo.CurrentCulture)}} {{T("js.bytes")}}</span></div>
        <div class="editor-controls"><span class="muted">{{T("adaptiveLinks")}}</span>
        <button id="save" class="primary" type="submit">{{T("save")}}</button></div>
        <p id="form-error" class="error" role="alert" hidden></p></form></section>
        <aside class="rules"><h2>{{T("rulesTitle")}}</h2><dl><dt>{{T("expiryTitle")}}</dt><dd>{{T("expiryRule")}}</dd><dt>{{T("sizeTitle")}}</dt><dd>{{T("sizeRule")}}</dd><dt>{{T("deleteRuleTitle")}}</dt><dd>{{T("deleteRule")}}</dd></dl>
        <div class="notice"><strong>{{T("publicTitle")}}</strong><p>{{T("publicWarning")}}</p></div></aside></div>
        <section class="terminal" aria-labelledby="terminal-title"><h2 id="terminal-title">{{T("terminalTitle")}}</h2><p>{{T("curlDescription")}}</p>
        <div class="command-row"><code id="curl-example">curl -X POST '{{E(origin)}}/api/v1/pastes' -H 'Content-Type: text/plain; charset=utf-8' --data-binary 'Hello, world!'</code><button type="button" data-copy="curl-example" class="copy">{{T("copy")}}</button></div>
        <p>{{T("fileHelp")}} <code>--data-binary @example.txt</code>.</p>
        <p>{{T("jsonHelp")}} <code>Content-Type: application/json</code>: <code>{"content":"Hello!"}</code></p>
        {{NetcatGuide(origin, netcat)}}</section>
        """);

    private static string NetcatGuide(string origin, NetcatOptions? netcat)
    {
        if (netcat?.Enabled != true) return "";
        var host = netcat.PublicHost ?? new Uri(origin).Host.Trim('[', ']');
        var target = $"{host} {netcat.PublicPort}";
        return $$"""
            <div class="netcat-guide"><h3>{{T("netcatTitle")}}</h3><p>{{T("netcatDescription")}}</p>
            <div class="command-row"><code id="nc-example">echo 'Hello, world!' | nc {{E(target)}}</code><button class="copy" data-copy="nc-example">{{T("copy")}}</button></div>
            <div class="terminal-notes"><p><span>{{T("file")}}</span><code>cat example.txt | nc {{E(target)}}</code></p><p><span>{{T("command")}}</span><code>ls -la | nc {{E(target)}}</code></p></div>
            <p>{{T("alias")}}</p><div class="command-row"><code id="nc-alias">alias tb='nc {{E(target)}}'</code><button class="copy" data-copy="nc-alias">{{T("copy")}}</button></div>
            <p>{{T("aliasHelp")}} <code>ls -la | tb</code>.</p>
            <p class="technical-note">{{T("tcpWarning")}} {{E(Text.Format("tcpLimits", netcat.IdleTimeoutMilliseconds / 1000.0, netcat.TotalTimeoutSeconds))}}</p></div>
            """;
    }

    public static string Result(Paste paste, string origin)
    {
        var url = $"{origin}/{paste.Slug}";
        var raw = $"{origin}/raw/{paste.Slug}";
        var api = $"{origin}/api/v1/pastes/{paste.Slug}";
        var download = $"{origin}/download/{paste.Slug}";
        var expiry = paste.ExpiresAt.ToString("O", CultureInfo.InvariantCulture);
        return Shell($$"""
            <section class="intro"><h1>{{T("ready")}}</h1><p class="lead">{{T("readyHint")}}</p></section>
            <section class="card result-card"><div class="panel-heading"><h2>{{T("shareLink")}}</h2><span id="countdown" data-expires="{{E(expiry)}}" role="timer">{{T("js.calculating")}}</span></div>
            <div class="share-main"><a id="main-url" href="{{E(url)}}">{{E(url)}}</a><button class="primary" data-copy="main-url">{{T("copyLink")}}</button></div>
            <div class="link-grid">{{Link("raw", "raw-url", raw)}}{{Link("json", "api-url", api)}}{{Link("download", "download-url", download)}}</div>
            <div class="command-row"><code id="read-curl">curl '{{E(raw)}}'</code><button class="copy" data-copy="read-curl">{{T("copyCurl")}}</button></div>
            <p class="expiry">{{T("expiresAt")}} <time id="expires-at" datetime="{{E(expiry)}}">{{E(paste.ExpiresAt.ToString("f", CultureInfo.CurrentCulture))}} UTC</time> · {{paste.SizeBytes.ToString("N0", CultureInfo.CurrentCulture)}} {{T("js.bytes")}}<br>{{T("noRenewal")}}</p></section>
            <section class="card preview-card" id="preview"><div class="panel-heading"><h2>{{T("preview")}}</h2><div class="preview-actions"><button class="copy" id="toggle-lines" aria-pressed="false">{{T("lineNumbers")}}</button><a href="{{E(download)}}">{{T("download")}}</a></div></div><pre id="paste-content">{{E(paste.Content)}}</pre></section>
            {{UsageExamples(raw)}}
            <section class="delete-section"><h2>{{T("removeTitle")}}</h2><p>{{T("removeHint")}}</p>
            <button class="copy danger" id="delete-paste" data-slug="{{E(paste.Slug)}}">{{T("delete")}}</button><p id="delete-status" role="status" hidden></p>
            <div class="command-row"><code id="delete-curl">curl -X POST '{{E(origin)}}/delete?slug={{E(paste.Slug)}}'</code><button class="copy" data-copy="delete-curl">{{T("copyCurl")}}</button></div></section>
            <div class="result-bottom"><a class="primary" href="/">{{T("newPaste")}}</a></div>
            """, Text.Get("ready"));
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private static string UsageExamples(string raw)
    {
        var fetch = $"curl --fail --silent --show-error {Quote(raw)}";
        const string endpoint = "'https://api.example.com/endpoint'";
        return $$"""
            <section class="usage-section"><h2>{{T("usage.title")}}</h2><p>{{T("usage.note")}}</p>
            {{Example("usage.file", "usage.fileHint", "save-file", $"{fetch} --output paste.txt")}}
            {{Example("usage.body", "usage.bodyHint", "post-text", $"{fetch} --output paste.txt && curl --fail -X POST {endpoint} -H 'Content-Type: text/plain; charset=utf-8' --data-binary '@paste.txt'")}}
            {{Example("usage.json", "usage.jsonHint", "post-json", $"{fetch} --output payload.json && jq -e -s 'length == 1' payload.json > /dev/null && curl --fail -X POST {endpoint} -H 'Content-Type: application/json' --data-binary '@payload.json'")}}
            {{Example("usage.field", "usage.fieldHint", "post-field", $"{fetch} --output paste.txt && jq -n --rawfile content paste.txt '{{content: $content}}' > payload.json && curl --fail -X POST {endpoint} -H 'Content-Type: application/json' --data-binary '@payload.json'")}}
            </section>
            """;
    }
    private static string Example(string title, string hint, string id, string command) => $"<article class=\"usage-example\"><h3>{T(title)}</h3><p>{T(hint)}</p><div class=\"command-row\"><code id=\"{id}\">{E(command)}</code><button class=\"copy\" data-copy=\"{id}\" aria-label=\"{T("copy")} {T(title)}\">{T("copy")}</button></div></article>";

    public static string Disclaimer() => Shell($$"""
        <section class="intro"><h1>{{T("disclaimer.title")}}</h1><p class="lead">{{T("disclaimer.intro")}}</p></section>
        <article class="legal-content"><h2>{{T("disclaimer.contentTitle")}}</h2><p>{{T("disclaimer.content")}}</p>
        <h2>{{T("disclaimer.useTitle")}}</h2><p>{{T("disclaimer.use")}}</p>
        <h2>{{T("disclaimer.privacyTitle")}}</h2><p>{{T("disclaimer.privacy")}}</p>
        <h2>{{T("disclaimer.availabilityTitle")}}</h2><p>{{T("disclaimer.availability")}}</p>
        <h2>{{T("disclaimer.liabilityTitle")}}</h2><p>{{T("disclaimer.liability")}}</p><p>{{T("disclaimer.rights")}}</p>
        <h2>{{T("disclaimer.reportTitle")}}</h2><p>{{T("disclaimer.report")}} <a href="https://github.com/jeffbelmiro/PsMrbin/issues/new" rel="noreferrer">{{T("disclaimer.reportLink")}}</a>.</p>
        <p><a href="/">{{T("home")}}</a></p></article>
        """, Text.Get("disclaimer.title"));
    private static string Link(string title, string id, string url) => $"<div class=\"link-item\"><h3>{T(title)}</h3><a id=\"{id}\" href=\"{E(url)}\">{E(url)}</a><button class=\"copy\" data-copy=\"{id}\" aria-label=\"{T("copy")} {T(title)}\">{T("copy")}</button></div>";
    public static string Error(bool expired) => Shell($$"""
        <section class="intro error-page"><p class="error-code">{{(expired ? "410" : "404")}}</p><h1>{{T(expired ? "goneTitle" : "missingTitle")}}</h1><p class="lead">{{T(expired ? "goneHint" : "missingHint")}}</p><a class="primary" href="/">{{T("newPaste")}}</a></section>
        """, Text.Get(expired ? "goneTitle" : "missingTitle"));
}
