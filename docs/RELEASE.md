# Distribuție Windows

`scripts/Package-Release.ps1` produce un instalator per utilizator și o arhivă portabilă pentru Windows x64. Include .NET, fără instalare separată a acestuia; WebView2 Runtime rămâne o cerință Microsoft. Instalarea nu forțează browserul implicit. Dezinstalarea păstrează profilul, favoritele și descărcările. Aplicația deschisă blochează înlocuirea fișierelor printr-un mutex comun cu instalatorul.

```powershell
./scripts/Get-InstallerCompiler.ps1
./scripts/Package-Release.ps1
./scripts/Verify-Release.ps1 -Directory artifacts/releases
```

Compilerul Inno Setup 6.4.3 este descărcat din release-ul oficial, cu SHA-256 fixat și verificarea semnăturii editorului. Traducerea română inclusă provine din sursa oficială Inno Setup, cu creditul traducătorului păstrat și mesajele noi completate. Licență și surse: https://github.com/jrsoftware/issrc/tree/is-6_4_3 . Nu este necesară o subscripție de împachetare.

## Semnare

Nu există un certificat configurat în acest proiect. Pachetele locale sunt **nesemnate**. `release-manifest.json` consemnează explicit acest lucru. SHA-256 detectează deteriorarea; nu dovedește identitatea editorului.

Pentru un certificat de semnare real, instalat în Windows cu cheia privată, scriptul acceptă `-CertificateThumbprint` și `-RequireSignature`, semnează aplicația și instalatorul și verifică rezultatele. `Verify-Release.ps1 -RequireSignature -ExpectedSignerThumbprint ...` respinge semnăturile invalide sau alt editor. Semnarea dezinstalatorului încorporat necesită configurarea suplimentară Inno `SignedUninstaller`/`SignTool` înaintea unei distribuții comerciale semnate. Acest flux nu a fost testat cu un certificat real. Nu crea certificate autofabricate ca substitut pentru încrederea Windows.

## Găzduire fără server propriu

