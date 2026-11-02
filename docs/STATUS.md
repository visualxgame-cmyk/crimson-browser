# Starea funcțiilor Vela — 2 noiembrie 2026

Prioritatea rămâne consumul redus: șase controale live implicit, trei în modul economic, taburi în fundal create fără renderer, o listă de blocare partajată în proces, lucru declanșat de evenimente și operații opționale la cerere. Limitele pe fereastră respectă protecțiile pentru formulare, audio, descărcări și site-uri exceptate.

| Funcție cerută | Stare |
| --- | --- |
| Solicitări de permisiuni | Bară WPF nemodală pe tab, fără focus furat; Permite/Blochează persistente, Nu acum temporar; cadrele și navigarea eliberează deferral-urile; alegeri private doar în fereastră |
| Compatibilitatea conectării Google | Formularele de autentificare nu primesc veto-ul injectat de navigare; documentele și cadrele interactive sunt protejate de eliberare; fluxuri sintetice verificate, confirmarea reală pe telefon rămâne de verificat |
| Măsurători la 1/10/30 de taburi | Site-uri reale în mod normal și economic, fără erori de navigare; excepțiile native de închidere sunt numărate; vezi PERFORMANCE.md |
| Verificări de compatibilitate și regresie | 274 verificări trecute în profil izolat; permisiuni nemodale, autentificare sintetică, PDF, fișiere, WebRTC în fundal/la minimizare, popupuri, formulare, navigări suprapuse, migrarea setărilor și crash recovery |
| Mod economic | Limite 3 / 1 minut / 5 minute; păstrează preferințele personalizate și protecțiile existente |
| Activitate media fără sunet | Urmărire prin evenimente pentru WebRTC, redare media și captură; include cadrele și eliberarea protecției după oprire; fără polling de JavaScript |
| Transfer de fișiere încărcate | POST real prin loopback de 1 MB, SHA-256 identic; fișier sintetic, fără date personale |
| Ferestre mici și tastatură | Biblioteca și setările verificate la 760×500; editor adaptiv, butoane accesibile și traversare a focusului |
| Profiluri normale multiple | Directoare și procese separate; setările, favoritele și cookie-urile persistente izolate după repornire; pornirea reală App.xaml verificată separat |
| Import din alte browsere | Chrome, Edge, Brave, JSON Chromium și HTML; foldere Unicode, validarea adreselor și eliminarea duplicatelor |
| Grupuri de taburi | Atribuire, restrângere/extindere, căutare după grup și recuperarea metadatelor din sesiune |
| Citește mai târziu | Stare citit/necitit, salvare locală, backup și sincronizare cu înregistrări de ștergere |
| Consum pe procese | RAM/CPU reale per PID, asociere prin cadre native, procese partajate marcate; fără încărcarea taburilor eliberate |
| Dimensiuni și DPI | Adaptare la zona de lucru și schimbări DPI; calcule sintetice verificate la 125%, 150% și 200% |
| Site-uri publice și video | Wikipedia, Microsoft Learn, YouTube și decodare MP4 HTTPS W3C verificate cu scutul activ |
| Instalator și dezinstalator | Actualizare reală 0.1.0 → 0.1.1 în aceeași instalare de test, hash/versiune noi confirmate; șase fișiere sintetice de profil păstrate după upgrade și dezinstalare |
| Semnare și verificarea pachetelor | Verificare SHA-256/Authenticode și proces de semnare pregătite; certificatul real lipsește |
| Backup și recuperare | Arhive criptate cu setări, favorite, scurtături, fundal și preferințe pe site; copie de revenire la restaurare |
| Ferestre private | Profil InPrivate nativ; izolare cookie-uri și excluderea istoricului/sesiunii verificate |
| Manager de istoric | Căutare, intervale și eliminarea înregistrărilor |
| Manager de favorite și foldere | Editare, mutare, foldere imbricate, import/export HTML |
| Reordonare taburi prin tragere | Integrată cu limitele taburilor fixate |
| Taburi fixate | Antet compact și persistență în sesiune |
| Căutare taburi | În toate ferestrele aceluiași mod, fără trezirea paginilor |
| Duplicare și mutare între ferestre | Transfer live; verificat inclusiv după distrugerea ferestrei sursă |
| Ctrl+click și clic cu rotița | Taburi neîncărcate în fundal; evenimente de intrare reale ale motorului verificate |
| Indicator audio și mute | Stare nativă, păstrată la reconstruirea tabului |
| Zoom pe site | Persistență normală, preferințe private temporare; izolare între origini verificată |
| Imprimare și PDF | API nativ; fișier PDF generat și verificat |
| Folder de descărcări și Salvează ca | Folder Windows relocat sau configurat, dialog opțional și nume fără suprascriere automată |
| Progres descărcări | Indicator în toolbar, actualizat din evenimente, cu limitarea redesenărilor |
| Opțiuni la pornire | Sesiune, pagină principală sau New Tab |
| Motoare de căutare | DuckDuckGo, Google, Bing, Brave și adresă personalizată |
| Acțiuni în bara de adrese | Editare standard, lipire și accesare/căutare, copiere URL complet |
| Poziția ferestrei | Plasare nativă, maximimizare și verificarea monitorului disponibil |
| Ecran complet | `F11` și cererile paginilor acoperă întreaga suprafață a monitorului, inclusiv zona barei de activități; fereastra este marcată nativ ca fullscreen (`ITaskbarList2`), cu revenire `Topmost` dacă shell-ul refuză marcajul; la ieșire se restaurează poziția, dimensiunea și starea anterioară |
| Parole și completare automată | Controale native de salvare/ștergere; API-ul nu permite listarea parolelor individuale |
| Conexiune și certificat | HTTPS validat de motor, certificat la cerere, fără ocolirea erorilor TLS |
| Explicații de confidențialitate | SmartScreen, modul privat, traducerea externă și datele excluse din sincronizare |
| Actualizare liste cu revenire | HTTPS manual, validare înainte de înlocuire, copie precedentă; cazurile de eroare testate |
| Excepții persistente pentru scut | Pe origine, cu preferințe separate temporare în privat |
| Ștergerea datelor pe interval | Istoric Vela și API-ul nativ WebView2 pentru datele selectate |
| Tastatură și accesibilitate | Comenzi, nume accesibile, focus și verificare vizuală pe ecranul disponibil |
| Export de diagnostic | Câmpuri permise explicit, fără URL-uri, titluri, parole sau căi personale |
| Oprirea diagnosticului la minimizare | Oprire și reluare verificate |
| Cititor, traducere, extensii, sincronizare | Cititor local; traducere externă confirmată; extensii neîmpachetate opționale; sincronizare manuală criptată prin folder |

