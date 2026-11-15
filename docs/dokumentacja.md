# YouTube Terminal — dokumentacja

Przeglądarka YouTube w terminalu (TUI) z odtwarzaniem wideo przez **Sixel**, **oktanty** lub **półbloki**.
To jeden plik wykonywalny skompilowany przez **.NET Native AOT**: nie wymaga zainstalowanego .NET ani
niczego innego. Działa w Windows, Linuksie i macOS. Interfejs jest po polsku i po angielsku.

Krótkie omówienie po angielsku jest w [README](../README.md).

## Funkcje

- **Wyszukiwarka** z podpowiedziami, filtrem (wszystko / filmy / kanały / playlisty), sortowaniem
  (trafność, data, wyświetlenia, ocena), nieskończonym przewijaniem i historią wyszukiwań.
  Można też wkleić link do filmu, Shorts, kanału, `@uchwytu` lub playlisty.
- **Kanały**: nagłówek z awatarem, zakładki (Filmy, Shorts, Na żywo, Playlisty…), sortowanie
  (najnowsze / popularne / najstarsze), subskrypcje.
- **Playlisty i składanki**: odtwarzanie całości, od wybranego filmu lub losowo.
- **Szczegóły filmu**: miniatura, statystyki, opis, rozdziały, komentarze (z odpowiedziami
  i sortowaniem) oraz podobne filmy.
- **Odtwarzacz** zsynchronizowany z dźwiękiem, w jednym z trzech trybów obrazu (klawisz `v`):
  - **Sixel** — pełna grafika pikselowa;
  - **oktanty** (Unicode 16) — 2×4 „piksele” na komórkę, 4× więcej szczegółów niż półbloki;
  - **półbloki** `▀` — działają w każdym terminalu z kolorem 24-bit.

  Do tego: pauza, przewijanie, skoki procentowe, rozdziały, głośność i wyciszanie, tryb pełnoekranowy,
  tryb „tylko dźwięk”, kolejka i autoodtwarzanie, wznawianie od miejsca przerwania, **transmisje na żywo**.
- **Biblioteka lokalna**: subskrypcje (z kanałem „najnowsze filmy” zbieranym z RSS), historia
  z postępem oglądania oraz lista „Do obejrzenia”.
- Miniatury na listach i w panelu podglądu, obsługa myszy (kółko, kliknięcia, pasek postępu).
- **Język interfejsu**: polski lub angielski (Ustawienia → Język interfejsu; domyślnie według systemu).

## Wymagania

| System | Wymagania |
| --- | --- |
| Windows 10/11 x64 | Do Sixel: **Windows Terminal 1.22+**; do oktantów: **Windows Terminal 1.25+** |
| Linux x64 / arm64 | glibc 2.35+ (np. Ubuntu 22.04, Debian 12, Fedora 36), dźwięk przez PulseAudio lub PipeWire |
| macOS (Apple Silicon) | macOS 12+ |

Sixel obsługują m.in. Windows Terminal, WezTerm, foot, Konsole, xterm (`-ti vt340`), mlterm, iTerm2.
W terminalach bez Sixel program przełącza się na półbloki. Oktanty rysuje sam terminal (np. Windows
Terminal 1.25+, kitty, WezTerm, foot) albo czcionka ze znakami Unicode 16.

## Uruchomienie

```bash
YoutubeTerminal.exe
```

W Linuksie i macOS:

```bash
./YoutubeTerminal
```

Program przyjmuje też frazę albo link w linii poleceń:

```bash
YoutubeTerminal.exe "lofi hip hop"
```

```bash
YoutubeTerminal.exe https://youtu.be/aqz-KE-bpKQ
```

Przy pierwszym uruchomieniu program:

- wypakowuje wbudowany yt-dlp do folderu danych aplikacji (`bin`);
- tworzy obok siebie deinstalator: `uninstall.cmd` (Windows) albo `uninstall.sh` (Linux, macOS).

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
| `o` | przeglądarka (w komentarzach: zmiana sortowania) |
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
| `v` | tryb obrazu: Sixel → oktanty → półbloki |
| `d` | paleta adaptacyjna ↔ dithering |
| `[ ]` | rozmiar obrazu |
| `a` | tylko dźwięk |
| `i` | statystyki |
| `End` | na żywo: powrót do bieżącego momentu |