Repository-ul public de distribuție este [visualxstudio/vela-browser](https://github.com/visualxstudio/vela-browser). Versiunile publicate `v0.1.0`, `v0.1.1`, `v0.1.3` și `v0.1.4` conțin instalatorul, arhiva portabilă și manifestul (versiunea `v0.1.2` nu a fost publicată). Codul sursă și profilurile utilizatorului nu au fost încărcate. Repository-ul pentru actualizări este preconfigurat în aplicație; migrarea păstrează adresele personalizate și limitele de memorie existente. Etichetele trebuie să fie `v0.1.0`, `v0.2.0` etc., în acord cu versiunea aplicației.

Vela verifică numai la cerere și deschide pagina release-ului validată pentru repository-ul ales. Nu descarcă și nu rulează automat executabile. Verificatorul real a identificat prima versiune publicată prin API-ul public GitHub.

Publicarea următoarei versiuni folosește autentificarea existentă Git Credential Manager, fără scrierea tokenului în fișiere sau argumente. Versiunea din proiect și notele trebuie actualizate înainte de împachetare. Scriptul încarcă fișierele în streaming într-un draft, compară dimensiunile și SHA-256 calculate de GitHub și refuză suprascrierea fișierelor diferite dintr-un release existent.

```powershell
./scripts/Publish-GitHubRelease.ps1 -NotesFile docs/RELEASE-NOTES-0.1.0.md -AllowUnsigned
./scripts/Publish-GitHubRelease.ps1 -NotesFile docs/RELEASE-NOTES-0.1.0.md -AllowUnsigned -Publish
```

Primul pas pregătește draftul; al doilea îl publică după verificarea fișierelor. `-InitializeRepository` se folosește numai pentru crearea unui repository nou. `-AllowUnsigned` este necesar explicit pentru pachetele fără certificat. Reluarea publicării verifică fișierele deja încărcate. Fluxul manual **Build Windows packages** rămâne pregătit în proiectul local, dar nu este instalat în repository-ul public de distribuție și nu a rulat online.

## Verificări locale

```powershell
dotnet run --project tests/Vela.SmokeTests -c Release
dotnet run --project tests/Vela.SmokeTests -c Release -- --permissions
dotnet run --project tests/Vela.SmokeTests -c Release -- --auth-compat
dotnet run --project tests/Vela.SmokeTests -c Release -- --extensions
dotnet run --project tests/Vela.SmokeTests -c Release -- --benchmark
dotnet run --project tests/Vela.SmokeTests -c Release -- --public-sites
dotnet run --project tests/Vela.SmokeTests -c Release -- --real-benchmark
dotnet run --project tests/Vela.SmokeTests -c Release -- --real-benchmark --economy
```

Harness-ul compilează sursa reală cu un director de date temporar separat. Testele nu folosesc profilul Vela obișnuit, conturi reale sau foldere cloud reale. Testele WebView2 cer o sesiune desktop Windows interactivă și runtime-ul instalat; workflow-ul de împachetare compilează harness-ul, dar nu pretinde că îl rulează într-un desktop CI.

Suita principală verifică și modul economic, grupurile, importul Chromium, lista de lectură, o descărcare de 64 MB și profilurile normale în procese separate, cu repornire și cookie-uri persistente. Testele `--public-sites` accesează Internetul într-un profil temporar și scriu `artifacts/public-sites-report.json`; includ YouTube public și un video W3C redat fără audio. Nu validează autentificări reale, streaming DRM sau apeluri cu cameră/microfon. Ferestrele se adaptează la zona de lucru, dar verificările de 125/150/200% sunt calcule sintetice și nu substituie testarea reală pe monitoare și sisteme diferite.

SDK-ul WebView2 este fixat la 1.0.4314.35. Integrarea compensează închiderea automată a ferestrei WPF la `window.close()` (https://github.com/MicrosoftEdge/WebView2Feedback/issues/5737). Mutarea controalelor live folosește un adaptor izolat pentru legătura internă cu fereastra inițială, pe care SDK-ul nu o actualizează public la reparentare. Forma necunoscută a SDK-ului respinge mutarea înainte de modificări. Orice upgrade trebuie să treacă testul mutării ultimului tab urmat de distrugerea ferestrei sursă și testul închiderii unui popup fără pierderea ferestrei proprietare.

Benchmarkul folosește New Tab local la 1/10/30 de taburi. CPU reprezintă o fereastră de o secundă imediat după navigare, nu consumul stabil în repaus și nici consumul site-urilor reale. RAM include aplicația și procesele WebView2.

Benchmarkurile `--real-benchmark` folosesc Wikipedia, Microsoft Learn, example.com, MDN, GitHub și YouTube în procese izolate succesive pentru modul normal și economic. Rapoartele sunt `artifacts/benchmarks/real-normal.json` și `real-economy.json`. Toate taburile sunt activate pe rând. Sunt măsurate starea încărcată, repausul și eliberarea; vârsta de inactivitate este avansată explicit pentru a executa politica reală fără a aștepta minute. CPU este normalizat la toate procesoarele logice, în intervale de cinci secunde. Suma working set-urilor poate include pagini partajate de mai multe procese; nu reprezintă memoria fizică unică. Site-urile dinamice și rețeaua influențează mostrele, deci rapoartele nu demonstrează un procent universal de îmbunătățire și nu compară Vela cu alte browsere.

Protecția `beforeunload` împiedică distrugerea paginii. Paginile despre care se știe că au această protecție păstrează politica conservatoare de suspendare. O încercare de a suspenda cele 13 pagini protejate din testul cu 30 de taburi a produs aproximativ 24–32% CPU în randerele native, deși API-ul raporta suspendarea; ajustarea a fost respinsă. Testul nu identifică o cauză internă definitivă în Chromium. Editările urmărite, media, apelurile, transferurile și excepțiile explicite continuă să împiedice suspendarea. Limita de controale încărcate este flexibilă când există documente protejate.

`scripts/Test-InstallerUpgrade.ps1 -OldPublishDirectory ... -NewPublishDirectory ...` compilează două instalatoare cu identitate de test, instalează versiunea veche, actualizează în același director și verifică versiunea/hash-ul noului executabil și păstrarea fișierelor de profil sintetice aflate în exterior. Dezinstalarea de test păstrează aceleași date. Nu lansează executabilul instalat și nu folosește profilul real al utilizatorului; cookie-urile native și repornirea profilurilor sunt verificate separat de suita browserului.
