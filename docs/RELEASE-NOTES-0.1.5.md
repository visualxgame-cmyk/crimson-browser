Compatibilitate și protecție pentru navigare.

- Ecranul complet (`F11` și cererile paginilor) acoperă acum întreaga suprafață a monitorului, inclusiv bara de activități. La ieșire, poziția, dimensiunea și starea ferestrei revin exact la valorile anterioare.
- Popupurile automate sunt limitate la două într-un interval de 20 de secunde pentru același tab. Ferestrele declanșate direct de utilizator nu sunt limitate, iar o fereastră obișnuită de autentificare rămâne posibilă.
- Managerul de extensii afișează, înainte de instalare și în listă, permisiunile API și site-urile declarate de manifest. Pachetele rămân copii locale gestionate de Vela și extensiile sunt oprite implicit.
- Cererile de cameră și microfon sunt acoperite de verificări native: sunt identificate clar în bara de permisiuni și pot fi refuzate fără deschiderea dispozitivului.
- Verificarea actualizărilor rămâne manuală, din Setări → Navigare, fără serviciu sau trafic de fundal.

Testele automate nu folosesc conturi Google reale, camere sau microfoane fizice. Conectarea cu confirmare pe telefon și apelurile reale rămân de verificat de utilizator.

Pachetele includ .NET și necesită Microsoft Edge WebView2 Runtime. Rămân nesemnate digital, conform opțiunii curente a proiectului. Manifestul SHA-256 verifică integritatea fișierelor.