## Ustawienia i wydajność

Ustawienia (klawisz `5`) zapisują się w `settings.json` w folderze danych aplikacji. Jeśli obraz się
zacina, zmniejsz kolejno:

- **maks. szerokość obrazu wideo** (domyślnie 960 px);
- **kolory w klatce wideo** (domyślnie 128);
- **limit FPS**.

Albo przełącz odtwarzacz na **oktanty** (`v`): przy podobnej ilości danych co półbloki dają czterokrotnie
więcej szczegółów, a w dużych oknach bywają płynniejsze od Sixel.

Tylko zmienione fragmenty klatki są wysyłane do terminala, więc statyczne sceny (podcasty, streamy
lofi) zużywają niewiele przepustowości.

Gdy obrazy mają złe proporcje albo nachodzą na tekst, ustaw ręcznie **rozmiar komórki**. Windows Terminal
używa dla Sixel wirtualnej komórki 10×20 px.

**Język i region wyników** domyślnie podążają za językiem interfejsu i regionem systemu; można je ustawić
osobno (np. interfejs po polsku, wyniki z regionu US).

Gdy YouTube coś zmieni i odtwarzanie przestanie działać, zaktualizuj yt-dlp z poziomu Ustawień
(pozycja „yt-dlp”, Enter).

## Jak to działa

| Warstwa | Implementacja |
|---|---|
| Dane YouTube | publiczne API strony (InnerTube, klient WEB) i kanały RSS. Parser przechodzi drzewo JSON i rozpoznaje znane renderery, więc drobne zmiany układu nie psują aplikacji. |
| Adresy strumieni | wbudowany yt-dlp, wybierający H.264 + AAC. |
| Pobieranie | własny klient HTTP z żądaniami `Range` (pobieranie całego pliku jednym żądaniem YouTube mocno dławi). |
| Demultipleksacja | własny demuxer MP4 (DASH: `sidx`/`moof`/`trun` oraz klasyczne tablice próbek) i HLS (MPEG-TS + packed AAC) dla transmisji na żywo. |
| Dekodowanie | Windows: systemowe dekodery Media Foundation (H.264 MFT, AAC MFT) wywoływane przez tablice wirtualne COM (zgodne z AOT). Linux i macOS: ffmpeg jako proces (H.264 w Annex B i AAC w ADTS na wejściu, gotowe klatki i PCM na wyjściu) — najpierw systemowy, a gdy go brak, wbudowana minimalna kopia. |
| Obraz | NV12 → RGB, skalowanie, kwantyzacja median-cut, enkoder Sixel (równoległy, paskami), oktanty (dla każdej komórki dwa kolory i wzór 2×4 dobrany metodą 2-średnich) lub półbloki z kolorem 24-bit. |
| Dźwięk | Windows: WinMM `waveOut`; Linux: PulseAudio (także PipeWire); macOS: AudioQueue. Pozycja odtwarzania audio jest zegarem synchronizacji A/V. |
| Terminal | Windows: konsola w trybie VT; Linux/macOS: tryb surowy termios (`cfmakeraw`), `poll`/`read`/`write`. Reszta (parser klawiszy, renderer) jest wspólna. |
| TUI | własny renderer różnicowy z obsługą obrazów: komórki pod obrazem nie są nadpisywane tekstem, a klatki wideo zapisuje osobny wątek. |
| Języki | teksty parami przy miejscu użycia (`L.T("polski", "english")`), bez plików zasobów — zgodne z AOT. |

## Pliki i deinstalacja

Wszystko, co program tworzy, trafia do jednego folderu danych:

| System | Folder |
| --- | --- |
| Windows | `%LOCALAPPDATA%\YoutubeTerminal` |
| Linux | `~/.local/share/YoutubeTerminal` (lub `$XDG_DATA_HOME/YoutubeTerminal`) |
| macOS | `~/Library/Application Support/YoutubeTerminal` |

Zmienna `YOUTUBETERMINAL_HOME` wskazuje inny folder (przydatne do testów). W folderze są:

