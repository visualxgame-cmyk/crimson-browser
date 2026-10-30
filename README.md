# Vela

Un browser Windows mic și econom de memorie, construit ca un shell propriu peste **WebView2**
(motorul Chromium livrat de Microsoft). Interfața imită Brave: tab-uri orizontale sus, omnibox
rotunjit, temă întunecată și accent portocaliu `#FB542B` — dar consumul de RAM este ținut sub
control printr-o politică agresivă de adormire/eliberare a tab-urilor.

- Fără fork de Chromium, fără Electron, fără extensii: un singur executabil WPF (~1 MB) + runtime-ul WebView2 deja prezent pe Windows 11.
- Blocare de reclame și trackere pe trei niveluri (rețea + cosmetic + playerul YouTube), cu listă inclusă de peste 76.000 de domenii și suport pentru liste proprii.
- Tab-uri din fundal înghețate și, la nevoie, aruncate complet din memorie.

---

## Cerințe

| Ce | Detaliu |
| --- | --- |
| Sistem | Windows 10 1809+ / Windows 11 |
| Runtime | [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalat pe Windows 11) |
| Build | .NET SDK 10 (proiectul țintește `net10.0-windows`) |

## Build și rulare

```powershell
# debug
dotnet build src\Vela\Vela.csproj
.\src\Vela\bin\Debug\net10.0-windows\Vela.exe

# release, self-contained în publish\
dotnet publish src\Vela\Vela.csproj -c Release -r win-x64 --self-contained false -o publish
.\publish\Vela.exe
```

Sau, fără publish: `dotnet run --project src\Vela\Vela.csproj`.

## Ce știe să facă

- **Tab-uri** — deschidere/închidere, redeschiderea ultimului tab închis (`Ctrl+Shift+T`), indicator de încărcare, favicon, titlu live, tragerea ferestrei de bara de tab-uri.
- **Omnibox** — acceptă URL sau text de căutat (căutare implicită DuckDuckGo, configurabilă în `settings.json`). `https://` este adăugat automat, iar dacă scrii doar un cuvânt merge la căutare.
- **Scut Vela** — blocare reclame/trackere per site, cu contor de cereri blocate și comutator instant (reîncarcă pagina când îl re-activezi).
- **Favorite și istoric** — salvate local, vizibile în meniu; `Ctrl+D` comută favorita pentru pagina curentă.
- **Meniu** — tab nou, fereastră nouă, favorite, istoric recent, politică de memorie, ștergere istoric, „Despre Vela” (cu statistici: reguli de blocare, cereri blocate, RAM).
- **Sesiune** — la pornire reface tab-urile deschise la ultima închidere.
- **Ferestre multiple** — toate partajează același mediu WebView2 (un singur proces de browser, un singur profil pe disc).
- **Descărcări, permisiuni, devtools** — descărcările merg în folderul `Downloads`; permisiunile (cameră, microfon, notificări) sunt întrebate o singură dată per site; `F12` deschide DevTools.
- **Ecran complet** — `F11`, ieșire cu `Esc`.

## Politica de memorie (partea importantă)

Vela pornește de la ideea că un tab uitat în fundal nu trebuie să țină un proces Chromium treaz.
Trei mecanisme lucrează împreună, verificate la fiecare 10 secunde:

1. **Adormire (suspend)** — după `SuspendAfterMinutes` minute de inactivitate (implicit **2**), un tab din fundal este înghețat cu `CoreWebView2.TrySuspendAsync()`. JavaScript-ul se oprește, procesul renderează eliberează memorie, dar pagina rămâne încărcată și revine instant la click.
2. **Eliberare (discard)** — după `DiscardAfterMinutes` minute (implicit **30**), view-ul WebView2 este distrus complet. Tab-ul rămâne în bară cu titlu și favicon; la activare, view-ul este recreat și pagina reîncărcată din URL.
3. **Plafon de tab-uri vii** — `MaxLiveTabs` (implicit **10**, minim efectiv 3) limitează câte tab-uri încărcate pot exista simultan în afara celui activ; cele mai vechi sunt eliberate primele.

În plus, când fereastra este minimizată, toate tab-urile primesc `MemoryUsageTargetLevel.Low`, ca
Chromium să poată renunța la cache-uri. Bara de stare arată în timp real RAM-ul întregului arbore de
procese (`RAM 690 MB · 8 procese`) plus starea tab-urilor (`3 taburi · 2 în repaus · 1 eliberate din memorie`).

Toate pragurile se schimbă din **Meniu → Economie de memorie** sau direct în
`%LOCALAPPDATA%\Vela\Data\settings.json` (`0` = dezactivat).

## Blocarea reclamelor

Funcționează fără extensii, pe trei niveluri:

1. **Rețea** — fiecare cerere a unui tab trece prin `WebResourceRequested` și este comparată cu lista
   încărcată la pornire. Lista mare vine din StevenBlack unified hosts (peste 76.000 de domenii de
   reclame, trackere și malware), peste care se adaugă lista curată Vela.
2. **Cosmetic** — un script injectat înainte de scripturile paginii ascunde containerele de reclame
   rămase (bannere, sloturi GPT, casete sponsorizate, feed-uri). Folosește `adoptedStyleSheets`, deci
   funcționează și pe site-uri cu CSP strict.