Extensiile au și un test separat de instalare/dezactivare/eliminare cu o extensie locală fără permisiuni sensibile. Componentele interne Microsoft PDF/Clipboard nu sunt afișate ca extensii instalate de utilizator.

Nu au fost folosite conturi reale, cookie-uri personale, foldere cloud reale sau un certificat de semnare. Compatibilitatea autentificării cu furnizori reali, toate configurațiile de monitoare/DPI și dispozitivele cameră/microfon necesită verificări pe configurațiile respective. Testele locale nu reprezintă o garanție universală pentru toate site-urile.

Raportul site-urilor publice este în `artifacts/public-sites-report.json`. RAM include aplicația și toate procesele WebView2; mostrele CPU de cinci secunde includ activitatea de după navigare. Nu sunt măsurători stabile de repaus. Descărcarea locală de 64 MB și izolarea profilurilor în procese succesive au fost verificate. Nu au fost testate pe hardware separat Windows 10 și Windows 11 sau pe toate configurațiile multi-monitor.

YouTube poate înlocui propria navigare cu o cerere `themeRefresh`. Vela ignoră finalizarea cererii vechi atunci când actualizează încărcarea și securitatea paginii noi. Testul public urmărește navigarea înlocuitoare; nu confundă anularea primei cereri cu eșecul întregului site.

[Vela 0.1.4](https://github.com/visualxstudio/vela-browser/releases/tag/v0.1.4) este cea mai recentă versiune publicată, cu instalator, arhivă portabilă și manifest SHA-256. Hash-urile calculate de GitHub coincid cu fișierele locale. Repository-ul de distribuție conține pagina de descărcare și versiunile compilate; codul sursă rămâne local. Workflow-ul de compilare din proiect nu a fost executat online. Detalii: [RELEASE.md](RELEASE.md).

Măsurătorile și limitele lor sunt în [PERFORMANCE.md](PERFORMANCE.md). În ultimele mostre cu 30 de taburi, CPU a fost 0,64% normal și 0,61% economic. Au rămas 13 pagini cu protecție de închidere; memoria raportată este aproximativ 1,9 GB ca sumă a working set-urilor, nu memorie fizică unică.

Utilizatorul a ales Google pentru verificările cu cont real. Autentificarea și apelurile Google Meet cu dispozitive fizice rămân de confirmat de utilizator; testele automate nu au folosit credențiale personale. Deschiderea automată a Google în executabilul Vela a fost respinsă de politica de execuție, fără motiv detaliat; este necesară deschiderea manuală.