- `bin` — yt-dlp oraz (Linux/macOS, tylko gdy w systemie nie ma ffmpeg) wbudowany ffmpeg;
- `cache/yt-dlp`;
- `tmp` — pliki tymczasowe yt-dlp, kierowane tu zamiast do systemowego folderu tymczasowego;
- `settings.json`, `library.json`, `log.txt`.

Deinstalator (obok programu) usuwa ten folder, program i samego siebie. Opcjonalnie zachowuje
subskrypcje, historię i ustawienia.

```bash
uninstall.cmd
```

Bez pytań, z usunięciem wszystkiego:

```bash
uninstall.cmd /q
```

Bez pytań, z zachowaniem biblioteki i ustawień:

```bash
uninstall.cmd /keep
```

W Linuksie i macOS odpowiednio `./uninstall.sh`, `./uninstall.sh -q` i `./uninstall.sh --keep`.

## Budowanie

Wymagane jest .NET SDK 10, a do Native AOT:

- Windows: Visual Studio Build Tools z obciążeniem C++ (linker MSVC);
- Linux: `clang` i `zlib1g-dev`;
- macOS: Xcode Command Line Tools.

Narzędzia wbudowywane w program trzyma się w folderze `tools` (nie ma ich w repozytorium):

| Platforma | Pliki |
| --- | --- |
| Windows | `tools/yt-dlp.exe` lub `tools/win-x64/yt-dlp.exe` |
| Linux, macOS | `tools/<rid>/yt-dlp` i (opcjonalnie) `tools/<rid>/ffmpeg` |

`<rid>` to np. `linux-x64`, `linux-arm64`, `osx-arm64`. yt-dlp pobiera skrypt
`.github/scripts/fetch-yt-dlp.sh` (ze sprawdzeniem sumy SHA-256), a minimalny ffmpeg buduje
`.github/scripts/build-ffmpeg.sh`.

Windows:

```bash
build.cmd
```

Linux, macOS (wynik w `dist/`):

```bash
dotnet publish src/YoutubeTerminal -c Release -o dist
```

Testy jednostkowe (xUnit, bez dostępu do internetu: strumienie MP4/HLS serwuje lokalny serwer testowy;
test dekodowania przez ffmpeg uruchamia się tylko tam, gdzie ffmpeg jest zainstalowany):

```bash
dotnet test tests/YoutubeTerminal.Tests
```

## Wersje i wydania

Workflow **Build** (`.github/workflows/build.yml`) przy każdym pushu i pull requeście uruchamia testy
i buduje pliki dla Windows (x64), Linuksa (x64, arm64) i macOS (Apple Silicon).

Na gałęzi `main` wydanie powstaje automatycznie, na podstawie opisów commitów w formacie
[Conventional Commits](https://www.conventionalcommits.org/pl/v1.0.0/) od ostatniego tagu:

| Początek opisu commita | Skutek |
| --- | --- |
| `fix: …`, `perf: …` | wersja poprawkowa, np. 1.1.0 → 1.1.1 |
| `feat: …` | nowa funkcja, np. 1.1.1 → 1.2.0 |
| `feat!: …` albo `BREAKING CHANGE:` w treści | wersja główna, np. 1.2.0 → 2.0.0 |
| `docs:`, `test:`, `refactor:`, `ci:`, `chore:` i inne | bez nowego wydania |

Wtedy workflow tworzy tag `vX.Y.Z` i wydanie na GitHubie z listą zmian, plikami do pobrania
i `SHA256SUMS.txt`. Pierwsze wydanie (gdy nie ma jeszcze żadnego tagu) dostaje wersję z `<Version>`
w `YoutubeTerminal.csproj`. Wydanie można też wymusić ręcznie: **Actions → Build → Run workflow**
i wybór `patch` / `minor` / `major`.

Numer wersji pokazuje `YoutubeTerminal.exe --version`.

## Polecenia diagnostyczne (bez TUI)

- `--diag-yt "fraza"`: test API;
- `--diag-decode <id>`: test dekodowania (także przez ffmpeg w Linuksie i macOS);
- `--diag-sixel <id> <folder>`: test enkodera Sixel z zapisem PNG (Windows).

Do zrzutów ekranu w dokumentacji służy skrypt klawiszy, np.
`--keys "wait:8000 svg:zrzut.svg quit"`: polecenie `svg:` zapisuje ekran jako SVG z miniaturami
i bieżącą klatką wideo.
