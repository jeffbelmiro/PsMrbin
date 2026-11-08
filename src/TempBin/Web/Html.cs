using System.Globalization;
using System.Net;
using TempBin.Models;

namespace TempBin.Web;

public static class Html
{
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string Shell(string body, string title = "Texto passageiro. Partilha simples.") => $$"""
        <!doctype html><html lang="pt"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="robots" content="noindex,nofollow"><meta name="description" content="Partilhe texto durante 30 minutos. Sem conta. Pelo navegador, curl ou API.">
        <title>TempBin · {{E(title)}}</title><link rel="stylesheet" href="/app.css"><script src="/app.js" defer></script></head>
        <body><div class="ambient"></div><header class="topbar"><a class="brand" href="/" aria-label="TempBin início"><span class="logo">t<span>↗</span></span>TempBin<span class="beta">EPHEMERAL TEXT</span></a><span class="status"><i></i> Sem conta. Sem permanência.</span></header>
        <main>{{body}}</main><footer><a class="brand-small" href="/">TempBin</a><span>Feito para partilhar. Concebido para desaparecer.</span><a href="/openapi">OpenAPI ↗</a></footer></body></html>
        """;

    public static string Home(string origin) => Shell($$"""
        <section class="intro"><div><p class="eyebrow"><span class="tiny-line"></span> UMA IDEIA, UM LINK, 30 MINUTOS</p>
        <h1>O seu texto.<br><span>De passagem.</span></h1><p class="lead">Cole. Guarde. Partilhe. Um espaço temporário para texto simples,<br class="desktop"> sem registo e sem complicações.</p></div>
        <div class="time-card"><span class="clock">◷</span><strong>30<span>min</span></strong><p>É tudo o que fica.<br>Depois, desaparece.</p></div></section>
        <div class="workspace"><section class="editor card"><form id="paste-form"><div class="panel-heading"><h2><span class="dot"></span> Novo texto</h2><span class="muted">01 / ESCREVER</span></div>
        <label class="sr-only" for="content">Texto a partilhar</label><textarea id="content" name="content" placeholder="Cole o seu texto aqui…&#10;&#10;Uma nota, um snippet, uma resposta da API.&#10;O que precisa de partilhar, por pouco tempo." autofocus spellcheck="false" required aria-describedby="editor-help byte-count"></textarea>
        <div class="editor-meta"><span id="editor-help">Texto simples · UTF-8</span><span id="byte-count" aria-live="polite">0 / 262 144 bytes</span></div>
        <div class="editor-controls"><div><label for="slug-length">Comprimento do link</label><div class="select-wrap"><select id="slug-length" name="slug_length">{{string.Join("", Enumerable.Range(8, 9).Select(n => $"<option value=\"{n}\">{n} caracteres{(n == 8 ? " · padrão" : "")}</option>"))}}</select><span>8–16, letras e números</span></div></div><button id="save" class="primary" type="submit">Guardar e gerar link <span>↗</span></button></div>
        <p id="form-error" class="error" role="alert" hidden></p></form></section>
        <aside class="side"><section class="card rules"><p class="eyebrow">SIMPLES POR DEFINIÇÃO</p><h2>Partilhe o momento.<br>Não o histórico.</h2>
        <div class="rule"><span>01</span><div><h3>Expira em 30 minutos</h3><p>O prazo começa ao guardar. Ler não prolonga a validade. Sem edição nem recuperação.</p></div></div>
        <div class="rule"><span>02</span><div><h3>Um link, vários caminhos</h3><p>Aceda pela página, texto puro, JSON ou download .txt. Funciona com curl e API REST.</p></div></div>
        <div class="rule"><span>03</span><div><h3>Até 256 KiB por texto</h3><p>262 144 bytes em UTF-8. Sem login, email ou cookies de autenticação.</p></div></div>
        <div class="notice"><span>!</span><p><strong>Um link não é um segredo.</strong> Quem o tiver pode ler. Não publique palavras-passe, tokens ou informação sensível.</p></div></section></aside></div>
        <section class="terminal card"><div class="terminal-title"><div><span class="terminal-icon">&gt;_</span><h2>Também fala terminal.</h2></div><span class="muted">SEM SAIR DO SEU FLUXO</span></div>
        <p>Envie texto ou um ficheiro. A API devolve todos os links e a hora exata de expiração.</p>
        <div class="command-row"><code id="curl-example">curl -X POST '{{E(origin)}}/api/v1/pastes' -H 'Content-Type: text/plain; charset=utf-8' --data-binary 'Olá, mundo!'</code><button type="button" data-copy="curl-example" class="copy">Copiar</button></div>
        <div class="terminal-notes"><span><b>Ficheiro</b> Substitua o texto por <code>--data-binary @exemplo.txt</code></span><span><b>JSON</b> <code>{"content":"Olá!","slug_length":8}</code> com <code>Content-Type: application/json</code></span></div></section>
        """);

