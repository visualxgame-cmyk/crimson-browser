# Vela

Un browser Windows mic și econom de memorie, construit ca un shell propriu peste **WebView2**
(motorul Chromium livrat de Microsoft). Interfața imită Brave: tab-uri orizontale sus, omnibox
rotunjit, temă întunecată și accent portocaliu `#FB542B` — dar consumul de RAM este ținut sub
control printr-o politică agresivă de adormire/eliberare a tab-urilor.

- Fără fork de Chromium și fără Electron: interfață WPF peste WebView2. Extensiile sunt opționale și dezactivate implicit. Pachetul self-contained include .NET; motorul WebView2 este partajat cu Windows.
- Blocare de reclame și trackere pe trei niveluri (rețea + cosmetic + playerul YouTube), cu listă inclusă de peste 76.000 de domenii și suport pentru liste proprii.
- Tab-uri din fundal adormite după 1 minut, eliberate complet după 15 minute, cu maximum 6 tab-uri încărcate simultan; cele care redau audio sunt protejate.

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

# release pentru sistemul cu .NET Desktop Runtime instalat
dotnet publish src\Vela\Vela.csproj -c Release -r win-x64 --self-contained false -o publish
.\publish\Vela.exe
```

Sau, fără publish: `dotnet run --project src\Vela\Vela.csproj`.

## Ce știe să facă

- **Tab-uri** — deschidere/închidere, redeschiderea ultimului tab închis (`Ctrl+Shift+T`), indicator de încărcare, favicon, titlu live, tragerea ferestrei de bara de tab-uri.
- **Omnibox** — acceptă URL sau text de căutat (căutare implicită DuckDuckGo, configurabilă în `settings.json`). `https://` este adăugat automat, iar dacă scrii doar un cuvânt merge la căutare.
- **Scut Vela** — blocare reclame/trackere per site, cu contor de cereri blocate și comutator instant (reîncarcă pagina când îl re-activezi).
- **Favorite și istoric** — salvate local, vizibile în meniu; `Ctrl+D` comută favorita pentru pagina curentă.
- **Meniu** — tab nou, fereastră nouă, setări, favorite, istoric recent, politică de memorie, ștergere istoric, „Despre Vela” (cu statistici: reguli de blocare, cereri blocate, RAM).
- **Sesiune** — la pornire reface tab-urile deschise la ultima închidere.
- **Ferestre multiple** — ferestrele aceluiași profil partajează mediul WebView2. Profilurile distincte au procese și directoare de date separate.
- **Descărcări, permisiuni, devtools** — descărcările merg în folderul `Downloads`; permisiunile (cameră, microfon, notificări) apar într-o bară nemodală. „Permite” și „Blochează” se salvează pe site; „Nu acum” refuză temporar. În privat, alegerea rămâne doar în fereastra respectivă. `F12` deschide DevTools.
- **Ecran complet** — YouTube și alte pagini care cer fullscreen extind conținutul pe tot monitorul, inclusiv peste bara de activități; `Esc` iese din fullscreen-ul videoclipului, iar `F11` controlează fullscreen-ul browserului.
- **Popupuri și extensii** — Vela limitează exploziile de popupuri automate, păstrând ferestrele normale de autentificare. Înainte de instalarea unei extensii, afișează permisiunile și site-urile declarate în manifest.
- **Ecran complet** — `F11`, ieșire cu `Esc`.

## Navigare și recuperare locală

- **Ctrl+F** deschide căutarea nativă în pagină, cu număr de rezultate și navigare între ele.
- **Bara de adrese** propune cel mult opt rezultate din favorite și istoric, fără servicii de sugestii sau cereri de rețea. Folosește săgețile și Enter pentru selectare; Esc anulează.
- **Ctrl+J** sau butonul Descărcări deschide panoul nativ WebView2: progres, pauză/reluare, anulare și acces la fișiere. Descărcările în curs sau reluabile protejează tabul de eliberarea automată; închiderea lui cere confirmare.
- **Meniu → Economie de memorie → Nu adormi acest site** salvează o excepție pentru gazda exactă. Excepția se aplică adormirii și eliberării automate, inclusiv la minimizare. Site-urile protejate apar dedesubt și pot fi eliminate individual.
- **New Tab → Adaugă site / Personalizează** permite până la 24 de scurtături: adăugare, editare, ștergere și reordonare cu săgeți. Logo-urile implicite sunt incluse local; pentru alte site-uri faviconul este salvat după prima vizită și reutilizat fără rețea.
- **Sesiunea** se salvează la schimbarea taburilor, adreselor și titlurilor, cu o întârziere de grupare de 500 ms, înlocuire atomică și copie `.bak`. După o oprire neașteptată se refac adresele și titlurile; taburile din fundal rămân neîncărcate. Datele nesalvate din formulare nu sunt incluse. Ferestrele deschise contribuie la aceeași sesiune și taburile recuperate sunt grupate într-o singură fereastră.

