# Crimson Browser

Crimson Browser, made by VisualX Studio, păstrează interfața WPF Crimson și folosește Chromium prin CEF / CefSharp. Motorul este inclus în aplicație; WebView2 nu mai este o dependență.

## Build și verificare

Necesită Windows x64, .NET 10 SDK și Microsoft Visual C++ 2019 sau mai nou (x64). Pentru installer este necesar Inno Setup 6/7.

```powershell
dotnet build CrimsonBrowser.csproj -c Release
dotnet run --project tests/CrimsonBrowser.SmokeTests -c Release
dotnet run --project tests/CrimsonBrowser.StartupTests -c Release
dotnet run --project tests/CrimsonBrowser.CefTests -c Release
pwsh -NoProfile -ExecutionPolicy Bypass -File .\build-installer.ps1
```

Testele CEF folosesc un profil temporar și deschid o fereastră pentru verificarea motorului. Acoperă pagina locală, izolarea comenzii Setări, navigarea, căutarea nativă, zoom, popup-uri, scurtături și izolarea cookie-urilor. Nu autentifică utilizatori și nu modifică istoricul real.

Installerul și checksum-ul SHA-256 apar în `dist`. Aplicația completă este în `dist\publish\CrimsonBrowser.exe`. Fișierele motorului și directoarele lui trebuie distribuite împreună cu executabilul. Installerul include runtime-ul .NET și CEF; dacă lipsește Visual C++ x64, instalează redistributabilul Microsoft semnat. Această dependență poate cere drepturi de administrator. Aplicația se instalează per utilizator.

## Funcții

