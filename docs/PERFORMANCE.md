# Măsurători Vela 0.1.1 — site-uri reale

Rulări separate, în profiluri izolate, cu Wikipedia, Microsoft Learn, example.com, MDN, GitHub și YouTube. Fiecare tab a fost activat înainte de măsurare. Nu s-au folosit conturi personale.

| Mod | Taburi | RAM după încărcare (MB) | RAM după politica de eliberare (MB) | CPU în ultima mostră (%) | Controale păstrate | Pagini protejate |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| normal | 1 | 564.4 | 564.2 | 0.18 | 1 | 0 |
| normal | 10 | 1033.1 | 1024.5 | 0.46 | 5 | 4 |
| normal | 30 | 1940.8 | 1929.7 | 0.64 | 14 | 13 |
| economy | 1 | 579.5 | 565.5 | 0.21 | 1 | 0 |
| economy | 10 | 892.9 | 930.1 | 0.95 | 5 | 4 |
| economy | 30 | 1933.3 | 1929.5 | 0.61 | 14 | 13 |

RAM este suma working set-urilor aplicației și proceselor WebView2 și poate număra de mai multe ori memoria partajată. CPU este normalizat la toate procesoarele logice, în mostre de cinci secunde, și nu este o medie pe termen lung. Vârsta taburilor a fost avansată pentru testarea politicii reale; nu s-a așteptat pasiv expirarea minutelor.

La 30 de taburi, 13 pagini au avut protecție înainte de închidere. Aceste excepții împiedică impunerea unei limite stricte de 3/6 controale. Formularele, apelurile, media și descărcările rămân protejate. Consumul de memorie al acestui scenariu nu poate fi redus la costul a trei pagini fără a abandona aceste garanții.

O ajustare care suspenda paginile cu protecție de închidere a produs 24–32% CPU în randerele native și a fost respinsă. Politica conservatoare păstrată în versiunea finală a revenit la sub 1% CPU în ultimele mostre cu 30 de taburi. Nu este identificată o cauză internă definitivă în Chromium și nu este pretins un câștig universal pentru toate site-urile.

Rapoartele complete: artifacts/benchmarks/real-normal.json și artifacts/benchmarks/real-economy.json.