Scurtăturile și excepțiile sunt salvate în `Data/shortcuts.json` și `Data/protected-sites.json`.
Logo-urile personalizate sunt PNG-uri de 32 px în `ShortcutIcons/`. Salvarea sesiunii și căutarea
sugestiilor folosesc temporizatoare pornite doar de schimbări; nu adaugă polling permanent.

Verificările de integrare rulează într-un profil temporar, separat de datele utilizatorului:

```powershell
dotnet run --project tests\Vela.SmokeTests\Vela.SmokeTests.csproj -c Release
```

Acestea verifică inclusiv WebView2 real, logo-uri, editorul New Tab, sugestii, căutare,
descărcări locale, excepții, ferestre multiple și restaurare după oprirea forțată a procesului.

## Setări

Se deschid din **Meniu → Setări** sau cu `Ctrl+,`. Fereastra are bară laterală și cinci pagini;
orice modificare se vede imediat în browser ca previzualizare, iar **Salvează** o scrie în
`settings.json`. **Anulează** (sau închiderea ferestrei) readuce tema și fundalul la starea de dinainte.

| Pagină | Ce conține |
| --- | --- |
| **Aspect** | șase teme, accent personalizat |
| **Tab nou** | fundal: temă, culoare simplă sau imagine |
| **Memorie** | adormire, eliberare, plafon de tab-uri vii |
| **Confidențialitate** | scutul de reclame, restaurarea sesiunii, folderul `Filters` |
| **Navigare** | pagina de start și motorul de căutare |

### Teme

Tema schimbă tot browserul: bara de tab-uri, toolbar-ul, omnibox-ul, meniurile, popup-urile,
bara de stare și pagina de tab nou. Sunt șase palete gata făcute:

| Temă | Accent | Descriere |
| --- | --- | --- |
| **Vela** | `#FB542B` | portocaliul Brave pe grafit (implicit) |
| **Ocean** | `#3B82F6` | albastru rece, liniștit |
| **Pădure** | `#34D399` | verde profund, odihnitor |
| **Ametist** | `#A78BFA` | violet discret, seara |
| **Trandafir** | `#F472B6` | roz cald, fără zgomot |
| **Grafit** | `#6B7280` | monocrom, fără distrageri |

Peste temă poți pune un **accent personalizat** (`#RRGGBB`): un rând de nuanțe gata făcute plus
câmp hex. Accentul se aplică pe butoane, focus, indicatorul de tab activ, scut și pe pagina de tab nou.
„Folosește tema” șterge accentul personalizat.

Tehnic: `ThemeCatalog` înlocuiește perechile de `SolidColorBrush` înghețate din `Application.Resources`,
iar toate referințele din XAML sunt `DynamicResource`, deci schimbarea se face instant, fără repornire.
Pagina de tab nou primește tema prin parametri în URL (primul cadru e deja corect) și prin
`window.velaApplyTheme(...)` pentru actualizările live.

### Fundalul paginii de tab nou

Trei moduri:

