# YouTube Terminal

Przeglądarka YouTube w terminalu (TUI) z odtwarzaniem wideo przez **Sixel**. To jeden plik
`YoutubeTerminal.exe` skompilowany przez **.NET Native AOT**: nie wymaga zainstalowanego .NET,
ffmpeg ani niczego innego.

## Funkcje

- **Wyszukiwarka** z podpowiedziami, filtrem (wszystko / filmy / kanały / playlisty), sortowaniem
  (trafność, data, wyświetlenia, ocena), nieskończonym przewijaniem i historią wyszukiwań.
  Można też wkleić link do filmu, Shorts, kanału, `@uchwytu` lub playlisty.
- **Kanały**: nagłówek z awatarem, zakładki (Filmy, Shorts, Na żywo, Playlisty…), sortowanie
  (najnowsze / popularne / najstarsze), subskrypcje.
- **Playlisty i składanki**: odtwarzanie całości, od wybranego filmu lub losowo.
- **Szczegóły filmu**: miniatura, statystyki, opis, rozdziały, komentarze (z odpowiedziami
  i sortowaniem) oraz podobne filmy.
- **Odtwarzacz**: obraz Sixel (albo półbloki `▀` w terminalach bez Sixel) zsynchronizowany z dźwiękiem.
  - pauza, przewijanie, skoki procentowe, rozdziały;
  - głośność i wyciszanie, tryb pełnoekranowy, tryb „tylko dźwięk”;
  - kolejka i autoodtwarzanie, wznawianie od miejsca przerwania;
  - **transmisje na żywo**.
- **Biblioteka lokalna**: subskrypcje (z kanałem „najnowsze filmy” zbieranym z RSS), historia
  z postępem oglądania oraz lista „Do obejrzenia”.
- Miniatury Sixel na listach i w panelu podglądu, obsługa myszy (kółko, kliknięcia, pasek postępu).

## Wymagania

- Windows 10/11 x64.
- Do grafiki Sixel: **Windows Terminal 1.22 lub nowszy**. W innych terminalach program przełącza się
  na tryb półbloków.

## Uruchomienie

```bash
YoutubeTerminal.exe
```

Program przyjmuje też frazę albo link w linii poleceń:

```bash
YoutubeTerminal.exe "lofi hip hop"
```

```bash
YoutubeTerminal.exe https://youtu.be/aqz-KE-bpKQ
```

Przy pierwszym uruchomieniu program:

- wypakowuje wbudowany `yt-dlp.exe` do `%LOCALAPPDATA%\YoutubeTerminal\bin`;
- tworzy obok siebie deinstalator `odinstaluj.cmd`.

## Klawisze

| Ogólne | |
|---|---|
| `1`–`5` | Szukaj · Subskrypcje · Historia · Do obejrzenia · Ustawienia |
| `/` | wyszukiwarka |
| `Esc` / `Backspace` / `q` | wstecz (`q` na ekranie głównym zamyka program) |
| `F1` / `?` | pomoc |
| `Ctrl+C` | wyjście |

| Listy | |
|---|---|
| `↑↓ PgUp PgDn Home End` | nawigacja |
| `Enter` | otwórz |
| `p` / `Spacja` | odtwórz |
| `a` | tylko dźwięk |
| `c` | kanał |
| `s` | subskrybuj |
| `w` | do obejrzenia |
| `y` | kopiuj link |
| `o` | przeglądarka |
| `Tab` / `Shift+Tab` | filtr lub zakładki / sortowanie |

| Odtwarzacz | |
|---|---|
| `Spacja` / `k` | pauza |
| `← →` | ±5 s |
| `j l` | ±10 s |
| `PgUp PgDn` | ±60 s |
| `0`–`9` | skok do 0–90% |
| `↑ ↓` / `+ -` | głośność |
| `m` | wycisz |
| `, .` | rozdziały |
| `n N` | następny / poprzedni z kolejki |
| `f` | pełny ekran |
| `v` | Sixel ↔ półbloki |
| `d` | paleta adaptacyjna ↔ dithering |
| `[ ]` | rozmiar obrazu |
| `a` | tylko dźwięk |
| `i` | statystyki |
| `End` | na żywo: powrót do bieżącego momentu |

## Ustawienia i wydajność

Ustawienia (klawisz `5`) zapisują się w `%LOCALAPPDATA%\YoutubeTerminal\settings.json`. Jeśli obraz się
zacina, zmniejsz kolejno:

- **maks. szerokość obrazu wideo** (domyślnie 960 px);
- **kolory w klatce wideo** (domyślnie 128);
- **limit FPS**.

Tylko zmienione fragmenty klatki są wysyłane do terminala, więc statyczne sceny (podcasty, streamy
lofi) zużywają niewiele przepustowości.

Gdy obrazy mają złe proporcje albo nachodzą na tekst, ustaw ręcznie **rozmiar komórki**. Windows Terminal
używa dla Sixel wirtualnej komórki 10×20 px.

Gdy YouTube coś zmieni i odtwarzanie przestanie działać, zaktualizuj yt-dlp z poziomu Ustawień
(pozycja „yt-dlp”, Enter).

