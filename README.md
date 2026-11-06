# 🔴 CRIMSON BROWSER

**Crimson Browser** este un browser web modern, ultra-rapid și cu consum redus de resurse, bazat pe motorul **Chromium (Blink & V8)** prin Microsoft WebView2 Evergreen, conceput cu o tematică stealth **Red & Black (Crimson)**. Creat de **VisualX Studio**.

---

## ⚡ Caracteristici Principale (v1.0)

- **Motor Chromium de ultimă generație**: Suport complet pentru standardele web moderne (HTML5, CSS Grid, ES2024, WebAssembly, decodare video 4K/60fps, WebGL).
- **Consum minim de resurse (RAM & CPU)**: 
  - Folosește runtime-ul nativ Chromium instalat în Windows, eliminând duplicarea proceselor greoaie.
  - Executabilul are sub **1 MB**, pornind instantaneu.
  - Tab-urile închise își eliberează complet memoria RAM asociată.
- **Interfață Modernă Crimson (Roșu & Negru)**:
  - Design stealth cyberpunk cu finisaje negru-antracit (`#0D0E12`) și accente roșu-crimson (`#E61937`).
  - Bară de titlu personalizată fără margini generice Windows.
  - Logo unic vectorial Crimson Shield.
- **Sistem Multi-Tab Avansat**:
  - Deschidere filă nouă (`+` sau `Ctrl+T`).
  - Închidere filă (`✕` sau `Ctrl+W`).
  - Indicator dinamic de încărcare pentru fiecare filă.
  - Deschiderea link-urilor externe/popups direct în taburi noi în cadrul browserului.
- **Bară de Navigare & Omnibox Inteligent**:
  - Butoane: Înapoi (`Alt+Stânga`), Înainte (`Alt+Dreapta`), Reîmprospătare/Oprire (`Ctrl+R` / `F5`), Acasă.
  - Căutare inteligentă: recunoaște dacă ai introdus un domeniu (ex: `youtube.com`) sau o căutare (redirecționează pe Google).
  - Indicator de securitate SSL/HTTPS (lacăt verde/roșu).
- **Pagină de Pornire Personalizată (Crimson Start)**:
  - Pagină locală integrată cu căutare rapidă, comenzi rapide (YouTube, Google, GitHub, Reddit) și status de sistem.
- **Pregătit pentru Extensii & Shields**:
  - Buton dedicat **Crimson Shields** cu meniu flyout pentru monitorizarea intimității și a consumului.
  - Instrumente de dezvoltator integrate (**DevTools** prin `F12`).
  - Curățare instantanee de cache și date de navigare din meniu.

---

## ⌨️ Comenzi Rapide (Shortcuts)

| Comandă | Acțiune |
|---|---|
| `Ctrl + T` | Deschide o filă nouă |
| `Ctrl + W` | Închide fila curentă |
| `Ctrl + R` / `F5` | Reîmprospătează pagina |
| `Ctrl + L` | Selectează bara de adrese (Omnibox) |
| `F12` | Deschide Chromium Developer Tools |

---

## 🚀 Cum se pornește

Poți lansa browserul în 2 moduri simple:
1. Dublu-click pe fișierul **`run.bat`** din folderul `d:\Browser`.
2. Sau direct executabilul:
   `D:\Browser\bin\Debug\net10.0-windows\CrimsonBrowser.exe`

---

## 🛠️ Tehnologii Utilizate

- **C# / .NET 10**
- **WPF (Windows Presentation Foundation)** cu `WindowChrome`
- **Microsoft.Web.WebView2** (Chromium Evergreen)
- **HTML5, CSS3 & JavaScript** (Pagină locală Crimson Start)

---

## 📦 Instalare (Windows)

Construiește instalatorul cu:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-installer.ps1
```

Rezultatul apare în `dist\CrimsonBrowser-Setup-<versiune>.exe` (împreună cu fișierul
`.sha256` folosit de actualizările automate). Instalarea este per-utilizator, nu necesită
drepturi de administrator și poate instala automat Microsoft Edge WebView2 dacă lipsește.

> Instalatorul este nesemnat, deci Windows SmartScreen poate afișa un avertisment la prima rulare.

---

## 🔄 Auto-update prin GitHub Releases

Crimson Browser verifică GitHub Releases la pornire (după câteva secunde) și, dacă există o
versiune mai nouă, cere confirmarea. După acceptare descarcă instalatorul, verifică SHA-256,
îl rulează silențios și repornește browserul. Verificarea automată poate fi dezactivată din
**Setări › Despre**, iar verificarea manuală rămâne disponibilă în același loc.

Pentru a publica o versiune nouă:

1. Crește `<Version>` din `CrimsonBrowser.csproj` (de exemplu `1.0.1`).
2. Comite modificarea și creează un tag: `git tag v1.0.1`.
3. Trimite tag-ul: `git push origin v1.0.1`.

Workflow-ul `.github/workflows/release.yml` construiește automat instalatorul, generează
checksum-ul SHA-256 și publică ambele fișiere în GitHub Releases. Browserul va detecta
release-ul la următoarea pornire.

Repository-ul folosit este configurat în `Services/UpdateService.cs`
(`visualxgame-cmyk/crimson-browser`).