    public static string Result(Paste paste, string origin)
    {
        var url = $"{origin}/{paste.Slug}";
        var raw = $"{origin}/raw/{paste.Slug}";
        var api = $"{origin}/api/v1/pastes/{paste.Slug}";
        var download = $"{origin}/download/{paste.Slug}";
        var expiry = paste.ExpiresAt.ToString("O", CultureInfo.InvariantCulture);
        return Shell($$"""
            <section class="result-intro"><p class="eyebrow">PRONTO PARA PARTILHAR</p><h1>Um texto.<br><span>Vários caminhos.</span></h1><p class="lead">O link está pronto. A contagem já começou.</p></section>
            <section class="card result-card"><div class="panel-heading"><h2><span class="dot"></span> O seu link</h2><span id="countdown" data-expires="{{E(expiry)}}" role="timer">A calcular…</span></div>
            <div class="share-main"><a id="main-url" href="{{E(url)}}">{{E(url)}}</a><button class="primary" data-copy="main-url">Copiar link ↗</button></div>
            <div class="link-grid">{{Link("Texto puro", "raw-url", raw)}}{{Link("API JSON", "api-url", api)}}{{Link("Download .txt", "download-url", download)}}</div>
            <div class="command-row"><code id="read-curl">curl '{{E(raw)}}'</code><button class="copy" data-copy="read-curl">Copiar curl</button></div>
            <p class="expiry">Expira em <time id="expires-at" datetime="{{E(expiry)}}">{{E(expiry)}}</time> · {{paste.SizeBytes.ToString("N0", CultureInfo.GetCultureInfo("pt-PT"))}} bytes · Sem renovação</p></section>
            <section class="card preview-card" id="preview"><div class="panel-heading"><h2>Pré-visualização</h2><a href="{{E(download)}}">Descarregar .txt ↓</a></div><pre id="paste-content">{{E(paste.Content)}}</pre></section>
            <div class="result-bottom"><p>Quem tem o link pode ler. Após a expiração, o texto não pode ser recuperado.</p><a class="primary" href="/">Criar outro texto +</a></div>
            """, "Texto guardado");
    }
    private static string Link(string title, string id, string url) => $"<div class=\"link-item\"><span>{E(title)}</span><a id=\"{id}\" href=\"{E(url)}\">{E(url)}</a><button class=\"copy\" data-copy=\"{id}\" aria-label=\"Copiar {E(title)}\">Copiar</button></div>";
    public static string Error(bool expired) => Shell($"<section class=\"result-intro\"><p class=\"eyebrow\">{(expired ? "410 · PRAZO TERMINADO" : "404 · LINK DESCONHECIDO")}</p><h1>{(expired ? "Este texto<br><span>já passou.</span>" : "Nada por aqui.")}</h1><p class=\"lead\">{(expired ? "Os 30 minutos terminaram. O conteúdo foi eliminado e não pode ser recuperado." : "O link não existe, está incorreto ou o seu marcador de expiração já foi eliminado.")}</p><a class=\"primary\" href=\"/\">Criar um texto ↗</a></section>");
}
