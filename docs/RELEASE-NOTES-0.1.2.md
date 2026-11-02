Corecții pentru compatibilitatea autentificării și confirmărilor pe telefon.

- Editarea câmpurilor de autentificare nu mai adaugă avertismentul Vela de părăsire a paginii, care putea întrerupe redirecționările după confirmare. Protecția din browser împotriva eliberării automate și avertizarea la închiderea explicită rămân disponibile; protecțiile proprii ale site-urilor sunt păstrate.
- Etapele de autentificare Google și cadrele interactive de conectare sunt protejate de suspendare/eliberare în timp ce așteaptă confirmarea. Cadrele pasive pentru cookie-uri și verificarea sesiunii nu păstrează toate taburile autentificate în memorie.
- Scripturile cosmetice și modificările API-urilor playerului nu sunt aplicate documentelor Google de autentificare sau consimțământ. Blocarea de rețea rămâne activă.
- Câmpurile eliminate de formularele SPA nu mai rămân reținute în starea de editare. Observarea eliminărilor este activă numai cât există modificări.

223 verificări au trecut în profiluri izolate, inclusiv reproducerea problemei cu avertismentul injectat, confirmare asincronă în fundal/la minimizare, redirecționare, cadre interactive și revenirea la politica normală după terminarea fluxului. Testele folosesc documente locale și date sintetice; confirmarea reală Google pe telefon rămâne de verificat de utilizator.

Cookie-urile și datele contului nu sunt șterse. Pachetele rămân nesemnate digital și includ .NET; este necesar Microsoft Edge WebView2 Runtime. Manifestul SHA-256 verifică integritatea și nu înlocuiește semnătura editorului.