- Taburi, reordonare prin tragere, sesiune, linkuri externe în tab nou.
- Bara de adrese completează domeniile vizitate și afișează până la șase sugestii locale din istoric, fără cereri de rețea.
- Favorite, istoric, căutare în pagină, zoom, fullscreen și DevTools.
- Bara de favorite folosește lățimi adaptate titlurilor, afișează domeniul când titlul lipsește și permite derulare orizontală la depășirea spațiului disponibil.
- Fereastra și geometria sesiunii sunt afișate înaintea inițializării CEF; curățarea vechilor profiluri temporare rulează separat de pornirea interfeței.
- Setări în română/engleză, motor de căutare și fundal personalizat.
- Crimson Shields: filtrarea domeniilor cunoscute de reclame/tracking, script de filtrare și upgrade HTTPS.
- Filtrarea YouTube acoperă datele inițiale și răspunsurile JSON ale playerului, inclusiv clonele `fetch`. Mesajul de enforcement este eliminat, iar ecranul generic „A apărut o eroare” este curățat când fluxul media este disponibil. Dacă serverul reține fluxul, eroarea anti-adblock identificată prin pagina de ajutor 3037019 poate declanșa o singură reluare pentru videoclip, folosind contextul WEB/CHANNEL; erorile de cont și CAPTCHA rămân vizibile. După reluare, stratul vizual de eroare și coperta thumbnail rămase peste player sunt ascunse doar cât timp videoclipul redă efectiv cadre. Tratarea compatibilității urmărește [regulile uAssets pentru YouTube](https://github.com/uBlockOrigin/uAssets/blob/master/filters/quick-fixes.txt), fără instalarea unei extensii.
- Protecția pentru reclame reacționează la evenimentele playerului și se aplică pe loc când este comutată, inclusiv în incognito. Nu rulează verificări la 250 ms și nu schimbă forțat viteza, volumul sau timpul videoclipului. Transportul media semnat și raportarea playerului YouTube sunt păstrate pentru compatibilitate.
- Blocarea popup-urilor verifică linkul apăsat în cadrul sursă, blochează ferestrele publicitare declanșate de butoanele Play și anulează linkurile externe suprapuse peste video. Linkurile obișnuite și ferestrele Google/Microsoft de autentificare cu gest al utilizatorului sunt păstrate. Comutatorul se aplică și paginilor deja deschise; nu se folosesc temporizatoare pentru această protecție.
- Sesiunea încarcă paginile restaurate doar la selectare. Taburile ascunse sunt limitate de CEF. După trei minute, modul Low RAM poate elibera complet motorul și suprafața unui tab inactiv, restaurându-l la selectare. Taburile cu formulare modificate, media în redare, permisiuni media acordate, descărcări active sau istoric Înapoi/Înainte sunt protejate; CEF nu poate importa acel istoric într-un motor nou. Ferestrele închise își dezabonează evenimentele globale.
- Incognito folosește un context CEF fără cache pe disc, separat pentru fiecare fereastră; istoricul și permisiunile sale nu sunt persistate.
- Descărcări cu alegerea destinației și progres în interfața Crimson.
- Lista descărcărilor normale se păstrează între porniri (până la 200 de intrări); descărcările incognito nu sunt scrise pe disc. O descărcare protejează de eliberare doar tabul care o deține.
- Istoricul este salvat în fundal, cu scrieri serializate și finalizare la închidere. Erorile de stocare sunt raportate; o favorită este adăugată în listă numai după salvarea reușită.
- Actualizări prin GitHub Releases cu verificarea checksum-ului și repornire după instalare.

CEF păstrează arhitectura Chromium cu procese auxiliare. Folosim mecanismul oficial SelfHost: rendererul, GPU-ul și utilitarele rulează tot ca `CrimsonBrowser.exe`, fără WebView2 Manager sau un executabil CefSharp separat. Consumul depinde de site-uri, media și numărul de taburi și trebuie măsurat pe aceleași pagini.

## Profil și migrare

Favoritele, istoricul, setările și sesiunea folosesc în continuare `%LOCALAPPDATA%\CrimsonBrowser`. Noul profil web se află în `CefData`. Profilul WebView2 anterior rămâne intact; cookie-urile și autentificările nu sunt importate. Va fi necesară o nouă conectare pe site-uri.

„Șterge datele de navigare” elimină imediat cache-ul HTTP, cookie-urile și istoricul. La următoarea pornire resetează întregul profil CEF și permisiunile sale; bazele de date deschise nu sunt șterse în timpul funcționării. Favoritele și setările Crimson se păstrează.

Installerul include motorul CEF fixat prin SHA-256, cu AAC și H.264. Împachetarea verifică redarea efectivă a unui MP4 și testele de integrare CEF. Compilările simple prin `dotnet build` folosesc motorul standard NuGet; pentru distribuție se folosește `build-installer.ps1` din PowerShell 7.2+. Detaliile compilării native sunt în [tools/CEF-MEDIA.md](tools/CEF-MEDIA.md). Widevine/DRM nu este inclus; Netflix nu este încă suportat. Nu sunt ocolite restricțiile impuse de site-uri.

## Comenzi

| Comandă | Acțiune |
|---|---|
| Ctrl+T / Ctrl+W | Tab nou / închidere tab |
| Ctrl+Shift+T | Redeschide ultimul tab închis |
| Ctrl+L | Bara de adrese |
| Ctrl+R / F5 | Reîncarcă |
| Ctrl+F | Caută în pagină |
| Ctrl+Tab / Ctrl+Shift+Tab | Schimbă tabul |
| Ctrl+N / Ctrl+Shift+N | Fereastră normală / incognito |
| Ctrl+D / Ctrl+H | Favorit / istoric |
| F11 / Escape | Fullscreen / ieșire |
| F12 | DevTools |

## GitHub Releases

1. Crește `<Version>` din `CrimsonBrowser.csproj`.
2. Comite și împinge versiunea verificată în `main`.
3. Workflow-ul `.github/workflows/release.yml` construiește installerul și publică executabilul împreună cu `.sha256`.

O versiune publicată nu se suprascrie. Actualizările CEF se distribuie împreună cu o versiune nouă a browserului; motorul nu se actualizează prin Edge. Repository: [visualxgame-cmyk/crimson-browser](https://github.com/visualxgame-cmyk/crimson-browser).

Installerul este momentan nesemnat.