## Jak to działa

| Warstwa | Implementacja |
|---|---|
| Dane YouTube | publiczne API strony (InnerTube, klient WEB) i kanały RSS. Parser przechodzi drzewo JSON i rozpoznaje znane renderery, więc drobne zmiany układu nie psują aplikacji. |
| Adresy strumieni | wbudowany yt-dlp, wybierający H.264 + AAC. |
| Pobieranie | własny klient HTTP z żądaniami `Range` (pobieranie całego pliku jednym żądaniem YouTube mocno dławi). |
| Demultipleksacja | własny demuxer MP4 (DASH: `sidx`/`moof`/`trun` oraz klasyczne tablice próbek) i HLS (MPEG-TS + packed AAC) dla transmisji na żywo. |
| Dekodowanie | systemowe dekodery Windows Media Foundation (H.264 MFT, AAC MFT) wywoływane przez tablice wirtualne COM (zgodne z AOT). |
| Obraz | NV12 → RGB, skalowanie, kwantyzacja median-cut, enkoder Sixel (równoległy, paskami) lub półbloki z kolorem 24-bit. |
| Dźwięk | WinMM `waveOut`. Pozycja odtwarzania audio jest zegarem synchronizacji A/V. |
| TUI | własny renderer różnicowy z obsługą obrazów: komórki pod Sixelem nie są nadpisywane tekstem, a klatki wideo zapisuje osobny wątek. |

## Pliki i deinstalacja

Wszystko, co program tworzy, trafia do `%LOCALAPPDATA%\YoutubeTerminal`:

- `bin\yt-dlp.exe`;
- `cache\yt-dlp`;
- `tmp` (pliki tymczasowe yt-dlp, kierowane tu zamiast do `%TEMP%`);
- `settings.json`, `library.json`, `log.txt`.

Deinstalator `odinstaluj.cmd` (obok `.exe`) usuwa ten folder, program i samego siebie. Opcjonalnie
zachowuje subskrypcje, historię i ustawienia.

```bash
odinstaluj.cmd
```

Bez pytań, z usunięciem wszystkiego:

```bash
odinstaluj.cmd /q
```

Bez pytań, z zachowaniem biblioteki i ustawień:

```bash
odinstaluj.cmd /keep
```

## Budowanie

Wymagane są .NET SDK 10 oraz Visual Studio Build Tools z obciążeniem C++ (linker MSVC dla Native AOT).
Plik `tools\yt-dlp.exe` jest wbudowywany w program. Nie ma go w repozytorium (`.gitignore`), więc przed
budowaniem pobierz go z [wydań yt-dlp](https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe)
do folderu `tools`.

```bash
build.cmd
```

Wynik: `dist\YoutubeTerminal.exe`.

Testy jednostkowe (xUnit, bez dostępu do internetu: strumienie MP4/HLS serwuje lokalny serwer testowy):

```bash
dotnet test tests\YoutubeTerminal.Tests
```

## Wersje i wydania

Numer wersji (`YoutubeTerminal.exe --version`) i wydania na GitHubie powstają automatycznie, na
podstawie opisów commitów w formacie [Conventional Commits](https://www.conventionalcommits.org/pl/v1.0.0/):

| Początek opisu commita | Skutek |
| --- | --- |
| `fix: …` | wersja poprawkowa, np. 1.0.0 → 1.0.1 |
| `feat: …` | nowa funkcja, np. 1.0.1 → 1.1.0 |
| `feat!: …` albo `BREAKING CHANGE:` w treści | wersja główna, np. 1.1.0 → 2.0.0 |
| `perf:`, `docs:`, `test:`, `refactor:`, `ci:`, `chore:` | bez nowej wersji (`perf:` trafia do listy zmian) |

Przebieg:

1. Push na `main` uruchamia testy (workflow **CI**) oraz release-please (workflow **Release**).
2. release-please otwiera lub aktualizuje pull request „chore(main): release X.Y.Z” z nową wersją
   w `YoutubeTerminal.csproj` i wpisami w `CHANGELOG.md`.
3. Scalenie tego PR tworzy tag `vX.Y.Z` i wydanie na GitHubie. Workflow pobiera wtedy oficjalny
   yt-dlp (ze sprawdzeniem sumy SHA-256), uruchamia testy i buduje `YoutubeTerminal.exe` (Native AOT),
   a potem dołącza go do wydania razem z plikiem `.sha256`.

Pliki istniejącego wydania można przebudować ręcznie: **Actions → Release → Run workflow** z podanym tagiem.

Jednorazowo w ustawieniach repozytorium na GitHubie trzeba zaznaczyć:
**Settings → Actions → General → Workflow permissions → „Allow GitHub Actions to create and approve pull requests”**
(inaczej release-please nie otworzy PR wydania).

Polecenia diagnostyczne (bez TUI):

- `--diag-yt "fraza"`: test API;
- `--diag-decode <id>`: test dekodowania;
- `--diag-sixel <id> <folder>`: test enkodera Sixel z zapisem PNG.
