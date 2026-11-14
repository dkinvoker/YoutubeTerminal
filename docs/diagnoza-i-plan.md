# YouTube Terminal — diagnoza, plan poprawek i propozycje

14 listopada 2026

Płynność wideo ogranicza głównie ilość danych Sixel (ok. 1,2 MB na klatkę przy ustawieniach na maksimum) oraz szeregowe przetwarzanie klatek na procesorze. Wolno ładujące się komentarze to błąd odświeżania ekranu, a nie pobierania; poprawka jest mała. W Windows Terminal Sixel pozostaje jedyną technologią obrazu, dlatego proponuję automatyczną jakość, tryb oktantów oraz Kitty i iTerm2 dla innych terminali.

## Płynność wideo (7–15 FPS)

Przy ustawieniach na maksimum (obraz 1920×1080, 255 kolorów) jedna klatka Sixel ma ok. 1,2 MB, a jej przygotowanie zajmuje ok. 45 ms procesora. Każda z tych dwóch rzeczy osobno spycha odtwarzanie poniżej 30 FPS.

Pomiar na Twoim procesorze (AMD Ryzen 7 5700U, 8 rdzeni), klatka z prawdziwego materiału:

| Obraz wideo | Kodowanie Sixel, 255 kol. (ms) | Klatka, 128 kol. (KB) | Klatka, 255 kol. (KB) | Dane przy 30 FPS, 255 kol. (MB/s) |
| --- | --- | --- | --- | --- |
| 1920×1080 | 27,8 | 928 | 1227 | 36,0 |
| 1280×720 | 14,6 | 588 | 770 | 22,5 |
| 960×540 (domyślne) | 11,5 | 384 | 509 | 14,9 |
| 640×360 | 7,8 | 224 | 302 | 8,8 |

Do kodowania dochodzą konwersja kolorów NV12→RGB (2,1 ms przy źródle 480p, 6,6 ms przy 1080p), skalowanie (0–13 ms) i dekodowanie H.264.

**Przyczyny**

- **Przepustowość terminala — najpewniej główne ograniczenie.** 30 FPS przy 1920 px wymaga, by Windows Terminal przetworzył ok. 36 MB/s danych Sixel. Twój wynik (7–15 FPS przy ok. 1,2 MB na klatkę) odpowiada 9–18 MB/s. To hipoteza: dokładny limit Windows Terminal zmierzy test opisany niżej.
- **Szeregowy potok w jednym wątku.** Dekodowanie, konwersja, skalowanie i kodowanie idą po kolei. Przy 1080p daje to ok. 45 ms na klatkę, czyli najwyżej ok. 22 FPS nawet przy nieskończenie szybkim terminalu.
- **Powiększanie ponad źródło.** Przy źródle 720p obraz 1920×1080 nie ma więcej szczegółów, a kosztuje 2,1× więcej danych niż 1280×720 i dodatkowe 13 ms skalowania.

**Poprawki**

1. **Automatyczna jakość.** Krótki test przepustowości terminala przy pierwszym odtwarzaniu i stały pomiar czasu zapisu każdej klatki. Program sam dobiera rozmiar obrazu, liczbę kolorów i FPS tak, by utrzymać docelowe 24–30 FPS. Ręczne ustawienia zostają jako limity górne.
2. **Potok równoległy.** Osobny wątek dekodowania, dwa wątki kodowania pracujące na kolejnych klatkach oraz równoległy histogram i mapowanie kolorów w kwantyzatorze. Szacunkowo ok. 2× więcej klatek na sekundę po stronie CPU.
3. **Bez wysyłania zbędnych pikseli.** Obraz nie większy niż źródło, a jakość źródła dobierana do rozmiaru obrazu.
4. **Mniej danych na klatkę.** Paleta stabilna między klatkami (więcej pasków rozpoznanych jako niezmienione), paski o wysokości jednego wiersza zamiast trzech oraz domyślnie 96–128 kolorów dla wideo.
5. **Lepsze statystyki.** Ekran `i` w odtwarzaczu pokaże dodatkowo MB/s do terminala i czas zapisu klatki, żeby od razu było widać, czy hamuje procesor, czy terminal.

## Komentarze: długie ładowanie i zamrożony wskaźnik

