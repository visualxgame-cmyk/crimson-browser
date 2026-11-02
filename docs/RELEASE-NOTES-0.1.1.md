Actualizare de stabilitate și consum pentru Vela Windows x64.

- Apelurile WebRTC, captura media și videoclipurile fără sunet sunt protejate de suspendare/eliberare, pe baza evenimentelor. Protecția se eliberează la oprirea activității.
- Eliminarea unui handler `beforeunload` nu mai lasă tabul exceptat permanent de la eliberarea memoriei. Protecțiile încă active ale site-urilor rămân respectate.
- Paginile cu protecție nativă de închidere păstrează politica conservatoare; măsurătorile includ explicit excepțiile care pot depăși limita de taburi încărcate.
- Adresele introduse în timpul inițializării controlului așteaptă finalizarea acesteia.
- În ferestre înguste, editorul de favorite se mută sub listă. Paginile lungi și navigarea laterală pot fi derulate; mesajele din setări nu se suprapun cu butoanele.

205 verificări automate au trecut în profil izolat, inclusiv WebRTC local în fundal/la minimizare, media fără sunet, captură sintetică, încărcare HTTP de 1 MB cu SHA-256 identic, descărcare de 64 MB și păstrarea documentului protejat. Actualizarea instalatorului 0.1.0 → 0.1.1 și dezinstalarea au păstrat toate cele șase fișiere de profil sintetice. Testele nu înlocuiesc conectarea reală la Google sau verificarea fizică a camerei/microfonului.

Pachetele includ .NET și necesită Microsoft Edge WebView2 Runtime. Instalatorul și arhiva portabilă rămân nesemnate digital. Manifestul oferă verificarea integrității SHA-256 și nu înlocuiește semnătura editorului. Păstrează protecțiile Windows active.