3. **YouTube** — scriptul elimină `adPlacements`, `adSlots` și `playerAds` din răspunsul playerului
   înainte ca playerul să le vadă: atât din `ytInitialPlayerResponse` (încărcare directă), cât și din
   cererile `/youtubei/v1/player` (navigare în interiorul site-ului). Reclamele pre-roll și mid-roll
   dispar, la fel bannerul din feed și overlay-urile din player.

Detalii:

- Lista inclusă: `src\Vela\Assets\blocklist.txt` (domenii, format hosts, `||domeniu^`, fragmente de cale)
  și `src\Vela\Assets\blocklist-big.txt` (lista extinsă).
- Filtrele cosmetice și scriptul YouTube: `src\Vela\Assets\adblock.js`.
- Liste suplimentare: orice `*.txt` în `%LOCALAPPDATA%\Vela\Filters\` (poți copia liste ABP/uBlock; regulile complexe cu regex sunt ignorate, cele pe domenii și `@@||domeniu^` funcționează).
- Excepții: `%LOCALAPPDATA%\Vela\Filters\allowlist.txt`, un domeniu pe linie.
- Cererile **first-party** (același host ca pagina) nu sunt niciodată blocate, ca să nu stricăm site-ul.
- Comutator global: `BlockAds` în `settings.json`; per site: butonul Scut din toolbar. Scutul oprit
  reîncarcă pagina fără filtre cosmetice, ca să vezi site-ul exact cum arată fără blocare.

Cererile blocate primesc un răspuns `403` gol, deci scripturile de tracking nu se încarcă deloc.

**Limite:** YouTube își schimbă des playerul; dacă într-o zi apar din nou reclame, partea de scriptlet
trebuie actualizată. Reclamele livrate first-party sau lipite direct în fluxul video nu pot fi eliminate
prin rețea — cosmeticul ascunde doar containerele, nu conținutul video.

## Datele tale

Totul stă în `%LOCALAPPDATA%\Vela\`:

```
Profile\      profilul Chromium (cookies, cache, localStorage)
Data\         settings.json, history.json, bookmarks.json, session.json
Filters\      liste de blocare suplimentare + allowlist.txt
Logs\         vela.log (rotit la 1 MB)
```

Ștergerea folderului echivalează cu o instalare curată.

## Scurtături

| Scurtătură | Acțiune |
| --- | --- |
| `Ctrl+T` / `Ctrl+W` | tab nou / închide tab |
| `Ctrl+Shift+T` | redeschide ultimul tab închis |
| `Ctrl+Tab`, `Ctrl+PageDown` / `Ctrl+PageUp` | tab următor / anterior |
| `Ctrl+1`…`Ctrl+8`, `Ctrl+9` | sari la tab-ul N / ultimul tab |
| `Ctrl+N` | fereastră nouă |
| `Ctrl+L`, `Alt+D` | focus pe omnibox |
| `Ctrl+D` | adaugă/scoate favorita |
| `Ctrl+R`, `F5` | reîncarcă |
| `Alt+←` / `Alt+→` | înapoi / înainte |
| `Ctrl+=` / `Ctrl+-` / `Ctrl+0` | zoom + / − / reset |
| `F11`, `Esc` | ecran complet |
| `F12` | DevTools |

Scurtăturile funcționează și când focusul este în pagină: acceleratorii WebView2 sunt dezactivați
(`AreBrowserAcceleratorKeysEnabled = false`), iar tastele neconsumate urcă la fereastra WPF.

## Structura proiectului

```
Vela.slnx
src\Vela\
  App.xaml(.cs)            pornire, handler global de erori
  MainWindow.xaml(.cs)     toată logica de browser: tab-uri, politică de memorie, scurtături, popup-uri
  Theme\Theme.xaml         culori + stiluri (temă întunecată, accent Brave)
  Models\                  BrowserTab (starea unui tab), ObservableObject
  Services\
    AdBlocker.cs           parsare reguli + blocare la nivel de rețea + injectarea filtrelor
    Settings.cs            settings.json (praguri de memorie, căutare, scut)
    HistoryStore.cs        istoric local (ultimele 8000 de intrări)
    BookmarksStore.cs      favorite
    SessionStore.cs        tab-urile + fereastra de la închidere
    MemoryProbe.cs         RAM pe arborele de procese (NtQueryInformationProcess)
    UrlHelper.cs           „este URL sau căutare?”
    AppPaths.cs            %LOCALAPPDATA%\Vela\*
    Log.cs                 log rotativ
  Assets\
    newtab.html            pagina de tab nou (dark, căutare, tile-uri, statistici live)
    blocklist.txt          lista de blocare inclusă (curată, comentată)
    blocklist-big.txt      lista extinsă de domenii (StevenBlack unified hosts)
    adblock.js             filtre cosmetice + eliminarea reclamelor YouTube din player
```

## Limitări cunoscute

- Un tab = un proces Chromium de render, ca în orice browser pe Chromium; câștigul de memorie vine din adormire/eliberare, nu din alt motor.
- Nu există încă sincronizare, profile multiple, gesturi de mouse sau suport pentru extensii Chrome.
- Descărcările pornesc automat în `Downloads` (fără dialog „Salvează ca”).
- Parsarea listelor ABP este intenționat simplă: regulile pe domenii și fragmentele de cale sunt aplicate, regex-urile complexe sunt sărite.
