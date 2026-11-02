Solicitări de permisiuni integrate în browser.

- Ferestrele „Vela — permisiune” cu Yes/No sunt înlocuite cu o bară discretă sub bara de adrese, cu **Permite**, **Blochează** și **Nu acum**. Pagina și tastatura rămân utilizabile.
- Alegerile Permite/Blochează se păstrează în profilul nativ. Nu acum refuză temporar și împiedică repetarea solicitării în același document. În privat, alegerile rămân numai în fereastra respectivă.
- Solicitarea aparține tabului și originii care au cerut accesul. Navigarea, eliminarea cadrului și închiderea eliberează cererile rămase în așteptare. Cererile active sunt protejate de eliberarea automată a tabului.
- Interfața folosește controalele WPF existente și evenimentele motorului, fără renderer suplimentar sau verificări periodice.

Include și corecțiile de compatibilitate a autentificării din versiunea de test 0.1.2: evitarea avertismentului injectat la redirecționarea formularelor de conectare, protejarea etapelor interactive Google de suspendare și eliminarea stărilor de editare expirate din formulare SPA. Autentificarea reală Google cu confirmare pe telefon rămâne de verificat de utilizator; testele automate folosesc date sintetice.

Pachetele includ .NET și necesită Microsoft Edge WebView2 Runtime. Rămân nesemnate digital, conform opțiunii curente a proiectului. Manifestul SHA-256 verifică integritatea fișierelor.
