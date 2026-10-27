# Glint

Маленькая «пилюля» вверху по центру экрана для Windows 10/11. Показывает время и то, что сейчас играет, а при наведении раскрывается в панель с быстрыми инструментами.

## Возможности

- **Пилюля** с часами, обложкой трека и индикатором воспроизведения; короткие всплывающие сообщения («Скопировано», «Таймер готов»).
- **Панель при наведении** с вкладками: Главная, Буфер, Полка, Текст, Система, Настройки. Можно закрепить, чтобы не сворачивалась.
- **Медиа и громкость**: управление любым плеером, который поддерживает системные медиаэлементы Windows; ползунок громкости и отключение звука.
- **Быстрые действия**: скриншот всего экрана (PNG в «Изображения\Glint» и в буфер), скриншот области через системные «Ножницы», пипетка (цвет под курсором), блокировка, папка загрузок, запуск закреплённых приложений.
- **История буфера обмена**: текст и картинки, поиск, закрепление, вставка в предыдущее окно по клику.
- **Полка файлов**: перетащите файлы на пилюлю, чтобы отложить их, и вытащите обратно в любое приложение.
- **Командная строка** (Alt+Space): поиск приложений и файлов, калькулятор, таймеры, поиск в интернете.
- **Инструменты для текста**: исправление раскладки (ghbdtn → привет), транслитерация, смена регистра, форматирование JSON, Base64.
- **Нагрузка на систему** (вкладка «Система»): процессор (общая загрузка, по потокам, частота), видеокарта, память, диски, сеть и самые активные процессы, графики за последнюю минуту. На видеокартах NVIDIA ещё температура, видеопамять, питание и вентилятор. Температуры процессора нет: Windows отдаёт её только драйверам ядра, а Glint их не ставит. Замеры идут только пока вкладка открыта.
- **Цветовые темы** (Настройки → Оформление): семь готовых пресетов (Аврора, Закат, Океан, Лайм, Роза, Уголь, Моно), свои цвета акцентов в HEX, оттенок и прозрачность фона. Всё меняется сразу, без перезапуска, включая значок в трее.
- **Скрытие в полноэкранных приложениях** и в выбранных программах.
- **Значок в трее** с меню и автозапуском.

## Скачать

[**Glint.exe**](https://github.com/sempik/glint/releases/latest/download/Glint.exe) — один файл, установка не нужна, .NET ставить не надо. Просто запустите. Есть и [zip-архив](https://github.com/sempik/glint/releases/latest/download/Glint-win-x64.zip); контрольные суммы в `SHA256SUMS.txt` на странице [релиза](https://github.com/sempik/glint/releases/latest).

Файл не подписан платным сертификатом, поэтому Windows SmartScreen может предупредить при первом запуске: «Подробнее» → «Выполнить в любом случае». Сборки делает GitHub Actions прямо из этого репозитория.

## Требования

- Windows 10 версии 2004 (сборка 19041) или новее, либо Windows 11, x64

## Сборка

Нужен [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/sempik/glint.git
cd glint
dotnet build
```

Один exe-файл для распространения:

```powershell
dotnet publish src/Glint/Glint.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Готовые сборки появляются в [Releases](https://github.com/sempik/glint/releases) автоматически при пуше тега `v*`.

## Данные и приватность

Все настройки, история буфера и полка хранятся локально в `%AppData%\Glint`. Никакой телеметрии, аналитики и сетевых запросов, кроме тех, что вы запускаете сами (например, поиск в интернете из командной строки).

История буфера обмена (текст и картинки) хранится на диске **без шифрования** в `%AppData%\Glint` (`clipboard.json` и папка `clips\`) и сохраняется между перезапусками. Содержимое, которое менеджеры паролей помечают как приватное, не сохраняется, но пароль, скопированный из обычного поля, попадёт в историю. Удалить её можно кнопкой «Очистить» во вкладке «Буфер» или удалив эту папку; отключить историю — выставив `ClipboardHistoryLimit` в `0` в `settings.json`.

## Правовая информация

Glint — независимый проект с открытым исходным кодом. Он не связан с Apple или любым другим продуктом и не одобрен ими. Windows является товарным знаком Microsoft Corporation.

## Лицензия

[MIT](LICENSE) © 2026 sempik

---

## English

**Glint** is a small floating pill at the top center of the screen for Windows 10/11. It shows the clock and what's playing, and expands on hover into a panel with quick tools.

**Features:** clock & media pill with flash messages; hover panel with tabs; media controls and volume; quick actions (full-screen screenshot saved to Pictures\Glint, region screenshot via the Windows Snipping Tool, eyedropper, lock, downloads); clipboard history with search, pinning and click-to-paste; file shelf with drag in/out; Alt+Space command bar (apps, files, calculator, timers, web search); text tools (keyboard layout fix, transliteration, case, JSON, Base64); system load tab (CPU total/per-thread/clock, GPU, RAM, disks, network, top processes, one-minute graphs; GPU temperature, VRAM, power and fan on NVIDIA; no CPU temperature, since Windows only exposes it to kernel drivers; sampling runs only while the tab is open); color themes (seven presets, custom HEX accents, background tint and opacity, applied live without a restart, tray icon included); auto-hide in fullscreen and selected apps; tray icon with autostart.

**Download:** [Glint.exe](https://github.com/sempik/glint/releases/latest/download/Glint.exe) — a single self-contained file, no installer and no .NET runtime needed. It isn't code-signed, so SmartScreen may warn on first run ("More info" → "Run anyway"). Release builds are produced by GitHub Actions from this repository.

**Requirements:** Windows 10 19041+ or Windows 11, x64.

**Build:** install the .NET 8 SDK, then run `dotnet build`, or
`dotnet publish src/Glint/Glint.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish`.

**Privacy:** all data stays on your machine in `%AppData%\Glint`. No telemetry. Clipboard history (text and images) is stored **unencrypted** there (`clipboard.json` and `clips\`) and survives restarts. Content that password managers mark as private is not saved. Use "Очистить" in the clipboard tab or delete the folder to remove it.

**Not affiliated:** Glint is an independent project and is not affiliated with or endorsed by Apple or any other product. Windows is a trademark of Microsoft Corporation.

**License:** MIT.