Komentarze przychodziły szybko (zapytanie trwa ok. 0,3–0,5 s), ale ekran nie odświeżał się po ich nadejściu. Pojawiały się dopiero przy następnym naciśnięciu klawisza, dlatego ładowanie wyglądało na długie.

**Przyczyny w kodzie**

- `App.Post` przekazuje wynik zadania w tle do wątku interfejsu, ale nie oznacza ekranu do przerysowania. Ekran odświeża się dopiero przy innym zdarzeniu: klawiszu, myszy albo załadowanej miniaturze.
- Wskaźnik ładowania zmienia klatkę tylko wtedy, gdy trwa zadanie „głośne” (pokazywane w pasku stanu). Komentarze ładują się jako zadanie „ciche”, więc wskaźnik stoi.

Ten sam błąd dotyczy doczytywania kolejnych wyników na listach, rozwijania odpowiedzi pod komentarzem, podpowiedzi w wyszukiwarce i wersji yt-dlp w Ustawieniach.

**Poprawka (mała, kilkanaście linii)**

1. Każde zakończenie zadania w tle oznacza ekran do przerysowania.
2. Wskaźnik ładowania liczony z zegara, niezależnie od rodzaju zadania; widok odświeża się, dopóki coś się ładuje.
3. Komentarze pobierane w tle od razu po otwarciu filmu, żeby zakładka była zwykle gotowa w chwili wejścia.

## Inne usterki z przeglądu

Najbardziej odczuwalny jest czas startu odtwarzania (7–10 s); pozostałe to drobne błędy wyświetlania.

| Usterka | Przyczyna | Poprawka |
| --- | --- | --- |
| Start odtwarzania trwa 7–10 s | yt-dlp w wersji jednoplikowej rozpakowuje się przy każdym uruchomieniu (ok. 1–2 s), potem odpytuje YouTube (ok. 5 s) | Wbudować wersję katalogową yt-dlp (bez rozpakowywania) i pobierać adresy strumieni w tle już po zatrzymaniu zaznaczenia na filmie |
| Głośność zmienia się z opóźnieniem ok. 0,4 s | Głośność jest nakładana na próbki przed buforem audio | Ustawiać głośność na wyjściu audio, działa natychmiast |
| Szczegóły filmu otwartego z linku nie pokazują czasu trwania | Odpowiedź YouTube dla strony filmu nie zawiera długości | Uzupełniać z danych yt-dlp, które i tak są pobierane w tle |
| Nagłówek playlisty bywa bez autora | Nowy układ nagłówka playlist na YouTube | Dodatkowe źródło autora w parserze |
| Składanki pokazują autora „YouTube” i liczbę filmów „Składanka” | Inny format etykiet składanek w wynikach | Osobne etykiety dla składanek |
| W transmisji na żywo komunikat „przewijanie niedostępne” znika przy włączonych statystykach | Statystyki zajmują ten sam wiersz co komunikaty | Komunikaty w osobnym miejscu |

Podczas jednego z testów aplikacja zamknęła się po kilku szybkich przewinięciach. Błąd się nie powtórzył, a od tamtej pory każdy wyjątek trafia do `%LOCALAPPDATA%\YoutubeTerminal\log.txt`.

## Technologie obrazu w terminalu