- **Tema** — fundalul paletei active (implicit).
- **Culoare simplă** — orice nuanță, din cele opt presetate sau scrisă ca hex.
- **Imagine** — JPG, PNG, WebP sau BMP. Fișierul este copiat în `%LOCALAPPDATA%\Vela\Backgrounds\`,
  apoi servit local prin gazda virtuală `vela.bg` (fără cereri în rețea). Imaginea este afișată
  `cover`, cu un strat întunecat ca textul să rămână lizibil.

Fereastra de setări are o previzualizare a fundalului, iar „Elimină” șterge imaginea copiată.

## Politica de memorie (partea importantă)

Vela pornește de la ideea că un tab uitat în fundal nu trebuie să țină un proces Chromium treaz.
Trei mecanisme lucrează împreună, verificate la fiecare 10 secunde:

1. **Adormire (suspend)** — după `SuspendAfterMinutes` minute de inactivitate (implicit **1**), un tab din fundal este înghețat cu `CoreWebView2.TrySuspendAsync()`. JavaScript-ul se oprește, procesul renderează eliberează memorie, dar pagina rămâne încărcată și revine instant la click.
2. **Eliberare (discard)** — după `DiscardAfterMinutes` minute (implicit **15**), view-ul WebView2 este distrus complet. Tab-ul rămâne în bară cu titlu și favicon; la activare, view-ul este recreat și pagina reîncărcată din URL.
3. **Plafon de tab-uri încărcate** — `MaxLiveTabs` (implicit **6**, minim efectiv 3) limitează câte tab-uri încărcate pot exista simultan în afara celui activ; cele mai vechi sunt eliberate primele. Limita este aplicată imediat la schimbarea tab-ului, nu doar la următorul tick.

În plus:

- La fiecare schimbare de tab, view-urile ascunse primesc imediat `MemoryUsageTargetLevel.Low`, iar cel activ revine la `Normal`.
- Când fereastra este minimizată, toate tab-urile din fundal care nu redau audio sunt suspendate pe loc, iar cel activ primește `Low`.
- Tab-urile care redau audio (muzică, apeluri, upload-uri) nu sunt niciodată suspendate sau eliberate.
- Pragurile vechi (2/30/10) sunt migrate automat o singură dată la noile valori implicite; valorile schimbate manual de utilizator sunt păstrate.

Bara de stare arată în timp real RAM-ul întregului arbore de procese (`RAM 690 MB · 8 procese`)
plus starea tab-urilor (`3 taburi · 2 în repaus · 1 eliberate din memorie`).

Toate pragurile se schimbă din **Setări → Memorie** (sau din **Meniu → Economie de memorie**) sau direct în
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
Backgrounds\  imaginile folosite ca fundal pentru tab nou
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
| `Ctrl+,` | setări |
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
  SettingsWindow.xaml(.cs) fereastra de setări (teme, accent, fundal tab nou, memorie, navigare)
  Theme\Theme.xaml         culori + stiluri (temă întunecată, accent Brave)
  Models\                  BrowserTab (starea unui tab), ObservableObject
  Services\
    AdBlocker.cs           parsare reguli + blocare la nivel de rețea + injectarea filtrelor
    ThemeCatalog.cs        paletele de teme, aplicarea culorilor, fundalul de tab nou
    Settings.cs            settings.json (praguri de memorie, căutare, scut, temă)
    HistoryStore.cs        istoric local (ultimele 8000 de intrări)
    BookmarksStore.cs      favorite
    SessionStore.cs        tab-urile + fereastra de la închidere
    PerformanceProbe.cs    RAM/CPU pentru procesele raportate de WebView2
    UrlHelper.cs           „este URL sau căutare?”
    AppPaths.cs            %LOCALAPPDATA%\Vela\*
    Log.cs                 log rotativ
  Assets\
    vela.ico               iconița aplicației și a tab-urilor
    newtab.html            pagina de tab nou (temă din URL, căutare, tile-uri, statistici live)
    blocklist.txt          lista de blocare inclusă (curată, comentată)
    blocklist-big.txt      lista extinsă de domenii (StevenBlack unified hosts)
    adblock.js             filtre cosmetice + eliminarea reclamelor YouTube din player
```

## Limitări cunoscute

