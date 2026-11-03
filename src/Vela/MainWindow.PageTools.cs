using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Vela.Models;
using Vela.Services;

namespace Vela;

internal sealed record ReaderParagraph(string Kind, string Text);
internal sealed record ReaderArticle(string Title, string SourceUrl, string Language, List<ReaderParagraph> Paragraphs);

public partial class MainWindow
{
    private void PictureInPicture_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        _ = TogglePictureInPictureAsync();
    }

    /// <summary>
    /// Chromium only enters Picture-in-Picture from a user gesture. The host script is evaluated
    /// through DevTools with <c>userGesture: true</c>, which keeps the page's own video in PiP.
    /// </summary>
    private async Task TogglePictureInPictureAsync(BrowserTab? target = null)
    {
        var tab = target ?? _active;
        if (tab?.View?.CoreWebView2 is not { } core)
        {
            Status("Deschide o pagină cu video pentru Picture-in-Picture.", 4);
            return;
        }

        const string expression = """
            (async () => {
              try {
                if (document.pictureInPictureElement) {
                  await document.exitPictureInPicture();
                  return "exit";
                }
                const videos = Array.from(document.querySelectorAll("video"))
                  .filter(v => !v.disablePictureInPicture && v.readyState > 0);
                const video = videos.find(v => !v.paused && !v.ended) || videos[0];
                if (!video) return "none";
                await video.requestPictureInPicture();
                return "enter";
              } catch (e) { return "blocked"; }
            })()
            """;

        try
        {
            var request = JsonSerializer.Serialize(new
            {
                expression,
                userGesture = true,
                awaitPromise = true,
                returnByValue = true,
            });
            var response = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request);

            var value = "";
            using (var document = JsonDocument.Parse(response))
            {
                if (document.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("result", out var inner) &&
                    inner.TryGetProperty("value", out var text))
                {
                    value = text.GetString() ?? "";
                }
            }

            Status(value switch
            {
                "exit" => "Picture-in-Picture a fost închis.",
                "enter" => "Video redat în Picture-in-Picture.",
                "none" => "Pagina nu are un video redabil.",
                _ => "Picture-in-Picture nu este disponibil pentru acest video.",
            }, 4);
        }
        catch (Exception ex)
        {
            Log.Write("Picture-in-Picture: " + ex.Message);
            Status("Picture-in-Picture nu este disponibil pentru acest video.", 4);
        }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        try { LibraryCore.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); }
        catch (Exception ex) { Log.Write("Print page: " + ex.Message); Status("Imprimarea nu a putut fi deschisă."); }
    }
    private async void SavePdf_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        var core = _active?.View?.CoreWebView2;
        if (core is null) return;
        var dialog = new SaveFileDialog { Title = "Salvează pagina ca PDF", Filter = "Document PDF|*.pdf", FileName = "pagina.pdf", DefaultExt = ".pdf" };
        if (dialog.ShowDialog(this) != true) return;
        try { Status(await core.PrintToPdfAsync(dialog.FileName) ? "Pagina a fost salvată ca PDF." : "Motorul nu a putut genera PDF-ul."); }
        catch (Exception ex) { Log.Write("Save page PDF: " + ex.Message); Status("PDF-ul nu a putut fi salvat."); }
    }
    private async void Reader_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        try
        {
            if (_active is not { } source || SiteDataService.Origin(source.Url) is null) throw new InvalidOperationException("Modul de citire este disponibil pe pagini web.");
            var result = await LibraryCore.ExecuteScriptAsync("""
                (()=>{
                  const root=document.querySelector('article')||document.querySelector('main')||document.body;
                  if(!root)return null;
                  const clone=root.cloneNode(true);
                  clone.querySelectorAll('script,style,noscript,nav,header,footer,aside,form,button,iframe,object,embed,svg,canvas,video,audio,link,meta').forEach(e=>e.remove());
                  const tags='h1,h2,h3,h4,p,li,blockquote,pre';
                  const title=(root.querySelector('h1')?.textContent||document.title).trim().slice(0,500);
                  const paragraphs=[];let length=0;
                  for(const element of clone.querySelectorAll(tags)){
                    if(element.parentElement?.closest(tags))continue;
                    const text=(element.textContent||'').trim().slice(0,20000);
                    if(!text||(element.tagName==='H1'&&text===title))continue;
                    length+=text.length;if(length>200000)break;
                    paragraphs.push({kind:element.tagName.toLowerCase(),text});
                  }
                  return {title,sourceUrl:location.href,language:document.documentElement.lang,paragraphs};
                })()
                """);
            var article = JsonSerializer.Deserialize<ReaderArticle>(result, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (article is null || article.Paragraphs.Sum(p => p.Text.Length) < 120) throw new InvalidOperationException("Pagina nu conține suficient text pentru modul de citire.");
            var reader = CreateTabModel(AppConstants.ReaderUrl);
            reader.Reader = article;
            reader.Title = article.Title;
            await ActivateTabAsync(reader);
        }
        catch (Exception ex) { Log.Write("Reader page: " + ex.Message); Status(ex.Message); }
    }
    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        if (_active is not { } tab || SiteDataService.Origin(tab.Url) is null || !Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri)) return;
        if (uri.IsLoopback || IPAddress.TryParse(uri.Host, out _) || !uri.Host.Contains('.')) { Status("Traducerea externă este disponibilă doar pentru pagini web publice."); return; }
        if (!MessageDialog.Confirm(this, "Traducere externă",
                "Pentru traducere, adresa completă a paginii, inclusiv parametrii ei, va fi trimisă serviciului Google Translate.",
                "Continuă", "Anulează")) return;
        await AddTabAsync("https://translate.google.com/translate?sl=auto&tl=" + Uri.EscapeDataString(_settings.TranslationLanguage) + "&u=" + Uri.EscapeDataString(tab.Url));
    }
    private async void ConnectionInfo_Click(object sender, RoutedEventArgs e)
    {
        var tab = _active;
        if (tab is null) return;
        var origin = SiteDataService.Origin(tab.Url);
        if (origin is null) { MessageDialog.Inform(this, "Conexiune", tab.IsInternalPage ? "Această pagină este servită local de Vela." : "Pagină locală sau adresă fără conexiune HTTPS."); return; }
        var text = origin + "\n\n" + (tab.IsSecureConnection ? "Conexiune HTTPS validată de motor." : tab.ConnectionError ?? "Conexiunea nu este confirmată ca HTTPS validă.");
        if (tab.IsSecureConnection)
        {
            try
            {
                using var json = JsonDocument.Parse(await LibraryCore.CallDevToolsProtocolMethodAsync("Network.getCertificate", JsonSerializer.Serialize(new { origin })));
                var certificates = json.RootElement.GetProperty("tableNames");
                if (certificates.GetArrayLength() > 0)
                {
                    using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificates[0].GetString()!));
                    text += "\n\nSubiect: " + certificate.Subject + "\nEmitent: " + certificate.Issuer + "\nValabil până la: " + certificate.NotAfter.ToString("g") + "\nSHA-256: " + certificate.GetCertHashString(HashAlgorithmName.SHA256);
                }
            }
            catch (Exception ex) { Log.Write("Read connection certificate: " + ex.Message); text += "\n\nCertificatul nu este disponibil pentru această conexiune în motorul curent."; }
        }
        MessageDialog.Inform(this, "Informații conexiune", text);
    }
    private void DuplicateTab(Vela.Models.BrowserTab source)
    {
        if (source.Reader is not null)
        {
            var tab = CreateTabModel(AppConstants.ReaderUrl); tab.Reader = source.Reader; tab.Title = source.Title; _ = ActivateTabAsync(tab);
        }
        else _ = AddTabAsync(source.Url);
    }
}