W Windows Terminal jedyną technologią obrazu jest dziś Sixel. „Obrazki w terminalu”, o których słyszałeś, to najpewniej jeden z dwóch nowszych protokołów: Kitty graphics protocol albo iTerm2 Inline Images (narzędzie `imgcat`). Oba dają pełny kolor i kompresję, ale Windows Terminal nie obsługuje żadnego z nich ([test WT 1.24](https://ucs-detect.readthedocs.io/sw_results/windowsterminal.html)).

| Technologia | Jak przesyła obraz | Kolory | Dla wideo | Windows Terminal | Inne terminale |
| --- | --- | --- | --- | --- | --- |
| Sixel (obecnie używany) | Paski po 6 px z paletą, bez kompresji | Do 256 na obraz | Dużo danych: 1,2 MB na klatkę 1080p | Tak, od 1.22 | xterm, foot, mlterm, WezTerm, iTerm2, Konsole, mintty |
| [Kitty graphics protocol](https://github.com/microsoft/terminal/issues/8389) | Surowe RGB lub PNG, kompresja zlib, identyfikatory obrazów, podmiana klatek animacji | 16,7 mln | Najlepszy: pełny kolor, kompresja, aktualizacja obrazu w miejscu | Nie. Zespół eksperymentuje od sierpnia 2026, [PR #20885](https://github.com/microsoft/terminal/pull/20885) zamknięto bez scalenia 3 lis 2026 | kitty, Ghostty, WezTerm, iTerm2, Konsole |
| [iTerm2 Inline Images](https://iterm2.com/documentation-images.html) | Gotowy plik PNG lub JPEG w base64 | 16,7 mln | Klatka JPEG to kilkadziesiąt KB, ale każda klatka jest nowym obrazem | Nie | iTerm2, WezTerm, mintty |
| Znaki blokowe Unicode | Półbloki (1×2 px na komórkę, obecny tryb awaryjny) lub oktanty (2×4 px na komórkę) z kolorem tekstu i tła | 16,7 mln, ale 2 na komórkę | Działa wszędzie, gdzie jest tekst; mniej szczegółów niż Sixel | Półbloki tak; oktanty rysowane przez sam terminal od [1.25](https://github.com/microsoft/terminal/releases/tag/v1.25.2733.0) | Większość, zależnie od czcionki |

**Co z tego wynika dla programu**

- **Na Twoim terminalu zostaje Sixel.** Najwięcej dadzą poprawki z sekcji o płynności oraz aktualizacja Windows Terminal z 1.24 do 1.25, stabilnej od 2 października 2026. Przy wydaniu zapowiedzi 1.25 Microsoft podał 10–20% większą przepustowość wejścia/wyjścia ([ogłoszenie](https://devblogs.microsoft.com/commandline/?p=11224)).
- **Windows Terminal skaluje Sixel.** Obraz jest układany w wirtualnej komórce 10×20 px i dopasowywany do rozmiaru czcionki ([PR #17421](https://github.com/microsoft/terminal/pull/17421)), więc ostrość zależy od czcionki, a obraz szerszy niż ekran w pikselach nic nie daje.
- **Nowy tryb „oktanty”.** Daje 4× więcej szczegółów niż obecne półbloki i nie wymaga Sixela. W WT 1.25 te znaki rysuje sam terminal, więc wyglądają równo niezależnie od czcionki. Przy mniejszych oknach może być nawet płynniejszy od Sixela; do zmierzenia.
- **Kitty i iTerm2 jako dodatkowe tryby z automatycznym wykrywaniem.** Przydadzą się w WezTerm, Ghostty czy kitty i od razu zadziałają, jeśli Windows Terminal doda Kitty. Program ma już wydzieloną warstwę koderów obrazu, więc to głównie nowy koder i wykrywanie.

## Propozycje nowych funkcji

Najwięcej dla codziennego używania dadzą napisy, odtwarzanie w tle i automatyczna jakość obrazu; lista jest ułożona od najważniejszych.

| Propozycja | Co daje | Nakład |
| --- | --- | --- |
| Automatyczna jakość obrazu | Płynne wideo bez ręcznego strojenia (szczegóły w sekcji o płynności) | Średni |
| Napisy | Napisy z YouTube, także automatyczne i tłumaczone, wyświetlane pod obrazem; wybór języka klawiszem | Średni |
| Odtwarzanie w tle | Muzyka lub podcast gra dalej, a Ty przeglądasz YouTube; na dole ekranu pasek z tytułem, czasem i pauzą | Średni |
| Tryb oktantów | Wyraźnie lepszy obraz bez Sixela (2×4 px na komórkę zamiast 1×2) | Mały |
| Tryby Kitty i iTerm2 z wykrywaniem | Pełny kolor i mniej danych w WezTerm, Ghostty, kitty, mintty; gotowe na Kitty w Windows Terminal | Średni |
| Podgląd klatki przy przewijaniu | Miniatura miejsca, do którego przewijasz (storyboard YouTube), jak na stronie | Średni |
| Prędkość odtwarzania 0,5–2× | Szybsze oglądanie wykładów i poradników bez zmiany wysokości głosu | Średni |
| Kolejka „Odtwórz następne” | Dokładanie filmów do kolejki z dowolnej listy i podgląd kolejki | Mały |
| Zmiana jakości w trakcie odtwarzania | Przełączanie 360p/480p/720p/1080p bez wychodzenia z odtwarzacza | Mały |
| Import subskrypcji z Google Takeout | Jednorazowe przeniesienie subskrypcji z konta YouTube (plik `subscriptions.csv`) | Mały |
| Wyszukiwanie na kanale | Szukanie filmów w obrębie jednego kanału | Mały |
| Licznik nowych filmów | Liczba nowych filmów z subskrypcji od ostatniej wizyty, widoczna w nagłówku | Mały |
| SponsorBlock (opcja) | Automatyczne pomijanie segmentów sponsorowanych. Wymaga wysyłania identyfikatora filmu do zewnętrznego serwisu, więc tylko po włączeniu w Ustawieniach | Mały |
| Return YouTube Dislike (opcja) | Liczba łapek w dół w szczegółach filmu. Też zewnętrzny serwis, domyślnie wyłączone | Mały |

Celowo pomijam logowanie do konta Google: wymagałoby obsługi danych logowania lub ciasteczek przeglądarki, a lokalne subskrypcje i import z Takeout pokrywają większość potrzeb.

## Kolejność prac

Najpierw szybkie poprawki, potem wydajność wideo oparta na pomiarze, a nowe funkcje na końcu, według Twojego wyboru.

1. **Poprawki.** Odświeżanie ekranu po zadaniach w tle i wskaźnik ładowania, usterki z tabeli, szybszy start odtwarzania.
2. **Pomiar.** Test przepustowości terminala i rozszerzone statystyki w odtwarzaczu. Pierwszy pomiar na Twoim Windows Terminal rozstrzygnie, czy hamuje procesor, czy terminal.
3. **Wydajność wideo.** Potok równoległy, mniej danych na klatkę, automatyczna jakość.
4. **Nowe tryby obrazu.** Oktanty, a następnie Kitty i iTerm2 z automatycznym wykrywaniem.
5. **Nowe funkcje** wybrane z listy propozycji.

## Decyzje przed startem

- **Priorytet obrazu:** płynność (np. 30 FPS przy obrazie ok. 960 px) czy rozmiar i ostrość kosztem płynności? Od tego zależy domyślny cel automatycznej jakości.
- **Windows Terminal 1.25:** czy możesz zaktualizować z 1.24? Jeśli tak, tryb oktantów będzie wyglądał równo niezależnie od czcionki.
- **Szybszy start:** wersja katalogowa yt-dlp to ok. 5 MB więcej w pliku `.exe` i kilkaset plików rozpakowanych raz do `%LOCALAPPDATA%\YoutubeTerminal`. Czy taki koszt jest w porządku?
- **Nowe funkcje:** które z listy propozycji wchodzą, w tym czy włączamy opcje korzystające z zewnętrznych serwisów (SponsorBlock, Return YouTube Dislike)?

## Źródła

- [Windows Terminal: zgłoszenie #8389 — obsługa Kitty graphics protocol](https://github.com/microsoft/terminal/issues/8389)
- [Windows Terminal: PR #20885 — implementacja Kitty graphics (zamknięty)](https://github.com/microsoft/terminal/pull/20885)
- [Windows Terminal: PR #17421 — Sixel w conhost, wirtualna komórka 10×20](https://github.com/microsoft/terminal/pull/17421)
- [ucs-detect: wyniki testów Windows Terminal 1.24](https://ucs-detect.readthedocs.io/sw_results/windowsterminal.html)
- [Windows Terminal 1.25 — informacje o wydaniu stabilnym](https://github.com/microsoft/terminal/releases/tag/v1.25.2733.0)
- [Microsoft Command Line blog: Windows Terminal 1.25 Preview (5 mar 2026)](https://devblogs.microsoft.com/commandline/?p=11224)
- [iTerm2: Inline Images Protocol](https://iterm2.com/documentation-images.html)

Pomiary wydajności: własny test na Twoim komputerze (AMD Ryzen 7 5700U), 20 powtórzeń na rozmiar, kompilacja Release.