- Procesele și izolarea site-urilor sunt gestionate de Chromium/WebView2; câștigul de memorie vine din adormirea și eliberarea paginilor.
- Sincronizarea este manuală, printr-un folder ales de utilizator. Include favorite, lista de lectură și setări comune; nu transferă cookie-uri, parole sau istoric. Nu există server Vela sau cont Vela.
- Extensiile neîmpachetate folosesc API-ul WebView2, cu suport activat explicit și repornire; compatibilitatea nu este identică cu Chrome Web Store. Modul privat folosește un profil InPrivate separat în cadrul profilului ales.
- API-ul WebView2 nu expune o listă de parole individuale. Vela oferă controlul salvării, al completării automate și ștergerea datelor native, fără extragerea secretelor din profil.
- Pachetele sunt nesemnate până la configurarea unui certificat real. Versiunile sunt distribuite prin [GitHub Releases](https://github.com/visualxstudio/vela-browser/releases/latest); verificarea este manuală, fără instalare automată.
- Parsarea listelor ABP este intenționat simplă: regulile pe domenii și fragmentele de cale sunt aplicate, regex-urile complexe sunt sărite.

## Funcțiile noi și distribuția

- Mod economic în Setări → Memorie: maximum trei pagini live pe fereastră, repaus după un minut și eliberare după cinci minute. Formularele nesalvate, audio, descărcările și site-urile protejate pot depăși limita; dezactivarea modului păstrează valorile personalizate.
- Butonul profilului deschide gestionarea a maximum 16 profiluri, inclusiv cel principal. Profilurile închise nu pornesc procese. Datele profilului principal rămân în locul existent.
- Import de favorite din Chrome, Edge și Brave sau fișiere JSON/HTML, inclusiv exporturi Firefox. Sursa rămâne intactă; parolele și cookie-urile nu sunt importate.
- Grupuri de taburi cu restrângere/extindere și păstrare în sesiune. Lista „Citește mai târziu” are stare citit/necitit și este inclusă în backup și sincronizare.
- Consumul pe procese este măsurat numai la cerere. Asocierea paginilor folosește identificatorii nativi de cadre, iar procesele partajate sunt marcate explicit; paginile neîncărcate nu sunt trezite.
- Ferestrele principale și utilitare își limitează dimensiunea la zona de lucru a monitorului, inclusiv după schimbarea DPI.
- Apelurile WebRTC și media activă fără sunet sunt protejate prin evenimente. Oprirea activității și retragerea protecției `beforeunload` permit din nou eliberarea memoriei. Editorul de favorite se adaptează la ferestre înguste, iar paginile lungi rămân accesibile prin derulare.
- Ferestre private (`Ctrl+Shift+N`), taburi fixate, reordonare prin tragere, duplicare și mutare între ferestre cu păstrarea controlului live. Căutarea taburilor (`Ctrl+Shift+A`) nu încarcă paginile adormite.
- Ctrl+click și clicul cu rotița deschid taburi neîncărcate în fundal. Indicatorul audio și comanda de mute folosesc starea motorului. Popupurile păstrează `window.opener` pentru callbackuri de autentificare.
- Biblioteca include istoricul (`Ctrl+H`), favoritele cu foldere (`Ctrl+Shift+O`), backup/sincronizare, diagnostic, extensii și ștergerea datelor pe interval.
- Backup criptat cu restaurare și copie de revenire: favorite, setări, scurtături New Tab, imagine de fundal și preferințe zoom/scut. Sincronizarea păstrează înregistrări de ștergere pentru a preveni reapariția favoritelor eliminate.
- Zoom pe site, pornire în New Tab/pagina de start/sesiune, motoare de căutare predefinite și meniu contextual pentru bara de adrese.
- Imprimare (`Ctrl+P`), export PDF (`Ctrl+Shift+S`), mod de citire local și traducere externă Google numai după confirmare. Traducerea trimite adresa completă serviciului respectiv.
- Folder de descărcare configurabil, dialog opțional de salvare și progres în bara de instrumente. Informații despre HTTPS/certificat, controale native pentru autofill și explicații SmartScreen.
- Lista StevenBlack/hosts poate fi actualizată manual și restaurată la versiunea precedentă. Excepțiile scutului sunt persistente în profilul normal și temporare în modul privat.
- Diagnostic exportabil cu câmpuri permise explicit, fără URL-uri, titluri sau căi personale. Măsurarea resurselor se oprește la minimizare. Fără servicii permanente pentru sincronizare, actualizări sau traducere.

Instrucțiuni și limite pentru instalator, semnare, găzduire și teste: [docs/RELEASE.md](docs/RELEASE.md).
